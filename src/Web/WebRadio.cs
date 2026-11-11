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
    /// The web front end: a shared capture, one receiver per listener, and the
    /// HTTP endpoints that tie them together.
    ///
    /// Sessions are identified by an opaque id the browser keeps in a variable and
    /// appends to every request. No cookies: they would need parsing, expiry
    /// rules and a whole argument about consent, and a query parameter does the
    /// same job in one line.
    ///
    /// A request without an id gets the "default" session. That keeps a bare
    /// <c>curl http://host/state</c> useful and means a client that forgets to
    /// keep its id still gets a working receiver rather than an error.
    /// </summary>
    public sealed class WebRadio : IDisposable
    {
        public const int MaxSessions = 8;
        private const string DefaultSessionId = "default";
        private static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromMinutes(5);

        private readonly CaptureHub _hub;
        private readonly Dictionary<string, RadioSession> _sessions =
            new Dictionary<string, RadioSession>(StringComparer.Ordinal);
        private readonly object _sessionsLock = new object();

        private readonly double _initialFreq;
        private readonly DemodMode _initialMode;
        private readonly double _initialChannel;
        private readonly bool _correctImbalance;
        private readonly bool _stereo;
        private float? _initialBlanker;

        private Timer _reaper;
        private bool _disposed;

        public WebRadio(ISampleSource source, double initialFreq, DemodMode mode, double channelHz)
            : this(source, initialFreq, mode, channelHz, false, false)
        {
        }

        public WebRadio(ISampleSource source, double initialFreq, DemodMode mode, double channelHz,
                        bool correctImbalance)
            : this(source, initialFreq, mode, channelHz, correctImbalance, false)
        {
        }

        public WebRadio(ISampleSource source, double initialFreq, DemodMode mode, double channelHz,
                        bool correctImbalance, bool stereo)
        {
            _hub = new CaptureHub(source);

            _initialFreq = initialFreq;
            _initialMode = mode;
            _initialChannel = channelHz;
            _correctImbalance = correctImbalance;
            _stereo = stereo;

            // The default session exists from the start, so /state answers
            // immediately and the console banner can report real settings.
            GetOrCreate(DefaultSessionId);
        }

        /// <summary>True when the source is a quadrature front end.</summary>
        public bool IsQuadrature { get { return _hub.IsQuadrature; } }

        public int SampleRate { get { return _hub.SampleRate; } }

        /// <summary>Settings of the default session, for the startup banner.</summary>
        public DemodMode Mode { get { return GetOrCreate(DefaultSessionId).Mode; } }
        public double Frequency { get { return GetOrCreate(DefaultSessionId).Frequency; } }
        public double ChannelHz { get { return GetOrCreate(DefaultSessionId).ChannelHz; } }

        public int SessionCount { get { lock (_sessionsLock) return _sessions.Count; } }
        public double TotalFilterCost { get { return _hub.TotalFilterCost; } }

        public void Start()
        {
            _hub.Start();

            // Sessions whose browser tab was closed without saying goodbye would
            // otherwise hold a receiver thread for the life of the process.
            _reaper = new Timer(Reap, null, 60000, 60000);
        }

        public void Stop()
        {
            if (_reaper != null) { _reaper.Dispose(); _reaper = null; }
            _hub.Stop();

            lock (_sessionsLock)
            {
                foreach (KeyValuePair<string, RadioSession> kv in _sessions) kv.Value.Dispose();
                _sessions.Clear();
            }
        }

        public void SetBlanker(float? threshold)
        {
            _initialBlanker = threshold;
            GetOrCreate(DefaultSessionId).Configure(
                _initialMode, _initialChannel, null, threshold);
        }

        // ------------------------------------------------------------ sessions

        private RadioSession GetOrCreate(string id)
        {
            lock (_sessionsLock)
            {
                RadioSession existing;
                if (_sessions.TryGetValue(id, out existing))
                {
                    existing.Touch();
                    return existing;
                }

                if (_sessions.Count >= MaxSessions && id != DefaultSessionId)
                {
                    // Refuse rather than silently degrade everyone: N receivers
                    // means N FIR chains, and on an old CPU that ceiling is real.
                    throw new SessionLimitException(MaxSessions);
                }

                RadioSession session = new RadioSession(_hub, id, _initialFreq, _initialMode, _initialChannel,
                                                        _correctImbalance, _stereo);
                session.Configure(_initialMode, _initialChannel, null, _initialBlanker);
                _sessions[id] = session;
                return session;
            }
        }

        private RadioSession Lookup(HttpRequest req)
        {
            string id = req.Get("sid");
            if (string.IsNullOrEmpty(id)) id = DefaultSessionId;

            // Session ids come from the outside world, so keep them to something
            // that cannot be used to grow a dictionary without bound.
            if (id.Length > 40) id = id.Substring(0, 40);

            return GetOrCreate(id);
        }

        private void Reap(object state)
        {
            List<RadioSession> dead = new List<RadioSession>();
            DateTime now = DateTime.UtcNow;

            lock (_sessionsLock)
            {
                foreach (KeyValuePair<string, RadioSession> kv in _sessions)
                {
                    if (kv.Key == DefaultSessionId) continue;
                    if (now - kv.Value.LastAccess > SessionIdleTimeout) dead.Add(kv.Value);
                }

                for (int i = 0; i < dead.Count; i++) _sessions.Remove(dead[i].Id);
            }

            for (int i = 0; i < dead.Count; i++) dead[i].Dispose();
        }

        /// <summary>Thrown when the receiver limit is reached; the handler turns it into a 503.</summary>
        public sealed class SessionLimitException : Exception
        {
            public SessionLimitException(int limit)
                : base("this server is already running " + limit +
                       " receivers; close another tab or wait for one to time out")
            {
            }
        }

        // ------------------------------------------------------------ endpoints

        public HttpResponse Handle(HttpRequest req)
        {
            string path = req.Path;

            try
            {
                if (path == "/" || path == "/index.html") return Page.Index();
                if (path == "/waterfall") return HandleWaterfall(req);
                if (path == "/state") return HandleState(req);
                if (path == "/tune") return HandleTune(req);
                if (path == "/audio") return HandleAudio(req);

                return HttpResponse.NotFound(path);
            }
            catch (SessionLimitException ex)
            {
                return HttpResponse.Text(ex.Message, 503);
            }
        }

        private HttpResponse HandleWaterfall(HttpRequest req)
        {
            long from = (long)req.GetDouble("from", 0.0);
            byte[] data;
            long next = _hub.ReadColumns(from, out data);

            HttpResponse r = new HttpResponse();
            r.ContentType = "application/octet-stream";
            r.Body = data;
            r.Headers["X-Next-From"] = next.ToString(CultureInfo.InvariantCulture);
            r.Headers["X-Bins"] = CaptureHub.Bins.ToString(CultureInfo.InvariantCulture);
            r.Headers["X-Rate"] = _hub.SampleRate.ToString(CultureInfo.InvariantCulture);
            r.Headers["X-Band-Lo"] = _hub.BandLo.ToString("F0", CultureInfo.InvariantCulture);
            r.Headers["X-Band-Hi"] = _hub.BandHi.ToString("F0", CultureInfo.InvariantCulture);
            return r;
        }

        private HttpResponse HandleState(HttpRequest req)
        {
            RadioSession s = Lookup(req);

            StringBuilder sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"sid\":\"").Append(Escape(s.Id)).Append("\",");
            sb.Append("\"freq\":").Append(s.Frequency.ToString("F0", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"mode\":\"").Append(s.Mode.ToString().ToUpperInvariant()).Append("\",");
            sb.Append("\"channel\":").Append(s.ChannelHz.ToString("F0", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"rate\":").Append(_hub.SampleRate).Append(',');
            sb.Append("\"level\":").Append(s.LevelDb.ToString("F1", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"open\":").Append(s.SquelchOpen ? "true" : "false").Append(',');

            float? blanker = s.BlankerThreshold;
            sb.Append("\"blank\":").Append(blanker.HasValue
                ? blanker.Value.ToString("F0", CultureInfo.InvariantCulture)
                : "0").Append(',');

            float? squelch = s.SquelchDb;
            sb.Append("\"squelch\":").Append(squelch.HasValue
                ? squelch.Value.ToString("F0", CultureInfo.InvariantCulture)
                : "0").Append(',');

            sb.Append("\"sessions\":").Append(SessionCount).Append(',');
            sb.Append("\"stereo\":").Append(s.Channels == 2 ? "true" : "false").Append(',');

            // RDS only exists when a stereo FM receiver is running, so the fields
            // are emitted empty rather than absent: a client should not have to
            // special-case a missing key.
            RdsDecoder rds = s.Rds;
            sb.Append("\"rds\":").Append(rds != null && rds.Synced ? "true" : "false").Append(',');
            sb.Append("\"pi\":\"").Append(rds != null && rds.PiCode != 0
                ? rds.PiCode.ToString("X4", CultureInfo.InvariantCulture) : "").Append("\",");
            sb.Append("\"ps\":\"").Append(Escape(rds != null ? (rds.ProgramService ?? "") : "")).Append("\",");
            sb.Append("\"rt\":\"").Append(Escape(rds != null ? rds.RadioText : "")).Append("\",");
            sb.Append("\"pty\":").Append(rds != null ? rds.Pty : 0).Append(',');
            sb.Append("\"groups\":").Append(rds != null ? rds.Groups : 0).Append(',');
            sb.Append("\"quadrature\":").Append(_hub.IsQuadrature ? "true" : "false").Append(',');
            sb.Append("\"bandLo\":").Append(_hub.BandLo.ToString("F0", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"bandHi\":").Append(_hub.BandHi.ToString("F0", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"columns\":").Append(_hub.ColumnsTotal);
            sb.Append('}');

            return HttpResponse.Json(sb.ToString());
        }

        private HttpResponse HandleTune(HttpRequest req)
        {
            if (req.Method != "POST" && req.Method != "GET")
                return HttpResponse.Text("use POST", 405);

            RadioSession s = Lookup(req);

            double freq = req.GetDouble("f", s.Frequency);
            string modeText = req.Get("mode");
            double channel = req.GetDouble("bw", s.ChannelHz);
            string squelchText = req.Get("squelch");
            string blankText = req.Get("blank");

            DemodMode mode = s.Mode;
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

            float? squelch = s.SquelchDb;
            if (!string.IsNullOrEmpty(squelchText))
            {
                float sv;
                if (float.TryParse(squelchText, NumberStyles.Float, CultureInfo.InvariantCulture, out sv))
                    squelch = (sv > -120.0f) ? (float?)sv : null;
            }

            float? blanker = s.BlankerThreshold;
            if (!string.IsNullOrEmpty(blankText))
            {
                float bv;
                if (float.TryParse(blankText, NumberStyles.Float, CultureInfo.InvariantCulture, out bv))
                    blanker = (bv > 0.0f) ? (float?)bv : null;
            }

            // Clamp inside what the receiver can actually see. For a real source
            // that is 0..rate/2; for a quadrature front end it is the full
            // complex band around the local oscillator.
            double lo = _hub.BandLo + 500.0;
            double hi = _hub.BandHi - 500.0;
            if (freq < lo) freq = lo;
            if (freq > hi) freq = hi;

            bool needsRebuild = (mode != s.Mode) || Math.Abs(channel - s.ChannelHz) > 1.0 ||
                                !Nullable.Equals(squelch, s.SquelchDb) ||
                                !Nullable.Equals(blanker, s.BlankerThreshold);

            s.Tune(freq);
            if (needsRebuild) s.Configure(mode, channel, squelch, blanker);

            return HandleState(req);
        }

        private HttpResponse HandleAudio(HttpRequest req)
        {
            RadioSession s = Lookup(req);

            int channels = s.Channels;

            HttpResponse r = new HttpResponse();
            r.ContentType = "audio/L16;rate=" + RadioSession.AudioRate + ";channels=" + channels;
            r.Headers["X-Channels"] = channels.ToString(CultureInfo.InvariantCulture);

            RadioSession.AudioHandle handle = s.AttachAudio();
            float[] buffer = new float[4096];
            byte[] bytes = new byte[4096 * 2];

            r.Stream = delegate(Stream stream)
            {
                int idle = 0;

                try
                {
                    while (!_disposed)
                    {
                        int got = handle.Drain(buffer, buffer.Length);
                        if (got < 0) break;

                        if (got == 0)
                        {
                            // Nothing arrived. Drain waits 200 ms before
                            // returning zero, so fifty of these is ten seconds
                            // of silence — long enough to mean the client is
                            // gone, short enough to stop a thread leaking.
                            if (++idle >= 50) break;
                            continue;
                        }

                        idle = 0;

                        for (int i = 0; i < got; i++)
                        {
                            float v = buffer[i];
                            if (v > 1.0f) v = 1.0f;
                            if (v < -1.0f) v = -1.0f;
                            short sample = (short)(v * 32767.0f);
                            bytes[i * 2] = (byte)sample;
                            bytes[i * 2 + 1] = (byte)(sample >> 8);
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

        private static string Escape(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append('?');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();
            _hub.Dispose();
        }
    }
}
