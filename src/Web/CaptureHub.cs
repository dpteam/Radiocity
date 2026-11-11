using System;
using System.Collections.Generic;
using System.Threading;
using Radiocity.Dsp;

namespace Radiocity.Web
{
    /// <summary>
    /// One capture, many receivers.
    ///
    /// The sound card can only be opened once, so exactly one thread reads it and
    /// hands each raw block to every session that wants it. This is the piece
    /// that makes independent tuning possible: before it, one <c>Receiver</c>
    /// served every browser, so whoever tuned last won.
    ///
    /// The waterfall is computed here too, once, because the spectrum of the band
    /// is the same for everyone. Only the demodulation is per-session, which is
    /// also where the CPU goes — so the cost of N listeners is N FIR chains, and
    /// that is the number worth knowing.
    /// </summary>
    public sealed class CaptureHub : IDisposable
    {
        public const int Bins = 256;
        private const int MaxColumns = 512;

        private readonly ISampleSource _source;
        private readonly int _rate;
        private readonly int _fftSize;
        private readonly float[] _window;
        private readonly float[] _re;
        private readonly float[] _im;
        private readonly float[] _fftBuf;
        private int _fftFill;

        private readonly byte[][] _columns = new byte[MaxColumns][];
        private long _columnsTotal;
        private readonly object _columnsLock = new object();

        private readonly List<RadioSession> _sessions = new List<RadioSession>();
        private readonly object _sessionsLock = new object();

        private Thread _thread;
        private volatile bool _running;
        private bool _disposed;

        public CaptureHub(ISampleSource source)
        {
            _source = source;
            _rate = source.SampleRate;
            _fftSize = PickFftSize(_rate);

            _window = Fft.Hann(_fftSize);
            _re = new float[_fftSize];
            _im = new float[_fftSize];
            _fftBuf = new float[_fftSize];

            for (int i = 0; i < MaxColumns; i++) _columns[i] = new byte[Bins];
        }

        public int SampleRate { get { return _rate; } }
        public long ColumnsTotal { get { lock (_columnsLock) return _columnsTotal; } }

        public int SessionCount
        {
            get { lock (_sessionsLock) return _sessions.Count; }
        }

        public void Start()
        {
            _running = true;

            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Name = "capture";
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            if (_thread != null) _thread.Join(3000);
        }

        internal void Subscribe(RadioSession session)
        {
            lock (_sessionsLock) _sessions.Add(session);
        }

        internal void Unsubscribe(RadioSession session)
        {
            lock (_sessionsLock) _sessions.Remove(session);
        }

        /// <summary>Every session's filter cost, summed. What N listeners actually cost.</summary>
        public double TotalFilterCost
        {
            get
            {
                double total = 0.0;
                lock (_sessionsLock)
                {
                    for (int i = 0; i < _sessions.Count; i++) total += _sessions[i].FilterCost;
                }
                return total;
            }
        }

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

        private void Run()
        {
            float[] block = new float[32768];

            while (_running)
            {
                int n = _source.Read(block, 0, block.Length);
                if (n <= 0) { Thread.Sleep(2); continue; }

                PumpSpectrum(block, n);

                lock (_sessionsLock)
                {
                    for (int i = 0; i < _sessions.Count; i++)
                        _sessions[i].Feed(block, n);
                }
            }
        }

        /// <summary>
        /// Accumulates samples until a full FFT frame is available. A live source
        /// delivers a few hundred samples at a time, so "take the last N of the
        /// block" would either read garbage or never fire at all.
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

                // Fixed scale: -110 dBFS at the bottom, -20 at the top, so a
                // receiver's numbers stay comparable between sessions.
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

        /// <summary>
        /// FFT length from the sample rate, so the waterfall runs at about
        /// 25 columns per second at 48 kHz and at 192 kHz alike. A fixed
        /// 2048-point FFT would scroll four times too fast at 192 kHz.
        /// </summary>
        internal static int PickFftSize(int rate)
        {
            int target = rate / 25;
            int size = 1024;
            while (size < target && size < 16384) size <<= 1;
            return size;
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
