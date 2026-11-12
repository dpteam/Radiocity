using System;

namespace Radiocity.Dsp
{
    /// <summary>
    /// Golay (23,12): 12 data bits, 11 check bits, corrects 3 errors.
    ///
    /// DCS uses this code because the word repeats continuously at 134.4 bps with
    /// no framing at all — the receiver has to find the 23-bit boundary from the
    /// data alone. That only works if a random 23-bit window is almost certain to
    /// be rejected, which is what a code with 11 check bits buys.
    ///
    /// (23,12) is a *perfect* code: there are exactly 2^11 = 2048 syndromes and
    /// exactly 2048 error patterns of weight 0-3 (1 + 23 + 253 + 1771). So every
    /// syndrome corresponds to exactly one correctable error, and the decoder is
    /// a single table lookup rather than a search. The table is built once.
    /// </summary>
    public static class Golay23
    {
        /// <summary>x^11 + x^10 + x^6 + x^5 + x^4 + x^2 + 1.</summary>
        public const int Poly = 0xC75;

        /// <summary>All 23 bits of a code word: 0x7FFFFF, not 0x7FFFF.</summary>
        public const int AllOnes = 0x7FFFFF;

        private static readonly int[] ErrorFor = BuildTable();

        /// <summary>11-bit remainder of a 23-bit word divided by the generator.</summary>
        public static int Syndrome(int word)
        {
            int reg = word & AllOnes;

            for (int i = 22; i >= 11; i--)
                if (((reg >> i) & 1) != 0) reg ^= Poly << (i - 11);

            return reg & 0x7FF;
        }

        /// <summary>Appends 11 check bits to a 12-bit data word.</summary>
        public static int Encode(int data12)
        {
            int shifted = (data12 & 0xFFF) << 11;
            return shifted | Syndrome(shifted);
        }

        public static int Weight(int v)
        {
            int n = 0;
            while (v != 0) { n += v & 1; v >>= 1; }
            return n;
        }

        /// <summary>
        /// Corrects a word and returns its 12 data bits. Always succeeds: for a
        /// perfect code every syndrome has a correction, which is exactly why
        /// validity has to be judged by the caller (see DcsDecoder).
        /// </summary>
        public static bool TryDecode(int word, out int data12, out int bitErrors)
        {
            int syndrome = Syndrome(word);
            int pattern = ErrorFor[syndrome];

            if (pattern < 0)
            {
                data12 = 0;
                bitErrors = 0;
                return false;
            }

            int corrected = word ^ pattern;
            data12 = (corrected >> 11) & 0xFFF;
            bitErrors = Weight(pattern);
            return true;
        }

        private static int[] BuildTable()
        {
            int[] table = new int[2048];
            for (int i = 0; i < table.Length; i++) table[i] = -1;

            table[0] = 0;

            for (int a = 0; a < 23; a++)
            {
                int p1 = 1 << a;
                table[Syndrome(p1)] = p1;

                for (int b = a + 1; b < 23; b++)
                {
                    int p2 = p1 | (1 << b);
                    table[Syndrome(p2)] = p2;

                    for (int c = b + 1; c < 23; c++)
                    {
                        int p3 = p2 | (1 << c);
                        table[Syndrome(p3)] = p3;
                    }
                }
            }

            return table;
        }
    }

    /// <summary>
    /// The standard DCS codes.
    ///
    /// They are written in octal because each digit is exactly three bits, and
    /// the code itself is a 9-bit value that fits into the 12 data bits of a
    /// Golay word with three leading zeros. That is the whole mapping: "023" is
    /// 2*8+3 = 19, and the transmitted data word is 19.
    ///
    /// The table is a convenience for looking a code up by name. The decoder
    /// reports whatever it decodes, so a code that is missing here still works —
    /// it is simply displayed as octal without a name.
    /// </summary>
    public static class Dcs
    {
        public static readonly string[] Codes =
        {
            "023", "025", "026", "031", "032", "036", "043", "047",
            "051", "053", "054", "065", "071", "072", "073", "074",
            "114", "115", "116", "122", "125", "131", "132", "134",
            "143", "145", "152", "155", "156", "162", "165", "172", "174",
            "205", "212", "223", "225", "226", "243", "244", "245",
            "246", "251", "252", "255", "261", "263", "265", "266", "271", "274",
            "306", "311", "315", "325", "331", "332", "343", "346",
            "351", "356", "364", "365", "371",
            "411", "412", "413", "423", "431", "432", "445", "446",
            "452", "454", "455", "462", "464", "465", "466",
            "503", "506", "516", "523", "526", "532", "546", "565",
            "606", "612", "624", "627", "631", "632", "654", "662", "664",
            "703", "712", "723", "731", "732", "734", "743", "754"
        };

        /// <summary>"023" to 19. Rejects anything that is not three octal digits.</summary>
        public static int Parse(string octal)
        {
            if (octal == null) throw new ArgumentNullException("octal");
            if (octal.Length == 0 || octal.Length > 3) throw new FormatException("DCS code must be 1-3 octal digits");

            int v = 0;
            for (int i = 0; i < octal.Length; i++)
            {
                char c = octal[i];
                if (c < '0' || c > '7') throw new FormatException("not an octal digit: " + c);
                v = v * 8 + (c - '0');
            }

            if (v > 0x1FF) throw new FormatException("DCS code must be below 1000 octal");
            return v;
        }

        /// <summary>19 to "023".</summary>
        public static string Format(int code)
        {
            return Convert.ToString(code & 0x1FF, 8).PadLeft(3, '0');
        }

        /// <summary>
        /// True when <paramref name="detected"/> is one of the codes a receiver can
        /// legitimately report for a transmission of <paramref name="wanted"/>.
        ///
        /// Golay (23,12) is a *cyclic* code, so a rotation of a code word is also a
        /// code word — and it decodes to a *different* code. Since DCS has no
        /// framing, a receiver cannot know which bit the word starts at, so the
        /// same transmission legitimately decodes to any of several codes
        /// depending on where the frame boundary happens to fall.
        ///
        /// That is a property of the code, not a defect to be fixed. The honest
        /// resolution is to accept any alignment of the transmission the user
        /// asked for, which is what this does: it enumerates the alignments of
        /// the wanted code and asks whether the detected one is among them.
        /// </summary>
        public static bool SameCode(int detected, int wanted)
        {
            if ((detected & 0x1FF) == (wanted & 0x1FF)) return true;

            int word = Golay23.Encode(wanted & 0x1FF);

            for (int k = 1; k < 23; k++)
            {
                int data;
                int errors;

                if (!Golay23.TryDecode(Rotate(word, k), out data, out errors)) continue;
                if ((data & 0xE00) != 0) continue;
                if ((data & 0x1FF) == (detected & 0x1FF)) return true;
            }

            return false;
        }

        private static int Rotate(int word, int k)
        {
            int mask = Golay23.AllOnes;
            if (k == 0) return word & mask;
            return ((word << k) | (word >> (23 - k))) & mask;
        }

        /// <summary>True when this is one of the published codes.</summary>
        public static bool IsStandard(int code)
        {
            string s = Format(code);
            for (int i = 0; i < Codes.Length; i++)
                if (Codes[i] == s) return true;
            return false;
        }
    }

    /// <summary>
    /// DCS decoder: 134.4 bps NRZ, Golay words, word sync from the code itself.
    ///
    /// The hard part is not decoding a word, it is knowing where a word starts.
    /// There is no preamble, no frame marker, no bit clock — just the same 23-bit
    /// code word repeating forever. Three things make it workable:
    ///
    ///  - The code corrects three errors, so a real codeword is very likely to
    ///    decode, and a random window is very unlikely to produce a *valid DCS*
    ///    word (Golay validity plus the top three data bits being zero).
    ///  - The word repeats, so a candidate alignment can be confirmed by seeing
    ///    the same position succeed 23 bits later, three times running.
    ///  - Bit timing comes from the data: an NRZ transition can only fall on a
    ///    bit boundary, so every edge is a free clock reference.
    ///
    /// Inverted codes ("DCS 023 inverted", written with a bar over the number)
    /// are the same word with every bit flipped. They are common enough that a
    /// receiver which ignores them looks broken on half of a repeater's channels.
    /// </summary>
    public sealed class DcsDecoder
    {
        public const double BitRateHz = 134.4;

        private readonly RealDecimator _lowPass;
        private readonly float[] _filtered;

        // Slice level tracked from peak-to-peak, not from the mean. A Golay word
        // is not balanced — some have twenty ones and three zeros — so the mean
        // of a repeating unbalanced word is nowhere near the middle of its two
        // levels, and slicing at the mean would read every bit as zero.
        private float _hi;
        private float _lo;
        private bool _haveLevels;
        private readonly float _peakDecay;

        private double _bitPhase;
        private readonly double _bitInc;
        private int _lastSlice;

        private float _acc;
        private int _accCount;

        private int _reg;
        private long _bitIndex;
        private bool _synced;
        private readonly int[] _hitsFor = new int[23];
        private int _sinceSync;
        private bool _invertedGuess;

        public int Code { get; private set; }
        public bool Inverted { get; private set; }
        public bool Synced { get { return _synced; } }

        public long Words { get; private set; }
        public long CorrectedBits { get; private set; }

        /// <summary>Fires whenever a decoded code changes. 0 means "lost".</summary>
        public event Action<int, bool> CodeChanged;

        public DcsDecoder(double rate)
        {
            if (rate < 8000)
                throw new NotSupportedException("DCS needs at least 8 kHz, got " + rate);

            _bitInc = BitRateHz / rate;

            // 150 Hz low-pass: the DCS fundamental is at half the bit rate, about
            // 67 Hz, so this keeps the waveform and rejects the voice above it.
            double cutoff = 150.0 / rate;
            if (cutoff > 0.45) cutoff = 0.45;

            int taps = (int)Math.Ceiling(62.0 / (2.285 * 2.0 * Math.PI * Math.Max(0.002, cutoff)));
            if (taps < 63) taps = 63;
            if (taps > 1023) taps = 1023;

            _lowPass = new RealDecimator(FirDesign.LowPass(cutoff * 0.75, taps, 8.6), 1);
            _filtered = new float[32768];

            // 2% per sample: a peak follower that decays slowly enough to ride
            // through a long run of identical bits, fast enough to follow a fade.
            _peakDecay = 0.00002f;
        }

        public void Process(float[] audio, int n)
        {
            int pos = 0;

            while (pos < n)
            {
                int chunk = Math.Min(n - pos, _filtered.Length);
                Slice(audio, pos, chunk);
                pos += chunk;
            }
        }

        private void Slice(float[] audio, int offset, int n)
        {
            int count = _lowPass.Process(Offset(audio, offset, n), n, _filtered);
            if (count <= 0) return;

            for (int k = 0; k < count; k++)
            {
                float v = _filtered[k];

                if (!_haveLevels)
                {
                    _hi = v;
                    _lo = v;
                    _haveLevels = true;
                }

                if (v > _hi) _hi = v;
                else _hi -= _peakDecay * (_hi - v);

                if (v < _lo) _lo = v;
                else _lo += _peakDecay * (v - _lo);

                float mid = (_hi + _lo) * 0.5f;
                int slice = v > mid ? 1 : 0;

                // Every NRZ transition is a bit boundary, so it doubles as a clock
                // reference. Nudging the phase toward it keeps the sampler centred
                // without any separate timing loop.
                if (_lastSlice >= 0 && slice != _lastSlice)
                {
                    double err = _bitPhase;
                    if (err > 0.5) err -= 1.0;

                    _bitPhase -= 0.15 * err;
                    if (_bitPhase < 0.0) _bitPhase += 1.0;
                    if (_bitPhase >= 1.0) _bitPhase -= 1.0;
                }

                _lastSlice = slice;

                _acc += slice;
                _accCount++;

                _bitPhase += _bitInc;

                if (_bitPhase >= 1.0)
                {
                    _bitPhase -= 1.0;
                    OnBit(_acc * 2.0f >= _accCount ? 1 : 0);
                    _acc = 0.0f;
                    _accCount = 0;
                }
            }
        }

        private static float[] Offset(float[] src, int offset, int n)
        {
            if (offset == 0) return src;

            float[] tmp = new float[n];
            Array.Copy(src, offset, tmp, 0, n);
            return tmp;
        }

        private void OnBit(int bit)
        {
            _reg = ((_reg << 1) | bit) & Golay23.AllOnes;
            _bitIndex++;

            if (!_synced)
            {
                if (!IsValidDcsWord(_reg)) return;

                // Count hits per alignment, not per bit. The stream is periodic,
                // so *every* alignment produces a valid word every 23 bits; the
                // counters simply confirm the period, and the first alignment to
                // reach three is as good as any other. Which rotation it lands on
                // does not matter, because codes are compared by rotational class
                // (see Dcs.SameCode).
                int align = (int)(_bitIndex % 23);
                _hitsFor[align]++;

                if (_hitsFor[align] >= 3)
                {
                    _synced = true;
                    _sinceSync = 0;
                    DecodeCurrent();
                }

                return;
            }

            if (++_sinceSync >= 23)
            {
                _sinceSync = 0;

                if (!IsValidDcsWord(_reg))
                {
                    _synced = false;
                    Array.Clear(_hitsFor, 0, _hitsFor.Length);
                    Publish(0, false);
                    return;
                }

                DecodeCurrent();
            }
        }

        /// <summary>
        /// A valid DCS word decodes with few errors *and* leaves the top three of
        /// the twelve data bits zero, in either polarity.
        ///
        /// Both conditions matter: Golay validity alone is common enough to sync
        /// on noise, and the code is cyclic, so validity alone does not even
        /// narrow the alignment.
        /// </summary>
        private bool IsValidDcsWord(int word)
        {
            return IsValidPolarity(word) || IsValidPolarity((~word) & Golay23.AllOnes);
        }

        private static bool IsValidPolarity(int word)
        {
            int data;
            int errors;

            if (!Golay23.TryDecode(word, out data, out errors)) return false;
            if ((data & 0xE00) != 0) return false;
            return errors <= 3;
        }

        private void DecodeCurrent()
        {
            int normalData, normalErrors;
            int invertedData, invertedErrors;

            bool normalOk = Golay23.TryDecode(_reg, out normalData, out normalErrors) &&
                            (normalData & 0xE00) == 0;

            int flipped = (~_reg) & Golay23.AllOnes;

            bool invertedOk = Golay23.TryDecode(flipped, out invertedData, out invertedErrors) &&
                              (invertedData & 0xE00) == 0;

            bool inverted;

            if (normalOk && !invertedOk) inverted = false;
            else if (!normalOk && invertedOk) inverted = true;
            else if (normalOk && invertedOk)
            {
                // Both interpretations are possible after errors. Prefer the one
                // that needed fewer corrections; on a tie keep the previous
                // decision, because the code repeats and continuity is evidence.
                if (normalErrors < invertedErrors) inverted = false;
                else if (invertedErrors < normalErrors) inverted = true;
                else inverted = _invertedGuess;
            }
            else return;

            int data = inverted ? invertedData : normalData;
            int errors = inverted ? invertedErrors : normalErrors;

            _invertedGuess = inverted;
            Words++;
            CorrectedBits += errors;

            Publish(data & 0x1FF, inverted);
        }

        private void Publish(int code, bool inverted)
        {
            if (code == Code && inverted == Inverted) return;

            Code = code;
            Inverted = inverted;

            Action<int, bool> handler = CodeChanged;
            if (handler != null) handler(code, inverted);
        }

        public void Reset()
        {
            _lowPass.Reset();
            _haveLevels = false;
            _hi = 0.0f;
            _lo = 0.0f;
            _bitPhase = 0.0;
            _lastSlice = -1;
            _acc = 0.0f;
            _accCount = 0;
            _reg = 0;
            _bitIndex = 0;
            _synced = false;
            Array.Clear(_hitsFor, 0, _hitsFor.Length);
            _sinceSync = 0;
            _invertedGuess = false;
            Code = 0;
            Inverted = false;
            Words = 0;
            CorrectedBits = 0;
        }
    }

    /// <summary>
    /// DCS encoder, for the self-test and the synthetic radio source. Built from
    /// the same Golay code as the decoder on purpose: if the two disagree, the
    /// test fails.
    /// </summary>
    public static class DcsEncoder
    {
        /// <summary>23 bits of the code word, most significant bit first.</summary>
        public static int[] Bits(int code, bool inverted)
        {
            int word = Golay23.Encode(code & 0x1FF);
            if (inverted) word = (~word) & Golay23.AllOnes;

            int[] bits = new int[23];
            for (int i = 0; i < 23; i++) bits[i] = (word >> (22 - i)) & 1;

            return bits;
        }
    }
}
