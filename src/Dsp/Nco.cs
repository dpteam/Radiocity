using System;

namespace Radiocity.Dsp
{
    /// <summary>
    /// Numerically controlled oscillator: table lookup with linear interpolation.
    /// Math.Sin/Cos cost ~20 ns per call on old CPUs, a table lookup costs ~2 ns,
    /// and we need two of them per sample at 192 kHz x 2 channels.
    /// Phase is kept as a fraction of a full turn in [0,1), so retuning never
    /// produces a phase jump larger than the frequency change itself.
    /// </summary>
    public sealed class Nco
    {
        private const int Bits = 12;
        private const int Size = 1 << Bits;
        private const int Mask = Size - 1;
        private const float InvSize = 1.0f / Size;

        private static readonly float[] CosTable = BuildTable(true);
        private static readonly float[] SinTable = BuildTable(false);

        private double _phase;
        private double _inc;

        public void SetFrequency(double hz, double sampleRate)
        {
            _inc = hz / sampleRate;
            _inc -= Math.Floor(_inc);
        }

        public double Frequency { get { return _inc; } }

        public void Reset()
        {
            _phase = 0.0;
        }

        /// <summary>Real input -> complex baseband (multiply by exp(-j*2*pi*f*t)).</summary>
        public void MixReal(float[] src, float[] dstI, float[] dstQ, int count)
        {
            double p = _phase;
            double inc = _inc;

            for (int n = 0; n < count; n++)
            {
                int idx = (int)(p * Size);
                float frac = (float)(p * Size - idx);
                int idx1 = (idx + 1) & Mask;

                float c = CosTable[idx] + (CosTable[idx1] - CosTable[idx]) * frac;
                float s = SinTable[idx] + (SinTable[idx1] - SinTable[idx]) * frac;

                float x = src[n];
                dstI[n] = x * c;
                dstQ[n] = -x * s;

                p += inc;
                if (p >= 1.0) p -= 1.0;
            }

            _phase = p;
        }

        /// <summary>
        /// Complex input -> complex output, shifted DOWN by the NCO frequency:
        /// multiplies by exp(-j*2*pi*f*t).
        /// </summary>
        public void MixComplexDown(float[] srcI, float[] srcQ, float[] dstI, float[] dstQ, int count)
        {
            double p = _phase;
            double inc = _inc;

            for (int n = 0; n < count; n++)
            {
                int idx = (int)(p * Size);
                float frac = (float)(p * Size - idx);
                int idx1 = (idx + 1) & Mask;

                float c = CosTable[idx] + (CosTable[idx1] - CosTable[idx]) * frac;
                float s = SinTable[idx] + (SinTable[idx1] - SinTable[idx]) * frac;

                float i = srcI[n];
                float q = srcQ[n];
                dstI[n] = i * c + q * s;
                dstQ[n] = q * c - i * s;

                p += inc;
                if (p >= 1.0) p -= 1.0;
            }

            _phase = p;
        }

        /// <summary>
        /// Complex input -> complex output, shifted UP by the NCO frequency:
        /// multiplies by exp(+j*2*pi*f*t). This is the BFO of an SSB/CW receiver:
        /// a carrier sitting at DC lands at +f, where you can hear it.
        /// </summary>
        public void MixComplexUp(float[] srcI, float[] srcQ, float[] dstI, float[] dstQ, int count)
        {
            double p = _phase;
            double inc = _inc;

            for (int n = 0; n < count; n++)
            {
                int idx = (int)(p * Size);
                float frac = (float)(p * Size - idx);
                int idx1 = (idx + 1) & Mask;

                float c = CosTable[idx] + (CosTable[idx1] - CosTable[idx]) * frac;
                float s = SinTable[idx] + (SinTable[idx1] - SinTable[idx]) * frac;

                float i = srcI[n];
                float q = srcQ[n];
                dstI[n] = i * c - q * s;
                dstQ[n] = q * c + i * s;

                p += inc;
                if (p >= 1.0) p -= 1.0;
            }

            _phase = p;
        }

        private static float[] BuildTable(bool cosine)
        {
            float[] t = new float[Size + 1];
            for (int n = 0; n <= Size; n++)
            {
                double a = 2.0 * Math.PI * (n & Mask) * InvSize;
                t[n] = (float)(cosine ? Math.Cos(a) : Math.Sin(a));
            }
            return t;
        }
    }
}
