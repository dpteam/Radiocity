# Changelog

All notable changes to this project. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Planned
- RSDN-20 and SAQ decoders
- RDS clock time (group 4A), verified against a real transmitter
- DCS (digital coded squelch) alongside CTCSS
- Optional password and rate limiting for the web server

## [0.11.0] - 2026-11-12

### Added
- **Narrowband FM** (`nfm`) — the two-way radio mode: ±2.5 kHz deviation in a
  12.5 kHz channel, 300–3000 Hz audio band-pass, no de-emphasis by default.
- **CTCSS decoder** — all 50 EIA sub-audible tones, detected with a one-second
  Goertzel window after a two-stage decimating low-pass. Reports the tone in Hz.
- **Tone squelch** — `-ctcss` opens the gate for any tone, `-ctcss88.5` for a
  specific one. The gate ramps in 5 ms and out 80 ms, so it does not click.
- `SyntheticRadioSource` and `web --sim-radio`: a handheld transmitting a
  voice-like tone with 88.5 Hz CTCSS, so the radio path is demonstrable and
  testable without hardware.
- CTCSS badge in the web UI; `ctcss`, `ctcssWanted` and `ctcssOpen` in `/state`;
  `ctcss` parameter on `/tune`.
- NFM and CTCSS integration test in CI, including the negative case (wrong tone
  must produce silence, not just quieter audio).

### Changed
- `FirDesign.HighPass` via spectral inversion, so the speech band-pass is a
  low-pass and a high-pass with the same tap count and transition width.
- `Squelch` takes separate detector and gate rates.

### Notes
- The audio band-pass and the CTCSS detector disagree by design: the tone lives
  at 67–254 Hz and speech starts at 300 Hz, so the detector taps the
  discriminator output *before* the high-pass that removes the tone from the
  speaker.

## [0.10.0] - 2026-11-12

### Added
- **RDS decoding** — 57 kHz subcarrier, biphase symbols, differential decoding,
  block sync from CRC offset words, two-bit error correction, and group parsing
  for PI, PS, RadioText, PTY and TP.
- `SyntheticFmSource` and `web --sim-fm`: a complete synthetic FM stereo station
  with RDS, so the whole FM path can be demonstrated on a machine with no sound
  card and no antenna.
- RDS panel in the web UI and `pi`/`ps`/`rt`/`pty`/`groups`/`rds` in `/state`.
- RDS integration test in CI: it asserts the exact station name and text, because
  RDS is digital and "roughly right" means broken.

### Changed
- `RdsDecoder.Process` chunks internally, so callers can pass whatever block size
  their capture produced.

### Notes
- Clock time (group 4A) is deliberately not implemented. The field layout is
  documented but unverified against a real transmitter, and shipping a plausible
  guess for a *clock* is worse than shipping nothing.

## [0.9.0] - 2026-11-12

### Added
- **FM stereo** (`-stereo`, `web --stereo`) — pilot recovery at 19 kHz, 38 kHz
  subcarrier, stereo matrix, de-emphasis applied to L and R after the matrix.
  Measured separation 48–50 dB on a synthetic broadcast.
- `FmStereoDecoder`, `LinkedAgc`, `RealDecimator`.
- Stereo through the whole path: the web `/audio` stream declares two channels,
  the UI decodes interleaved PCM and feeds two Web Audio channels, and the CLI
  plays interleaved samples.
- `Receiver.Channels`, so a caller can tell mono from stereo without guessing.

### Changed
- **FM baseband now covers the channel, not the audio.** Broadcast FM deviates by
  ±75 kHz, so the chain must not decimate below ~150 kHz before the
  discriminator; a 48 kHz baseband clipped the deviation and destroyed the
  signal. Audio is decimated to 48 kHz *after* demodulation, in a separate stage.
- Mono FM also benefits: the old 12 kHz baseband truncated audio at 6 kHz.
- `Squelch` takes separate detector and gate rates, because for stereo they
  differ by the decimation factor.

### Fixed
- **Stereo subcarrier was derived from the pilot's first power instead of its
  square.** The subcarrier is at twice the pilot's angle, so the pilot phasor has
  to be squared: `e^{j(theta+2psi)} = e^{j theta} * (conj(p)/|p|)^2`. Using
  `conj(p)/|p|` directly works only when the pilot happens to start at phase
  zero, and otherwise swaps the channels with ~12 dB of leakage — which is
  exactly what it did, and why the bug survived the first round of tests.
- Sign of the squared phasor's imaginary part: with `+2*pI*pQ` the decoder locks
  to `2*theta - 2*psi` and mirrors the sideband.
- The FM discriminator was not being run at all in stereo mode, so the composite
  fed to the decoder was silence.

## [0.8.0] - 2026-11-12

### Added
- **Quadrature front end** (`radiocity web --iq`) — the sound card is opened as
  stereo, I in the left channel and Q in the right, exactly what a SoftRock-style
  downconverter produces. The receiver then sees the full complex band
  −rate/2 … +rate/2 around the local oscillator instead of 0 … rate/2.
- **`IqImbalance`** — blind Gram-Schmidt correction of gain and phase mismatch,
  measured from the signal statistics with no calibration signal. Takes the
  image from −31 dB to −80 dB for a 2% / 3° front end.
- `Receiver.ProcessComplex` — demodulation of complex input that still needs the
  NCO, as opposed to `ProcessBaseband` for recordings that are already centred.
- `SyntheticQuadratureSource` and `--sim-iq`: a simulated SoftRock *with* an
  imbalance, so the correction has something real to fix in tests and demos.
- Waterfall and `/state` now report `quadrature`, `bandLo` and `bandHi`; the UI
  draws a signed axis and maps clicks onto it.
- Quadrature integration test in CI, separate from the real-source one.

### Changed
- Frequency clamping uses the source's actual band rather than assuming 0..rate/2.

### Fixed
- **Tuning did nothing in quadrature mode.** I had assumed a quadrature front
  end removes the need for the NCO. It does not: the hardware mixes the wanted
  *band* down to DC, but a station still sits somewhere inside that band, so
  software still has to select it. Every frequency sounded identical until
  `ProcessComplex` put the NCO back in front of the demodulator.
- Imbalance correction moved ahead of the mixer. It is a real 2x2 matrix and does
  not commute with the complex rotation of the NCO, so correcting after mixing
  would leave a residual image.

## [0.7.0] - 2026-11-12

### Added
- **Independent tuning per listener.** One sound card, one capture thread, one
  `RadioSession` per browser — each with its own frequency, mode, bandwidth,
  squelch, blanker and AGC. Two people can listen to two different frequencies
  at the same time, which is what a WebSDR is supposed to do.
- `CaptureHub` — owns the source and the shared waterfall, fans raw blocks out
  to every session without blocking.
- Session ids in the query string, with a `default` session for clients that do
  not keep one, idle reaping after five minutes, and a hard cap of 8 receivers
  (`503` past that) because N listeners really do mean N FIR chains.
- Session count in the UI header and in `/state`.

### Changed
- The web front end is now three pieces instead of one: `CaptureHub` (source and
  spectrum), `RadioSession` (one receiver), `WebRadio` (sessions and endpoints).
- Receivers run from the moment a session exists, not from the moment audio
  attaches — a level meter that only works after you press "Listen" is useless,
  since its job is to tell you whether to press it.

### Fixed
- **An abandoned audio connection held a pool thread forever.** A socket whose
  peer vanished only reports it on write, and an idle stream never writes, so
  the handler span forever. Ten seconds of silence now ends it.

## [0.6.0] - 2026-11-12

### Added
- **I/Q recording** (`radiocity iq record|demod|info`) — captures the baseband
  the demodulators see and stores it as a stereo WAV (I left, Q right, 32-bit
  float, centre frequency in a `LIST/INFO` comment). One capture can then be
  replayed through AM, FM, USB, LSB and CW without going back to the air.
- **Impulse noise blanker** (`NoiseBlanker`, `-nb`, and a checkbox in the web
  UI) — detects spikes against a slow power average and blanks them before the
  channel filter smears the click across its whole impulse response.
- `Receiver.ProcessBaseband` — the demodulation half of the chain, callable on
  already-mixed I/Q. This is what makes recorded I/Q replayable.
- Selftests for the blanker (measured against a known clean signal) and for the
  I/Q file (bit-exact float32 round trip, half-an-LSB int16, metadata preserved).

### Fixed
- **The noise blanker deafened itself.** Its reference level starts at zero, so
  the threshold starts *below* the signal, the detector blanks the signal, and
  blanked samples never update the reference — a deadlock that looks exactly
  like a receiver gone deaf. It now runs a 50 ms warm-up where it only measures,
  and feeds the reference a clamped value while blanking.
- `IqFile` scaled int16 by 32767 but read it back divided by 32768, a systematic
  one-LSB error at full scale. Now symmetric, with rounding.

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
