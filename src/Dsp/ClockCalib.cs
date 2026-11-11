using System;

namespace Radiocity.Dsp
{
    /// <summary>
    /// Measures how far the sound card's sample clock is from its nominal rate,
    /// by finding a carrier whose frequency you already know.
    ///
    /// Why this matters: a "192 kHz" codec is typically off by 20-50 ppm, which
    /// is 4-10 Hz at 192 kHz. That sounds like nothing until you tune a 200 Hz
    /// wide signal and watch the waterfall drift by a kilohertz per minute.
    ///
    /// The measurement is a coarse FFT peak, refined by parabolic interpolation
    /// on the log magnitudes, then a fine Goertzel sweep. Doing it in one step
    /// with an FFT alone gets you +-4 ppm; the refinement gets you under 1 ppm.
    /// </summary>
    public static class ClockCalib
    {
        public sealed class Result
        {
            public double MeasuredHz;
            public double ExpectedHz;
            public double Ppm;
            public double NominalRate;
            public double CorrectedRate;

            public override string ToString()
            {
                return "measured " + MeasuredHz.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                       " Hz against " + ExpectedHz.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                       " Hz -> " + Ppm.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " ppm";
            }
        }

        public static Result Measure(float[] buf, int n, double nominalRate, double expectedHz)
        {
            int size = 8192;
            while (size * 2 <= n && size < 131072) size <<= 1;
            if (size > n) size = 4096;
            if (n < 4096) throw new ArgumentException("need at least 4096 samples");

            float[] re = new float[size];
            float[] im = new float[size];
            float[] win = Fft.Hann(size);

            int offset = n - size;
            for (int i = 0; i < size; i++)
            {
                re[i] = buf[offset + i] * win[i];
                im[i] = 0.0f;
            }

            Fft.Forward(re, im);

            double binHz = nominalRate / size;
            int lo = Math.Max(1, (int)(expectedHz * (1.0 - 500e-6) / binHz));
            int hi = Math.Min(size / 2 - 2, (int)(expectedHz * (1.0 + 500e-6) / binHz) + 1);
            if (hi <= lo) throw new ArgumentException("expected frequency outside the analysed band");

            int best = lo;
            double bestMag = Magnitude(re, im, lo);
            for (int i = lo + 1; i <= hi; i++)
            {
                double m = Magnitude(re, im, i);
                if (m > bestMag) { bestMag = m; best = i; }
            }

            // Parabolic interpolation in the log domain: three points are enough
            // because a Hann-windowed peak is very nearly quadratic near its top.
            double y0 = Math.Log(Magnitude(re, im, best - 1) + 1e-30);
            double y1 = Math.Log(Magnitude(re, im, best) + 1e-30);
            double y2 = Math.Log(Magnitude(re, im, best + 1) + 1e-30);
            double denom = y0 - 2.0 * y1 + y2;
            double delta = (Math.Abs(denom) > 1e-12) ? 0.5 * (y0 - y2) / denom : 0.0;
            if (delta > 0.5) delta = 0.5;
            if (delta < -0.5) delta = -0.5;

            double coarse = (best + delta) * binHz;

            // Fine sweep around the coarse estimate. 0.02 Hz steps over +-1.5 bins
            // is a few hundred Goertzels, which costs milliseconds.
            double span = binHz * 1.5;
            double stepHz = 0.02;
            double fine = coarse;
            double fineMag = 0.0;

            for (double f = coarse - span; f <= coarse + span; f += stepHz)
            {
                double m = Fft.Goertzel(buf, offset, size, f, nominalRate);
                if (m > fineMag) { fineMag = m; fine = f; }
            }

            Result r = new Result();
            r.ExpectedHz = expectedHz;
            r.MeasuredHz = fine;
            r.NominalRate = nominalRate;
            r.Ppm = (fine / expectedHz - 1.0) * 1e6;
            r.CorrectedRate = nominalRate * (1.0 + r.Ppm * 1e-6);
            return r;
        }

        private static double Magnitude(float[] re, float[] im, int bin)
        {
            return Math.Sqrt(re[bin] * (double)re[bin] + im[bin] * (double)im[bin]);
        }
    }
}
