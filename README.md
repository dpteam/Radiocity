<div align="center">

<img src="assets/radiocity.png" width="128" alt="Radiocity">

# Radiocity

**A sound card used as an SDR front end.**

No drivers. No third-party libraries. No NuGet. Just maths, `DllImport`, and the
audio hardware that is already in the machine.

[![build](https://github.com/DartPower/Radiocity/actions/workflows/build.yml/badge.svg)](https://github.com/DartPower/Radiocity/actions/workflows/build.yml)
[![.NET Framework 4.0](https://img.shields.io/badge/.NET%20Framework-4.0-512BD4?logo=dotnet&logoColor=white)](https://learn.microsoft.com/dotnet/framework/)
[![C# 4.0](https://img.shields.io/badge/C%23-4.0-239120?logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)
[![Windows XP SP3+](https://img.shields.io/badge/Windows-XP%20SP3%2B-0078D6?logo=windows&logoColor=white)](docs/ARCHITECTURE.md)
[![license MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
[![dependencies none](https://img.shields.io/badge/dependencies-none-brightgreen.svg)](#the-rules)

```
AM      : distortion 109.5 dB below tone      (below float32 resolution)
FM      : residual    62.3 dB below tone
FM st.  : 48 dB channel separation (1 kHz left, 3 kHz right)
NFM     : 75 dB CTCSS rejection, tone detected to 0.1 Hz
DCS     : Golay (23,12) exact, wrong code silences the receiver
RDS     : 24 groups, 0 bad blocks, station name and text exact
SSB     : sideband rejection -111.1 dB
CW      : carrier -> 700 Hz beat note
clock   : 27.0 ppm injected, 26.92 ppm measured
decim   : 192k -> 12k in 2 stages, 4.6x cheaper than one filter
squelch : mutes on silence, opens on carrier
blanker : 8.2x less impulse error, tone kept within 1%
I/Q     : float32 bit-exact round trip, centre frequency preserved
image   : -31.2 dB before correction, -80.2 dB after (blind, 2% / 3 deg)
web     : independent tuning, two sessions verified at 3 kHz and 18 kHz
DCF77   : full telegram decoded at -40 and -20 dBFS
```

</div>

---

## What this is

An SDR receiver written entirely in C# 4.0 for .NET Framework 4.0, that turns
whatever audio input the machine already has into a software-defined radio. It
captures with `winmm.dll`, mixes with a numerically controlled oscillator,
filters and decimates with a Kaiser-windowed FIR, demodulates AM / FM / USB /
LSB / CW, applies AGC, and plays the result back through the same API.

Everything is reproducible from source with the C# compiler that ships inside
Windows. `build.cmd` and you are done.

## What this is not

**A bare sound card cannot receive broadcast FM at 87.5 MHz.** Not "with effort"
— not at all. A 192 kHz codec samples 0–96 kHz; 87.5 MHz is 900× above that. No
demodulator recovers information the samples never contained.

What it *does* receive on a bare wire is **VLF/LF**: time signals, navigation
beacons, sferics, whistlers. See [docs/THEORY.md](docs/THEORY.md) for the
numbers and [docs/HARDWARE.md](docs/HARDWARE.md) for what to add when you want
more than a wire.

**With a quadrature downconverter in front of it, FM becomes possible** — and
that path is implemented (`radiocity web --iq`). A SoftRock-style front end
presents I and Q on the two channels, so a 192 kHz card becomes a 192 kHz-wide
*complex* digitiser: ±96 kHz around the local oscillator instead of 0–96 kHz.
A broadcast FM station is about 180–200 kHz wide (Carson's rule for ±75 kHz
deviation), so it just fits. That is the whole reason the stereo capture path
and the I/Q imbalance correction exist.

## Quick start

```cmd
git clone https://github.com/DartPower/Radiocity.git
cd Radiocity
build.cmd
bin\Radiocity.exe selftest
```

No SDK, no Visual Studio, no NuGet restore. If `selftest` prints
`ALL TESTS PASSED`, the DSP is working on your machine.

Then, with a wire in the line input:

```cmd
bin\Radiocity.exe scan 5 192000        rem what is out there, 0..96 kHz
bin\Radiocity.exe dcf77 70 192000 -b24 rem decode the 77.5 kHz time signal
```

## Commands

| Command | What it does |
|---|---|
| `selftest` | Runs every DSP stage against synthetic signals with numeric pass criteria |
| `spec <in.wav>` | ASCII spectrum of a WAV file (8/16/24/32-bit, PCM or float) |
| `demod <in> <out> <tuneHz> [mode] [decim]` | Offline demodulation of a recording |
| `scan [sec] [rate]` | Capture and list the strongest carriers |
| `listen <tuneHz> [sec] [mode] [rate] [channelHz]` | Live receive: capture, demodulate, play |
| `calib <refHz> [sec] [rate]` | Measure the card's clock error in ppm |
| `dcf77 [sec] [rate]` | Decode the DCF77 time signal at 77.5 kHz |
| `web [--sim] [--port N]` | Serve the WebSDR UI (waterfall, audio, tuning) |
| `web --iq` / `web --sim-iq` | Same, fed from a quadrature front end (I/Q on two channels) |

Add `-stereo` for FM stereo, and `-nb` for the impulse noise blanker (`-nb20` for
a looser threshold). Both are off by default: on a clean signal a blanker can
only do harm, and stereo needs a 192 kHz capture.

For the radio mode, `-ctcss` opens the squelch for any CTCSS tone and
`-ctcss88.5` only for that one. `-dcs` does the same for any DCS code, and
`-dcs023` only for that one.
| `iq record <file> <freqHz> [sec]` | Record baseband I/Q for offline analysis |
| `iq demod <file> <out.wav> [mode]` | Demodulate a recording — try any mode on the same capture |
| `iq info <file>` | Show what a recording contains |
Add `-nb` to any command to switch on the impulse noise blanker (`-nb20` for a
looser threshold). It is off by default: on a clean signal a blanker can only
do harm.

Modes: `am`, `fm`, `nfm`, `usb`, `lsb`, `cw`. Add `-b8`, `-b16`, `-b24` or `-b32` to
any command to choose the capture depth; `-b24` is strongly recommended for VLF,
where the signal sits 40 dB under local noise.

```
> bin\Radiocity.exe help
Radiocity 0.4.0 - a sound card used as an SDR front end
(.NET 4.0, no drivers, no third-party libraries)

  radiocity selftest                      verify the whole DSP chain on synthetic signals
  radiocity spec <in.wav>                 ASCII spectrum of an audio file
  radiocity demod <in.wav> <out.wav> <tuneHz> [am|fm|usb|lsb|cw] [decim]
  radiocity scan [seconds] [rate]         capture and list the strongest carriers
  radiocity listen <tuneHz> [seconds] [am|fm|usb|lsb|cw] [rate] [channelHz]
  radiocity calib <refHz> [seconds] [rate]  measure the card's clock error in ppm
  radiocity dcf77 [seconds] [rate]        decode the 77.5 kHz time signal
  radiocity web [--sim] [--port N] ...    serve a WebSDR UI on http://localhost:8080
```

## The signal path

```
waveIn ──► NCO ──► FIR ──► FIR ──► demod ──► AGC ──► squelch ──► resample ──► waveOut
           mix     ÷8      ÷2      AM/FM/SSB/CW                      or /audio
```

Two decimation stages rather than one, because the FIR cost is `taps × rate` and
the tap count explodes as the transition band narrows: 192 kHz → 12 kHz in one
step needs 553 taps at 192 kHz (106 M multiply-adds/s), while 192→24→12 needs
23 M/s for the same rejection. That difference is what makes a narrow CW filter
affordable on a 2005 CPU.

| Stage | File | The interesting part |
|---|---|---|
| Capture | `src/Audio/WinMM.cs` | `waveIn*` P/Invoke; the only API alive on XP, Vista, 7, 8, 10 and 11 |
| Mixing | `src/Dsp/Nco.cs` | Table lookup with interpolation; phase as a fraction of a turn, so retuning never clicks |
| Filtering | `src/Dsp/Fir.cs` | Kaiser window; tap count computed from the requirement, not guessed |
| Decimation | `src/Dsp/DecimationChain.cs` | Multi-stage: 192k→12k in 2 steps costs 4.6× less than one filter for the same result |
| AM | `src/Dsp/Demod.cs` | Envelope + 20 Hz DC blocker |
| FM | `src/Dsp/FmDemod.cs` | `atan2` of `z[n]·conj(z[n-1])` — amplitude-independent, so no limiter |
| SSB/CW | `src/Dsp/SsbDemod.cs` | Phasing method, 127-tap Hilbert, −111 dB rejection |
| FM stereo | `src/Dsp/FmStereo.cs` | 19 kHz pilot, 38 kHz subcarrier recovered by squaring the pilot phasor, stereo matrix |
| RDS | `src/Dsp/Rds.cs` | 57 kHz subcarrier, biphase + differential decode, CRC with offset words, station name and text |
| NFM | `src/Dsp/Receiver.cs` | ±2.5 kHz deviation, 12.5 kHz channel, 300–3000 Hz speech band |
| CTCSS | `src/Dsp/Ctcss.cs` | 50 EIA tones, 1 s Goertzel window, tone squelch with hysteresis |
| DCS | `src/Dsp/Dcs.cs` | Golay (23,12), sync from the code itself, cyclic-alias comparison |
| AGC | `src/Dsp/Demod.cs` | RMS detector, asymmetric *gain slew* (not an asymmetric detector) |
| Squelch | `src/Dsp/Squelch.cs` | Hysteresis on channel power, measured before the AGC |
| Blanker | `src/Dsp/NoiseBlanker.cs` | Impulse detection on the baseband, before the channel filter smears the click |
| I/Q file | `src/Dsp/IqFile.cs` | Stereo WAV, float32, centre frequency in a RIFF comment |
| Quadrature | `src/Web/QuadratureSource.cs` | A SoftRock-style front end: L = I, R = Q, full complex band |
| Imbalance | `src/Dsp/IqImbalance.cs` | Blind Gram-Schmidt correction, no calibration signal needed || Resampling | `src/Dsp/Demod.cs` | Catmull-Rom; linear interpolation would droop 3.3 dB at 5 kHz |
| Clock | `src/Dsp/ClockCalib.cs` | FFT peak + parabolic refinement + Goertzel sweep, under 1 ppm |
| DCF77 | `src/Dsp/Dcf77.cs` | 100/200 ms dip widths, BCD telegram, three parity bits |
| Web | `src/Web/` | `TcpListener` + hand-rolled HTTP/1.1; one capture, one receiver per listener |

Full explanation in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Why the numbers are what they are

`selftest` does not check that code "runs". It checks that it is *correct*:

| Test | Criterion | Why that number |
|---|---|---|
| AM distortion | > 90 dB below tone | below float32 resolution — nothing left to measure |
| FM residual | > 50 dB below tone | discriminator noise floor |
| SSB rejection | < −50 dB | a phasing receiver is judged by this and nothing else |
| CW | 700 Hz beat note at target level | proves the BFO shift works |
| Clock | within 1.5 ppm | 1 ppm at 192 kHz is 0.19 Hz |
| Decimation | chain output matches, split multiplies back | proves the cheap path is the same path |
| Squelch | mutes at 0.6 s, opens at 1.6 s, mutes at 2.6 s | follows the signal, not the block |
| Blanker | 8× less error, tone within 1% | removes the clicks, not the signal |
| I/Q file | float32 bit-exact, int16 within half an LSB | metadata survives the round trip |
| Imbalance | image from −31 dB to −80 dB | blind correction must not need a calibration signal |
| FM stereo | > 30 dB channel separation | below that the stereo image collapses to mono |
| NFM | CTCSS tone within 0.5 Hz, > 20 dB below voice | the tone must be found and must not be heard |
| DCS | Golay round trip, 3-bit repair, wrong code silences | the code layer is exact; the radio path is not |
| RDS | exact station name and text | it is digital; "roughly right" means broken |
| Resampler | amplitude within 2%, > 30 dB | a bad interpolator is audible as dullness |
| DCF77 | full frame at −40 and −20 dBFS | the second one is a noisy city, not a lab |

The AM chain measures 109 dB of distortion rejection on a clean input, which
means the arithmetic is not the limit — the input signal is.

## DCF77: a real clock out of the air

The most satisfying thing here, because success is unambiguous: either you get
the date and time or you do not.

```cmd
bin\Radiocity.exe dcf77 70 192000 -b24
```

```
14:38:02  2026-11-11 14:39 CET (Wed)
14:39:02  2026-11-11 14:40 CET (Wed)
```

Three things that break naive decoders, all of them fixed here:

1. **The telegram describes the next minute.** A frame running through minute M
   carries M+1.
2. **Fields are BCD, not binary.** `(hour / 2) % 2` works for 08:00 and lies for
   14:00.
3. **Day of week is 1 = Monday**, while .NET has 0 = Sunday.

Frame sync comes from the missing pulse at second 59: the gap across a minute
boundary is ~2 s instead of ~1 s, which is a free marker.

## The rules

Non-negotiable, and the reason the project exists:

- **.NET Framework 4.0 / C# 4.0.** Not 4.5. No `async`/`await`, no `Task`,
  no `WebSockets`, no `Span<T>`, no NuGet. A Windows XP SP3 machine must be able
  to build this with nothing but the runtime.
- **Zero dependencies.** `winmm.dll` and `kernel32.dll` are the whole list.
- **No allocation in `Process()`.** These run on the audio callback thread; an
  allocation there is a GC pause and an audible click.

See [CONTRIBUTING.md](CONTRIBUTING.md) for the full table of what is off limits
and why.

## Repository layout

```
build.cmd                    one-command build with the in-box csc.exe
src/Audio/WinMM.cs           waveIn/waveOut P/Invoke
src/Dsp/                     the entire signal processing chain
src/Web/                     capture hub, per-listener sessions, HTTP server
web/index.html               the UI, embedded into the exe as a resource
src/App/                     CLI, WAV reader/writer, assembly info
tools/make-icon.ps1          generates assets/radiocity.ico (reproducible, not hand-drawn)
docs/ARCHITECTURE.md         how it works and why each stage is built that way
docs/THEORY.md               the radio theory, in numbers
docs/HARDWARE.md             antennas and downconverters, by budget
.github/workflows/build.yml  CI: build, self-test, validate icon, publish artefacts
```

## Status

Working: capture and playback, AM, FM, USB, LSB, CW, AGC, squelch, impulse
blanker, spectrum, offline demodulation, clock calibration, DCF77 decoding,
8/16/24/32-bit capture, multi-stage decimation, I/Q recording, a **WebSDR front
end**, **independent tuning for every listener**, a **quadrature front end path**
with blind I/Q imbalance correction, **FM stereo**, **RDS**, and a
**narrowband FM radio mode** with CTCSS and DCS tone squelch.

Not yet:

- [ ] RSDN-20 and SAQ decoders.
- [ ] RDS clock time (group 4A). The layout is documented but not verified
      against a real transmitter, so it is left out rather than shipped wrong.
- [ ] RDS timing recovery. The bit clock is free-running today; see
      [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for what was tried and why.
- [ ] Verifying the DCS octal-to-bits mapping against a physical radio.
- [ ] Password and rate limiting on the web server. It is a receiver for a
      trusted network, not a public service.

## FM stereo

```cmd
bin\Radiocity.exe web --iq --rate 192000 -stereo
```

A stereo broadcast does not send left and right. It sends `M = (L+R)/2`, plus
`S = (L-R)/2` on a suppressed 38 kHz subcarrier, plus a 19 kHz pilot whose only
job is to say where that subcarrier is. Recovering L and R is: strip the pilot,
synchronously detect the subcarrier to get S, then `L = M+S`, `R = M-S`.

**Why a quadrature front end is required.** Stereo puts information up to 53 kHz
into the composite, so the receiver needs at least 106 kHz of baseband. A mono
sound-card input at 192 kHz has only ±96 kHz of *complex* range after mixing, and
the mixer image lands back inside the band — wideband FM needs the I/Q path.
`--rate 192000` plus `--iq` (or `--sim-iq`) is the configuration that works.

**Why the pilot matters.** A plain envelope detector on the 38 kHz sideband gives
`|S|` with a random sign, which swaps L and R at random. The pilot is the phase
reference that makes the sign meaningful.

**No PLL.** Mixing the composite down by the nominal 19 kHz and low-passing to
~100 Hz leaves the pilot as a slowly rotating complex number. Its *square* is the
subcarrier: `e^{j(theta+2psi)} = e^{j theta} * (conj(p)/|p|)^2`. Getting that
square wrong is a subtle bug — the channels still decode, but they swap depending
on the sample offset, which is exactly how it behaved before it was fixed.

Measured on a synthetic broadcast with 1 kHz in L and 3 kHz in R:

```
L: wanted 0.245, leaked 0.001  separation 48.4 dB
R: wanted 0.244, leaked 0.001  separation 50.3 dB
```

AGC is linked across the two channels, because a mono AGC on interleaved L/R
would move the stereo image every time one side got louder.

## Quadrature front end

```cmd
bin\Radiocity.exe web --sim-iq          rem simulate a SoftRock, 2% / 3 deg
bin\Radiocity.exe web --iq --rate 192000 -b24
```

`--iq` opens the sound card as **stereo**, left channel I, right channel Q. The
receiver then treats the card as a 192 kHz-wide complex digitiser centred on the
local oscillator, so the tuning range is −96 … +96 kHz instead of 0 … 96 kHz.
The waterfall switches to a signed axis to match.

Two things this changes:

- **The NCO is still needed.** It is a common misconception that a quadrature
  front end removes it. The hardware mixes the wanted *band* down to DC; a
  station still sits somewhere inside that band, and selecting it is software's
  job. What the hardware removes is the *image*, not the tuning.
- **A real front end has an imbalance.** The two analogue paths never match
  exactly, so a carrier at +f leaks into −f. With 2% gain error and 3° of phase
  error the image sits at about −31 dB, which is audible as a ghost station
  mirroring every signal. `IqImbalance` measures and corrects it blind —
  no calibration signal, no factory trim — and takes it to −80 dB:

```
injected 2.0% gain error, 3.0 deg phase error
measured 2.01% gain, 2.99 deg phase
image before : -31.2 dB
image after  : -80.2 dB
```

The correction is the identity transform on a perfectly balanced signal, which
is why it is safe to leave enabled. `--no-imbalance` turns it off if you want to
see the ghost.

## Many listeners, one sound card

```
sound card ──► CaptureHub ──┬──► RadioSession "alpha"  (3000 Hz, AM, 6 kHz)
   one ADC                  ├──► RadioSession "bravo"  (18 kHz, CW, 500 Hz)
   one thread               └──► RadioSession "charlie" ...
                                    each: own NCO, filters, demod, AGC
```

The card can only be opened once, so exactly one thread reads it and hands each
raw block to every session. Everything downstream of the mixer is per-session:
frequency, mode, bandwidth, squelch, blanker, AGC. Two browsers can sit on two
different frequencies and neither disturbs the other.

The waterfall is shared, because the spectrum of the band is the same for
everyone; only demodulation is duplicated.

**Cost.** N listeners means N FIR chains. At 192 kHz a 9 kHz AM channel is
~23 M multiply-adds/s each, so eight listeners is ~184 M/s — fine on anything
modern, and the reason the server caps sessions at 8 and returns `503` past
that rather than quietly degrading everyone. The cap is `WebRadio.MaxSessions`.

**Sessions** are identified by an opaque id the page keeps in a variable and
appends to every request. No cookies: they need parsing, expiry and a
conversation about consent, and a query parameter does the same job. A request
without an id uses the `default` session, so `curl http://host/state` still
works. Idle sessions are reaped after five minutes, except `default`.

```
curl "http://localhost:8080/state?sid=alpha"
curl -d "f=18000&mode=cw&bw=500" "http://localhost:8080/tune?sid=alpha"
```

## RDS

```cmd
bin\Radiocity.exe web --sim-fm          rem synthetic station, no hardware
bin\Radiocity.exe web --iq --rate 192000 -stereo
```

RDS rides on a 57 kHz subcarrier - the third harmonic of the 19 kHz stereo pilot,
which is not a coincidence: it lets a receiver lock the data to a reference it
already has. Same trick as stereo, one power higher: the pilot phasor is cubed,
not squared.

What comes out:

```
pi=CAFE ps=RADIOCIT pty=10
RT: RADIOCITY SOUND CARD SDR - SYNTHETIC FM DEMO
```

Decoded: PI (station identifier), PS (8-character station name), RadioText (up to
64 characters), PTY (programme type), and group statistics.

**Why there is no preamble.** Every 26-bit block is 16 data bits plus a 10-bit
CRC, and the CRC is XORed with an *offset word* that identifies the block position
in the group (A, B, C or D). Block sync is therefore free: look for a window
whose syndrome equals offset A.

**Error correction** repairs up to two flipped bits by trying every one- and
two-bit pattern. The full standard corrects five using a syndrome table; that
table costs more memory than the rest of the decoder and two bits covers most
real damage, so three or more drops the block and the group parser waits.

**Differential decoding** means a 180 degree phase error in the demodulator is
harmless: what arrives is the XOR of consecutive bits, so inverting everything
changes nothing.

**The bit clock is free-running.** There is no timing recovery, and that is a
known limitation rather than a choice: the textbook detectors all assume a
sharper waveform than a band-limited biphase symbol provides, and all three
attempts made a clean signal worse. With the clocks agreeing the decoder is exact
(227 groups, 0 bad blocks over 20 s); with a 20 ppm offset it degrades to partial
text rather than to garbage. The measurements and the proper fix are in
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

**Integrity comes from the PI.** Every group repeats the same programme
identifier, so a group that decodes with a different one was read at the wrong
alignment and is discarded instead of overwriting the station name.

The web UI shows the station name and text in a panel under the waterfall, and
`/state` exposes `pi`, `ps`, `rt`, `pty`, `groups` and `rds`.

## Radio mode (narrowband FM)

```cmd
bin\Radiocity.exe web --sim-radio            rem synthetic handheld, no hardware
bin\Radiocity.exe web --iq --mode nfm -ctcss
bin\Radiocity.exe listen 145500000 60 nfm 192000 -ctcss88.5
```

NFM is a different receiver from broadcast FM, not a narrower setting:

| | Broadcast FM | Narrowband FM |
|---|---|---|
| Deviation | ±75 kHz | ±2.5 kHz |
| Channel | 180–200 kHz | 12.5 kHz |
| Baseband | 128–192 kHz | 16 kHz |
| Audio | 15 kHz, de-emphasis | 300–3000 Hz, no de-emphasis |
| Extra | stereo, RDS | CTCSS tone squelch |

**CTCSS** is the part that makes it a radio rather than a scanner. A handheld
transmits a sub-audible tone (67–254 Hz) alongside the voice, and the receiver
keeps its speaker muted until it hears *its* tone. That is how several groups
share one channel without hearing each other.

The detector runs on the raw discriminator output, before the 300 Hz high-pass
that removes the tone from the speaker — the tone and the speech are only 46 Hz
apart, so the filter that hides the tone is exactly the filter that would blind
the detector.

```
ctcss 88.5 Hz        <- tone present
ctcssWanted 88.5     <- what this receiver listens for
ctcssOpen true       <- gate open
```

**DCS** is the digital successor: instead of a tone, a Golay-coded word repeats
at 134.4 bps. `-dcs023` opens for that code, `-dcs` for any.

```
dcs 023              <- code decoded
dcsWanted 023        <- what this receiver listens for
dcsOpen true         <- gate open
```

The word is a Golay (23,12) code word — 12 data bits, 11 check bits, corrects
three errors. There is no framing at all, so block sync has to come from the data
itself, which is why the code carries so much redundancy.

**One thing worth knowing.** Golay (23,12) is a *cyclic* code, so every rotation
of a code word is also a valid code word, and decodes to a *different* code. A
receiver cannot tell from the word alone which bit the frame starts at, so the
same transmission legitimately decodes to any of several codes depending on
where the boundary falls. That is a property of the code, not a defect. This
implementation accepts any alignment of the code you asked for, so a repeater
transmitting 023 opens a receiver set to 023 regardless.

With the wrong code configured, the gate stays shut and the audio is silent —
not quieter, silent. That is the assertion the CI test makes.

Two things worth knowing before pointing this at real hardware:

- **446 MHz is not in the sound card's band.** PMR446 and LPD433 need the same
  quadrature downconverter as broadcast FM; see [docs/HARDWARE.md](docs/HARDWARE.md).
- **A handheld is not a repeater.** Its deviation, its antenna and its 0.5 W are
  all built for a few hundred metres. Expect to need the downconverter's gain,
  not the receiver's.

## Recording the air

```
bin\Radiocity.exe iq record capture.wav 77500 30 192000 -b24
bin\Radiocity.exe iq info capture.wav
bin\Radiocity.exe iq demod capture.wav am.wav am
bin\Radiocity.exe iq demod capture.wav usb.wav usb
```

The recording is the baseband the demodulators see, so one capture can be
replayed through every mode without going back to the air. That is the
difference between "I heard something" and "I have the evidence".

The format is a stereo WAV — I in the left channel, Q in the right — with
32-bit float samples and the centre frequency in a `LIST/INFO` comment. It
opens in Audacity, SoX and Python, and the header carries the sample rate
where it belongs. A raw dump plus a README is not a file format.

## WebSDR

```cmd
bin\Radiocity.exe web --sim
```

Then open <http://localhost:8080/>. `--sim` serves a synthetic band with five
AM carriers, so the UI can be developed and tested on a machine with no audio
input at all — which is also how CI would exercise it.

With a real card:

```cmd
bin\Radiocity.exe web --rate 192000 --freq 77500 --mode am --port 8080
```

| Option | Meaning |
|---|---|
| `--sim` | synthetic source instead of the sound card |
| `--port N` | HTTP port, default 8080 |
| `--rate N` | capture rate, default 48000 |
| `--freq N` | initial tuning in Hz |
| `--mode M` | `am`, `fm`, `usb`, `lsb`, `cw` |
| `--bits N` | capture depth (same as `-b24`) |

Endpoints: `/` (UI), `/waterfall?from=N` (raw spectrum columns),
`/state` (JSON), `/tune` (POST), `/audio` (chunked 16-bit PCM at 48 kHz).

Two constraints shaped this:

- **`HttpListener` needs admin rights on XP** because it sits on `http.sys`, so
  the server is a plain `TcpListener` with hand-rolled HTTP/1.1. It supports
  keep-alive, query strings, form bodies and chunked streaming — enough for a
  WebSDR and nothing more.
- **.NET 4.0 has no `WebSockets`** (that is 4.5), so audio is a chunked HTTP
  stream decoded with `fetch` + `ReadableStream`, and the waterfall is polled.
  Both work in any browser that can run the page at all.

## License

MIT. See [LICENSE](LICENSE).

<div align="center">
<sub>Built for machines that were supposed to be obsolete.</sub>
</div>
