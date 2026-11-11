using System;
using System.Threading;
using Radiocity.Audio;

namespace Radiocity.Web
{
    /// <summary>
    /// A quadrature front end feeding a stereo sound card: left channel is I,
    /// right channel is Q.
    ///
    /// This is the hardware path the whole project was aimed at. A SoftRock-style
    /// downconverter mixes 87.5 MHz (or 7 MHz, or anything else) down to audio
    /// with a local oscillator and a Tayloe detector, and presents the result as
    /// two audio channels in quadrature. From there the receiver needs no NCO at
    /// all — the mixing already happened in hardware — and the sound card is a
    /// 192 kHz, 24-bit I/Q digitiser.
    ///
    /// The difference matters: with a real quadrature front end the whole
    /// 0..96 kHz band is usable *complex* bandwidth, so a 200 kHz wide FM station
    /// fits inside it. That is the only way this software ever receives
    /// broadcast FM, and it needs a piece of wire and a crystal, not a new
    /// program.
    /// </summary>
    public sealed class QuadratureSource : IQuadratureSource
    {
        private readonly WaveIn _capture;
        private readonly Dsp.RingBuffer _ringI;
        private readonly Dsp.RingBuffer _ringQ;
        private readonly int _rate;
        private readonly int _bits;
        private readonly float[] _interleaved;

        public QuadratureSource(int rate, int bits)
        {
            _rate = rate;
            _bits = bits;

            _ringI = new Dsp.RingBuffer(1 << 18);
            _ringQ = new Dsp.RingBuffer(1 << 18);
            _interleaved = new float[8192];

            _capture = new WaveIn(rate, 2, bits);
            _capture.DataAvailable += OnData;
        }

        public int SampleRate { get { return _rate; } }
        public int Bits { get { return _bits; } }

        public string Description
        {
            get { return "quadrature input, " + _rate + " Hz " + _bits + " bit stereo (L=I, R=Q)"; }
        }

        private void OnData(float[] buffer, int frames)
        {
            // The driver hands over interleaved stereo; split it into the two
            // rings so the DSP side never has to think about channels again.
            int n = Math.Min(frames, _interleaved.Length / 2);

            for (int k = 0; k < n; k++)
            {
                _interleaved[k] = buffer[k * 2];
                _interleaved[n + k] = buffer[k * 2 + 1];
            }

            _ringI.Write(_interleaved, 0, n);
            _ringQ.Write(_interleaved, n, n);
        }

        public void Start() { _capture.Start(); }
        public void Stop() { _capture.Stop(); }

        /// <summary>Fills dst with interleaved I,Q pairs. Returns the number of frames.</summary>
        public int ReadQuadrature(float[] dstI, float[] dstQ, int frames)
        {
            int got = 0;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(200);

            while (got == 0 && DateTime.UtcNow < deadline)
            {
                got = _ringI.Read(dstI, 0, frames);
                if (got == 0) { Thread.Sleep(2); continue; }
                _ringQ.Read(dstQ, 0, got);
            }

            return got;
        }

        public int Read(float[] dst, int offset, int count)
        {
            // ISampleSource is mono by contract; this source is only meaningful
            // through ReadQuadrature. Returning the I channel keeps the interface
            // honest instead of throwing at runtime.
            return _ringI.Read(dst, offset, count);
        }

        public void Dispose()
        {
            _capture.Dispose();
        }
    }

    /// <summary>
    /// A simulated quadrature front end, for developing and testing the stereo
    /// path on a machine with no sound card and no hardware downconverter.
    ///
    /// It deliberately introduces gain and phase imbalance, because a simulator
    /// that produces perfect I/Q tests the one thing that never needs testing.
    /// With the imbalance in place the receiver's correction stage has something
    /// real to fix, and the image rejection can be measured end to end.
    /// </summary>
    public sealed class SyntheticQuadratureSource : IQuadratureSource
    {
        private readonly int _rate;
        private readonly double _gainError;
        private readonly double _phaseErrorDeg;
        private readonly Random _rnd = new Random(4242);
        private readonly object _lock = new object();

        private long _produced;
        private DateTime _start;
        private bool _running;

        // A few carriers at known offsets, so a tuned session has something to
        // demodulate and the waterfall has something to show.
        private static readonly double[] Carriers = { 3000.0, 12000.0, 21000.0 };
        private static readonly double[] Tones = { 440.0, 700.0, 300.0 };
        private static readonly double[] Levels = { 0.30, 0.25, 0.15 };

        public SyntheticQuadratureSource(int rate, double gainError, double phaseErrorDeg)
        {
            _rate = rate;
            _gainError = gainError;
            _phaseErrorDeg = phaseErrorDeg;
        }

        public int SampleRate { get { return _rate; } }
        public int Bits { get { return 32; } }

        public string Description
        {
            get
            {
                return "synthetic quadrature, " + _rate + " Hz, gain error " +
                       (_gainError * 100.0).ToString("F1") + "%, phase error " +
                       _phaseErrorDeg.ToString("F1") + " deg";
            }
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
            double phi = _phaseErrorDeg * Math.PI / 180.0;

            lock (_lock)
            {
                for (int k = 0; k < n; k++)
                {
                    long index = _produced + k;
                    double t = (double)index / _rate;
                    double i = 0.0, q = 0.0;

                    for (int c = 0; c < Carriers.Length; c++)
                    {
                        double m = 1.0 + 0.7 * Math.Cos(2.0 * Math.PI * Tones[c] * t);
                        double amp = Levels[c] * 0.5 * m;
                        double phase = 2.0 * Math.PI * Carriers[c] * t;

                        i += amp * Math.Cos(phase);
                        q += amp * Math.Sin(phase);
                    }

                    // Noise on both branches, uncorrelated.
                    i += (_rnd.NextDouble() * 2.0 - 1.0) * 0.002;
                    q += (_rnd.NextDouble() * 2.0 - 1.0) * 0.002;

                    // The imbalance a real front end would have: the Q branch has
                    // slightly different gain and its phase is off by phi.
                    double scaled = q * (1.0 + _gainError);
                    double imbalanced = scaled * Math.Cos(phi) + i * Math.Sin(phi);

                    dstI[k] = (float)i;
                    dstQ[k] = (float)imbalanced;
                }

                _produced += n;
            }

            return n;
        }

        private float[] _monoScratch;

        public int Read(float[] dst, int offset, int count)
        {
            // Mono fallback for anything that expects ISampleSource semantics.
            // The scratch buffer is reused so this does not allocate per call.
            if (_monoScratch == null || _monoScratch.Length < count) _monoScratch = new float[count];
            int n = ReadQuadrature(dst, _monoScratch, count);
            return n;
        }

        public void Dispose() { _running = false; }
    }
}
