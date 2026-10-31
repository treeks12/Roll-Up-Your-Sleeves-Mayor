using System;

namespace SC4Evolution.Simulation.Employment
{
    /// <summary>Explicit workforce-to-job transition table (sprint 01). One cell may hold a
    /// job in a segment only when the cell's education is at least the segment minimum;
    /// eligibility is monotone in education, so higher-educated workers may take simpler
    /// jobs, but a segment never absorbs workforce below its education floor. Wealth does
    /// not gate eligibility: in the SC4 demand graph the education leaves (Workforce $ EQ1..4
    /// and siblings) carry no outgoing links, so no observed wealth-eligibility rule exists
    /// to copy; wealth only selects which cell new immigration is attributed to
    /// (see EmploymentMarket). The default minimum-education column is an authored
    /// hypothesis aligned with the segment naming and tiers in EmploymentSegments and is
    /// NOT an extracted SC4 coefficient; inject another table for tests or after evidence.</summary>
    public sealed class EmploymentQualification
    {
        private readonly int[] _minimumEducation;

        public static EmploymentQualification Default
        {
            get { return new EmploymentQualification(new[] { 0, 1, 2, 1, 2, 0, 0, 1, 3 }); }
        }

        public EmploymentQualification(int[] minimumEducationBySegment)
        {
            if (minimumEducationBySegment == null) throw new ArgumentNullException("minimumEducationBySegment");
            EmploymentSegments.ValidateSegmentVector(minimumEducationBySegment, "minimumEducationBySegment");
            for (int segment = 0; segment < EmploymentSegments.Segments; segment++)
            {
                int value = minimumEducationBySegment[segment];
                if (value < 0 || value >= EmploymentSegments.EducationLevels)
                    throw new ArgumentOutOfRangeException("minimumEducationBySegment",
                        "Segment " + EmploymentSegments.SegmentName(segment) + " requires education 0.." + (EmploymentSegments.EducationLevels - 1) + ".");
            }
            _minimumEducation = (int[])minimumEducationBySegment.Clone();
        }

        public int MinimumEducation(int segment)
        {
            EmploymentSegments.ValidateSegment(segment);
            return _minimumEducation[segment];
        }

        public bool IsEligible(int segment, int cell)
        {
            EmploymentSegments.ValidateSegment(segment);
            EmploymentSegments.ValidateCell(cell);
            return EmploymentSegments.EducationOfCell(cell) >= _minimumEducation[segment];
        }

        /// <summary>Human-readable form of the whole table, so reports and reviews never
        /// depend on scattered magic numbers.</summary>
        public override string ToString()
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            for (int segment = 0; segment < EmploymentSegments.Segments; segment++)
            {
                if (segment > 0) text.Append("; ");
                text.Append(EmploymentSegments.SegmentName(segment));
                text.Append(">=EQ").Append(_minimumEducation[segment] + 1);
            }
            return text.ToString();
        }
    }
}
