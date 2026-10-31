using System;
using System.Collections.Generic;
using System.IO;
using SC4Evolution.Core;
using SC4Evolution.Core.Decisions;

internal static class Program
{
    private static int _checks;

    private static AdvisorRecommendation Recommendation(AdvisorId advisor, DecisionConsequence consequence,
        string action, string indicator, double value, double expected, string target)
    {
        return AdvisorRecommendation.Create(advisor, advisor.ToString(), consequence, action, indicator, value, expected, target);
    }

    private static int Main()
    {
        try
        {
            EventLifecycle();
            CooldownSuppression();
            DurationExpiry();
            RecoveryByDisappearance();
            Conflicts();
            NoticeResolution();
            StaleNotices();
            CodecRoundtrip();
            CodecRejections();
            RestoreKeepsCooldowns();
            Validation();
            Determinism();
            Console.WriteLine("PASS: " + _checks + " decision-event assertions; pure Core extension, no native execution, no UI buffers read.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); return 1; }
    }

    private static void EventLifecycle()
    {
        DecisionEngine engine = new DecisionEngine(100, 10);
        DecisionEvent created = engine.Observe(1, "unemployment:W$EQ1", true,
            Recommendation(AdvisorId.UrbanPlanning, DecisionConsequence.Unemployment, "Zone CS$/ID near EQ1 housing", "Unemployed W$ EQ1", 120, 20, ""), DecisionUrgency.Urgent);
        True(created != null, "active condition creates an event");
        Equal(0L, created.Id, "first event id");
        Equal(101u, created.ExpiresAtFrame, "duration applied");
        Equal(DecisionUrgency.Urgent, created.Urgency, "urgency kept");
        Equal(DecisionConsequence.Unemployment, created.Recommendation.Consequence, "cause kept");
        Equal(1, engine.ActiveCount, "engine tracks one event");
        // Same frame re-observe: suppressed by cooldown.
        True(engine.Observe(1, "unemployment:W$EQ1", true, created.Recommendation, DecisionUrgency.Urgent) == null, "same-frame re-alert suppressed");
    }

    private static void CooldownSuppression()
    {
        DecisionEngine engine = new DecisionEngine(1000, 30);
        AdvisorRecommendation recommendation = Recommendation(AdvisorId.Finance, DecisionConsequence.TaxRevenueLoss, "Lower R$ rate", "Delta", -50, 0, "");
        True(engine.Observe(10, "tax:R$", true, recommendation, DecisionUrgency.Attention) != null, "first alert fires");
        for (uint frame = 11; frame < 40; frame++)
            True(engine.Observe(frame, "tax:R$", true, recommendation, DecisionUrgency.Attention) == null,
                "cooldown suppresses re-alert at frame " + frame);
        DecisionEvent reAlert = engine.Observe(40, "tax:R$", true, recommendation, DecisionUrgency.Attention);
        True(reAlert != null, "re-alert allowed after cooldown");
        Equal(40u, reAlert.LastAlertFrame, "re-alert frame recorded");
        // Updating the recommendation inside the cooldown does not re-alert, but evidence
        // stays current for anyone reading the event.
        AdvisorRecommendation updated = Recommendation(AdvisorId.Finance, DecisionConsequence.TaxRevenueLoss, "Lower R$ rate", "Delta", -80, 0, "");
        True(engine.Observe(41, "tax:R$", true, updated, DecisionUrgency.Attention) == null, "cooldown still suppresses");
        True(engine.Observe(42, "tax:R$", true, updated, DecisionUrgency.Attention) == null, "suppressed again");
    }

    private static void DurationExpiry()
    {
        DecisionEngine engine = new DecisionEngine(50, 1000);
        AdvisorRecommendation recommendation = Recommendation(AdvisorId.EducationHealth, DecisionConsequence.ServiceUnderfunding, "Fund the clinic", "Coverage", 0.2, 0.6, "bld:77");
        engine.Observe(0, "civic:bld:77", true, recommendation, DecisionUrgency.Critical);
        engine.Observe(10, "civic:bld:77", true, recommendation, DecisionUrgency.Critical);
        engine.Expire(60);
        Equal(0, engine.ActiveCount, "unobserved event expires with its duration");
        // The same cause observed again after expiry is a fresh event.
        DecisionEvent fresh = engine.Observe(70, "civic:bld:77", true, recommendation, DecisionUrgency.Critical);
        True(fresh != null, "post-expiry observation creates a new event");
        Equal(1L, fresh.Id, "new event gets a new id");
        // Duration elapsed while the condition persists: refresh instead of dropping.
        DecisionEngine persistent = new DecisionEngine(20, 1000);
        persistent.Observe(0, "civic:bld:78", true, recommendation, DecisionUrgency.Critical);
        DecisionEvent refreshed = persistent.Observe(25, "civic:bld:78", true, recommendation, DecisionUrgency.Critical);
        True(refreshed != null, "persisting condition refreshes its window");
        Equal(45u, refreshed.ExpiresAtFrame, "refreshed expiry");
    }

    private static void RecoveryByDisappearance()
    {
        DecisionEngine engine = new DecisionEngine(1000, 10);
        AdvisorRecommendation recommendation = Recommendation(AdvisorId.Environment, DecisionConsequence.FreightGridlock, "Add freight route", "Stock", 90, 10, "bld:9");
        engine.Observe(5, "freight:bld:9", true, recommendation, DecisionUrgency.Urgent);
        Equal(1, engine.ActiveCount, "event active");
        engine.Observe(6, "freight:bld:9", false, recommendation, DecisionUrgency.Urgent);
        Equal(0, engine.ActiveCount, "condition ending removes the event (recovery)");
        // Recurrence is a new event with a new id.
        DecisionEvent again = engine.Observe(7, "freight:bld:9", true, recommendation, DecisionUrgency.Urgent);
        True(again != null, "recurrence fires again");
        Equal(1L, again.Id, "recurrence id differs from the first event (0)");
    }

    private static void Conflicts()
    {
        DecisionEvent a = DecisionEvent.Detached(
            Recommendation(AdvisorId.Finance, DecisionConsequence.TaxRevenueLoss, "Raise business tax", "Cash", 0, 500, "seg:CO$$$"), DecisionUrgency.Urgent);
        DecisionEvent b = DecisionEvent.Detached(
            Recommendation(AdvisorId.UrbanPlanning, DecisionConsequence.DemandSuppression, "Cut business tax", "Demand", 0.2, 0.6, "seg:CO$$$"), DecisionUrgency.Attention);
        DecisionEvent c = DecisionEvent.Detached(
            Recommendation(AdvisorId.Environment, DecisionConsequence.NeighborhoodDecline, "Clean the district", "Attractiveness", 30, 60, "ngh:4"), DecisionUrgency.Info);
        List<DecisionEvent> events = new List<DecisionEvent> { a, b, c };
        int conflicts = DecisionEngine.MarkConflicts(events);
        Equal(1, conflicts, "one conflicting pair detected");
        True(a.Recommendation.HasOpportunityCost, "finance recommendation marked");
        True(b.Recommendation.HasOpportunityCost, "planning recommendation marked");
        True(!c.Recommendation.HasOpportunityCost, "unrelated recommendation unmarked");
        // Same advisor or same consequence never conflicts.
        DecisionEvent sameAdvisor = DecisionEvent.Detached(
            Recommendation(AdvisorId.Finance, DecisionConsequence.DemandSuppression, "Other", "X", 1, 2, "seg:CO$$$"), DecisionUrgency.Attention);
        List<DecisionEvent> noConflict = new List<DecisionEvent> { a, sameAdvisor };
        Equal(0, DecisionEngine.MarkConflicts(noConflict), "same advisor never conflicts");
        Throws<ArgumentNullException>(delegate { DecisionEngine.MarkConflicts(null); }, "null event list rejected");
    }

    private static void NoticeResolution()
    {
        DecisionEngine engine = new DecisionEngine(100, 10);
        DecisionEvent decision = engine.Observe(5, "unemployment:W$EQ1", true,
            Recommendation(AdvisorId.UrbanPlanning, DecisionConsequence.Unemployment, "Zone workplaces for EQ1", "Unemployed W$ EQ1", 120, 20, "ngh:12"), DecisionUrgency.Urgent);
        DecisionNotice notice = DecisionEngine.BuildNotice(decision, 5, "Laura Dean", "Action needed: {0}", "Advisor asks: {0}");
        Equal(0L, notice.EventId, "notice carries the event id");
        Equal(AdvisorId.UrbanPlanning, notice.Advisor, "notice advisor");
        Equal("Laura Dean", notice.AdvisorName, "notice advisor name");
        Equal("Action needed: Zone workplaces for EQ1", notice.Title, "notice title built");
        True(notice.Message.Contains("Zone workplaces for EQ1"), "notice message contains the action");
        True(notice.IndicatorText.Contains("Unemployed W$ EQ1"), "notice shows the indicator");
        True(notice.IndicatorText.Contains("120") && notice.IndicatorText.Contains("20"), "notice shows current and expected values");
        Equal("ngh:12", notice.TargetText, "notice shows the affected target when identity is valid");
        Equal(5u, notice.ShownAtFrame, "notice frame");
        // City-wide requests carry an empty target.
        DecisionEvent citywide = engine.Observe(6, "tax:R$", true,
            Recommendation(AdvisorId.Finance, DecisionConsequence.TaxRevenueLoss, "Lower R$ rate", "Delta", -50, 0, ""), DecisionUrgency.Attention);
        DecisionNotice wide = DecisionEngine.BuildNotice(citywide, 6, "Laura Dean", "{0}", "{0}");
        Equal("", wide.TargetText, "city-wide notice has no target");
        Throws<ArgumentNullException>(delegate { DecisionEngine.BuildNotice(null, 1, "x", "{0}", "{0}"); }, "null decision rejected");
    }

    private static void StaleNotices()
    {
        DecisionEngine engine = new DecisionEngine(1000, 10);
        DecisionEvent decision = engine.Observe(1, "tax:R$", true,
            Recommendation(AdvisorId.Finance, DecisionConsequence.TaxRevenueLoss, "Lower R$ rate", "Delta", -50, 0, ""), DecisionUrgency.Attention);
        DecisionNotice notice = DecisionEngine.BuildNotice(decision, 1, "Laura Dean", "{0}", "{0}");
        True(!DecisionEngine.IsStale(notice, decision), "fresh notice accepted");
        engine.Observe(11, "tax:R$", true, decision.Recommendation, DecisionUrgency.Attention); // re-alert at 11
        True(DecisionEngine.IsStale(notice, decision), "notice snapshot predating the last alert is stale");
        True(DecisionEngine.IsStale(notice, null), "notice without a live event is stale");
        DecisionEvent other = engine.Observe(12, "tax:R$$", true,
            Recommendation(AdvisorId.Finance, DecisionConsequence.TaxRevenueLoss, "Lower R$$ rate", "Delta", -50, 0, ""), DecisionUrgency.Attention);
        True(DecisionEngine.IsStale(notice, other), "notice of another event id is stale");
    }

    private static void CodecRoundtrip()
    {
        DecisionEngine engine = new DecisionEngine(500, 20);
        engine.Observe(1, "unemployment:W$EQ1", true,
            Recommendation(AdvisorId.UrbanPlanning, DecisionConsequence.Unemployment, "Zone workplaces", "Unemployed", 120, 20, "ngh:12"), DecisionUrgency.Urgent);
        engine.Observe(2, "tax:R$", true,
            Recommendation(AdvisorId.Finance, DecisionConsequence.TaxRevenueLoss, "Lower R$ rate", "Delta", -50, 0, ""), DecisionUrgency.Attention);
        byte[] saved = engine.Save();
        DecisionEngine restored = DecisionEngine.Restore(500, 20, saved);
        Equal(2, restored.ActiveCount, "both events survive restore");
        EqualBytes(saved, restored.Save(), "re-save is byte-identical");
    }

    private static void CodecRejections()
    {
        DecisionEngine engine = new DecisionEngine(100, 10);
        engine.Observe(1, "tax:R$", true,
            Recommendation(AdvisorId.Finance, DecisionConsequence.TaxRevenueLoss, "Lower R$ rate", "Delta", -50, 0, ""), DecisionUrgency.Attention);
        byte[] saved = engine.Save();
        byte[] truncated = new byte[saved.Length - 1];
        Buffer.BlockCopy(saved, 0, truncated, 0, truncated.Length);
        Throws<InvalidDataException>(delegate { DecisionEngine.Restore(100, 10, truncated); }, "truncated state rejected");
        byte[] corrupted = (byte[])saved.Clone();
        corrupted[corrupted.Length - 2] ^= 0xFF;
        Throws<InvalidDataException>(delegate { DecisionEngine.Restore(100, 10, corrupted); }, "corrupted state rejected");
        byte[] trailing = new byte[saved.Length + 1];
        Buffer.BlockCopy(saved, 0, trailing, 0, saved.Length);
        Throws<InvalidDataException>(delegate { DecisionEngine.Restore(100, 10, trailing); }, "trailing bytes rejected");
        Throws<ArgumentNullException>(delegate { DecisionEngine.Restore(100, 10, null); }, "null state rejected");
    }

    private static void RestoreKeepsCooldowns()
    {
        // A reloaded cabinet must not re-alert a cause immediately.
        DecisionEngine live = new DecisionEngine(1000, 30);
        AdvisorRecommendation recommendation = Recommendation(AdvisorId.Utilities, DecisionConsequence.JobAccessLoss, "Extend transit", "Access", 0.1, 0.5, "ngh:3");
        live.Observe(10, "access:ngh:3", true, recommendation, DecisionUrgency.Urgent);
        DecisionEngine restored = DecisionEngine.Restore(1000, 30, live.Save());
        True(restored.Observe(11, "access:ngh:3", true, recommendation, DecisionUrgency.Urgent) == null,
            "restored engine keeps the cooldown memory");
        True(restored.Observe(41, "access:ngh:3", true, recommendation, DecisionUrgency.Urgent) != null,
            "cooldown still expires after restore");
    }

    private static void Validation()
    {
        DecisionEngine engine = new DecisionEngine(100, 10);
        Throws<ArgumentOutOfRangeException>(delegate { new DecisionEngine(0, 10); }, "zero duration rejected");
        AdvisorRecommendation recommendation = Recommendation(AdvisorId.Finance, DecisionConsequence.TaxRevenueLoss, "Act", "X", 1, 0, "");
        Throws<ArgumentNullException>(delegate { engine.Observe(1, null, true, recommendation, DecisionUrgency.Info); }, "null cause key rejected");
        Throws<ArgumentNullException>(delegate { engine.Observe(1, "k", true, null, DecisionUrgency.Info); }, "null recommendation rejected");
    }

    private static void Determinism()
    {
        DecisionEngine first = new DecisionEngine(100, 10);
        DecisionEngine second = new DecisionEngine(100, 10);
        AdvisorRecommendation recommendation = Recommendation(AdvisorId.Transport, DecisionConsequence.FreightGridlock, "Fix freight", "Stock", 80, 10, "bld:5");
        for (uint frame = 1; frame <= 30; frame++)
        {
            bool active = frame % 7 != 0; // periodic condition
            DecisionEvent a = first.Observe(frame, "freight:bld:5", active, recommendation, DecisionUrgency.Attention);
            DecisionEvent b = second.Observe(frame, "freight:bld:5", active, recommendation, DecisionUrgency.Attention);
            Equal(a == null, b == null, "identical alert decisions at frame " + frame);
            if (a != null) Equal(b.Id, a.Id, "identical event ids at frame " + frame);
            first.Expire(frame);
            second.Expire(frame);
            Equal(first.ActiveCount, second.ActiveCount, "identical active counts at frame " + frame);
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

    private static void Equal(uint expected, uint actual, string label)
    {
        _checks++;
        if (expected != actual) throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual + ".");
    }

    private static void Equal(string expected, string actual, string label)
    {
        _checks++;
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException(label + ": expected '" + expected + "', got '" + actual + "'.");
    }

    private static void Equal(DecisionUrgency expected, DecisionUrgency actual, string label)
    {
        _checks++;
        if (expected != actual) throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual + ".");
    }

    private static void Equal(DecisionConsequence expected, DecisionConsequence actual, string label)
    {
        _checks++;
        if (expected != actual) throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual + ".");
    }

    private static void Equal(AdvisorId expected, AdvisorId actual, string label)
    {
        _checks++;
        if (expected != actual) throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual + ".");
    }

    private static void Equal(bool expected, bool actual, string label)
    {
        _checks++;
        if (expected != actual) throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual + ".");
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
