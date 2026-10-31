using System;
using SC4Evolution.Mod.Economy;

internal static class Program
{
    private static int _checks;

    private static int Main()
    {
        try
        {
            SegmentAndInputValidation();
            RevenueAndLedgerAreVirtual();
            MultiplierAndPressureAreMonotonic();
            ConfidenceAndEmptyInput();
            SaturationAndReset();
            DeterminismAndCloning();
            Console.WriteLine("PASS: " + _checks + " shadow-tax assertions; pure virtual ledger only.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            return 1;
        }
    }

    private static void SegmentAndInputValidation()
    {
        ShadowTaxSegment valid = new ShadowTaxSegment("Residential", 1000, 10);
        Equal("Residential", valid.Key, "segment key");
        Equal(1000L, valid.TaxableBase, "segment base");
        Equal(10, valid.TaxRatePercent, "segment rate");

        Throws<ArgumentException>(delegate { new ShadowTaxSegment("", 1, 1); }, "empty key rejected");
        Throws<ArgumentOutOfRangeException>(delegate { new ShadowTaxSegment("R", -1, 1); }, "negative base rejected");
        Throws<ArgumentOutOfRangeException>(delegate { new ShadowTaxSegment("R", 1, 101); }, "rate above 100 rejected");
        Throws<ArgumentOutOfRangeException>(delegate { new ShadowTaxSegment("R", 1, -1); }, "negative rate rejected");

        ShadowTaxSegment[] segments = new[] { valid };
        ShadowTaxInput input = new ShadowTaxInput(-25, 100, 500, segments);
        segments[0] = new ShadowTaxSegment("Changed", 999, 99);
        Equal("Residential", input.Segments[0].Key, "input clones segment array");
        Equal(-25L, input.ObservedCash, "observed cash preserved");
        Equal(100, input.TaxMultiplierPercent, "multiplier preserved");
        Equal(500, input.Population, "population preserved");

        Throws<ArgumentNullException>(delegate { new ShadowTaxInput(0, 100, 1, null); }, "null segments rejected");
        Throws<ArgumentOutOfRangeException>(delegate { new ShadowTaxInput(0, -1, 1, new ShadowTaxSegment[0]); }, "negative multiplier rejected");
        Throws<ArgumentOutOfRangeException>(delegate { new ShadowTaxInput(0, 1001, 1, new ShadowTaxSegment[0]); }, "excessive multiplier rejected");
        Throws<ArgumentOutOfRangeException>(delegate { new ShadowTaxInput(0, 100, -1, new ShadowTaxSegment[0]); }, "negative population rejected");
        ShadowTaxSegment[] invalidDefault = new ShadowTaxSegment[1];
        Throws<ArgumentException>(delegate { new ShadowTaxInput(0, 100, 1, invalidDefault); }, "default segment rejected");
    }

    private static void RevenueAndLedgerAreVirtual()
    {
        ShadowTaxSegment[] segments =
        {
            new ShadowTaxSegment("Residential", 1000, 10),
            new ShadowTaxSegment("Commercial", 500, 20)
        };
        ShadowTaxInput input = new ShadowTaxInput(1000, 100, 1000, segments);
        ShadowTaxLedger ledger = new ShadowTaxLedger();

        ShadowTaxResult first = ledger.Apply(input);
        Equal(1500L, first.VirtualTaxBase, "aggregate base");
        Equal(200L, first.ExpectedRevenue, "expected revenue at neutral multiplier");
        Equal(200L, first.VirtualBalance, "virtual balance after first period");
        Equal(1000L, first.ObservedCash, "native cash is only observed");
        Equal(1L, first.AppliedPeriods, "period count");
        Equal(200L, ledger.VirtualBalance, "ledger balance");
        Equal(1000L, input.ObservedCash, "input remains unchanged by ledger");

        ShadowTaxResult second = ledger.Apply(input);
        Equal(400L, second.VirtualBalance, "ledger accumulates virtual revenue");
        Equal(2L, second.AppliedPeriods, "period count increments");
    }

    private static void MultiplierAndPressureAreMonotonic()
    {
        ShadowTaxSegment[] segments = { new ShadowTaxSegment("R", 1000, 10) };
        ShadowTaxResult neutral = new ShadowTaxLedger().Apply(new ShadowTaxInput(0, 100, 100, segments));
        ShadowTaxResult high = new ShadowTaxLedger().Apply(new ShadowTaxInput(0, 200, 100, segments));
        ShadowTaxResult funded = new ShadowTaxLedger().Apply(new ShadowTaxInput(10000, 200, 100, segments));
        ShadowTaxResult deficit = new ShadowTaxLedger().Apply(new ShadowTaxInput(-10000, 200, 100, segments));

        Equal(100L, neutral.ExpectedRevenue, "neutral revenue");
        Equal(200L, high.ExpectedRevenue, "multiplier scales revenue");
        True(high.FiscalPressurePercent > neutral.FiscalPressurePercent, "higher burden raises pressure");
        True(funded.FiscalPressurePercent < high.FiscalPressurePercent, "positive cash relieves pressure");
        True(deficit.FiscalPressurePercent > high.FiscalPressurePercent, "observed deficit raises pressure");
        True(deficit.ObservedDeficit, "deficit is reported");
    }

    private static void ConfidenceAndEmptyInput()
    {
        ShadowTaxLedger ledger = new ShadowTaxLedger();
        ShadowTaxResult empty = ledger.Apply(new ShadowTaxInput(0, 100, 0, new ShadowTaxSegment[0]));
        Equal(0L, empty.ExpectedRevenue, "empty revenue");
        Equal(0, empty.FiscalPressurePercent, "empty pressure");
        Equal(0, empty.ConfidencePercent, "empty confidence");
        Equal(0, empty.SegmentCount, "empty segment count");

        ShadowTaxResult partial = ledger.Apply(new ShadowTaxInput(0, 100, 0, new[] { new ShadowTaxSegment("R", 0, 10) }));
        Equal(40, partial.ConfidencePercent, "segment presence confidence");
        ShadowTaxResult complete = ledger.Apply(new ShadowTaxInput(0, 100, 100, new[] { new ShadowTaxSegment("R", 1, 10) }));
        Equal(80, complete.ConfidencePercent, "aggregate confidence cap");
        True(complete.ConfidencePercent < 100, "aggregate identity never claims native certainty");
    }

    private static void SaturationAndReset()
    {
        ShadowTaxSegment huge = new ShadowTaxSegment("Huge", long.MaxValue, 100);
        ShadowTaxLedger ledger = new ShadowTaxLedger();
        ShadowTaxResult saturated = ledger.Apply(new ShadowTaxInput(0, 1000, int.MaxValue, new[] { huge }));
        Equal(long.MaxValue, saturated.VirtualTaxBase, "base saturates");
        Equal(long.MaxValue, saturated.ExpectedRevenue, "revenue saturates");
        Equal(long.MaxValue, saturated.VirtualBalance, "balance saturates");
        ledger.Apply(new ShadowTaxInput(0, 100, 1, new[] { new ShadowTaxSegment("R", 10, 10) }));
        Equal(long.MaxValue, ledger.VirtualBalance, "saturated balance stays bounded");
        ledger.Reset();
        Equal(0L, ledger.VirtualBalance, "reset clears virtual balance");
        Equal(0L, ledger.AppliedPeriods, "reset clears period count");
    }

    private static void DeterminismAndCloning()
    {
        ShadowTaxSegment[] segments = { new ShadowTaxSegment("R", 1234, 17), new ShadowTaxSegment("I", 987, 6) };
        ShadowTaxInput input = new ShadowTaxInput(4321, 137, 321, segments);
        ShadowTaxResult left = new ShadowTaxLedger().Apply(input);
        ShadowTaxResult right = new ShadowTaxLedger().Apply(input);
        Equal(left.VirtualTaxBase, right.VirtualTaxBase, "deterministic base");
        Equal(left.ExpectedRevenue, right.ExpectedRevenue, "deterministic revenue");
        Equal(left.FiscalPressurePercent, right.FiscalPressurePercent, "deterministic pressure");
        Equal(left.ConfidencePercent, right.ConfidencePercent, "deterministic confidence");

        ShadowTaxSegment[] clone = input.Segments;
        clone[0] = new ShadowTaxSegment("mutated", 1, 1);
        Equal("R", input.Segments[0].Key, "segments getter clones");
    }

    private static void True(bool value, string message)
    {
        _checks++;
        if (!value) throw new InvalidOperationException("Assertion failed: " + message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        _checks++;
        if (!Object.Equals(expected, actual))
            throw new InvalidOperationException("Assertion failed: " + message + ". Expected " + expected + ", got " + actual + ".");
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        _checks++;
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException("Assertion failed: " + message + ". Expected " + typeof(T).Name + ".");
    }
}
