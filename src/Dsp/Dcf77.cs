using System;
using System.Text;

namespace Radiocity.Dsp
{
    /// <summary>A decoded DCF77 time telegram.</summary>
    public sealed class Dcf77Frame
    {
        public int Year;
        public int Month;
        public int Day;
        public int Hour;
        public int Minute;
        public int DayOfWeek;          // 1 = Monday .. 7 = Sunday
        public bool SummerTime;
        public bool Valid;
        public string Error = string.Empty;
        public int[] Bits;

        public DateTime ToDateTime()
        {
            return new DateTime(Year, Month, Day, Hour, Minute, 0, DateTimeKind.Unspecified);
        }

        public override string ToString()
        {
            if (!Valid) return "invalid frame: " + Error;

            return string.Format("{0:0000}-{1:00}-{2:00} {3:00}:{4:00} {5} ({6})",
                Year, Month, Day, Hour, Minute,
                SummerTime ? "CEST" : "CET",
                DayName(DayOfWeek));
        }

        private static string DayName(int dow)
        {
            string[] names = { "?", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
            if (dow < 1 || dow > 7) return "?";
            return names[dow];
        }
    }

    /// <summary>
    /// DCF77 bit layout. 59 bits, one per second, second 59 is the minute marker
    /// (no amplitude dip at all).
    ///
    ///   0-14  weather / civil warning (encrypted since 2007, useless)
    ///  15-19  call bit, DST announcement, CET/CEST, leap second announcement, start of time
    ///  20     start of encoded time (always 1)
    ///  21-27  minute BCD, 28 = even parity over 21-28
    ///  29-34  hour BCD,   35 = even parity over 29-35
    ///  36-41  day BCD, 42 = parity over 36-42
    ///  43-44  day of week
    ///  45-49  month BCD
    ///  50-57  year BCD (last two digits)
    ///  58     parity over 36-58
    /// </summary>
    public static class Dcf77Code
    {
        public const int BitCount = 59;

        /// <summary>
        /// Builds the 59-bit telegram for a CET/CEST wall-clock time.
        /// Real DCF77 transmits the *following* minute, so pass the time you want
        /// the receiver to display.
        /// </summary>
        public static int[] Encode(DateTime cet)
        {
            int[] b = new int[BitCount];

            b[15] = 0;                                  // normal operation, no call bit
            b[16] = 0;                                  // no DST change announcement
            b[17] = cet.IsDaylightSavingTime() ? 1 : 0; // 1 = CEST, 0 = CET
            b[18] = 0;                                  // no leap second
            b[19] = 1;                                  // start of encoded time
            b[20] = 1;

            // BCD, not binary: each field is one bit per weight of the decimal
            // digit. Writing (hour / 2) % 2 instead of (hour % 10) >> 1 looks
            // right for 8 o'clock and is wrong for 14.
            PackBcd(b, 21, cet.Minute, 4, 3);           // 1 2 4 8 | 10 20 40
            b[28] = Parity(b, 21, 27);

            PackBcd(b, 29, cet.Hour, 4, 2);             // 1 2 4 8 | 10 20
            b[35] = Parity(b, 29, 34);

            PackBcd(b, 36, cet.Day, 4, 2);              // 1 2 4 8 | 10 20
            b[42] = Parity(b, 36, 41);

            // DCF77 numbers Monday as 1; .NET has Sunday as 0.
            int dow = ((int)cet.DayOfWeek + 6) % 7 + 1;
            b[43] = dow % 2;
            b[44] = (dow / 2) % 2;

            PackBcd(b, 45, cet.Month, 4, 1);            // 1 2 4 8 | 10
            PackBcd(b, 50, cet.Year % 100, 4, 4);       // 1 2 4 8 | 10 20 40 80

            b[58] = Parity(b, 36, 57);

            return b;
        }

        /// <summary>Packs a two-digit decimal number as BCD, least significant bit first.</summary>
        private static void PackBcd(int[] b, int offset, int value, int unitBits, int tenBits)
        {
            int units = value % 10;
            int tens = value / 10;

            for (int i = 0; i < unitBits; i++) b[offset + i] = (units >> i) & 1;
            for (int i = 0; i < tenBits; i++) b[offset + unitBits + i] = (tens >> i) & 1;
        }

        public static Dcf77Frame Decode(int[] b)
        {
            Dcf77Frame f = new Dcf77Frame();
            f.Bits = b;

            if (b == null || b.Length < BitCount)
            {
                f.Error = "short telegram";
                return f;
            }

            if (b[20] != 1)
            {
                f.Error = "start-of-time bit missing";
                return f;
            }

            if (!ParityOk(b, 21, 28)) { f.Error = "minute parity"; return f; }
            if (!ParityOk(b, 29, 35)) { f.Error = "hour parity"; return f; }
            if (!ParityOk(b, 36, 58)) { f.Error = "date parity"; return f; }

            int minute = Bcd(b, 21, 1, 2, 4, 8) + Bcd(b, 25, 10, 20, 40);
            int hour = Bcd(b, 29, 1, 2, 4, 8) + Bcd(b, 33, 10, 20);
            int day = Bcd(b, 36, 1, 2, 4, 8) + Bcd(b, 40, 10, 20);
            int month = Bcd(b, 45, 1, 2, 4, 8) + Bcd(b, 49, 10);
            int year = Bcd(b, 50, 1, 2, 4, 8) + Bcd(b, 54, 10, 20, 40, 80);
            int dow = b[43] * 1 + b[44] * 2;

            if (minute > 59) { f.Error = "minute out of range"; return f; }
            if (hour > 23) { f.Error = "hour out of range"; return f; }
            if (month < 1 || month > 12) { f.Error = "month out of range"; return f; }
            if (day < 1 || day > 31) { f.Error = "day out of range"; return f; }

            f.Year = 2000 + year;
            f.Month = month;
            f.Day = day;
            f.Hour = hour;
            f.Minute = minute;
            f.DayOfWeek = dow;
            f.SummerTime = b[17] == 1;

            try
            {
                DateTime check = new DateTime(f.Year, f.Month, f.Day, f.Hour, f.Minute, 0);
                // DCF77 day-of-week is 1=Monday..7=Sunday; .NET is 0=Sunday..6=Saturday.
                if (check.DayOfWeek != (DayOfWeek)(dow % 7))
                {
                    f.Error = "day of week mismatch";
                    return f;
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                f.Error = "impossible date";
                return f;
            }

            f.Valid = true;
            return f;
        }

        /// <summary>Parity bit that makes the count of ones over from..to even.</summary>
        private static int Parity(int[] b, int from, int to)
        {
            int ones = 0;
            for (int i = from; i <= to; i++) ones += b[i];
            return (ones & 1) == 0 ? 0 : 1;
        }

        private static bool ParityOk(int[] b, int from, int to)
        {
            int ones = 0;
            for (int i = from; i <= to; i++) ones += b[i];
            return (ones & 1) == 0;
        }

        private static int Bcd(int[] b, int offset, params int[] weights)
        {
            int v = 0;
            for (int i = 0; i < weights.Length; i++) v += b[offset + i] * weights[i];
            return v;
        }
    }

    /// <summary>
    /// Streaming decoder: eats the carrier envelope, finds the 100/200 ms dips,
    /// assembles a telegram, validates parity.
    ///
    /// It works entirely in the time domain, which is the whole point: DCF77 is
    /// one bit per second, so you need no demodulator at all beyond an envelope
    /// detector and a threshold.
    /// </summary>
    public sealed class Dcf77Decoder
    {
        private const int BucketRate = 1000;

        private readonly int _samplesPerBucket;
        private readonly float[] _bits = new float[Dcf77Code.BitCount];
        private readonly bool[] _haveBit = new bool[Dcf77Code.BitCount];

        private double _bucketSum;
        private int _bucketCount;
        private long _ms;
        private long _prevStart;
        private bool _havePrev;
        private bool _inPulse;
        private float _peak = 0.001f;
        private int _secondIndex;
        private int _bitCount;

        public int Pulses;
        public int Frames;
        public int ValidFrames;

        public event Action<Dcf77Frame> FrameDecoded;

        /// <summary>Fires for every dip that was measured. secondIndex, durationMs.</summary>
        public event Action<int, int> PulseMeasured;

        public Dcf77Decoder(double envelopeRate)
        {
            _samplesPerBucket = Math.Max(1, (int)Math.Round(envelopeRate / BucketRate));
        }

        /// <summary>Feed the carrier envelope (magnitude), any rate above ~2 kHz.</summary>
        public void Process(float[] mag, int n)
        {
            for (int i = 0; i < n; i++)
            {
                _bucketSum += mag[i];

                if (++_bucketCount >= _samplesPerBucket)
                {
                    Step((float)(_bucketSum / _bucketCount));
                    _bucketSum = 0.0;
                    _bucketCount = 0;
                }
            }
        }

        public void Reset()
        {
            Array.Clear(_haveBit, 0, _haveBit.Length);
            _bitCount = 0;
            _secondIndex = 0;
            _havePrev = false;
            _inPulse = false;
            _peak = 0.001f;
            _prevStart = 0;
        }

        private void Step(float level)
        {
            _ms++;

            // The carrier is at full level 85% of the time, so a peak follower
            // with a slow release is a stable reference and needs no AGC.
            if (level > _peak) _peak += 0.3f * (level - _peak);
            else _peak += 0.00035f * (level - _peak);

            if (_peak < 1e-5f) return;

            float hi = _peak * 0.70f;
            float lo = _peak * 0.40f;

            if (!_inPulse)
            {
                if (level < lo)
                {
                    _inPulse = true;
                    OnPulseStart();
                }
            }
            else if (level > hi)
            {
                _inPulse = false;
                OnPulseEnd((int)(_ms - _prevStart));
            }
        }

        private void OnPulseStart()
        {
            if (!_havePrev)
            {
                _havePrev = true;
                _secondIndex = 0;
                ClearBits();
            }
            else
            {
                long gap = _ms - _prevStart;

                if (gap > 1500)
                {
                    // Second 59 has no pulse at all, so the gap across a minute
                    // boundary is ~2 s. That is our frame sync.
                    FinishFrame();
                    _secondIndex = 0;
                }
                else
                {
                    _secondIndex++;
                }
            }

            _prevStart = _ms;
            Pulses++;
        }

        private void ClearBits()
        {
            Array.Clear(_haveBit, 0, _haveBit.Length);
            _bitCount = 0;
        }

        private void OnPulseEnd(int durationMs)
        {
            int bit;

            if (durationMs < 150) bit = 0;
            else if (durationMs < 300) bit = 1;
            else return;                        // glitch, ignore

            Action<int, int> seen = PulseMeasured;
            if (seen != null) seen(_secondIndex, durationMs);

            if (_secondIndex < Dcf77Code.BitCount && !_haveBit[_secondIndex])
            {
                _bits[_secondIndex] = bit;
                _haveBit[_secondIndex] = true;
                _bitCount++;
            }
        }

        private void FinishFrame()
        {
            if (_bitCount < Dcf77Code.BitCount - 1)
            {
                ClearBits();
                return;
            }

            int[] b = new int[Dcf77Code.BitCount];
            for (int i = 0; i < Dcf77Code.BitCount; i++)
            {
                if (!_haveBit[i])
                {
                    ClearBits();
                    return;
                }
                b[i] = (int)_bits[i];
            }

            Dcf77Frame frame = Dcf77Code.Decode(b);
            Frames++;
            if (frame.Valid) ValidFrames++;

            Action<Dcf77Frame> handler = FrameDecoded;
            if (handler != null) handler(frame);

            ClearBits();
        }
    }

    /// <summary>
    /// Front end for 77.5 kHz: mix down, two-stage decimation, magnitude, decode.
    /// Two stages matter: going from 192 kHz straight to 6 kHz in one FIR would
    /// need hundreds of taps per input sample, which on an old CPU is the
    /// difference between real time and not.
    /// </summary>
    public sealed class Dcf77Receiver
    {
        private readonly Nco _nco;
        private readonly ComplexDecimator _stage1;
        private readonly ComplexDecimator _stage2;
        private readonly float[] _i;
        private readonly float[] _q;
        private readonly float[] _i2;
        private readonly float[] _q2;
        private readonly float[] _i3;
        private readonly float[] _q3;
        private readonly float[] _mag;

        public Dcf77Receiver(double sampleRate, double carrierHz)
        {
            SampleRate = sampleRate;

            _nco = new Nco();
            _nco.SetFrequency(carrierHz, sampleRate);

            int d1 = Math.Max(1, (int)Math.Round(sampleRate / 24000.0));
            int mid = (int)(sampleRate / d1);
            int d2 = Math.Max(1, (int)Math.Round(mid / 6000.0));
            EnvelopeRate = (int)(mid / d2);

            _stage1 = new ComplexDecimator(Stage(sampleRate, mid, d1), d1);
            _stage2 = new ComplexDecimator(Stage(mid, EnvelopeRate, d2), d2);

            int block = 32768;
            _i = new float[block];
            _q = new float[block];
            _i2 = new float[block];
            _q2 = new float[block];
            _i3 = new float[block];
            _q3 = new float[block];
            _mag = new float[block];
        }

        public double SampleRate { get; private set; }
        public int EnvelopeRate { get; private set; }
        public int MaxBlock { get { return _i.Length; } }

        public void Process(float[] input, int n, Dcf77Decoder decoder)
        {
            if (n > _i.Length) throw new ArgumentOutOfRangeException("n");

            _nco.MixReal(input, _i, _q, n);
            int m1 = _stage1.Process(_i, _q, n, _i2, _q2);
            int m2 = _stage2.Process(_i2, _q2, m1, _i3, _q3);

            for (int k = 0; k < m2; k++)
                _mag[k] = (float)Math.Sqrt(_i3[k] * _i3[k] + _q3[k] * _q3[k]);

            decoder.Process(_mag, m2);
        }

        /// <summary>Narrow low-pass: DCF77 needs about 20 Hz of bandwidth, not 6 kHz.</summary>
        private static float[] Stage(double rate, int outRate, int decim)
        {
            double pass = 250.0 / rate;
            double stop = 0.5 / decim;
            if (stop - pass < 0.002) return FirDesign.LowPass(0.45 / decim, 63, 8.6);

            double deltaF = stop - pass;
            int taps = (int)Math.Ceiling(62.0 / (2.285 * 2.0 * Math.PI * deltaF));
            if (taps < 15) taps = 15;
            if (taps > 2047) taps = 2047;

            return FirDesign.LowPass((pass + stop) * 0.5, taps, 8.6);
        }
    }

    /// <summary>
    /// Synthetic DCF77 transmitter, used by the self-test to prove the whole
    /// chain (RF -> envelope -> bits -> parity -> clock) actually closes.
    /// </summary>
    public static class Dcf77Signal
    {
        public static float[] Generate(DateTime cetStart, int seconds, double rate, double carrierHz, double noise)
        {
            int n = (int)(seconds * rate);
            float[] buf = new float[n];
            Random rnd = new Random(77);

            // 100 ms = 0, 200 ms = 1, carrier drops to 15% during the dip.
            const double dipLevel = 0.15;

            for (int s = 0; s < seconds; s++)
            {
                DateTime t = cetStart.AddSeconds(s);
                int secondIndex = t.Second;

                double dipMs = 0.0;
                if (secondIndex != 59)
                {
                    // A real telegram describes the minute that begins at the next
                    // minute marker, so the frame running through minute M carries M+1.
                    DateTime minute = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0);
                    int[] bits = Dcf77Code.Encode(minute.AddMinutes(1));
                    dipMs = bits[secondIndex] == 1 ? 200.0 : 100.0;
                }

                int from = (int)(s * rate);
                int to = (int)((s + 1) * rate);
                int dipEnd = from + (int)(dipMs * rate / 1000.0);
                double ramp = 0.002 * rate;

                for (int i = from; i < to && i < n; i++)
                {
                    double env = 1.0;

                    if (dipMs > 0.0)
                    {
                        if (i < dipEnd)
                        {
                            double a = Math.Min(1.0, (i - from) / ramp);
                            env = 1.0 - (1.0 - dipLevel) * a;
                        }
                        else
                        {
                            double a = Math.Min(1.0, (i - dipEnd) / ramp);
                            env = dipLevel + (1.0 - dipLevel) * a;
                        }
                    }

                    double tsec = (double)i / rate;
                    double v = env * Math.Cos(2.0 * Math.PI * carrierHz * tsec);
                    if (noise > 0.0) v += (rnd.NextDouble() * 2.0 - 1.0) * noise;

                    buf[i] = (float)v;
                }
            }

            return buf;
        }
    }
}
