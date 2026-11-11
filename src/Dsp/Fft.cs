using System;

namespace Radiocity.Dsp
{
    /// <summary>In-place iterative radix-2 FFT. Length must be a power of two.</summary>
    public static class Fft
    {
        public static void Forward(float[] re, float[] im)
        {
            int n = re.Length;
            if (n != im.Length) throw new ArgumentException("re/im length mismatch");
            if (n < 2 || (n & (n - 1)) != 0) throw new ArgumentException("length must be a power of two");

            int j = 0;
            for (int i = 1; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;

                if (i < j)
                {
                    float t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                int half = len >> 1;
                double ang = -2.0 * Math.PI / len;
                float wr = (float)Math.Cos(ang);
                float wi = (float)Math.Sin(ang);

                for (int i = 0; i < n; i += len)
                {
                    float cr = 1.0f, ci = 0.0f;
                    for (int k = 0; k < half; k++)
                    {
                        int a = i + k;
                        int b = a + half;

                        float xr = re[b] * cr - im[b] * ci;
                        float xi = re[b] * ci + im[b] * cr;

                        re[b] = re[a] - xr;
                        im[b] = im[a] - xi;
                        re[a] += xr;
                        im[a] += xi;

                        float nr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = nr;
                    }
                }
            }
        }

        /// <summary>Hann window coefficients, precomputed once per length.</summary>
        public static float[] Hann(int n)
        {
            float[] w = new float[n];
            for (int i = 0; i < n; i++)
                w[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (n - 1)));
            return w;
        }

        /// <summary>
        /// Single-bin DFT by the Goertzel algorithm. Cheaper than a full FFT when
        /// you only need one frequency (used by the self-test and by SRO tracking).
        /// </summary>
        public static double Goertzel(float[] x, int offset, int count, double freq, double sampleRate)
        {
            double w = 2.0 * Math.PI * freq / sampleRate;
            double coeff = 2.0 * Math.Cos(w);
            double s0 = 0.0, s1 = 0.0, s2 = 0.0;

            for (int i = 0; i < count; i++)
            {
                s0 = x[offset + i] + coeff * s1 - s2;
                s2 = s1;
                s1 = s0;
            }

            double real = s1 - s2 * Math.Cos(w);
            double imag = s2 * Math.Sin(w);
            return Math.Sqrt(real * real + imag * imag) * 2.0 / count;
        }
    }
}
