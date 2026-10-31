using System;

namespace SC4Evolution.Simulation
{
    public enum CivicFundingArchetype
    {
        ElementarySchool,
        HighSchool,
        Clinic,
        Hospital
    }

    public sealed class CivicFundingEvaluation
    {
        internal CivicFundingEvaluation(double efficiencyFactor, double coverageFactor, int maintenanceCost)
        {
            EfficiencyFactor = efficiencyFactor;
            CoverageFactor = coverageFactor;
            MaintenanceCost = maintenanceCost;
        }

        public double EfficiencyFactor { get; }
        public double CoverageFactor { get; }
        public int MaintenanceCost { get; }
    }

    // Pure adapter only; no native building, budget, currency or service is changed.
    // Curves and dimensionless expense weights are from the verified SC4 inputs.
    // Local overrides use 0..120. Inherited native budgets may use the full saved
    // byte range; curves hold their endpoint while expense still uses raw funding.
    // One final integer truncation is an adapter rule.
    public static class CivicFundingEvaluator
    {
        public static CivicFundingEvaluation Evaluate(
            CivicFundingArchetype archetype,
            int capacityFundingPercent,
            int coverageFundingPercent,
            int fullNativeMaintenanceCost)
        {
            if (capacityFundingPercent < 0 || capacityFundingPercent > 255)
                throw new ArgumentOutOfRangeException("capacityFundingPercent", "Resolved funding percent must be between 0 and 255.");
            if (coverageFundingPercent < 0 || coverageFundingPercent > 255)
                throw new ArgumentOutOfRangeException("coverageFundingPercent", "Resolved funding percent must be between 0 and 255.");
            if (fullNativeMaintenanceCost < 0)
                throw new ArgumentOutOfRangeException("fullNativeMaintenanceCost", "Maintenance cost must be non-negative.");

            int capacityWeight;
            int coverageWeight;
            bool isSchool;
            switch (archetype)
            {
                case CivicFundingArchetype.ElementarySchool:
                    capacityWeight = 300;
                    coverageWeight = 100;
                    isSchool = true;
                    break;
                case CivicFundingArchetype.HighSchool:
                    capacityWeight = 650;
                    coverageWeight = 100;
                    isSchool = true;
                    break;
                case CivicFundingArchetype.Clinic:
                    capacityWeight = 300;
                    coverageWeight = 100;
                    isSchool = false;
                    break;
                case CivicFundingArchetype.Hospital:
                    capacityWeight = 1000;
                    coverageWeight = 200;
                    isSchool = false;
                    break;
                default:
                    throw new ArgumentOutOfRangeException("archetype", "Unknown civic funding archetype.");
            }

            double efficiencyFactor = InterpolateEfficiency(capacityFundingPercent) / 100.0;
            double coverageFactor = (isSchool
                ? InterpolateSchoolCoverage(coverageFundingPercent)
                : InterpolateHealthCoverage(coverageFundingPercent)) / 100.0;

            long weightedFunding = checked(
                (long)capacityWeight * capacityFundingPercent +
                (long)coverageWeight * coverageFundingPercent);
            long weightedCost = checked((long)fullNativeMaintenanceCost * weightedFunding);
            long denominator = checked(100L * (capacityWeight + coverageWeight));
            int maintenanceCost = checked((int)(weightedCost / denominator));

            return new CivicFundingEvaluation(efficiencyFactor, coverageFactor, maintenanceCost);
        }

        private static double InterpolateEfficiency(int percent)
        {
            if (percent >= 120)
                return 110.0;
            if (percent <= 100)
                return percent;
            return 100.0 + (percent - 100) * 0.5;
        }

        private static double InterpolateSchoolCoverage(int percent)
        {
            if (percent >= 120)
                return 110.0;
            if (percent <= 100)
                return 50.0 + percent * 0.5;
            return 100.0 + (percent - 100) * 0.5;
        }

        private static double InterpolateHealthCoverage(int percent)
        {
            if (percent <= 100)
                return 50.0 + percent * 0.5;
            return 100.0;
        }
    }
}
