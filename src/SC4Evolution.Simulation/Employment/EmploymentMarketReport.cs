using System;

namespace SC4Evolution.Simulation.Employment
{
    public enum ImmigrationBlockReason : byte
    {
        None = 0,
        Granted = 1,
        /// <summary>Immigration demand exists but available home units ran out first.</summary>
        PartialResidentialCapacity = 2,
        BlockedNoResidentialCapacity = 3,
        BlockedNoEntryPath = 4
    }

    /// <summary>Immutable result of one employment market update. Every array is a copy;
    /// all counts are persons/jobs in census units. Pressure values are dimensionless
    /// signals for construction demand and advisors; they are not money, not population
    /// changes and never applied to the simulation by this model.</summary>
    public sealed class EmploymentMarketReport
    {
        public uint Frame { get; private set; }
        public int[] EmployedByCell { get; private set; }
        public int[] UnemployedByCell { get; private set; }
        /// <summary>Jobs the model attributed to workforce cells per segment. When the
        /// observed filled count exceeds what eligible workforce can hold, the difference
        /// appears in UnfilledObservedFilled instead of being silently allocated.</summary>
        public int[] ModelFilledBySegment { get; private set; }
        public int[] UnfilledObservedFilledBySegment { get; private set; }
        /// <summary>capacity - filled, purely observed; a visual or reserved vacancy is not
        /// an immigration success until the model grants immigrants for it.</summary>
        public int[] VacantBySegment { get; private set; }
        /// <summary>Vacancies no eligible unemployed resident can take: the shortage of
        /// qualified workers for that segment.</summary>
        public int[] UnfillableVacanciesBySegment { get; private set; }
        /// <summary>Unemployed residents whose education exactly matches the segment floor:
        /// the labor supply that may attract new businesses of that segment.</summary>
        public int[] SectorGrowthPressureBySegment { get; private set; }
        /// <summary>Unfillable vacancies attributed to the cell that would immigrate to
        /// fill them (segment tier wealth, minimum education): the pressure for housing.</summary>
        public int[] ResidentialPressureByCell { get; private set; }
        public int[] ImmigrationDemandByCell { get; private set; }
        public int[] ImmigrationGrantByCell { get; private set; }
        public ImmigrationBlockReason[] ImmigrationReasonByCell { get; private set; }
        public int AvailableHomeUnits { get; private set; }
        public int HomeUnitsUsedByImmigration { get; private set; }
        public bool EntryPathOpen { get; private set; }
        public int UnmappedJobCapacity { get; private set; }
        public int UnmappedFilledJobs { get; private set; }
        /// <summary>Segments whose observed filled count exceeds built capacity.</summary>
        public int FilledAboveCapacitySegments { get; private set; }

        internal EmploymentMarketReport(uint frame,
            int[] employed, int[] unemployed, int[] modelFilled, int[] unfilledObserved,
            int[] vacant, int[] unfillable, int[] sectorPressure, int[] residentialPressure,
            int[] immigrationDemand, int[] immigrationGrant, ImmigrationBlockReason[] immigrationReason,
            int availableHomeUnits, int homeUnitsUsed, bool entryPathOpen,
            int unmappedCapacity, int unmappedFilled, int filledAboveCapacity)
        {
            Frame = frame;
            EmployedByCell = employed;
            UnemployedByCell = unemployed;
            ModelFilledBySegment = modelFilled;
            UnfilledObservedFilledBySegment = unfilledObserved;
            VacantBySegment = vacant;
            UnfillableVacanciesBySegment = unfillable;
            SectorGrowthPressureBySegment = sectorPressure;
            ResidentialPressureByCell = residentialPressure;
            ImmigrationDemandByCell = immigrationDemand;
            ImmigrationGrantByCell = immigrationGrant;
            ImmigrationReasonByCell = immigrationReason;
            AvailableHomeUnits = availableHomeUnits;
            HomeUnitsUsedByImmigration = homeUnitsUsed;
            EntryPathOpen = entryPathOpen;
            UnmappedJobCapacity = unmappedCapacity;
            UnmappedFilledJobs = unmappedFilled;
            FilledAboveCapacitySegments = filledAboveCapacity;
        }

        public int TotalEmployed()
        {
            int total = 0;
            for (int cell = 0; cell < EmploymentSegments.WorkforceCells; cell++) total += EmployedByCell[cell];
            return total;
        }

        public int TotalUnemployed()
        {
            int total = 0;
            for (int cell = 0; cell < EmploymentSegments.WorkforceCells; cell++) total += UnemployedByCell[cell];
            return total;
        }

        public int TotalImmigrationGranted()
        {
            int total = 0;
            for (int cell = 0; cell < EmploymentSegments.WorkforceCells; cell++) total += ImmigrationGrantByCell[cell];
            return total;
        }
    }
}
