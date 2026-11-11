# Architecture

How a sound card becomes a receiver, stage by stage, and why each stage looks
the way it does. Read this before touching the DSP.

## The signal path

```
                    ┌──────────── sound card (48 or 192 kHz, 16/24 bit) ────────────┐
                    │                                                              │
   antenna ─────────┤ ADC ──► waveIn ──► RingBuffer ──► NCO ──► FIR ──► ÷D ──► demod │
   (a wire)         │                                  mix     channel   decimate    │
                    └──────────────────────────────────────────────────────────────┘
                                                                      │
                                    ┌─────────────────────────────────┴──────┐
                                    ▼                                        ▼
                              AM envelope                            FM quadrature
                              SSB phasing                           CW via BFO
                                    └────────────────┬───────────────────────┘
                                                     ▼
                                              AGC ──► resampler ──► waveOut
```

## Stage by stage

### 1. Capture — `src/Audio/WinMM.cs`

`waveIn*` from `winmm.dll`. This is the only audio API present on every Windows
from 98 to 11 with no third-party driver, and it is the reason the project can
target XP at all: WASAPI arrived with Vista, and ASIO is never in the box.

Cost: 20–60 ms of latency with small buffers. Irrelevant for receive.

The driver calls back on its own thread with a filled buffer. That buffer is
reused, so it is copied into a lock-free `RingBuffer` and the DSP runs on the
consumer side. Never do work in the callback.

### 2. Mixing — `src/Dsp/Nco.cs`

Multiply the real input by `exp(-j·2π·f·t)` to shift the tuned carrier down to
0 Hz. Output is complex: I and Q.

Two details that matter:

- **Table lookup with interpolation**, not `Math.Sin`. On an old CPU `Math.Sin`
  is ~20 ns; at 192 kHz × 2 channels that is 8% of a core just for the
  oscillator.
- **Phase as a fraction of a turn**, kept in `[0,1)`. Retuning changes the
  increment, not the phase, so a frequency change never produces a click.

### 3. Channel filtering and decimation — `src/Dsp/Fir.cs`

A complex FIR followed by an integer decimation. This is what sets the channel
width and what keeps the ADC's noise from aliasing into it.

Tap count is **computed from the requirement**, not guessed:

```
taps = ceil(62 / (2.285 · 2π · (f_stop - f_pass)))
```

That is the Kaiser estimate for roughly 70 dB of rejection (β = 8.6). A 9 kHz AM
channel at 48 kHz needs 139 taps; a 12 kHz FM channel at 48 kHz needs 347.

### 4. Demodulation

| Mode | File | Method |
|---|---|---|
| AM | `Demod.cs` | Envelope `√(I²+Q²)`, then a one-pole DC blocker at 20 Hz |
| FM | `FmDemod.cs` | `arg(z[n] · conj(z[n-1]))` via `atan2`, then de-emphasis |
| SSB | `SsbDemod.cs` | Phasing: Hilbert 90° on Q, then `I ∓ H(Q)` |
| CW | `SsbDemod.cs` | Same, with a BFO shift so the carrier becomes a tone |

**Why `atan2` for FM and not the usual `I·dQ − Q·dI`:** the approximation needs
division by the signal power to be amplitude-independent. Dividing by a fading
signal is how a discriminator turns into a noise generator. `atan2` of the
product is exactly the phase difference, so amplitude does not enter at all —
no limiter stage needed.

**Why phasing and not Weaver for SSB:** one FIR and two adds per sample. The
rejection is set purely by tap count (127 taps → −107 dB), and there is no
branching on mode inside the loop.

### 5. AGC — `Demod.cs`

An RMS detector with a 30 ms window, then an asymmetric gain slew (fast attack,
slow release).

Two mistakes are easy here and both were made once during development:

1. **A peak detector with fast attack is not an envelope detector.** It chases
   the crest of the waveform, reads ~3 dB high on a sine, and drags the whole
   gain curve with it.
2. **A detector window shorter than a few audio periods** modulates the gain at
   twice the tone frequency. That is distortion, not AGC.

Asymmetry belongs on the *gain slew*, never on the *detector*.

### 6. Resampling — `Demod.cs`

The channel rate is `captureRate / decimation`, which is almost never a rate a
sound card accepts (12 kHz, 7.5 kHz, ...). Catmull-Rom cubic interpolation
bridges the gap.

Linear interpolation would be simpler and audibly wrong: it is a `sinc²` filter,
so −2.3% at 1 kHz and −3.3 dB at 5 kHz going 12 → 48 kHz. That is the range
where speech intelligibility lives.

The block boundary carries two samples of history, otherwise every block edge
clicks.

## Timing and threading

```
driver thread ──► RingBuffer ──► DSP thread ──► WaveOut.Write (blocks on buffer free)
```

`WaveOut.Write` blocking on a free driver buffer is deliberate: it paces the DSP
chain to real time without a clock. If the DSP cannot keep up, the ring buffer
overflows and audio stutters — which is the honest failure mode.

`RingBuffer` is single-producer / single-consumer and lock-free. For multiple
concurrent listeners (the WebSDR case) each needs its own ring.

## Clock accuracy — `src/Dsp/ClockCalib.cs`

A "192000 Hz" codec is typically off by 20–50 ppm, which is 4–10 Hz at 192 kHz.
That is enough to make a waterfall drift visibly.

Measured against a carrier of known frequency: coarse FFT peak, parabolic
interpolation on the log magnitudes, then a fine Goertzel sweep. FFT alone gives
±4 ppm; the refinement gets under 1 ppm.

## DCF77 — `src/Dsp/Dcf77.cs`
An entirely different kind of receiver, which is why it lives on its own: one
bit per second, so the "demodulator" is an envelope detector and a threshold.

```
77.5 kHz ──► mix to DC ──► ÷8 ──► ÷4 ──► |·| ──► peak-follower ──► dip detector
                                                                        │
                                              100 ms = 0, 200 ms = 1 ───┘
                                                        │
                                             59-bit telegram ──► parity ──► clock
```

Three things that break naive decoders:

1. **The telegram describes the *next* minute.** A frame running through minute
   M carries M+1.
2. **Fields are BCD, not binary.** `(hour / 2) % 2` instead of `(hour % 10) >> 1`
   works for 08:00 and lies for 14:00.
3. **Day of week is 1 = Monday**, while .NET has 0 = Sunday.

Frame sync comes from the missing pulse at second 59: the gap across a minute
boundary is ~2 s instead of ~1 s.

## Impulse blanking — `src/Dsp/NoiseBlanker.cs`

A squelch answers "is there a signal". A blanker answers "is this sample a
click", and removes the click while keeping the signal under it. Ignition
noise, switch-mode hash and lightning all arrive as very short, very loud
spikes — a handful of samples at any baseband rate — so blanking them is nearly
free, while filtering them out is not: a filter that removes a one-sample spike
also removes everything else at that bandwidth.

It runs **before** the channel filter, on the complex baseband. After the filter
the impulse has already been convolved with the whole impulse response, and by
then removing it means removing the signal too.

Two things make the difference between working and deaf:

1. **Warm-up.** The reference level starts at zero, so the threshold starts
   below the signal itself. The detector then blanks the signal, blanked samples
   never update the reference, and the blanker locks itself out permanently.
   It measures for 50 ms first, with no blanking.
2. **Clamped reference.** While blanking, the reference is fed
   `threshold × average` rather than the actual (huge) value, so a burst of
   clicks cannot raise the threshold above its own amplitude and blind the
   detector.

The test measures error against a *known* clean signal, which is the only way
to answer "did blanking help" rather than "did blanking happen".

## Recording I/Q — `src/Dsp/IqFile.cs`

A recording is the output of the mixer, so it can be replayed through any
demodulator without going back to the air. That is the difference between
"I heard something" and "I have the evidence".

Format: stereo WAV, I in the left channel, Q in the right, 32-bit float by
default (a 24-bit card's baseband has more range than int16 can carry, and
clipping a recording cannot be undone). The centre frequency goes into a
`LIST/INFO` comment as `center=77500`.

Why WAV and not a raw dump with a sidecar README: the WAV opens in Audacity, SoX
and Python, and the header carries the sample rate where it belongs. A raw file
plus a text file is a format that only works if the text file survives.

`Receiver.ProcessBaseband` is the piece that makes replay possible: the
demodulation half of the chain, callable on already-mixed I/Q.

## The web layer — `src/Web/`

One capture, many receivers. The card can only be opened once, so exactly one
thread reads it and fans each raw block out to every session.

```
ISampleSource ──► CaptureHub ──┬──► RadioSession ──► per-client ring ──► /audio
   waveIn or synthetic          ├──► RadioSession ──► ...
   one thread                   └──► spectrum columns ─────────────────► /waterfall
```

**`CaptureHub`** owns the source and the waterfall. The spectrum of the band is
the same for every listener, so it is computed once, on the capture thread.

**`RadioSession`** owns everything downstream of the mixer: NCO, decimation
chain, demodulator, AGC, squelch, blanker. Its own thread, so a session that
asked for a narrow filter does more work than one listening wide and they
degrade independently instead of the slowest setting the latency for everybody.

**Why not `HttpListener`:** it is a wrapper over `http.sys`, and registering a
URL prefix requires administrator rights on Windows XP. Since XP is an explicit
target, the server is a plain `TcpListener` with hand-written HTTP/1.1. It
implements keep-alive, query strings, form bodies, `Content-Length` responses
and chunked streaming — the minimum a WebSDR needs.

**Why polling and not WebSockets:** .NET 4.0 has no `System.Net.WebSockets`
(that arrived in 4.5). Audio is a chunked HTTP response consumed with
`fetch` + `ReadableStream`; the waterfall is a poll for the columns produced
since a watermark. A slow client gets the columns it missed in one response
instead of silently dropping them.

**Sessions.** An opaque id, kept in a page variable and appended to every
request. No cookies: they need parsing, expiry and a conversation about consent,
and a query parameter does the same job. No id means the `default` session, so
`curl http://host/state` works. Idle sessions are reaped after five minutes.

**The ceiling is real.** N listeners means N FIR chains — 23 M multiply-adds/s
each at 192 kHz. The server caps sessions at 8 and returns `503` past that
rather than quietly degrading everyone, because "it works" and "it works for
eight people at once" are different claims.

**Blocking writes are the clock.** A session's DSP thread never blocks on a
client: a listener that falls behind has its oldest audio dropped so latency
stays bounded. The HTTP thread blocks in `Drain` waiting for samples, which
paces it to real time without a timer. An abandoned connection is detected by
ten seconds of silence, so a closed tab cannot hold a pool thread forever.

**FFT length is chosen from the sample rate** (`CaptureHub.PickFftSize`) so the
waterfall runs at about 25 columns per second at 48 kHz and at 192 kHz alike. A
fixed 2048-point FFT would scroll four times too fast at 192 kHz.

## Quadrature front end — `src/Web/QuadratureSource.cs`, `src/Dsp/IqImbalance.cs`
A SoftRock-style downconverter mixes a band down to audio and presents it as two
channels in quadrature. The card then becomes a 192 kHz-wide *complex*
digitiser, and the receiver sees −96 … +96 kHz around the local oscillator
instead of 0 … 96 kHz. That is the only configuration in which this software can
receive broadcast FM, because a station is 180–200 kHz wide and needs the full
complex band to fit.

Two things are easy to get wrong here, and both were:

**The NCO is still needed.** It is tempting to think a quadrature front end
removes the mixer. It does not. The hardware mixes the wanted *band* down to DC;
a station still sits somewhere inside that band, and selecting it is software's
job. Without an NCO in front of the demodulator every frequency sounds the same,
which is exactly what happened until `ProcessComplex` was added.

**The imbalance must be corrected before the mixer.** A real front end has gain
and phase mismatch between its two paths, which puts an image of every signal at
the mirror frequency. The correction is a real 2×2 matrix on (I, Q), and a 2×2
matrix does not commute with the complex rotation of the NCO — correcting after
mixing leaves a residual image.

The estimator is blind, because there is nothing to calibrate against:

```
rho = E[I*Q] / E[I^2]        correlation, caused by phase error
q   = E[Q^2] / E[I^2]        power ratio, caused by gain error
Q'  = (Q - rho*I) / sqrt(q - rho^2)
```

After that `E[I·Q'] = 0` and `E[Q'²] = E[I²]`, so the image is gone. On a
balanced signal `rho = 0` and `q = 1`, which makes the whole thing the identity
transform — the reason it can stay switched on for synthesised I/Q.

Guards that matter: no correction until 64 samples of statistics have
accumulated, and none when `E[I²]` or the determinant is near zero. Dividing by
a vanishing determinant is how a silent channel turns into infinite gain.

## FM stereo — `src/Dsp/FmStereo.cs`

A stereo broadcast sends three things at once:

```
MPX = M + S*cos(2*pi*38000*t) + P*cos(2*pi*19000*t)
M   = (L+R)/2      audio, 0-15 kHz
S   = (L-R)/2      on a suppressed 38 kHz subcarrier
P                  a 19 kHz pilot, whose only job is a phase reference
```

So decoding is: low-pass for M, synchronous detection at 38 kHz for S, then
`L = M+S` and `R = M-S`.

**Why the pilot exists.** Envelope-detecting the sideband gives `|S|` with an
unknown sign, and an unknown sign swaps L and R at random. The pilot tells the
receiver which half of the 38 kHz cycle is which.

**No PLL.** Mixing the composite down by the nominal 19 kHz and low-passing to
about 100 Hz leaves the pilot as a slowly rotating complex number `p`. Its angle
is the phase error, and the subcarrier needs *twice* that angle:

```
e^{j(theta + 2*psi)} = e^{j*theta} * (conj(p)/|p|)^2
```

Squaring is the part that is easy to get wrong. Using `conj(p)/|p|` without the
square gives `theta + psi` instead of `theta + 2*psi`, which decodes correctly
only when the pilot happens to start at phase zero and otherwise swaps the
channels with about 12 dB of leakage. That bug survived a first round of tests
because the test signal started at phase zero.

The other sign trap: with `p = pI + j*pQ`, `(conj(p))^2 = (pI^2 - pQ^2) -
j*2*pI*pQ`. A `+` there locks the decoder to `2*theta - 2*psi` and mirrors the
sideband.

**Baseband width.** The composite reaches 53 kHz, so the chain must deliver at
least ~106 kHz to the discriminator. This is also why broadcast FM needs the
quadrature path: with a mono input, mixing down by a low carrier puts the image
back inside the band and aliases.

**De-emphasis goes after the matrix**, because the transmitter pre-emphasised L
and R, not the composite. Applying it early would distort the sideband.

**AGC is linked** (`LinkedAgc`): one gain from the louder channel applied to both.
A mono AGC over interleaved samples would move the stereo image every time one
side got louder.

## Performance notes

Everything in `Process()` is preallocated. The whole AM chain at 48 kHz runs in
a few percent of one core on a 2005-era CPU; at 192 kHz the FIR dominates and
the tap count is the lever.

`selftest` runs in about 3 seconds including a 65-second synthetic DCF77 minute,
because the DCF77 test processes pre-generated samples rather than waiting.
