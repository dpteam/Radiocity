using System;

namespace Radiocity.Dsp
{
    /// <summary>
    /// The standard EIA CTCSS tones, in Hz.
    ///
    /// CTCSS ("Continuous Tone-Coded Squelch System", or "PL" to radio amateurs)
    /// is a sub-audible tone a repeater or a radio transmits alongside the voice.
    /// The receiver keeps its audio muted until it hears *its* tone, so several
    /// users can share a channel without hearing each other. The tones sit
    /// between 67 and 254 Hz, below the 300 Hz where speech starts, which is why
    /// they are inaudible.
    ///
    /// The spacing is deliberately uneven: 67.0 and 69.3 are only 2.3 Hz apart,
    /// while the upper tones are 8 Hz apart. That unevenness is what makes the
    /// detector's frequency resolution matter.
    /// </summary>
    public static class Ctcss
    {
        public static readonly double[] Tones =
        {
            67.0, 69.3, 71.9, 74.4, 77.0, 79.7, 82.5, 85.4, 88.5, 91.5,
            94.8, 97.4, 100.0, 103.5, 107.2, 110.9, 114.8, 118.8, 123.0, 127.3,
            131.8, 136.5, 141.3, 146.2, 151.4, 156.7, 159.8, 162.2, 165.5, 167.9,
            171.3, 173.8, 177.3, 179.9, 183.5, 186.2, 189.9, 192.8, 196.6, 199.5,
            203.5, 206.5, 210.7, 218.1, 225.7, 229.1, 233.6, 241.8, 250.3, 254.1
        };

        /// <summary>Nearest standard tone, or 0 when nothing is close enough.</summary>
        public static double Nearest(double hz)
        {
            double best = 0.0;
            double bestDist = double.MaxValue;

            for (int i = 0; i < Tones.Length; i++)
            {
                double d = Math.Abs(Tones[i] - hz);
                if (d < bestDist) { bestDist = d; best = Tones[i]; }
            }

            return bestDist <= 1.5 ? best : 0.0;
        }
    }

    /// <summary>
    /// CTCSS tone detector and tone squelch.
    ///
    /// The hard part is not finding the tone, it is not finding one that is not
    /// there. Speech starts at 300 Hz and the highest CTCSS tone is 254 Hz, so
    /// there are 46 Hz between the thing you want and the thing you must reject.
    /// Two things make that separation workable:
    ///
    ///  - A steep low-pass before analysis, applied in two decimating stages so
    ///    it stays cheap.
    ///  - A one-second analysis window, which gives 1 Hz resolution. A broadband
    ///    voice signal spreads its energy across hundreds of bins; a steady tone
    ///    puts all of it in one.
    ///
    /// The detector also requires the winner to beat the runner-up by a margin,
    /// because "some bin is the largest" is always true, even in silence.
    /// </summary>
    public sealed class CtcssDecoder
    {
        private readonly RealDecimator[] _stages;
        private readonly float[][] _stageBuf;
        private readonly float[] _filtered;
        private readonly float[] _window;
        private int _fill;

        private readonly double[] _magnitudes = new double[Ctcss.Tones.Length];

        /// <summary>Tone amplitude below which nothing is reported.</summary>
        public double Threshold = 0.02;

        /// <summary>How much the winner must beat the runner-up, as a ratio.</summary>
        public double Dominance = 2.0;

        /// <summary>Detected tone in Hz, or 0 when none.</summary>
        public double ToneHz { get; private set; }

        /// <summary>Amplitude of the detected tone, in discriminator units.</summary>
        public double ToneLevel { get; private set; }

        /// <summary>True when a tone is present and dominant.</summary>
        public bool TonePresent { get { return ToneHz > 0.0; } }

        /// <summary>Fires whenever the detected tone changes. 0 means "none".</summary>
        public event Action<double> ToneChanged;

        public CtcssDecoder(double rate)
        {
            if (rate < 4000)
                throw new NotSupportedException("CTCSS needs at least 4 kHz, got " + rate);

            // 16k -> 4k -> 1k. Two cheap stages beat one expensive one, and the
            // second stage runs at a quarter of the rate.
            int target = 1000;
            int total = Math.Max(1, (int)Math.Round(rate / target));
            int[] factors = SplitDecimation(total);

            _stages = new RealDecimator[factors.Length];
            _stageBuf = new float[factors.Length][];

            double stageRate = rate;
            int cum = 1;

            for (int s = 0; s < factors.Length; s++)
            {
                int d = factors[s];
                cum *= d;

                // Pass 260 Hz, stop 500 Hz: the tone band, with the speech band
                // starting just above it.
                double pass = 260.0 / stageRate;
                double stop = 500.0 / stageRate;
                if (stop > 0.45) stop = 0.45;

                int taps = (int)Math.Ceiling(62.0 / (2.285 * 2.0 * Math.PI * Math.Max(0.002, stop - pass)));
                if (taps < 31) taps = 31;
                if (taps > 1023) taps = 1023;

                _stages[s] = new RealDecimator(FirDesign.LowPass((pass + stop) * 0.5, taps, 8.6), d);
                _stageBuf[s] = new float[16384];

                stageRate = rate / cum;
            }

            Rate = (int)Math.Round(rate / cum);

            // One second: 1 Hz resolution, which is what separates 67.0 from 69.3.
            _window = new float[Rate];
            _filtered = new float[16384];
        }

        public int Rate { get; private set; }

        public void Process(float[] audio, int n)
        {
            int pos = 0;

            while (pos < n)
            {
                int chunk = Math.Min(n - pos, _filtered.Length);
                Filter(audio, pos, chunk);
                pos += chunk;
            }
        }

        private void Filter(float[] audio, int offset, int n)
        {
            float[] cur = audio;
            int count = n;
            int curOffset = offset;

            // The first stage reads straight from the caller's buffer, the rest
            // from their own scratch space.
            for (int s = 0; s < _stages.Length; s++)
            {
                float[] src;

                if (s == 0)
                {
                    src = new float[count];
                    Array.Copy(audio, offset, src, 0, count);
                }
                else
                {
                    src = _stageBuf[s - 1];
                }

                float[] dst = _stageBuf[s];
                int produced = _stages[s].Process(src, count, dst);

                cur = dst;
                count = produced;
                curOffset = 0;
            }

            if (curOffset != 0 || count <= 0) return;

            for (int k = 0; k < count; k++)
            {
                _window[_fill++] = cur[k];

                if (_fill >= _window.Length)
                {
                    _fill = 0;
                    Analyse();
                }
            }
        }

        private void Analyse()
        {
            double best = 0.0;
            double second = 0.0;
            int bestIndex = -1;

            for (int t = 0; t < Ctcss.Tones.Length; t++)
            {
                double m = Goertzel(Ctcss.Tones[t]);
                _magnitudes[t] = m;

                if (m > best) { second = best; best = m; bestIndex = t; }
                else if (m > second) second = m;
            }

            if (bestIndex < 0) return;

            bool present = best >= Threshold && best >= second * Dominance;
            double tone = present ? Ctcss.Tones[bestIndex] : 0.0;

            ToneLevel = best;

            if (tone != ToneHz)
            {
                ToneHz = tone;

                Action<double> handler = ToneChanged;
                if (handler != null) handler(tone);
            }
        }

        private double Goertzel(double hz)
        {
            double w = 2.0 * Math.PI * hz / Rate;
            double coeff = 2.0 * Math.Cos(w);
            double s1 = 0.0;
            double s2 = 0.0;

            for (int i = 0; i < _window.Length; i++)
            {
                double s0 = _window[i] + coeff * s1 - s2;
                s2 = s1;
                s1 = s0;
            }

            double re = s1 - s2 * Math.Cos(w);
            double im = s2 * Math.Sin(w);

            return 2.0 * Math.Sqrt(re * re + im * im) / _window.Length;
        }

        public void Reset()
        {
            for (int s = 0; s < _stages.Length; s++)
            {
                _stages[s].Reset();
                Array.Clear(_stageBuf[s], 0, _stageBuf[s].Length);
            }

            Array.Clear(_window, 0, _window.Length);
            _fill = 0;
            ToneHz = 0.0;
            ToneLevel = 0.0;
        }

        internal static int[] SplitDecimation(int total)
        {
            if (total <= 4) return new int[] { total };

            System.Collections.Generic.List<int> parts = new System.Collections.Generic.List<int>();
            int remaining = total;

            while (remaining > 4)
            {
                int f = 4;
                while (f > 2 && (remaining % f) != 0) f--;
                if ((remaining % f) != 0) f = 2;

                parts.Add(f);
                remaining /= f;
            }

            if (remaining > 1) parts.Add(remaining);
            return parts.ToArray();
        }
    }
}
