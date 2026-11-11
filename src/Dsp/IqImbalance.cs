using System;

namespace Radiocity.Dsp
{
    /// <summary>
    /// Blind I/Q imbalance correction (Gram-Schmidt).
    ///
    /// Every real quadrature front end has two analogue paths that are not quite
    /// identical: the two mixers have slightly different gain, and the 90 degree
    /// phase shift is 90 plus or minus a couple of degrees. The result is an
    /// *image*: a strong carrier at +f leaks into -f, and in a sound-card SDR
    /// that leak is typically only 25-40 dB down. It is the difference between
    /// "I can hear the station" and "I can hear the station and its ghost".
    ///
    /// The fix needs no calibration signal. For any real signal the two branches
    /// should have equal power and zero correlation; whatever violates that is
    /// the imbalance, and it can be measured from the statistics directly:
    ///
    ///     rho  = E[I*Q] / E[I^2]          (correlation, caused by phase error)
    ///     q    = E[Q^2] / E[I^2]          (power ratio, caused by gain error)
    ///     Q'   = (Q - rho*I) / sqrt(q - rho^2)
    ///
    /// After that E[I*Q'] = 0 and E[Q'^2] = E[I^2], so the image is gone. On a
    /// perfectly balanced signal rho = 0 and q = 1, which makes this the identity
    /// transform — the reason it is safe to leave switched on for synthesised
    /// I/Q that never had an imbalance to begin with.
    /// </summary>
    public sealed class IqImbalance
    {
        private readonly float _alpha;
        private double _eII;
        private double _eQQ;
        private double _eIQ;
        private long _samples;

        /// <summary>Smoothed estimates, for display and for tests.</summary>
        public double GainRatio { get { return _eII > 1e-20 ? Math.Sqrt(_eQQ / _eII) : 1.0; } }

        /// <summary>Phase error in degrees implied by the correlation.</summary>
        public double PhaseErrorDeg
        {
            get
            {
                double rho = Correlation;
                if (rho > 1.0) rho = 1.0;
                if (rho < -1.0) rho = -1.0;
                return Math.Asin(rho) * 180.0 / Math.PI;
            }
        }

        public double Correlation
        {
            get { return _eII > 1e-20 ? _eIQ / _eII : 0.0; }
        }

        /// <summary>Image rejection implied by the current estimates, in dB (negative).</summary>
        public double ImageRejectionDb
        {
            get
            {
                // For gain error g and phase error phi the image level relative to
                // the wanted signal is about sqrt((g-1)^2 + phi^2)/2.
                double g = GainRatio - 1.0;
                double phi = PhaseErrorDeg * Math.PI / 180.0;
                double ratio = Math.Sqrt(g * g + phi * phi) * 0.5;
                if (ratio < 1e-9) return -120.0;
                return 20.0 * Math.Log10(ratio);
            }
        }

        /// <param name="rate">Baseband sample rate; the statistics window is 300 ms.</param>
        public IqImbalance(double rate)
        {
            _alpha = (float)(1.0 - Math.Exp(-1.0 / (rate * 0.300)));
        }

        /// <summary>
        /// Updates the estimates and corrects in place. The correction is applied
        /// only once the statistics have settled, so the first few milliseconds
        /// pass through untouched rather than being mangled by a wild estimate.
        /// </summary>
        public void Process(float[] i, float[] q, int n)
        {
            for (int k = 0; k < n; k++)
            {
                double ii = i[k];
                double qq = q[k];

                _eII += _alpha * (ii * ii - _eII);
                _eQQ += _alpha * (qq * qq - _eQQ);
                _eIQ += _alpha * (ii * qq - _eIQ);
                _samples++;
            }

            // Nothing sensible to correct until the statistics have settled, and
            // dividing by a nearly zero E[I^2] would turn a silent channel into
            // an infinite gain.
            if (_samples < 64 || _eII < 1e-12) return;

            double rho = _eIQ / _eII;
            double powerRatio = _eQQ / _eII;
            double det = powerRatio - rho * rho;

            // A degenerate detector (one branch silent, or pure DC) would make
            // this explode. Leave the signal alone instead of amplifying noise.
            if (det < 1e-9) return;

            double scale = 1.0 / Math.Sqrt(det);

            for (int k = 0; k < n; k++)
            {
                double ii = i[k];
                double qq = q[k];
                q[k] = (float)((qq - rho * ii) * scale);
            }
        }

        public void Reset()
        {
            _eII = 0.0;
            _eQQ = 0.0;
            _eIQ = 0.0;
            _samples = 0;
        }
    }
}
