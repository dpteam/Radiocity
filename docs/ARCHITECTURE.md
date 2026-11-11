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

One receiver, many listeners. The DSP runs whether anyone is connected or not;
audio clients attach and detach, and the waterfall keeps rolling.

```
ISampleSource ──► WebRadio (DSP thread) ──┬──► per-client RingBuffer ──► /audio
   waveIn or synthetic                    └──► spectrum columns ──────► /waterfall
```

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

**Blocking writes are the clock.** The DSP thread calls `Broadcast`, which never
blocks; a client that falls behind has its oldest audio dropped so latency stays
bounded. The HTTP thread blocks in `Drain` waiting for samples, which paces it to
real time without a timer.

**FFT length is chosen from the sample rate** (`PickFftSize`) so the waterfall
runs at about 25 columns per second at 48 kHz and at 192 kHz alike. A fixed
2048-point FFT would scroll four times too fast at 192 kHz.

## Performance notes

Everything in `Process()` is preallocated. The whole AM chain at 48 kHz runs in
a few percent of one core on a 2005-era CPU; at 192 kHz the FIR dominates and
the tap count is the lever.

`selftest` runs in about 3 seconds including a 65-second synthetic DCF77 minute,
because the DCF77 test processes pre-generated samples rather than waiting.
