using System;
using System.Collections.Generic;
using System.Threading;
using Radiocity.Dsp;

namespace Radiocity.Web
{
    /// <summary>
    /// One listener's receiver: its own tuning, mode, bandwidth, squelch, blanker
    /// and AGC, all fed from the shared capture.
    ///
    /// Why a thread per session rather than one thread looping over all sessions:
    /// a session whose client has asked for a narrow filter does more work than
    /// one listening wide, and with a single loop the slowest session sets the
    /// latency for everybody. With a thread each, they degrade independently, and
    /// the cost is a few hundred kilobytes of stack that is mostly untouched.
    ///
    /// The DSP only runs while somebody is actually listening. Tuning state
    /// survives, so a client that pauses and resumes finds its frequency intact.
    /// </summary>
    public sealed class RadioSession : IDisposable
    {
        public const int AudioRate = 48000;

        private readonly CaptureHub _hub;
        private readonly int _rate;
        private readonly string _id;
        private readonly object _configLock = new object();
        private readonly object _clientsLock = new object();
        private readonly List<AudioClient> _clients = new List<AudioClient>();

        private readonly RingBuffer _inRing = new RingBuffer(1 << 17);
        private readonly RingBuffer _inRingQ = new RingBuffer(1 << 17);
        private readonly bool _quadrature;
        private readonly bool _correctImbalance;
        private readonly bool _stereo;

        private double _freq;
        private DemodMode _mode;
        private double _channelHz;
        private float? _squelchDb;
        private float? _blankerThreshold;
        private double? _ctcssTone;
        private int? _dcsCode;
        private int _generation;

        private Thread _thread;
        private volatile bool _running;
        private volatile float _levelDb = -120.0f;
        private volatile bool _squelchOpen = true;
        private long _lastAccessTicks;
        private bool _disposed;

        internal RadioSession(CaptureHub hub, string id, double freq, DemodMode mode, double channelHz,
                              bool correctImbalance, bool stereo)
        {
            _hub = hub;
            _id = id;
            _rate = hub.SampleRate;
            _quadrature = hub.IsQuadrature;
            _correctImbalance = correctImbalance;
            _stereo = stereo;

            _freq = freq;
            _mode = mode;
            _channelHz = channelHz;
            _lastAccessTicks = DateTime.UtcNow.Ticks;

            _hub.Subscribe(this);

            // The receiver runs whether anyone is listening or not. A level meter
            // and a squelch indicator that only start working after you press
            // "Listen" are useless: they exist to tell you whether to press it.
            EnsureRunning();
        }

        public string Id { get { return _id; } }
        public int SampleRate { get { return _rate; } }

        public double Frequency { get { lock (_configLock) return _freq; } }
        public DemodMode Mode { get { lock (_configLock) return _mode; } }
        public double ChannelHz { get { lock (_configLock) return _channelHz; } }
        public float? SquelchDb { get { lock (_configLock) return _squelchDb; } }
        public float? BlankerThreshold { get { lock (_configLock) return _blankerThreshold; } }
        public double? CtcssTone { get { lock (_configLock) return _ctcssTone; } }
        public int? DcsCode { get { lock (_configLock) return _dcsCode; } }
        public float LevelDb { get { return _levelDb; } }
        public bool SquelchOpen { get { return _squelchOpen; } }

        /// <summary>1 for mono, 2 for FM stereo. Audio is interleaved when 2.</summary>
        public int Channels { get { return _channels; } }
        private volatile int _channels = 1;

        /// <summary>RDS state, or null when the current receiver has no RDS decoder.</summary>
        public RdsDecoder Rds { get { return _rds; } }
        private volatile RdsDecoder _rds;
        private volatile CtcssDecoder _ctcss;
        private volatile DcsDecoder _dcs;

        /// <summary>CTCSS detector, or null when the current receiver has none.</summary>
        public CtcssDecoder Ctcss { get { return _ctcss; } }

        /// <summary>DCS decoder, or null when the current receiver has none.</summary>
        public DcsDecoder Dcs { get { return _dcs; } }

        /// <summary>Filter cost of this session's chain, once it has been built.</summary>
        public double FilterCost { get { return _filterCost; } }
        private double _filterCost;

        public DateTime LastAccess
        {
            get { return new DateTime(Interlocked.Read(ref _lastAccessTicks), DateTimeKind.Utc); }
        }

        public void Touch()
        {
            Interlocked.Exchange(ref _lastAccessTicks, DateTime.UtcNow.Ticks);
        }

        // ------------------------------------------------------------ control

        public void Tune(double hz)
        {
            lock (_configLock) _freq = hz;
            Touch();
        }

        public void Configure(DemodMode mode, double channelHz, float? squelchDb, float? blanker)
        {
            Configure(mode, channelHz, squelchDb, blanker, null, null);
        }

        public void Configure(DemodMode mode, double channelHz, float? squelchDb, float? blanker,
                              double? ctcssTone, int? dcsCode)
        {
            lock (_configLock)
            {
                _mode = mode;
                _channelHz = channelHz;
                _squelchDb = squelchDb;
                _blankerThreshold = blanker;
                _ctcssTone = ctcssTone;
                _dcsCode = dcsCode;
            }

            Interlocked.Increment(ref _generation);
            Touch();
        }

        // ------------------------------------------------------------ audio clients

        internal sealed class AudioClient
        {
            public RingBuffer Ring = new RingBuffer(1 << 16);
            public volatile bool Closed;
        }

        public sealed class AudioHandle : IDisposable
        {
            private readonly RadioSession _session;
            private readonly AudioClient _client;
            private bool _disposed;

            internal AudioHandle(RadioSession session, AudioClient client)
            {
                _session = session;
                _client = client;
            }

            /// <summary>
            /// Copies available samples. Returns the count, 0 if nothing arrived
            /// in time, or -1 when the session or the client is gone.
            /// </summary>
            public int Drain(float[] dst, int count)
            {
                if (_client.Closed) return -1;

                for (int attempt = 0; attempt < 40; attempt++)
                {
                    int got = _client.Ring.Read(dst, 0, count);
                    if (got > 0) return got;
                    if (_client.Closed || _session._disposed) return -1;
                    Thread.Sleep(5);
                }

                return 0;
            }

            /// <summary>
            /// True when the client has gone away. A socket whose peer vanished
            /// only reports it on write, so an idle stream has to be probed, and
            /// an abandoned connection must not hold a pool thread forever.
            /// </summary>
            public bool Closed { get { return _client.Closed; } }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _session.DetachAudio(_client);
            }
        }

        public AudioHandle AttachAudio()
        {
            AudioClient client = new AudioClient();
            lock (_clientsLock) _clients.Add(client);

            Touch();
            return new AudioHandle(this, client);
        }

        private void DetachAudio(AudioClient client)
        {
            client.Closed = true;
            lock (_clientsLock) _clients.Remove(client);
        }

        // ------------------------------------------------------------ feed

        /// <summary>
        /// Called by the capture thread with a raw block. Never blocks: a session
        /// that cannot keep up loses its oldest samples rather than stalling
        /// everyone else's receiver.
        /// </summary>
        internal void Feed(float[] block, float[] blockQ, int n)
        {
            if (!_running) return;

            if (_inRing.Free < n) _inRing.Clear();
            _inRing.Write(block, 0, n);

            if (_quadrature)
            {
                if (_inRingQ.Free < n) _inRingQ.Clear();
                _inRingQ.Write(blockQ, 0, n);
            }
        }

        private void EnsureRunning()
        {
            if (_running) return;

            lock (_clientsLock)
            {
                if (_running) return;
                _running = true;

                _thread = new Thread(Run);
                _thread.IsBackground = true;
                _thread.Name = "dsp-" + _id;
                _thread.Start();
            }
        }

        private void Run()
        {
            float[] block = new float[32768];
            float[] blockQ = new float[32768];
            float[] audio = new float[32768 * 2];
            float[] play = new float[32768 * 4 + 64];

            float[] leftBuf = new float[32768];
            float[] rightBuf = new float[32768];
            float[] leftOut = new float[32768 * 4 + 64];
            float[] rightOut = new float[32768 * 4 + 64];

            Receiver rx = null;
            InterpolatingResampler resampler = null;
            InterpolatingResampler resamplerQ = null;
            int seenGeneration = -1;

            while (_running)
            {
                if (seenGeneration != Volatile.Read(ref _generation))
                {
                    DemodMode mode;
                    double channel;
                    float? squelch;
                    float? blanker;
                    double? ctcss;
                    int? dcs;
                    double freq;

                    lock (_configLock)
                    {
                        mode = _mode;
                        channel = _channelHz;
                        squelch = _squelchDb;
                        blanker = _blankerThreshold;
                        ctcss = _ctcssTone;
                        dcs = _dcsCode;
                        freq = _freq;
                    }

                    // Rebuilding allocates, but on the session's own thread in
                    // response to a user action, never inside an audio callback.
                    ReceiverOptions options = new ReceiverOptions();
                    options.Mode = mode;
                    options.ChannelHz = channel;
                    options.SquelchDb = squelch;
                    options.BlankerThreshold = blanker;
                    options.CtcssTone = ctcss;
                    options.DcsCode = dcs;
                    options.CorrectImbalance = _quadrature && _correctImbalance;
                    if (mode == DemodMode.Fm)
                    {
                        options.DeviationHz = 75000.0;
                        options.DeemphasisUs = 50.0;
                        options.FmStereo = _stereo;
                        options.Rds = _stereo;
                    }
                    if (mode == DemodMode.Nfm)
                    {
                        options.DeviationHz = 2500.0;
                        options.DeemphasisUs = 0.0;
                    }
                    if (mode == DemodMode.Cw) options.BfoHz = 700.0;

                    options.Decimation = Math.Max(1, (int)Math.Round((double)_rate / 12000.0));

                    rx = new Receiver(_rate, freq, options);
                    _channels = rx.Channels;
                    _rds = rx.Rds;
                    _ctcss = rx.Ctcss;
                    _dcs = rx.Dcs;

                    resampler = (rx.OutputRate == AudioRate)
                        ? null
                        : new InterpolatingResampler(rx.OutputRate, AudioRate);

                    resamplerQ = (resampler == null || rx.Channels != 2)
                        ? null
                        : new InterpolatingResampler(rx.OutputRate, AudioRate);

                    _filterCost = rx.FilterCost;
                    seenGeneration = Volatile.Read(ref _generation);
                }

                if (_inRing.Count < 1024)
                {
                    Thread.Sleep(2);
                    continue;
                }

                int n = _inRing.Read(block, 0, block.Length);
                if (n <= 0) { Thread.Sleep(2); continue; }

                int m;

                if (_quadrature)
                {
                    // A quadrature front end already removed the image, but the
                    // band it hands over is up to 192 kHz wide, so the NCO still
                    // has to pick a station inside it.
                    int nq = _inRingQ.Read(blockQ, 0, n);
                    if (nq < n) n = nq;
                    if (n <= 0) continue;

                    rx.SetTune(Frequency);
                    m = rx.ProcessComplex(block, blockQ, n, audio);
                }
                else
                {
                    rx.SetTune(Frequency);
                    m = rx.Process(block, n, audio);
                }

                if (m <= 0) continue;

                _levelDb = rx.LevelDb;
                _squelchOpen = rx.Squelch.Open;

                int produced;

                if (resampler == null)
                {
                    // Mono or stereo, the output is already at the audio rate.
                    Array.Copy(audio, play, m * _channels);
                    produced = m * _channels;
                }
                else if (_channels == 2)
                {
                    // Resample the two channels independently, then interleave.
                    // A mono resampler applied to interleaved samples would
                    // resample the left/right sequence as if it were one signal.
                    int frames = m;
                    int half = frames;
                    for (int k = 0; k < half; k++)
                    {
                        leftBuf[k] = audio[k * 2];
                        rightBuf[k] = audio[k * 2 + 1];
                    }

                    int wl = resampler.Process(leftBuf, half, leftOut, leftOut.Length);
                    int wr = resamplerQ.Process(rightBuf, half, rightOut, rightOut.Length);
                    int w = Math.Min(wl, wr);

                    for (int k = 0; k < w; k++)
                    {
                        play[k * 2] = leftOut[k];
                        play[k * 2 + 1] = rightOut[k];
                    }

                    produced = w * 2;
                }
                else
                {
                    produced = resampler.Process(audio, m, play, play.Length);
                }

                if (produced > 0) Broadcast(play, produced);
            }
        }

        private void Broadcast(float[] data, int count)
        {
            lock (_clientsLock)
            {
                for (int i = 0; i < _clients.Count; i++)
                {
                    AudioClient c = _clients[i];
                    if (c.Closed) continue;

                    // A stalled client must not grow its buffer forever: drop the
                    // oldest audio so latency stays bounded and we stay live.
                    if (c.Ring.Free < count) c.Ring.Clear();
                    c.Ring.Write(data, 0, count);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _running = false;
            if (_thread != null) _thread.Join(2000);

            lock (_clientsLock)
            {
                for (int i = 0; i < _clients.Count; i++) _clients[i].Closed = true;
                _clients.Clear();
            }

            _hub.Unsubscribe(this);
        }
    }
}
