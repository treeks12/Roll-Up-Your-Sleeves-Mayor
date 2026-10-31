using System;

namespace SC4Evolution.Mod.Economy
{
    /// <summary>
    /// One aggregate tax segment used by the shadow ledger. The key is a caller
    /// supplied label (for example, "Residential" or "Commercial"); it is not
    /// interpreted as a native Cities: Skylines account.
    /// </summary>
    public struct ShadowTaxSegment
    {
        public ShadowTaxSegment(string key, long taxableBase, int taxRatePercent)
        {
            ValidateKey(key);
            ValidateBase(taxableBase);
            ValidateRate(taxRatePercent);

            Key = key;
            TaxableBase = taxableBase;
            TaxRatePercent = taxRatePercent;
        }

        public string Key { get; private set; }
        public long TaxableBase { get; private set; }
        public int TaxRatePercent { get; private set; }

        internal void Validate()
        {
            ValidateKey(Key);
            ValidateBase(TaxableBase);
            ValidateRate(TaxRatePercent);
        }

        private static void ValidateKey(string key)
        {
            if (String.IsNullOrEmpty(key) || key.Trim().Length == 0)
                throw new ArgumentException("A shadow tax segment needs a non-empty key.", "key");
        }

        private static void ValidateBase(long taxableBase)
        {
            if (taxableBase < 0)
                throw new ArgumentOutOfRangeException("taxableBase", taxableBase, "Taxable base cannot be negative.");
        }

        private static void ValidateRate(int taxRatePercent)
        {
            if (taxRatePercent < 0 || taxRatePercent > 100)
                throw new ArgumentOutOfRangeException("taxRatePercent", taxRatePercent, "Tax rate must be between 0 and 100 percent.");
        }
    }

    /// <summary>
    /// Read-only observations consumed by <see cref="ShadowTaxLedger"/>.
    /// ObservedCash is a signal only: the ledger never writes it back to the
    /// native treasury.
    /// </summary>
    public sealed class ShadowTaxInput
    {
        public const int MinimumTaxMultiplierPercent = 0;
        public const int MaximumTaxMultiplierPercent = 1000;

        private readonly ShadowTaxSegment[] _segments;

        public ShadowTaxInput(long observedCash, int taxMultiplierPercent, int population, ShadowTaxSegment[] segments)
        {
            if (taxMultiplierPercent < MinimumTaxMultiplierPercent || taxMultiplierPercent > MaximumTaxMultiplierPercent)
                throw new ArgumentOutOfRangeException("taxMultiplierPercent", taxMultiplierPercent, "Tax multiplier must be between 0 and 1000 percent.");
            if (population < 0)
                throw new ArgumentOutOfRangeException("population", population, "Population cannot be negative.");
            if (segments == null)
                throw new ArgumentNullException("segments");

            _segments = (ShadowTaxSegment[])segments.Clone();
            for (int i = 0; i < _segments.Length; i++)
                _segments[i].Validate();

            ObservedCash = observedCash;
            TaxMultiplierPercent = taxMultiplierPercent;
            Population = population;
        }

        public long ObservedCash { get; private set; }
        public int TaxMultiplierPercent { get; private set; }
        public int Population { get; private set; }

        /// <summary>Returns a clone so callers cannot mutate an accepted observation.</summary>
        public ShadowTaxSegment[] Segments
        {
            get { return (ShadowTaxSegment[])_segments.Clone(); }
        }
    }

    /// <summary>
    /// Result of one shadow observation. ExpectedRevenue and VirtualBalance are
    /// synthetic values and must not be posted to native cash.
    /// </summary>
    public sealed class ShadowTaxResult
    {
        internal ShadowTaxResult(
            long observedCash,
            long virtualTaxBase,
            long expectedRevenue,
            long virtualBalance,
            int fiscalPressurePercent,
            int confidencePercent,
            int segmentCount,
            long appliedPeriods)
        {
            ObservedCash = observedCash;
            VirtualTaxBase = virtualTaxBase;
            ExpectedRevenue = expectedRevenue;
            VirtualBalance = virtualBalance;
            FiscalPressurePercent = fiscalPressurePercent;
            ConfidencePercent = confidencePercent;
            SegmentCount = segmentCount;
            AppliedPeriods = appliedPeriods;
        }

        public long ObservedCash { get; private set; }
        public long VirtualTaxBase { get; private set; }
        public long ExpectedRevenue { get; private set; }
        public long VirtualBalance { get; private set; }
        public int FiscalPressurePercent { get; private set; }
        public int ConfidencePercent { get; private set; }
        public int SegmentCount { get; private set; }
        public long AppliedPeriods { get; private set; }

        public bool ObservedDeficit
        {
            get { return ObservedCash < 0; }
        }
    }

    /// <summary>
    /// Small, stateful virtual ledger for a shadow tax signal. It only keeps its
    /// own cumulative balance; the observed native cash in each input is never
    /// changed and no game API is referenced here.
    /// </summary>
    public sealed class ShadowTaxLedger
    {
        private long _virtualBalance;
        private long _appliedPeriods;

        public long VirtualBalance
        {
            get { return _virtualBalance; }
        }

        public long AppliedPeriods
        {
            get { return _appliedPeriods; }
        }

        public ShadowTaxResult Apply(ShadowTaxInput input)
        {
            if (input == null)
                throw new ArgumentNullException("input");

            ShadowTaxSegment[] segments = input.Segments;
            long virtualTaxBase = 0;
            long expectedRevenue = 0;
            for (int i = 0; i < segments.Length; i++)
            {
                virtualTaxBase = SaturatingAdd(virtualTaxBase, segments[i].TaxableBase);
                long nominalRevenue = Percent(segments[i].TaxableBase, segments[i].TaxRatePercent);
                long adjustedRevenue = Percent(nominalRevenue, input.TaxMultiplierPercent);
                expectedRevenue = SaturatingAdd(expectedRevenue, adjustedRevenue);
            }

            _virtualBalance = SaturatingAdd(_virtualBalance, expectedRevenue);
            _appliedPeriods = SaturatingAdd(_appliedPeriods, 1);

            return new ShadowTaxResult(
                input.ObservedCash,
                virtualTaxBase,
                expectedRevenue,
                _virtualBalance,
                ComputePressure(expectedRevenue, input.ObservedCash, input.Population),
                ComputeConfidence(segments, virtualTaxBase, input.Population),
                segments.Length,
                _appliedPeriods);
        }

        public void Reset()
        {
            _virtualBalance = 0;
            _appliedPeriods = 0;
        }

        /// <summary>
        /// Pressure is a bounded diagnostic index, not a native game meter. It
        /// compares current virtual tax burden plus an observed deficit against
        /// positive cash and one unit per resident. This keeps the formula
        /// monotonic while making its inputs explicit and testable.
        /// </summary>
        private static int ComputePressure(long expectedRevenue, long observedCash, int population)
        {
            long deficit = observedCash < 0
                ? (observedCash == long.MinValue ? long.MaxValue : -observedCash)
                : 0;
            long burden = SaturatingAdd(expectedRevenue, deficit);
            if (burden == 0)
                return 0;

            long positiveCash = observedCash > 0 ? observedCash : 0;
            long relief = SaturatingAdd(positiveCash, population);
            relief = SaturatingAdd(relief, 1);
            long denominator = SaturatingAdd(burden, relief);
            if (denominator <= 0 || burden >= denominator)
                return 100;

            decimal pressure = (decimal)burden * 100m / denominator;
            if (pressure <= 0m)
                return 0;
            if (pressure >= 100m)
                return 100;
            return (int)pressure;
        }

        /// <summary>
        /// Confidence describes input completeness for this aggregate estimate.
        /// It intentionally stops at 80: segment aggregates do not prove native
        /// CS1/SC4 account identity, even when every supplied value is valid.
        /// </summary>
        private static int ComputeConfidence(ShadowTaxSegment[] segments, long virtualTaxBase, int population)
        {
            if (segments.Length == 0)
                return 0;

            int confidence = 40;
            if (virtualTaxBase > 0)
                confidence += 20;
            if (population > 0)
                confidence += 20;
            return confidence > 80 ? 80 : confidence;
        }

        private static long Percent(long value, int percent)
        {
            if (value == 0 || percent == 0)
                return 0;

            decimal scaled = (decimal)value * percent / 100m;
            return scaled >= long.MaxValue ? long.MaxValue : (long)scaled;
        }

        private static long SaturatingAdd(long left, long right)
        {
            if (right > 0 && left > long.MaxValue - right)
                return long.MaxValue;
            if (right < 0 && left < long.MinValue - right)
                return long.MinValue;
            return left + right;
        }
    }
}
