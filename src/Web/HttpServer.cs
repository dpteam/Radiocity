using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Radiocity.Web
{
    public sealed class HttpRequest
    {
        public string Method;
        public string Path;
        public string RawQuery;
        public string HttpVersion;
        public Dictionary<string, string> Headers;
        public Dictionary<string, string> Query;

        public string Get(string name)
        {
            string v;
            return Query.TryGetValue(name, out v) ? v : null;
        }

        public int GetInt(string name, int fallback)
        {
            string v = Get(name);
            int r;
            if (v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) return r;
            return fallback;
        }

        public double GetDouble(string name, double fallback)
        {
            string v = Get(name);
            double r;
            if (v != null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out r)) return r;
            return fallback;
        }
    }

    public sealed class HttpResponse
    {
        public int Status = 200;
        public string ContentType = "text/plain; charset=utf-8";
        public byte[] Body;
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Set for streaming responses. Receives a stream that already handles
        /// chunked framing, so the callback just writes bytes and returns.
        /// The connection closes when the callback returns.
        /// </summary>
        public Action<Stream> Stream;

        public static HttpResponse Text(string text, int status)
        {
            HttpResponse r = new HttpResponse();
            r.Status = status;
            r.Body = Encoding.UTF8.GetBytes(text);
            return r;
        }

        public static HttpResponse Json(string json)
        {
            HttpResponse r = new HttpResponse();
            r.ContentType = "application/json; charset=utf-8";
            r.Body = Encoding.UTF8.GetBytes(json);
            return r;
        }

        public static HttpResponse NotFound(string what)
        {
            return Text("not found: " + what, 404);
        }
    }

    /// <summary>
    /// A deliberately small HTTP/1.1 server over TcpListener.
    ///
    /// Why not HttpListener: it sits on http.sys, and on Windows XP registering a
    /// URL prefix requires administrator rights. This project targets XP, so a
    /// plain socket server is the only option that works without elevation.
    ///
    /// What it supports: GET and POST, query strings, form bodies, keep-alive,
    /// Content-Length responses and chunked streaming. That is exactly what a
    /// WebSDR needs and nothing more.
    /// </summary>
    public sealed class HttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<HttpRequest, HttpResponse> _handler;
        private readonly int _port;
        private Thread _accept;
        private volatile bool _running;
        private bool _disposed;

        public HttpServer(int port, Func<HttpRequest, HttpResponse> handler)
        {
            _port = port;
            _handler = handler;
            _listener = new TcpListener(IPAddress.Any, port);
        }

        public int Port { get { return _port; } }

        public void Start()
        {
            _listener.Start();
            _running = true;

            _accept = new Thread(AcceptLoop);
            _accept.IsBackground = true;
            _accept.Name = "http-accept";
            _accept.Start();
        }

        public void Stop()
        {
            _running = false;

            try { _listener.Stop(); }
            catch (SocketException) { }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    if (!_running) return;
                    Thread.Sleep(20);
                    continue;
                }

                ThreadPool.QueueUserWorkItem(delegate(object state) { Serve((TcpClient)state); }, client);
            }
        }

        private void Serve(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 30000;

                using (NetworkStream net = client.GetStream())
                {
                    while (_running)
                    {
                        HttpRequest req = ReadRequest(net);
                        if (req == null) return;

                        HttpResponse res;
                        try
                        {
                            res = _handler(req);
                        }
                        catch (Exception ex)
                        {
                            res = HttpResponse.Text("internal error: " + ex.Message, 500);
                        }

                        bool keepAlive = WantsKeepAlive(req) && res.Stream == null;

                        if (!WriteResponse(net, res, keepAlive)) return;
                        if (!keepAlive) return;
                    }
                }
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                try { client.Close(); }
                catch (Exception) { }
            }
        }

        private static bool WantsKeepAlive(HttpRequest req)
        {
            string conn;
            if (req.Headers.TryGetValue("Connection", out conn))
            {
                if (conn.IndexOf("close", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                if (conn.IndexOf("keep-alive", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }

            return req.HttpVersion != "HTTP/1.0";
        }

        private static HttpRequest ReadRequest(NetworkStream net)
        {
            // Read until the blank line that ends the header block. 16 KB is
            // generous: browsers send far less, and anything bigger is abuse.
            MemoryStream buffer = new MemoryStream();
            byte[] one = new byte[1];
            int matched = 0;

            while (buffer.Length < 16384)
            {
                int read;
                try
                {
                    read = net.Read(one, 0, 1);
                }
                catch (IOException)
                {
                    return null;
                }

                if (read <= 0) return null;

                buffer.WriteByte(one[0]);

                // Track CRLFCRLF.
                if ((matched == 0 || matched == 2) && one[0] == (byte)'\r') matched++;
                else if ((matched == 1 || matched == 3) && one[0] == (byte)'\n') matched++;
                else matched = (one[0] == (byte)'\r') ? 1 : 0;

                if (matched == 4) break;
            }

            if (buffer.Length == 0) return null;

            string header = Encoding.ASCII.GetString(buffer.ToArray());
            string[] lines = header.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0 || lines[0].Length == 0) return null;

            string[] parts = lines[0].Split(' ');
            if (parts.Length < 2) return null;

            HttpRequest req = new HttpRequest();
            req.Method = parts[0].ToUpperInvariant();
            req.HttpVersion = parts.Length > 2 ? parts[2] : "HTTP/1.0";
            req.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string target = parts[1];
            int q = target.IndexOf('?');
            if (q >= 0)
            {
                req.Path = target.Substring(0, q);
                req.RawQuery = target.Substring(q + 1);
            }
            else
            {
                req.Path = target;
                req.RawQuery = string.Empty;
            }

            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                string name = lines[i].Substring(0, colon).Trim();
                string value = lines[i].Substring(colon + 1).Trim();
                req.Headers[name] = value;
            }

            // A POST carries its parameters in the body.
            string body = string.Empty;
            string lenHeader;
            if (req.Method == "POST" && req.Headers.TryGetValue("Content-Length", out lenHeader))
            {
                int len;
                if (int.TryParse(lenHeader, NumberStyles.Integer, CultureInfo.InvariantCulture, out len) && len > 0 && len < 65536)
                {
                    byte[] bodyBytes = new byte[len];
                    int got = 0;
                    while (got < len)
                    {
                        int r = net.Read(bodyBytes, got, len - got);
                        if (r <= 0) break;
                        got += r;
                    }
                    body = Encoding.UTF8.GetString(bodyBytes, 0, got);
                }
            }

            string all = req.RawQuery;
            if (body.Length > 0) all = all.Length > 0 ? all + "&" + body : body;
            req.Query = ParseQuery(all);

            return req;
        }

        private static Dictionary<string, string> ParseQuery(string raw)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(raw)) return d;

            string[] pairs = raw.Split('&');
            for (int i = 0; i < pairs.Length; i++)
            {
                if (pairs[i].Length == 0) continue;

                int eq = pairs[i].IndexOf('=');
                string key = eq >= 0 ? pairs[i].Substring(0, eq) : pairs[i];
                string val = eq >= 0 ? pairs[i].Substring(eq + 1) : string.Empty;

                d[UrlDecode(key)] = UrlDecode(val);
            }

            return d;
        }

        private static string UrlDecode(string s)
        {
            if (s.IndexOf('%') < 0 && s.IndexOf('+') < 0) return s;

            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '+') { sb.Append(' '); continue; }

                if (s[i] == '%' && i + 2 < s.Length)
                {
                    int hi = HexValue(s[i + 1]);
                    int lo = HexValue(s[i + 2]);
                    if (hi >= 0 && lo >= 0)
                    {
                        sb.Append((char)(hi * 16 + lo));
                        i += 2;
                        continue;
                    }
                }

                sb.Append(s[i]);
            }

            return sb.ToString();
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        private static bool WriteResponse(NetworkStream net, HttpResponse res, bool keepAlive)
        {
            StringBuilder head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(res.Status).Append(' ').Append(StatusText(res.Status)).Append("\r\n");
            head.Append("Server: Radiocity\r\n");
            head.Append("Content-Type: ").Append(res.ContentType).Append("\r\n");

            foreach (KeyValuePair<string, string> h in res.Headers)
                head.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");

            if (res.Stream != null)
            {
                head.Append("Transfer-Encoding: chunked\r\n");
                head.Append("Cache-Control: no-store\r\n");
                head.Append("Connection: close\r\n");
            }
            else
            {
                head.Append("Content-Length: ").Append(res.Body == null ? 0 : res.Body.Length).Append("\r\n");
                head.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n");
            }

            head.Append("\r\n");

            byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
            net.Write(headBytes, 0, headBytes.Length);

            if (res.Stream != null)
            {
                ChunkedStream chunked = new ChunkedStream(net);

                try
                {
                    res.Stream(chunked);
                }
                catch (IOException) { }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
                catch (Exception ex)
                {
                    // A streaming handler that throws must not take the process
                    // down with it: this runs on a pool thread, where an escaped
                    // exception is fatal.
                    Console.Error.WriteLine("stream handler failed: " + ex.Message);
                }

                chunked.Finish();
                return false;
            }

            if (res.Body != null && res.Body.Length > 0)
                net.Write(res.Body, 0, res.Body.Length);

            return true;
        }

        private static string StatusText(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 500: return "Internal Server Error";
                default: return "OK";
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }

    /// <summary>
    /// Wraps a stream in HTTP chunked framing, so callers can just Write() and
    /// let the browser reassemble. Flushes every chunk: audio is useless buffered.
    /// </summary>
    internal sealed class ChunkedStream : Stream
    {
        private readonly Stream _inner;
        private readonly byte[] _prefix = new byte[16];
        private bool _finished;

        public ChunkedStream(Stream inner)
        {
            _inner = inner;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_finished || count <= 0) return;

            string hex = count.ToString("x", CultureInfo.InvariantCulture);
            int n = Encoding.ASCII.GetBytes(hex, 0, hex.Length, _prefix, 0);

            _inner.Write(_prefix, 0, n);
            _inner.Write(new byte[] { 13, 10 }, 0, 2);

            _inner.Write(buffer, offset, count);
            _inner.Write(new byte[] { 13, 10 }, 0, 2);
            _inner.Flush();
        }

        public void Finish()
        {
            if (_finished) return;
            _finished = true;

            try
            {
                _inner.Write(new byte[] { 48, 13, 10, 13, 10 }, 0, 5);   // "0\r\n\r\n"
                _inner.Flush();
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        public override bool CanRead { get { return false; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { _inner.Flush(); }
        public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
    }
}
