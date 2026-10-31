using System;

namespace SC4Evolution.Simulation.Employment
{
    /// <summary>One immutable observation entering the employment market. All units stay
    /// separated exactly as the census publishes them: workforce cells are actual
    /// non-student young/adult residents, job capacity is built workplace capacity and
    /// job filled is occupied workplace count; neither implies demand or money.</summary>
    public sealed class EmploymentMarketInput
    {
        public uint Frame { get; private set; }
        public int[] WorkforceByCell { get; private set; }
        public int[] JobCapacityBySegment { get; private set; }
        public int[] JobFilledBySegment { get; private set; }
        public int UnmappedJobCapacity { get; private set; }
        public int UnmappedFilledJobs { get; private set; }
        /// <summary>Empty HOME units available for new households, from the residential
        /// stock; nominal person capacity is deliberately not used here.</summary>
        public int AvailableHomeUnits { get; private set; }
        /// <summary>Whether an outside connection usable by immigrants exists. The native
        /// grounding of "entry path" is still pending evidence; callers pass their best
        /// current observation and the model only consumes the flag.</summary>
        public bool EntryPathOpen { get; private set; }

        public EmploymentMarketInput(uint frame, int[] workforceByCell, int[] jobCapacityBySegment,
            int[] jobFilledBySegment, int unmappedJobCapacity, int unmappedFilledJobs,
            int availableHomeUnits, bool entryPathOpen)
        {
            if (workforceByCell == null) throw new ArgumentNullException("workforceByCell");
            if (jobCapacityBySegment == null) throw new ArgumentNullException("jobCapacityBySegment");
            if (jobFilledBySegment == null) throw new ArgumentNullException("jobFilledBySegment");
            EmploymentSegments.ValidateCellVector(workforceByCell, "workforceByCell");
            EmploymentSegments.ValidateSegmentVector(jobCapacityBySegment, "jobCapacityBySegment");
            EmploymentSegments.ValidateSegmentVector(jobFilledBySegment, "jobFilledBySegment");
            for (int cell = 0; cell < workforceByCell.Length; cell++)
                if (workforceByCell[cell] < 0) throw new ArgumentException("Workforce cells cannot be negative.", "workforceByCell");
            for (int segment = 0; segment < jobCapacityBySegment.Length; segment++)
                if (jobCapacityBySegment[segment] < 0) throw new ArgumentException("Job capacity cannot be negative.", "jobCapacityBySegment");
            for (int segment = 0; segment < jobFilledBySegment.Length; segment++)
                if (jobFilledBySegment[segment] < 0) throw new ArgumentException("Filled jobs cannot be negative.", "jobFilledBySegment");
            if (unmappedJobCapacity < 0 || unmappedFilledJobs < 0) throw new ArgumentException("Unmapped job counts cannot be negative.");
            if (availableHomeUnits < 0) throw new ArgumentOutOfRangeException("availableHomeUnits");
            Frame = frame;
            WorkforceByCell = (int[])workforceByCell.Clone();
            JobCapacityBySegment = (int[])jobCapacityBySegment.Clone();
            JobFilledBySegment = (int[])jobFilledBySegment.Clone();
            UnmappedJobCapacity = unmappedJobCapacity;
            UnmappedFilledJobs = unmappedFilledJobs;
            AvailableHomeUnits = availableHomeUnits;
            EntryPathOpen = entryPathOpen;
        }
    }
}
