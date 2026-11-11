using System;
using System.Threading;
using Radiocity.Audio;

namespace Radiocity.Web
{
    /// <summary>
    /// Where the samples come from. Exists so the web front end can be developed
    /// and tested without a sound card — which is not a luxury, it is the only
    /// way to run the server on a machine that has no audio input at all.
    /// </summary>
    public interface ISampleSource : IDisposable
    {
        int SampleRate { get; }
        int Bits { get; }
        string Description { get; }

        void Start();

        /// <summary>Fills up to count samples, blocking briefly if none are ready.</summary>
        int Read(float[] dst, int offset, int count);

        void Stop();
    }

    /// <summary>Real hardware through winmm.</summary>
    public sealed class WaveInSource : ISampleSource
    {
        private readonly WaveIn _capture;
        private readonly Dsp.RingBuffer _ring;
        private readonly int _rate;
        private readonly int _bits;

        public WaveInSource(int rate, int bits)
        {
            _rate = rate;
            _bits = bits;
            _ring = new Dsp.RingBuffer(1 << 18);
            _capture = new WaveIn(rate, 1, bits);
            _capture.DataAvailable += delegate(float[] buf, int frames) { _ring.Write(buf, 0, frames); };
        }

        public int SampleRate { get { return _rate; } }
        public int Bits { get { return _bits; } }
        public string Description { get { return "waveIn " + _rate + " Hz " + _bits + " bit"; } }

        public void Start() { _capture.Start(); }
        public void Stop() { _capture.Stop(); }

        public int Read(float[] dst, int offset, int count)
        {
            int got = 0;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(200);

            while (got == 0 && DateTime.UtcNow < deadline)
            {
                got = _ring.Read(dst, offset, count);
                if (got == 0) Thread.Sleep(2);
            }

            if (got == 0) return 0;
            if (got < count) got += _ring.Read(dst, offset + got, count - got);
            return got;
        }

        public void Dispose()
        {
            _capture.Dispose();
        }
    }

    /// <summary>
    /// Synthetic band for demos and for CI. A handful of AM carriers at known
    /// frequencies plus a noise floor, generated at real-time pace so the
    /// waterfall moves the way it would with a real receiver.
    ///
    /// The carriers are deliberately spread across the band and modulated with
    /// different tones, so both the waterfall and the audio tell you immediately
    /// whether tuning and demodulation work.
    /// </summary>
    public sealed class SyntheticSource : ISampleSource
    {
        private struct Carrier
        {
            public double Hz;
            public double ToneHz;
            public double Depth;
            public double Level;
        }

        private static readonly Carrier[] Carriers =
        {
            new Carrier { Hz = 3000,  ToneHz = 440,  Depth = 0.9, Level = 0.35 },
            new Carrier { Hz = 7500,  ToneHz = 1000, Depth = 0.6, Level = 0.20 },
            new Carrier { Hz = 12000, ToneHz = 700,  Depth = 0.8, Level = 0.30 },
            new Carrier { Hz = 18000, ToneHz = 300,  Depth = 0.5, Level = 0.12 },
            new Carrier { Hz = 21000, ToneHz = 1500, Depth = 0.7, Level = 0.08 }
        };

        private readonly int _rate;
        private readonly Random _rnd = new Random(2026);
        private readonly object _lock = new object();

        private long _produced;
        private DateTime _start;
        private bool _running;

        public SyntheticSource(int rate)
        {
            _rate = rate;
        }

        public int SampleRate { get { return _rate; } }
        public int Bits { get { return 32; } }

        public string Description
        {
            get { return "synthetic band, " + _rate + " Hz, " + Carriers.Length + " carriers"; }
        }

        public void Start()
        {
            lock (_lock)
            {
                _produced = 0;
                _start = DateTime.UtcNow;
                _running = true;
            }
        }

        public void Stop()
        {
            _running = false;
        }

        public int Read(float[] dst, int offset, int count)
        {
            if (!_running) return 0;

            long due;
            lock (_lock)
            {
                double elapsed = (DateTime.UtcNow - _start).TotalSeconds;
                due = (long)(elapsed * _rate) - _produced;
            }

            if (due <= 0)
            {
                Thread.Sleep(2);
                return 0;
            }

            int n = (int)Math.Min(due, count);

            lock (_lock)
            {
                for (int k = 0; k < n; k++)
                {
                    long index = _produced + k;
                    double t = (double)index / _rate;
                    double v = 0.0;

                    for (int c = 0; c < Carriers.Length; c++)
                    {
                        double m = 1.0 + Carriers[c].Depth * Math.Cos(2.0 * Math.PI * Carriers[c].ToneHz * t);
                        v += Carriers[c].Level * 0.5 * m * Math.Cos(2.0 * Math.PI * Carriers[c].Hz * t);
                    }

                    v += (_rnd.NextDouble() * 2.0 - 1.0) * 0.002;
                    dst[offset + k] = (float)v;
                }

                _produced += n;
            }

            return n;
        }

        public void Dispose()
        {
            _running = false;
        }
    }
}
