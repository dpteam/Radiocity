using System;

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
            o.ChannelHz = 3000.0;
            return o;
        }

        public static ReceiverOptions Cw(double pitchHz, int decimation)
        {
            ReceiverOptions o = new ReceiverOptions();
            o.Mode = DemodMode.Cw;
            o.Decimation = decimation;
            o.ChannelHz = 1200.0;
            o.BfoHz = pitchHz;
            return o;
        }
    }

    /// <summary>
    /// The whole receive chain in one place: tune, mix, channel-filter, decimate,
    /// demodulate, AGC. Everything is preallocated; Process() performs zero heap
    /// allocations so it can run on the audio callback thread without waking the GC.
    /// </summary>
    public sealed class Receiver
    {
        private readonly Nco _nco;
        private readonly ComplexDecimator _decim;
        private readonly AmDemod _am;
        private readonly FmDemod _fm;
        private readonly SsbDemod _ssb;
        private readonly Agc _agc;
        private readonly DemodMode _mode;
        private readonly bool _agcEnabled;

        private readonly float[] _i;
        private readonly float[] _q;
        private readonly float[] _bi;
        private readonly float[] _bq;
        private readonly float[] _audio;

        public Receiver(double sampleRate, double tuneHz, ReceiverOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");

            _mode = options.Mode;
            SampleRate = sampleRate;
            OutputRate = (int)(sampleRate / options.Decimation);

            _nco = new Nco();
            _nco.SetFrequency(tuneHz, sampleRate);

            _decim = new ComplexDecimator(
                DesignChannelFilter(sampleRate, OutputRate, options.ChannelHz, options.Decimation),
                options.Decimation);

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
        public int Taps { get { return _decim.Taps; } }
        public DemodMode Mode { get { return _mode; } }

        public void SetTune(double hz)
        {
            _nco.SetFrequency(hz, SampleRate);
        }

        /// <summary>Returns the number of audio samples written to output.</summary>
        public int Process(float[] input, int n, float[] output)
        {
            if (n > _i.Length) throw new ArgumentOutOfRangeException("n");

            _nco.MixReal(input, _i, _q, n);
            int m = _decim.Process(_i, _q, n, _bi, _bq);

            switch (_mode)
            {
                case DemodMode.Am: _am.Process(_bi, _bq, m, _audio); break;
                case DemodMode.Fm: _fm.Process(_bi, _bq, m, _audio); break;
                default: _ssb.Process(_bi, _bq, m, _audio); break;
            }

            if (_agcEnabled) _agc.Process(_audio, m);

            Array.Copy(_audio, 0, output, 0, m);
            return m;
        }

        /// <summary>
        /// Sizes the anti-alias filter from the actual requirement instead of a
        /// guessed tap count. Passband ends at half the channel width, stopband
        /// starts at the new Nyquist; the Kaiser estimate then tells us how many
        /// taps it takes to get 70 dB of rejection across that gap.
        /// </summary>
        private static float[] DesignChannelFilter(double sampleRate, int outputRate, double channelHz, int decimation)
        {
            double pass = Math.Min(channelHz * 0.5, outputRate * 0.45) / sampleRate;
            double stop = 0.5 / decimation;

            // No room for a transition band (wide FM at a high output rate):
            // fall back to a gentle filter, the sound card itself does the rest.
            if (stop - pass < 0.002)
                return FirDesign.LowPass(0.45 / decimation, 63, 8.6);

            double deltaF = stop - pass;
            int taps = (int)Math.Ceiling(62.0 / (2.285 * 2.0 * Math.PI * deltaF));
            if (taps < 15) taps = 15;
            if (taps > 1023) taps = 1023;

            double cutoff = (pass + stop) * 0.5;
            return FirDesign.LowPass(cutoff, taps, 8.6);
        }
    }
}
