using System;

namespace SC4Evolution.Simulation.Neighborhoods
{
    /// <summary>Attractiveness inputs for one lot, each normalized to 0..100 where 100 is
    /// best (for pollution and traffic, 100 means least pollution / least congestion).
    /// Adapters fill these from observations; the pure model never invents a factor.</summary>
    public struct NeighborhoodFactors
    {
        /// <summary>Accessible employment: share of the resident workforce with usable jobs,
        /// later fed by the sprint-01/sprint-05 access metrics.</summary>
        public float AccessibleEmployment;
        /// <summary>Covered civic services (schools, health, etc.) for this location.</summary>
        public float Services;
        /// <summary>Tax burden comfort: 100 at the friendliest effective rate.</summary>
        public float Taxes;
        public float Pollution;
        public float Traffic;
        public float LandValue;

        public static NeighborhoodFactors Neutral()
        {
            NeighborhoodFactors factors = new NeighborhoodFactors();
            factors.AccessibleEmployment = 100f; factors.Services = 100f; factors.Taxes = 100f;
            factors.Pollution = 100f; factors.Traffic = 100f; factors.LandValue = 100f;
            return factors;
        }

        public bool IsValid()
        {
            return IsRange(AccessibleEmployment) && IsRange(Services) && IsRange(Taxes) &&
                   IsRange(Pollution) && IsRange(Traffic) && IsRange(LandValue);
        }

        private static bool IsRange(float value)
        {
            return value >= 0f && value <= 100f && !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    /// <summary>The single authored weight table for lot attractiveness (sprint 02). Weights
    /// are positive and fixed at construction; the default column is an authored hypothesis
    /// inspired by the SC4 desirability structure (jobs, services, taxes, pollution, traffic,
    /// land value all matter), NOT an extracted coefficient; inject another table for tests
    /// or after native evidence. Attractiveness is the weighted mean on 0..100.</summary>
    public sealed class NeighborhoodAttractiveness
    {
        public const int FactorCount = 6;

        private readonly float[] _weights;

        public static NeighborhoodAttractiveness Default
        {
            get { return new NeighborhoodAttractiveness(new[] { 0.25f, 0.20f, 0.15f, 0.15f, 0.10f, 0.15f }); }
        }

        public NeighborhoodAttractiveness(float[] weights)
        {
            if (weights == null) throw new ArgumentNullException("weights");
            if (weights.Length != FactorCount) throw new ArgumentException("Expected " + FactorCount + " factor weights.", "weights");
            float total = 0f;
            for (int i = 0; i < weights.Length; i++)
            {
                if (float.IsNaN(weights[i]) || float.IsInfinity(weights[i]) || weights[i] < 0f)
                    throw new ArgumentException("Weights must be finite and non-negative.", "weights");
                total += weights[i];
            }
            if (total <= 0f) throw new ArgumentException("At least one weight must be positive.", "weights");
            _weights = (float[])weights.Clone();
        }

        public float Weight(int factor)
        {
            if (factor < 0 || factor >= FactorCount) throw new ArgumentOutOfRangeException("factor");
            return _weights[factor];
        }

        /// <summary>Weighted mean of the six factors on 0..100; deterministic and monotone
        /// in every factor.</summary>
        public float Evaluate(NeighborhoodFactors factors)
        {
            if (!factors.IsValid()) throw new ArgumentException("Factors must be finite values on 0..100.", "factors");
            float total = 0f, weightSum = 0f;
            total += _weights[0] * factors.AccessibleEmployment;
            total += _weights[1] * factors.Services;
            total += _weights[2] * factors.Taxes;
            total += _weights[3] * factors.Pollution;
            total += _weights[4] * factors.Traffic;
            total += _weights[5] * factors.LandValue;
            for (int i = 0; i < FactorCount; i++) weightSum += _weights[i];
            return total / weightSum;
        }
    }
}
