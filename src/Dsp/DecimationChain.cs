using System;
using System.Collections.Generic;

namespace Radiocity.Dsp
{
    /// <summary>
    /// Multi-stage complex decimation.
    ///
    /// Why not one stage: the FIR cost is taps x input rate, and the tap count
    /// grows as the transition band shrinks. Asking one filter to go from
    /// 192 kHz down to 12 kHz while keeping a 9 kHz channel clean means a
    /// transition band of 1.5 kHz at 192 kHz — 554 taps, or 106 M multiply-adds
    /// per second. That is a 2005 CPU's entire budget for one channel.
    ///
    /// Splitting the same job into 192k -> 24k -> 12k costs about 23 M/s: the
    /// first stage has a wide transition band and is cheap per sample, the second
    /// runs at a sixteenth of the rate so its narrow filter costs almost nothing.
    /// Same result, four times cheaper, and it is what makes narrow CW filters
    /// affordable at all.
    /// </summary>
    public sealed class DecimationChain
    {
        private readonly ComplexDecimator[] _stages;
        private readonly int[] _cumulative;
        private readonly float[][] _bufI;
        private readonly float[][] _bufQ;
        private readonly int _totalTaps;
        private readonly int[] _stageTaps;
        private readonly double[] _stageRates;

        public DecimationChain(double sampleRate, int outputRate, double channelHz, int maxBlock)
        {
            if (outputRate <= 0) throw new ArgumentOutOfRangeException("outputRate");
            if (maxBlock < 1) throw new ArgumentOutOfRangeException("maxBlock");

            int total = (int)Math.Round(sampleRate / outputRate);
            if (total < 1) total = 1;

            int[] factors = SplitDecimation(total);
            _stages = new ComplexDecimator[factors.Length];
            _cumulative = new int[factors.Length];
            _bufI = new float[factors.Length][];
            _bufQ = new float[factors.Length][];
            _stageTaps = new int[factors.Length];
            _stageRates = new double[factors.Length];

            double rate = sampleRate;
            int cum = 1;
            int taps = 0;

            for (int s = 0; s < factors.Length; s++)
            {
                int d = factors[s];
                cum *= d;
                int stageOut = (int)Math.Round(sampleRate / cum);

                float[] h = DesignStage(rate, stageOut, channelHz, d);
                _stages[s] = new ComplexDecimator(h, d);
                _cumulative[s] = cum;
                taps += h.Length;
                _stageTaps[s] = h.Length;
                _stageRates[s] = rate;

                // Room for the whole block at this stage's input rate, plus slack
                // for the filter's group delay carry-over.
                int size = maxBlock / Math.Max(1, cum / d) + 16;
                _bufI[s] = new float[size];
                _bufQ[s] = new float[size];

                rate = stageOut;
            }

            _totalTaps = taps;
            OutputRate = (int)Math.Round(sampleRate / cum);
            Stages = factors.Length;
        }

        public int OutputRate { get; private set; }
        public int Stages { get; private set; }
        public int TotalTaps { get { return _totalTaps; } }

        /// <summary>Tap count of each stage, in order.</summary>
        public int[] StageTaps { get { return (int[])_stageTaps.Clone(); } }

        /// <summary>Input rate of each stage, in order.</summary>
        public double[] StageRates { get { return (double[])_stageRates.Clone(); } }

        /// <summary>
        /// Multiply-adds per second for the whole chain. This is the number that
        /// decides whether the thing runs in real time on an old CPU, so it is
        /// reported rather than estimated.
        /// </summary>
        public double CostPerSecond
        {
            get
            {
                double total = 0.0;
                for (int s = 0; s < _stageTaps.Length; s++)
                    total += _stageTaps[s] * _stageRates[s];
                return total;
            }
        }

        /// <summary>
        /// What a single-stage filter would cost for the same job, so the saving
        /// can be measured instead of asserted. Uses the same Kaiser estimate the
        /// real design uses; the only difference is doing it in one step.
        /// </summary>
        public static int EstimateSingleStageTaps(double rate, int outputRate, double channelHz)
        {
            int decim = Math.Max(1, (int)Math.Round(rate / outputRate));
            double pass = Math.Min(channelHz * 0.5, outputRate * 0.45) / rate;
            double stop = 0.5 / decim;

            if (stop - pass < 0.002) return 63;

            int taps = (int)Math.Ceiling(62.0 / (2.285 * 2.0 * Math.PI * (stop - pass)));
            if (taps < 15) taps = 15;
            if (taps > 4095) taps = 4095;
            return taps;
        }

        /// <summary>Returns the number of output samples written.</summary>
        public int Process(float[] inI, float[] inQ, int n, float[] outI, float[] outQ)        {
            float[] curI = inI;
            float[] curQ = inQ;
            int count = n;

            for (int s = 0; s < _stages.Length; s++)
            {
                bool last = (s == _stages.Length - 1);
                float[] oi = last ? outI : _bufI[s];
                float[] oq = last ? outQ : _bufQ[s];

                count = _stages[s].Process(curI, curQ, count, oi, oq);
                curI = oi;
                curQ = oq;
            }

            return count;
        }

        public void Reset()
        {
            for (int s = 0; s < _stages.Length; s++)
            {
                _stages[s].Reset();
                Array.Clear(_bufI[s], 0, _bufI[s].Length);
                Array.Clear(_bufQ[s], 0, _bufQ[s].Length);
            }
        }

        /// <summary>
        /// Factors of the total decimation, each at most 8. Small factors keep
        /// every individual filter cheap; the product is unchanged.
        /// </summary>
        internal static int[] SplitDecimation(int total)
        {
            if (total <= 8) return new int[] { total };

            List<int> parts = new List<int>();
            int remaining = total;

            while (remaining > 8)
            {
                int f = 8;
                while (f > 2 && (remaining % f) != 0) f--;
                if ((remaining % f) != 0) f = 2;

                parts.Add(f);
                remaining /= f;
            }

            if (remaining > 1) parts.Add(remaining);
            return parts.ToArray();
        }

        /// <summary>
        /// Sizes one stage's anti-alias filter from the actual requirement: pass
        /// everything inside half the channel, reject everything above the new
        /// Nyquist, and let the Kaiser estimate decide the tap count.
        /// </summary>
        private static float[] DesignStage(double rate, int stageOutRate, double channelHz, int decim)
        {
            double pass = Math.Min(channelHz * 0.5, stageOutRate * 0.45) / rate;
            double stop = 0.5 / decim;

            // No room for a transition band: fall back to a gentle filter and let
            // the next stage (or the sound card) do the rest.
            if (stop - pass < 0.002 || pass <= 0.0)
                return FirDesign.LowPass(0.45 / decim, 63, 8.6);

            double deltaF = stop - pass;
            int taps = (int)Math.Ceiling(62.0 / (2.285 * 2.0 * Math.PI * deltaF));
            if (taps < 15) taps = 15;
            if (taps > 2047) taps = 2047;

            return FirDesign.LowPass((pass + stop) * 0.5, taps, 8.6);
        }
    }
}
