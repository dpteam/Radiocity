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
SSB     : sideband rejection -106.9 dB
CW      : carrier -> 700 Hz beat note
clock   : 27.0 ppm injected, 26.92 ppm measured
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

**It cannot receive broadcast FM at 87.5 MHz.** Not "with effort" — not at all.
A 192 kHz codec samples 0–96 kHz; 87.5 MHz is 900× above that. No demodulator
recovers information the samples never contained.

What it *does* receive on a bare wire is **VLF/LF**: time signals, navigation
beacons, sferics, whistlers. See [docs/THEORY.md](docs/THEORY.md) for the
numbers and [docs/HARDWARE.md](docs/HARDWARE.md) for what to add when you want
more than a wire.

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

Modes: `am`, `fm`, `usb`, `lsb`, `cw`. Add `-b8`, `-b16`, `-b24` or `-b32` to
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
```

## The signal path

```
waveIn ──► NCO ──► FIR + decimate ──► demod ──► AGC ──► resample ──► waveOut
           mix      channel filter    AM/FM/SSB/CW
```

| Stage | File | The interesting part |
|---|---|---|
| Capture | `src/Audio/WinMM.cs` | `waveIn*` P/Invoke; the only API alive on XP, Vista, 7, 8, 10 and 11 |
| Mixing | `src/Dsp/Nco.cs` | Table lookup with interpolation; phase as a fraction of a turn, so retuning never clicks |
| Filtering | `src/Dsp/Fir.cs` | Kaiser window; tap count computed from the requirement, not guessed |
| AM | `src/Dsp/Demod.cs` | Envelope + 20 Hz DC blocker |
| FM | `src/Dsp/FmDemod.cs` | `atan2` of `z[n]·conj(z[n-1])` — amplitude-independent, so no limiter |
| SSB/CW | `src/Dsp/SsbDemod.cs` | Phasing method, 127-tap Hilbert, −107 dB rejection |
| AGC | `src/Dsp/Demod.cs` | RMS detector, asymmetric *gain slew* (not an asymmetric detector) |
| Resampling | `src/Dsp/Demod.cs` | Catmull-Rom; linear interpolation would droop 3.3 dB at 5 kHz |
| Clock | `src/Dsp/ClockCalib.cs` | FFT peak + parabolic refinement + Goertzel sweep, under 1 ppm |
| DCF77 | `src/Dsp/Dcf77.cs` | 100/200 ms dip widths, BCD telegram, three parity bits |

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
src/App/                     CLI, WAV reader/writer, assembly info
tools/make-icon.ps1          generates assets/radiocity.ico (reproducible, not hand-drawn)
docs/ARCHITECTURE.md         how it works and why each stage is built that way
docs/THEORY.md               the radio theory, in numbers
docs/HARDWARE.md             antennas and downconverters, by budget
.github/workflows/build.yml  CI: build, self-test, validate icon, publish artefacts
```

## Status

Working: capture and playback, AM, FM, USB, LSB, CW, AGC, spectrum, offline
demodulation, clock calibration, DCF77 decoding, 8/16/24/32-bit capture.

Not yet:

- [ ] WebSDR front end — `HttpListener` + waterfall + audio stream. Note that
      .NET 4.0 has no `WebSockets` (that is 4.5), so long-poll, SSE, or a custom
      framing over TCP.
- [ ] A second decimation stage for real CW selectivity — right now the channel
      filter is ~3 kHz, so a CW note is audible but noisy.
- [ ] Squelch / noise blanker.
- [ ] I/Q recording to file, so the air can be analysed offline.
- [ ] RSDN-20 and SAQ decoders.

## License

MIT. See [LICENSE](LICENSE).

<div align="center">
<sub>Built for machines that were supposed to be obsolete.</sub>
</div>
