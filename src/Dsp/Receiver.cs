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
        Cw
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
        private readonly DemodMode _mode;
        private readonly bool _agcEnabled;

        private readonly float[] _i;
        private readonly float[] _q;
        private readonly float[] _bi;
        private readonly float[] _bq;
        private readonly float[] _audio;

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
            OutputRate = (int)(sampleRate / options.Decimation);

            _nco = new Nco();
            _nco.SetFrequency(tuneHz, sampleRate);

            _chain = new DecimationChain(sampleRate, OutputRate, options.ChannelHz, 32768);
            OutputRate = _chain.OutputRate;

            switch (_mode)
            {
                case DemodMode.Am:
                    _am = new AmDemod(OutputRate, 20.0);
                    break;
                case DemodMode.Fm:
                    _fm = new FmDemod(OutputRate, options.DeviationHz, options.DeemphasisUs);
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
            }

            // SSB and CW have no carrier to lock onto, so the AGC must be slower:
            // a fast release on speech pumps the noise floor between syllables.
            bool voice = _mode == DemodMode.Usb || _mode == DemodMode.Lsb || _mode == DemodMode.Cw;
            _agc = voice ? new Agc(OutputRate, 40.0, 600.0, 0.30) : new Agc(OutputRate, 25.0, 400.0, 0.30);
            _agcEnabled = options.AgcEnabled;

            _squelch = new Squelch(OutputRate);
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
                _blanker = new NoiseBlanker(OutputRate);
                _blanker.Threshold = options.BlankerThreshold.Value;
            }

            int block = 32768;
            _i = new float[block];
            _q = new float[block];
            _bi = new float[block];
            _bq = new float[block];
            _audio = new float[block];
        }

        public double SampleRate { get; private set; }
        public int OutputRate { get; private set; }
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
        /// Demodulates an already-mixed complex baseband, skipping the NCO.
        /// This is what makes recorded I/Q replayable: the recording is the
        /// output of the mixer, so every demodulator can be tried against the
        /// same capture without going back to the air.
        /// </summary>
        public int ProcessBaseband(float[] i, float[] q, int n, float[] output)
        {
            if (n > _bi.Length) throw new ArgumentOutOfRangeException("n");
            return Demodulate(i, q, n, output);
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
                case DemodMode.Fm: _fm.Process(_bi, _bq, m, _audio); break;
                default: _ssb.Process(_bi, _bq, m, _audio); break;
            }

            if (_agcEnabled) _agc.Process(_audio, m);
            _squelch.Apply(_audio, m);

            Array.Copy(_audio, 0, output, 0, m);
            return m;
        }
    }
}
