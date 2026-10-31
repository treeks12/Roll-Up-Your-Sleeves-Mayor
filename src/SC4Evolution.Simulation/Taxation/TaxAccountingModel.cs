using System;
using System.Collections.Generic;

namespace SC4Evolution.Simulation.Taxation
{
    /// <summary>Deterministic tax accounting engine (sprint 04). One Apply per period:
    /// records each account's nominal rate into its windowed reaction history, computes the
    /// demand modifier from the EFFECTIVE (windowed) rate via the injected response curve,
    /// computes collectible income from the CURRENT nominal rate, adds ONLY the observed
    /// native collection to cash, and records the reconciliation delta. Meters never mint
    /// money; the native calculation is untouched whenever the adapter is not enabled.</summary>
    public sealed class TaxAccountingModel
    {
        public const int DefaultWindowDays = 90;
        public const int DefaultMaxEntries = 366;

        private readonly ITaxResponseCurve _curve;
        private readonly TaxReactionHistory[] _history;
        private readonly long[] _cash;

        public TaxAccountingModel(ITaxResponseCurve curve)
            : this(curve, DefaultWindowDays, DefaultMaxEntries)
        {
        }

        public TaxAccountingModel(ITaxResponseCurve curve, int windowDays, int maxEntries)
        {
            if (curve == null) throw new ArgumentNullException("curve");
            _curve = curve;
            _history = new TaxReactionHistory[TaxAccounts.Accounts];
            _cash = new long[TaxAccounts.Accounts];
            for (int account = 0; account < TaxAccounts.Accounts; account++)
                _history[account] = new TaxReactionHistory(windowDays, maxEntries);
        }

        public TaxAccountingReport Apply(TaxAccountingInput input)
        {
            if (input == null) throw new ArgumentNullException("input");
            for (int account = 0; account < TaxAccounts.Accounts; account++)
                _history[account].Record(input.Day, input.NominalRatePercent[account]);
            int[] effective = new int[TaxAccounts.Accounts];
            float[] modifier = new float[TaxAccounts.Accounts];
            long[] collectible = new long[TaxAccounts.Accounts];
            long[] delta = new long[TaxAccounts.Accounts];
            long[] collected = new long[TaxAccounts.Accounts];
            long[] cash = new long[TaxAccounts.Accounts];
            for (int account = 0; account < TaxAccounts.Accounts; account++)
            {
                effective[account] = _history[account].EffectiveRate(input.Day);
                modifier[account] = _curve.DemandFactor(account, effective[account]);
                if (float.IsNaN(modifier[account]) || float.IsInfinity(modifier[account]) || modifier[account] < 0f)
                    throw new InvalidOperationException("Tax response curve produced an invalid factor for " + TaxAccounts.Name(account) + ".");
                collectible[account] = input.BaseIncome[account] * input.NominalRatePercent[account] / 100L;
                collected[account] = input.CollectedActual[account];
                _cash[account] += collected[account];
                cash[account] = _cash[account];
                delta[account] = collectible[account] - collected[account];
            }
            return new TaxAccountingReport(input.Day, (int[])input.NominalRatePercent.Clone(),
                effective, modifier, collectible, collected, delta, cash, input.BaseIncome);
        }

        public long Cash(int account)
        {
            TaxAccounts.Validate(account);
            return _cash[account];
        }

        public byte[] Save()
        {
            return TaxHistoryCodec.Save(this);
        }

        public static TaxAccountingModel Restore(ITaxResponseCurve curve, int windowDays, int maxEntries, byte[] data)
        {
            if (curve == null) throw new ArgumentNullException("curve");
            if (data == null) throw new ArgumentNullException("data");
            TaxAccountingModel model = new TaxAccountingModel(curve, windowDays, maxEntries);
            TaxHistoryCodec.ReadInto(model, data, windowDays, maxEntries);
            return model;
        }

        internal TaxReactionHistory History(int account)
        {
            TaxAccounts.Validate(account);
            return _history[account];
        }

        internal void InstallHistory(int account, TaxReactionHistory history)
        {
            TaxAccounts.Validate(account);
            if (history == null) throw new ArgumentNullException("history");
            _history[account] = history;
        }
    }
}
