using System;

namespace Radiocity.Dsp
{
    /// <summary>
    /// Impulse noise blanker for the complex baseband.
    ///
    /// Why this is not the squelch: a squelch answers "is there a signal", and
    /// mutes everything when there is not. A blanker answers "is this sample a
    /// click", and surgically removes the click while keeping the signal under
    /// it. Ignition noise, switch-mode hash and lightning all arrive as very
    /// short, very loud spikes — microseconds wide, which is a handful of
    /// samples at any baseband rate. Blanking those is nearly free; filtering
    /// them out is not, because a filter that removes a 1-sample spike also
    /// removes everything else at that bandwidth.
    ///
    /// The detector compares instantaneous power against a slow average. Two
    /// details keep it from eating the signal:
    ///
    ///  - The average is only updated on samples that are *not* impulses.
    ///    Otherwise a burst of clicks raises the threshold that is supposed to
    ///    detect them, and the blanker goes blind exactly when it is needed.
    ///  - The hold is short (a few samples). A long hold turns into audible
    ///    "holes" in the signal; at 12 kHz baseband, 4 samples is 0.3 ms.
    /// </summary>
    public sealed class NoiseBlanker
    {
        private readonly float _alpha;
        private readonly float _warmAlpha;
        private readonly int _warmSamples;
        private float _average;
        private int _hold;
        private int _warmup;

        /// <summary>Blank when instantaneous power exceeds this multiple of the average.</summary>
        public float Threshold;

        /// <summary>How many samples to blank once an impulse is detected.</summary>
        public int HoldSamples;

        /// <summary>Impulses blanked since construction, for the UI and for tests.</summary>
        public long Blanked;

        /// <summary>Total samples blanked, as a fraction of all samples processed.</summary>
        public long BlankedSamples;

        public NoiseBlanker(double rate)
        {
            // 200 ms average: long enough to be a stable reference, short enough
            // to follow fading and AGC movement.
            _alpha = (float)(1.0 - Math.Exp(-1.0 / (rate * 0.200)));

            // A blanker cannot decide anything before it knows what "normal"
            // looks like. Without this warm-up the reference starts at zero, the
            // threshold starts below the signal itself, and the detector blanks
            // the signal — which prevents the reference from ever rising, so it
            // never recovers. It is a deadlock, and it looks exactly like a
            // receiver that has gone deaf.
            _warmAlpha = (float)(1.0 - Math.Exp(-1.0 / (rate * 0.010)));
            _warmSamples = (int)(rate * 0.050);
            _warmup = _warmSamples;

            _average = 0.0f;
            _hold = 0;
            Threshold = 12.0f;
            HoldSamples = 4;
        }

        public float AveragePower { get { return _average; } }

        public void Process(float[] i, float[] q, int n)
        {
            for (int k = 0; k < n; k++)
            {
                float ii = i[k];
                float qq = q[k];
                float p = ii * ii + qq * qq;

                if (_warmup > 0)
                {
                    _warmup--;
                    _average += _warmAlpha * (p - _average);
                    continue;
                }

                if (_hold > 0)
                {
                    i[k] = 0.0f;
                    q[k] = 0.0f;
                    _hold--;
                    BlankedSamples++;
                    continue;
                }

                bool impulse = p > Threshold * _average;

                if (impulse)
                {
                    i[k] = 0.0f;
                    q[k] = 0.0f;
                    _hold = HoldSamples - 1;
                    Blanked++;
                    BlankedSamples++;

                    // Feed the reference the largest value it would have accepted
                    // anyway. Blanked samples then cannot drag the average up,
                    // and the average still has a sample to work with.
                    _average += _alpha * (Threshold * _average - _average);
                    continue;
                }

                _average += _alpha * (p - _average);
            }
        }

        public void Reset()
        {
            _average = 0.0f;
            _hold = 0;
            _warmup = _warmSamples;
            Blanked = 0;
            BlankedSamples = 0;
        }
    }
}
