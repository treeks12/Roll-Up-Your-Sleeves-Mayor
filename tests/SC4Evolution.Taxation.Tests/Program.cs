using System;
using System.IO;
using SC4Evolution.Mod.Economy;
using SC4Evolution.Simulation.Taxation;

internal static class Program
{
    private static int _checks;

    /// <summary>Synthetic response curve for tests: residential accounts lose 2% demand
    /// per rate point above 10; business accounts lose 3% per point above 8. Exact,
    /// deterministic and obviously not native data.</summary>
    private sealed class SyntheticCurve : ITaxResponseCurve
    {
        public float DemandFactor(int account, int effectiveRatePercent)
        {
            TaxAccounts.Validate(account);
            TaxRateValidation.Validate(effectiveRatePercent);
            int pivot = TaxAccounts.IsResidential(account) ? 10 : 8;
            if (effectiveRatePercent <= pivot) return 1f;
            return Math.Max(0f, 1f - (TaxAccounts.IsResidential(account) ? 0.02f : 0.03f) * (effectiveRatePercent - pivot));
        }
    }

    private static int[] Rates(int residential, int business)
    {
        int[] rates = new int[TaxAccounts.Accounts];
        for (int i = 0; i < TaxAccounts.Accounts; i++) rates[i] = TaxAccounts.IsResidential(i) ? residential : business;
        return rates;
    }

    private static long[] Bases(long residential, long business)
    {
        long[] bases = new long[TaxAccounts.Accounts];
        for (int i = 0; i < TaxAccounts.Accounts; i++) bases[i] = TaxAccounts.IsResidential(i) ? residential : business;
        return bases;
    }

    private static long[] Collected(long amount)
    {
        long[] collected = new long[TaxAccounts.Accounts];
        for (int i = 0; i < TaxAccounts.Accounts; i++) collected[i] = amount;
        return collected;
    }

    private static int Main()
    {
        try
        {
            AccountKeys();
            HistoryWindow();
            InputValidation();
            Neutrality();
            RateIncreaseAndDecrease();
            ReactionDelay();
            NoCollection();
            CashOnlyFromObservedCollection();
            ModifierIsNotMoney();
            CurveValidation();
            CodecRoundtrip();
            CodecRejections();
            RestoreContinuesWindow();
            AdapterOptIn();
            Determinism();
            Console.WriteLine("PASS: " + _checks + " tax accounting assertions; pure model + real adapter source, no native execution.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); return 1; }
    }

    private static void AccountKeys()
    {
        Equal(12, TaxAccounts.Accounts, "twelve accounts");
        Equal(3, TaxAccounts.ResidentialAccounts, "three residential accounts");
        Equal(9, TaxAccounts.BusinessAccounts, "nine business accounts");
        Equal(0, TaxAccounts.Residential(0), "R$ key");
        Equal(2, TaxAccounts.Residential(2), "R$$$ key");
        Equal(3, TaxAccounts.Business(0), "CS$ key");
        Equal(11, TaxAccounts.Business(8), "IHT key");
        Equal("R$", TaxAccounts.Name(0), "R$ name");
        Equal("R$$$", TaxAccounts.Name(2), "R$$$ name");
        Equal("CO$$$", TaxAccounts.Name(7), "CO$$$ name");
        Equal("IHT", TaxAccounts.Name(11), "IHT name");
        True(TaxAccounts.IsResidential(1), "R$$ is residential");
        True(!TaxAccounts.IsResidential(3), "CS$ is business");
        Throws<ArgumentOutOfRangeException>(delegate { TaxAccounts.Residential(3); }, "residential key above range rejected");
        Throws<ArgumentOutOfRangeException>(delegate { TaxAccounts.Business(9); }, "business key above range rejected");
        Throws<ArgumentOutOfRangeException>(delegate { TaxAccounts.Name(12); }, "account name above range rejected");
        Throws<ArgumentNullException>(delegate { TaxAccounts.ValidateVector<int>(null, "x"); }, "null vector rejected");
        Throws<ArgumentException>(delegate { TaxAccounts.ValidateVector(new int[11], "x"); }, "short vector rejected");
        TaxRateValidation.Validate(0);
        TaxRateValidation.Validate(100);
        Throws<ArgumentOutOfRangeException>(delegate { TaxRateValidation.Validate(101); }, "rate above 100 rejected");
        Throws<ArgumentOutOfRangeException>(delegate { TaxRateValidation.Validate(-1); }, "negative rate rejected");
    }

    private static void HistoryWindow()
    {
        TaxReactionHistory history = new TaxReactionHistory(90, 366);
        Throws<ArgumentOutOfRangeException>(delegate { new TaxReactionHistory(0, 10); }, "zero window rejected");
        Throws<ArgumentOutOfRangeException>(delegate { new TaxReactionHistory(10, 0); }, "zero capacity rejected");
        history.Record(10, 9);
        history.Record(20, 20);
        history.Record(30, 12);
        Equal(20, history.EffectiveRate(30), "window keeps the maximum");
        Throws<ArgumentException>(delegate { history.Record(30, 5); }, "duplicate day rejected");
        Throws<ArgumentException>(delegate { history.Record(25, 5); }, "out-of-order day rejected");
        Throws<ArgumentOutOfRangeException>(delegate { history.EffectiveRate(-1); }, "negative day rejected");
        // Inclusive window: the high from day 20 stays visible through day 109 (20+90-1),
        // then leaves at 110.
        Equal(20, history.EffectiveRate(109), "last day of the inclusive window");
        Equal(12, history.EffectiveRate(110), "high leaves after the window");
        // Empty window: no reaction memory.
        TaxReactionHistory empty = new TaxReactionHistory(90, 366);
        Equal(0, empty.EffectiveRate(50), "empty history has no effective rate");
        // Capacity trims the oldest entries: after three records only the last two remain.
        TaxReactionHistory tiny = new TaxReactionHistory(365, 2);
        tiny.Record(1, 5); tiny.Record(2, 6); tiny.Record(3, 7);
        Equal(2, tiny.Count, "capacity trims the oldest entry");
        Equal(7, tiny.EffectiveRate(2), "trimmed history keeps the recent maximum");
        Throws<ArgumentException>(delegate { tiny.Record(3, 7); }, "record still validates after trim");
    }

    private static long BaseOf(TaxAccountingReport report, int account) { return report.BaseIncomeOf(account); }

    private static void InputValidation()
    {
        int[] rates = Rates(10, 9);
        long[] bases = Bases(1000, 500);
        long[] collected = Collected(50);
        TaxAccountingInput input = new TaxAccountingInput(5, rates, bases, collected);
        rates[0] = 99; bases[0] = 99; collected[0] = 99;
        Equal(10, input.NominalRatePercent[0], "input clones rates");
        Equal(1000, input.BaseIncome[0], "input clones bases");
        Equal(50, input.CollectedActual[0], "input clones collected");
        Equal(5, input.Day, "input day");
        int[] badRate = Rates(10, 9); badRate[4] = 101;
        Throws<ArgumentOutOfRangeException>(delegate { new TaxAccountingInput(0, badRate, bases, collected); }, "rate above range rejected");
        long[] negativeBase = Bases(1000, 500); negativeBase[2] = -1;
        Throws<ArgumentException>(delegate { new TaxAccountingInput(0, rates, negativeBase, collected); }, "negative base rejected");
        long[] negativeCollected = Collected(50); negativeCollected[7] = -1;
        Throws<ArgumentException>(delegate { new TaxAccountingInput(0, rates, bases, negativeCollected); }, "negative collection rejected");
        Throws<ArgumentNullException>(delegate { new TaxAccountingInput(0, null, bases, collected); }, "null rates rejected");
        Throws<ArgumentOutOfRangeException>(delegate { new TaxAccountingInput(-1, rates, bases, collected); }, "negative day rejected");
        Throws<ArgumentNullException>(delegate { new TaxAccountingModel(null); }, "null curve rejected");
        TaxAccountingModel model = new TaxAccountingModel(new SyntheticCurve());
        Throws<ArgumentNullException>(delegate { model.Apply(null); }, "null input rejected");
    }

    private static void Neutrality()
    {
        // Neutral pivot rates: modifier exactly 1, collectible equals base * rate, cash
        // equals observed collection, delta records the difference.
        TaxAccountingModel model = new TaxAccountingModel(new SyntheticCurve());
        TaxAccountingReport report = model.Apply(new TaxAccountingInput(1, Rates(10, 8), Bases(1000, 500), Collected(100)));
        for (int account = 0; account < TaxAccounts.Accounts; account++)
        {
            long baseIncome = report.BaseIncomeOf(account);
            long expectedCollectible = baseIncome * report.NominalRatePercent[account] / 100L;
            Equal(1f, report.DemandModifier[account], "neutral modifier " + TaxAccounts.Name(account));
            Equal(expectedCollectible, report.CollectibleIncome[account], "neutral collectible " + TaxAccounts.Name(account));
            Equal(100, report.CollectedActual[account], "observed collection " + TaxAccounts.Name(account));
            Equal(expectedCollectible - 100, report.ReconciliationDelta[account], "neutral delta " + TaxAccounts.Name(account));
            Equal(100, report.CashByAccount[account], "cash from observation only " + TaxAccounts.Name(account));
        }
    }

    private static void RateIncreaseAndDecrease()
    {
        TaxAccountingModel model = new TaxAccountingModel(new SyntheticCurve());
        // Increase: R$ from 10 to 20 -> modifier 0.8 immediately (max in window).
        model.Apply(new TaxAccountingInput(1, Rates(10, 8), Bases(1000, 500), Collected(100)));
        TaxAccountingReport raised = model.Apply(new TaxAccountingInput(2, Rates(20, 8), Bases(1000, 500), Collected(200)));
        Equal(20, raised.EffectiveRatePercent[0], "raise applies immediately");
        Equal(0.8f, raised.DemandModifier[0], "residential raise suppresses demand");
        // Business raise 8 -> 18: 1 - 0.03*10 = 0.7.
        TaxAccountingReport business = model.Apply(new TaxAccountingInput(3, Rates(20, 18), Bases(1000, 500), Collected(200)));
        Equal(0.7f, business.DemandModifier[3], "business raise suppresses demand");
        // Collectible follows the CURRENT nominal rate, not the window.
        Equal(500 * 18 / 100, business.CollectibleIncome[3], "collectible uses current rate");
    }

    private static void ReactionDelay()
    {
        TaxAccountingModel model = new TaxAccountingModel(new SyntheticCurve());
        int window = 30;
        model = new TaxAccountingModel(new SyntheticCurve(), window, 366);
        model.Apply(new TaxAccountingInput(1, Rates(20, 18), Bases(1000, 500), Collected(100)));
        // Cut back to 10 on day 2: the meter must stay suppressed while 20/18 remain in
        // the inclusive window (through day 30 for day-1 samples).
        TaxAccountingReport cut = model.Apply(new TaxAccountingInput(2, Rates(10, 8), Bases(1000, 500), Collected(100)));
        Equal(20, cut.EffectiveRatePercent[0], "cut does not react immediately");
        Equal(0.8f, cut.DemandModifier[0], "modifier keeps the window maximum after a cut");
        Equal(1000 * 10 / 100, cut.CollectibleIncome[0], "collectible follows the cut at once");
        TaxAccountingReport after = model.Apply(new TaxAccountingInput(31, Rates(10, 8), Bases(1000, 500), Collected(100)));
        Equal(10, after.EffectiveRatePercent[0], "old high left the window");
        Equal(1f, after.DemandModifier[0], "modifier recovers after the delay");
    }

    private static void NoCollection()
    {
        TaxAccountingModel model = new TaxAccountingModel(new SyntheticCurve());
        TaxAccountingReport report = model.Apply(new TaxAccountingInput(1, Rates(10, 8), Bases(1000, 500), Collected(0)));
        for (int account = 0; account < TaxAccounts.Accounts; account++)
        {
            Equal(0, report.CollectedActual[account], "no native collection observed");
            Equal(0, report.CashByAccount[account], "cash stays zero without collection");
            Equal(report.CollectibleIncome[account], report.ReconciliationDelta[account], "delta exposes the full shortfall");
        }
        // A later period with collection still grows cash from observations only.
        TaxAccountingReport later = model.Apply(new TaxAccountingInput(2, Rates(10, 8), Bases(1000, 500), Collected(90)));
        Equal(90, later.CashByAccount[0], "cash accumulates observed collections");
    }

    private static void CashOnlyFromObservedCollection()
    {
        TaxAccountingModel model = new TaxAccountingModel(new SyntheticCurve());
        long[] small = Collected(0); small[0] = 40;
        TaxAccountingReport first = model.Apply(new TaxAccountingInput(1, Rates(10, 8), Bases(1000, 500), small));
        // Collectible for R$ is 100; the native city only collected 40. Cash is 40 — the
        // model refuses to top the treasury up to its own expectation.
        Equal(40, first.CashByAccount[0], "cash never minted from collectible");
        Equal(60, first.ReconciliationDelta[0], "shortfall recorded as delta");
        long[] big = Collected(0); big[0] = 300;
        TaxAccountingReport second = model.Apply(new TaxAccountingInput(2, Rates(10, 8), Bases(1000, 500), big));
        Equal(340, second.CashByAccount[0], "cash sums observations");
        Equal(100 - 300, second.ReconciliationDelta[0], "over-collection recorded as negative delta");
        Equal(300, second.TotalCollected, "total collected covers this period's observations");
    }

    private static void ModifierIsNotMoney()
    {
        TaxAccountingModel model = new TaxAccountingModel(new SyntheticCurve());
        // Hostile modifier (0) with full base and full collection: cash unchanged by meters.
        TaxAccountingReport report = model.Apply(new TaxAccountingInput(1, Rates(60, 60), Bases(100000, 100000), Collected(123)));
        for (int account = 0; account < TaxAccounts.Accounts; account++)
            True(report.DemandModifier[account] <= 1f, "modifier bounded " + TaxAccounts.Name(account));
        Equal(123, report.CashByAccount[0], "cash ignores the demand meter");
        Equal(100000 * 60 / 100, report.CollectibleIncome[0], "collectible is nominal arithmetic");
    }

    private static void CurveValidation()
    {
        TaxAccountingModel model = new TaxAccountingModel(new SyntheticCurve());
        int[] rates = Rates(10, 8);
        // A curve returning NaN must stop the model rather than corrupt accounting.
        TaxAccountingModel broken = new TaxAccountingModel(new ThrowingCurve());
        Throws<InvalidOperationException>(delegate { broken.Apply(new TaxAccountingInput(1, rates, Bases(1, 1), Collected(0))); }, "invalid curve factor rejected");
    }

    private sealed class ThrowingCurve : ITaxResponseCurve
    {
        public float DemandFactor(int account, int effectiveRatePercent) { return float.NaN; }
    }

    private static void CodecRoundtrip()
    {
        TaxAccountingModel model = new TaxAccountingModel(new SyntheticCurve());
        model.Apply(new TaxAccountingInput(5, Rates(10, 8), Bases(100, 100), Collected(1)));
        model.Apply(new TaxAccountingInput(6, Rates(15, 12), Bases(100, 100), Collected(2)));
        byte[] saved = model.Save();
        TaxAccountingModel restored = TaxAccountingModel.Restore(new SyntheticCurve(), TaxAccountingModel.DefaultWindowDays, TaxAccountingModel.DefaultMaxEntries, saved);
        Equal(3, model.Cash(0), "live model keeps runtime cash");
        Equal(0, restored.Cash(0), "cash is runtime-only and resets on restore (the native treasury owns money)");
        EqualBytes(saved, restored.Save(), "re-save is byte-identical before any new sample");
        TaxAccountingReport live = model.Apply(new TaxAccountingInput(7, Rates(15, 12), Bases(100, 100), Collected(3)));
        TaxAccountingReport fromRestored = restored.Apply(new TaxAccountingInput(7, Rates(15, 12), Bases(100, 100), Collected(3)));
        for (int account = 0; account < TaxAccounts.Accounts; account++)
        {
            Equal(live.EffectiveRatePercent[account], fromRestored.EffectiveRatePercent[account], "effective rate survives restore " + TaxAccounts.Name(account));
            Equal(live.DemandModifier[account], fromRestored.DemandModifier[account], "modifier survives restore " + TaxAccounts.Name(account));
        }
        // The delay semantics survive restore: cut now, recover only after the window.
        int window = 20;
        TaxAccountingModel shortModel = new TaxAccountingModel(new SyntheticCurve(), window, 366);
        shortModel.Apply(new TaxAccountingInput(1, Rates(20, 18), Bases(10, 10), Collected(0)));
        TaxAccountingModel shortRestored = TaxAccountingModel.Restore(new SyntheticCurve(), window, 366, shortModel.Save());
        shortModel.Apply(new TaxAccountingInput(2, Rates(10, 8), Bases(10, 10), Collected(0)));
        shortRestored.Apply(new TaxAccountingInput(2, Rates(10, 8), Bases(10, 10), Collected(0)));
        Equal(shortModel.Apply(new TaxAccountingInput(21, Rates(10, 8), Bases(10, 10), Collected(0))).DemandModifier[0],
            shortRestored.Apply(new TaxAccountingInput(21, Rates(10, 8), Bases(10, 10), Collected(0))).DemandModifier[0], "window expiry identical after restore");
    }

    private static void CodecRejections()
    {
        TaxAccountingModel model = new TaxAccountingModel(new SyntheticCurve());
        model.Apply(new TaxAccountingInput(1, Rates(10, 8), Bases(10, 10), Collected(0)));
        byte[] saved = model.Save();
        byte[] truncated = new byte[saved.Length - 1];
        Buffer.BlockCopy(saved, 0, truncated, 0, truncated.Length);
        Throws<InvalidDataException>(delegate { TaxAccountingModel.Restore(new SyntheticCurve(), 90, 366, truncated); }, "truncated history rejected");
        byte[] corrupted = (byte[])saved.Clone();
        corrupted[corrupted.Length - 3] ^= 0xFF;
        Throws<InvalidDataException>(delegate { TaxAccountingModel.Restore(new SyntheticCurve(), 90, 366, corrupted); }, "corrupted history rejected");
        byte[] trailing = new byte[saved.Length + 2];
        Buffer.BlockCopy(saved, 0, trailing, 0, saved.Length);
        Throws<InvalidDataException>(delegate { TaxAccountingModel.Restore(new SyntheticCurve(), 90, 366, trailing); }, "trailing bytes rejected");
        Throws<ArgumentNullException>(delegate { TaxAccountingModel.Restore(new SyntheticCurve(), 90, 366, null); }, "null history rejected");
    }

    private static void RestoreContinuesWindow()
    {
        // Full trajectory equality between a live and a repeatedly restored model.
        TaxAccountingModel live = new TaxAccountingModel(new SyntheticCurve(), 40, 366);
        TaxAccountingModel restored = new TaxAccountingModel(new SyntheticCurve(), 40, 366);
        for (int day = 1; day <= 50; day++)
        {
            int rate = day < 10 ? 9 : (day < 30 ? 21 : 10);
            TaxAccountingReport a = live.Apply(new TaxAccountingInput(day, Rates(rate, rate), Bases(100, 100), Collected(5)));
            restored = TaxAccountingModel.Restore(new SyntheticCurve(), 40, 366, restored.Save());
            TaxAccountingReport b = restored.Apply(new TaxAccountingInput(day, Rates(rate, rate), Bases(100, 100), Collected(5)));
            for (int account = 0; account < TaxAccounts.Accounts; account++)
            {
                Equal(a.EffectiveRatePercent[account], b.EffectiveRatePercent[account], "trajectory effective rate day " + day);
                Equal(a.DemandModifier[account], b.DemandModifier[account], "trajectory modifier day " + day);
            }
        }
    }

    private static void AdapterOptIn()
    {
        True(TaxAccountingSession.TryCreate(false, new SyntheticCurve(), 90, 366) == null, "disabled session is null");
        True(TaxAccountingSession.TryRestore(false, new SyntheticCurve(), 90, 366, new byte[8]) == null, "disabled restore is null");
        TaxAccountingSession session = TaxAccountingSession.TryCreate(true, new SyntheticCurve(), 90, 366);
        True(session != null, "enabled session exists");
        TaxAccountingReport report = session.Apply(new TaxAccountingInput(3, Rates(12, 9), Bases(1000, 500), Collected(60)));
        Equal(3, report.Day, "adapter day passthrough");
        Equal(60 * TaxAccounts.Accounts, report.TotalCollected, "adapter totals collection");
        True(session.Save().Length > 0, "session persists");
        TaxAccountingSession restored = TaxAccountingSession.TryRestore(true, new SyntheticCurve(), 90, 366, session.Save());
        True(restored != null, "session restores");
        Throws<ArgumentNullException>(delegate { TaxAccountingSession.TryCreate(true, null, 90, 366); }, "adapter rejects null curve");
        Throws<ArgumentNullException>(delegate { session.Apply(null); }, "adapter rejects null input");
    }

    private static void Determinism()
    {
        TaxAccountingModel first = new TaxAccountingModel(new SyntheticCurve(), 25, 366);
        TaxAccountingModel second = new TaxAccountingModel(new SyntheticCurve(), 25, 366);
        for (int day = 1; day <= 40; day++)
        {
            int rate = (day * 7) % 30;
            TaxAccountingReport a = first.Apply(new TaxAccountingInput(day, Rates(rate, rate + 2), Bases(100 + day, 200), Collected(day)));
            TaxAccountingReport b = second.Apply(new TaxAccountingInput(day, Rates(rate, rate + 2), Bases(100 + day, 200), Collected(day)));
            for (int account = 0; account < TaxAccounts.Accounts; account++)
            {
                Equal(a.EffectiveRatePercent[account], b.EffectiveRatePercent[account], "deterministic effective rate");
                Equal(a.CollectibleIncome[account], b.CollectibleIncome[account], "deterministic collectible");
                Equal(a.CashByAccount[account], b.CashByAccount[account], "deterministic cash");
            }
        }
    }

    private static void Equal(int expected, int actual, string label)
    {
        _checks++;
        if (expected != actual) throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual + ".");
    }

    private static void Equal(long expected, long actual, string label)
    {
        _checks++;
        if (expected != actual) throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual + ".");
    }

    private static void Equal(float expected, float actual, string label)
    {
        _checks++;
        if (Math.Abs(expected - actual) > 0.0001f) throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual + ".");
    }

    private static void Equal(string expected, string actual, string label)
    {
        _checks++;
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException(label + ": expected '" + expected + "', got '" + actual + "'.");
    }

    private static void EqualBytes(byte[] expected, byte[] actual, string label)
    {
        _checks++;
        if (expected.Length != actual.Length) throw new InvalidOperationException(label + ": length differs.");
        for (int i = 0; i < expected.Length; i++)
            if (expected[i] != actual[i]) throw new InvalidOperationException(label + ": byte " + i + " differs.");
    }

    private static void True(bool condition, string label)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(label + ": condition false.");
    }

    private static void Throws<TException>(Action action, string label) where TException : Exception
    {
        _checks++;
        try { action(); }
        catch (TException) { return; }
        catch (Exception error)
        {
            throw new InvalidOperationException(label + ": wrong exception type " + error.GetType().Name + ".");
        }
        throw new InvalidOperationException(label + ": no exception thrown.");
    }
}
