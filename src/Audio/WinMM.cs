using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Radiocity.Audio
{
    /// <summary>
    /// Windows Multimedia (winmm.dll) capture. This is the only audio API that
    /// exists on every Windows from 98 to 11 without third-party drivers, which
    /// is exactly the constraint of this project: XP SP3 has no WASAPI.
    ///
    /// Notes:
    ///  - MME gives us the device's native format, up to 24 bit / 192 kHz.
    ///  - Latency is 20-60 ms with small buffers; Kernel Streaming (ksuser.dll)
    ///    is the next step if that ever becomes a problem.
    ///  - The driver serialises callbacks, so one shared conversion buffer is
    ///    safe, but each block still gets its own buffer to keep it obvious.
    /// </summary>
    public sealed class WaveIn : IDisposable
    {
        private const int BufferCount = 8;
        private const int BufferFrames = 2048;

        private const uint CallbackFunction = 0x00030000;
        private const uint WIM_DATA = 0x3C0;

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveFormatEx
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHdr
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        private delegate void WaveInProc(IntPtr hwi, uint msg, IntPtr instance, IntPtr param1, IntPtr param2);

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern int waveInOpen(out IntPtr hwi, uint deviceId, ref WaveFormatEx format,
            IntPtr callback, IntPtr instance, uint flags);

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern int waveInPrepareHeader(IntPtr hwi, IntPtr hdr, int size);

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr hdr, int size);

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern int waveInAddBuffer(IntPtr hwi, IntPtr hdr, int size);

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern int waveInStart(IntPtr hwi);

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern int waveInStop(IntPtr hwi);

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern int waveInReset(IntPtr hwi);

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern int waveInClose(IntPtr hwi);

        private readonly int _channels;
        private readonly int _bits;
        private readonly int _bytesPerSample;
        private readonly float[] _scratch;
        private readonly IntPtr[] _headers;
        private readonly IntPtr[] _data;
        private readonly float[][] _converted;

        private IntPtr _handle;
        private WaveInProc _proc;   // must stay referenced while the device is open
        private volatile bool _running;
        private bool _disposed;

        /// <summary>Raised on the driver's callback thread. The buffer is reused, so copy it.</summary>
        public event Action<float[], int> DataAvailable;

        public WaveIn(int sampleRate, int channels, int bits)
        {
            _channels = channels;
            _bits = bits;
            _bytesPerSample = bits / 8;

            int bytes = BufferFrames * channels * _bytesPerSample;
            _scratch = new float[BufferFrames * channels];
            _headers = new IntPtr[BufferCount];
            _data = new IntPtr[BufferCount];
            _converted = new float[BufferCount][];

            for (int i = 0; i < BufferCount; i++)
            {
                _data[i] = Marshal.AllocHGlobal(bytes);
                _headers[i] = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WaveHdr)));
                _converted[i] = new float[BufferFrames * channels];

                WaveHdr hdr = new WaveHdr();
                hdr.lpData = _data[i];
                hdr.dwBufferLength = (uint)bytes;
                hdr.dwUser = (IntPtr)i;
                Marshal.StructureToPtr(hdr, _headers[i], false);
            }

            WaveFormatEx fmt = new WaveFormatEx();
            fmt.wFormatTag = 1;                       // WAVE_FORMAT_PCM
            fmt.nChannels = (ushort)channels;
            fmt.nSamplesPerSec = (uint)sampleRate;
            fmt.wBitsPerSample = (ushort)bits;
            fmt.nBlockAlign = (ushort)(channels * _bytesPerSample);
            fmt.nAvgBytesPerSec = (uint)(sampleRate * fmt.nBlockAlign);

            _proc = new WaveInProc(OnWaveIn);

            int rc = waveInOpen(out _handle, 0xFFFFFFFF /* WAVE_MAPPER */, ref fmt,
                Marshal.GetFunctionPointerForDelegate(_proc), IntPtr.Zero, CallbackFunction);
            if (rc != 0) throw new InvalidOperationException("waveInOpen failed: " + MmError(rc) +
                " (requested " + sampleRate + " Hz, " + bits + " bit, " + channels + " ch)");

            for (int i = 0; i < BufferCount; i++)
            {
                int rc2 = waveInPrepareHeader(_handle, _headers[i], Marshal.SizeOf(typeof(WaveHdr)));
                if (rc2 != 0) throw new InvalidOperationException("waveInPrepareHeader failed, MMRESULT=" + rc2);
            }
        }

        public void Start()
        {
            if (_running) return;
            _running = true;

            for (int i = 0; i < BufferCount; i++)
            {
                int rc = waveInAddBuffer(_handle, _headers[i], Marshal.SizeOf(typeof(WaveHdr)));
                if (rc != 0) throw new InvalidOperationException("waveInAddBuffer failed, MMRESULT=" + rc);
            }

            waveInStart(_handle);
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            waveInStop(_handle);
            waveInReset(_handle);
        }

        private void OnWaveIn(IntPtr hwi, uint msg, IntPtr instance, IntPtr param1, IntPtr param2)
        {
            if (msg != WIM_DATA) return;

            WaveHdr hdr = (WaveHdr)Marshal.PtrToStructure(param1, typeof(WaveHdr));
            int index = (int)hdr.dwUser;
            int frames = (int)(hdr.dwBytesRecorded / (_channels * _bytesPerSample));

            if (frames > 0 && _running)
            {
                Unpack(_data[index], frames, _converted[index]);

                Action<float[], int> handler = DataAvailable;
                if (handler != null) handler(_converted[index], frames);
            }

            if (_running)
            {
                hdr.dwBytesRecorded = 0;
                Marshal.StructureToPtr(hdr, param1, false);
                waveInAddBuffer(hwi, param1, Marshal.SizeOf(typeof(WaveHdr)));
            }
        }

        private unsafe void Unpack(IntPtr src, int frames, float[] dst)
        {
            int n = frames * _channels;

            if (_bits == 16)
            {
                unsafe
                {
                    short* p = (short*)src;
                    for (int i = 0; i < n; i++) dst[i] = p[i] / 32768.0f;
                }
            }
            else if (_bits == 24)
            {
                // 24-bit PCM arrives as 3 packed bytes; sign-extend by hand.
                byte* p = (byte*)src;
                for (int i = 0; i < n; i++)
                {
                    int s = p[0] | (p[1] << 8) | (p[2] << 16);
                    if ((s & 0x800000) != 0) s |= unchecked((int)0xFF000000);
                    dst[i] = s / 8388608.0f;
                    p += 3;
                }
            }
            else
            {
                int* p = (int*)src;
                for (int i = 0; i < n; i++) dst[i] = p[i] / 2147483648.0f;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();

            if (_handle != IntPtr.Zero)
            {
                for (int i = 0; i < BufferCount; i++)
                    waveInUnprepareHeader(_handle, _headers[i], Marshal.SizeOf(typeof(WaveHdr)));

                waveInClose(_handle);
                _handle = IntPtr.Zero;
            }

            for (int i = 0; i < BufferCount; i++)
            {
                if (_data[i] != IntPtr.Zero) Marshal.FreeHGlobal(_data[i]);
                if (_headers[i] != IntPtr.Zero) Marshal.FreeHGlobal(_headers[i]);
                _data[i] = IntPtr.Zero;
                _headers[i] = IntPtr.Zero;
            }

            _proc = null;
            GC.SuppressFinalize(this);
        }

        /// <summary>Turns an MMRESULT into something a human can act on.</summary>
        internal static string MmError(int code)
        {
            switch (code)
            {
                case 1: return "MMSYSERR_ERROR";
                case 2: return "MMSYSERR_BADDEVICEID - no such device";
                case 3: return "MMSYSERR_NOTENABLED - the driver is disabled";
                case 4: return "MMSYSERR_ALLOCATED - the device is already in use";
                case 5: return "MMSYSERR_INVALHANDLE";
                case 6: return "MMSYSERR_NODRIVER - no driver installed";
                case 7: return "MMSYSERR_NOMEM";
                case 8: return "MMSYSERR_NOTSUPPORTED - the card does not support this format";
                case 11: return "MMSYSERR_INVALPARAM - bad format";
                case 32: return "WAVERR_BADFORMAT - the card does not support this format";
                case 33: return "WAVERR_STILLPLAYING";
                case 34: return "WAVERR_UNPREPARED";
                default: return "MMRESULT " + code;
            }
        }
    }

    /// <summary>
    /// Playback side of the same story: waveOut, mono/stereo 16 or 24 bit.
    /// Write() blocks until the driver frees a buffer, which is what we want
    /// from a receiver loop: it paces the DSP chain to real time.
    /// </summary>
    public sealed class WaveOut : IDisposable
    {
        private const int BufferCount = 4;
        private const int BufferFrames = 2048;
        private const uint CallbackFunction = 0x00030000;
        private const uint WOM_DONE = 0x3BD;

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveFormatEx
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHdr
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        private delegate void WaveOutProc(IntPtr hwo, uint msg, IntPtr instance, IntPtr param1, IntPtr param2);

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern int waveOutOpen(out IntPtr hwo, uint deviceId, ref WaveFormatEx format,
            IntPtr callback, IntPtr instance, uint flags);

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, int size);

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, int size);

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, int size);

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern int waveOutReset(IntPtr hwo);

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern int waveOutClose(IntPtr hwo);

        private readonly int _bytesPerSample;
        private readonly int _channels;
        private readonly IntPtr[] _headers;
        private readonly IntPtr[] _data;
        private readonly bool[] _free;
        private readonly object _lock = new object();

        private IntPtr _handle;
        private WaveOutProc _proc;
        private bool _disposed;

        public WaveOut(int sampleRate, int channels, int bits)
        {
            _channels = channels;
            _bytesPerSample = bits / 8;

            int bytes = BufferFrames * channels * _bytesPerSample;
            _headers = new IntPtr[BufferCount];
            _data = new IntPtr[BufferCount];
            _free = new bool[BufferCount];

            for (int i = 0; i < BufferCount; i++)
            {
                _data[i] = Marshal.AllocHGlobal(bytes);
                _headers[i] = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WaveHdr)));
                _free[i] = true;

                WaveHdr hdr = new WaveHdr();
                hdr.lpData = _data[i];
                hdr.dwBufferLength = (uint)bytes;
                hdr.dwUser = (IntPtr)i;
                Marshal.StructureToPtr(hdr, _headers[i], false);
            }

            WaveFormatEx fmt = new WaveFormatEx();
            fmt.wFormatTag = 1;
            fmt.nChannels = (ushort)channels;
            fmt.nSamplesPerSec = (uint)sampleRate;
            fmt.wBitsPerSample = (ushort)bits;
            fmt.nBlockAlign = (ushort)(channels * _bytesPerSample);
            fmt.nAvgBytesPerSec = (uint)(sampleRate * fmt.nBlockAlign);

            _proc = new WaveOutProc(OnWaveOut);

            int rc = waveOutOpen(out _handle, 0xFFFFFFFF, ref fmt,
                Marshal.GetFunctionPointerForDelegate(_proc), IntPtr.Zero, CallbackFunction);
            if (rc != 0) throw new InvalidOperationException("waveOutOpen failed: " + WaveIn.MmError(rc) +
                " (requested " + sampleRate + " Hz, " + bits + " bit)");

            for (int i = 0; i < BufferCount; i++)
                waveOutPrepareHeader(_handle, _headers[i], Marshal.SizeOf(typeof(WaveHdr)));
        }

        /// <summary>
        /// Writes any number of samples, splitting them across driver buffers.
        /// Blocks until the driver frees slots, which is what paces the DSP chain
        /// to real time.
        /// </summary>
        public void Write(float[] samples, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int n = Math.Min(count - offset, BufferFrames * _channels);
                WriteChunk(samples, offset, n);
                offset += n;
            }
        }

        private unsafe void WriteChunk(float[] samples, int offset, int count)
        {
            int slot = -1;
            while (slot < 0)
            {
                lock (_lock)
                {
                    for (int i = 0; i < BufferCount; i++)
                    {
                        if (_free[i]) { _free[i] = false; slot = i; break; }
                    }
                }
                if (slot < 0) Thread.Sleep(1);
            }

            IntPtr dst = _data[slot];

            if (_bytesPerSample == 2)
            {
                short* p = (short*)dst;
                for (int i = 0; i < count; i++)
                {
                    float v = samples[offset + i];
                    if (v > 1.0f) v = 1.0f;
                    if (v < -1.0f) v = -1.0f;
                    p[i] = (short)(v * 32767.0f);
                }
            }
            else
            {
                byte* p = (byte*)dst;
                for (int i = 0; i < count; i++)
                {
                    float v = samples[offset + i];
                    if (v > 1.0f) v = 1.0f;
                    if (v < -1.0f) v = -1.0f;
                    int s = (int)(v * 8388607.0f);
                    p[0] = (byte)s;
                    p[1] = (byte)(s >> 8);
                    p[2] = (byte)(s >> 16);
                    p += 3;
                }
            }

            WaveHdr hdr = (WaveHdr)Marshal.PtrToStructure(_headers[slot], typeof(WaveHdr));
            hdr.dwBufferLength = (uint)(count * _bytesPerSample);
            hdr.dwBytesRecorded = 0;
            Marshal.StructureToPtr(hdr, _headers[slot], false);

            waveOutWrite(_handle, _headers[slot], Marshal.SizeOf(typeof(WaveHdr)));
        }

        private void OnWaveOut(IntPtr hwo, uint msg, IntPtr instance, IntPtr param1, IntPtr param2)
        {
            if (msg != WOM_DONE) return;
            WaveHdr hdr = (WaveHdr)Marshal.PtrToStructure(param1, typeof(WaveHdr));
            lock (_lock) { _free[(int)hdr.dwUser] = true; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_handle != IntPtr.Zero)
            {
                waveOutReset(_handle);
                for (int i = 0; i < BufferCount; i++)
                    waveOutUnprepareHeader(_handle, _headers[i], Marshal.SizeOf(typeof(WaveHdr)));
                waveOutClose(_handle);
                _handle = IntPtr.Zero;
            }

            for (int i = 0; i < BufferCount; i++)
            {
                if (_data[i] != IntPtr.Zero) Marshal.FreeHGlobal(_data[i]);
                if (_headers[i] != IntPtr.Zero) Marshal.FreeHGlobal(_headers[i]);
            }

            _proc = null;
            GC.SuppressFinalize(this);
        }
    }
}
