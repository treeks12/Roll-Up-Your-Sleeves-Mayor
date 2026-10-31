using System;
using System.Collections.Generic;
using SC4Evolution.Simulation.Access;
using SC4Evolution.Simulation.Freight;

namespace SC4Evolution.Mod.Economy
{
    /// <summary>Explicit opt-in session for access and freight metrics (sprint 05).
    /// No Harmony/game dependencies: the same source is compiled into the isolated harness.
    ///
    /// INTEGRATION CONTRACT (wired after the sprint-03 merge; EconomicBridge owns config and
    /// is not modified by this sprint): the bridge samples NATIVE observations only —
    /// pathfind/route failure counts, transfer offer/match outcomes, completed arrivals and
    /// goods deliveries, building stock units, and outside-connection usage — on the
    /// simulation thread, and feeds them here. This adapter NEVER substitutes the traffic
    /// AI: no synthetic trips, no invented routes, no direct TrafficManager access (the
    /// handoff forbids touching traffic AI without a separate contract). Outputs are
    /// published scores and effect levels only; applying multipliers to occupancy,
    /// productivity, demand or abandonment happens through the owning systems (sprints 01,
    /// 02, 04) behind their own opt-ins. Measurement cadence and cost bounds are the
    /// caller's documented responsibility.</summary>
    public sealed class AccessFreightSession
    {
        private readonly EmploymentAccessMetric _employment;
        private readonly FreightOutflowMetric _freight;
        private readonly AccessEffectBands _bands;

        private AccessFreightSession(EmploymentAccessMetric employment, FreightOutflowMetric freight, AccessEffectBands bands)
        {
            _employment = employment;
            _freight = freight;
            _bands = bands;
        }

        public static AccessFreightSession TryCreate(bool enabled,
            long employmentTimeBudgetTicks, int freightStockCapacityUnits, AccessEffectBands bands)
        {
            if (!enabled) return null;
            if (bands == null) throw new ArgumentNullException("bands");
            return new AccessFreightSession(
                new EmploymentAccessMetric(employmentTimeBudgetTicks),
                new FreightOutflowMetric(freightStockCapacityUnits), bands);
        }

        public AccessEffectBands Bands
        {
            get { return _bands; }
        }

        public float EmploymentScore(EmploymentAccessObservation observation)
        {
            return _employment.Score(observation);
        }

        public float FreightScore(FreightOutflowObservation observation)
        {
            return _freight.Score(observation);
        }

        public AccessEffectLevel EmploymentLevel(EmploymentAccessObservation observation)
        {
            return _bands.Level(_employment.Score(observation));
        }

        public AccessEffectLevel FreightLevel(FreightOutflowObservation observation)
        {
            return _bands.Level(_freight.Score(observation));
        }

        /// <summary>Batch evaluation preserving input order; every origin appears with its
        /// own score and level, so effects land on the correct building or business.</summary>
        public AccessFreightBatch Evaluate(EmploymentAccessObservation[] employment, FreightOutflowObservation[] freight)
        {
            if (employment == null) throw new ArgumentNullException("employment");
            if (freight == null) throw new ArgumentNullException("freight");
            float[] employmentScores = new float[employment.Length];
            AccessEffectLevel[] employmentLevels = new AccessEffectLevel[employment.Length];
            for (int i = 0; i < employment.Length; i++)
            {
                employmentScores[i] = _employment.Score(employment[i]);
                employmentLevels[i] = _bands.Level(employmentScores[i]);
            }
            float[] freightScores = new float[freight.Length];
            AccessEffectLevel[] freightLevels = new AccessEffectLevel[freight.Length];
            for (int i = 0; i < freight.Length; i++)
            {
                freightScores[i] = _freight.Score(freight[i]);
                freightLevels[i] = _bands.Level(freightScores[i]);
            }
            return new AccessFreightBatch(employmentScores, employmentLevels, freightScores, freightLevels);
        }
    }

    /// <summary>Immutable batch result: per-origin scores and levels in input order.</summary>
    public sealed class AccessFreightBatch
    {
        public float[] EmploymentScores { get; private set; }
        public AccessEffectLevel[] EmploymentLevels { get; private set; }
        public float[] FreightScores { get; private set; }
        public AccessEffectLevel[] FreightLevels { get; private set; }

        internal AccessFreightBatch(float[] employmentScores, AccessEffectLevel[] employmentLevels,
            float[] freightScores, AccessEffectLevel[] freightLevels)
        {
            EmploymentScores = employmentScores;
            EmploymentLevels = employmentLevels;
            FreightScores = freightScores;
            FreightLevels = freightLevels;
        }
    }
}
