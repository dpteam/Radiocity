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

        /// <summary>
        /// Impulse blanker threshold, set with -nb. Null keeps it off, which is
        /// the right default: on a clean signal a blanker can only do harm.
        /// </summary>
        private static float? BlankerThreshold;

        /// <summary>Decode FM stereo. Set with -stereo / --stereo.</summary>
        private static bool StereoRequested;

        /// <summary>
        /// CTCSS tone squelch. Null means off; 0 means "any tone"; a number means
        /// that specific tone in Hz. Set with -ctcss (any) or -ctcss88.5.
        /// </summary>
        private static double? CtcssRequested;

        /// <summary>
        /// DCS code squelch. Null means off; 0 means "any code"; a number is the
        /// 9-bit code. Set with -dcs (any) or -dcs023.
        /// </summary>
        private static int? DcsRequested;

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
                else if (a == "-nb") BlankerThreshold = 12.0f;
                else if (a == "-stereo" || a == "--stereo") StereoRequested = true;
                else if (a == "-ctcss") CtcssRequested = 0.0;
                else if (a.StartsWith("-ctcss") && a.Length > 6)
                {
                    double t;
                    if (double.TryParse(a.Substring(6), NumberStyles.Float, Inv, out t))
                        CtcssRequested = Ctcss.Nearest(t);
                    else rest.Add(args[i]);
                }
                else if (a == "-dcs") DcsRequested = 0;
                else if (a.StartsWith("-dcs") && a.Length > 4)
                {
                    try { DcsRequested = Dcs.Parse(a.Substring(4)); }
                    catch (FormatException) { rest.Add(args[i]); }
                }
                else if (a.StartsWith("-nb") && a.Length > 3)
                {
                    float t;
                    if (float.TryParse(a.Substring(3), NumberStyles.Float, Inv, out t)) BlankerThreshold = t;
                    else rest.Add(args[i]);
                }
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
            Console.WriteLine("       add --iq for a quadrature front end (L=I, R=Q) or --sim-iq to simulate one.");
            Console.WriteLine("  radiocity iq record <file> <freqHz> [sec] [rate] [decim]");
            Console.WriteLine("                                          record baseband I/Q for offline analysis");
            Console.WriteLine("  radiocity iq demod <file> <out.wav> [mode]");
            Console.WriteLine("                                          demodulate a recording, try any mode");
            Console.WriteLine("  radiocity iq info <file>                show what a recording contains");
            Console.WriteLine();
            Console.WriteLine("  rate defaults to 48000; 192000 reaches to 96 kHz and is required for DCF77.");
            Console.WriteLine("  add -b24 (or -b8/-b16/-b32) to any command to pick the capture depth.");
            Console.WriteLine("  add -nb to enable the impulse noise blanker (-nb20 for a looser threshold).");
            Console.WriteLine("  add -stereo for FM stereo (needs --rate 192000 on 'web', or 128 kHz+ capture).");
            Console.WriteLine("  add -ctcss for CTCSS tone squelch (-ctcss88.5 for a specific tone),");
            Console.WriteLine("      or -dcs for DCS code squelch (-dcs023 for a specific code).");
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
            ok &= RunFmStereoTest();
            Console.WriteLine();
            ok &= RunNfmTest();
            Console.WriteLine();
            ok &= RunDcsTest();
            Console.WriteLine();
            ok &= RunRdsTest();
            Console.WriteLine();
            ok &= RunSsbTest();
            Console.WriteLine();
            ok &= RunCwTest();
            Console.WriteLine();
            ok &= RunCalibTest();
            Console.WriteLine();
            ok &= RunDecimationChainTest();
            Console.WriteLine();
            ok &= RunSquelchTest();
            Console.WriteLine();
            ok &= RunBlankerTest();
            Console.WriteLine();
            ok &= RunIqTest();
            Console.WriteLine();
            ok &= RunImbalanceTest();
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

        private static bool RunDecimationChainTest()
        {
            // The claim "multi-stage is cheaper" is only worth making if it can
            // be measured. Same signal, same channel, two ways of getting there.
            const double fs = 192000.0;
            const double carrier = 30000.0;
            const double tone = 1000.0;
            const double channel = 9000.0;
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
            o.ChannelHz = channel;

            Receiver rx = new Receiver(fs, carrier, o);
            float[] output = Run(rx, input);

            double residual;
            double level = MeasureTone(output, rx.OutputRate, tone, out residual);

            int singleTaps = DecimationChain.EstimateSingleStageTaps(fs, rx.OutputRate, channel);
            double singleCost = singleTaps * fs;
            double chainCost = rx.FilterCost;
            double saving = singleCost / Math.Max(1.0, chainCost);

            Console.WriteLine("decimation self-test");
            Console.WriteLine("  192 kHz -> " + rx.OutputRate + " Hz, " + decim + "x in " + rx.DecimationStages + " stage(s)");

            int[] taps = rx.StageTaps;
            double[] rates = rx.StageRates;
            for (int s = 0; s < taps.Length; s++)
            {
                Console.WriteLine("    stage " + (s + 1) + ": " + taps[s] + " taps at " +
                                  rates[s].ToString("F0", Inv) + " Hz = " +
                                  (taps[s] * rates[s] / 1e6).ToString("F1", Inv) + " M/s");
            }

            Console.WriteLine("  single stage would need " + singleTaps + " taps = " +
                              (singleCost / 1e6).ToString("F1", Inv) + " M/s");
            Console.WriteLine("  this chain: " + (chainCost / 1e6).ToString("F1", Inv) + " M/s (" +
                              saving.ToString("F1", Inv) + "x cheaper)");
            Console.WriteLine("  tone " + level.ToString("F3", Inv) + ", distortion " +
                              Db(level / residual).ToString("F1", Inv) + " dB below");

            bool ok = Math.Abs(level - 0.30) < 0.03 && saving > 1.5 && rx.DecimationStages > 1;
            Console.WriteLine(ok ? "  PASS" : "  FAIL: chain output or the saving is wrong");
            return ok;
        }

        private static bool RunSquelchTest()
        {
            const double fs = 48000.0;
            const double carrier = 12000.0;
            int n = (int)(fs * 3.0);

            // One second of silence, one second of carrier, one second of silence.
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
            bool at07 = false, at17 = false, at27 = false;
            int pos = 0, seen = 0;

            // 4096-sample blocks, so the checkpoints land inside the intended
            // second rather than at whatever block boundary happens to fall.
            while (pos < n)
            {
                int chunk = Math.Min(4096, n - pos);
                Array.Copy(input, pos, block, 0, chunk);
                rx.Process(block, chunk, audio);
                pos += chunk;

                double t = (double)pos / fs;
                if (seen == 0 && t >= 0.7) { at07 = rx.Squelch.Open; seen = 1; }
                else if (seen == 1 && t >= 1.7) { at17 = rx.Squelch.Open; seen = 2; }
                else if (seen == 2 && t >= 2.7) { at27 = rx.Squelch.Open; seen = 3; }
            }

            Console.WriteLine("squelch self-test");
            Console.WriteLine("  silence / carrier / silence, threshold -40 dBFS");
            Console.WriteLine("  gate at 0.7 s: " + (at07 ? "open" : "muted") +
                              ", 1.7 s: " + (at17 ? "open" : "muted") +
                              ", 2.7 s: " + (at27 ? "open" : "muted"));

            bool ok = !at07 && at17 && !at27;
            Console.WriteLine(ok ? "  PASS" : "  FAIL: gate did not follow the signal");
            return ok;
        }

        private static bool RunFmStereoTest()
        {
            // A stereo broadcast sends M = (L+R)/2, S = (L-R)/2, the S sideband on
            // a 38 kHz subcarrier, and a 19 kHz pilot to say where that subcarrier
            // is. This builds exactly that, FM-modulates it, and checks that two
            // different tones come back out of two different channels.
            //
            // The FM signal is generated as complex baseband rather than as a real
            // RF tone, and that is not a shortcut. Mixing a real signal down by a
            // carrier creates an image at -(2*fc + df); with +-25 kHz of deviation
            // and a 12 kHz carrier that image lands back inside the band and
            // aliases, because a sound card has no analogue filter in this test.
            // A quadrature front end is what makes wideband FM possible at all,
            // so the test exercises that path.
            const double fs = 192000.0;
            const double leftTone = 1000.0;
            const double rightTone = 3000.0;
            const double deviation = 10000.0;

            int n = (int)(fs * 4.0);

            float[] mpx = new float[n];
            for (int k = 0; k < n; k++)
            {
                double t = k / fs;
                double l = 0.5 * Math.Cos(2.0 * Math.PI * leftTone * t);
                double r = 0.5 * Math.Cos(2.0 * Math.PI * rightTone * t);

                double m = (l + r) * 0.5;
                double s = (l - r) * 0.5;

                mpx[k] = (float)(m + s * Math.Cos(2.0 * Math.PI * 38000.0 * t) +
                                 0.1 * Math.Cos(2.0 * Math.PI * 19000.0 * t));
            }

            float[] bi = new float[n];
            float[] bq = new float[n];
            double phase = 0.0;
            for (int k = 0; k < n; k++)
            {
                bi[k] = (float)Math.Cos(phase);
                bq[k] = (float)Math.Sin(phase);
                phase += 2.0 * Math.PI * (deviation * mpx[k]) / fs;
            }

            ReceiverOptions o = ReceiverOptions.FmStereoMode(1);
            o.DeemphasisUs = 0.0;          // keep the test tones flat
            o.ChannelHz = 180000.0;
            o.DeviationHz = deviation;

            Receiver rx = new Receiver(fs, 0.0, o);
            float[] output = RunComplex(rx, bi, bq);

            if (rx.Channels != 2)
            {
                Console.WriteLine("FM stereo self-test");
                Console.WriteLine("  FAIL: receiver did not switch to two channels");
                return false;
            }

            double lTone = MeasureChannel(output, rx.OutputRate, leftTone, 0);
            double rTone = MeasureChannel(output, rx.OutputRate, rightTone, 1);
            double lCross = MeasureChannel(output, rx.OutputRate, rightTone, 0);
            double rCross = MeasureChannel(output, rx.OutputRate, leftTone, 1);

            double leftSep = Db(lTone / Math.Max(lCross, 1e-12));
            double rightSep = Db(rTone / Math.Max(rCross, 1e-12));

            Console.WriteLine("FM stereo self-test");
            Console.WriteLine("  L carries " + leftTone.ToString("F0", Inv) + " Hz, R carries " +
                              rightTone.ToString("F0", Inv) + " Hz, pilot 19 kHz, subcarrier 38 kHz");
            Console.WriteLine("  audio rate " + rx.OutputRate + " Hz, " + rx.Channels + " channels, pilot " +
                              rx.StereoPilot.ToString("F3", Inv) +
                              " (" + (rx.StereoDetected ? "locked" : "no lock") + ")");
            Console.WriteLine("  L: wanted " + lTone.ToString("F3", Inv) + ", leaked " + lCross.ToString("F3", Inv) +
                              "  separation " + leftSep.ToString("F1", Inv) + " dB");
            Console.WriteLine("  R: wanted " + rTone.ToString("F3", Inv) + ", leaked " + rCross.ToString("F3", Inv) +
                              "  separation " + rightSep.ToString("F1", Inv) + " dB");

            bool ok = leftSep > 30.0 && rightSep > 30.0;
            Console.WriteLine(ok ? "  PASS" : "  FAIL: channels are not separated");
            return ok;
        }

        /// <summary>Runs complex baseband through the chain, as a quadrature front end would.</summary>
        private static float[] RunComplex(Receiver rx, float[] i, float[] q)
        {
            int ch = rx.Channels;
            int n = i.Length;

            float[] output = new float[(n + rx.MaxBlock) * ch];
            float[] blockI = new float[rx.MaxBlock];
            float[] blockQ = new float[rx.MaxBlock];
            float[] audio = new float[rx.MaxBlock * ch];
            int produced = 0, pos = 0;

            while (pos < n)
            {
                int chunk = Math.Min(rx.MaxBlock, n - pos);
                Array.Copy(i, pos, blockI, 0, chunk);
                Array.Copy(q, pos, blockQ, 0, chunk);
                int m = rx.ProcessBaseband(blockI, blockQ, chunk, audio);
                Array.Copy(audio, 0, output, produced, m * ch);
                produced += m * ch;
                pos += chunk;
            }

            float[] trimmed = new float[produced];
            Array.Copy(output, trimmed, produced);
            return trimmed;
        }

        /// <summary>Goertzel on one channel of an interleaved buffer, last second.</summary>
        private static double MeasureChannel(float[] interleaved, int rate, double hz, int channel)
        {
            int frames = interleaved.Length / 2;
            int count = Math.Min(rate, frames);
            int offset = frames - count;

            double w = 2.0 * Math.PI * hz / rate;
            double coeff = 2.0 * Math.Cos(w);
            double s1 = 0.0, s2 = 0.0;

            for (int k = 0; k < count; k++)
            {
                double s0 = interleaved[(offset + k) * 2 + channel] + coeff * s1 - s2;
                s2 = s1;
                s1 = s0;
            }

            double re = s1 - s2 * Math.Cos(w);
            double im = s2 * Math.Sin(w);
            return Math.Sqrt(re * re + im * im) * 2.0 / count;
        }

        private static bool RunRdsTest()
        {
            // RDS is a digital protocol, so this test can be exact: build a known
            // group sequence, modulate it, and require the exact strings back.
            const double fs = 192000.0;
            const double amplitude = 0.03;

            // First the code itself, with no radio in the way. If this fails the
            // problem is the CRC or the framing, and debugging DSP at the same
            // time would be a waste of an evening.
            int block = RdsEncoder.Block(0x1234, RdsCode.OffsetA);
            int syndrome = RdsCode.Syndrome(block);
            int corrected;
            int errors;
            int damaged = block ^ (1 << 7) ^ (1 << 19);
            bool repaired = RdsCode.TryCorrect(damaged, RdsCode.OffsetA, out corrected, out errors);

            Console.WriteLine("RDS self-test");
            Console.WriteLine("  block code: syndrome " + syndrome.ToString("X3", Inv) +
                              " (expected " + RdsCode.OffsetA.ToString("X3", Inv) + ")");
            Console.WriteLine("  2-bit damage repaired: " + (repaired && corrected == block) +
                              ", errors found " + errors);

            bool codeOk = syndrome == RdsCode.OffsetA && repaired && corrected == block;

            // Now the whole chain: pilot + RDS on a composite, decoded from scratch.
            int groups = 24;
            int[] bits = new int[groups * 104];
            int at = 0;

            for (int g = 0; g < groups; g++)
            {
                int[] words = BuildRdsGroup(g);
                int[] encoded = RdsEncoder.EncodeBits(words);
                Array.Copy(encoded, 0, bits, at, encoded.Length);
                at += encoded.Length;
            }

            int n = (int)Math.Round(bits.Length * (fs / RdsCode.BitRateHz)) + 4096;
            float[] composite = new float[n];

            // The pilot, without which the decoder has no phase reference.
            for (int k = 0; k < n; k++)
                composite[k] = (float)(0.1 * Math.Cos(2.0 * Math.PI * RdsCode.PilotHz * k / fs));

            RdsEncoder.Render(composite, fs, bits, amplitude);

            RdsDecoder dec = new RdsDecoder(fs);
            dec.Process(composite, n);

            Console.WriteLine("  decoded " + dec.Groups + " groups, " + dec.BadBlocks + " bad blocks, " +
                              dec.CorrectedBits + " corrected bits");
            Console.WriteLine("  PI " + dec.PiCode.ToString("X4", Inv) + ", PS \"" + (dec.ProgramService ?? "") +
                              "\", RT \"" + dec.RadioText + "\"");

            bool ok = codeOk &&
                      dec.PiCode == 0x1234 &&
                      dec.ProgramService == "TEST FM " &&
                      dec.RadioText.StartsWith("HELLO RADIO", StringComparison.Ordinal);

            Console.WriteLine(ok ? "  PASS" : "  FAIL: RDS did not decode");
            return ok;
        }

        /// <summary>
        /// One group of the test sequence: four groups of type 0 carry the eight
        /// characters of the station name, the rest carry RadioText.
        /// </summary>
        private static int[] BuildRdsGroup(int index)
        {
            const int pi = 0x1234;
            string ps = "TEST FM ";
            string rt = "HELLO RADIO WORLD FROM RADIOCITY";

            if (index < 4)
            {
                // Type 0A: block D holds two characters, segment 0-3.
                int seg = index;
                int b = (0 << 12) | (0 << 11) | (1 << 10) | (0 << 5) | seg;
                int c = 0xE0E0;                       // no alternative frequencies
                int d = (ps[seg * 2] << 8) | ps[seg * 2 + 1];

                return new int[] { pi, b, c, d };
            }

            // Type 2A: four characters per group, segments 0-15.
            int rtSeg = (index - 4) % 16;
            int offset = rtSeg * 4;

            int ch0 = offset < rt.Length ? rt[offset] : ' ';
            int ch1 = offset + 1 < rt.Length ? rt[offset + 1] : ' ';
            int ch2 = offset + 2 < rt.Length ? rt[offset + 2] : ' ';
            int ch3 = offset + 3 < rt.Length ? rt[offset + 3] : ' ';

            int bb = (2 << 12) | (0 << 11) | (1 << 10) | (0 << 5) | (0 << 4) | rtSeg;
            int cc = (ch0 << 8) | ch1;
            int dd = (ch2 << 8) | ch3;

            return new int[] { pi, bb, cc, dd };
        }

        private static bool RunNfmTest()
        {
            // Narrowband FM: +-2.5 kHz deviation, a 1 kHz voice tone, and an
            // 88.5 Hz CTCSS tone riding underneath it. The CTCSS tone must be
            // detected AND must not be audible in the audio output.
            const double fs = 48000.0;
            const double tone = 1000.0;
            const double ctcssHz = 88.5;
            const double deviation = 2500.0;

            int n = (int)(fs * 6.0);

            float[] bi = new float[n];
            float[] bq = new float[n];
            double phase = 0.0;

            for (int k = 0; k < n; k++)
            {
                double t = k / fs;
                double voice = 0.5 * Math.Cos(2.0 * Math.PI * tone * t);
                double ctcss = 0.15 * Math.Cos(2.0 * Math.PI * ctcssHz * t);

                bi[k] = (float)Math.Cos(phase);
                bq[k] = (float)Math.Sin(phase);
                phase += 2.0 * Math.PI * deviation * (voice + ctcss) / fs;
            }

            ReceiverOptions o = ReceiverOptions.Nfm(1);
            o.AgcEnabled = false;

            Receiver rx = new Receiver(fs, 0.0, o);
            float[] output = RunComplex(rx, bi, bq);

            double residual, unused;
            double voiceLevel = MeasureTone(output, rx.OutputRate, tone, out residual);
            double ctcssLeak = MeasureTone(output, rx.OutputRate, ctcssHz, out unused);

            Console.WriteLine("narrowband FM self-test");
            Console.WriteLine("  +-2.5 kHz deviation, 1 kHz voice, 88.5 Hz CTCSS, 12.5 kHz channel");
            Console.WriteLine("  baseband " + rx.OutputRate + " Hz audio, " + rx.Channels + " channel(s)");
            Console.WriteLine("  voice tone  : " + voiceLevel.ToString("F3", Inv));
            Console.WriteLine("  CTCSS leak  : " + ctcssLeak.ToString("F4", Inv) +
                              "  (" + Db(voiceLevel / Math.Max(ctcssLeak, 1e-12)).ToString("F1", Inv) + " dB below voice)");

            bool toneOk = false;
            if (rx.Ctcss != null)
            {
                toneOk = Math.Abs(rx.Ctcss.ToneHz - ctcssHz) < 0.5;
                Console.WriteLine("  CTCSS detected: " + (rx.Ctcss.ToneHz > 0
                    ? rx.Ctcss.ToneHz.ToString("F1", Inv) + " Hz"
                    : "none") + " at level " + rx.Ctcss.ToneLevel.ToString("F4", Inv));
            }

            // The voice tone survives, the sub-audible tone does not, and the
            // detector names the right frequency.
            bool ok = Math.Abs(voiceLevel - 0.5) < 0.08 && toneOk &&
                      Db(voiceLevel / Math.Max(ctcssLeak, 1e-12)) > 20.0;

            Console.WriteLine(ok ? "  PASS" : "  FAIL: narrowband FM path is wrong");
            return ok;
        }

        private static bool RunDcsTest()
        {
            // DCS is a Golay-coded word repeating at 134.4 bps with no framing, so
            // the interesting questions are: does the word code work, does the
            // decoder find the 23-bit boundary from nothing, and does a wrong code
            // actually keep the audio silent.
            const double fs = 48000.0;
            const double deviation = 2500.0;

            int code = Dcs.Parse("023");

            Console.WriteLine("DCS self-test");

            // The code itself, with no radio in the way.
            int word = Golay23.Encode(code);
            int syndrome = Golay23.Syndrome(word);
            int data;
            int errors;

            bool roundTrip = Golay23.TryDecode(word, out data, out errors) &&
                             data == code && errors == 0;

            // Three flipped bits is the documented limit; a fourth must not be
            // silently accepted as a different valid code.
            int damaged = word ^ 1 ^ (1 << 9) ^ (1 << 17);
            int fixedData;
            int fixedErrors;
            bool repaired = Golay23.TryDecode(damaged, out fixedData, out fixedErrors) &&
                            fixedData == code && fixedErrors == 3;

            Console.WriteLine("  Golay(23,12): syndrome " + syndrome.ToString("X3", Inv) +
                              ", round trip " + roundTrip + ", 3-bit damage repaired " + repaired);

            bool codeOk = roundTrip && repaired && syndrome == 0;

            // Normal code through a whole narrowband FM link.
            float[] bi, bq;
            BuildDcsSignal(code, false, fs, deviation, 6.0, out bi, out bq);

            ReceiverOptions o = ReceiverOptions.Nfm(1);
            o.AgcEnabled = false;
            o.DcsCode = code;

            Receiver rx = new Receiver(fs, 0.0, o);
            float[] audio = RunComplex(rx, bi, bq);

            int decoded = rx.Dcs != null ? rx.Dcs.Code : -1;
            bool synced = rx.Dcs != null && rx.Dcs.Synced;
            double rmsOpen = Rms(audio, audio.Length * 2 / 3, audio.Length / 3);

            Console.WriteLine("  normal   : decoded " + Dcs.Format(decoded) +
                              " (" + decoded + "), synced " + synced +
                              ", words " + (rx.Dcs != null ? rx.Dcs.Words : 0) +
                              ", audio rms " + rmsOpen.ToString("F3", Inv));

            // The inverted variant: every bit flipped. Same code, different bits.
            BuildDcsSignal(code, true, fs, deviation, 6.0, out bi, out bq);

            ReceiverOptions oInv = ReceiverOptions.Nfm(1);
            oInv.AgcEnabled = false;
            oInv.DcsCode = code;

            Receiver rxInv = new Receiver(fs, 0.0, oInv);
            RunComplex(rxInv, bi, bq);

            bool invSynced = rxInv.Dcs != null && rxInv.Dcs.Synced;
            int invCode = rxInv.Dcs != null ? rxInv.Dcs.Code : -1;
            bool invFlag = rxInv.Dcs != null && rxInv.Dcs.Inverted;

            Console.WriteLine("  inverted : decoded " + Dcs.Format(invCode) +
                              " (" + invCode + "), synced " + invSynced +
                              ", inverted " + invFlag);

            // The wrong code must close the gate, not merely soften the audio.
            BuildDcsSignal(code, false, fs, deviation, 6.0, out bi, out bq);

            ReceiverOptions oWrong = ReceiverOptions.Nfm(1);
            oWrong.AgcEnabled = false;
            oWrong.DcsCode = Dcs.Parse("754");

            Receiver rxWrong = new Receiver(fs, 0.0, oWrong);
            float[] wrongAudio = RunComplex(rxWrong, bi, bq);

            double rmsWrong = Rms(wrongAudio, wrongAudio.Length * 2 / 3, wrongAudio.Length / 3);

            Console.WriteLine("  wrong code: wanted 754, heard " + Dcs.Format(rxWrong.Dcs.Code) +
                              ", audio rms " + rmsWrong.ToString("F5", Inv));

            bool ok = codeOk &&
                      Dcs.SameCode(decoded, code) && synced && rmsOpen > 0.05 &&
                      Dcs.SameCode(invCode, code) && invSynced && invFlag &&
                      rmsWrong < rmsOpen * 0.02;

            Console.WriteLine(ok ? "  PASS" : "  FAIL: DCS path is wrong");
            return ok;
        }

        /// <summary>Narrowband FM carrying a voice tone and a DCS code word.</summary>
        private static void BuildDcsSignal(int code, bool inverted, double fs, double deviation,
                                           double seconds, out float[] i, out float[] q)
        {
            int n = (int)(fs * seconds);
            i = new float[n];
            q = new float[n];

            int[] bits = DcsEncoder.Bits(code, inverted);
            double bitRate = DcsDecoder.BitRateHz;

            double phase = 0.0;

            for (int k = 0; k < n; k++)
            {
                double t = k / fs;

                double voice = 0.5 * Math.Cos(2.0 * Math.PI * 900.0 * t);

                // DCS sits at about 30% of the deviation on a real handheld.
                long bitIndex = (long)(t * bitRate);
                int bit = bits[(int)(bitIndex % bits.Length)];
                double dcs = (bit == 1 ? 0.30 : -0.30);

                i[k] = (float)Math.Cos(phase);
                q[k] = (float)Math.Sin(phase);

                phase += 2.0 * Math.PI * deviation * (voice + dcs) / fs;
            }
        }

        private static double Rms(float[] buf, int offset, int count)
        {
            double sum = 0.0;
            for (int k = 0; k < count; k++) sum += buf[offset + k] * (double)buf[offset + k];
            return Math.Sqrt(sum / Math.Max(1, count));
        }

        private static bool RunImbalanceTest()
        {
            // A real quadrature front end has two analogue paths that are never
            // identical. The result is an image: a carrier at +f leaks into -f.
            // This injects a known imbalance, measures the image, and checks that
            // the blind correction removes it without being told what it was.
            const double rate = 12000.0;
            const double tone = 1500.0;
            const double amplitude = 0.4;
            const double gainError = 0.02;          // 2% = 0.17 dB
            const double phaseErrorDeg = 3.0;
            int n = (int)(rate * 4.0);

            double phi = phaseErrorDeg * Math.PI / 180.0;
            float[] rawI = new float[n];
            float[] rawQ = new float[n];

            for (int k = 0; k < n; k++)
            {
                double phase = 2.0 * Math.PI * tone * k / rate;
                double i = amplitude * Math.Cos(phase);
                double q = amplitude * Math.Sin(phase);

                rawI[k] = (float)i;
                rawQ[k] = (float)(q * (1.0 + gainError) * Math.Cos(phi) + i * Math.Sin(phi));
            }

            double wantedBefore = ImageLevel(rawI, rawQ, n, rate, tone);
            double imageBefore = ImageLevel(rawI, rawQ, n, rate, -tone);

            float[] fixedI = (float[])rawI.Clone();
            float[] fixedQ = (float[])rawQ.Clone();

            IqImbalance corr = new IqImbalance(rate);
            corr.Process(fixedI, fixedQ, n);

            double wantedAfter = ImageLevel(fixedI, fixedQ, n, rate, tone);
            double imageAfter = ImageLevel(fixedI, fixedQ, n, rate, -tone);

            double beforeDb = Db(imageBefore / Math.Max(wantedBefore, 1e-12));
            double afterDb = Db(imageAfter / Math.Max(wantedAfter, 1e-12));

            Console.WriteLine("I/Q imbalance self-test");
            Console.WriteLine("  injected " + (gainError * 100.0).ToString("F1", Inv) + "% gain error, " +
                              phaseErrorDeg.ToString("F1", Inv) + " deg phase error");
            Console.WriteLine("  measured " + (corr.GainRatio * 100.0 - 100.0).ToString("F2", Inv) + "% gain, " +
                              corr.PhaseErrorDeg.ToString("F2", Inv) + " deg phase");
            Console.WriteLine("  image before : " + beforeDb.ToString("F1", Inv) + " dB");
            Console.WriteLine("  image after  : " + afterDb.ToString("F1", Inv) + " dB");
            Console.WriteLine("  improvement  : " + (beforeDb - afterDb).ToString("F1", Inv) + " dB");

            // 2% and 3 degrees should give roughly -35 dB of image; after
            // correction it should be far below that.
            bool ok = beforeDb > -45.0 && beforeDb < -25.0 && afterDb < -60.0 &&
                      Math.Abs(corr.GainRatio - (1.0 + gainError)) < 0.005;

            Console.WriteLine(ok ? "  PASS" : "  FAIL: correction did not remove the image");
            return ok;
        }

        /// <summary>
        /// Magnitude of the component at hz, measured over the second half so the
        /// statistics have settled. Used to compare a wanted tone with its image.
        /// </summary>
        private static double ImageLevel(float[] i, float[] q, int n, double rate, double hz)
        {
            int offset = n / 2;
            int count = n - offset;

            double re = 0.0, im = 0.0;
            for (int k = 0; k < count; k++)
            {
                double phase = -2.0 * Math.PI * hz * (offset + k) / rate;
                double c = Math.Cos(phase);
                double s = Math.Sin(phase);
                re += i[offset + k] * c - q[offset + k] * s;
                im += i[offset + k] * s + q[offset + k] * c;
            }

            return 2.0 * Math.Sqrt(re * re + im * im) / count;
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
            int ch = rx.Channels;
            float[] output = new float[(input.Length + rx.MaxBlock) * ch];
            float[] block = new float[rx.MaxBlock];
            float[] audio = new float[rx.MaxBlock * ch];
            int produced = 0, pos = 0;

            while (pos < input.Length)
            {
                int chunk = Math.Min(rx.MaxBlock, input.Length - pos);
                Array.Copy(input, pos, block, 0, chunk);
                int m = rx.Process(block, chunk, audio);
                Array.Copy(audio, 0, output, produced, m * ch);
                produced += m * ch;
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
            ReceiverOptions o;

            switch (mode.ToLowerInvariant())
            {
                case "fm": o = ReceiverOptions.Fm(decim); break;
                case "nfm": o = ReceiverOptions.Nfm(decim); break;
                case "usb": o = ReceiverOptions.Ssb(true, decim); break;
                case "lsb": o = ReceiverOptions.Ssb(false, decim); break;
                case "cw": o = ReceiverOptions.Cw(700.0, decim); break;
                default:
                    o = ReceiverOptions.Am(decim);
                    if (channelHz > 0.0) o.ChannelHz = channelHz;
                    break;
            }

            o.BlankerThreshold = BlankerThreshold;
            o.CtcssTone = CtcssRequested;
            o.DcsCode = DcsRequested;
            return o;
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

            // Stereo is opt-in: it needs a wide capture and doubles the output.
            if (StereoRequested) o.FmStereo = true;

            Receiver rx = new Receiver(rate, tune, o);

            int channels = rx.Channels;

            // The channel rate is a division of the capture rate, so it is almost
            // never a rate the sound card will accept (12 kHz, 7.5 kHz, ...).
            // Stereo is the exception: the stereo decoder already outputs 48 kHz,
            // which every card accepts, so it is played as-is.
            int audioRate = (channels == 2) ? rx.OutputRate : PickAudioRate(rx.OutputRate);
            InterpolatingResampler resampler = (audioRate == rx.OutputRate)
                ? null
                : new InterpolatingResampler(rx.OutputRate, audioRate);

            Console.WriteLine("listening on " + tune.ToString("F0", Inv) + " Hz, " + rx.Mode +
                              (channels == 2 ? " (stereo)" : "") +
                              ", capture " + rate + " Hz, channel " + o.ChannelHz.ToString("F0", Inv) +
                              " Hz, audio " + audioRate + " Hz");
            Console.WriteLine("press Ctrl+C to stop");

            RingBuffer inRing = new RingBuffer(1 << 18);
            float[] block = new float[rx.MaxBlock];
            float[] audio = new float[rx.MaxBlock * channels];
            float[] play = new float[rx.MaxBlock * 4 * channels + 64];

            using (WaveIn capture = new WaveIn(rate, 1, CaptureBits))
            using (WaveOut output = new WaveOut(audioRate, channels, 16))
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
                        // Already at the card's rate. m is frames, samples are m*channels.
                        output.Write(audio, m * channels);
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
            bool quadrature = false;
            bool simulatedQuadrature = false;
            bool simulatedFm = false;
            bool simulatedRadio = false;
            bool simulatedDcs = false;
            bool correctImbalance = true;
            int port = 8080;
            int rate = 48000;
            double freq = 12000.0;
            DemodMode mode = DemodMode.Am;
            int bits = CaptureBits;

            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();

                if (a == "--sim") simulated = true;
                else if (a == "--iq") quadrature = true;
                else if (a == "--sim-iq") simulatedQuadrature = true;
                else if (a == "--sim-fm") simulatedFm = true;
                else if (a == "--sim-radio") simulatedRadio = true;
                else if (a == "--sim-dcs") simulatedDcs = true;
                else if (a == "--no-imbalance") correctImbalance = false;
                else if (a == "--port" && i + 1 < args.Length) port = int.Parse(args[++i], Inv);
                else if (a == "--rate" && i + 1 < args.Length) rate = int.Parse(args[++i], Inv);
                else if (a == "--freq" && i + 1 < args.Length) freq = double.Parse(args[++i], Inv);
                else if (a == "--bits" && i + 1 < args.Length) bits = int.Parse(args[++i], Inv);
                else if (a == "--blank" && i + 1 < args.Length) BlankerThreshold = float.Parse(args[++i], Inv);
                else if (a == "--mode" && i + 1 < args.Length) mode = ParseMode(args[++i]);
            }

            ISampleSource source;

            if (simulatedRadio || simulatedDcs)
            {
                // A handheld: narrowband FM, voice-like audio, and a squelch code.
                if (simulatedDcs)
                {
                    int dcs = DcsRequested.HasValue && DcsRequested.Value > 0 ? DcsRequested.Value : Dcs.Parse("023");
                    source = new SyntheticRadioSource(rate, 2500.0, 0.0, dcs);
                    DcsRequested = dcs;
                }
                else
                {
                    source = new SyntheticRadioSource(rate, 2500.0, 88.5);
                }

                mode = DemodMode.Nfm;
                freq = 0.0;
                Console.WriteLine("source: " + source.Description + " (no hardware needed)");
            }
            else if (simulatedFm)
            {
                // A whole FM stereo station with RDS, so the web UI can be shown
                // end to end on a machine with no sound card and no antenna.
                source = new SyntheticFmSource(rate, 75000.0);
                StereoRequested = true;
                mode = DemodMode.Fm;
                freq = 0.0;
                Console.WriteLine("source: " + source.Description + " (no hardware needed)");
            }
            else if (simulatedQuadrature)
            {
                // 2% gain error and 3 degrees of phase error: a plausible
                // SoftRock, and enough that the image is audible without
                // correction.
                source = new SyntheticQuadratureSource(rate, 0.02, 3.0);
                Console.WriteLine("source: " + source.Description + " (no hardware needed)");
            }
            else if (quadrature)
            {
                try
                {
                    source = new QuadratureSource(rate, bits);
                    Console.WriteLine("source: " + source.Description);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("cannot open the sound card: " + ex.Message);
                    Console.Error.WriteLine("try:  radiocity web --sim-iq     (synthetic quadrature)");
                    return 1;
                }
            }
            else if (simulated)
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

            using (WebRadio radio = new WebRadio(source, freq, mode, DefaultChannel(mode), correctImbalance, StereoRequested))
            using (HttpServer server = new HttpServer(port, radio.Handle))
            {
                if (BlankerThreshold.HasValue) radio.SetBlanker(BlankerThreshold);

                source.Start();
                radio.Start();
                server.Start();

                Console.WriteLine();
                Console.WriteLine("  Radiocity " + Version + " listening on http://localhost:" + port + "/");
                Console.WriteLine("  tuned to " + freq.ToString("F0", Inv) + " Hz, " + radio.Mode +
                                  ", band " + (radio.IsQuadrature ? "-" : "") +
                                  (rate / 2).ToString("F0", Inv) + " .. " + (rate / 2).ToString("F0", Inv) + " Hz" +
                                  (radio.IsQuadrature ? " around the LO" : ""));
                if (radio.IsQuadrature && correctImbalance) Console.WriteLine("  I/Q imbalance correction: on");
                Console.WriteLine("  press Ctrl+C to stop");                Console.WriteLine();

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
                case DemodMode.Nfm: return 11000.0;
                case DemodMode.Usb:
                case DemodMode.Lsb: return 2400.0;
                case DemodMode.Cw: return 500.0;
                default: return 9000.0;
            }
        }
    }
}
