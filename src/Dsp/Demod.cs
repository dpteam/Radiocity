using System;

namespace Radiocity.Dsp
{
    /// <summary>
    /// Envelope detector with a DC blocker. For AM this is the whole demodulator;
    /// for SSB/CW you would replace it with a product detector plus a phasing filter.
    /// </summary>
    public sealed class AmDemod
    {
        private float _prevIn;
        private float _prevOut;
        private readonly float _r;

        /// <param name="sampleRate">Sample rate of the *baseband* stream.</param>
        /// <param name="dcCutoffHz">Corner of the DC blocker (a few Hz is fine).</param>
        public AmDemod(double sampleRate, double dcCutoffHz)
        {
            _r = (float)Math.Exp(-2.0 * Math.PI * dcCutoffHz / sampleRate);
        }

        public void Process(float[] i, float[] q, int n, float[] audio)
        {
            for (int k = 0; k < n; k++)
            {
                float env = (float)Math.Sqrt(i[k] * i[k] + q[k] * q[k]);

                // One-pole high-pass: removes the carrier term and any DC offset
                // the sound card's ADC left behind.
                float y = env - _prevIn + _r * _prevOut;
                _prevIn = env;
                _prevOut = y;

                audio[k] = y;
            }
        }

        public void Reset()
        {
            _prevIn = 0.0f;
            _prevOut = 0.0f;
        }
    }

    /// <summary>
    /// RMS AGC with independent attack and release, in the dB domain.
    ///
    /// Two details that are easy to get wrong:
    ///  1. The level detector must be *symmetric*. Running a fast attack against a
    ///     slow release on an RMS detector turns it into a peak detector (it
    ///     chases the crest of the waveform, not its mean power), which reads
    ///     about 3 dB high on a sine and drags the whole gain curve with it.
    ///     Asymmetry belongs on the gain slew, not on the detector.
    ///  2. The detector window must span many audio periods. Anything shorter
    ///     than a few milliseconds modulates the gain at 2x the tone frequency
    ///     and shows up as distortion, not as AGC.
    /// </summary>
    public sealed class Agc
    {
        private readonly float _detector;
        private readonly float _attack;
        private readonly float _release;
        private readonly float _targetDb;
        private float _gainDb;
        private float _ms;

        /// <param name="targetPeak">Desired peak level, e.g. 0.3 for -10 dBFS.</param>
        public Agc(double sampleRate, double attackMs, double releaseMs, double targetPeak)
        {
            _detector = (float)(1.0 - Math.Exp(-1.0 / (sampleRate * 0.030)));

            // Gain slew: fast when we must duck, slow when we can open up.
            _attack = (float)(1.0 - Math.Exp(-1.0 / (sampleRate * attackMs / 1000.0)));
            _release = (float)(1.0 - Math.Exp(-1.0 / (sampleRate * releaseMs / 1000.0)));

            // The AGC works on RMS; the caller thinks in peak. Sine crest factor 1.41.
            double targetRms = targetPeak / 1.41421356;
            _targetDb = (float)(20.0 * Math.Log10(targetRms));
            _gainDb = 0.0f;
            _ms = 0.0f;
        }

        public float GainDb { get { return _gainDb; } }

        public void Process(float[] buf, int n)
        {
            for (int k = 0; k < n; k++)
            {
                float x = buf[k];
                float sq = x * x;

                _ms += _detector * (sq - _ms);

                float levelDb = 10.0f * (float)Math.Log10(Math.Max(_ms, 1e-12f));
                float want = _targetDb - levelDb;

                float slew = (want < _gainDb) ? _attack : _release;
                _gainDb += slew * (want - _gainDb);

                if (_gainDb > 60.0f) _gainDb = 60.0f;
                if (_gainDb < -20.0f) _gainDb = -20.0f;

                buf[k] = x * (float)Math.Pow(10.0, _gainDb / 20.0);
            }
        }

        public void Reset()
        {
            _gainDb = 0.0f;
            _ms = 0.0f;
        }
    }

    /// <summary>
    /// Stateful Catmull-Rom resampler for the last step before the sound card,
    /// which accepts a handful of rates while our channel rate is whatever the
    /// decimation produced (12 kHz, 7.5 kHz, ...).
    ///
    /// Why cubic and not linear: linear interpolation is a sinc^2 filter, so it
    /// droops by 2.3% at 1 kHz and 3.3 dB at 5 kHz when going 12 -> 48 kHz. That
    /// is exactly the range where speech intelligibility lives, and it sounds
    /// dull. Catmull-Rom is flat to within 0.05 dB across the audio band for the
    /// price of two extra multiplies.
    ///
    /// The awkward part is the block boundary: the last two samples of a block
    /// are the left neighbours of the first two of the next, so they are carried
    /// over or every block edge becomes a click.
    /// </summary>
    public sealed class InterpolatingResampler
    {
        private readonly double _ratio;
        private double _pos;
        private float _h1;
        private float _h2;
        private bool _haveHistory;

        public InterpolatingResampler(double inRate, double outRate)
        {
            if (inRate <= 0.0 || outRate <= 0.0) throw new ArgumentOutOfRangeException("rate");
            _ratio = inRate / outRate;
        }

        /// <summary>
        /// Consumes src[0..n-1] and writes up to maxOut samples. Returns how many
        /// were written. Positions straddle blocks, hence the carried history.
        /// </summary>
        public int Process(float[] src, int n, float[] dst, int maxOut)
        {
            if (n < 3) return 0;

            int o = 0;
            while (o < maxOut)
            {
                int i = (int)Math.Floor(_pos);
                if (i > n - 3) break;

                double t = _pos - i;

                float p0 = At(src, i - 1);
                float p1 = At(src, i);
                float p2 = At(src, i + 1);
                float p3 = At(src, i + 2);

                dst[o++] = Cubic(p0, p1, p2, p3, t);
                _pos += _ratio;
            }

            _pos -= n;
            _h2 = src[n - 2];
            _h1 = src[n - 1];
            _haveHistory = true;
            return o;
        }

        public void Reset()
        {
            _pos = 0.0;
            _h1 = 0.0f;
            _h2 = 0.0f;
            _haveHistory = false;
        }

        private float At(float[] src, int index)
        {
            if (index >= 0) return src[index];
            if (!_haveHistory) return src[0];
            return index == -1 ? _h1 : _h2;
        }

        private static float Cubic(float p0, float p1, float p2, float p3, double t)
        {
            double a = -0.5 * p0 + 1.5 * p1 - 1.5 * p2 + 0.5 * p3;
            double b = p0 - 2.5 * p1 + 2.0 * p2 - 0.5 * p3;
            double c = -0.5 * p0 + 0.5 * p2;

            return (float)(((a * t + b) * t + c) * t + p1);
        }
    }
}
