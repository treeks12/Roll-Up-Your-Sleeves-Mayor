using System;

namespace SC4Evolution.Simulation.Neighborhoods
{
    /// <summary>Observable lot phases of the neighborhood cycle (sprint 02). Empty, abandoned
    /// and under construction are three distinct states, never merged: a vacant lot still
    /// seeks residents, an abandoned lot needs rehabilitation before demand may return, and
    /// construction has not yet produced habitable stock. Native Upgrading stock is treated
    /// as construction; native Unavailable (collapsed/demolishing) is terminal.</summary>
    public enum NeighborhoodPhase : byte
    {
        None = 0,
        Constructing = 1,
        Occupied = 2,
        Vacant = 3,
        Abandoned = 4,
        NativeUnavailable = 5
    }

    /// <summary>Cause recorded with every phase or class transition, so news and advisors
    /// never receive a state change without its reason. None marks absence of transition.</summary>
    public enum NeighborhoodTransitionReason : byte
    {
        None = 0,
        ConstructionCompleted = 1,
        AttractivenessBelowMaintenance = 2,
        ProlongedVacancyLowAttractiveness = 3,
        ReoccupationDemandMet = 4,
        RehabilitationDemandMet = 5,
        ClassReplacement = 6,
        GenerationReset = 7,
        NativeUnavailable = 8,
        NativeRedevelopment = 9,
        ObservedVacancy = 10,
        ObservedReoccupation = 11
    }

    public static class NeighborhoodPhases
    {
        public static string PhaseName(NeighborhoodPhase phase)
        {
            switch (phase)
            {
                case NeighborhoodPhase.Constructing: return "Constructing";
                case NeighborhoodPhase.Occupied: return "Occupied";
                case NeighborhoodPhase.Vacant: return "Vacant";
                case NeighborhoodPhase.Abandoned: return "Abandoned";
                case NeighborhoodPhase.NativeUnavailable: return "NativeUnavailable";
                default: return "None";
            }
        }

        public static string ReasonName(NeighborhoodTransitionReason reason)
        {
            switch (reason)
            {
                case NeighborhoodTransitionReason.ConstructionCompleted: return "ConstructionCompleted";
                case NeighborhoodTransitionReason.AttractivenessBelowMaintenance: return "AttractivenessBelowMaintenance";
                case NeighborhoodTransitionReason.ProlongedVacancyLowAttractiveness: return "ProlongedVacancyLowAttractiveness";
                case NeighborhoodTransitionReason.ReoccupationDemandMet: return "ReoccupationDemandMet";
                case NeighborhoodTransitionReason.RehabilitationDemandMet: return "RehabilitationDemandMet";
                case NeighborhoodTransitionReason.ClassReplacement: return "ClassReplacement";
                case NeighborhoodTransitionReason.GenerationReset: return "GenerationReset";
                case NeighborhoodTransitionReason.NativeUnavailable: return "NativeUnavailable";
                case NeighborhoodTransitionReason.NativeRedevelopment: return "NativeRedevelopment";
                case NeighborhoodTransitionReason.ObservedVacancy: return "ObservedVacancy";
                case NeighborhoodTransitionReason.ObservedReoccupation: return "ObservedReoccupation";
                default: return "None";
            }
        }

        /// <summary>Maps the native stock phase onto the neighborhood cycle. Unknown native
        /// phases are rejected instead of guessed.</summary>
        public static NeighborhoodPhase FromStockPhase(ResidentialStockPhase stockPhase)
        {
            switch (stockPhase)
            {
                case ResidentialStockPhase.Constructing:
                case ResidentialStockPhase.Upgrading:
                    return NeighborhoodPhase.Constructing;
                case ResidentialStockPhase.Completed:
                    return NeighborhoodPhase.None; // occupied/vacant is model state, not native state
                case ResidentialStockPhase.Unavailable:
                    return NeighborhoodPhase.NativeUnavailable;
                default:
                    throw new ArgumentOutOfRangeException("stockPhase");
            }
        }
    }
}
