using System;
using SC4Evolution.Simulation;

internal static class Program
{
    private static int _assertions;

    private static int Main()
    {
        try
        {
            TestCurvesAgainstIndependentTables();
            TestFactorsAreIndependent();
            TestWeightedMaintenance();
            TestValidationAndOverflow();
            TestMonotonicity();
            Console.WriteLine("PASS: " + _assertions + " civic funding assertions.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            return 1;
        }
    }

    private static void TestCurvesAgainstIndependentTables()
    {
        int percent;
        CivicFundingArchetype[] archetypes = Archetypes();
        for (int archetypeIndex = 0; archetypeIndex < archetypes.Length; archetypeIndex++)
        {
            CivicFundingArchetype archetype = archetypes[archetypeIndex];
            for (percent = 0; percent <= 255; percent++)
            {
                CivicFundingEvaluation result = CivicFundingEvaluator.Evaluate(archetype, percent, percent, 1000);
                double expectedEfficiency = ReferenceInterpolate(percent, new int[] { 0, 100, 120 }, new double[] { 0, 100, 110 }) / 100.0;
                double[] coveragePoints = IsSchool(archetype)
                    ? new double[] { 50, 100, 110 }
                    : new double[] { 50, 100 };
                int[] coveragePercentPoints = IsSchool(archetype)
                    ? new int[] { 0, 100, 120 }
                    : new int[] { 0, 100 };
                double expectedCoverage = ReferenceInterpolate(percent, coveragePercentPoints, coveragePoints) / 100.0;
                Near(expectedEfficiency, result.EfficiencyFactor, "efficiency table archetype " + archetype + " percent " + percent);
                Near(expectedCoverage, result.CoverageFactor, "coverage table archetype " + archetype + " percent " + percent);
            }
        }

        CivicFundingEvaluation point0 = CivicFundingEvaluator.Evaluate(CivicFundingArchetype.ElementarySchool, 0, 0, 100);
        CivicFundingEvaluation point100 = CivicFundingEvaluator.Evaluate(CivicFundingArchetype.ElementarySchool, 100, 100, 100);
        CivicFundingEvaluation point120 = CivicFundingEvaluator.Evaluate(CivicFundingArchetype.ElementarySchool, 120, 120, 100);
        Near(0.0, point0.EfficiencyFactor, "efficiency endpoint 0");
        Near(0.5, point0.CoverageFactor, "school coverage endpoint 0");
        Near(1.0, point100.EfficiencyFactor, "efficiency endpoint 100");
        Near(1.0, point100.CoverageFactor, "school coverage endpoint 100");
        Near(1.1, point120.EfficiencyFactor, "efficiency endpoint 120");
        Near(1.1, point120.CoverageFactor, "school coverage endpoint 120");
        Near(1.0, CivicFundingEvaluator.Evaluate(CivicFundingArchetype.Hospital, 100, 120, 100).CoverageFactor, "health coverage clamps at 120");
        CivicFundingEvaluation inherited = CivicFundingEvaluator.Evaluate(CivicFundingArchetype.ElementarySchool, 150, 150, 1000);
        Near(1.1, inherited.EfficiencyFactor, "inherited efficiency holds final curve endpoint");
        Near(1.1, inherited.CoverageFactor, "inherited school coverage holds final curve endpoint");
        Equal(1500, inherited.MaintenanceCost, "inherited overfunding still charges the actual percentage");
        CivicFundingEvaluation mixedInherited = CivicFundingEvaluator.Evaluate(CivicFundingArchetype.Hospital, 255, 37, 98765);
        Equal((int)(98765L * (1000L * 255 + 200L * 37) / 120000L), mixedInherited.MaintenanceCost, "mixed inherited funding cost uses raw axes");
    }

    private static void TestFactorsAreIndependent()
    {
        CivicFundingEvaluation result = CivicFundingEvaluator.Evaluate(CivicFundingArchetype.ElementarySchool, 50, 100, 1000);
        Near(0.5, result.EfficiencyFactor, "capacity percent controls efficiency");
        Near(1.0, result.CoverageFactor, "coverage percent controls coverage independently");
        CivicFundingEvaluation reverse = CivicFundingEvaluator.Evaluate(CivicFundingArchetype.ElementarySchool, 100, 50, 1000);
        Near(1.0, reverse.EfficiencyFactor, "capacity percent independent reverse efficiency");
        Near(0.75, reverse.CoverageFactor, "coverage percent independent reverse coverage");
    }

    private static void TestWeightedMaintenance()
    {
        CivicFundingArchetype[] archetypes = Archetypes();
        int[] capacityWeights = new int[] { 300, 650, 300, 1000 };
        int[] coverageWeights = new int[] { 100, 100, 100, 200 };
        for (int i = 0; i < archetypes.Length; i++)
        {
            CivicFundingEvaluation full = CivicFundingEvaluator.Evaluate(archetypes[i], 100, 100, 98765);
            Equal(98765, full.MaintenanceCost, "full funding preserves base cost " + archetypes[i]);
            CivicFundingEvaluation zero = CivicFundingEvaluator.Evaluate(archetypes[i], 0, 0, 98765);
            Equal(0, zero.MaintenanceCost, "zero funding gives zero cost " + archetypes[i]);

            long numerator = 98765L * (capacityWeights[i] * 37L + coverageWeights[i] * 83L);
            long denominator = 100L * (capacityWeights[i] + coverageWeights[i]);
            CivicFundingEvaluation mixed = CivicFundingEvaluator.Evaluate(archetypes[i], 37, 83, 98765);
            Equal((int)(numerator / denominator), mixed.MaintenanceCost, "weighted mixed maintenance " + archetypes[i]);
        }

        CivicFundingEvaluation rounding = CivicFundingEvaluator.Evaluate(CivicFundingArchetype.Clinic, 100, 100, 3);
        int incorrectlyTruncatedItems = (int)(3L * 300 * 100 / 40000) + (int)(3L * 100 * 100 / 40000);
        Equal(3, rounding.MaintenanceCost, "round only after combining weighted items");
        Equal(2, incorrectlyTruncatedItems, "independent item truncation loses a unit in the regression case");

        Throws<OverflowException>(delegate
        {
            CivicFundingEvaluator.Evaluate(CivicFundingArchetype.Hospital, 120, 120, int.MaxValue);
        }, "result outside Int32 is rejected");
    }

    private static void TestValidationAndOverflow()
    {
        Throws<ArgumentOutOfRangeException>(delegate { CivicFundingEvaluator.Evaluate(CivicFundingArchetype.Clinic, -1, 0, 0); }, "capacity below range");
        Throws<ArgumentOutOfRangeException>(delegate { CivicFundingEvaluator.Evaluate(CivicFundingArchetype.Clinic, 0, 256, 0); }, "coverage above native saved range");
        Throws<ArgumentOutOfRangeException>(delegate { CivicFundingEvaluator.Evaluate(CivicFundingArchetype.Clinic, 256, 0, 0); }, "capacity above native saved range");
        Throws<ArgumentOutOfRangeException>(delegate { CivicFundingEvaluator.Evaluate(CivicFundingArchetype.Clinic, 0, 0, -1); }, "negative base cost");
        Throws<ArgumentOutOfRangeException>(delegate { CivicFundingEvaluator.Evaluate((CivicFundingArchetype)99, 0, 0, 0); }, "unknown archetype");
    }

    private static void TestMonotonicity()
    {
        CivicFundingArchetype[] archetypes = Archetypes();
        for (int archetypeIndex = 0; archetypeIndex < archetypes.Length; archetypeIndex++)
        {
            CivicFundingArchetype archetype = archetypes[archetypeIndex];
            CivicFundingEvaluation previousCapacity = CivicFundingEvaluator.Evaluate(archetype, 0, 60, 100000);
            CivicFundingEvaluation previousCoverage = CivicFundingEvaluator.Evaluate(archetype, 60, 0, 100000);
            for (int percent = 1; percent <= 255; percent++)
            {
                CivicFundingEvaluation capacity = CivicFundingEvaluator.Evaluate(archetype, percent, 60, 100000);
                CivicFundingEvaluation coverage = CivicFundingEvaluator.Evaluate(archetype, 60, percent, 100000);
                Require(capacity.EfficiencyFactor >= previousCapacity.EfficiencyFactor, "efficiency monotonic " + archetype + " at " + percent);
                Require(capacity.MaintenanceCost >= previousCapacity.MaintenanceCost, "capacity maintenance monotonic " + archetype + " at " + percent);
                Require(coverage.CoverageFactor >= previousCoverage.CoverageFactor, "coverage monotonic " + archetype + " at " + percent);
                Require(coverage.MaintenanceCost >= previousCoverage.MaintenanceCost, "coverage maintenance monotonic " + archetype + " at " + percent);
                previousCapacity = capacity;
                previousCoverage = coverage;
            }
        }
    }

    private static double ReferenceInterpolate(int input, int[] x, double[] y)
    {
        if (input <= x[0])
            return y[0];
        for (int i = 1; i < x.Length; i++)
        {
            if (input <= x[i])
            {
                double fraction = (input - x[i - 1]) / (double)(x[i] - x[i - 1]);
                return y[i - 1] + fraction * (y[i] - y[i - 1]);
            }
        }
        return y[y.Length - 1];
    }

    private static CivicFundingArchetype[] Archetypes()
    {
        return new CivicFundingArchetype[]
        {
            CivicFundingArchetype.ElementarySchool,
            CivicFundingArchetype.HighSchool,
            CivicFundingArchetype.Clinic,
            CivicFundingArchetype.Hospital
        };
    }

    private static bool IsSchool(CivicFundingArchetype archetype)
    {
        return archetype == CivicFundingArchetype.ElementarySchool || archetype == CivicFundingArchetype.HighSchool;
    }

    private static void Near(double expected, double actual, string message)
    {
        if (Math.Abs(expected - actual) > 0.0000000001)
            throw new InvalidOperationException(message + ": expected " + expected + ", got " + actual);
        _assertions++;
    }

    private static void Equal(int expected, int actual, string message)
    {
        if (expected != actual)
            throw new InvalidOperationException(message + ": expected " + expected + ", got " + actual);
        _assertions++;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
        _assertions++;
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            _assertions++;
            return;
        }
        throw new InvalidOperationException(message + ": expected " + typeof(T).Name);
    }
}
