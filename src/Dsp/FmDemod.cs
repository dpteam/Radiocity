using System;

namespace Radiocity.Dsp
{
    /// <summary>
    /// Quadrature FM discriminator.
    ///
    /// The instantaneous frequency of the baseband signal is the phase of the
    /// product z[n] * conj(z[n-1]). Using atan2 of that product (instead of the
    /// common I*dQ - Q*dI approximation divided by power) makes the detector
    /// completely immune to amplitude variation, so no limiter stage is needed
    /// and a fading signal does not turn into a burst of noise.
    /// </summary>
    public sealed class FmDemod
    {
        private readonly float _scale;
        private readonly float _deemph;
        private float _prevI;
        private float _prevQ;
        private float _de;

        /// <param name="basebandRate">Sample rate of the I/Q stream.</param>
        /// <param name="maxDeviationHz">75 kHz for broadcast, 2.5-5 kHz for narrowband.</param>
        /// <param name="deemphasisUs">50 for Europe, 75 for the Americas, 0 to disable.</param>
        public FmDemod(double basebandRate, double maxDeviationHz, double deemphasisUs)
        {
            _scale = (float)(basebandRate / (2.0 * Math.PI * maxDeviationHz));

            if (deemphasisUs > 0.0)
            {
                // One-pole low-pass with tau = deemphasisUs.
                double dt = 1.0 / basebandRate;
                double tau = deemphasisUs * 1e-6;
                _deemph = (float)(dt / (tau + dt));
            }
            else
            {
                _deemph = 1.0f;     // no filtering
            }
        }

        public void Process(float[] i, float[] q, int n, float[] audio)
        {
            float pi = _prevI;
            float pq = _prevQ;
            float de = _de;

            for (int k = 0; k < n; k++)
            {
                float ci = i[k];
                float cq = q[k];

                // z[n] * conj(z[n-1])
                float re = ci * pi + cq * pq;
                float im = cq * pi - ci * pq;

                float phase = (float)Math.Atan2(im, re);
                float v = phase * _scale;

                de += _deemph * (v - de);
                audio[k] = de;

                pi = ci;
                pq = cq;
            }

            _prevI = pi;
            _prevQ = pq;
            _de = de;
        }

        public void Reset()
        {
            _prevI = 0.0f;
            _prevQ = 0.0f;
            _de = 0.0f;
        }
    }
}
