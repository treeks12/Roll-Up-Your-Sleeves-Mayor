using System;
using System.Collections.Generic;

namespace SC4Evolution.Simulation.Neighborhoods
{
    /// <summary>One lot observation entering the model. Stock fields mirror
    /// ResidentialStockLot; HasLiveResidents is true when the stock observed any live
    /// (settled or incoming) household members.</summary>
    public struct NeighborhoodLotInput
    {
        public ushort LotId;
        public uint BuildIndex;
        public int Level;
        /// <summary>Stock lot class: 0 unknown, 1..3 meaning wealth class 0..2.</summary>
        public int ObservedStockClass;
        public ResidentialStockPhase StockPhase;
        public NeighborhoodFactors Factors;
        public bool HasLiveResidents;
    }

    /// <summary>One update batch: all observed lots plus city-scale demand for homes by
    /// resident class (persons seeking). Demand feeds reoccupation and replacement; the
    /// model never invents it.</summary>
    public sealed class NeighborhoodUpdateInput
    {
        public uint Frame { get; private set; }
        public IList<NeighborhoodLotInput> Lots { get; private set; }
        public int[] DemandByClass { get; private set; }

        public NeighborhoodUpdateInput(uint frame, IList<NeighborhoodLotInput> lots, int[] demandByClass)
        {
            if (lots == null) throw new ArgumentNullException("lots");
            if (demandByClass == null) throw new ArgumentNullException("demandByClass");
            if (demandByClass.Length != 3) throw new ArgumentException("Expected demand for exactly three wealth classes.", "demandByClass");
            for (int i = 0; i < 3; i++)
                if (demandByClass[i] < 0) throw new ArgumentException("Demand cannot be negative.", "demandByClass");
            for (int i = 0; i < lots.Count; i++)
            {
                NeighborhoodLotInput lot = lots[i];
                if (lot.StockPhase != ResidentialStockPhase.Constructing &&
                    lot.StockPhase != ResidentialStockPhase.Completed &&
                    lot.StockPhase != ResidentialStockPhase.Upgrading &&
                    lot.StockPhase != ResidentialStockPhase.Unavailable)
                    throw new ArgumentException("Unknown stock phase on lot " + lot.LotId + ".", "lots");
                if (lot.ObservedStockClass < 0 || lot.ObservedStockClass > 3)
                    throw new ArgumentException("Stock class must be 0..3 on lot " + lot.LotId + ".", "lots");
                if (lot.Level < 0) throw new ArgumentException("Level cannot be negative on lot " + lot.LotId + ".", "lots");
                if (!lot.Factors.IsValid()) throw new ArgumentException("Factors out of range on lot " + lot.LotId + ".", "lots");
            }
            Frame = frame;
            Lots = new List<NeighborhoodLotInput>(lots).AsReadOnly();
            DemandByClass = (int[])demandByClass.Clone();
        }
    }

    /// <summary>A recorded transition: identity, phases and the cause that produced it.
    /// Advisors and news consume these; no transition is emitted without a reason.</summary>
    public struct NeighborhoodTransition
    {
        public uint Frame;
        public ushort LotId;
        public uint BuildIndex;
        public NeighborhoodPhase FromPhase;
        public NeighborhoodPhase ToPhase;
        public int FromClass;
        public int ToClass;
        public NeighborhoodTransitionReason Reason;
    }

    /// <summary>Immutable result of one model update: per-lot snapshots, every transition
    /// with cause, and summary counters for news and advisors.</summary>
    public sealed class NeighborhoodReport
    {
        public uint Frame { get; private set; }
        public NeighborhoodLotState[] Lots { get; private set; }
        public NeighborhoodTransition[] Transitions { get; private set; }
        public int[] PhaseCounts { get; private set; }
        /// <summary>Count per NeighborhoodTransitionReason over this update's transitions.</summary>
        public int[] ReasonCounts { get; private set; }

        internal NeighborhoodReport(uint frame, NeighborhoodLotState[] lots,
            NeighborhoodTransition[] transitions, int[] phaseCounts, int[] reasonCounts)
        {
            Frame = frame;
            Lots = lots;
            Transitions = transitions;
            PhaseCounts = phaseCounts;
            ReasonCounts = reasonCounts;
        }
    }
}
