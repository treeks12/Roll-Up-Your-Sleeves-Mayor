using System;

namespace SC4Evolution.Simulation.Freight
{
    /// <summary>Freight outflow observation for one origin business toward destinations
    /// (sprint 05). Quantities stay separated exactly as the handoff demands:
    /// - RouteAttempts: routing queries (a found route is not a delivery);
    /// - DeliveriesCompleted: goods actually delivered at a destination;
    /// - TransferFailures: offers/matches that failed to move;
    /// - StockUnits: goods waiting at the origin (stored units);
    /// - ExternalConnectionOpen: whether an outside connection accepted freight.
    /// The metric never counts a visual route without a delivery as success, and an origin
    /// without a usable destination scores zero regardless of road existence.</summary>
    public struct FreightOutflowObservation
    {
        public ushort OriginId;
        public int RouteAttempts;
        public int DeliveriesCompleted;
        public int TransferFailures;
        public int StockUnits;
        public bool ExternalConnectionOpen;
    }

    /// <summary>Deterministic freight outflow metric (sprint 05). Score 0..1 is the product
    /// of: delivery rate (deliveries / attempts), stock pressure (unbounded stock pushes the
    /// factor toward 0 with the injected capacity; at or below capacity the factor is 1) and
    /// a hard external-connection factor (0 when the only outlet is closed and stock is
    /// positive). Mere existence of roads never enters the formula.</summary>
    public sealed class FreightOutflowMetric
    {
        private readonly int _stockCapacityUnits;

        public FreightOutflowMetric(int stockCapacityUnits)
        {
            if (stockCapacityUnits <= 0) throw new ArgumentOutOfRangeException("stockCapacityUnits", "Stock capacity must be positive.");
            _stockCapacityUnits = stockCapacityUnits;
        }

        public int StockCapacityUnits
        {
            get { return _stockCapacityUnits; }
        }

        /// <summary>Score 0..1 for one origin. Zero attempts mean no observed activity:
        /// score 0, because "no measurement" must never read as free outflow.</summary>
        public float Score(FreightOutflowObservation observation)
        {
            if (observation.RouteAttempts < 0 || observation.DeliveriesCompleted < 0 ||
                observation.TransferFailures < 0 || observation.StockUnits < 0)
                throw new ArgumentException("Freight observations cannot be negative.", "observation");
            if (observation.DeliveriesCompleted + observation.TransferFailures > observation.RouteAttempts)
                throw new ArgumentException("Outcomes cannot exceed route attempts.", "observation");
            if (observation.RouteAttempts == 0) return 0f;
            float deliveryRate = (float)observation.DeliveriesCompleted / observation.RouteAttempts;
            float stockFactor;
            if (observation.StockUnits <= 0) stockFactor = 1f;
            else if (observation.StockUnits >= _stockCapacityUnits) stockFactor = 0f;
            else stockFactor = 1f - (float)observation.StockUnits / _stockCapacityUnits;
            float connectionFactor = 1f;
            if (!observation.ExternalConnectionOpen && observation.StockUnits > 0) connectionFactor = 0f;
            float score = deliveryRate * stockFactor * connectionFactor;
            if (score < 0f) return 0f;
            if (score > 1f) return 1f;
            return score;
        }
    }
}
