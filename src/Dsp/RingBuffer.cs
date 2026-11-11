using System;
using System.Threading;

namespace Radiocity.Dsp
{
    /// <summary>
    /// Single-producer / single-consumer lock-free ring buffer of float samples.
    /// Capacity must be a power of two. Never allocates after construction, so it is
    /// safe to use inside an audio callback (the producer side).
    /// </summary>
    public sealed class RingBuffer
    {
        private readonly float[] _buf;
        private readonly int _mask;
        private int _write;
        private int _read;

        public RingBuffer(int capacityPow2)
        {
            if (capacityPow2 < 2 || (capacityPow2 & (capacityPow2 - 1)) != 0)
                throw new ArgumentException("capacity must be a power of two", "capacityPow2");

            _buf = new float[capacityPow2];
            _mask = capacityPow2 - 1;
        }

        public int Capacity { get { return _buf.Length; } }

        public int Count
        {
            get { return Volatile.Read(ref _write) - Volatile.Read(ref _read); }
        }

        public int Free { get { return Capacity - Count; } }

        /// <summary>Writes as many samples as fit. Returns the number actually written.</summary>
        public int Write(float[] src, int offset, int count)
        {
            int w = _write;
            int free = Capacity - (w - Volatile.Read(ref _read));
            if (count > free) count = free;
            if (count <= 0) return 0;

            int idx = w & _mask;
            int first = Math.Min(count, Capacity - idx);
            Array.Copy(src, offset, _buf, idx, first);
            if (count > first)
                Array.Copy(src, offset + first, _buf, 0, count - first);

            Volatile.Write(ref _write, w + count);
            return count;
        }

        /// <summary>Reads as many samples as are available, up to count.</summary>
        public int Read(float[] dst, int offset, int count)
        {
            int r = _read;
            int avail = Volatile.Read(ref _write) - r;
            if (count > avail) count = avail;
            if (count <= 0) return 0;

            int idx = r & _mask;
            int first = Math.Min(count, Capacity - idx);
            Array.Copy(_buf, idx, dst, offset, first);
            if (count > first)
                Array.Copy(_buf, 0, dst, offset + first, count - first);

            Volatile.Write(ref _read, r + count);
            return count;
        }

        public void Clear()
        {
            Volatile.Write(ref _read, 0);
            Volatile.Write(ref _write, 0);
        }
    }
}
