# Changelog

All notable changes to this project. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Planned
- I/Q recording to file
- Noise blanker (impulse rejection)
- RSDN-20 and SAQ decoders
- Multiple simultaneous listeners with independent tuning

## [0.5.0] - 2026-11-12

The version where it becomes something you can point a browser at.

### Added
- **WebSDR front end** (`radiocity web`) — waterfall, click-to-tune, mode
  switching, bandwidth, squelch, live audio. The UI is embedded in the exe as a
  resource, so the build stays a single file.
- **Hand-rolled HTTP/1.1 server** over `TcpListener`: keep-alive, query strings,
  form bodies, chunked streaming. `HttpListener` was rejected because it sits on
  `http.sys` and needs administrator rights on XP.
- **Multi-stage decimation** (`DecimationChain`) — 192 kHz → 12 kHz in two steps
  costs 23 M multiply-adds/s instead of 106 M for the same rejection, a 4.6×
  saving that is what makes narrow CW filters affordable.
- **Squelch** with hysteresis on channel power, measured before the AGC, with a
  smooth 2 ms / 40 ms gate so it does not click.
- **Synthetic band source** (`--sim`) — five AM carriers at known frequencies, so
  the web UI and the whole receive path can be exercised on a machine with no
  audio input at all.
- Channel power meter, independent of the squelch state.
- CW channel narrowed from 3 kHz to 500 Hz; SSB from 3 kHz to 2.4 kHz.

### Fixed
- **Squelch reacted thousands of times too slowly.** The smoothing coefficient
  was computed per sample and applied once per block, so an 8192-sample block
  made the detector a memory rather than a filter. The coefficient is now
  derived from the block length.
- **The waterfall never produced a column.** It took "the last N samples of the
  block" for the FFT, but a live source delivers a few hundred samples at a
  time, so the condition never held. Samples are now accumulated until a full
  frame is available.
- **The server died on the first audio request.** `Encoding.GetBytes` was called
  with a character count longer than the string, and the resulting exception on
  a pool thread terminated the process. Also fixed: stream handlers are now
  wrapped, and an unhandled-exception handler reports rather than vanishing.
- Level meter read −200 dBFS when the squelch was off, because it shared the
  squelch's (disabled) power estimate.
- A squelch that was configured now starts muted instead of open.

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
