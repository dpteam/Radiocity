using System;
using System.Threading;

namespace Radiocity.Dsp
{
    public enum DemodMode
    {
        Am,
        Fm,
        Usb,
        Lsb,
        Cw,

        /// <summary>
        /// Narrowband FM: two-way radio, not broadcast. +-2.5 kHz deviation in a
        /// 12.5 kHz channel, 300-3000 Hz audio, optional CTCSS tone squelch.
        /// </summary>
        Nfm
    }

    /// <summary>
    /// Everything that defines a receive setup. A class rather than a long
    /// parameter list because half of these only make sense for one mode and a
    /// caller that gets them wrong deserves a named field, not a swapped double.
    /// </summary>
    public sealed class ReceiverOptions
    {
        public DemodMode Mode;
        public int Decimation;
        public double ChannelHz;
        public double DeviationHz;
        public double DeemphasisUs;

        /// <summary>Beat frequency oscillator offset. Non-zero only for CW.</summary>
        public double BfoHz;

        /// <summary>Hilbert length for SSB. 127 taps is about -70 dB sideband rejection.</summary>
        public int SsbTaps;

        /// <summary>
        /// Turn the AGC off to measure the raw demodulator output. On an SSB
        /// receiver the AGC will happily apply +60 dB to a silent channel, which
        /// is correct for listening and ruinous for measuring sideband rejection.
        /// </summary>
        public bool AgcEnabled;

        /// <summary>Squelch threshold in dBFS of channel power; null disables it.</summary>
        public float? SquelchDb;

        /// <summary>
        /// Impulse blanker threshold as a multiple of the average power; null
        /// disables it. 10-20 is a sensible range: too low starts eating the
        /// signal, too high stops catching the clicks.
        /// </summary>
        public float? BlankerThreshold;

        /// <summary>
        /// Correct I/Q gain and phase imbalance (Gram-Schmidt). Only meaningful
        /// for a real quadrature front end; on synthesised I/Q the correction is
        /// the identity transform, so leaving it off is also correct.
        /// </summary>
        public bool CorrectImbalance;

        /// <summary>
        /// Decode FM stereo (pilot + 38 kHz subcarrier) instead of mono. Needs a
        /// wide baseband: the composite carries information up to 53 kHz, so the
        /// card must be sampling at 128 kHz or more.
        /// </summary>
        public bool FmStereo;

        /// <summary>
        /// Decode RDS from the 57 kHz subcarrier. Only meaningful together with
        /// FmStereo, because it needs the pilot as a phase reference and a
        /// baseband wide enough to hold 57 kHz.
        /// </summary>
        public bool Rds;

        /// <summary>
        /// CTCSS tone the receiver will listen for, in Hz; 0 accepts any tone and
        /// null disables tone squelch entirely (carrier squelch only).
        /// </summary>
        public double? CtcssTone;

        /// <summary>
        /// DCS code the receiver will listen for, as the 9-bit value (see
        /// <see cref="Dcs.Parse"/>); 0 accepts any code, null disables DCS.
        /// </summary>
        public int? DcsCode;

        public ReceiverOptions()
        {
            Mode = DemodMode.Am;
            Decimation = 4;
            ChannelHz = 9000.0;
            DeviationHz = 75000.0;
            DeemphasisUs = 50.0;
            BfoHz = 0.0;
            SsbTaps = 127;
            AgcEnabled = true;
            SquelchDb = null;
            BlankerThreshold = null;
            CorrectImbalance = false;
            FmStereo = false;
            Rds = false;
        }

        public static ReceiverOptions Am(int decimation)
        {
            ReceiverOptions o = new ReceiverOptions();
            o.Mode = DemodMode.Am;
            o.Decimation = decimation;
            o.ChannelHz = 9000.0;
            return o;
        }

        public static ReceiverOptions Fm(int decimation)
        {
            ReceiverOptions o = new ReceiverOptions();
            o.Mode = DemodMode.Fm;
            o.Decimation = decimation;
            o.ChannelHz = 180000.0;
            o.DeviationHz = 75000.0;
            o.DeemphasisUs = 50.0;
            return o;
        }

        /// <summary>FM stereo. Requires the card to deliver at least 128 kHz.</summary>
        public static ReceiverOptions FmStereoMode(int decimation)
        {
            ReceiverOptions o = Fm(decimation);
            o.FmStereo = true;
            o.Rds = true;
            return o;
        }

        /// <summary>
        /// Narrowband FM, the two-way radio mode: +-2.5 kHz deviation, 12.5 kHz
        /// channel, 300-3000 Hz audio. No de-emphasis by default, because
        /// handhelds usually do not apply one and adding it would make a clean
        /// signal sound thin.
        /// </summary>
        public static ReceiverOptions Nfm(int decimation)
        {
            ReceiverOptions o = new ReceiverOptions();
            o.Mode = DemodMode.Nfm;
            o.Decimation = decimation;
            o.ChannelHz = 11000.0;          // Carson bandwidth for 2.5 kHz deviation
            o.DeviationHz = 2500.0;
            o.DeemphasisUs = 0.0;
            return o;
        }

        public static ReceiverOptions Ssb(bool upperSideband, int decimation)
        {
            ReceiverOptions o = new ReceiverOptions();
            o.Mode = upperSideband ? DemodMode.Usb : DemodMode.Lsb;
            o.Decimation = decimation;
            o.ChannelHz = 2400.0;
            return o;
        }

        public static ReceiverOptions Cw(double pitchHz, int decimation)
        {
            ReceiverOptions o = new ReceiverOptions();
            o.Mode = DemodMode.Cw;
            o.Decimation = decimation;

            // 500 Hz channel: 250 Hz either side of the carrier. Wide enough that
            // a little drift does not lose the signal, narrow enough that the
            // noise floor drops by about 13 dB against a 2.4 kHz SSB filter.
            o.ChannelHz = 500.0;
            o.BfoHz = pitchHz;
            return o;
        }
    }

    /// <summary>
    /// The whole receive chain in one place: tune, mix, channel-filter, decimate,
    /// demodulate, AGC, squelch. Everything is preallocated; Process() performs
    /// zero heap allocations so it can run on the audio callback thread without
    /// waking the GC.
    /// </summary>
    public sealed class Receiver
    {
        private readonly Nco _nco;
        private readonly DecimationChain _chain;
        private readonly AmDemod _am;
        private readonly FmDemod _fm;
        private readonly SsbDemod _ssb;
        private readonly Agc _agc;
        private readonly Squelch _squelch;
        private readonly NoiseBlanker _blanker;
        private readonly IqImbalance _imbalance;
        private readonly FmStereoDecoder _stereo;
        private readonly RdsDecoder _rds;
        private readonly LinkedAgc _stereoAgc;
        private readonly DemodMode _mode;
        private readonly bool _agcEnabled;

        private readonly float[] _i;
        private readonly float[] _q;
        private readonly float[] _bi;
        private readonly float[] _bq;
        private readonly float[] _audio;
        private readonly float[] _stereoOut;
        private readonly RealDecimator _audioDecim;
        private readonly float[] _audioDecimOut;

        private readonly RealDecimator _bandPassLo;
        private readonly RealDecimator _bandPassHi;
        private readonly CtcssDecoder _ctcss;
        private readonly DcsDecoder _dcs;
        private readonly float[] _narrowBuf;
        private readonly double? _ctcssExpected;
        private readonly int? _dcsExpected;
        private float _ctcssGate = 1.0f;
        private float _ctcssGateAttack;
        private float _ctcssGateRelease;

        /// <summary>
        /// Pending tune in whole Hz, 0 meaning none. An int so the write is atomic
        /// on every CPU this runs on: the DSP thread reads it at the start of a
        /// block and never sees a half-updated double.
        /// </summary>
        private volatile int _pendingTuneHz;

        private float _levelDb = -120.0f;

        public Receiver(double sampleRate, double tuneHz, ReceiverOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");

            _mode = options.Mode;
            SampleRate = sampleRate;
            _ctcssExpected = options.CtcssTone;
            _dcsExpected = options.DcsCode;

            // FM needs a much wider baseband than AM or SSB. A stereo broadcast
            // puts information up to 53 kHz into the composite, so the chain must
            // not decimate below ~106 kHz; even mono FM carries audio to 15 kHz,
            // which 12 kHz of baseband would truncate at 6 kHz.
            int decim = options.Decimation;
            if (_mode == DemodMode.Fm)
            {
                // The baseband must cover the *channel*, not the audio. Broadcast
                // FM deviates by +-75 kHz, so anything narrower than ~150 kHz
                // clips the deviation and destroys the signal before the
                // discriminator ever sees it. Audio is decimated after
                // demodulation, which is a separate step.
                double target = Math.Max(options.FmStereo ? 128000.0 : 48000.0, options.ChannelHz * 1.1);
                decim = Math.Max(1, (int)(sampleRate / target));
            }
            else if (_mode == DemodMode.Nfm)
            {
                // 16 kHz covers +-2.5 kHz of deviation plus 3 kHz of audio with
                // room to spare, and it divides both 48 and 192 kHz exactly.
                // Audio is decimated to 8 kHz after demodulation.
                decim = Math.Max(1, (int)(sampleRate / 16000.0));
            }

            int basebandRate;

            int baseband = (int)(sampleRate / decim);
            if (_mode == DemodMode.Fm && options.FmStereo && baseband < 106000)
            {
                throw new NotSupportedException(
                    "FM stereo needs at least 128 kHz of capture; this is " +
                    (sampleRate / 1000.0).ToString("F0", System.Globalization.CultureInfo.InvariantCulture) +
                    " kHz. Use --rate 192000, or listen in mono.");
            }

            OutputRate = baseband;

            _nco = new Nco();
            _nco.SetFrequency(tuneHz, sampleRate);

            _chain = new DecimationChain(sampleRate, OutputRate, options.ChannelHz, 32768);
            OutputRate = _chain.OutputRate;
            basebandRate = OutputRate;

            switch (_mode)
            {
                case DemodMode.Am:
                    _am = new AmDemod(OutputRate, 20.0);
                    break;
                case DemodMode.Fm:
                    // The discriminator always runs: it produces the composite,
                    // which is the input to both the mono path and the stereo
                    // decoder. De-emphasis is deliberately zero here for stereo,
                    // because the transmitter pre-emphasised L and R, not the
                    // composite — applying it early would distort the sideband.
                    _fm = new FmDemod(OutputRate, options.DeviationHz,
                                      options.FmStereo ? 0.0 : options.DeemphasisUs);

                    if (options.FmStereo)
                    {
                        _stereo = new FmStereoDecoder(OutputRate, 48000, options.DeemphasisUs);

                        // RDS rides on the same composite at 57 kHz, so it has to
                        // be decoded at the baseband rate, before OutputRate is
                        // reassigned to the audio rate.
                        if (options.Rds && OutputRate >= 32000) _rds = new RdsDecoder(OutputRate);

                        OutputRate = _stereo.AudioRate;
                        Channels = 2;

                        // Linked, because a mono AGC on interleaved L/R would move
                        // the stereo image every time one channel got louder.
                        _stereoAgc = new LinkedAgc(OutputRate, 25.0, 400.0, 0.30);
                    }
                    else
                    {
                        // The discriminator output is a composite at the full
                        // baseband rate; the audio it carries stops at 15 kHz, so
                        // drop it to ~48 kHz before anything else touches it.
                        int audioDecim = Math.Max(1, OutputRate / 48000);
                        if (audioDecim > 1)
                        {
                            double pass = 15000.0 / OutputRate;
                            double stop = 0.5 / audioDecim;
                            int taps = (stop - pass < 0.002)
                                ? 63
                                : (int)Math.Ceiling(62.0 / (2.285 * 2.0 * Math.PI * (stop - pass)));

                            _audioDecim = new RealDecimator(
                                FirDesign.LowPass((pass + stop) * 0.5, taps, 8.6), audioDecim);
                            OutputRate = OutputRate / audioDecim;
                        }
                    }
                    break;
                case DemodMode.Usb:
                    _ssb = new SsbDemod(options.SsbTaps, 32768, OutputRate, 0.0, true);
                    break;
                case DemodMode.Lsb:
                    _ssb = new SsbDemod(options.SsbTaps, 32768, OutputRate, 0.0, false);
                    break;
                case DemodMode.Cw:
                    _ssb = new SsbDemod(options.SsbTaps, 32768, OutputRate, options.BfoHz, true);
                    break;
                case DemodMode.Nfm:
                    {
                        _fm = new FmDemod(OutputRate, options.DeviationHz, options.DeemphasisUs);

                        // Band-pass 300-3000 Hz: the speech band. Below 300 Hz
                        // lives the CTCSS tone, which must not be audible; above
                        // 3 kHz is discriminator noise that adds nothing.
                        int baseRate = OutputRate;

                        double lpPass = 3000.0 / baseRate;
                        double lpStop = Math.Min(0.45, 4500.0 / baseRate);
                        int lp = (int)Math.Ceiling(62.0 / (2.285 * 2.0 * Math.PI * Math.Max(0.002, lpStop - lpPass)));
                        if (lp < 31) lp = 31;
                        if (lp > 1023) lp = 1023;

                        _bandPassLo = new RealDecimator(FirDesign.LowPass((lpPass + lpStop) * 0.5, lp, 8.6), 1);

                        double hpPass = 300.0 / baseRate;
                        double hpStop = Math.Min(0.44, 200.0 / baseRate);
                        int hp = (int)Math.Ceiling(62.0 / (2.285 * 2.0 * Math.PI * Math.Max(0.002, hpPass - hpStop)));
                        if (hp < 31) hp = 31;
                        if (hp > 1023) hp = 1023;

                        _bandPassHi = new RealDecimator(FirDesign.HighPass((hpPass + hpStop) * 0.5, hp, 8.6), 1);

                        _ctcss = new CtcssDecoder(baseRate);
                        _dcs = new DcsDecoder(baseRate);

                        // 16 kHz -> 8 kHz. Half of what the card was told, and
                        // exactly the rate the card will accept.
                        int audioDecim = Math.Max(1, baseRate / 8000);
                        if (audioDecim > 1)
                        {
                            double pass = 3000.0 / baseRate;
                            double stop = 0.5 / audioDecim;
                            int taps = (stop - pass < 0.002)
                                ? 63
                                : (int)Math.Ceiling(62.0 / (2.285 * 2.0 * Math.PI * (stop - pass)));

                            _audioDecim = new RealDecimator(
                                FirDesign.LowPass((pass + stop) * 0.5, taps, 8.6), audioDecim);
                            OutputRate = baseRate / audioDecim;
                        }
                    }
                    break;
            }

            // SSB and CW have no carrier to lock onto, so the AGC must be slower:
            // a fast release on speech pumps the noise floor between syllables.
            bool voice = _mode == DemodMode.Usb || _mode == DemodMode.Lsb ||
                         _mode == DemodMode.Cw || _mode == DemodMode.Nfm;
            _agc = voice ? new Agc(OutputRate, 40.0, 600.0, 0.30) : new Agc(OutputRate, 25.0, 400.0, 0.30);
            _agcEnabled = options.AgcEnabled;

            _squelch = new Squelch(basebandRate, OutputRate);
            if (options.SquelchDb.HasValue)
            {
                _squelch.Enabled = true;
                _squelch.ThresholdDb = options.SquelchDb.Value;

                // Start muted: opening on the first block of noise would defeat
                // the point of having a squelch at all.
                _squelch.PrimeClosed();
            }

            if (options.BlankerThreshold.HasValue)
            {
                _blanker = new NoiseBlanker(basebandRate);
                _blanker.Threshold = options.BlankerThreshold.Value;
            }

            if (options.CorrectImbalance) _imbalance = new IqImbalance(basebandRate);

            int block = 32768;
            _i = new float[block];
            _q = new float[block];
            _bi = new float[block];
            _bq = new float[block];
            _audio = new float[block];
            _stereoOut = new float[block * 2];
            _audioDecimOut = new float[block];
            _narrowBuf = new float[block];

            if (Channels == 0) Channels = 1;

            if (_mode == DemodMode.Nfm && (_ctcssExpected.HasValue || _dcsExpected.HasValue))
            {
                // 5 ms to open, 80 ms to close. Opening fast means the first
                // syllable is not chopped; closing slowly means a fading carrier
                // does not make the gate chatter.
                _ctcssGateAttack = (float)(1.0 - Math.Exp(-1.0 / (OutputRate * 0.005)));
                _ctcssGateRelease = (float)(1.0 - Math.Exp(-1.0 / (OutputRate * 0.080)));
                _ctcssGate = 0.0f;
            }
        }

        public double SampleRate { get; private set; }
        public int OutputRate { get; private set; }

        /// <summary>
        /// 1 for mono, 2 for stereo. When 2, Process writes interleaved samples
        /// and the returned count is frames, not samples.
        /// </summary>
        public int Channels { get; private set; }

        public int MaxBlock { get { return _i.Length; } }
        public int Taps { get { return _chain.TotalTaps; } }
        public int DecimationStages { get { return _chain.Stages; } }

        /// <summary>Tap count of each decimation stage, in order.</summary>
        public int[] StageTaps { get { return _chain.StageTaps; } }

        /// <summary>Input rate of each decimation stage, in order.</summary>
        public double[] StageRates { get { return _chain.StageRates; } }

        /// <summary>Filter multiply-adds per second; the real cost figure.</summary>
        public double FilterCost { get { return _chain.CostPerSecond; } }
        public DemodMode Mode { get { return _mode; } }
        public Squelch Squelch { get { return _squelch; } }

        /// <summary>Channel power in dBFS, for meters and the web UI.</summary>
        public float LevelDb { get { return _levelDb; } }

        /// <summary>
        /// Measured I/Q imbalance, when correction is enabled: gain ratio,
        /// phase error and the image rejection it implies.
        /// </summary>
        public IqImbalance Imbalance { get { return _imbalance; } }

        /// <summary>True when a 19 kHz pilot is present and the subcarrier is locked.</summary>
        public bool StereoDetected { get { return _stereo != null && _stereo.Stereo; } }

        /// <summary>Pilot amplitude, for a stereo indicator.</summary>
        public float StereoPilot { get { return _stereo != null ? _stereo.PilotLevel : 0.0f; } }

        /// <summary>RDS decoder, or null when RDS is not enabled.</summary>
        public RdsDecoder Rds { get { return _rds; } }

        /// <summary>CTCSS detector, or null outside narrowband FM.</summary>
        public CtcssDecoder Ctcss { get { return _ctcss; } }

        /// <summary>DCS decoder, or null outside narrowband FM.</summary>
        public DcsDecoder Dcs { get { return _dcs; } }

        /// <summary>Safe to call from any thread; takes effect at the next block.</summary>
        public void SetTune(double hz)
        {
            _pendingTuneHz = (int)Math.Round(hz);
        }

        /// <summary>Returns the number of audio samples written to output.</summary>
        public int Process(float[] input, int n, float[] output)
        {
            if (n > _i.Length) throw new ArgumentOutOfRangeException("n");

            int tune = _pendingTuneHz;
            if (tune != 0)
            {
                _nco.SetFrequency(tune, SampleRate);
                _pendingTuneHz = 0;
            }

            _nco.MixReal(input, _i, _q, n);
            return Demodulate(_i, _q, n, output);
        }

        /// <summary>
        /// Demodulates complex input that still needs tuning, i.e. I/Q from a
        /// quadrature front end.
        ///
        /// This is not the same as ProcessBaseband. A quadrature detector mixes
        /// the wanted band down to DC, but that band is up to 192 kHz wide and a
        /// station sits somewhere inside it — so the NCO is still needed to select
        /// one. What the hardware removes is the *image*, not the tuning.
        /// </summary>
        public int ProcessComplex(float[] i, float[] q, int n, float[] output)
        {
            if (n > _bi.Length) throw new ArgumentOutOfRangeException("n");

            // Imbalance is a property of the hardware, so it is corrected on the
            // raw I/Q before anything else touches it. Correcting after the mixer
            // would be wrong: the correction is a real 2x2 matrix, and it does not
            // commute with the complex rotation of the NCO.
            if (_imbalance != null) _imbalance.Process(i, q, n);

            int tune = _pendingTuneHz;
            if (tune != 0)
            {
                _nco.SetFrequency(tune, SampleRate);
                _pendingTuneHz = 0;
            }

            _nco.MixComplexDown(i, q, _i, _q, n);
            return Demodulate(_i, _q, n, output);
        }

        /// <summary>
        /// Demodulates an already-mixed complex baseband, skipping the NCO.
        /// This is what makes recorded I/Q replayable: the recording is the
        /// output of the mixer, so every demodulator can be tried against the
        /// same capture without going back to the air.
        /// </summary>
        public int ProcessBaseband(float[] i, float[] q, int n, float[] output)
        {
            if (n > _bi.Length) throw new ArgumentOutOfRangeException("n");
            if (_imbalance != null) _imbalance.Process(i, q, n);
            return Demodulate(i, q, n, output);
        }

        /// <summary>
        /// Narrowband FM post-processing: measure CTCSS, then band-limit to the
        /// speech band, then apply tone squelch.
        ///
        /// The order matters. CTCSS is a sub-audible tone at 67-254 Hz and the
        /// band-pass below starts at 300 Hz, so measuring after the filter would
        /// find nothing. Conversely the tone must not reach the speaker, which is
        /// exactly what the high-pass is for.
        /// </summary>
        private void ShapeNfmAudio(int n)
        {
            bool open = true;

            if (_ctcss != null)
            {
                _ctcss.Process(_audio, n);

                if (_ctcssExpected != null)
                {
                    open &= _ctcss.TonePresent &&
                            (_ctcssExpected.Value <= 0.0 ||
                             Math.Abs(_ctcss.ToneHz - _ctcssExpected.Value) < 0.5);
                }
            }

            // DCS lives in the same sub-audible region as CTCSS, so it is tapped
            // from the same point: before the 300 Hz high-pass that removes both
            // from the speaker.
            if (_dcs != null)
            {
                _dcs.Process(_audio, n);

                if (_dcsExpected != null)
                {
                    open &= _dcs.Synced &&
                            (_dcsExpected.Value <= 0 ||
                             Radiocity.Dsp.Dcs.SameCode(_dcs.Code, _dcsExpected.Value));
                }
            }

            int a = _bandPassHi.Process(_audio, n, _narrowBuf);
            int b = _bandPassLo.Process(_narrowBuf, a, _audio);

            if (_ctcssExpected == null && _dcsExpected == null) return;

            float target = open ? 1.0f : 0.0f;

            for (int k = 0; k < b; k++)
            {
                float slew = (target > _ctcssGate) ? _ctcssGateAttack : _ctcssGateRelease;
                _ctcssGate += slew * (target - _ctcssGate);
                _audio[k] *= _ctcssGate;
            }
        }

        private int Demodulate(float[] srcI, float[] srcQ, int n, float[] output)
        {
            // The blanker runs first: an impulse that reaches the channel filter
            // has already been smeared across its whole impulse response, and by
            // then it cannot be removed without removing the signal too.
            if (_blanker != null) _blanker.Process(srcI, srcQ, n);

            int m = _chain.Process(srcI, srcQ, n, _bi, _bq);

            // Channel power is measured here, where it still means "is there a
            // signal", and the squelch gates the audio after the AGC, where it
            // does not. The meter reads the same number, so it works with the
            // squelch switched off.
            double sum = 0.0;
            for (int k = 0; k < m; k++)
                sum += _bi[k] * (double)_bi[k] + _bq[k] * (double)_bq[k];

            float meanPower = (float)(sum / Math.Max(1, m));
            _squelch.Update(meanPower, m);
            _levelDb = 10.0f * (float)Math.Log10(Math.Max(meanPower, 1e-20f));

            switch (_mode)
            {
                case DemodMode.Am: _am.Process(_bi, _bq, m, _audio); break;
                case DemodMode.Fm:
                case DemodMode.Nfm: _fm.Process(_bi, _bq, m, _audio); break;
                default: _ssb.Process(_bi, _bq, m, _audio); break;
            }

            // Narrowband FM shapes the discriminator output into speech: CTCSS is
            // measured first, because the band-pass that follows is designed to
            // remove exactly the band it lives in.
            if (_mode == DemodMode.Nfm) ShapeNfmAudio(m);

            // RDS reads the composite, so it has to run before the stereo decoder
            // turns that composite into audio.
            if (_rds != null) _rds.Process(_audio, m);

            if (_stereo != null)
            {
                // The composite sits in _audio; the decoder turns it into
                // interleaved L/R at the audio rate.
                int frames = _stereo.Process(_audio, m, _stereoOut);

                if (_agcEnabled) _stereoAgc.Process(_stereoOut, frames);
                _squelch.Apply(_stereoOut, frames * 2);

                Array.Copy(_stereoOut, 0, output, 0, frames * 2);
                return frames;
            }

            if (_audioDecim != null)
            {
                int d = _audioDecim.Process(_audio, m, _audioDecimOut);
                if (_agcEnabled) _agc.Process(_audioDecimOut, d);
                _squelch.Apply(_audioDecimOut, d);

                Array.Copy(_audioDecimOut, 0, output, 0, d);
                return d;
            }

            if (_agcEnabled) _agc.Process(_audio, m);
            _squelch.Apply(_audio, m);

            Array.Copy(_audio, 0, output, 0, m);
            return m;
        }
    }
}
