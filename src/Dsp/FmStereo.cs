using System;

namespace Radiocity.Dsp
{
    /// <summary>
    /// FM stereo decoder: pilot recovery, 38 kHz subcarrier, stereo matrix.
    ///
    /// A stereo broadcast does not send left and right. It sends
    ///
    ///     MPX = M + S*cos(2*pi*38000*t) + P*cos(2*pi*19000*t)
    ///
    /// where M = (L+R)/2, S = (L-R)/2, and P is a 19 kHz pilot whose only job is
    /// to tell the receiver where the 38 kHz subcarrier is. Recovering L and R is
    /// therefore: strip the pilot, synchronously detect the subcarrier to get S,
    /// then L = M + S and R = M - S.
    ///
    /// Why the pilot matters: a plain envelope detector on the 38 kHz sideband
    /// gives |S| with random sign, which swaps L and R at random and sounds like
    /// the stereo image jumping. The pilot is the phase reference that makes the
    /// sign meaningful.
    ///
    /// Implementation note: there is no PLL here. Mixing the composite down by the
    /// nominal 19 kHz and low-passing to ~100 Hz leaves the pilot as a slowly
    /// rotating complex number; its angle *is* the phase error, and twice that
    /// angle rotates the subcarrier. A PLL would do the same job with more parts
    /// to tune and a way to lose lock.
    /// </summary>
    public sealed class FmStereoDecoder
    {
        private const double PilotHz = 19000.0;
        private const double SubcarrierHz = 38000.0;

        private readonly double _rate;
        private readonly int _decim;
        private readonly RealDecimator _mPath;
        private readonly RealDecimator _sPath;
        private readonly float[] _sMix;
        private readonly float[] _mOut;
        private readonly float[] _sOut;

        private readonly float _pilotAlpha;
        private readonly double _pilotInc;
        private double _pilotPhase;

        private readonly float _deAlpha;
        private float _deL;
        private float _deR;

        private float _pilotI;
        private float _pilotQ;

        /// <summary>Pilot amplitude below which the station is treated as mono.</summary>
        public float PilotThreshold = 0.002f;

        public FmStereoDecoder(double rate, int audioRate, double deemphasisUs)
        {
            if (audioRate <= 0) throw new ArgumentOutOfRangeException("audioRate");

            _rate = rate;
            _decim = Math.Max(1, (int)Math.Round(rate / audioRate));
            AudioRate = (int)Math.Round(rate / _decim);

            // 15 kHz passband, stopband at the new Nyquist. At 192k -> 48k that
            // is a 9 kHz transition band, so a short filter is plenty.
            double pass = 15000.0 / rate;
            double stop = 0.5 / _decim;
            int taps;
            if (stop - pass < 0.002) taps = 63;
            else taps = (int)Math.Ceiling(62.0 / (2.285 * 2.0 * Math.PI * (stop - pass)));

            if (taps < 31) taps = 31;
            if (taps > 1023) taps = 1023;

            float[] h = FirDesign.LowPass((pass + stop) * 0.5, taps, 8.6);
            _mPath = new RealDecimator(h, _decim);
            _sPath = new RealDecimator(h, _decim);

            int block = 32768;
            _sMix = new float[block];
            _mOut = new float[block];
            _sOut = new float[block];

            // 100 Hz around the pilot: narrow enough to reject audio (which lands
            // 4-34 kHz away after mixing) and wide enough to follow a transmitter
            // that is a few Hz off nominal.
            _pilotAlpha = (float)(1.0 - Math.Exp(-2.0 * Math.PI * 100.0 / rate));
            _pilotInc = 2.0 * Math.PI * PilotHz / rate;

            if (deemphasisUs > 0.0)
            {
                double dt = 1.0 / AudioRate;
                double tau = deemphasisUs * 1e-6;
                _deAlpha = (float)(dt / (tau + dt));
            }
            else
            {
                _deAlpha = 1.0f;
            }
        }

        public int AudioRate { get; private set; }

        /// <summary>True when a pilot was present and the subcarrier was locked.</summary>
        public bool Stereo { get { return _pilotI * _pilotI + _pilotQ * _pilotQ > PilotThreshold * PilotThreshold; } }

        /// <summary>Pilot amplitude, for a stereo indicator.</summary>
        public float PilotLevel
        {
            get { return 2.0f * (float)Math.Sqrt(_pilotI * _pilotI + _pilotQ * _pilotQ); }
        }

        /// <summary>
        /// Decodes a composite signal into interleaved stereo: dst[0] = L, dst[1] = R.
        /// Returns the number of frames written (dst needs 2x that many samples).
        /// </summary>
        public int Process(float[] composite, int n, float[] dst)
        {
            if (n > _sMix.Length) throw new ArgumentOutOfRangeException("n");

            bool stereo = Stereo;

            for (int k = 0; k < n; k++)
            {
                float x = composite[k];

                // Pilot downconversion and 100 Hz low-pass. A one-pole on both I
                // and Q has zero phase shift at DC, so the angle below is a true
                // phase error and not a filter artefact.
                double c = Math.Cos(_pilotPhase);
                double s = Math.Sin(_pilotPhase);
                _pilotI += _pilotAlpha * (float)(x * c - _pilotI);
                _pilotQ += _pilotAlpha * (float)(x * s - _pilotQ);

                // Subcarrier = cos(theta + 2*psi), where theta is the nominal
                // 38 kHz phase and psi is the pilot's phase offset. The pilot
                // complex is p = A*e^{-j*psi}, so e^{j*psi} = conj(p)/A and the
                // subcarrier needs the *square* of that:
                //
                //   e^{j(theta+2psi)} = e^{j*theta} * (conj(p)/A)^2
                //
                // Taking only the first power (conjugating without squaring)
                // looks right, works when the pilot happens to start at phase
                // zero, and quietly halves the correction otherwise — the
                // channels then swap and leak depending on the sample offset.
                double cosSub;
                float mag2 = _pilotI * _pilotI + _pilotQ * _pilotQ;

                if (mag2 > 1e-20f)
                {
                    double twoTheta = 2.0 * _pilotPhase;
                    double cos2 = Math.Cos(twoTheta);
                    double sin2 = Math.Sin(twoTheta);

                    // (conj(p))^2 / A^2 = cos(2*psi) + j*sin(2*psi), with p = pI + j*pQ.
                    // Expanding: re = (pI^2 - pQ^2)/A^2, im = -2*pI*pQ/A^2.
                    // The minus sign matters: with a plus the decoder locks to
                    // 2*theta - 2*psi instead of 2*theta + 2*psi, which flips the
                    // sideband and swaps the channels.
                    double re = (_pilotI * _pilotI - _pilotQ * _pilotQ) / mag2;
                    double im = (-2.0 * _pilotI * _pilotQ) / mag2;

                    cosSub = cos2 * re - sin2 * im;
                }
                else
                {
                    cosSub = Math.Cos(2.0 * _pilotPhase);
                }

                _sMix[k] = (float)(x * cosSub);

                _pilotPhase += _pilotInc;
                if (_pilotPhase > 2.0 * Math.PI) _pilotPhase -= 2.0 * Math.PI;
            }

            int m = _mPath.Process(composite, n, _mOut);
            int sCount = _sPath.Process(_sMix, n, _sOut);
            if (sCount < m) m = sCount;

            for (int k = 0; k < m; k++)
            {
                float mid = _mOut[k];

                // The synchronous detector halves the sideband amplitude; a mono
                // station has no sideband at all, so S must go to zero rather
                // than to whatever noise the detector found.
                float side = stereo ? 2.0f * _sOut[k] : 0.0f;

                float l = mid + side;
                float r = mid - side;

                // De-emphasis belongs on L and R, after the matrix: the
                // transmitter applied pre-emphasis to the audio, not to the MPX.
                _deL += _deAlpha * (l - _deL);
                _deR += _deAlpha * (r - _deR);

                dst[k * 2] = _deL;
                dst[k * 2 + 1] = _deR;
            }

            return m;
        }

        public void Reset()
        {
            _mPath.Reset();
            _sPath.Reset();
            _pilotI = 0.0f;
            _pilotQ = 0.0f;
            _pilotPhase = 0.0;
            _deL = 0.0f;
            _deR = 0.0f;
        }
    }

    /// <summary>
    /// AGC that applies one gain to both stereo channels.
    ///
    /// Applying a mono AGC to interleaved L,R would treat the channels as one
    /// stream and move the stereo image every time one side got louder. A linked
    /// detector on the louder channel keeps the balance intact.
    /// </summary>
    public sealed class LinkedAgc
    {
        private readonly float _detector;
        private readonly float _attack;
        private readonly float _release;
        private readonly float _targetDb;
        private float _gainDb;
        private float _ms;

        public LinkedAgc(double sampleRate, double attackMs, double releaseMs, double targetPeak)
        {
            _detector = (float)(1.0 - Math.Exp(-1.0 / (sampleRate * 0.030)));
            _attack = (float)(1.0 - Math.Exp(-1.0 / (sampleRate * attackMs / 1000.0)));
            _release = (float)(1.0 - Math.Exp(-1.0 / (sampleRate * releaseMs / 1000.0)));

            double targetRms = targetPeak / 1.41421356;
            _targetDb = (float)(20.0 * Math.Log10(targetRms));
            _gainDb = 0.0f;
        }

        public float GainDb { get { return _gainDb; } }

        public void Process(float[] interleaved, int frames)
        {
            for (int k = 0; k < frames; k++)
            {
                float l = interleaved[k * 2];
                float r = interleaved[k * 2 + 1];

                float sq = Math.Max(l * l, r * r);
                _ms += _detector * (sq - _ms);

                float levelDb = 10.0f * (float)Math.Log10(Math.Max(_ms, 1e-12f));
                float want = _targetDb - levelDb;
                float slew = (want < _gainDb) ? _attack : _release;
                _gainDb += slew * (want - _gainDb);

                if (_gainDb > 60.0f) _gainDb = 60.0f;
                if (_gainDb < -20.0f) _gainDb = -20.0f;

                float g = (float)Math.Pow(10.0, _gainDb / 20.0);
                interleaved[k * 2] = l * g;
                interleaved[k * 2 + 1] = r * g;
            }
        }

        public void Reset()
        {
            _gainDb = 0.0f;
            _ms = 0.0f;
        }
    }
}
