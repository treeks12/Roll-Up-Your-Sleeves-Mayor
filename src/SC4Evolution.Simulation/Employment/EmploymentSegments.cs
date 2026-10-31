using System;

namespace SC4Evolution.Simulation.Employment
{
    /// <summary>Stable keys for the segmented employment model (sprint 01).
    /// Workforce cells are wealth-major like CityCensus: index = wealth*4 + education,
    /// wealth 0..2 (native WealthLevel), education 0..3 (native EducationLevel; SC4 EQ1..4).
    /// Job segments reuse the provisional native-category adapter order already published by
    /// CityCensus.SegmentFor: CS$, CS$$, CS$$$, CO$$, CO$$$, IR, ID, IM, IHT.</summary>
    public static class EmploymentSegments
    {
        public const int WealthLevels = 3;
        public const int EducationLevels = 4;
        public const int WorkforceCells = WealthLevels * EducationLevels;
        public const int Segments = 9;

        private static readonly string[] SegmentNames = new[]
        {
            "CS$", "CS$$", "CS$$$", "CO$$", "CO$$$", "IR", "ID", "IM", "IHT"
        };

        // Authored wealth tier per segment, following the segment naming above and the
        // installed SC4 demand taxonomy (Jobs $/$$/$$$ tiers, sector census CS$/CS$$/CS$$$,
        // CO$$/CO$$$, IR, ID, IM, IH). Declared hypothesis until native confirmation.
        private static readonly int[] SegmentTiers = new[] { 0, 1, 2, 1, 2, 0, 0, 1, 2 };

        public static string SegmentName(int segment)
        {
            ValidateSegment(segment);
            return SegmentNames[segment];
        }

        public static int TierOfSegment(int segment)
        {
            ValidateSegment(segment);
            return SegmentTiers[segment];
        }

        public static int CellIndex(int wealth, int education)
        {
            if (wealth < 0 || wealth >= WealthLevels) throw new ArgumentOutOfRangeException("wealth");
            if (education < 0 || education >= EducationLevels) throw new ArgumentOutOfRangeException("education");
            return wealth * EducationLevels + education;
        }

        public static int WealthOfCell(int cell)
        {
            ValidateCell(cell);
            return cell / EducationLevels;
        }

        public static int EducationOfCell(int cell)
        {
            ValidateCell(cell);
            return cell % EducationLevels;
        }

        public static string CellName(int cell)
        {
            int wealth = WealthOfCell(cell), education = EducationOfCell(cell);
            string wealthName = wealth == 0 ? "$" : (wealth == 1 ? "$$" : "$$$");
            return "W" + wealthName + " EQ" + (education + 1);
        }

        public static void ValidateSegment(int segment)
        {
            if (segment < 0 || segment >= Segments) throw new ArgumentOutOfRangeException("segment");
        }

        public static void ValidateCell(int cell)
        {
            if (cell < 0 || cell >= WorkforceCells) throw new ArgumentOutOfRangeException("cell");
        }

        public static void ValidateCellVector(int[] values, string name)
        {
            if (values == null) throw new ArgumentNullException(name);
            if (values.Length != WorkforceCells) throw new ArgumentException("Expected " + WorkforceCells + " workforce cells.", name);
        }

        public static void ValidateSegmentVector(int[] values, string name)
        {
            if (values == null) throw new ArgumentNullException(name);
            if (values.Length != Segments) throw new ArgumentException("Expected " + Segments + " job segments.", name);
        }
    }
}
