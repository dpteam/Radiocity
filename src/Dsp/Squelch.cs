using System;

namespace Radiocity.Dsp
{
    /// <summary>
    /// Squelch: mutes the audio unless the signal in the passband is above a
    /// threshold, with hysteresis and a smooth ramp so it does not click.
    ///
    /// Measured on the channel power *before* the AGC, which is the only place
    /// where "is there a signal" still means something: after the AGC a silent
    /// channel and a strong one look identical, because that is the AGC's job.
    /// </summary>
    public sealed class Squelch
    {
        private readonly float _attack;
        private readonly float _release;
        private readonly double _rate;
        private readonly double _tau;

        private float _power;
        private float _gate;
        private bool _open;

        /// <summary>Threshold in dBFS of channel power. -100 means effectively off.</summary>
        public float ThresholdDb;

        /// <summary>Hysteresis in dB: open above threshold + h, close below threshold - h.</summary>
        public float HysteresisDb = 3.0f;

        public bool Enabled;

        public Squelch(double sampleRate)
            : this(sampleRate, sampleRate)
        {
        }

        /// <param name="sampleRate">Rate the power detector is fed at.</param>
        /// <param name="gateRate">
        /// Rate the gate is applied at. They differ for FM stereo, where the
        /// detector sees the composite at baseband but the gate multiplies audio
        /// at the output rate; using one rate for both would make the ramp
        /// four times slower than intended.
        /// </param>
        public Squelch(double sampleRate, double gateRate)
        {
            // 20 ms power estimate: long enough to average over speech syllables,
            // short enough that the gate does not lag the signal.
            _rate = sampleRate;
            _tau = 0.020;

            // 2 ms to open, 40 ms to close. Opening fast avoids chopping the first
            // syllable; closing slowly avoids chattering on a fading carrier.
            _attack = (float)(1.0 - Math.Exp(-1.0 / (gateRate * 0.002)));
            _release = (float)(1.0 - Math.Exp(-1.0 / (gateRate * 0.040)));

            ThresholdDb = -100.0f;
            Enabled = false;
            _gate = 1.0f;
            _open = true;
        }

        /// <summary>Start muted when a squelch is configured: silence is the honest default.</summary>
        public void PrimeClosed()
        {
            _open = false;
            _gate = 0.0f;
            _power = 0.0f;
        }

        /// <summary>Channel power in dBFS. A full-scale carrier reads about -3 dB.</summary>
        public float LevelDb
        {
            get { return 10.0f * (float)Math.Log10(Math.Max(_power, 1e-20f)); }
        }

        public bool Open { get { return !Enabled || _open; } }

        /// <summary>
        /// Feeds a precomputed block mean power. Keeping the measurement outside
        /// lets the level meter work even when the squelch is switched off, and
        /// stops the sum over the block being computed twice.
        /// </summary>
        public void Update(float meanPower, int n)
        {
            if (n <= 0) return;

            // The detector runs once per block, so the smoothing coefficient has
            // to be derived from the block length. A per-sample coefficient
            // applied per block makes the squelch react n times too slowly —
            // at 8192 samples that is not a filter, it is a memory.
            double alpha = 1.0 - Math.Exp(-n / (_rate * _tau));
            _power += (float)alpha * (meanPower - _power);

            if (!Enabled) return;

            float level = LevelDb;
            float h = HysteresisDb;

            if (_open)
            {
                if (level < ThresholdDb - h) _open = false;
            }
            else
            {
                if (level > ThresholdDb + h) _open = true;
            }
        }

        /// <summary>Applies the gate to already-demodulated audio.</summary>
        public void Apply(float[] audio, int n)
        {
            if (!Enabled) return;

            float target = _open ? 1.0f : 0.0f;

            for (int k = 0; k < n; k++)
            {
                float slew = (target > _gate) ? _attack : _release;
                _gate += slew * (target - _gate);
                audio[k] *= _gate;
            }
        }

        public void Reset()
        {
            _power = 0.0f;
            _gate = Enabled ? 0.0f : 1.0f;
            _open = false;
        }
    }
}
