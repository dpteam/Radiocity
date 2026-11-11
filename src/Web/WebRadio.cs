using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Radiocity.Dsp;

namespace Radiocity.Web
{
    /// <summary>
    /// The shared receiver behind the web front end: one DSP thread, one tuning,
    /// many listeners.
    ///
    /// This is the difference between "a program you operate" and "a server you
    /// tune": the receiver runs whether anyone is listening or not, audio clients
    /// attach and detach, and the waterfall keeps rolling. Every client gets its
    /// own ring buffer, so a slow browser cannot stall the DSP thread.
    /// </summary>
    public sealed class WebRadio : IDisposable
    {
        public const int AudioRate = 48000;
        public const int Bins = 256;
        private const int MaxColumns = 512;

        /// <summary>
        /// FFT length is chosen from the sample rate so the waterfall always runs
        /// at roughly 25 columns per second. A fixed 2048-point FFT is 42 ms per
        /// column at 48 kHz but 10 ms at 192 kHz, which scrolls far too fast.
        /// </summary>
        private static int PickFftSize(int rate)
        {
            int target = rate / 25;
            int size = 1024;
            while (size < target && size < 16384) size <<= 1;
            return size;
        }

        private readonly ISampleSource _source;
        private readonly int _rate;

        // Configuration, guarded by _configLock. Frequency alone can change
        // without a rebuild; mode and bandwidth need a new Receiver.
        private readonly object _configLock = new object();
        private double _freq;
        private DemodMode _mode;
        private double _channelHz;
        private float? _squelchDb;
        private float? _blankerThreshold;
        private int _generation;

        private readonly List<AudioClient> _clients = new List<AudioClient>();
        private readonly object _clientsLock = new object();

        private readonly byte[][] _columns = new byte[MaxColumns][];
        private long _columnsTotal;
        private readonly object _columnsLock = new object();

        private readonly float[] _window;
        private readonly float[] _re;
        private readonly float[] _im;
        private readonly float[] _fftBuf;
        private int _fftFill;
        private readonly int _fftSize;

        private Thread _thread;
        private volatile bool _running;
        private volatile float _levelDb = -120.0f;
        private volatile bool _squelchOpen = true;
        private bool _disposed;

        public WebRadio(ISampleSource source, double initialFreq, DemodMode mode, double channelHz)
        {
            _source = source;
            _rate = source.SampleRate;

            _freq = initialFreq;
            _mode = mode;
            _channelHz = channelHz;

            _fftSize = PickFftSize(_rate);
            _window = Fft.Hann(_fftSize);
            _re = new float[_fftSize];
            _im = new float[_fftSize];
            _fftBuf = new float[_fftSize];

            for (int i = 0; i < MaxColumns; i++) _columns[i] = new byte[Bins];
        }

        public int SampleRate { get { return _rate; } }

        public double Frequency { get { lock (_configLock) return _freq; } }
        public DemodMode Mode { get { lock (_configLock) return _mode; } }
        public double ChannelHz { get { lock (_configLock) return _channelHz; } }
        public float LevelDb { get { return _levelDb; } }
        public bool SquelchOpen { get { return _squelchOpen; } }

        public void Start()
        {
            _running = true;

            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Name = "dsp";
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            if (_thread != null) _thread.Join(2000);
        }

        // ------------------------------------------------------------ control

        /// <summary>Frequency changes do not need a new receiver, only a new NCO increment.</summary>
        public void Tune(double hz)
        {
            lock (_configLock) _freq = hz;
        }

        /// <summary>Mode or bandwidth change: the DSP thread rebuilds its chain.</summary>
        public void Configure(DemodMode mode, double channelHz, float? squelchDb)
        {
            lock (_configLock)
            {
                _mode = mode;
                _channelHz = channelHz;
                _squelchDb = squelchDb;
            }

            Interlocked.Increment(ref _generation);
        }

        /// <summary>Impulse blanker threshold; null switches it off.</summary>
        public void SetBlanker(float? threshold)
        {
            lock (_configLock) _blankerThreshold = threshold;
            Interlocked.Increment(ref _generation);
        }

        public void SetSquelch(float? squelchDb)
        {
            lock (_configLock) _squelchDb = squelchDb;
            Interlocked.Increment(ref _generation);
        }

        // ------------------------------------------------------------ clients

        internal sealed class AudioClient
        {
            public RingBuffer Ring = new RingBuffer(1 << 17);
            public volatile bool Closed;
        }

        public AudioClientHandle Attach()
        {
            AudioClient client = new AudioClient();
            lock (_clientsLock) _clients.Add(client);
            return new AudioClientHandle(this, client);
        }

        private void Detach(AudioClient client)
        {
            client.Closed = true;
            lock (_clientsLock) _clients.Remove(client);
        }

        /// <summary>
        /// Handle for one listener. Drain() blocks until there is audio, so the
        /// HTTP thread can just push bytes to the socket.
        /// </summary>
        public sealed class AudioClientHandle : IDisposable
        {
            private readonly WebRadio _radio;
            private readonly AudioClient _client;
            private bool _disposed;

            internal AudioClientHandle(WebRadio radio, AudioClient client)
            {
                _radio = radio;
                _client = client;
            }

            /// <summary>
            /// Copies available samples. Returns the count, 0 if nothing arrived
            /// in time, or -1 when the client is gone.
            /// </summary>
            public int Drain(float[] dst, int count)
            {
                if (_client.Closed) return -1;

                for (int attempt = 0; attempt < 400; attempt++)
                {
                    int got = _client.Ring.Read(dst, 0, count);
                    if (got > 0) return got;
                    if (_client.Closed) return -1;
                    Thread.Sleep(5);
                }

                return 0;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _radio.Detach(_client);
            }
        }

        // ------------------------------------------------------------ waterfall

        /// <summary>
        /// Copies every spectrum column produced since <paramref name="from"/>.
        /// Returns the new watermark, so a slow browser can catch up in one go
        /// instead of silently dropping columns.
        /// </summary>
        public long ReadColumns(long from, out byte[] data)
        {
            lock (_columnsLock)
            {
                long total = _columnsTotal;
                if (from > total) from = total;
                if (from < total - MaxColumns) from = total - MaxColumns;

                int count = (int)(total - from);
                if (count <= 0)
                {
                    data = new byte[0];
                    return total;
                }

                data = new byte[count * Bins];
                for (int i = 0; i < count; i++)
                {
                    int slot = (int)((from + i) % MaxColumns);
                    Buffer.BlockCopy(_columns[slot], 0, data, i * Bins, Bins);
                }

                return total;
            }
        }

        public long ColumnsTotal
        {
            get { lock (_columnsLock) return _columnsTotal; }
        }

        // ------------------------------------------------------------ dsp loop

        private void Run()
        {
            float[] block = new float[32768];
            float[] audio = new float[32768];
            float[] play = new float[32768 * 4 + 64];

            Receiver rx = null;
            InterpolatingResampler resampler = null;
            int seenGeneration = -1;

            while (_running)
            {
                if (seenGeneration != Volatile.Read(ref _generation))
                {
                    DemodMode mode;
                    double channel;
                    float? squelch;
                    float? blanker;
                    double freq;

                    lock (_configLock)
                    {
                        mode = _mode;
                        channel = _channelHz;
                        squelch = _squelchDb;
                        blanker = _blankerThreshold;
                        freq = _freq;
                    }

                    // Rebuild allocates, but it happens on the DSP thread in
                    // response to a user action, not inside the audio callback.
                    ReceiverOptions options = new ReceiverOptions();
                    options.Mode = mode;
                    options.ChannelHz = channel;
                    options.SquelchDb = squelch;
                    options.BlankerThreshold = blanker;
                    if (mode == DemodMode.Fm)
                    {
                        options.DeviationHz = 75000.0;
                        options.DeemphasisUs = 50.0;
                    }
                    if (mode == DemodMode.Cw) options.BfoHz = 700.0;

                    options.Decimation = Math.Max(1, (int)Math.Round((double)_rate / 12000.0));

                    rx = new Receiver(_rate, freq, options);
                    resampler = (rx.OutputRate == AudioRate)
                        ? null
                        : new InterpolatingResampler(rx.OutputRate, AudioRate);

                    seenGeneration = Volatile.Read(ref _generation);
                }

                int n = _source.Read(block, 0, block.Length);
                if (n <= 0) { Thread.Sleep(2); continue; }

                rx.SetTune(Frequency);

                PumpSpectrum(block, n);

                int m = rx.Process(block, n, audio);
                if (m <= 0) continue;

                _levelDb = rx.LevelDb;
                _squelchOpen = rx.Squelch.Open;

                int produced;
                if (resampler == null)
                {
                    Array.Copy(audio, play, m);
                    produced = m;
                }
                else
                {
                    produced = resampler.Process(audio, m, play, play.Length);
                }

                if (produced > 0) Broadcast(play, produced);
            }

            lock (_clientsLock)
            {
                for (int i = 0; i < _clients.Count; i++) _clients[i].Closed = true;
                _clients.Clear();
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

        /// <summary>
        /// Accumulates samples until a full FFT frame is available, then emits a
        /// column. Live blocks are often shorter than the FFT (a sound card
        /// delivers a few hundred samples at a time), so taking "the last N
        /// samples of the block" would either read garbage or never fire at all.
        /// </summary>
        private void PumpSpectrum(float[] block, int n)
        {
            int offset = 0;

            while (offset < n)
            {
                int take = Math.Min(_fftSize - _fftFill, n - offset);
                Array.Copy(block, offset, _fftBuf, _fftFill, take);
                _fftFill += take;
                offset += take;

                if (_fftFill >= _fftSize)
                {
                    EmitColumn();
                    _fftFill = 0;
                }
            }
        }

        private void EmitColumn()
        {
            for (int i = 0; i < _fftSize; i++)
            {
                _re[i] = _fftBuf[i] * _window[i];
                _im[i] = 0.0f;
            }

            Fft.Forward(_re, _im);

            byte[] column = new byte[Bins];
            int usable = _fftSize / 2;
            int perBin = Math.Max(1, usable / Bins);

            for (int b = 0; b < Bins; b++)
            {
                double peak = 0.0;
                int from = b * perBin;
                int to = Math.Min(usable, from + perBin);

                for (int i = from; i < to; i++)
                {
                    double mag = _re[i] * (double)_re[i] + _im[i] * (double)_im[i];
                    if (mag > peak) peak = mag;
                }

                double amp = Math.Sqrt(peak) * 4.0 / _fftSize;
                double db = 20.0 * Math.Log10(Math.Max(amp, 1e-12));

                // Fixed scale: -110 dBFS at the bottom, -20 at the top. A real
                // receiver's numbers stay comparable between sessions this way.
                int v = (int)((db + 110.0) / 90.0 * 255.0);
                if (v < 0) v = 0;
                if (v > 255) v = 255;
                column[b] = (byte)v;
            }

            lock (_columnsLock)
            {
                _columns[(int)(_columnsTotal % MaxColumns)] = column;
                _columnsTotal++;
            }
        }

        // ------------------------------------------------------------ endpoints

        public HttpResponse Handle(HttpRequest req)
        {
            string path = req.Path;

            if (path == "/" || path == "/index.html") return Page.Index();
            if (path == "/waterfall") return HandleWaterfall(req);
            if (path == "/state") return HandleState();
            if (path == "/tune") return HandleTune(req);
            if (path == "/audio") return HandleAudio();

            return HttpResponse.NotFound(path);
        }

        private HttpResponse HandleWaterfall(HttpRequest req)
        {
            long from = (long)req.GetDouble("from", 0.0);
            byte[] data;
            long next = ReadColumns(from, out data);

            HttpResponse r = new HttpResponse();
            r.ContentType = "application/octet-stream";
            r.Body = data;
            r.Headers["X-Next-From"] = next.ToString(CultureInfo.InvariantCulture);
            r.Headers["X-Bins"] = Bins.ToString(CultureInfo.InvariantCulture);
            r.Headers["X-Rate"] = _rate.ToString(CultureInfo.InvariantCulture);
            return r;
        }

        private HttpResponse HandleState()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"freq\":").Append(Frequency.ToString("F0", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"mode\":\"").Append(Mode.ToString().ToUpperInvariant()).Append("\",");
            sb.Append("\"channel\":").Append(ChannelHz.ToString("F0", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"rate\":").Append(_rate).Append(',');
            sb.Append("\"level\":").Append(LevelDb.ToString("F1", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"open\":").Append(SquelchOpen ? "true" : "false").Append(',');

            float? blanker = CurrentBlanker;
            sb.Append("\"blank\":").Append(blanker.HasValue
                ? blanker.Value.ToString("F0", CultureInfo.InvariantCulture)
                : "0").Append(',');

            sb.Append("\"columns\":").Append(ColumnsTotal);
            sb.Append('}');

            return HttpResponse.Json(sb.ToString());
        }

        private HttpResponse HandleTune(HttpRequest req)
        {
            if (req.Method != "POST" && req.Method != "GET")
                return HttpResponse.Text("use POST", 405);

            double freq = req.GetDouble("f", Frequency);
            string modeText = req.Get("mode");
            double channel = req.GetDouble("bw", ChannelHz);
            string squelchText = req.Get("squelch");
            string blankText = req.Get("blank");

            DemodMode mode = Mode;
            if (!string.IsNullOrEmpty(modeText))
            {
                switch (modeText.ToLowerInvariant())
                {
                    case "am": mode = DemodMode.Am; break;
                    case "fm": mode = DemodMode.Fm; break;
                    case "usb": mode = DemodMode.Usb; break;
                    case "lsb": mode = DemodMode.Lsb; break;
                    case "cw": mode = DemodMode.Cw; break;
                }
            }

            float? squelch = null;
            if (!string.IsNullOrEmpty(squelchText))
            {
                float sv;
                if (float.TryParse(squelchText, NumberStyles.Float, CultureInfo.InvariantCulture, out sv))
                    squelch = sv;
            }

            float? blanker;
            lock (_configLock) blanker = _blankerThreshold;

            if (!string.IsNullOrEmpty(blankText))
            {
                float bv;
                if (float.TryParse(blankText, NumberStyles.Float, CultureInfo.InvariantCulture, out bv))
                    blanker = (bv > 0.0f) ? (float?)bv : null;
            }

            // Clamp inside what the card can actually see.
            double limit = _rate * 0.5 - 500.0;
            if (freq < 0.0) freq = 0.0;
            if (freq > limit) freq = limit;

            bool needsRebuild = (mode != Mode) || Math.Abs(channel - ChannelHz) > 1.0;

            Tune(freq);
            if (needsRebuild) Configure(mode, channel, squelch);
            else if (!string.IsNullOrEmpty(squelchText)) SetSquelch(squelch);

            if (!Nullable.Equals(blanker, CurrentBlanker)) SetBlanker(blanker);

            return HandleState();
        }

        /// <summary>Current blanker threshold, null when off.</summary>
        public float? CurrentBlanker
        {
            get { lock (_configLock) return _blankerThreshold; }
        }

        private HttpResponse HandleAudio()
        {
            HttpResponse r = new HttpResponse();
            r.ContentType = "audio/L16;rate=" + AudioRate + ";channels=1";

            AudioClientHandle handle = Attach();
            float[] buffer = new float[4096];
            byte[] bytes = new byte[4096 * 2];

            r.Stream = delegate(Stream stream)
            {
                try
                {
                    while (_running)
                    {
                        int got = handle.Drain(buffer, buffer.Length);
                        if (got < 0) break;
                        if (got == 0) continue;      // idle is not an error

                        for (int i = 0; i < got; i++)
                        {
                            float v = buffer[i];
                            if (v > 1.0f) v = 1.0f;
                            if (v < -1.0f) v = -1.0f;
                            short s = (short)(v * 32767.0f);
                            bytes[i * 2] = (byte)s;
                            bytes[i * 2 + 1] = (byte)(s >> 8);
                        }

                        stream.Write(bytes, 0, got * 2);
                    }
                }
                catch (IOException) { }
                finally
                {
                    handle.Dispose();
                }
            };

            return r;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();
            _source.Dispose();
        }
    }
}
