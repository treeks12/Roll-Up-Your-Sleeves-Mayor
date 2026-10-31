using System;

namespace SC4Evolution.Simulation.Neighborhoods
{
    /// <summary>Per-lot cycle state keyed by native building id, validated by generation:
    /// a reused id with a different buildIndex resets the state, so old phase, class or
    /// counters never apply to a new building (sprint 02). Building level and resident
    /// class are stored separately and never derive from each other.</summary>
    public sealed class NeighborhoodLotState
    {
        public ushort LotId { get; internal set; }
        public uint BuildIndex { get; internal set; }
        /// <summary>Building level as observed; informational, distinct from ResidentClass.</summary>
        public int Level { get; internal set; }
        /// <summary>Resident wealth class 0..2, or -1 while unknown/constructing.</summary>
        public int ResidentClass { get; internal set; }
        public NeighborhoodPhase Phase { get; internal set; }
        /// <summary>Consecutive updates vacant (0 while occupied).</summary>
        public int VacancyUpdates { get; internal set; }
        /// <summary>Consecutive occupied updates at or below the maintenance bar.</summary>
        public int MaintenanceStrainUpdates { get; internal set; }
        /// <summary>Consecutive updates satisfying the replacement sustained condition.</summary>
        public int ReplacementSustainedUpdates { get; internal set; }
        public float LastAttractiveness { get; internal set; }
        public NeighborhoodTransitionReason LastReason { get; internal set; }
        public uint LastTransitionFrame { get; internal set; }

        internal NeighborhoodLotState()
        {
            ResidentClass = -1;
        }
    }
}
