using System;
using System.Threading;
using Radiocity.Dsp;

namespace Radiocity.Web
{
    /// <summary>
    /// A synthetic narrowband FM handheld: a voice-like tone with an 88.5 Hz
    /// CTCSS tone underneath it, +-2.5 kHz deviation.
    ///
    /// The point is to make the radio mode demonstrable without hardware. NFM is
    /// a different receiver from broadcast FM (16 kHz of baseband instead of 192,
    /// 2.5 kHz of deviation instead of 75), so it needs its own source to be
    /// tested end to end rather than assumed to work because the other one does.
    /// </summary>
    public sealed class SyntheticRadioSource : IQuadratureSource
    {
        private readonly int _rate;
        private readonly double _deviation;
        private readonly double _ctcssHz;
        private readonly Random _rnd = new Random(909);

        private readonly object _lock = new object();
        private long _produced;
        private DateTime _start;
        private bool _running;
        private double _phase;

        public SyntheticRadioSource(int rate, double deviation, double ctcssHz)
        {
            _rate = rate;
            _deviation = deviation;
            _ctcssHz = ctcssHz;
        }

        public int SampleRate { get { return _rate; } }
        public int Bits { get { return 32; } }

        public string Description
        {
            get
            {
                return "synthetic NFM handheld, " + _rate + " Hz, +-" +
                       (_deviation / 1000.0).ToString("F1") + " kHz, CTCSS " +
                       _ctcssHz.ToString("F1") + " Hz";
            }
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

                    // Syllables: a 3 Hz envelope on a 900 Hz tone sounds like
                    // someone talking without being speech, and it makes the
                    // AGC work for its living.
                    double syllable = 0.5 + 0.5 * Math.Sin(twoPi * 3.0 * t);
                    double voice = 0.55 * syllable * Math.Cos(twoPi * 900.0 * t);

                    // CTCSS sits at 15% of full deviation: typical for a handheld.
                    double ctcss = 0.15 * Math.Cos(twoPi * _ctcssHz * t);

                    double modulation = voice + ctcss;

                    dstI[k] = (float)Math.Cos(phase);
                    dstQ[k] = (float)Math.Sin(phase);

                    phase += twoPi * _deviation * modulation / _rate;
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
