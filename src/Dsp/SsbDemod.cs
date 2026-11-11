using System;

namespace Radiocity.Dsp
{
    /// <summary>
    /// 90-degree phase splitter (Hilbert transformer) plus the matching delay
    /// line for the in-phase path.
    ///
    /// This is the piece that makes SSB work: given a complex baseband z = I + jQ
    /// containing both sidebands, the analytic signal is (z + j*H(z)), so keeping
    /// only the positive frequencies is Re((z + j*H(z))/2) = (I - H(Q))/2.
    /// For the negative side it is (I + H(Q))/2. One Hilbert filter, no phasing
    /// network of op-amps, and the rejection is set purely by tap count.
    ///
    /// Odd tap count required: the response is Type III, so every even tap is
    /// zero and the centre tap is zero, which is exactly a 90 degree rotation.
    /// </summary>
    public sealed class Hilbert90
    {
        private readonly float[] _h;
        private readonly float[] _histI;
        private readonly float[] _histQ;
        private readonly int _n;
        private readonly int _delay;
        private int _pos;

        public Hilbert90(int taps)
        {
            if (taps < 7) taps = 7;
            if ((taps & 1) == 0) taps++;
            if (taps > 1023) taps = 1023;

            _n = taps;
            _delay = (taps - 1) / 2;
            _h = Design(taps);
            _histI = new float[taps];
            _histQ = new float[taps];
        }

        public int Delay { get { return _delay; } }
        public int Taps { get { return _n; } }

        /// <summary>
        /// Writes outQ = Hilbert(q) and outI = i delayed by the filter's group
        /// delay, so the two outputs stay time-aligned.
        /// </summary>
        public void Process(float[] i, float[] q, int n, float[] outI, float[] outQ)
        {
            for (int k = 0; k < n; k++)
            {
                _histI[_pos] = i[k];
                _histQ[_pos] = q[k];

                float acc = 0.0f;
                int idx = _pos;
                for (int t = 0; t < _n; t++)
                {
                    acc += _h[t] * _histQ[idx];
                    if (--idx < 0) idx = _n - 1;
                }
                outQ[k] = acc;

                int di = _pos - _delay;
                if (di < 0) di += _n;
                outI[k] = _histI[di];

                if (++_pos == _n) _pos = 0;
            }
        }

        public void Reset()
        {
            Array.Clear(_histI, 0, _histI.Length);
            Array.Clear(_histQ, 0, _histQ.Length);
            _pos = 0;
        }

        private static float[] Design(int taps)
        {
            float[] h = new float[taps];
            int delay = (taps - 1) / 2;
            double i0 = FirDesign.BesselI0(8.6);

            for (int k = 0; k < taps; k++)
            {
                int m = k - delay;

                double v;
                if ((m & 1) == 0) v = 0.0;          // even offsets and the centre tap
                else v = 2.0 / (Math.PI * m);

                // Kaiser window, same beta as the channel filters: about -70 dB.
                double r = 2.0 * k / (taps - 1) - 1.0;
                double w = FirDesign.BesselI0(8.6 * Math.Sqrt(Math.Max(0.0, 1.0 - r * r))) / i0;

                h[k] = (float)(v * w);
            }

            return h;
        }
    }

    /// <summary>
    /// Phasing-method SSB demodulator. Optional BFO shift first, so the same code
    /// serves CW (carrier at DC, shifted up to an audible pitch) and voice SSB
    /// (no shift at all).
    /// </summary>
    public sealed class SsbDemod
    {
        private readonly Hilbert90 _hilbert;
        private readonly Nco _bfo;
        private readonly bool _upper;
        private readonly float[] _si;
        private readonly float[] _sq;
        private readonly float[] _ai;
        private readonly float[] _aq;

        public SsbDemod(int taps, int blockSize, double rate, double bfoHz, bool upperSideband)
        {
            _hilbert = new Hilbert90(taps);
            _upper = upperSideband;

            if (bfoHz != 0.0)
            {
                _bfo = new Nco();
                _bfo.SetFrequency(bfoHz, rate);
            }

            _si = new float[blockSize];
            _sq = new float[blockSize];
            _ai = new float[blockSize];
            _aq = new float[blockSize];
        }

        public int Delay { get { return _hilbert.Delay; } }

        public void Process(float[] i, float[] q, int n, float[] audio)
        {
            if (n > _si.Length) throw new ArgumentOutOfRangeException("n");

            float[] bi = i;
            float[] bq = q;

            if (_bfo != null)
            {
                _bfo.MixComplexUp(i, q, _si, _sq, n);
                bi = _si;
                bq = _sq;
            }

            _hilbert.Process(bi, bq, n, _ai, _aq);

            if (_upper)
            {
                for (int k = 0; k < n; k++) audio[k] = _ai[k] - _aq[k];
            }
            else
            {
                for (int k = 0; k < n; k++) audio[k] = _ai[k] + _aq[k];
            }
        }

        public void Reset()
        {
            _hilbert.Reset();
        }
    }
}
