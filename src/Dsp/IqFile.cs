using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Radiocity.Dsp
{
    /// <summary>
    /// Baseband I/Q on disk, as a stereo WAV.
    ///
    /// Why WAV and not a raw dump with a sidecar file: a raw dump plus a README
    /// is a file format that only works if the README survives. A stereo WAV is
    /// readable by Audacity, SoX, Python and every SDR tool, and it carries the
    /// sample rate in the header where it belongs.
    ///
    /// Left is I, right is Q. The centre frequency has nowhere standard to live,
    /// so it goes into a RIFF LIST/INFO comment as "center=77500". Tools that do
    /// not know about it ignore it; ours reads it back.
    ///
    /// Default sample format is 32-bit float: baseband I/Q from a 24-bit card
    /// has more dynamic range than int16 can carry, and clipping a recording is
    /// the one mistake you cannot fix afterwards.
    /// </summary>
    public static class IqFile
    {
        public const string CommentPrefix = "center=";

        public sealed class Info
        {
            public int SampleRate;
            public double CenterHz;
            public int Bits;
            public int Frames;
        }

        public static void Write(string path, float[] i, float[] q, int count,
                                 int sampleRate, double centerHz, bool useFloat)
        {
            if (i == null || q == null) throw new ArgumentNullException("i/q");
            if (i.Length < count || q.Length < count) throw new ArgumentException("buffers shorter than count");

            int bits = useFloat ? 32 : 16;
            int bytesPerSample = bits / 8;
            int blockAlign = 2 * bytesPerSample;
            int dataBytes = count * blockAlign;

            string comment = CommentPrefix + centerHz.ToString("F1", CultureInfo.InvariantCulture);

            using (BinaryWriter bw = new BinaryWriter(File.Create(path)))
            {
                int listSize = 4 + 8 + 4 + PadToEven(comment.Length + 1);

                bw.Write(Encoding.ASCII.GetBytes("RIFF"));
                bw.Write((uint)(4 + 8 + 16 + 8 + dataBytes + 8 + listSize));
                bw.Write(Encoding.ASCII.GetBytes("WAVE"));

                // fmt
                bw.Write(Encoding.ASCII.GetBytes("fmt "));
                bw.Write((uint)16);
                bw.Write((ushort)(useFloat ? 3 : 1));
                bw.Write((ushort)2);
                bw.Write((uint)sampleRate);
                bw.Write((uint)(sampleRate * blockAlign));
                bw.Write((ushort)blockAlign);
                bw.Write((ushort)bits);

                // data
                bw.Write(Encoding.ASCII.GetBytes("data"));
                bw.Write((uint)dataBytes);

                for (int k = 0; k < count; k++)
                {
                    if (useFloat)
                    {
                        bw.Write(i[k]);
                        bw.Write(q[k]);
                    }
                    else
                    {
                        bw.Write((short)Clamp16(i[k]));
                        bw.Write((short)Clamp16(q[k]));
                    }
                }

                // LIST INFO, holding the centre frequency
                bw.Write(Encoding.ASCII.GetBytes("LIST"));
                bw.Write((uint)listSize);
                bw.Write(Encoding.ASCII.GetBytes("INFO"));
                bw.Write(Encoding.ASCII.GetBytes("ICMT"));
                bw.Write((uint)(comment.Length + 1));
                bw.Write(Encoding.ASCII.GetBytes(comment));
                bw.Write((byte)0);
                if ((comment.Length + 1) % 2 != 0) bw.Write((byte)0);
            }
        }

        public static void Read(string path, out float[] i, out float[] q, out Info info)
        {
            info = new Info();
            i = null;
            q = null;

            using (BinaryReader br = new BinaryReader(File.OpenRead(path)))
            {
                if (ReadTag(br) != "RIFF") throw new InvalidDataException("not a RIFF file");
                br.ReadUInt32();
                if (ReadTag(br) != "WAVE") throw new InvalidDataException("not a WAVE file");

                int format = 1;
                int channels = 2;
                int bits = 16;
                long dataPos = -1;
                long dataLen = 0;
                string comment = null;

                while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
                {
                    string id = ReadTag(br);
                    uint size = br.ReadUInt32();
                    long next = br.BaseStream.Position + size + (size & 1);

                    if (id == "fmt ")
                    {
                        format = br.ReadUInt16();
                        channels = br.ReadUInt16();
                        info.SampleRate = (int)br.ReadUInt32();
                        br.ReadUInt32();
                        br.ReadUInt16();
                        bits = br.ReadUInt16();
                    }
                    else if (id == "data")
                    {
                        dataPos = br.BaseStream.Position;
                        dataLen = size;
                    }
                    else if (id == "LIST")
                    {
                        long end = br.BaseStream.Position + size;
                        if (ReadTag(br) == "INFO")
                        {
                            while (br.BaseStream.Position + 8 <= end)
                            {
                                string sub = ReadTag(br);
                                uint subSize = br.ReadUInt32();
                                long subEnd = br.BaseStream.Position + subSize + (subSize & 1);

                                if (sub == "ICMT" && subSize < 512)
                                {
                                    byte[] raw = br.ReadBytes((int)subSize);
                                    comment = Encoding.ASCII.GetString(raw).TrimEnd('\0');
                                }

                                br.BaseStream.Position = subEnd;
                            }
                        }
                    }

                    br.BaseStream.Position = next;
                }

                if (dataPos < 0) throw new InvalidDataException("no data chunk");
                if (channels != 2) throw new InvalidDataException("I/Q needs two channels, found " + channels);

                int bytesPerSample = bits / 8;
                int frames = (int)(dataLen / (2 * bytesPerSample));
                info.Bits = bits;
                info.Frames = frames;
                info.CenterHz = ParseCenter(comment);

                i = new float[frames];
                q = new float[frames];
                br.BaseStream.Position = dataPos;

                for (int k = 0; k < frames; k++)
                {
                    i[k] = ReadSample(br, format, bits);
                    q[k] = ReadSample(br, format, bits);
                }
            }
        }

        private static double ParseCenter(string comment)
        {
            if (string.IsNullOrEmpty(comment)) return 0.0;

            int at = comment.IndexOf(CommentPrefix, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return 0.0;

            string tail = comment.Substring(at + CommentPrefix.Length);
            int end = tail.IndexOf(';');
            if (end >= 0) tail = tail.Substring(0, end);

            double hz;
            if (double.TryParse(tail.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out hz)) return hz;
            return 0.0;
        }

        private static float ReadSample(BinaryReader br, int format, int bits)
        {
            if (format == 3 && bits == 32) return br.ReadSingle();

            if (bits == 16) return br.ReadInt16() / 32768.0f;

            if (bits == 24)
            {
                int b0 = br.ReadByte();
                int b1 = br.ReadByte();
                int b2 = br.ReadByte();
                int s = b0 | (b1 << 8) | (b2 << 16);
                if ((s & 0x800000) != 0) s |= unchecked((int)0xFF000000);
                return s / 8388608.0f;
            }

            if (bits == 32) return br.ReadInt32() / 2147483648.0f;

            throw new InvalidDataException("unsupported sample format: " + bits + " bit, tag " + format);
        }

        private static int Clamp16(float v)
        {
            if (v > 1.0f) v = 1.0f;
            if (v < -1.0f) v = -1.0f;

            // Scale by 32768 and clamp, rather than scaling by 32767: the reader
            // divides by 32768, and using a different factor on the way in is a
            // systematic error of one LSB at full scale for no reason at all.
            int s = (int)Math.Round(v * 32768.0f);
            if (s > 32767) s = 32767;
            return s;
        }

        private static int PadToEven(int n)
        {
            return (n % 2 == 0) ? n : n + 1;
        }

        private static string ReadTag(BinaryReader br)
        {
            byte[] b = br.ReadBytes(4);
            if (b.Length < 4) return string.Empty;
            return Encoding.ASCII.GetString(b);
        }
    }
}
