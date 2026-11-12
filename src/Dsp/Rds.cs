using System;
using System.Text;

namespace Radiocity.Dsp
{
    /// <summary>
    /// RDS framing constants and the block code.
    ///
    /// Every RDS block is 26 bits: 16 of data followed by a 10-bit checkword.
    /// The checkword is the CRC remainder of the data (polynomial
    /// x^10 + x^8 + x^7 + x^5 + x^4 + x^3 + 1) XORed with an *offset word* that
    /// identifies the block's position in the group. That is why a receiver can
    /// find block sync without any framing preamble: the offset word tells it
    /// which block it is looking at.
    /// </summary>
    public static class RdsCode
    {
        /// <summary>x^10 + x^8 + x^7 + x^5 + x^4 + x^3 + 1, as 11 bits.</summary>
        public const int Poly = 0x5B9;

        public const int OffsetA = 0x0FC;
        public const int OffsetB = 0x198;
        public const int OffsetC = 0x168;
        public const int OffsetCPrime = 0x350;
        public const int OffsetD = 0x1B4;

        public const double BitRateHz = 1187.5;
        public const double PilotHz = 19000.0;
        public const double SubcarrierHz = 57000.0;

        /// <summary>Remainder of a 26-bit block divided by the generator polynomial.</summary>
        public static int Syndrome(int block)
        {
            int reg = block & 0x3FFFFFF;

            for (int i = 25; i >= 10; i--)
            {
                if (((reg >> i) & 1) != 0) reg ^= Poly << (i - 10);
            }

            return reg & 0x3FF;
        }

        /// <summary>The checkword a transmitter would append to this data word.</summary>
        public static int Checkword(int data16)
        {
            return Syndrome(data16 << 10);
        }

        /// <summary>
        /// Repairs a block that arrived with up to two flipped bits.
        ///
        /// Two bits, not five: the full RDS corrector uses a syndrome table and
        /// handles five, but the table costs more memory than the rest of this
        /// decoder and two bits covers the great majority of real errors. Three
        /// or more and the block is dropped, which the group parser already
        /// tolerates.
        /// </summary>
        public static bool TryCorrect(int block, int expectedOffset, out int corrected, out int bitErrors)
        {
            corrected = block;
            bitErrors = 0;

            if (Syndrome(block) == expectedOffset) return true;

            for (int i = 0; i < 26; i++)
            {
                int t = block ^ (1 << i);
                if (Syndrome(t) == expectedOffset)
                {
                    corrected = t;
                    bitErrors = 1;
                    return true;
                }
            }

            for (int i = 0; i < 26; i++)
            {
                for (int j = i + 1; j < 26; j++)
                {
                    int t = block ^ (1 << i) ^ (1 << j);
                    if (Syndrome(t) == expectedOffset)
                    {
                        corrected = t;
                        bitErrors = 2;
                        return true;
                    }
                }
            }

            return false;
        }
    }

    /// <summary>
    /// A decoded RDS group: four 16-bit words plus the header fields.
    ///
    /// Group type and version (A/B) come from the top four bits of block B and
    /// decide how the rest is interpreted. Type 0 carries the station name,
    /// type 2 the scrolling text, type 4 the clock. Others are mostly
    /// programme-specific and are counted but not parsed.
    /// </summary>
    public sealed class RdsGroup
    {
        public int A;
        public int B;
        public int C;
        public int D;

        public int Type { get { return (B >> 12) & 0x0F; } }
        public bool VersionB { get { return ((B >> 11) & 1) == 1; } }
        public bool TrafficProgram { get { return ((B >> 10) & 1) == 1; } }
        public int Pty { get { return (B >> 5) & 0x1F; } }
        public bool TrafficAnnouncement { get { return ((B >> 4) & 1) == 1; } }
        public bool Music { get { return ((B >> 3) & 1) == 1; } }

        public int Segment { get { return B & (Type == 0 ? 0x03 : 0x0F); } }

        /// <summary>PI code: the station identifier, same in every group.</summary>
        public int Pi { get { return A; } }

        public override string ToString()
        {
            return "type " + Type + (VersionB ? "B" : "A") +
                   ", PI " + Pi.ToString("X4") + ", PTY " + Pty;
        }
    }

    /// <summary>
    /// RDS decoder: 57 kHz subcarrier, biphase symbols, differential decoding,
    /// block sync and group parsing.
    ///
    /// The subcarrier is the third harmonic of the 19 kHz stereo pilot, which is
    /// not a coincidence — it lets a receiver lock RDS to a signal it already
    /// has. The pilot phasor p gives the correction directly:
    ///
    ///     e^{j(3*theta + 3*psi)} = e^{j*3*theta} * (p/|p|)^3
    ///
    /// Cubing, not squaring: this is three times the pilot frequency, so three
    /// times the phase error. (Stereo needs the square; RDS needs the cube.)
    ///
    /// Data rate is 1187.5 bps, which is 57000/48 — exactly 48 cycles of the
    /// subcarrier per bit, so the whole scheme is phase-coherent by design.
    /// </summary>
    public sealed class RdsDecoder
    {
        private const int TargetRate = 9600;

        private readonly double _rate;
        private readonly RealDecimator[] _stages;
        private readonly float[][] _stageI;
        private readonly float[][] _stageQ;
        private readonly float[] _baseband;
        private readonly float[] _decimated;

        private readonly float _pilotAlpha;
        private double _pilotPhase;
        private readonly double _pilotInc;
        private float _pilotI;
        private float _pilotQ;

        private double _bitPhase;
        private double _bitInc;

        /// <summary>Tracking gain per bit, once the offset has been found.</summary>

        private float _accMain;

        private int _reg;
        private int _bitsInBlock;
        private bool _synced;
        private int _expectedOffset;
        private int _blocksInGroup;
        private int _failStreak;
        private readonly int[] _groupWords = new int[4];

        private readonly int[] _psChars = new int[8];
        private readonly bool[] _psSeen = new bool[8];
        private readonly char[] _rtChars = new char[64];
        private readonly bool[] _rtSeen = new bool[64];

        /// <summary>Station identifier, or 0 until the first valid block.</summary>
        public int PiCode { get; private set; }

        /// <summary>Programme type code, 0-31.</summary>
        public int Pty { get; private set; }

        /// <summary>Programme service name, 8 characters. Empty until complete.</summary>
        public string ProgramService { get; private set; }

        /// <summary>
        /// RadioText, up to 64 characters.
        ///
        /// Only the contiguous prefix that has actually arrived is returned. A
        /// transmitter sends the text in sixteen four-character segments in
        /// rotation, and a segment can be missed when a block fails its check —
        /// so the received set is full of holes. Rendering those holes as NUL
        /// characters produces the kind of output that makes a bug report say
        /// "SYNTH????????????????", which helps nobody. Stopping at the first
        /// gap is honest: what you see is what arrived, in order.
        /// </summary>
        public string RadioText
        {
            get
            {
                int len = 0;
                while (len < _rtChars.Length && _rtSeen[len]) len++;
                if (len == 0) return string.Empty;

                // 0x0D marks end-of-text; anything after it is padding the
                // transmitter never intended to be displayed.
                for (int i = 0; i < len; i++)
                    if (_rtChars[i] == '\r') { len = i; break; }

                while (len > 0 && _rtChars[len - 1] == ' ') len--;

                return new string(_rtChars, 0, len);
            }
        }

        /// <summary>
        /// True when the text has arrived in full, so <see cref="RadioText"/> is
        /// complete rather than merely the part that survived.
        ///
        /// "In full" means either the end-of-text marker was reached with no gaps
        /// before it, or all 64 characters arrived. A stray segment near the end
        /// of the buffer is not enough: with a drifting clock the decoder can lock
        /// onto the wrong alignment and fill the tail with noise, and reporting
        /// that as complete would make a caller act on rubbish.
        /// </summary>
        public bool RadioTextComplete
        {
            get
            {
                for (int i = 0; i < _rtChars.Length; i++)
                {
                    if (!_rtSeen[i]) return false;
                    if (_rtChars[i] == '\r') return true;
                }

                return true;
            }
        }

        /// <summary>True when the 57 kHz subcarrier is present and blocks decode.</summary>
        public bool Synced { get { return _synced; } }

        public long Groups { get; private set; }
        public long BadBlocks { get; private set; }
        public long CorrectedBits { get; private set; }

        public event Action<RdsGroup> GroupDecoded;

        public RdsDecoder(double rate)
        {
            if (rate < 32000)
                throw new NotSupportedException("RDS needs at least 32 kHz of baseband, got " + rate);

            _rate = rate;

            // 100 Hz around the pilot: wide enough to follow a transmitter a few
            // Hz off nominal, narrow enough to reject the audio 4 kHz away.
            _pilotAlpha = (float)(1.0 - Math.Exp(-2.0 * Math.PI * 100.0 / rate));
            _pilotInc = 2.0 * Math.PI * RdsCode.PilotHz / rate;

            // Decimate in stages: one filter going straight from 192 kHz to 9.6 kHz
            // would need hundreds of taps at the full rate for no benefit.
            int total = Math.Max(1, (int)Math.Round(rate / TargetRate));
            int[] factors = SplitDecimation(total);

            _stages = new RealDecimator[factors.Length];
            _stageI = new float[factors.Length][];
            _stageQ = new float[factors.Length][];

            double stageRate = rate;
            int cum = 1;

            for (int s = 0; s < factors.Length; s++)
            {
                int d = factors[s];
                cum *= d;

                double pass = 2400.0 / stageRate;
                double stop = 0.5 / d;
                int taps = (stop - pass < 0.002)
                    ? 63
                    : (int)Math.Ceiling(62.0 / (2.285 * 2.0 * Math.PI * (stop - pass)));

                if (taps < 31) taps = 31;
                if (taps > 1023) taps = 1023;

                _stages[s] = new RealDecimator(FirDesign.LowPass((pass + stop) * 0.5, taps, 8.6), d);
                _stageI[s] = new float[32768];
                _stageQ[s] = new float[32768];

                stageRate = rate / cum;
            }

            DecimatedRate = (int)Math.Round(rate / cum);
            _bitInc = RdsCode.BitRateHz / DecimatedRate;

            _baseband = new float[32768];
            _decimated = new float[32768];
        }

        public int DecimatedRate { get; private set; }

        public void Process(float[] composite, int n)
        {
            // Chunk internally: callers hand over whatever block size their
            // capture produced, and the fixed working buffers are smaller.
            int pos = 0;
            while (pos < n)
            {
                int chunk = Math.Min(n - pos, _baseband.Length);
                ProcessChunk(composite, pos, chunk);
                pos += chunk;
            }
        }

        private void ProcessChunk(float[] composite, int offset, int n)
        {
            // Mix the 57 kHz subcarrier down to DC, using a reference built from
            // the pilot so it stays phase-locked to the transmitter.
            for (int k = 0; k < n; k++)
            {
                float x = composite[offset + k];

                double c = Math.Cos(_pilotPhase);
                double s = Math.Sin(_pilotPhase);
                _pilotI += _pilotAlpha * (float)(x * c - _pilotI);
                _pilotQ += _pilotAlpha * (float)(x * s - _pilotQ);

                double theta3 = 3.0 * _pilotPhase;
                double cos3 = Math.Cos(theta3);
                double sin3 = Math.Sin(theta3);

                double ref3;
                float mag2 = _pilotI * _pilotI + _pilotQ * _pilotQ;

                if (mag2 > 1e-20f)
                {
                    // (p/|p|)^3 = cos(3*psi) + j*sin(3*psi)
                    double pI = _pilotI;
                    double pQ = _pilotQ;
                    double m = Math.Sqrt(mag2);
                    double m3 = mag2 * m;

                    double re = (pI * pI * pI - 3.0 * pI * pQ * pQ) / m3;
                    double im = (3.0 * pI * pI * pQ - pQ * pQ * pQ) / m3;

                    ref3 = cos3 * re - sin3 * im;
                }
                else
                {
                    ref3 = cos3;
                }

                _baseband[k] = (float)(x * ref3);

                _pilotPhase += _pilotInc;
                if (_pilotPhase > 2.0 * Math.PI) _pilotPhase -= 2.0 * Math.PI;
            }

            float[] cur = _baseband;
            int count = n;

            for (int s = 0; s < _stages.Length; s++)
            {
                bool last = (s == _stages.Length - 1);
                float[] dst = last ? _decimated : _stageI[s];
                count = _stages[s].Process(cur, count, dst);
                cur = dst;
            }

            DecodeBits(cur, count);
        }

        private void DecodeBits(float[] x, int n)
        {
            for (int k = 0; k < n; k++)
            {
                double p = Wrap(_bitPhase);
                float v = x[k];

                // Biphase: within one bit the reference is +1 then -1. Correlating
                // against that reference turns the transition itself into the bit.
                _accMain += v * ((p < 0.5) ? 1.0f : -1.0f);

                _bitPhase += _bitInc;

                if (_bitPhase >= 1.0)
                {
                    _bitPhase -= 1.0;
                    EmitBit(_accMain > 0.0f);
                    _accMain = 0.0f;
                }
            }
        }

        private static double Wrap(double p)
        {
            if (p < 0.0) p += 1.0;
            if (p >= 1.0) p -= 1.0;
            return p;
        }

        private int _prevEncoded = -1;

        private void EmitBit(bool encoded)
        {
            // Differential decoding: what arrives is the XOR of consecutive data
            // bits, so the data is the XOR of consecutive received bits. This is
            // what makes an inverted demodulator (180 degrees of phase) harmless.
            int e = encoded ? 1 : 0;
            if (_prevEncoded < 0) { _prevEncoded = e; return; }

            int bit = e ^ _prevEncoded;
            _prevEncoded = e;

            _reg = ((_reg << 1) | bit) & 0x3FFFFFF;

            if (!_synced)
            {
                if (RdsCode.Syndrome(_reg) == RdsCode.OffsetA)
                {
                    _synced = true;
                    _failStreak = 0;
                    _bitsInBlock = 0;
                    _blocksInGroup = 0;
                    _expectedOffset = RdsCode.OffsetB;
                    AcceptBlock(_reg, 0);
                }
                return;
            }

            if (++_bitsInBlock < 26) return;
            _bitsInBlock = 0;

            int corrected;
            int errors;

            if (RdsCode.TryCorrect(_reg, _expectedOffset, out corrected, out errors))
            {
                _failStreak = 0;
                CorrectedBits += errors;
                AcceptBlock(corrected, errors);
                return;
            }

            BadBlocks++;

            // One bad block is not proof that the alignment is wrong: a burst of
            // noise, a click, a moment of multipath. Dropping sync on the first
            // failure costs a whole group plus the three words needed to re-sync,
            // and with a transmitter whose clock is even slightly off that happens
            // often enough to dominate the error budget. Two consecutive failures
            // is a real misalignment.
            if (++_failStreak >= 2)
            {
                _synced = false;
                _failStreak = 0;
                _blocksInGroup = 0;
            }
        }

        private void AcceptBlock(int block, int errors)
        {
            int data = (block >> 10) & 0xFFFF;
            _groupWords[_blocksInGroup] = data;

            _blocksInGroup++;

            if (_blocksInGroup == 4)
            {
                _blocksInGroup = 0;
                _expectedOffset = RdsCode.OffsetA;
                HandleGroup();
                return;
            }

            // A, B, C, D: version B uses a different offset for block C, and that
            // is only known after block B has been read.
            switch (_blocksInGroup)
            {
                case 1:
                    _expectedOffset = RdsCode.OffsetB;
                    break;
                case 2:
                    {
                        bool versionB = ((_groupWords[1] >> 11) & 1) == 1;
                        _expectedOffset = versionB ? RdsCode.OffsetCPrime : RdsCode.OffsetC;
                    }
                    break;
                case 3:
                    _expectedOffset = RdsCode.OffsetD;
                    break;
            }
        }

        private void HandleGroup()
        {
            RdsGroup g = new RdsGroup();
            g.A = _groupWords[0];
            g.B = _groupWords[1];
            g.C = _groupWords[2];
            g.D = _groupWords[3];

            // Every RDS group carries the same programme identifier. A group whose
            // PI differs from the one we are locked to was decoded at the wrong
            // alignment — a valid syndrome is easy to hit when the clock has
            // drifted — and its payload is noise. Rejecting it costs nothing and
            // is the difference between a receiver that shows a short station name
            // and one that shows "RA??OCITY SOUND CARDER??".
            if (PiCode != 0 && g.Pi != PiCode)
            {
                BadBlocks++;
                return;
            }

            Groups++;

            if (g.Pi != 0) PiCode = g.Pi;
            Pty = g.Pty;

            switch (g.Type)
            {
                case 0: HandlePs(g); break;
                case 2: HandleRadioText(g); break;
            }

            Action<RdsGroup> handler = GroupDecoded;
            if (handler != null) handler(g);
        }

        /// <summary>Type 0: two characters of the 8-character station name per group.</summary>
        private void HandlePs(RdsGroup g)
        {
            int seg = g.Segment & 0x03;
            int a = (g.D >> 8) & 0xFF;
            int b = g.D & 0xFF;

            _psChars[seg * 2] = Printable((char)a);
            _psChars[seg * 2 + 1] = Printable((char)b);
            _psSeen[seg * 2] = true;
            _psSeen[seg * 2 + 1] = true;

            for (int i = 0; i < 8; i++)
                if (!_psSeen[i]) return;

            StringBuilder sb = new StringBuilder(8);
            for (int i = 0; i < 8; i++) sb.Append((char)_psChars[i]);
            ProgramService = sb.ToString();
        }

        /// <summary>Type 2: four characters of RadioText per group, 16 groups.</summary>
        private void HandleRadioText(RdsGroup g)
        {
            int start = (g.Segment & 0x0F) * 4;
            if (start + 4 > _rtChars.Length) return;

            _rtChars[start] = Printable((char)((g.C >> 8) & 0xFF));
            _rtChars[start + 1] = Printable((char)(g.C & 0xFF));
            _rtChars[start + 2] = Printable((char)((g.D >> 8) & 0xFF));
            _rtChars[start + 3] = Printable((char)(g.D & 0xFF));

            _rtSeen[start] = true;
            _rtSeen[start + 1] = true;
            _rtSeen[start + 2] = true;
            _rtSeen[start + 3] = true;
        }

        /// <summary>
        /// Keeps the display to characters that can be displayed.
        ///
        /// RDS defines its own character set, but a group decoded at the wrong
        /// alignment produces bytes that are not in it — and a terminal, a JSON
        /// response and a PowerShell string all handle control characters badly.
        /// Anything outside printable ASCII becomes a space, which at worst looks
        /// like a gap and never like a broken receiver.
        /// </summary>
        private static char Printable(char c)
        {
            return (c >= 0x20 && c < 0x7F) ? c : ' ';
        }

        public void Reset()
        {
            for (int s = 0; s < _stages.Length; s++) _stages[s].Reset();

            _pilotI = 0.0f;
            _pilotQ = 0.0f;
            _pilotPhase = 0.0;

            _bitPhase = 0.0;
            _accMain = 0.0f;

            _reg = 0;
            _bitsInBlock = 0;
            _synced = false;
            _expectedOffset = RdsCode.OffsetA;
            _blocksInGroup = 0;
            _failStreak = 0;
            _prevEncoded = -1;

            Array.Clear(_psSeen, 0, _psSeen.Length);
            Array.Clear(_rtSeen, 0, _rtSeen.Length);
            ProgramService = null;
        }

        /// <summary>Factors of the decimation, each at most 8.</summary>
        internal static int[] SplitDecimation(int total)
        {
            if (total <= 8) return new int[] { total };

            System.Collections.Generic.List<int> parts = new System.Collections.Generic.List<int>();
            int remaining = total;

            while (remaining > 8)
            {
                int f = 8;
                while (f > 2 && (remaining % f) != 0) f--;
                if ((remaining % f) != 0) f = 2;

                parts.Add(f);
                remaining /= f;
            }

            if (remaining > 1) parts.Add(remaining);
            return parts.ToArray();
        }
    }

    /// <summary>
    /// RDS encoder, used by the self-test and by the synthetic FM source.
    ///
    /// Written from the same constants as the decoder on purpose: if the two
    /// disagree the tests fail, which is the point. What it does *not* prove is
    /// that either matches the standard — that is why the group layout is
    /// documented field by field rather than trusted.
    /// </summary>
    public static class RdsEncoder
    {
        /// <summary>Builds the 26-bit block for a data word and an offset.</summary>
        public static int Block(int data16, int offset)
        {
            int check = RdsCode.Checkword(data16) ^ offset;
            return ((data16 & 0xFFFF) << 10) | (check & 0x3FF);
        }

        /// <summary>
        /// Turns four data words into the differentially encoded bit stream that
        /// goes on the air: 4 blocks x 26 bits, each bit XORed with the previous
        /// one.
        /// </summary>
        public static int[] EncodeBits(int[] dataWords)
        {
            int[] offsets = { RdsCode.OffsetA, RdsCode.OffsetB, RdsCode.OffsetC, RdsCode.OffsetD };
            int[] bits = new int[4 * 26];

            int n = 0;
            int prev = 0;

            for (int w = 0; w < 4; w++)
            {
                int block = Block(dataWords[w], offsets[w]);

                for (int b = 25; b >= 0; b--)
                {
                    int dataBit = (block >> b) & 1;
                    int encoded = dataBit ^ prev;
                    prev = encoded;
                    bits[n++] = encoded;
                }
            }

            return bits;
        }

        /// <summary>
        /// Renders a bit stream as a 57 kHz biphase signal and adds it to a
        /// buffer of composite samples.
        ///
        /// Bit boundaries are computed in floating point and rounded, so the
        /// 1187.5 bps rate works at any sample rate without accumulating drift.
        /// </summary>
        public static void Render(float[] composite, double sampleRate, int[] encodedBits, double amplitude)
        {
            double samplesPerBit = sampleRate / RdsCode.BitRateHz;

            for (int i = 0; i < encodedBits.Length; i++)
            {
                int from = (int)Math.Round(i * samplesPerBit);
                int to = (int)Math.Round((i + 1) * samplesPerBit);
                int mid = (from + to) / 2;

                bool one = encodedBits[i] == 1;

                for (int s = from; s < to; s++)
                {
                    if (s < 0 || s >= composite.Length) continue;

                    bool firstHalf = s < mid;

                    // Biphase: a 1 is +1 then -1, a 0 is -1 then +1.
                    double sign = one ? (firstHalf ? 1.0 : -1.0) : (firstHalf ? -1.0 : 1.0);

                    double t = (double)s / sampleRate;
                    composite[s] += (float)(amplitude * sign *
                        Math.Cos(2.0 * Math.PI * RdsCode.SubcarrierHz * t));
                }
            }
        }
    }
}
