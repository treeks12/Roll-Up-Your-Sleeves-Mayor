using System;

namespace SC4Evolution.Simulation.Taxation
{
    /// <summary>One accounting period observation per account (sprint 04). BaseIncome is the
    /// account's taxable base BEFORE the rate (native observation); CollectedActual is what
    /// the native treasury really collected this period. The model adds no revenue of its
    /// own anywhere.</summary>
    public sealed class TaxAccountingInput
    {
        public int Day { get; private set; }
        public int[] NominalRatePercent { get; private set; }
        public long[] BaseIncome { get { return (long[])_baseIncome.Clone(); } }
        public long[] CollectedActual { get { return (long[])_collectedActual.Clone(); } }
        private readonly long[] _baseIncome;
        private readonly long[] _collectedActual;

        public TaxAccountingInput(int day, int[] nominalRatePercent, long[] baseIncome, long[] collectedActual)
        {
            if (nominalRatePercent == null) throw new ArgumentNullException("nominalRatePercent");
            if (baseIncome == null) throw new ArgumentNullException("baseIncome");
            if (collectedActual == null) throw new ArgumentNullException("collectedActual");
            TaxAccounts.ValidateVector(nominalRatePercent, "nominalRatePercent");
            TaxAccounts.ValidateVector(baseIncome, "baseIncome");
            TaxAccounts.ValidateVector(collectedActual, "collectedActual");
            if (day < 0) throw new ArgumentOutOfRangeException("day");
            for (int account = 0; account < TaxAccounts.Accounts; account++)
            {
                TaxRateValidation.Validate(nominalRatePercent[account]);
                if (baseIncome[account] < 0) throw new ArgumentException("Base income cannot be negative.", "baseIncome");
                if (collectedActual[account] < 0) throw new ArgumentException("Collected amounts cannot be negative.", "collectedActual");
            }
            Day = day;
            NominalRatePercent = (int[])nominalRatePercent.Clone();
            _baseIncome = (long[])baseIncome.Clone();
            _collectedActual = (long[])collectedActual.Clone();
        }
    }

    /// <summary>Immutable accounting snapshot for one period. The five quantities stay
    /// separated exactly: NominalRate (policy), Modifier (demand meter, dimensionless),
    /// CollectibleIncome (expected revenue at policy), CollectedActual (observed native
    /// collection), Cash (cumulative, grows only by CollectedActual). ReconciliationDelta
    /// is CollectibleIncome - CollectedActual and is recorded, never silently zeroed.</summary>
    public sealed class TaxAccountingReport
    {
        public int Day { get; private set; }
        public int[] NominalRatePercent { get; private set; }
        public int[] EffectiveRatePercent { get; private set; }
        public float[] DemandModifier { get; private set; }
        public long[] CollectibleIncome { get; private set; }
        public long[] CollectedActual { get; private set; }
        public long[] ReconciliationDelta { get; private set; }
        public long[] CashByAccount { get; private set; }
        public long TotalCollected { get; private set; }
        private readonly long[] _baseIncome;

        /// <summary>Test/reporting helper: the observed base for one account.</summary>
        public long BaseIncomeOf(int account)
        {
            TaxAccounts.Validate(account);
            return _baseIncome[account];
        }

        internal TaxAccountingReport(int day, int[] nominal, int[] effective, float[] modifier,
            long[] collectible, long[] collected, long[] delta, long[] cash, long[] baseIncome)
        {
            Day = day;
            NominalRatePercent = nominal;
            EffectiveRatePercent = effective;
            DemandModifier = modifier;
            CollectibleIncome = collectible;
            CollectedActual = collected;
            ReconciliationDelta = delta;
            CashByAccount = cash;
            _baseIncome = baseIncome;
            long total = 0;
            for (int account = 0; account < collected.Length; account++) total += collected[account];
            TotalCollected = total;
        }
    }
}
