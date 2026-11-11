using System;
using System.Threading;
using Radiocity.Dsp;

namespace Radiocity.Web
{
    /// <summary>
    /// A complete synthetic FM stereo station with RDS, delivered as complex
    /// baseband.
    ///
    /// Why this exists: every other part of the FM path can be tested in
    /// isolation, but "does the whole thing work" needs one signal that carries
    /// audio, a stereo subcarrier, a pilot and RDS at once — and a machine with
    /// no sound card and no antenna cannot produce that. This does, so the web UI
    /// can be demonstrated end to end on any laptop.
    ///
    /// The station identity is deliberately unmistakable: different tones in L
    /// and R, and RDS text that says what it is.
    /// </summary>
    public sealed class SyntheticFmSource : IQuadratureSource
    {
        private const int GroupsPerCycle = 20;

        private readonly int _rate;
        private readonly double _deviation;
        private readonly int[] _bits;
        private readonly double _samplesPerBit;

        private readonly object _lock = new object();
        private long _produced;
        private DateTime _start;
        private bool _running;

        private double _phase;

        public SyntheticFmSource(int rate, double deviation)
        {
            _rate = rate;
            _deviation = deviation;
            _samplesPerBit = rate / RdsCode.BitRateHz;

            _bits = BuildCycle();
        }

        public int SampleRate { get { return _rate; } }
        public int Bits { get { return 32; } }

        public string Description
        {
            get
            {
                return "synthetic FM stereo + RDS, " + _rate + " Hz, deviation " +
                       (_deviation / 1000.0).ToString("F0") + " kHz";
            }
        }

        /// <summary>
        /// One full RDS cycle: four type-0 groups with the station name, then
        /// sixteen type-2 groups with the scrolling text.
        /// </summary>
        private static int[] BuildCycle()
        {
            const int pi = 0xCAFE;
            const string ps = "RADIOCIT";
            const string rt = "RADIOCITY SOUND CARD SDR - SYNTHETIC FM DEMO";

            int[] bits = new int[GroupsPerCycle * 104];
            int at = 0;

            for (int g = 0; g < GroupsPerCycle; g++)
            {
                int[] words;

                if (g < 4)
                {
                    int seg = g;
                    int b = (0 << 12) | (0 << 11) | (1 << 10) | (10 << 5) | seg;
                    int c = 0xE0E0;
                    int d = (ps[seg * 2] << 8) | ps[seg * 2 + 1];
                    words = new int[] { pi, b, c, d };
                }
                else
                {
                    int seg = g - 4;
                    int off = seg * 4;

                    int ch0 = off < rt.Length ? rt[off] : ' ';
                    int ch1 = off + 1 < rt.Length ? rt[off + 1] : ' ';
                    int ch2 = off + 2 < rt.Length ? rt[off + 2] : ' ';
                    int ch3 = off + 3 < rt.Length ? rt[off + 3] : ' ';

                    int b = (2 << 12) | (0 << 11) | (1 << 10) | (10 << 5) | (0 << 4) | seg;
                    words = new int[] { pi, b, (ch0 << 8) | ch1, (ch2 << 8) | ch3 };
                }

                int[] encoded = RdsEncoder.EncodeBits(words);
                Array.Copy(encoded, 0, bits, at, encoded.Length);
                at += encoded.Length;
            }

            return bits;
        }

        public void Start()
        {
            lock (_lock)
            {
                _produced = 0;
                _start = DateTime.UtcNow;
                _running = true;
                _phase = 0.0;
            }
        }

        public void Stop() { _running = false; }

        public int ReadQuadrature(float[] dstI, float[] dstQ, int frames)
        {
            if (!_running) return 0;

            long due;
            lock (_lock)
            {
                double elapsed = (DateTime.UtcNow - _start).TotalSeconds;
                due = (long)(elapsed * _rate) - _produced;
            }

            if (due <= 0) { Thread.Sleep(2); return 0; }

            int n = (int)Math.Min(due, frames);
            double twoPi = 2.0 * Math.PI;
            double phase = _phase;

            lock (_lock)
            {
                for (int k = 0; k < n; k++)
                {
                    long index = _produced + k;
                    double t = (double)index / _rate;

                    // Stereo programme: 440 Hz on the left, 660 Hz on the right,
                    // so a swapped channel is immediately audible.
                    double l = 0.30 * Math.Cos(twoPi * 440.0 * t);
                    double r = 0.30 * Math.Cos(twoPi * 660.0 * t);

                    double m = (l + r) * 0.5;
                    double s = (l - r) * 0.5;

                    double pilot = 0.09 * Math.Cos(twoPi * RdsCode.PilotHz * t);

                    // RDS: biphase symbols on the 57 kHz subcarrier.
                    double bitPos = t * RdsCode.BitRateHz;
                    long bitIndex = (long)bitPos;
                    double withinBit = bitPos - bitIndex;
                    int bit = _bits[(int)(bitIndex % _bits.Length)];
                    bool firstHalf = withinBit < 0.5;
                    double sign = (bit == 1) ? (firstHalf ? 1.0 : -1.0) : (firstHalf ? -1.0 : 1.0);
                    double rds = 0.025 * sign * Math.Cos(twoPi * RdsCode.SubcarrierHz * t);

                    double mpx = m + s * Math.Cos(twoPi * 38000.0 * t) + pilot + rds;

                    dstI[k] = (float)Math.Cos(phase);
                    dstQ[k] = (float)Math.Sin(phase);

                    phase += twoPi * _deviation * mpx / _rate;
                    if (phase > twoPi) phase -= twoPi;
                }

                _phase = phase;
                _produced += n;
            }

            return n;
        }

        private float[] _monoScratch;

        public int Read(float[] dst, int offset, int count)
        {
            if (_monoScratch == null || _monoScratch.Length < count) _monoScratch = new float[count];
            return ReadQuadrature(dst, _monoScratch, count);
        }

        public void Dispose() { _running = false; }
    }
}
