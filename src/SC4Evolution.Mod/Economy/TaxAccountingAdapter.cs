using System;
using SC4Evolution.Simulation.Taxation;

namespace SC4Evolution.Mod.Economy
{
    /// <summary>Explicit opt-in session for segmented tax accounting (sprint 04).
    /// No Harmony/game dependencies: the same source is compiled into the isolated harness.
    ///
    /// INTEGRATION CONTRACT (wired after the sprint-03 merge; EconomicBridge owns config and
    /// is not modified by this sprint): with the opt-in OFF this session never exists, so the
    /// native calculation is untouched (Shadow unchanged). With the opt-in ON, the bridge
    /// supplies, per native tax period: nominal rates (residential sliders by wealth class,
    /// business rates mapped by segment), each account's taxable base BEFORE the rate, and
    /// the native cash delta measured around the native collection — the collected amount
    /// enters as an observation only. The adapter adds no revenue and pays nothing: cash in
    /// the report reconciles against the native treasury, and every difference is reported
    /// as a delta instead of being adjusted away. Demand modifiers are meters; they feed the
    /// existing demand pipeline and never mint money. Persisted history uses the model codec
    /// through a future envelope extension file, not a new save key.</summary>
    public sealed class TaxAccountingSession
    {
        private readonly TaxAccountingModel _model;

        private TaxAccountingSession(TaxAccountingModel model)
        {
            _model = model;
        }

        public static TaxAccountingSession TryCreate(bool enabled, ITaxResponseCurve curve,
            int windowDays, int maxEntries)
        {
            if (!enabled) return null;
            if (curve == null) throw new ArgumentNullException("curve");
            return new TaxAccountingSession(new TaxAccountingModel(curve, windowDays, maxEntries));
        }

        public static TaxAccountingSession TryRestore(bool enabled, ITaxResponseCurve curve,
            int windowDays, int maxEntries, byte[] persisted)
        {
            if (!enabled) return null;
            if (curve == null) throw new ArgumentNullException("curve");
            return new TaxAccountingSession(TaxAccountingModel.Restore(curve, windowDays, maxEntries, persisted));
        }

        public byte[] Save()
        {
            return _model.Save();
        }

        public long Cash(int account)
        {
            return _model.Cash(account);
        }

        public TaxAccountingReport Apply(TaxAccountingInput input)
        {
            return _model.Apply(input);
        }
    }
}
