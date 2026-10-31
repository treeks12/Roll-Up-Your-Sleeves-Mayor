using System;
using System.Collections.Generic;
using System.IO;
using SC4Evolution.Mod.Economy;
using SC4Evolution.Simulation;
using SC4Evolution.Simulation.Neighborhoods;

internal static class Program
{
    private static int _checks;

    private static int Main()
    {
        try
        {
            PhaseDistinctions();
            AttractivenessMath();
            ThresholdValidation();
            ConstructionCompletion();
            MaintenanceGrace();
            AbandonmentCycle();
            ReoccupationConditions();
            RehabilitationConditions();
            ObservedTransitions();
            ReplacementKeepsLevelSeparate();
            GenerationReset();
            ReportIntegrity();
            CodecRoundtrip();
            CodecRejections();
            RestoreContinuesTrajectory();
            AdapterOptIn();
            NativeLevelUpPolicy();
            Determinism();
            Console.WriteLine("PASS: " + _checks + " neighborhood assertions; pure cycle model + real adapter source, no native execution.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); return 1; }
    }

    private static NeighborhoodFactors Factors(float value)
    {
        NeighborhoodFactors factors = new NeighborhoodFactors();
        factors.AccessibleEmployment = value; factors.Services = value; factors.Taxes = value;
        factors.Pollution = value; factors.Traffic = value; factors.LandValue = value;
        return factors;
    }

    private static NeighborhoodLotInput Lot(ushort id, uint buildIndex, int stockClass,
        ResidentialStockPhase phase, bool residents, float factorValue)
    {
        NeighborhoodLotInput input = new NeighborhoodLotInput();
        input.LotId = id; input.BuildIndex = buildIndex; input.Level = 1;
        input.ObservedStockClass = stockClass; input.StockPhase = phase;
        input.HasLiveResidents = residents; input.Factors = Factors(factorValue);
        return input;
    }

    private static NeighborhoodUpdateInput Update(uint frame, params NeighborhoodLotInput[] lots)
    {
        return new NeighborhoodUpdateInput(frame, lots, new[] { 0, 0, 0 });
    }

    private static NeighborhoodUpdateInput UpdateDemand(uint frame, int c0, int c1, int c2, params NeighborhoodLotInput[] lots)
    {
        return new NeighborhoodUpdateInput(frame, lots, new[] { c0, c1, c2 });
    }

    private static void PhaseDistinctions()
    {
        True(NeighborhoodPhase.Vacant != NeighborhoodPhase.Abandoned, "vacant is not abandoned");
        True(NeighborhoodPhase.Vacant != NeighborhoodPhase.Constructing, "vacant is not constructing");
        True(NeighborhoodPhase.Abandoned != NeighborhoodPhase.Constructing, "abandoned is not constructing");
        Equal("Vacant", NeighborhoodPhases.PhaseName(NeighborhoodPhase.Vacant), "vacant name");
        Equal("Abandoned", NeighborhoodPhases.PhaseName(NeighborhoodPhase.Abandoned), "abandoned name");
        Equal(NeighborhoodPhase.Constructing, NeighborhoodPhases.FromStockPhase(ResidentialStockPhase.Constructing), "stock constructing maps");
        Equal(NeighborhoodPhase.Constructing, NeighborhoodPhases.FromStockPhase(ResidentialStockPhase.Upgrading), "stock upgrading maps to constructing");
        Equal(NeighborhoodPhase.NativeUnavailable, NeighborhoodPhases.FromStockPhase(ResidentialStockPhase.Unavailable), "stock unavailable maps");
        Equal(NeighborhoodPhase.None, NeighborhoodPhases.FromStockPhase(ResidentialStockPhase.Completed), "stock completed defers to model");
        Equal("ClassReplacement", NeighborhoodPhases.ReasonName(NeighborhoodTransitionReason.ClassReplacement), "reason name");
        Throws<ArgumentOutOfRangeException>(delegate { NeighborhoodPhases.FromStockPhase((ResidentialStockPhase)99); }, "unknown stock phase rejected");
    }

    private static void AttractivenessMath()
    {
        NeighborhoodAttractiveness weights = NeighborhoodAttractiveness.Default;
        Equal(100f, weights.Evaluate(Factors(100f)), "neutral factors are fully attractive");
        Equal(0f, weights.Evaluate(Factors(0f)), "zero factors are unattractive");
        // Default weight of accessible employment is 0.25: losing it alone costs 25 points.
        NeighborhoodFactors mixed = Factors(100f);
        mixed.AccessibleEmployment = 0f;
        Equal(75f, weights.Evaluate(mixed), "employment weight is a documented share");
        for (int field = 0; field < 6; field++)
        {
            NeighborhoodFactors reduced = Factors(100f);
            switch (field)
            {
                case 0: reduced.AccessibleEmployment = 40f; break;
                case 1: reduced.Services = 40f; break;
                case 2: reduced.Taxes = 40f; break;
                case 3: reduced.Pollution = 40f; break;
                case 4: reduced.Traffic = 40f; break;
                default: reduced.LandValue = 40f; break;
            }
            True(weights.Evaluate(reduced) < 100f, "attractiveness monotone in factor " + field);
            True(weights.Evaluate(reduced) >= 0f, "attractiveness stays in range for factor " + field);
        }
        NeighborhoodFactors invalid = Factors(50f);
        invalid.Pollution = 101f;
        Throws<ArgumentException>(delegate { weights.Evaluate(invalid); }, "out-of-range factor rejected");
        invalid = Factors(50f); invalid.Taxes = float.NaN;
        Throws<ArgumentException>(delegate { weights.Evaluate(invalid); }, "NaN factor rejected");
        Throws<ArgumentNullException>(delegate { new NeighborhoodAttractiveness(null); }, "null weights rejected");
        Throws<ArgumentException>(delegate { new NeighborhoodAttractiveness(new[] { 1f, 1f, 1f }); }, "short weights rejected");
        Throws<ArgumentException>(delegate { new NeighborhoodAttractiveness(new[] { -1f, 1f, 1f, 1f, 1f, 1f }); }, "negative weight rejected");
        Throws<ArgumentException>(delegate { new NeighborhoodAttractiveness(new[] { 0f, 0f, 0f, 0f, 0f, 0f }); }, "all-zero weights rejected");
    }

    private static void ThresholdValidation()
    {
        NeighborhoodThresholds t = NeighborhoodThresholds.Default;
        True(t.MaintenanceGraceUpdates >= 1, "grace at least one update");
        True(t.AbandonmentAttractiveness <= t.MaintenanceAttractiveness, "abandonment bar at or below maintenance");
        True(t.RehabilitationAttractiveness >= t.ReoccupationAttractiveness, "rehabilitation at least reoccupation");
        Throws<ArgumentException>(delegate { new NeighborhoodThresholds(25f, 3, 40f, 5, 55f, 65f, 4, 70f); }, "abandonment above maintenance rejected");
        Throws<ArgumentOutOfRangeException>(delegate { new NeighborhoodThresholds(40f, 0, 25f, 5, 55f, 65f, 4, 70f); }, "zero grace rejected");
        Throws<ArgumentOutOfRangeException>(delegate { new NeighborhoodThresholds(40f, 3, 25f, 5, 55f, 65f, 4, 101f); }, "out-of-range bar rejected");
        Throws<ArgumentNullException>(delegate { new NeighborhoodModel(null, t); }, "null attractiveness rejected");
        Throws<ArgumentNullException>(delegate { new NeighborhoodModel(NeighborhoodAttractiveness.Default, null); }, "null thresholds rejected");
    }

    private static void ConstructionCompletion()
    {
        NeighborhoodModel model = new NeighborhoodModel(NeighborhoodAttractiveness.Default, NeighborhoodThresholds.Default);
        NeighborhoodReport constructing = model.Apply(Update(1, Lot(7, 100, 1, ResidentialStockPhase.Constructing, false, 80f)));
        Equal(NeighborhoodPhase.Constructing, constructing.Lots[0].Phase, "starts constructing");
        NeighborhoodReport done = model.Apply(Update(2, Lot(7, 100, 1, ResidentialStockPhase.Completed, true, 80f)));
        Equal(NeighborhoodPhase.Occupied, done.Lots[0].Phase, "completed with residents is occupied");
        Equal(NeighborhoodTransitionReason.ConstructionCompleted, done.Transitions[0].Reason, "completion cause recorded");
        // Second lot finishes without any resident yet: distinct vacant, still not abandoned.
        model.Apply(Update(3, Lot(8, 200, 1, ResidentialStockPhase.Constructing, false, 80f)));
        NeighborhoodReport emptyDone = model.Apply(Update(4, Lot(8, 200, 1, ResidentialStockPhase.Completed, false, 80f)));
        Equal(NeighborhoodPhase.Vacant, emptyDone.Lots[0].Phase, "completed without residents is vacant");
        Equal(NeighborhoodTransitionReason.ConstructionCompleted, emptyDone.Transitions[0].Reason, "vacant completion cause");
    }

    private static void MaintenanceGrace()
    {
        NeighborhoodModel model = new NeighborhoodModel(NeighborhoodAttractiveness.Default, NeighborhoodThresholds.Default);
        NeighborhoodLotInput home = Lot(9, 300, 1, ResidentialStockPhase.Completed, true, 100f);
        model.Apply(Update(1, home));
        home.Factors = Factors(0f);
        NeighborhoodReport first = model.Apply(Update(2, home));
        Equal(NeighborhoodPhase.Occupied, first.Lots[0].Phase, "attractiveness collapse does not vacate on the same update");
        Equal(0, first.Transitions.Length, "no transition without the grace elapsing");
        NeighborhoodReport second = model.Apply(Update(3, home));
        Equal(NeighborhoodPhase.Occupied, second.Lots[0].Phase, "second strained update still occupied");
        NeighborhoodReport third = model.Apply(Update(4, home));
        Equal(NeighborhoodPhase.Vacant, third.Lots[0].Phase, "grace exhausted vacates");
        Equal(NeighborhoodTransitionReason.AttractivenessBelowMaintenance, third.Transitions[0].Reason, "vacancy cause recorded");
        // Recovery during grace keeps the family: drop, drop, recover.
        NeighborhoodModel recover = new NeighborhoodModel(NeighborhoodAttractiveness.Default, NeighborhoodThresholds.Default);
        NeighborhoodLotInput lot = Lot(10, 310, 1, ResidentialStockPhase.Completed, true, 100f);
        recover.Apply(Update(1, lot));
        lot.Factors = Factors(0f);
        recover.Apply(Update(2, lot));
        lot.Factors = Factors(90f);
        NeighborhoodReport saved = recover.Apply(Update(3, lot));
        Equal(NeighborhoodPhase.Occupied, saved.Lots[0].Phase, "recovery inside grace keeps residents");
    }

    private static void AbandonmentCycle()
    {
        NeighborhoodModel model = new NeighborhoodModel(NeighborhoodAttractiveness.Default, NeighborhoodThresholds.Default);
        NeighborhoodLotInput home = Lot(11, 400, 1, ResidentialStockPhase.Completed, true, 100f);
        model.Apply(Update(1, home));
        home.Factors = Factors(0f); home.HasLiveResidents = false;
        // Frame 2 vacates (observed emptiness); frames 3-5 accumulate three vacancy updates.
        for (uint frame = 2; frame <= 5; frame++) model.Apply(Update(frame, home));
        NeighborhoodReport before = model.Apply(Update(6, home));
        Equal(NeighborhoodPhase.Vacant, before.Lots[0].Phase, "still vacant before abandonment duration");
        NeighborhoodReport fifth = model.Apply(Update(7, home));
        Equal(NeighborhoodPhase.Abandoned, fifth.Lots[0].Phase, "abandoned after vacancy duration at low attractiveness");
        Equal(NeighborhoodTransitionReason.ProlongedVacancyLowAttractiveness, fifth.Transitions[0].Reason, "abandonment cause");
        // Vacancy alone with acceptable attractiveness never abandons.
        NeighborhoodModel stable = new NeighborhoodModel(NeighborhoodAttractiveness.Default, NeighborhoodThresholds.Default);
        NeighborhoodLotInput calm = Lot(12, 410, 1, ResidentialStockPhase.Completed, false, 50f);
        for (uint frame = 1; frame <= 20; frame++) stable.Apply(Update(frame, calm));
        Equal(NeighborhoodPhase.Vacant, stable.Apply(Update(21, calm)).Lots[0].Phase, "acceptable attractiveness never abandons");
    }

    private static void ReoccupationConditions()
    {
        NeighborhoodModel model = new NeighborhoodModel(NeighborhoodAttractiveness.Default, NeighborhoodThresholds.Default);
        NeighborhoodLotInput home = Lot(13, 500, 1, ResidentialStockPhase.Completed, false, 60f);
        model.Apply(Update(1, home));
        Equal(NeighborhoodPhase.Vacant, model.Apply(Update(2, home)).Lots[0].Phase, "no demand means no reoccupation");
        NeighborhoodReport reoccupied = model.Apply(UpdateDemand(3, 5, 0, 0, home));
        Equal(NeighborhoodPhase.Occupied, reoccupied.Lots[0].Phase, "demand plus attractiveness reoccupies");
        Equal(NeighborhoodTransitionReason.ReoccupationDemandMet, reoccupied.Transitions[0].Reason, "reoccupation cause");
        // Attractiveness below the bar: demand alone is not enough.
        NeighborhoodModel weak = new NeighborhoodModel(NeighborhoodAttractiveness.Default, NeighborhoodThresholds.Default);
        NeighborhoodLotInput poor = Lot(14, 510, 1, ResidentialStockPhase.Completed, false, 50f);
        weak.Apply(Update(1, poor));
        Equal(NeighborhoodPhase.Vacant, weak.Apply(UpdateDemand(2, 99, 0, 0, poor)).Lots[0].Phase, "unattractive lot is not reoccupied by demand");
    }

    private static void RehabilitationConditions()
    {
        NeighborhoodModel model = new NeighborhoodModel(NeighborhoodAttractiveness.Default, NeighborhoodThresholds.Default);
        NeighborhoodLotInput home = Lot(15, 600, 1, ResidentialStockPhase.Completed, false, 0f);
        for (uint frame = 1; frame <= 10; frame++) model.Apply(Update(frame, home));
        Equal(NeighborhoodPhase.Abandoned, model.Apply(Update(11, home)).Lots[0].Phase, "lot abandoned before rehabilitation");
        home.Factors = Factors(60f);
        Equal(NeighborhoodPhase.Abandoned, model.Apply(UpdateDemand(12, 5, 0, 0, home)).Lots[0].Phase, "reoccupation bar alone does not rehabilitate");
        home.Factors = Factors(65f);
        Equal(NeighborhoodPhase.Abandoned, model.Apply(Update(13, home)).Lots[0].Phase, "attractiveness alone does not rehabilitate");
        NeighborhoodReport healed = model.Apply(UpdateDemand(14, 5, 0, 0, home));
        Equal(NeighborhoodPhase.Occupied, healed.Lots[0].Phase, "demand plus rehabilitation bar restores occupancy");
        Equal(NeighborhoodTransitionReason.RehabilitationDemandMet, healed.Transitions[0].Reason, "rehabilitation cause");
    }

    private static void ObservedTransitions()
    {
        NeighborhoodModel model = new NeighborhoodModel(NeighborhoodAttractiveness.Default, NeighborhoodThresholds.Default);
        NeighborhoodLotInput home = Lot(16, 700, 1, ResidentialStockPhase.Completed, true, 80f);
        model.Apply(Update(1, home));
        home.HasLiveResidents = false;
        NeighborhoodReport left = model.Apply(Update(2, home));
        Equal(NeighborhoodPhase.Vacant, left.Lots[0].Phase, "observed emptiness vacates");
        Equal(NeighborhoodTransitionReason.ObservedVacancy, left.Transitions[0].Reason, "observed vacancy cause");
        home.HasLiveResidents = true;
        NeighborhoodReport arrived = model.Apply(Update(3, home));
        Equal(NeighborhoodPhase.Occupied, arrived.Lots[0].Phase, "observed residents reoccupy");
        Equal(NeighborhoodTransitionReason.ObservedReoccupation, arrived.Transitions[0].Reason, "observed reoccupation cause");
    }

    private static void ReplacementKeepsLevelSeparate()
    {
        NeighborhoodModel model = new NeighborhoodModel(NeighborhoodAttractiveness.Default, NeighborhoodThresholds.Default);
        NeighborhoodLotInput home = Lot(17, 800, 1, ResidentialStockPhase.Completed, true, 80f);
        for (uint frame = 1; frame <= 3; frame++) model.Apply(UpdateDemand(frame, 0, 0, 0, home));
        Equal(0, model.Apply(UpdateDemand(4, 0, 0, 0, home)).Transitions.Length, "no higher-class demand means no replacement");
        for (uint frame = 5; frame <= 7; frame++) model.Apply(UpdateDemand(frame, 0, 10, 0, home));
        NeighborhoodReport still = model.Apply(UpdateDemand(8, 0, 10, 0, home));
        Equal(NeighborhoodPhase.Occupied, still.Lots[0].Phase, "replacement keeps the lot occupied");
        Equal(1, still.Lots[0].ResidentClass, "sustained demand replaces residents upward");
        Equal(1, still.Lots[0].Level, "level unchanged by class replacement");
        Equal(NeighborhoodTransitionReason.ClassReplacement, still.Transitions[0].Reason, "replacement cause");
        // Top class never replaces further.
        NeighborhoodModel top = new NeighborhoodModel(NeighborhoodAttractiveness.Default, NeighborhoodThresholds.Default);
        NeighborhoodLotInput estate = Lot(18, 810, 3, ResidentialStockPhase.Completed, true, 90f);
        for (uint frame = 1; frame <= 10; frame++) top.Apply(UpdateDemand(frame, 0, 0, 99, estate));
        Equal(2, top.Apply(UpdateDemand(11, 0, 0, 99, estate)).Lots[0].ResidentClass, "class two is the ceiling");
    }

    private static void GenerationReset()
    {
        NeighborhoodModel model = new NeighborhoodModel(NeighborhoodAttractiveness.Default, NeighborhoodThresholds.Default);
        NeighborhoodLotInput home = Lot(19, 900, 1, ResidentialStockPhase.Completed, false, 0f);
        for (uint frame = 1; frame <= 10; frame++) model.Apply(Update(frame, home));
        Equal(NeighborhoodPhase.Abandoned, model.Apply(Update(11, home)).Lots[0].Phase, "old generation abandoned");
        NeighborhoodLotInput rebuilt = Lot(19, 901, 1, ResidentialStockPhase.Constructing, false, 80f);
        NeighborhoodReport reset = model.Apply(Update(12, rebuilt));
        Equal(NeighborhoodPhase.Constructing, reset.Lots[0].Phase, "new generation starts fresh");
        Equal(NeighborhoodTransitionReason.GenerationReset, reset.Transitions[0].Reason, "generation reset cause");
        Equal(901u, reset.Transitions[0].BuildIndex, "transition carries new generation");
        // The abandoned past never reappears for the new building.
        NeighborhoodReport finished = model.Apply(Update(13, Lot(19, 901, 1, ResidentialStockPhase.Completed, true, 80f)));
        Equal(NeighborhoodPhase.Occupied, finished.Lots[0].Phase, "rebuilt lot occupies without old state");
        Equal(0, finished.Lots[0].VacancyUpdates, "counters reset with generation");
    }

    private static void ReportIntegrity()
    {
        NeighborhoodModel model = new NeighborhoodModel(NeighborhoodAttractiveness.Default, NeighborhoodThresholds.Default);
        NeighborhoodReport report = model.Apply(new NeighborhoodUpdateInput(5, new[]
        {
            Lot(20, 1000, 1, ResidentialStockPhase.Constructing, false, 80f),
            Lot(21, 1001, 2, ResidentialStockPhase.Completed, true, 90f),
            Lot(22, 1002, 3, ResidentialStockPhase.Completed, false, 0f)
        }, new[] { 1, 0, 0 }));
        int phaseSum = 0;
        for (int i = 0; i < report.PhaseCounts.Length; i++) phaseSum += report.PhaseCounts[i];
        Equal(report.Lots.Length, phaseSum, "phase counts partition the lots");
        for (int i = 0; i < report.Transitions.Length; i++)
            True(report.Transitions[i].Reason != NeighborhoodTransitionReason.None, "every transition carries a cause");
        int reasonSum = 0;
        for (int i = 0; i < report.ReasonCounts.Length; i++) reasonSum += report.ReasonCounts[i];
        Equal(report.Transitions.Length, reasonSum, "reason counts partition the transitions");
    }

    private static void CodecRoundtrip()
    {
        NeighborhoodModel model = new NeighborhoodModel(NeighborhoodAttractiveness.Default, NeighborhoodThresholds.Default);
        model.Apply(new NeighborhoodUpdateInput(1, new[]
        {
            Lot(30, 2000, 1, ResidentialStockPhase.Constructing, false, 80f),
            Lot(31, 2001, 2, ResidentialStockPhase.Completed, true, 90f),
            Lot(32, 2002, 3, ResidentialStockPhase.Completed, false, 0f)
        }, new[] { 0, 0, 0 }));
        model.Apply(Update(2, Lot(32, 2002, 3, ResidentialStockPhase.Completed, false, 0f)));
        byte[] saved = model.Save();
        List<NeighborhoodLotState> restored = NeighborhoodStateCodec.Read(saved);
        Equal(3, restored.Count, "codec preserves lot count");
        for (int i = 0; i < restored.Count; i++)
        {
            NeighborhoodLotState before;
            True(model.TryGetLot(restored[i].LotId, out before), "saved lot is tracked");
            NeighborhoodLotState after = restored[i];
            Equal(before.BuildIndex, after.BuildIndex, "build index round-trips");
            Equal(before.Phase, after.Phase, "phase round-trips");
            Equal(before.ResidentClass, after.ResidentClass, "class round-trips");
            Equal(before.VacancyUpdates, after.VacancyUpdates, "vacancy counter round-trips");
            Equal(before.MaintenanceStrainUpdates, after.MaintenanceStrainUpdates, "strain counter round-trips");
            Equal(before.ReplacementSustainedUpdates, after.ReplacementSustainedUpdates, "replacement counter round-trips");
            Equal(before.LastAttractiveness, after.LastAttractiveness, "attractiveness round-trips");
            Equal((int)before.LastReason, (int)after.LastReason, "reason round-trips");
            Equal(before.LastTransitionFrame, after.LastTransitionFrame, "frame round-trips");
        }
        // Byte-identical re-save proves deterministic encoding.
        NeighborhoodModel model2 = NeighborhoodModel.Restore(NeighborhoodAttractiveness.Default, NeighborhoodThresholds.Default, saved);
        EqualBytes(saved, model2.Save(), "re-save is byte-identical");
    }

    private static NeighborhoodLotState ModelLot(NeighborhoodModel model, ushort lotId)
    {
        NeighborhoodLotState state;
        if (!model.TryGetLot(lotId, out state)) throw new InvalidOperationException("lot missing from model");
        return state;
    }

    private static void CodecRejections()
    {
        NeighborhoodModel model = new NeighborhoodModel(NeighborhoodAttractiveness.Default, NeighborhoodThresholds.Default);
        model.Apply(Update(1, Lot(40, 3000, 1, ResidentialStockPhase.Completed, true, 80f)));
        byte[] saved = model.Save();
        byte[] truncated = new byte[saved.Length - 1];
        Buffer.BlockCopy(saved, 0, truncated, 0, truncated.Length);
        Throws<InvalidDataException>(delegate { NeighborhoodStateCodec.Read(truncated); }, "truncated state rejected");
        byte[] corrupted = (byte[])saved.Clone();
        corrupted[corrupted.Length - 5] ^= 0xFF;
        Throws<InvalidDataException>(delegate { NeighborhoodStateCodec.Read(corrupted); }, "checksum mismatch rejected");
        byte[] trailing = new byte[saved.Length + 1];
        Buffer.BlockCopy(saved, 0, trailing, 0, saved.Length);
        Throws<InvalidDataException>(delegate { NeighborhoodStateCodec.Read(trailing); }, "trailing bytes rejected");
        byte[] badMagic = (byte[])saved.Clone();
        badMagic[0] = (byte)'X';
        Throws<InvalidDataException>(delegate { NeighborhoodStateCodec.Read(badMagic); }, "magic mismatch rejected");
        byte[] badVersion = (byte[])saved.Clone();
        badVersion[8] = 9; // corrupts the payload length prefix
        Throws<InvalidDataException>(delegate { NeighborhoodStateCodec.Read(badVersion); }, "corrupted length prefix rejected");
        Throws<ArgumentNullException>(delegate { NeighborhoodStateCodec.Read(null); }, "null state rejected");
        Throws<ArgumentNullException>(delegate { NeighborhoodModel.Restore(NeighborhoodAttractiveness.Default, NeighborhoodThresholds.Default, null); }, "restore rejects null");
    }

    private static void RestoreContinuesTrajectory()
    {
        NeighborhoodModel live = new NeighborhoodModel(NeighborhoodAttractiveness.Default, NeighborhoodThresholds.Default);
        NeighborhoodModel restored;
        NeighborhoodLotInput home = Lot(50, 4000, 1, ResidentialStockPhase.Completed, true, 100f);
        live.Apply(Update(1, home));
        restored = NeighborhoodModel.Restore(NeighborhoodAttractiveness.Default, NeighborhoodThresholds.Default, live.Save());
        home.Factors = Factors(0f);
        for (uint frame = 2; frame <= 4; frame++)
        {
            live.Apply(Update(frame, home));
            restored.Apply(Update(frame, home));
        }
        NeighborhoodReport liveReport = live.Apply(Update(5, home));
        NeighborhoodReport restoredReport = restored.Apply(Update(5, home));
        Equal((int)liveReport.Lots[0].Phase, (int)restoredReport.Lots[0].Phase, "restored model follows the same trajectory");
        Equal((int)liveReport.Transitions[0].Reason, (int)restoredReport.Transitions[0].Reason, "restored model produces the same cause");
    }

    private static void AdapterOptIn()
    {
        True(NeighborhoodCycleSession.TryCreate(false, NeighborhoodAttractiveness.Default, NeighborhoodThresholds.Default) == null, "disabled session is null");
        True(NeighborhoodCycleSession.TryRestore(false, NeighborhoodAttractiveness.Default, NeighborhoodThresholds.Default, new byte[16]) == null, "disabled restore is null");
        NeighborhoodCycleSession session = NeighborhoodCycleSession.TryCreate(true, NeighborhoodAttractiveness.Default, NeighborhoodThresholds.Default);
        True(session != null, "enabled session exists");
        ResidentialStockSnapshot stock = new ResidentialStockSnapshot(42, new[]
        {
            new ResidentialStockLot(60, 5000, 1, ResidentialStockPhase.Completed, 6, 1, 5, 0, 12, 2, 0, 0),
            new ResidentialStockLot(61, 5001, 0, ResidentialStockPhase.Constructing, 0, 0, 0, 0, 0, 0, 0, 0)
        });
        Dictionary<ushort, NeighborhoodFactors> factors = new Dictionary<ushort, NeighborhoodFactors>();
        factors[60] = Factors(80f);
        int missing;
        NeighborhoodReport report = session.Observe(42, stock, factors, new[] { 3, 0, 0 }, out missing);
        Equal(42u, report.Frame, "adapter frame passthrough");
        Equal(2, report.Lots.Length, "adapter converts both lots");
        Equal(1, missing, "missing factor samples counted");
        Equal(NeighborhoodPhase.Occupied, report.Lots[0].Phase, "stock lot with residents occupied");
        Equal(NeighborhoodPhase.Constructing, report.Lots[1].Phase, "stock constructing lot constructing");
        True(session.Save().Length > 0, "session persists state");
        NeighborhoodCycleSession restored = NeighborhoodCycleSession.TryRestore(true,
            NeighborhoodAttractiveness.Default, NeighborhoodThresholds.Default, session.Save());
        True(restored != null, "session restores");
        Throws<ArgumentNullException>(delegate { session.Observe(1, null, factors, new[] { 0, 0, 0 }, out missing); }, "adapter rejects null stock");
    }

    private static void Determinism()
    {
        NeighborhoodModel first = new NeighborhoodModel(NeighborhoodAttractiveness.Default, NeighborhoodThresholds.Default);
        NeighborhoodModel second = new NeighborhoodModel(NeighborhoodAttractiveness.Default, NeighborhoodThresholds.Default);
        NeighborhoodLotInput a = Lot(70, 6000, 1, ResidentialStockPhase.Completed, true, 100f);
        NeighborhoodLotInput b = Lot(71, 6001, 2, ResidentialStockPhase.Completed, false, 10f);
        for (uint frame = 1; frame <= 8; frame++)
        {
            if (frame == 3) a.Factors = Factors(0f);
            if (frame == 6) a.Factors = Factors(70f);
            NeighborhoodReport r1 = first.Apply(new NeighborhoodUpdateInput(frame, new[] { a, b }, new[] { 1, 2, 0 }));
            NeighborhoodReport r2 = second.Apply(new NeighborhoodUpdateInput(frame, new[] { a, b }, new[] { 1, 2, 0 }));
            Equal(r1.Lots.Length, r2.Lots.Length, "same lot count");
            for (int i = 0; i < r1.Lots.Length; i++)
            {
                Equal((int)r1.Lots[i].Phase, (int)r2.Lots[i].Phase, "same phase frame " + frame);
                Equal(r1.Lots[i].ResidentClass, r2.Lots[i].ResidentClass, "same class frame " + frame);
                Equal((int)r1.Lots[i].LastReason, (int)r2.Lots[i].LastReason, "same reason frame " + frame);
            }
        }
    }

    private static void NativeLevelUpPolicy()
    {
        NeighborhoodNativeGrowthInput neutral = new NeighborhoodNativeGrowthInput();
        neutral.CurrentLevel = 1; neutral.TargetLevel = 2;
        neutral.SecondaryProgress = 12; neutral.PrimaryProgress = 10; neutral.Attractiveness = 100f;
        NeighborhoodNativeGrowthOutput unchanged = NeighborhoodNativeLevelUpPolicy.ApplyResidential(neutral);
        Equal(2, unchanged.TargetLevel, "neutral score keeps native target");
        Equal(12, unchanged.SecondaryProgress, "neutral score keeps secondary progress");
        Equal(10, unchanged.PrimaryProgress, "neutral score keeps land-value progress");

        neutral.Attractiveness = 40f;
        NeighborhoodNativeGrowthOutput slowed = NeighborhoodNativeLevelUpPolicy.ApplyResidential(neutral);
        Equal(1, slowed.TargetLevel, "low score gates upgrade at current level");
        Equal(12, slowed.SecondaryProgress, "low score does not invent secondary changes");
        Equal(4, slowed.PrimaryProgress, "low score scales native progress down");

        neutral.Attractiveness = 0f;
        NeighborhoodNativeGrowthOutput stalled = NeighborhoodNativeLevelUpPolicy.ApplyCommercial(neutral);
        Equal(1, stalled.TargetLevel, "commercial low score gates upgrade");
        Equal(0, stalled.PrimaryProgress, "zero score stalls progress");
        NeighborhoodNativeGrowthInput nativeDowngrade = neutral;
        nativeDowngrade.TargetLevel = 0;
        NeighborhoodNativeGrowthOutput preserved = NeighborhoodNativeLevelUpPolicy.ApplyResidential(nativeDowngrade);
        Equal(0, preserved.TargetLevel, "native lower target is preserved without invention");
        Throws<ArgumentException>(delegate
        {
            NeighborhoodNativeGrowthInput invalid = neutral;
            invalid.Attractiveness = float.NaN;
            NeighborhoodNativeLevelUpPolicy.ApplyResidential(invalid);
        }, "native policy rejects NaN score");
    }

    private static void Equal(int expected, int actual, string label)
    {
        _checks++;
        if (expected != actual) throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual + ".");
    }

    private static void Equal(uint expected, uint actual, string label)
    {
        _checks++;
        if (expected != actual) throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual + ".");
    }

    private static void Equal(float expected, float actual, string label)
    {
        _checks++;
        if (Math.Abs(expected - actual) > 0.0001f) throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual + ".");
    }

    private static void Equal(NeighborhoodPhase expected, NeighborhoodPhase actual, string label)
    {
        _checks++;
        if (expected != actual) throw new InvalidOperationException(label + ": expected " + NeighborhoodPhases.PhaseName(expected) + ", got " + NeighborhoodPhases.PhaseName(actual) + ".");
    }

    private static void Equal(NeighborhoodTransitionReason expected, NeighborhoodTransitionReason actual, string label)
    {
        _checks++;
        if (expected != actual) throw new InvalidOperationException(label + ": expected " + NeighborhoodPhases.ReasonName(expected) + ", got " + NeighborhoodPhases.ReasonName(actual) + ".");
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
