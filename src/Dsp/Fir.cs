using System;

namespace Radiocity.Dsp
{
    /// <summary>Windowed-sinc FIR design (Kaiser window) plus a decimating complex FIR.</summary>
    public static class FirDesign
    {
        /// <summary>
        /// Low-pass design. cutoff is in cycles/sample (0..0.5). taps should be odd.
        /// beta ~ 8.6 gives about -70 dB stopband, beta ~ 5.0 about -50 dB.
        /// </summary>
        public static float[] LowPass(double cutoff, int taps, double beta)
        {
            if (taps < 3) throw new ArgumentOutOfRangeException("taps");
            if (taps % 2 == 0) taps++;
            if (cutoff <= 0.0 || cutoff >= 0.5) throw new ArgumentOutOfRangeException("cutoff");

            float[] h = new float[taps];
            int m = taps - 1;
            double i0Beta = BesselI0(beta);
            double sum = 0.0;

            for (int n = 0; n < taps; n++)
            {
                double x = n - m / 2.0;
                double sinc = (x == 0.0) ? 2.0 * cutoff : Math.Sin(2.0 * Math.PI * cutoff * x) / (Math.PI * x);
                double r = 2.0 * n / m - 1.0;
                double w = BesselI0(beta * Math.Sqrt(Math.Max(0.0, 1.0 - r * r))) / i0Beta;
                double v = sinc * w;
                h[n] = (float)v;
                sum += v;
            }

            // Unity DC gain, so decimation does not change the signal level.
            if (sum != 0.0)
            {
                float k = (float)(1.0 / sum);
                for (int n = 0; n < taps; n++) h[n] *= k;
            }

            return h;
        }

        public static double BesselI0(double x)
        {
            double sum = 1.0;
            double term = 1.0;
            double y = x * x * 0.25;

            for (int k = 1; k < 64; k++)
            {
                term *= y / ((double)k * k);
                sum += term;
                if (term < 1e-16 * sum) break;
            }

            return sum;
        }
    }

    /// <summary>
    /// Complex FIR followed by decimation by an integer factor: the classic
    /// channel filter of a sound-card SDR. Cost is taps multiply-adds per output
    /// sample, so keep taps proportional to the decimation factor.
    /// </summary>
    public sealed class ComplexDecimator
    {
        private readonly float[] _h;
        private readonly int _taps;
        private readonly int _decim;
        private readonly float[] _histI;
        private readonly float[] _histQ;
        private int _pos;
        private int _cnt;

        public ComplexDecimator(float[] taps, int decimation)
        {
            if (taps == null || taps.Length < 3) throw new ArgumentException("taps");
            if (decimation < 1) throw new ArgumentOutOfRangeException("decimation");

            _h = taps;
            _taps = taps.Length;
            _decim = decimation;
            _histI = new float[_taps];
            _histQ = new float[_taps];
        }

        public int Decimation { get { return _decim; } }
        public int Taps { get { return _taps; } }

        /// <summary>Maximum output samples Process can emit for n input samples.</summary>
        public int MaxOut(int n)
        {
            return n / _decim + 2;
        }

        public int Process(float[] srcI, float[] srcQ, int n, float[] outI, float[] outQ)
        {
            int o = 0;
            for (int s = 0; s < n; s++)
            {
                _histI[_pos] = srcI[s];
                _histQ[_pos] = srcQ[s];

                _pos++;
                if (_pos == _taps) _pos = 0;

                if (++_cnt >= _decim)
                {
                    _cnt = 0;
                    outI[o] = Dot(_histI);
                    outQ[o] = Dot(_histQ);
                    o++;
                }
            }
            return o;
        }

        public void Reset()
        {
            Array.Clear(_histI, 0, _histI.Length);
            Array.Clear(_histQ, 0, _histQ.Length);
            _pos = 0;
            _cnt = 0;
        }

        private float Dot(float[] hist)
        {
            float acc = 0.0f;
            int idx = _pos - 1;
            if (idx < 0) idx += _taps;

            for (int k = 0; k < _taps; k++)
            {
                acc += _h[k] * hist[idx];
                if (--idx < 0) idx = _taps - 1;
            }
            return acc;
        }
    }
}
