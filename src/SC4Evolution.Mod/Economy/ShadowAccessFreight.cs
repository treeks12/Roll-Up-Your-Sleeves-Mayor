using System;

namespace SC4Evolution.Mod.Economy
{
    /// <summary>
    /// Inputs available from an aggregate access sample. Traffic and distance are
    /// intentionally not route observations: they describe the local building or
    /// segment only. Demand and offers are counts already exposed by the caller.
    /// </summary>
    public struct ShadowAccessObservation
    {
        /// <summary>Congestion/load on the observed network segment, 0 (free) to 100 (gridlock).</summary>
        public int TrafficLoadPercent;

        /// <summary>Non-negative distance units from the observed origin to a usable connection.</summary>
        public int DistanceUnits;

        /// <summary>Whether the sampled origin currently has a usable network connection.</summary>
        public bool HasConnection;

        /// <summary>Residents or jobs requesting an access offer in this sample.</summary>
        public int DemandUnits;

        /// <summary>Offers already visible to the caller for the same sample.</summary>
        public int OfferUnits;
    }

    /// <summary>
    /// Inputs available from an aggregate freight sample. Stock is the backlog at the
    /// origin; offers are the outbound transfer capacity visible to the caller.
    /// </summary>
    public struct ShadowFreightObservation
    {
        /// <summary>Congestion/load on the observed network segment, 0 (free) to 100 (gridlock).</summary>
        public int TrafficLoadPercent;

        /// <summary>Non-negative distance units from the origin to a usable freight connection.</summary>
        public int DistanceUnits;

        /// <summary>Whether the sampled origin currently has a usable network connection.</summary>
        public bool HasConnection;

        /// <summary>Outbound transfer offers already visible to the caller.</summary>
        public int OfferUnits;

        /// <summary>Goods waiting at the origin.</summary>
        public int StockUnits;

        /// <summary>Observed storage capacity for the origin. Zero means no capacity was observed.</summary>
        public int StockCapacityUnits;
    }

    /// <summary>Aggregate signals used by a shadow result; no signal identifies a route.</summary>
    [Flags]
    public enum ShadowPressureSignal : byte
    {
        None = 0,
        Traffic = 1,
        Distance = 2,
        Connectivity = 4,
        Offers = 8,
        Stock = 16
    }

    /// <summary>
    /// A bounded pressure result. Pressure is 0 for no observed constraint and 100 for
    /// a hard constraint. Confidence is deliberately fixed at 50 for active samples:
    /// this is an aggregate heuristic and never claims an exact path or vehicle route.
    /// A zero-activity sample has zero confidence and should not change gameplay.
    /// </summary>
    public struct ShadowPressure
    {
        public byte PressurePercent { get; private set; }
        public byte ConfidencePercent { get; private set; }
        public ShadowPressureSignal Signals { get; private set; }

        public bool IsConstrained
        {
            get { return PressurePercent >= ShadowAccessFreight.ConstrainedThresholdPercent; }
        }

        internal ShadowPressure(int pressurePercent, int confidencePercent, ShadowPressureSignal signals)
        {
            PressurePercent = (byte)ShadowAccessFreight.ClampPercent(pressurePercent);
            ConfidencePercent = (byte)ShadowAccessFreight.ClampPercent(confidencePercent);
            Signals = signals;
        }
    }

    /// <summary>
    /// Small deterministic shadow model for access and freight. It consumes only
    /// aggregate traffic, connection distance, connectivity, offers and stock. It does
    /// not query TrafficManager, create trips, infer routes, or mutate native state.
    ///
    /// The formulas are intentionally transparent and bounded. They are a safe bridge
    /// until a native observation with per-origin identity exists; callers can use the
    /// pressure as a gradual signal without treating it as a route success metric.
    /// </summary>
    public sealed class ShadowAccessFreight
    {
        public const int DefaultDistanceScaleUnits = 1000;
        public const int ConstrainedThresholdPercent = 50;
        public const int HeuristicConfidencePercent = 50;

        private readonly int _distanceScaleUnits;

        public ShadowAccessFreight()
            : this(DefaultDistanceScaleUnits)
        {
        }

        public ShadowAccessFreight(int distanceScaleUnits)
        {
            if (distanceScaleUnits <= 0)
                throw new ArgumentOutOfRangeException("distanceScaleUnits", "Distance scale must be positive.");
            _distanceScaleUnits = distanceScaleUnits;
        }

        public int DistanceScaleUnits
        {
            get { return _distanceScaleUnits; }
        }

        /// <summary>
        /// Evaluates access pressure for one building or aggregate origin. A missing
        /// connection is a hard constraint; otherwise traffic, distance and offer
        /// shortage contribute weighted pressure.
        /// </summary>
        public ShadowPressure EvaluateAccess(ShadowAccessObservation observation)
        {
            if (observation.DemandUnits <= 0)
                return Inactive(ShadowPressureSignal.Traffic | ShadowPressureSignal.Distance |
                    ShadowPressureSignal.Connectivity | ShadowPressureSignal.Offers);

            int traffic = ClampPercent(observation.TrafficLoadPercent);
            int distance = DistancePressure(observation.DistanceUnits);
            int connectivity = observation.HasConnection ? 0 : 100;
            int shortage = ShortagePercent(observation.OfferUnits, observation.DemandUnits);
            int weighted = ClampPercent((int)(((long)traffic * 45L + (long)distance * 20L +
                (long)connectivity * 25L + (long)shortage * 10L + 50L) / 100L));
            int pressure = observation.HasConnection ? weighted : 100;
            return Active(pressure, ShadowPressureSignal.Traffic | ShadowPressureSignal.Distance |
                ShadowPressureSignal.Connectivity | ShadowPressureSignal.Offers);
        }

        /// <summary>
        /// Evaluates freight pressure for one business or aggregate origin. Empty stock
        /// is inactive; otherwise traffic, distance, connectivity, storage fill and
        /// visible outbound offers are combined into a bounded backlog signal.
        /// </summary>
        public ShadowPressure EvaluateFreight(ShadowFreightObservation observation)
        {
            if (observation.StockUnits <= 0)
                return Inactive(ShadowPressureSignal.Traffic | ShadowPressureSignal.Distance |
                    ShadowPressureSignal.Connectivity | ShadowPressureSignal.Offers |
                    ShadowPressureSignal.Stock);

            int traffic = ClampPercent(observation.TrafficLoadPercent);
            int distance = DistancePressure(observation.DistanceUnits);
            int connectivity = observation.HasConnection ? 0 : 100;
            int fill = FillPercent(observation.StockUnits, observation.StockCapacityUnits);
            int shortage = ShortagePercent(observation.OfferUnits, observation.StockUnits);
            int weighted = ClampPercent((int)(((long)traffic * 35L + (long)distance * 15L +
                (long)connectivity * 25L + (long)fill * 20L + (long)shortage * 5L + 50L) / 100L));
            int pressure = observation.HasConnection ? weighted : 100;
            return Active(pressure, ShadowPressureSignal.Traffic | ShadowPressureSignal.Distance |
                ShadowPressureSignal.Connectivity | ShadowPressureSignal.Offers |
                ShadowPressureSignal.Stock);
        }

        private int DistancePressure(int distanceUnits)
        {
            if (distanceUnits <= 0) return 0;
            long scaled = (long)distanceUnits * 100L / _distanceScaleUnits;
            return ClampPercent((int)Math.Min(100L, scaled));
        }

        private static int FillPercent(int stockUnits, int capacityUnits)
        {
            if (stockUnits <= 0) return 0;
            if (capacityUnits <= 0) return 100;
            long scaled = (long)stockUnits * 100L / capacityUnits;
            return ClampPercent((int)Math.Min(100L, scaled));
        }

        private static int ShortagePercent(int offers, int required)
        {
            if (required <= 0 || offers >= required) return 0;
            if (offers <= 0) return 100;
            long missing = required - (long)offers;
            return ClampPercent((int)(missing * 100L / required));
        }

        private static ShadowPressure Active(int pressure, ShadowPressureSignal signals)
        {
            return new ShadowPressure(pressure, HeuristicConfidencePercent, signals);
        }

        private static ShadowPressure Inactive(ShadowPressureSignal signals)
        {
            return new ShadowPressure(0, 0, signals);
        }

        internal static int ClampPercent(int value)
        {
            if (value <= 0) return 0;
            if (value >= 100) return 100;
            return value;
        }
    }
}
