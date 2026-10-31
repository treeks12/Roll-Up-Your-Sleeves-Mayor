using System;

namespace SC4Evolution.Simulation.Employment
{
    /// <summary>Deterministic employment market over census aggregates (sprint 01).
    ///
    /// Bookkeeping rules, in one place:
    /// 1. The model owns an internal allocation of observed filled jobs to workforce
    ///    cells. A cell may hold jobs in a segment only when qualification allows it, so a
    ///    new office never absorbs unemployment below its education floor.
    /// 2. Hiring into a segment prefers exact-education cells, then the segment's wealth
    ///    tier, then remaining wealth, always ascending; layoffs run the same order
    ///    backwards, so overqualified and off-tier jobs are released first.
    /// 3. When the observed filled count exceeds eligible workforce, the remainder becomes
    ///    UnfilledObservedFilled; nothing is invented to hide the mismatch.
    /// 4. Job capacity, filled jobs, vacancies and unemployment stay separate quantities;
    ///    vacancy = capacity - filled is observation, not a result of hiring.
    /// 5. Immigration needs compatible unfillable vacancies, free home units and an open
    ///    entry path together. Grants are reported per cell and never mutate the model's
    ///    workforce; the next census observes arrivals natively.
    /// All arithmetic is exact integer math; there is no randomness, so equal inputs on
    /// equal state always produce equal reports.</summary>
    public sealed class EmploymentMarket
    {
        private readonly EmploymentQualification _qualification;
        private readonly int[,] _allocated = new int[EmploymentSegments.Segments, EmploymentSegments.WorkforceCells];
        private readonly int[][] _hireOrder;

        public EmploymentMarket(EmploymentQualification qualification)
        {
            if (qualification == null) throw new ArgumentNullException("qualification");
            _qualification = qualification;
            _hireOrder = new int[EmploymentSegments.Segments][];
            for (int segment = 0; segment < EmploymentSegments.Segments; segment++)
                _hireOrder[segment] = BuildCellOrder(segment);
        }

        public EmploymentQualification Qualification
        {
            get { return _qualification; }
        }

        public EmploymentMarketReport Apply(EmploymentMarketInput input)
        {
            if (input == null) throw new ArgumentNullException("input");
            ReconcileWorkforce(input.WorkforceByCell);
            int[] modelFilled = new int[EmploymentSegments.Segments];
            int[] unfilledObserved = new int[EmploymentSegments.Segments];
            for (int segment = 0; segment < EmploymentSegments.Segments; segment++)
            {
                int current = SegmentEmployed(segment);
                int target = input.JobFilledBySegment[segment];
                if (current < target) Hire(segment, target - current, input.WorkforceByCell);
                else if (current > target) LayOff(segment, current - target);
                current = SegmentEmployed(segment);
                modelFilled[segment] = current;
                if (current < target) unfilledObserved[segment] = target - current;
            }
            return BuildReport(input, modelFilled, unfilledObserved);
        }

        private int SegmentEmployed(int segment)
        {
            int total = 0;
            for (int cell = 0; cell < EmploymentSegments.WorkforceCells; cell++) total += _allocated[segment, cell];
            return total;
        }

        private int CellEmployed(int cell)
        {
            int total = 0;
            for (int segment = 0; segment < EmploymentSegments.Segments; segment++) total += _allocated[segment, cell];
            return total;
        }

        private void ReconcileWorkforce(int[] workforce)
        {
            for (int cell = 0; cell < EmploymentSegments.WorkforceCells; cell++)
            {
                int surplus = CellEmployed(cell) - workforce[cell];
                if (surplus <= 0) continue;
                int[] segments = BuildCellSegmentOrder(cell);
                for (int i = segments.Length - 1; i >= 0 && surplus > 0; i--)
                {
                    int segment = segments[i];
                    int drop = Math.Min(surplus, _allocated[segment, cell]);
                    _allocated[segment, cell] -= drop;
                    surplus -= drop;
                }
            }
        }

        private void Hire(int segment, int openings, int[] workforce)
        {
            int[] order = _hireOrder[segment];
            for (int i = 0; i < order.Length && openings > 0; i++)
            {
                int cell = order[i];
                int free = workforce[cell] - CellEmployed(cell);
                if (free <= 0) continue;
                int take = Math.Min(openings, free);
                _allocated[segment, cell] += take;
                openings -= take;
            }
        }

        private void LayOff(int segment, int excess)
        {
            int[] order = _hireOrder[segment];
            for (int i = order.Length - 1; i >= 0 && excess > 0; i--)
            {
                int cell = order[i];
                int drop = Math.Min(excess, _allocated[segment, cell]);
                _allocated[segment, cell] -= drop;
                excess -= drop;
            }
        }

        private EmploymentMarketReport BuildReport(EmploymentMarketInput input, int[] modelFilled, int[] unfilledObserved)
        {
            int[] employed = new int[EmploymentSegments.WorkforceCells];
            int[] unemployed = new int[EmploymentSegments.WorkforceCells];
            for (int cell = 0; cell < EmploymentSegments.WorkforceCells; cell++)
            {
                employed[cell] = CellEmployed(cell);
                unemployed[cell] = input.WorkforceByCell[cell] - employed[cell];
                if (unemployed[cell] < 0) throw new InvalidOperationException("Employment allocation exceeded workforce.");
            }
            int[] vacant = new int[EmploymentSegments.Segments];
            int[] unfillable = new int[EmploymentSegments.Segments];
            int[] sectorPressure = new int[EmploymentSegments.Segments];
            int filledAboveCapacity = 0;
            for (int segment = 0; segment < EmploymentSegments.Segments; segment++)
            {
                int capacity = input.JobCapacityBySegment[segment];
                int filled = input.JobFilledBySegment[segment];
                if (filled > capacity) filledAboveCapacity++;
                vacant[segment] = Math.Max(0, capacity - filled);
                int eligibleUnemployed = 0, floorUnemployed = 0;
                int floor = _qualification.MinimumEducation(segment);
                for (int cell = 0; cell < EmploymentSegments.WorkforceCells; cell++)
                {
                    if (!_qualification.IsEligible(segment, cell)) continue;
                    eligibleUnemployed += unemployed[cell];
                    if (EmploymentSegments.EducationOfCell(cell) == floor) floorUnemployed += unemployed[cell];
                }
                unfillable[segment] = Math.Min(vacant[segment], Math.Max(0, vacant[segment] - eligibleUnemployed));
                sectorPressure[segment] = floorUnemployed;
            }
            int[] residentialPressure = new int[EmploymentSegments.WorkforceCells];
            int[] demand = new int[EmploymentSegments.WorkforceCells];
            for (int segment = 0; segment < EmploymentSegments.Segments; segment++)
            {
                if (unfillable[segment] <= 0) continue;
                int cell = EmploymentSegments.CellIndex(
                    EmploymentSegments.TierOfSegment(segment), _qualification.MinimumEducation(segment));
                demand[cell] += unfillable[segment];
                residentialPressure[cell] += unfillable[segment];
            }
            int[] grant = new int[EmploymentSegments.WorkforceCells];
            ImmigrationBlockReason[] reason = new ImmigrationBlockReason[EmploymentSegments.WorkforceCells];
            int remainingHomes = input.AvailableHomeUnits;
            for (int cell = 0; cell < EmploymentSegments.WorkforceCells; cell++)
            {
                if (demand[cell] == 0) { reason[cell] = ImmigrationBlockReason.None; continue; }
                if (!input.EntryPathOpen) { reason[cell] = ImmigrationBlockReason.BlockedNoEntryPath; continue; }
                if (remainingHomes == 0) { reason[cell] = ImmigrationBlockReason.BlockedNoResidentialCapacity; continue; }
                int taken = Math.Min(demand[cell], remainingHomes);
                grant[cell] = taken;
                remainingHomes -= taken;
                reason[cell] = taken == demand[cell]
                    ? ImmigrationBlockReason.Granted
                    : ImmigrationBlockReason.PartialResidentialCapacity;
            }
            int usedHomes = 0;
            for (int cell = 0; cell < EmploymentSegments.WorkforceCells; cell++) usedHomes += grant[cell];
            return new EmploymentMarketReport(input.Frame,
                employed, unemployed, modelFilled, unfilledObserved, vacant, unfillable, sectorPressure,
                residentialPressure, demand, grant, reason, input.AvailableHomeUnits, usedHomes,
                input.EntryPathOpen, input.UnmappedJobCapacity, input.UnmappedFilledJobs, filledAboveCapacity);
        }

        /// <summary>Cells eligible for a segment in hire-priority order: education distance
        /// to the floor ascending, then the segment's tier wealth, then other wealths.</summary>
        private int[] BuildCellOrder(int segment)
        {
            int floor = _qualification.MinimumEducation(segment);
            int tier = EmploymentSegments.TierOfSegment(segment);
            int[] cells = new int[EmploymentSegments.WorkforceCells];
            int count = 0;
            for (int cell = 0; cell < EmploymentSegments.WorkforceCells; cell++)
                if (_qualification.IsEligible(segment, cell)) cells[count++] = cell;
            Array.Resize(ref cells, count);
            for (int i = 1; i < cells.Length; i++)
            {
                int cell = cells[i], j = i - 1;
                while (j >= 0 && HireKey(segment, floor, tier, cells[j]) > HireKey(segment, floor, tier, cell))
                { cells[j + 1] = cells[j]; j--; }
                cells[j + 1] = cell;
            }
            return cells;
        }

        private static int HireKey(int segment, int floor, int tier, int cell)
        {
            int educationDistance = EmploymentSegments.EducationOfCell(cell) - floor;
            int tierPenalty = EmploymentSegments.WealthOfCell(cell) == tier ? 0 : 1;
            return educationDistance * 10 + tierPenalty * 5 + EmploymentSegments.WealthOfCell(cell);
        }

        /// <summary>Segments a cell may work in, most-preferred first, using the same key
        /// as hiring; layoffs and workforce reconciliation strip from the end.</summary>
        private int[] BuildCellSegmentOrder(int cell)
        {
            int[] segments = new int[EmploymentSegments.Segments];
            int count = 0;
            for (int segment = 0; segment < EmploymentSegments.Segments; segment++)
                if (_qualification.IsEligible(segment, cell)) segments[count++] = segment;
            Array.Resize(ref segments, count);
            for (int i = 1; i < segments.Length; i++)
            {
                int segment = segments[i], j = i - 1;
                while (j >= 0 && HireKey(segments[j],
                    _qualification.MinimumEducation(segments[j]),
                    EmploymentSegments.TierOfSegment(segments[j]), cell) >
                    HireKey(segment, _qualification.MinimumEducation(segment),
                    EmploymentSegments.TierOfSegment(segment), cell))
                { segments[j + 1] = segments[j]; j--; }
                segments[j + 1] = segment;
            }
            return segments;
        }
    }
}
