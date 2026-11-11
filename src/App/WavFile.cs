using System;
using System.IO;
using System.Text;

namespace Radiocity.App
{
    /// <summary>
    /// Tiny RIFF reader/writer. Only what a receiver needs: PCM 8/16/24/32 and
    /// float32 input, mono float output. 24-bit support matters because that is
    /// the only way to get real dynamic range out of a 192 kHz sound card.
    /// </summary>
    public static class WavFile
    {
        public static float[] Read(string path, out int sampleRate)
        {
            using (BinaryReader br = new BinaryReader(File.OpenRead(path)))
            {
                if (ReadTag(br) != "RIFF") throw new InvalidDataException("not a RIFF file");
                br.ReadUInt32();
                if (ReadTag(br) != "WAVE") throw new InvalidDataException("not a WAVE file");

                int channels = 1;
                int bits = 16;
                int format = 1;
                sampleRate = 44100;
                long dataPos = -1;
                long dataLen = 0;

                while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
                {
                    string id = ReadTag(br);
                    uint size = br.ReadUInt32();

                    if (id == "fmt ")
                    {
                        format = br.ReadUInt16();
                        channels = br.ReadUInt16();
                        sampleRate = (int)br.ReadUInt32();
                        br.ReadUInt32();
                        br.ReadUInt16();
                        bits = br.ReadUInt16();
                        if (size > 16) br.BaseStream.Position += size - 16;
                    }
                    else if (id == "data")
                    {
                        dataPos = br.BaseStream.Position;
                        dataLen = size;
                        br.BaseStream.Position += size + (size & 1);
                    }
                    else
                    {
                        br.BaseStream.Position += size + (size & 1);
                    }
                }

                if (dataPos < 0) throw new InvalidDataException("no data chunk");

                int bytesPerSample = bits / 8;
                int frames = (int)(dataLen / (bytesPerSample * channels));
                float[] mono = new float[frames];
                br.BaseStream.Position = dataPos;

                for (int f = 0; f < frames; f++)
                {
                    double acc = 0.0;
                    for (int c = 0; c < channels; c++)
                    {
                        double v;
                        if (format == 3 && bits == 32)
                        {
                            v = br.ReadSingle();
                        }
                        else if (bits == 8)
                        {
                            v = (br.ReadByte() - 128) / 128.0;
                        }
                        else if (bits == 16)
                        {
                            v = br.ReadInt16() / 32768.0;
                        }
                        else if (bits == 24)
                        {
                            int b0 = br.ReadByte();
                            int b1 = br.ReadByte();
                            int b2 = br.ReadByte();
                            int s = b0 | (b1 << 8) | (b2 << 16);
                            if ((s & 0x800000) != 0) s |= unchecked((int)0xFF000000);
                            v = s / 8388608.0;
                        }
                        else if (bits == 32)
                        {
                            v = br.ReadInt32() / 2147483648.0;
                        }
                        else
                        {
                            throw new InvalidDataException("unsupported bit depth: " + bits);
                        }
                        acc += v;
                    }
                    mono[f] = (float)(acc / channels);
                }

                return mono;
            }
        }

        public static void Write(string path, float[] data, int count, int sampleRate)
        {
            using (BinaryWriter bw = new BinaryWriter(File.Create(path)))
            {
                int bytes = count * 2;

                bw.Write(Encoding.ASCII.GetBytes("RIFF"));
                bw.Write((uint)(36 + bytes));
                bw.Write(Encoding.ASCII.GetBytes("WAVE"));
                bw.Write(Encoding.ASCII.GetBytes("fmt "));
                bw.Write((uint)16);
                bw.Write((ushort)1);
                bw.Write((ushort)1);
                bw.Write((uint)sampleRate);
                bw.Write((uint)(sampleRate * 2));
                bw.Write((ushort)2);
                bw.Write((ushort)16);
                bw.Write(Encoding.ASCII.GetBytes("data"));
                bw.Write((uint)bytes);

                for (int i = 0; i < count; i++)
                {
                    float v = data[i];
                    if (v > 1.0f) v = 1.0f;
                    if (v < -1.0f) v = -1.0f;
                    bw.Write((short)(v * 32767.0f));
                }
            }
        }

        private static string ReadTag(BinaryReader br)
        {
            byte[] b = br.ReadBytes(4);
            if (b.Length < 4) return string.Empty;
            return Encoding.ASCII.GetString(b);
        }
    }
}
