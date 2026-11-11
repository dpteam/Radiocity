# Changelog

All notable changes to this project. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Planned
- WebSDR front end (`HttpListener`, waterfall, audio stream)
- Second decimation stage for real CW selectivity
- Squelch and noise blanker
- I/Q recording to file
- RSDN-20 and SAQ decoders

## [0.4.0] - 2026-11-11

First version that does something real with the air.

### Added
- **DCF77 decoder** — 77.5 kHz time signal, 100/200 ms dip widths, BCD telegram,
  three parity checks, automatic frame sync from the missing pulse at second 59.
  Verified at −40 and −20 dBFS.
- **SSB (USB/LSB) via the phasing method** — 127-tap Hilbert transformer,
  −107 dB unwanted sideband rejection.
- **CW** — the same phasing path with a BFO shift, turning an unmodulated carrier
  into a 700 Hz beat note.
- **Clock calibration** — measures the sound card's sample-clock error in ppm
  against a carrier of known frequency (FFT peak, parabolic refinement, Goertzel
  sweep). Better than 1 ppm.
- **24-bit capture** (`-b24`) and 8/32-bit, selectable per command.
- **Catmull-Rom resampler** between the channel rate and the rate the sound card
  will accept.
- `ReceiverOptions` to describe a receive setup by name instead of a long
  positional parameter list, including `AgcEnabled` for measurement.

### Changed
- `AmReceiver` replaced by `Receiver`, which handles all five modes.
- Channel filter tap count is now computed from the passband/stopband
  requirement instead of being guessed.
- `WaveOut.Write` splits arbitrary block sizes across driver buffers.

### Fixed
- **AGC read ~3 dB high.** An asymmetric attack/release on a peak detector turns
  it into a waveform follower. The asymmetry now lives on the gain slew, not the
  detector.
- **AGC window was shorter than a few audio periods**, modulating gain at twice
  the tone frequency — measurable distortion, not AGC.
- **DCF77 encoder packed BCD as binary.** `(hour / 2) % 2` instead of
  `(hour % 10) >> 1`: correct for 08:00, wrong for 14:00.
- **DCF77 day-of-week off by one.** DCF77 uses 1 = Monday; .NET uses 0 = Sunday.
- **Resampler dropped samples at block boundaries** and, once fixed, drooped
  3.3 dB at 5 kHz because it was linear. Now cubic.
- **SSB rejection appeared to be −36 dB** because the AGC amplified the
  cancelled sideband back to full scale. Real figure is −107 dB.

## [0.3.0] - 2026-11-11

### Added
- FM demodulator: quadrature discriminator via `atan2`, plus de-emphasis.
- `listen` command for live receive.
- `scan` command with an ASCII waterfall.
- Kaiser-windowed FIR design with a computed tap count.

### Fixed
- AM self-test measured through the AGC settling transient, which smeared the
  Goertzel bin and looked like distortion.

## [0.2.0] - 2026-11-11

### Added
- AM demodulator with envelope detection and a DC blocker.
- RMS AGC with independent attack and release.
- WAV reader/writer: 8/16/24/32-bit PCM and float32 input.
- `spec` and `demod` commands.

## [0.1.0] - 2026-11-11

### Added
- `waveIn`/`waveOut` P/Invoke against `winmm.dll`.
- Lock-free single-producer/single-consumer ring buffer.
- Table-driven NCO with linear interpolation.
- Complex FIR decimator.
- Radix-2 FFT and Goertzel.
- `selftest` command.
- `build.cmd` driving the in-box `csc.exe`, so no SDK or targeting pack is
  needed.
