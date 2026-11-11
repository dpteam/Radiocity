using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Radiocity.Audio;
using Radiocity.Dsp;
using Radiocity.Web;

namespace Radiocity.App
{
    internal static class Program
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>
        /// Capture depth, settable with -b8/-b16/-b24/-b32 on any command.
        /// 16 bit is about 96 dB of range; a good codec's own noise floor is
        /// below that, so 24 bit is not a luxury for an SDR, it is the difference
        /// between hearing a weak carrier and hearing the quantisation of a
        /// strong neighbour.
        /// </summary>
        private static int CaptureBits = 16;

        private static int Main(string[] args)
        {
            // A pool thread that throws takes the whole process down. Say why,
            // instead of vanishing silently with a closed port and no clue.
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Console.Error.WriteLine("fatal: " + e.ExceptionObject);
            };

            try
            {
                args = StripBitDepth(args);

                if (args.Length == 0) return Help();

                switch (args[0].ToLowerInvariant())
                {
                    case "selftest": return SelfTest();
                    case "spec": return Spectrum(args);
                    case "demod": return Demod(args);
                    case "scan": return Scan(args);
                    case "listen": return Listen(args);
                    case "calib": return Calibrate(args);
                    case "dcf77": return Dcf77Live(args);
                    case "web": return WebServe(args);
                    case "iq": return IqCommand(args);
                    case "help":
                    case "-h":
                    case "--help": return Help();
                    default:
                        Console.Error.WriteLine("unknown command: " + args[0]);
                        return Help();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 1;
            }
        }

        private static string[] StripBitDepth(string[] args)
        {
            System.Collections.Generic.List<string> rest = new System.Collections.Generic.List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();

                if (a == "-b8") CaptureBits = 8;
                else if (a == "-b16") CaptureBits = 16;
                else if (a == "-b24") CaptureBits = 24;
                else if (a == "-b32") CaptureBits = 32;
                else rest.Add(args[i]);
            }

            return rest.ToArray();
        }

        /// <summary>Read from the assembly so the banner cannot drift from the build.</summary>
        private static string Version
        {
            get
            {
                System.Version v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
        }

        private static int Help()
        {
            Console.WriteLine("Radiocity " + Version + " - a sound card used as an SDR front end");
            Console.WriteLine("(.NET 4.0, no drivers, no third-party libraries)");
            Console.WriteLine();
            Console.WriteLine("  radiocity selftest                      verify the whole DSP chain on synthetic signals");
            Console.WriteLine("  radiocity spec <in.wav>                 ASCII spectrum of an audio file");
            Console.WriteLine("  radiocity demod <in.wav> <out.wav> <tuneHz> [am|fm|usb|lsb|cw] [decim]");
            Console.WriteLine("  radiocity scan [seconds] [rate]         capture and list the strongest carriers");
            Console.WriteLine("  radiocity listen <tuneHz> [seconds] [am|fm|usb|lsb|cw] [rate] [channelHz]");
            Console.WriteLine("  radiocity calib <refHz> [seconds] [rate]  measure the card's clock error in ppm");
            Console.WriteLine("  radiocity dcf77 [seconds] [rate]        decode the 77.5 kHz time signal");
            Console.WriteLine("  radiocity web [--sim] [--port N] [--rate N] [--freq N] [--mode M]");
            Console.WriteLine("                                          serve a WebSDR UI on http://localhost:8080");
            Console.WriteLine("  radiocity iq record <file> <freqHz> [sec] [rate] [decim]");
            Console.WriteLine("                                          record baseband I/Q for offline analysis");
            Console.WriteLine("  radiocity iq demod <file> <out.wav> [mode]");
            Console.WriteLine("                                          demodulate a recording, try any mode");
            Console.WriteLine("  radiocity iq info <file>                show what a recording contains");
            Console.WriteLine();
            Console.WriteLine("  rate defaults to 48000; 192000 reaches to 96 kHz and is required for DCF77.");
            Console.WriteLine("  add -b24 (or -b8/-b16/-b32) to any command to pick the capture depth.");
            Console.WriteLine("  add --sim to 'web' to serve a synthetic band, no sound card needed.");
            Console.WriteLine();
            Console.WriteLine("  Reality check: a sound card ADC stops at its own sample rate, so it hears");
            Console.WriteLine("  0..96 kHz at best. Broadcast FM at 87.5 MHz needs a downconverter in front");
            Console.WriteLine("  of it. What works with a bare wire is VLF/LF: time signals, navigation");
            Console.WriteLine("  beacons, sferics and whistlers.");
            return 0;
        }

        // ================================================================ selftest

        private static int SelfTest()
        {
            bool ok = true;

            ok &= RunAmTest();
            Console.WriteLine();
            ok &= RunFmTest();
            Console.WriteLine();
            ok &= RunSsbTest();
            Console.WriteLine();
            ok &= RunCwTest();
            Console.WriteLine();
            ok &= RunCalibTest();
            Console.WriteLine();
            ok &= RunDecimationTest();
            Console.WriteLine();
            ok &= RunSquelchTest();
            Console.WriteLine();
            ok &= RunBlankerTest();
            Console.WriteLine();
            ok &= RunIqTest();
            Console.WriteLine();
            ok &= RunResamplerTest();
            Console.WriteLine();
            ok &= RunDcf77Test();
            Console.WriteLine();
            Console.WriteLine(ok ? "ALL TESTS PASSED" : "SOME TESTS FAILED");
            return ok ? 0 : 2;
        }

        private static bool RunAmTest()
        {
            const double fs = 48000.0;
            const double carrier = 12000.0;
            const double tone = 1000.0;
            const double depth = 0.6;
            const double noise = 0.003;
            const int decim = 4;

            int n = (int)(fs * 4.0);
            float[] input = new float[n];
            Random rnd = new Random(1234);

            for (int i = 0; i < n; i++)
            {
                double t = i / fs;
                double m = 1.0 + depth * Math.Cos(2.0 * Math.PI * tone * t);
                double s = m * Math.Cos(2.0 * Math.PI * carrier * t) / (1.0 + depth);
                s += (rnd.NextDouble() * 2.0 - 1.0) * noise;
                input[i] = (float)s;
            }

            Receiver rx = new Receiver(fs, carrier, ReceiverOptions.Am(decim));
            float[] output = Run(rx, input);

            float[] clean = new float[n];
            for (int i = 0; i < n; i++)
                clean[i] = (float)((1.0 + depth * Math.Cos(2.0 * Math.PI * tone * i / fs)) *
                    Math.Cos(2.0 * Math.PI * carrier * i / fs) / (1.0 + depth));

            Receiver rx2 = new Receiver(fs, carrier, ReceiverOptions.Am(decim));
            float[] output2 = Run(rx2, clean);

            double residual, residualClean;
            double toneLevel = MeasureTone(output, rx.OutputRate, tone, out residual);
            double toneClean = MeasureTone(output2, rx2.OutputRate, tone, out residualClean);

            Console.WriteLine("AM self-test");
            Console.WriteLine("  input    : 48 kHz, 4 s, carrier 12 kHz, tone 1 kHz, depth 60%, noise -50 dBFS");
            Console.WriteLine("  chain    : NCO -> FIR " + rx.Taps + " taps, decimate x" + decim + " -> envelope -> DC block -> AGC");
            Console.WriteLine("  baseband : " + rx.OutputRate + " Hz complex");
            Console.WriteLine("  with noise : tone " + toneLevel.ToString("F3", Inv) + "  SNR " + Db(toneLevel / residual).ToString("F1", Inv) + " dB");
            Console.WriteLine("  clean input: tone " + toneClean.ToString("F3", Inv) + "  distortion " + Db(toneClean / residualClean).ToString("F1", Inv) + " dB below tone");

            bool ok = Math.Abs(toneLevel - 0.30) < 0.03 && Math.Abs(toneClean - 0.30) < 0.03;
            Console.WriteLine(ok ? "  PASS" : "  FAIL: recovered tone level is off target");
            return ok;
        }

        private static bool RunFmTest()
        {
            const double fs = 48000.0;
            const double carrier = 12000.0;
            const double tone = 1000.0;
            const double deviation = 2000.0;
            const int decim = 4;

            int n = (int)(fs * 4.0);
            float[] input = new float[n];
            double phase = 0.0;

            // Phase accumulator FM: the only way to keep the instantaneous
            // frequency exact without accumulating rounding error.
            for (int i = 0; i < n; i++)
            {
                double t = i / fs;
                input[i] = (float)Math.Cos(phase);
                phase += 2.0 * Math.PI * (carrier + deviation * Math.Cos(2.0 * Math.PI * tone * t)) / fs;
            }

            ReceiverOptions o = ReceiverOptions.Fm(decim);
            o.ChannelHz = 12000.0;

            Receiver rx = new Receiver(fs, carrier, o);
            float[] output = Run(rx, input);

            double residual;
            double toneLevel = MeasureTone(output, rx.OutputRate, tone, out residual);

            Console.WriteLine("FM self-test");
            Console.WriteLine("  input    : 48 kHz, 4 s, carrier 12 kHz, tone 1 kHz, deviation 2 kHz");
            Console.WriteLine("  chain    : NCO -> FIR " + rx.Taps + " taps, decimate x" + decim + " -> quadrature discriminator -> AGC");
            Console.WriteLine("  tone " + toneLevel.ToString("F3", Inv) + "  residual " + Db(toneLevel / residual).ToString("F1", Inv) + " dB below tone");

            bool ok = Math.Abs(toneLevel - 0.30) < 0.03;
            Console.WriteLine(ok ? "  PASS" : "  FAIL: recovered tone level is off target");
            return ok;
        }

        private static bool RunSsbTest()
        {
            const double fs = 48000.0;
            const double carrier = 12000.0;
            const double tone = 1000.0;
            const int decim = 4;
            int n = (int)(fs * 4.0);

            // Suppressed-carrier USB: the only thing in the spectrum is 13 kHz.
            float[] usb = new float[n];
            float[] lsb = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = i / fs;
                usb[i] = (float)Math.Cos(2.0 * Math.PI * (carrier + tone) * t);
                lsb[i] = (float)Math.Cos(2.0 * Math.PI * (carrier - tone) * t);
            }

            Receiver rxUsb = new Receiver(fs, carrier, ReceiverOptions.Ssb(true, decim));
            float[] outUsb = Run(rxUsb, usb);

            Receiver rxLsb = new Receiver(fs, carrier, ReceiverOptions.Ssb(false, decim));
            float[] outLsb = Run(rxLsb, lsb);

            double r1, r2;
            double t1 = MeasureTone(outUsb, rxUsb.OutputRate, tone, out r1);
            double t2 = MeasureTone(outLsb, rxLsb.OutputRate, tone, out r2);

            // Rejection has to be measured with the AGC out of the loop: on a
            // cancelled sideband the AGC would apply its full +60 dB and turn
            // numerical dust into a tone, which flatters nothing and proves nothing.
            ReceiverOptions raw = ReceiverOptions.Ssb(false, decim);
            raw.AgcEnabled = false;
            Receiver rxWrong = new Receiver(fs, carrier, raw);
            float[] outWrong = Run(rxWrong, usb);

            ReceiverOptions rawRight = ReceiverOptions.Ssb(true, decim);
            rawRight.AgcEnabled = false;
            Receiver rxRight = new Receiver(fs, carrier, rawRight);
            float[] outRight = Run(rxRight, usb);

            double rr, rw;
            double toneRight = MeasureTone(outRight, rxRight.OutputRate, tone, out rr);
            double toneWrong = MeasureTone(outWrong, rxWrong.OutputRate, tone, out rw);
            double rejection = Db(toneWrong / Math.Max(toneRight, 1e-12));

            Console.WriteLine("SSB self-test (phasing method, Hilbert " + raw.SsbTaps + " taps)");
            Console.WriteLine("  USB signal -> USB demod : tone " + t1.ToString("F3", Inv));
            Console.WriteLine("  LSB signal -> LSB demod : tone " + t2.ToString("F3", Inv));
            Console.WriteLine("  USB signal -> LSB demod : " + rejection.ToString("F1", Inv) + " dB (unwanted sideband)");

            bool ok = Math.Abs(t1 - 0.30) < 0.03 && Math.Abs(t2 - 0.30) < 0.03 && rejection < -50.0;
            Console.WriteLine(ok ? "  PASS" : "  FAIL: sideband rejection or level is wrong");
            return ok;
        }

        private static bool RunCwTest()
        {
            const double fs = 48000.0;
            const double carrier = 12000.0;
            const double pitch = 700.0;
            const int decim = 4;
            int n = (int)(fs * 4.0);

            // A CW carrier: one unmodulated tone. The receiver should turn it into
            // a 700 Hz beat note, which is the entire trick of a BFO.
            float[] input = new float[n];
            for (int i = 0; i < n; i++)
                input[i] = (float)Math.Cos(2.0 * Math.PI * carrier * i / fs);

            Receiver rx = new Receiver(fs, carrier, ReceiverOptions.Cw(pitch, decim));
            float[] output = Run(rx, input);

            double residual;
            double level = MeasureTone(output, rx.OutputRate, pitch, out residual);

            Console.WriteLine("CW self-test");
            Console.WriteLine("  unmodulated carrier at 12 kHz, BFO 700 Hz -> beat note " + level.ToString("F3", Inv) +
                              " at " + pitch.ToString("F0", Inv) + " Hz");

            bool ok = Math.Abs(level - 0.30) < 0.03;
            Console.WriteLine(ok ? "  PASS" : "  FAIL: beat note is off target");
            return ok;
        }

        private static bool RunCalibTest()
        {
            const double rate = 192000.0;
            const double nominal = 77500.0;
            const double truePpm = 27.0;

            // A card that runs 27 ppm fast makes a 77.5 kHz carrier look 2.09 Hz high.
            double f = nominal * (1.0 + truePpm * 1e-6);
            int n = (int)rate;
            float[] buf = new float[n];
            for (int i = 0; i < n; i++)
                buf[i] = (float)Math.Cos(2.0 * Math.PI * f * i / rate);

            ClockCalib.Result r = ClockCalib.Measure(buf, n, rate, nominal);

            Console.WriteLine("clock calibration self-test");
            Console.WriteLine("  injected " + truePpm.ToString("F1", Inv) + " ppm, measured " + r.Ppm.ToString("F2", Inv) + " ppm");
            Console.WriteLine("  " + r.ToString());
            Console.WriteLine("  corrected sample rate: " + r.CorrectedRate.ToString("F2", Inv) + " Hz");

            bool ok = Math.Abs(r.Ppm - truePpm) < 1.5;
            Console.WriteLine(ok ? "  PASS" : "  FAIL: ppm estimate is off");
            return ok;
        }

        private static bool RunDecimationTest()
        {
            // At 192 kHz, a 9 kHz AM channel needs a transition band of 1.5 kHz.
            // One filter doing that in a single step is expensive; splitting it
            // 192k -> 24k -> 12k is roughly four times cheaper for the same
            // result. This measures both, so the claim is checkable.
            const double fs = 192000.0;
            const double carrier = 30000.0;
            const double tone = 1000.0;
            const int decim = 16;

            int n = (int)(fs * 2.0);
            float[] input = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = i / fs;
                double m = 1.0 + 0.6 * Math.Cos(2.0 * Math.PI * tone * t);
                input[i] = (float)(m * Math.Cos(2.0 * Math.PI * carrier * t) / 1.6);
            }

            ReceiverOptions o = ReceiverOptions.Am(decim);
            o.ChannelHz = 9000.0;

            Receiver rx = new Receiver(fs, carrier, o);
            float[] output = Run(rx, input);

            double residual;
            double level = MeasureTone(output, rx.OutputRate, tone, out residual);

            // Single-stage cost, computed with the same Kaiser formula.
            double pass = 4500.0 / fs;
            double stop = 0.5 / decim;
            int singleTaps = (int)Math.Ceiling(62.0 / (2.285 * 2.0 * Math.PI * (stop - pass)));
            double singleMacs = singleTaps * fs;

            Console.WriteLine("decimation self-test");
            Console.WriteLine("  192 kHz -> " + rx.OutputRate + " Hz in " + rx.DecimationStages +
                              " stage(s), " + rx.Taps + " taps total");
            Console.WriteLine("  single stage would need " + singleTaps + " taps at " + fs.ToString("F0", Inv) +
                              " Hz = " + (singleMacs / 1e6).ToString("F0", Inv) + " M multiply-adds/s");
            Console.WriteLine("  this chain costs " + (rx.FilterCost / 1e6).ToString("F0", Inv) +
                              " M/s, so " + (singleMacs / Math.Max(1.0, rx.FilterCost)).ToString("F1", Inv) + "x cheaper");
            Console.WriteLine("  tone " + level.ToString("F3", Inv) + ", distortion " +
                              Db(level / residual).ToString("F1", Inv) + " dB below");

            bool ok = Math.Abs(level - 0.30) < 0.03 && rx.DecimationStages >= 2;

            // Split factors must multiply back to the requested decimation.
            int[] parts = DecimationChain.SplitDecimation(decim);
            int product = 1;
            for (int i = 0; i < parts.Length; i++)
            {
                product *= parts[i];
                if (parts[i] > 8) ok = false;
            }
            if (product != decim) ok = false;

            Console.WriteLine("  split of x" + decim + ": " + string.Join(" x ", IntToStrings(parts)) +
                              " = " + product);
            Console.WriteLine(ok ? "  PASS" : "  FAIL: chain output or split is wrong");
            return ok;
        }

        private static string[] IntToStrings(int[] values)
        {
            string[] s = new string[values.Length];
            for (int i = 0; i < values.Length; i++) s[i] = values[i].ToString(Inv);
            return s;
        }

        private static bool RunSquelchTest()
        {
            const double fs = 48000.0;
            const double carrier = 12000.0;
            int n = (int)(fs * 3.0);

            // One second of nothing, one second of carrier, one second of nothing.
            float[] input = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = i / fs;
                bool on = t >= 1.0 && t < 2.0;
                input[i] = on ? (float)Math.Cos(2.0 * Math.PI * carrier * t) : 0.0f;
            }

            ReceiverOptions o = ReceiverOptions.Am(4);
            o.SquelchDb = -40.0f;

            Receiver rx = new Receiver(fs, carrier, o);

            float[] block = new float[rx.MaxBlock];
            float[] audio = new float[rx.MaxBlock];
            bool at05 = false, at15 = false, at25 = false;
            bool got05 = false, got15 = false, got25 = false;
            int pos = 0;

            while (pos < n)
            {
                int chunk = Math.Min(rx.MaxBlock, n - pos);
                Array.Copy(input, pos, block, 0, chunk);
                rx.Process(block, chunk, audio);
                pos += chunk;

                // Latch once at each checkpoint. Re-reading every later block
                // would let the flags follow the signal instead of sampling it.
                double t = (double)pos / fs;
                if (!got05 && t >= 0.6) { at05 = rx.Squelch.Open; got05 = true; }
                if (!got15 && t >= 1.6) { at15 = rx.Squelch.Open; got15 = true; }
                if (!got25 && t >= 2.6) { at25 = rx.Squelch.Open; got25 = true; }
            }

            Console.WriteLine("squelch self-test");
            Console.WriteLine("  1 s silence, 1 s carrier, 1 s silence, threshold -40 dBFS");
            Console.WriteLine("  gate at 0.6 s: " + (at05 ? "open" : "muted") +
                              ", at 1.6 s: " + (at15 ? "open" : "muted") +
                              ", at 2.6 s: " + (at25 ? "open" : "muted"));

            bool ok = !at05 && at15 && !at25;
            Console.WriteLine(ok ? "  PASS" : "  FAIL: gate did not follow the signal");
            return ok;
        }

        private static bool RunBlankerTest()
        {
            // Impulse noise is easy to test honestly because the clean signal is
            // known exactly: whatever differs from it is the error, and the only
            // question is whether blanking makes that error smaller.
            const double rate = 12000.0;
            const double tone = 800.0;
            const double amplitude = 0.5;
            const int n = (int)(rate * 4.0);
            const int impulsePeriod = 600;      // 20 impulses per second
            const float impulseLevel = 20.0f;

            float[] cleanI = new float[n];
            float[] cleanQ = new float[n];
            for (int k = 0; k < n; k++)
            {
                double phase = 2.0 * Math.PI * tone * k / rate;
                cleanI[k] = (float)(amplitude * Math.Cos(phase));
                cleanQ[k] = (float)(amplitude * Math.Sin(phase));
            }

            float[] noisyI = (float[])cleanI.Clone();
            float[] noisyQ = (float[])cleanQ.Clone();
            int impulses = 0;
            for (int k = 100; k < n; k += impulsePeriod)
            {
                noisyI[k] += impulseLevel;
                impulses++;
            }

            float[] rawI = (float[])noisyI.Clone();
            float[] rawQ = (float[])noisyQ.Clone();

            NoiseBlanker blanker = new NoiseBlanker(rate);
            float[] blkI = (float[])noisyI.Clone();
            float[] blkQ = (float[])noisyQ.Clone();
            blanker.Process(blkI, blkQ, n);

            double rawError = ComplexError(rawI, rawQ, cleanI, cleanQ, n);
            double blkError = ComplexError(blkI, blkQ, cleanI, cleanQ, n);

            // The tone must still be there afterwards, or "less error" just means
            // "less signal".
            double toneBefore = ComplexTone(cleanI, cleanQ, n, rate, tone);
            double toneAfter = ComplexTone(blkI, blkQ, n, rate, tone);

            Console.WriteLine("noise blanker self-test");
            Console.WriteLine("  800 Hz baseband tone, " + impulses + " impulses at " + impulseLevel.ToString("F0", Inv) +
                              "x amplitude in " + (n / rate).ToString("F0", Inv) + " s");
            Console.WriteLine("  error without blanker : " + rawError.ToString("F3", Inv));
            Console.WriteLine("  error with blanker    : " + blkError.ToString("F3", Inv) +
                              "  (" + (rawError / Math.Max(blkError, 1e-9)).ToString("F1", Inv) + "x less)");
            Console.WriteLine("  blanked " + blanker.Blanked + " impulses, " + blanker.BlankedSamples +
                              " samples (" + (100.0 * blanker.BlankedSamples / n).ToString("F2", Inv) + "%)");
            Console.WriteLine("  tone level " + toneBefore.ToString("F3", Inv) + " -> " + toneAfter.ToString("F3", Inv));

            bool ok = blkError < rawError / 3.0 && toneAfter > toneBefore * 0.98;
            Console.WriteLine(ok ? "  PASS" : "  FAIL: blanker did not remove the impulses cleanly");
            return ok;
        }

        private static double ComplexError(float[] i, float[] q, float[] refI, float[] refQ, int n)
        {
            double sum = 0.0;
            for (int k = 0; k < n; k++)
            {
                double di = i[k] - refI[k];
                double dq = q[k] - refQ[k];
                sum += di * di + dq * dq;
            }
            return Math.Sqrt(sum / n);
        }

        /// <summary>Magnitude of one complex frequency bin, via a quadrature Goertzel.</summary>
        private static double ComplexTone(float[] i, float[] q, int n, double rate, double hz)
        {
            double re = 0.0, im = 0.0;
            for (int k = 0; k < n; k++)
            {
                double phase = -2.0 * Math.PI * hz * k / rate;
                double c = Math.Cos(phase);
                double s = Math.Sin(phase);
                re += i[k] * c - q[k] * s;
                im += i[k] * s + q[k] * c;
            }
            return 2.0 * Math.Sqrt(re * re + im * im) / n;
        }

        private static bool RunIqTest()
        {
            const int frames = 4096;
            const int rate = 12000;
            const double center = 77500.0;

            float[] i = new float[frames];
            float[] q = new float[frames];
            Random rnd = new Random(99);
            for (int k = 0; k < frames; k++)
            {
                i[k] = (float)(rnd.NextDouble() * 2.0 - 1.0);
                q[k] = (float)(rnd.NextDouble() * 2.0 - 1.0);
            }

            string path = Path.Combine(Path.GetTempPath(), "radiocity_iq_test.wav");
            float[] backI, backQ;
            IqFile.Info info;
            double maxErr;

            try
            {
                // Float path: must be bit-exact, because there is nothing to lose.
                IqFile.Write(path, i, q, frames, rate, center, true);
                IqFile.Read(path, out backI, out backQ, out info);

                maxErr = 0.0;
                for (int k = 0; k < frames; k++)
                {
                    maxErr = Math.Max(maxErr, Math.Abs(backI[k] - i[k]));
                    maxErr = Math.Max(maxErr, Math.Abs(backQ[k] - q[k]));
                }

                bool floatOk = info.Frames == frames && info.SampleRate == rate &&
                               Math.Abs(info.CenterHz - center) < 0.5 && maxErr < 1e-6;

                // Int16 path: quantisation is allowed, losing the header is not.
                IqFile.Write(path, i, q, frames, rate, center, false);
                IqFile.Read(path, out backI, out backQ, out info);

                double int16Err = 0.0;
                for (int k = 0; k < frames; k++)
                {
                    int16Err = Math.Max(int16Err, Math.Abs(backI[k] - i[k]));
                    int16Err = Math.Max(int16Err, Math.Abs(backQ[k] - q[k]));
                }

                bool int16Ok = info.Frames == frames && info.Bits == 16 &&
                               Math.Abs(info.CenterHz - center) < 0.5 && int16Err < 1.0 / 32000.0;

                Console.WriteLine("I/Q file self-test");
                Console.WriteLine("  float32 round-trip: " + frames + " frames, max error " + maxErr.ToString("E1", Inv) +
                                  ", centre " + info.CenterHz.ToString("F1", Inv) + " Hz");
                Console.WriteLine("  int16   round-trip: max error " + int16Err.ToString("E1", Inv) +
                                  " (quantisation is " + (1.0 / 32768.0).ToString("E1", Inv) + ")");

                bool ok = floatOk && int16Ok;
                Console.WriteLine(ok ? "  PASS" : "  FAIL: I/Q file lost data or metadata");
                return ok;
            }
            finally
            {
                try { File.Delete(path); }
                catch (IOException) { }
            }
        }

        private static bool RunResamplerTest()
        {
            // The live path hands the card whatever rate the decimator produced,
            // and the card accepts almost none of them. This is the piece that
            // bridges the two, so it gets its own check: odd block sizes, and the
            // block boundary must not produce a click.
            const int inRate = 12000;
            const int outRate = 48000;
            const double tone = 1000.0;
            int n = inRate * 2;

            float[] src = new float[n];
            for (int i = 0; i < n; i++) src[i] = (float)Math.Cos(2.0 * Math.PI * tone * i / inRate);

            InterpolatingResampler rs = new InterpolatingResampler(inRate, outRate);
            float[] dst = new float[n * 4 + 256];
            float[] block = new float[1000];
            float[] outBlock = new float[1000 * 4 + 64];
            int produced = 0, pos = 0;

            while (pos < n)
            {
                int c = Math.Min(1000, n - pos);
                Array.Copy(src, pos, block, 0, c);
                int w = rs.Process(block, c, outBlock, outBlock.Length);
                Array.Copy(outBlock, 0, dst, produced, w);
                produced += w;
                pos += c;
            }

            float[] trimmed = new float[produced];
            Array.Copy(dst, trimmed, produced);

            double residual;
            double level = MeasureTone(trimmed, outRate, tone, out residual);

            Console.WriteLine("resampler self-test");
            Console.WriteLine("  12 kHz -> 48 kHz, 1 kHz tone, blocks of 1000 samples");
            Console.WriteLine("  " + produced + " samples out (expected about " + (n * 4) + "), tone " +
                              level.ToString("F3", Inv) + ", distortion " + Db(level / residual).ToString("F1", Inv) + " dB below");

            // A sample or two per block boundary is carried into the next block by
            // design; what matters is that the tone survives and nothing clicks.
            bool ok = Math.Abs(produced - n * 4) <= 32 && Math.Abs(level - 1.0) < 0.02 && level / residual > 30.0;
            Console.WriteLine(ok ? "  PASS" : "  FAIL: resampler loses samples or adds distortion");
            return ok;
        }

        private static bool RunDcf77Test()
        {
            Console.WriteLine("DCF77 self-test");

            // Logic first, radio second: if this fails the problem is the code,
            // not the DSP, and the two should never be debugged together.
            DateTime sample = new DateTime(2026, 11, 11, 14, 39, 0);
            Dcf77Frame logic = Dcf77Code.Decode(Dcf77Code.Encode(sample));
            Console.WriteLine("  codec round-trip: " + logic.ToString());
            bool ok = logic.Valid && logic.Hour == 14 && logic.Minute == 39 && logic.DayOfWeek == 3;

            // -40 dBFS is a clean night-time signal, -20 dBFS is a noisy city
            // with a bad antenna. Both must survive; if the second one fails the
            // threshold logic is too fragile to trust on a real receiver.
            ok &= RunDcf77At(0.01, "-40 dBFS");
            ok &= RunDcf77At(0.10, "-20 dBFS");

            Console.WriteLine(ok ? "  PASS" : "  FAIL: no valid telegram");
            return ok;
        }

        private static bool RunDcf77At(double noise, string label)
        {
            // Start at second 57 so the first, partially-seen minute is discarded
            // and the decoder syncs on the 2 s minute marker instead of guessing.
            DateTime start = new DateTime(2026, 11, 11, 14, 37, 57);
            const double rate = 192000.0;
            const double carrier = 77500.0;
            const int seconds = 65;

            float[] sig = Dcf77Signal.Generate(start, seconds, rate, carrier, noise);
            Dcf77Receiver front = new Dcf77Receiver(rate, carrier);
            Dcf77Decoder dec = new Dcf77Decoder(front.EnvelopeRate);

            Dcf77Frame last = null;
            dec.FrameDecoded += delegate(Dcf77Frame f) { last = f; };

            System.Text.StringBuilder widths = new System.Text.StringBuilder();
            dec.PulseMeasured += delegate(int second, int durationMs)
            {
                if (widths.Length > 500) widths.Remove(0, 250);
                widths.Append(durationMs.ToString(Inv).PadLeft(4));
            };

            float[] block = new float[front.MaxBlock];
            int pos = 0;
            while (pos < sig.Length)
            {
                int n = Math.Min(front.MaxBlock, sig.Length - pos);
                Array.Copy(sig, pos, block, 0, n);
                front.Process(block, n, dec);
                pos += n;
            }

            Console.WriteLine("  " + label + ": " + seconds + " s at " + rate.ToString("F0", Inv) +
                              " Hz, envelope " + front.EnvelopeRate + " Hz, " + dec.Pulses + " pulses, " +
                              dec.ValidFrames + "/" + dec.Frames + " frames");

            if (last != null && last.Valid)
            {
                Console.WriteLine("    decoded  : " + last.ToString());
                return last.Year == 2026 && last.Month == 11 && last.Day == 11 &&
                       last.Hour == 14 && last.Minute == 39;
            }

            Console.WriteLine("    " + (last != null ? "invalid: " + last.Error : "no frame decoded"));
            Console.WriteLine("    dip widths (ms): " + widths.ToString());
            return false;
        }

        // ================================================================ helpers

        private static float[] Run(Receiver rx, float[] input)
        {
            float[] output = new float[input.Length + rx.MaxBlock];
            float[] block = new float[rx.MaxBlock];
            float[] audio = new float[rx.MaxBlock];
            int produced = 0, pos = 0;

            while (pos < input.Length)
            {
                int chunk = Math.Min(rx.MaxBlock, input.Length - pos);
                Array.Copy(input, pos, block, 0, chunk);
                int m = rx.Process(block, chunk, audio);
                Array.Copy(audio, 0, output, produced, m);
                produced += m;
                pos += chunk;
            }

            float[] trimmed = new float[produced];
            Array.Copy(output, trimmed, produced);
            return trimmed;
        }

        /// <summary>Goertzel on the last second, plus the RMS of everything else.</summary>
        private static double MeasureTone(float[] buf, int rate, double toneHz, out double residual)
        {
            int count = Math.Min(rate, buf.Length);
            int offset = buf.Length - count;

            double tone = Fft.Goertzel(buf, offset, count, toneHz, rate);
            double sum = 0.0;
            for (int i = 0; i < count; i++) sum += buf[offset + i] * (double)buf[offset + i];
            double rms = Math.Sqrt(sum / count);

            residual = Math.Sqrt(Math.Max(1e-12, rms * rms - tone * tone / 2.0));
            return tone;
        }

        private static double Db(double ratio)
        {
            return 20.0 * Math.Log10(Math.Max(ratio, 1e-12));
        }

        private static ReceiverOptions ParseOptions(string mode, double channelHz, int decim)
        {
            switch (mode.ToLowerInvariant())
            {
                case "fm": return ReceiverOptions.Fm(decim);
                case "usb": return ReceiverOptions.Ssb(true, decim);
                case "lsb": return ReceiverOptions.Ssb(false, decim);
                case "cw": return ReceiverOptions.Cw(700.0, decim);
                default:
                    ReceiverOptions o = ReceiverOptions.Am(decim);
                    if (channelHz > 0.0) o.ChannelHz = channelHz;
                    return o;
            }
        }

        // ================================================================ spectrum

        private static int Spectrum(string[] args)
        {
            if (args.Length < 2) return Help();

            int fs;
            float[] data = WavFile.Read(args[1], out fs);
            int size = 8192;
            if (data.Length < size) size = 1024;

            float[] frame = new float[size];
            float[] re = new float[size];
            float[] im = new float[size];
            float[] win = Fft.Hann(size);

            Array.Copy(data, data.Length / 2 - size / 2, frame, 0, size);
            for (int i = 0; i < size; i++) { re[i] = frame[i] * win[i]; im[i] = 0.0f; }
            Fft.Forward(re, im);

            double[] db = new double[size / 2];
            double peak = 1e-12;
            for (int i = 1; i < size / 2; i++)
            {
                double m = Math.Sqrt(re[i] * re[i] + im[i] * im[i]) * 4.0 / size;
                db[i] = 20.0 * Math.Log10(Math.Max(m, 1e-12));
                if (db[i] > peak) peak = db[i];
            }

            Console.WriteLine("file: " + args[1] + "  fs=" + fs + " Hz  fft=" + size);
            DrawSpectrum(db, size, fs, peak);

            Console.WriteLine();
            Console.WriteLine("strongest bins:");
            for (int k = 0; k < 5; k++)
            {
                int best = 1;
                for (int i = 2; i < size / 2; i++) if (db[i] > db[best]) best = i;
                Console.WriteLine("  " + (best * (double)fs / size).ToString("F1", Inv).PadLeft(9) + " Hz  " + db[best].ToString("F1", Inv) + " dB");
                db[best] = -999.0;
            }

            return 0;
        }

        private static void DrawSpectrum(double[] db, int size, int fs, double peak)
        {
            Console.WriteLine();
            int rows = 32;
            int width = 96;

            for (int r = rows - 1; r >= 0; r--)
            {
                double from = peak - 80.0 * (rows - 1 - r) / (rows - 1);
                double to = peak - 80.0 * (rows - 2 - r) / (rows - 1);
                Console.Write(((int)from).ToString(Inv).PadLeft(5) + " dB |");

                for (int c = 0; c < width; c++)
                {
                    int bin = 1 + (int)((double)c / width * (size / 2 - 2));
                    Console.Write(db[bin] >= from && db[bin] < to ? '#' : ' ');
                }
                Console.WriteLine();
            }

            Console.WriteLine("        +" + new string('-', width));
            Console.Write("         ");
            for (int k = 0; k <= 8; k++)
                Console.Write((fs / 2.0 * k / 8.0).ToString("F0", Inv).PadLeft(k == 0 ? 1 : 11));
            Console.WriteLine("  Hz");
        }

        // ================================================================ demod

        private static int Demod(string[] args)
        {
            if (args.Length < 4) return Help();

            int fs;
            float[] input = WavFile.Read(args[1], out fs);
            double tune = double.Parse(args[3], Inv);
            string mode = args.Length > 4 ? args[4] : "am";
            int decim = args.Length > 5 ? int.Parse(args[5], Inv) : 4;

            Receiver rx = new Receiver(fs, tune, ParseOptions(mode, 0.0, decim));
            float[] output = Run(rx, input);

            WavFile.Write(args[2], output, output.Length, rx.OutputRate);
            Console.WriteLine("tuned to " + tune.ToString("F0", Inv) + " Hz, " + rx.Mode + ", decimation x" + decim +
                              ", wrote " + args[2] + " at " + rx.OutputRate + " Hz");
            return 0;
        }

        // ================================================================ scan

        private static int Scan(string[] args)
        {
            double seconds = args.Length > 1 ? double.Parse(args[1], Inv) : 5.0;
            int rate = args.Length > 2 ? int.Parse(args[2], Inv) : 48000;

            const int fftSize = 16384;
            RingBuffer ring = new RingBuffer(1 << 19);

            using (WaveIn capture = new WaveIn(rate, 1, CaptureBits))
            {
                capture.DataAvailable += delegate(float[] buf, int frames) { ring.Write(buf, 0, frames); };
                capture.Start();

                Console.WriteLine("capturing " + rate + " Hz mono 16 bit for " + seconds.ToString("F0", Inv) + " s...");
                Thread.Sleep((int)(seconds * 1000));
                capture.Stop();

                float[] frame = new float[fftSize];
                int got = ring.Read(frame, 0, fftSize);
                if (got < fftSize) { Console.Error.WriteLine("not enough samples captured"); return 1; }

                float[] re = new float[fftSize];
                float[] im = new float[fftSize];
                float[] win = Fft.Hann(fftSize);
                for (int i = 0; i < fftSize; i++) { re[i] = frame[i] * win[i]; im[i] = 0.0f; }
                Fft.Forward(re, im);

                double[] db = new double[fftSize / 2];
                double peak = -999.0;
                for (int i = 1; i < fftSize / 2; i++)
                {
                    double m = Math.Sqrt(re[i] * re[i] + im[i] * im[i]) * 4.0 / fftSize;
                    db[i] = 20.0 * Math.Log10(Math.Max(m, 1e-12));
                    if (db[i] > peak) peak = db[i];
                }

                DrawSpectrum(db, fftSize, rate, peak);

                Console.WriteLine();
                Console.WriteLine("strongest carriers:");
                double[] copy = (double[])db.Clone();
                for (int k = 0; k < 10; k++)
                {
                    int best = 1;
                    for (int i = 2; i < fftSize / 2; i++) if (copy[i] > copy[best]) best = i;
                    if (copy[best] < -140.0) break;

                    Console.WriteLine("  " + (best * (double)rate / fftSize).ToString("F1", Inv).PadLeft(10) + " Hz  " +
                                      copy[best].ToString("F1", Inv) + " dB");

                    for (int i = Math.Max(1, best - 6); i <= Math.Min(fftSize / 2 - 1, best + 6); i++) copy[i] = -999.0;
                }
            }

            return 0;
        }

        // ================================================================ listen

        private static int Listen(string[] args)
        {
            if (args.Length < 2) return Help();

            double tune = double.Parse(args[1], Inv);
            double seconds = args.Length > 2 ? double.Parse(args[2], Inv) : 15.0;
            string mode = args.Length > 3 ? args[3] : "am";
            int rate = args.Length > 4 ? int.Parse(args[4], Inv) : 48000;
            double channel = args.Length > 5 ? double.Parse(args[5], Inv) : 0.0;

            // Aim for roughly 12 kHz of audio, which every card accepts.
            int decim = Math.Max(1, rate / 12000);
            ReceiverOptions o = ParseOptions(mode, channel, decim);
            if (channel > 0.0) o.ChannelHz = channel;

            Receiver rx = new Receiver(rate, tune, o);

            // The channel rate is a division of the capture rate, so it is almost
            // never a rate the sound card will accept (12 kHz, 7.5 kHz, ...).
            int audioRate = PickAudioRate(rx.OutputRate);
            InterpolatingResampler resampler = (audioRate == rx.OutputRate)
                ? null
                : new InterpolatingResampler(rx.OutputRate, audioRate);

            Console.WriteLine("listening on " + tune.ToString("F0", Inv) + " Hz, " + rx.Mode +
                              ", capture " + rate + " Hz, channel " + o.ChannelHz.ToString("F0", Inv) +
                              " Hz, audio " + audioRate + " Hz");
            Console.WriteLine("press Ctrl+C to stop");

            RingBuffer inRing = new RingBuffer(1 << 18);
            float[] block = new float[rx.MaxBlock];
            float[] audio = new float[rx.MaxBlock];
            float[] play = new float[rx.MaxBlock * 4 + 64];

            using (WaveIn capture = new WaveIn(rate, 1, CaptureBits))
            using (WaveOut output = new WaveOut(audioRate, 1, 16))
            {
                capture.DataAvailable += delegate(float[] buf, int frames) { inRing.Write(buf, 0, frames); };
                capture.Start();

                DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
                int chunk = 4096;

                while (DateTime.UtcNow < deadline)
                {
                    if (inRing.Count < chunk) { Thread.Sleep(2); continue; }

                    int n = inRing.Read(block, 0, chunk);
                    int m = rx.Process(block, n, audio);
                    if (m <= 0) continue;

                    if (resampler == null)
                    {
                        output.Write(audio, m);
                    }
                    else
                    {
                        int w = resampler.Process(audio, m, play, play.Length);
                        if (w > 0) output.Write(play, w);
                    }
                }

                capture.Stop();
            }

            Console.WriteLine("stopped");
            return 0;
        }

        /// <summary>
        /// Sound cards accept a short list of rates. Pick the smallest one that
        /// does not throw away bandwidth, and fall back to 48 kHz.
        /// </summary>
        private static int PickAudioRate(int channelRate)
        {
            int[] rates = { 8000, 11025, 16000, 22050, 32000, 44100, 48000 };

            for (int i = 0; i < rates.Length; i++)
                if (rates[i] >= channelRate) return rates[i];

            return 48000;
        }

        // ================================================================ calib

        private static int Calibrate(string[] args)
        {
            if (args.Length < 2) return Help();

            double reference = double.Parse(args[1], Inv);
            double seconds = args.Length > 2 ? double.Parse(args[2], Inv) : 5.0;
            int rate = args.Length > 3 ? int.Parse(args[3], Inv) : 48000;

            RingBuffer ring = new RingBuffer(1 << 20);

            using (WaveIn capture = new WaveIn(rate, 1, CaptureBits))
            {
                capture.DataAvailable += delegate(float[] buf, int frames) { ring.Write(buf, 0, frames); };
                capture.Start();
                Console.WriteLine("capturing " + seconds.ToString("F0", Inv) + " s at " + rate + " Hz, looking for " +
                                  reference.ToString("F1", Inv) + " Hz...");
                Thread.Sleep((int)(seconds * 1000));
                capture.Stop();
            }

            float[] captured = new float[ring.Count];
            int n = ring.Read(captured, 0, captured.Length);
            if (n < 8192) { Console.Error.WriteLine("not enough samples"); return 1; }

            ClockCalib.Result r = ClockCalib.Measure(captured, n, rate, reference);
            Console.WriteLine("  " + r.ToString());
            Console.WriteLine("  nominal rate " + r.NominalRate.ToString("F1", Inv) + " Hz, actual " + r.CorrectedRate.ToString("F2", Inv) + " Hz");
            Console.WriteLine("  to correct a displayed frequency, multiply by " + (1.0 / (1.0 + r.Ppm * 1e-6)).ToString("F8", Inv));
            return 0;
        }

        // ================================================================ dcf77

        private static int Dcf77Live(string[] args)
        {
            double seconds = args.Length > 1 ? double.Parse(args[1], Inv) : 70.0;
            int rate = args.Length > 2 ? int.Parse(args[2], Inv) : 192000;

            if (rate < 160000)
            {
                Console.Error.WriteLine("DCF77 is at 77.5 kHz: you need at least 160 kHz of sample rate.");
                return 1;
            }

            Dcf77Receiver front = new Dcf77Receiver(rate, 77500.0);
            Dcf77Decoder dec = new Dcf77Decoder(front.EnvelopeRate);
            dec.FrameDecoded += delegate(Dcf77Frame f)
            {
                Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + "  " + f.ToString());
            };

            RingBuffer ring = new RingBuffer(1 << 20);
            float[] block = new float[front.MaxBlock];

            using (WaveIn capture = new WaveIn(rate, 1, CaptureBits))
            {
                capture.DataAvailable += delegate(float[] buf, int frames) { ring.Write(buf, 0, frames); };
                capture.Start();

                Console.WriteLine("listening for DCF77 on 77.5 kHz for " + seconds.ToString("F0", Inv) +
                                  " s. A telegram takes a full minute, so the first result arrives after 60 s.");

                DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
                while (DateTime.UtcNow < deadline)
                {
                    if (ring.Count < front.MaxBlock) { Thread.Sleep(5); continue; }
                    int n = ring.Read(block, 0, front.MaxBlock);
                    front.Process(block, n, dec);
                }

                capture.Stop();
            }

            Console.WriteLine("pulses " + dec.Pulses + ", frames " + dec.ValidFrames + "/" + dec.Frames + " valid");
            return dec.ValidFrames > 0 ? 0 : 1;
        }

        // ================================================================ web

        private static int WebServe(string[] args)
        {
            bool simulated = false;
            int port = 8080;
            int rate = 48000;
            double freq = 12000.0;
            DemodMode mode = DemodMode.Am;
            int bits = CaptureBits;

            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();

                if (a == "--sim") simulated = true;
                else if (a == "--port" && i + 1 < args.Length) port = int.Parse(args[++i], Inv);
                else if (a == "--rate" && i + 1 < args.Length) rate = int.Parse(args[++i], Inv);
                else if (a == "--freq" && i + 1 < args.Length) freq = double.Parse(args[++i], Inv);
                else if (a == "--bits" && i + 1 < args.Length) bits = int.Parse(args[++i], Inv);
                else if (a == "--mode" && i + 1 < args.Length) mode = ParseMode(args[++i]);
            }

            ISampleSource source;
            if (simulated)
            {
                source = new SyntheticSource(rate);
                Console.WriteLine("source: " + source.Description + " (no sound card needed)");
            }
            else
            {
                try
                {
                    source = new WaveInSource(rate, bits);
                    Console.WriteLine("source: " + source.Description);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("cannot open the sound card: " + ex.Message);
                    Console.Error.WriteLine("try:  radiocity web --sim     (synthetic band, no hardware)");
                    return 1;
                }
            }

            using (WebRadio radio = new WebRadio(source, freq, mode, DefaultChannel(mode)))
            using (HttpServer server = new HttpServer(port, radio.Handle))
            {
                source.Start();
                radio.Start();
                server.Start();

                Console.WriteLine();
                Console.WriteLine("  Radiocity " + Version + " listening on http://localhost:" + port + "/");
                Console.WriteLine("  tuned to " + freq.ToString("F0", Inv) + " Hz, " + radio.Mode +
                                  ", band 0.." + (rate / 2).ToString("F0", Inv) + " Hz");
                Console.WriteLine("  press Ctrl+C to stop");
                Console.WriteLine();

                ManualResetEvent stop = new ManualResetEvent(false);
                Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
                {
                    e.Cancel = true;
                    stop.Set();
                };

                stop.WaitOne();

                Console.WriteLine("stopping...");
                server.Stop();
                radio.Stop();
                source.Stop();
            }

            return 0;
        }

        // ================================================================ iq

        private static int IqCommand(string[] args)
        {
            if (args.Length < 2) return Help();

            switch (args[1].ToLowerInvariant())
            {
                case "record": return IqRecord(args);
                case "demod": return IqDemod(args);
                case "info": return IqInfo(args);
                default:
                    Console.Error.WriteLine("unknown iq command: " + args[1]);
                    return Help();
            }
        }

        /// <summary>
        /// Captures from the sound card, mixes down and decimates, and stores the
        /// result as I/Q. What gets recorded is exactly what the demodulators
        /// see, so a recording can be replayed through AM, then FM, then SSB,
        /// without going back to the air.
        /// </summary>
        private static int IqRecord(string[] args)
        {
            if (args.Length < 4) return Help();

            string path = args[2];
            double freq = double.Parse(args[3], Inv);
            double seconds = args.Length > 4 ? double.Parse(args[4], Inv) : 10.0;
            int rate = args.Length > 5 ? int.Parse(args[5], Inv) : 48000;
            int decim = args.Length > 6 ? int.Parse(args[6], Inv) : 4;

            int baseband = Math.Max(1, rate / decim);
            Nco nco = new Nco();
            nco.SetFrequency(freq, rate);

            DecimationChain chain = new DecimationChain(rate, baseband, 12000.0, 32768);

            RingBuffer ring = new RingBuffer(1 << 18);
            float[] block = new float[32768];
            float[] mi = new float[32768];
            float[] mq = new float[32768];
            float[] bi = new float[32768];
            float[] bq = new float[32768];

            // Growable rather than a fixed size: the capture length is a request,
            // not a promise, and the driver decides how much actually arrives.
            float[] outI = new float[baseband * 4];
            float[] outQ = new float[baseband * 4];
            int produced = 0;

            using (WaveIn capture = new WaveIn(rate, 1, CaptureBits))
            {
                capture.DataAvailable += delegate(float[] buf, int frames) { ring.Write(buf, 0, frames); };
                capture.Start();

                Console.WriteLine("recording " + seconds.ToString("F0", Inv) + " s at " + freq.ToString("F0", Inv) +
                                  " Hz, capture " + rate + " Hz " + CaptureBits + " bit, baseband " + baseband + " Hz");

                DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
                while (DateTime.UtcNow < deadline)
                {
                    if (ring.Count < 4096) { Thread.Sleep(2); continue; }

                    int n = ring.Read(block, 0, block.Length);
                    nco.MixReal(block, mi, mq, n);
                    int m = chain.Process(mi, mq, n, bi, bq);

                    if (produced + m > outI.Length)
                    {
                        Array.Resize(ref outI, (produced + m) * 2);
                        Array.Resize(ref outQ, (produced + m) * 2);
                    }

                    Array.Copy(bi, 0, outI, produced, m);
                    Array.Copy(bq, 0, outQ, produced, m);
                    produced += m;
                }

                capture.Stop();
            }

            IqFile.Write(path, outI, outQ, produced, baseband, freq, true);

            Console.WriteLine("wrote " + path + ": " + produced + " I/Q frames (" +
                              (produced / (double)baseband).ToString("F1", Inv) + " s), float32 stereo");
            return produced > 0 ? 0 : 1;
        }

        private static int IqDemod(string[] args)
        {
            if (args.Length < 4) return Help();

            float[] i, q;
            IqFile.Info info;
            IqFile.Read(args[2], out i, out q, out info);

            string mode = args.Length > 4 ? args[4] : "am";

            // Decimation 1: the recording is already baseband at its own rate, so
            // there is nothing to divide down. The channel filter still runs, at
            // that rate, and is what sets the bandwidth.
            ReceiverOptions o = ParseOptions(mode, 0.0, 1);
            o.Decimation = 1;

            Receiver rx = new Receiver(info.SampleRate, 0.0, o);

            float[] audio = new float[rx.MaxBlock];
            float[] result = new float[info.Frames + rx.MaxBlock];
            float[] ci = new float[rx.MaxBlock];
            float[] cq = new float[rx.MaxBlock];
            int pos = 0, outPos = 0;

            while (pos < info.Frames)
            {
                int n = Math.Min(rx.MaxBlock, info.Frames - pos);
                Array.Copy(i, pos, ci, 0, n);
                Array.Copy(q, pos, cq, 0, n);

                int m = rx.ProcessBaseband(ci, cq, n, audio);
                Array.Copy(audio, 0, result, outPos, m);
                outPos += m;
                pos += n;
            }

            WavFile.Write(args[3], result, outPos, rx.OutputRate);

            Console.WriteLine("recorded at " + info.CenterHz.ToString("F0", Inv) + " Hz, " +
                              info.SampleRate + " Hz baseband, " + info.Frames + " frames");
            Console.WriteLine("demodulated as " + rx.Mode + ", wrote " + args[3] + " at " + rx.OutputRate + " Hz");
            return 0;
        }

        private static int IqInfo(string[] args)
        {
            if (args.Length < 3) return Help();

            // Reads the samples too, not just the header: it is one code path
            // instead of two, and an I/Q capture is not a 4 GB file.
            float[] i, q;
            IqFile.Info info;
            IqFile.Read(args[2], out i, out q, out info);

            double seconds = info.SampleRate > 0 ? (double)info.Frames / info.SampleRate : 0.0;

            Console.WriteLine("file      : " + args[2]);
            Console.WriteLine("centre    : " + info.CenterHz.ToString("F0", Inv) + " Hz");
            Console.WriteLine("baseband  : " + info.SampleRate + " Hz, " + info.Bits + " bit, stereo (I/Q)");
            Console.WriteLine("frames    : " + info.Frames + " (" + seconds.ToString("F2", Inv) + " s)");
            Console.WriteLine("band      : " + (info.CenterHz - info.SampleRate / 2.0).ToString("F0", Inv) +
                              " .. " + (info.CenterHz + info.SampleRate / 2.0).ToString("F0", Inv) + " Hz");
            return 0;
        }

        private static DemodMode ParseMode(string text)
        {
            switch (text.ToLowerInvariant())
            {
                case "fm": return DemodMode.Fm;
                case "usb": return DemodMode.Usb;
                case "lsb": return DemodMode.Lsb;
                case "cw": return DemodMode.Cw;
                default: return DemodMode.Am;
            }
        }

        private static double DefaultChannel(DemodMode mode)
        {
            switch (mode)
            {
                case DemodMode.Fm: return 180000.0;
                case DemodMode.Usb:
                case DemodMode.Lsb: return 2400.0;
                case DemodMode.Cw: return 500.0;
                default: return 9000.0;
            }
        }
    }
}
