using System;
using SC4Evolution.Simulation;

internal static class Program
{
    private static readonly Guid ClassGuid = new Guid("9132cdd4-1655-4cb7-8b44-8c36c243569f");
    private static int _checks;
    private static int Main()
    {
        try
        {
            DailyReservations(); NativeSuccessAndReconciliation(); OwnershipAndEpoch(); StockTransitions();
            SameClassReplacement(); ReplacementFloor(); DifferentClassReplacement(); PredecessorCancellation(); RestoreAndReplay();
            ExternalRevisions(); ExternalStockChanges(); TransactionalRestoreFailures(); ArithmeticAndMutationIsolation(); CyclicConservation();
            Console.WriteLine("PASS: " + _checks + " assertions across 14 pure capacity-ledger scenarios; no native effects, codec, clock or RNG.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static ResidentialCapacityLedger New(long epoch = 1, long firstDay = 0)
    { ResidentialCapacityLedger ledger = new ResidentialCapacityLedger(ClassGuid, epoch, firstDay); Publish(ledger); return ledger; }
    private static void Publish(ResidentialCapacityLedger ledger)
    { ResidentialCapacityLedgerSnapshot snapshot = ledger.Snapshot(); Check(ledger.AcknowledgePublication(snapshot.StockRevision, snapshot.ExternalRevision), "coherent publication acknowledged"); }
    private static ResidentialLotIdentity Identity(ushort id, uint index = 0, string prefab = "NativeHome", byte width = 1, byte length = 1)
    { return new ResidentialLotIdentity(id, index, 0, 0, prefab, 1, 1, width, length); }
    private static ResidentialCapacityRecord Record(ResidentialLotIdentity identity, ulong generation, ResidentialClass lotClass = ResidentialClass.Low,
        long homes = 1, ResidentialCapacityPhase phase = ResidentialCapacityPhase.Constructing, ResidentialNominalCategory category = ResidentialNominalCategory.LowGeneric,
        int level = 0, Guid? guid = null)
    { return new ResidentialCapacityRecord(guid ?? ClassGuid, identity, generation, lotClass, category, level, homes, phase, ClassOrigin.Selected); }
    private static ResidentialCapacityLotSpec Spec(uint index = 0, string prefab = "NativeHome", byte width = 1, byte length = 1,
        ResidentialClass lotClass = ResidentialClass.Low, ResidentialNominalCategory category = ResidentialNominalCategory.LowGeneric, int level = 0)
    { return new ResidentialCapacityLotSpec(index, 0, 0, prefab, 1, 1, width, length, lotClass, category, level); }
    private static ResidentialCapacityReservation Reserve(ResidentialCapacityLedger ledger, object context, ResidentialCapacityLotSpec spec,
        ResidentialLotIdentity old = null, ulong generation = 0)
    { ResidentialCapacityReservation token; Check(ledger.TryReserve(context, ledger.Epoch, spec, old, generation, out token), "reservation accepted"); return token; }
    private static void Stock(ResidentialCapacityLedger ledger, long low, long medium = 0, long high = 0)
    { long[] values = ledger.Snapshot().StockQ; Equal(low, values[0], "low stockQ"); Equal(medium, values[1], "medium stockQ"); Equal(high, values[2], "high stockQ"); }

    private static void DailyReservations()
    {
        ResidentialCapacityLedger ledger = New();
        Check(ledger.TryOpenWindow(ResidentialClass.Low, 10, 5.9f, 1.5f), "unqualified day consumed");
        Equal(0L, ledger.GetWindow(ResidentialClass.Low).DesiredQ, "trunc5.9 does not qualify");
        Check(!ledger.TryOpenWindow(ResidentialClass.Low, 10, 20, 1.5f), "RAW change does not reopen same day");
        Check(!ledger.TryOpenWindow(ResidentialClass.Low, 9, 20, 1.5f), "calendar rewind does not reopen");
        Check(ledger.TryOpenWindow(ResidentialClass.Low, 11, 6, 1.5f), "raw6 qualifies");
        Equal(24L, ledger.GetWindow(ResidentialClass.Low).DesiredQ, "desired in quarters");
        Equal(36L, ledger.GetWindow(ResidentialClass.Low).DesiredMaximumQ, "overbuild passed by caller");
        object context = new object(); ResidentialCapacityReservation a = Reserve(ledger, context, Spec()), b = Reserve(ledger, context, Spec());
        Equal(26L, ledger.Snapshot().ReservedQuotaQ[0], "nested holds visible immediately");
        ResidentialCapacityReservation rejected;
        Check(!ledger.TryReserve(context, 1, Spec(), null, 0, out rejected), "held target blocks third attempt");
        ResidentialCapacityLedgerState state; Check(!ledger.TryCapture(out state), "no save with in-flight holds");
        Check(!ledger.Cancel(b, new object(), 1), "wrong context cannot free hold");
        Check(!ledger.Cancel(b, context, 2), "wrong epoch cannot free hold");
        Check(ledger.Cancel(b, context, 1), "failed native attempt rolls back exactly");
        Check(!ledger.Cancel(b, context, 1), "double cancel does not refund");
        Check(ledger.MarkNativeCreated(a, context, 1), "native success marked");
        Check(!ledger.Cancel(a, context, 1), "success cannot be canceled into absent stock");
        Check(ledger.CommitCreated(a, context, 1, Record(Identity(1), 1)), "commit first native lot");
        Stock(ledger, 13); Equal(13L, ledger.GetWindow(ResidentialClass.Low).CommittedQ, "daily expenditure exact");
        Check(!ledger.TryReserve(context, 1, Spec(), null, 0, out rejected), "stale stock publication blocks spend");
        Publish(ledger); a = Reserve(ledger, context, Spec());
        Check(ledger.CommitCreated(a, context, 1, Record(Identity(2), 2)), "second physical commit");
        Equal(ResidentialCapacityWindowPhase.Exhausted, ledger.GetWindow(ResidentialClass.Low).Phase, "last lot may reach beyond desired");
        Check(ledger.Release(Identity(1), 1), "release new lot");
        Equal(26L, ledger.GetWindow(ResidentialClass.Low).CommittedQ, "release does not refund daily budget");
        Publish(ledger); Check(!ledger.TryOpenWindow(ResidentialClass.Low, 11, 100, 1.5f), "publication does not renew day");
        Check(!ledger.TryReserve(context, 1, Spec(), null, 0, out rejected), "spent day remains spent after release");
    }
    private static void NativeSuccessAndReconciliation()
    {
        ResidentialCapacityLedger ledger = New(); object context = new object();
        Check(ledger.TryOpenWindow(ResidentialClass.Low, 1, 20, 1.5f), "open rounding window");
        ResidentialCapacityReservation token = Reserve(ledger, context, Spec(width: 4, length: 4));
        Equal(52L, token.ReservedCapacityQ, "reserve HOME maximum before ID exists");
        Check(ledger.MarkNativeCreated(token, context, 1), "native-created before metadata validation");
        Check(!ledger.MarkNativeCreated(token, context, 1), "mark idempotent");
        Check(!ledger.CommitCreated(token, context, 1, Record(Identity(1, prefab: "Unexpected", width: 4, length: 4), 1, homes: 3)), "mismatch does not commit fake identity");
        Equal(1, ledger.Snapshot().UnresolvedCreations, "post-success failure retained");
        ResidentialCapacityLedgerState state; Check(!ledger.TryCapture(out state), "unresolved save unavailable");
        Check(!ledger.Cancel(token, context, 1), "failed metadata cannot cancel physical success");
        ResidentialCapacityReservation other; Check(!ledger.TryReserve(context, 1, Spec(), null, 0, out other), "unresolved native success blocks admissions");
        Check(ledger.CommitCreated(token, context, 1, Record(Identity(1, width: 4, length: 4), 1, homes: 3)), "verified retry resolves same token");
        Stock(ledger, 39); Equal(39L, ledger.GetWindow(ResidentialClass.Low).CommittedQ, "Hactual spends39 not reservation52");
        Equal(0L, ledger.Snapshot().ReservedQuotaQ[0], "unused rounding hold released exactly");
        Check(!ledger.CommitCreated(token, context, 1, Record(Identity(2, width: 4, length: 4), 2, homes: 3)), "token single-use");
        Check(ledger.TryCapture(out state), "stable state capturable");

        ledger = New(); Check(ledger.TryOpenWindow(ResidentialClass.Low, 1, 6, 1.5f), "open reconciliation overrun case");
        token = Reserve(ledger, context, Spec()); ledger.MarkNativeCreated(token, context, 1);
        Check(!ledger.CommitCreated(token, context, 1, Record(Identity(3, prefab: "Observed", width: 4, length: 4), 3, homes: 4)), "unexpected physical amount rejected ordinarily");
        Check(ledger.ReconcileCreated(token, context, 1, Record(Identity(3, prefab: "Observed", width: 4, length: 4), 3, homes: 4)), "explicit reconciliation accounts real created stock");
        Stock(ledger, 52); Equal(ResidentialCapacityWindowPhase.Invalidated, ledger.GetWindow(ResidentialClass.Low).Phase, "reconciliation closes compromised window");
        Equal(52L, ledger.GetWindow(ResidentialClass.Low).CommittedQ, "overrun visible rather than silently clamped");
        Check(ledger.TryCapture(out state), "resolved overrun persists as invalidated day");
        ResidentialCapacityLedger restored = ResidentialCapacityLedger.Restore(ClassGuid, state, 9); Publish(restored);
        Check(!restored.TryOpenWindow(ResidentialClass.Low, 1, 100, 1.5f), "reconciled invalidated day not replayed");
    }
    private static void OwnershipAndEpoch()
    {
        ResidentialCapacityLedger first = New(), second = New(); object context = new object();
        first.TryOpenWindow(ResidentialClass.Low, 1, 20, 1.5f); second.TryOpenWindow(ResidentialClass.Low, 1, 20, 1.5f);
        ResidentialCapacityReservation token = Reserve(first, context, Spec());
        Check(!second.Cancel(token, context, 1), "other ledger cannot consume token");
        Check(!second.MarkNativeCreated(token, context, 1), "other ledger cannot mark token");
        Equal(13L, first.Snapshot().ReservedQuotaQ[0], "foreign calls preserve owner balance");
        first.InvalidateSession();
        Check(!first.Cancel(token, context, 1), "invalidated session cannot mutate tokens");
        Check(!first.CommitCreated(token, context, 1, Record(Identity(1), 1)), "stale session cannot commit");
        Equal(13L, first.Snapshot().ReservedQuotaQ[0], "invalidated ledger retains unresolved hold for owner disposal");
        Check(!first.Snapshot().SessionValid, "session diagnostics invalidated");
        ResidentialCapacityReservation next; Check(!first.TryReserve(context, 1, Spec(), null, 0, out next), "invalidated session cannot open new reservation");
    }
    private static void StockTransitions()
    {
        ResidentialCapacityLedger ledger = New(); ResidentialLotIdentity before = Identity(7, width: 4, length: 4);
        Check(ledger.Import(Record(before, 10, homes: 3)), "import baseline"); Check(!ledger.Import(Record(before, 10, homes: 3)), "import repeat idempotent");
        Stock(ledger, 39); Check(ledger.Complete(before, 10), "construction completes"); Stock(ledger, 39);
        Check(!ledger.Complete(before, 10), "completion repeat no second stock");
        Check(ledger.BeginUpgrade(before, 10), "upgrade keeps old capacity until verified"); Stock(ledger, 39);
        ResidentialLotIdentity after = Identity(7, prefab: "Upgraded", width: 4, length: 4);
        ResidentialCapacityRecord upgraded = Record(after, 10, homes: 4, phase: ResidentialCapacityPhase.Completed, level: 1);
        ResidentialCapacityRecord changedOrigin = new ResidentialCapacityRecord(ClassGuid, after, 10, ResidentialClass.Low, ResidentialNominalCategory.LowGeneric, 1, 4,
            ResidentialCapacityPhase.Completed, ClassOrigin.ImportedNative);
        Check(!ledger.RefreshUpgrade(before, 10, changedOrigin), "upgrade cannot rewrite selected/imported provenance");
        Check(ledger.RefreshUpgrade(before, 10, upgraded), "verified upgrade refreshes same generation"); Stock(ledger, 52);
        Check(!ledger.RefreshUpgrade(before, 10, upgraded), "stale upgrade callback cannot double delta");
        Check(!ledger.Release(before, 10), "old prefab identity cannot remove upgraded record");
        Check(ledger.Abandon(after, 10), "abandon withdraws contribution"); Stock(ledger, 0);
        Check(!ledger.Abandon(after, 10), "abandon idempotent");
        Check(!ledger.Complete(after, 10), "completion does not resurrect unavailable record");
        Check(ledger.Reoccupy(after, 10, ResidentialCapacityPhase.Completed), "reoccupy exact retained nominal"); Stock(ledger, 52);
        Check(!ledger.Reoccupy(after, 10, ResidentialCapacityPhase.Completed), "reoccupy idempotent");
        Check(ledger.Release(after, 10), "release exact generation"); Check(!ledger.Release(after, 10), "release idempotent"); Stock(ledger, 0);
        Check(ledger.Import(Record(Identity(7, index: 1), 11)), "ID may be reused after exact release");
        Check(!ledger.Release(after, 10), "stale generation cannot erase reused ID"); Stock(ledger, 13);
    }
    private static void SameClassReplacement()
    {
        ResidentialCapacityLedger ledger = New(); object context = new object(); ResidentialLotIdentity old = Identity(1);
        ledger.Import(Record(old, 1, phase: ResidentialCapacityPhase.Completed)); Publish(ledger);
        ledger.TryOpenWindow(ResidentialClass.Low, 1, 20, 1.5f);
        ResidentialCapacityReservation wrongSite;
        ResidentialCapacityLotSpec foreignSite = new ResidentialCapacityLotSpec(1, 1065353216, 0, "NativeHome", 1, 1, 4, 4, ResidentialClass.Low, ResidentialNominalCategory.LowGeneric, 0);
        Check(!ledger.TryReserve(context, 1, foreignSite, old, 1, out wrongSite), "predecessor credit cannot subsidize a different physical site");
        ResidentialCapacityReservation token = Reserve(ledger, context, Spec(index: 1, width: 4, length: 4), old, 1);
        Equal(39L, token.ReservedQuotaQ, "same-class reservation credits old13 against new52");
        ResidentialCapacityReservation nested; Check(!ledger.TryReserve(context, 1, Spec(index: 1, width: 4, length: 4), old, 1, out nested), "one predecessor cannot back two reservations");
        Check(ledger.Release(old, 1), "native predecessor released after reservation"); Stock(ledger, 0);
        ResidentialCapacityRecord tombstone; Check(ledger.TryGet(old, 1, out tombstone), "old record retained while token lives");
        Equal(ResidentialCapacityPhase.Unavailable, tombstone.Phase, "released predecessor is tombstone");
        Throws<InvalidOperationException>(delegate { ledger.Import(Record(Identity(1, index: 1), 9)); }, "ID reuse cannot overwrite live predecessor token");
        Check(ledger.CommitCreated(token, context, 1, Record(Identity(1, index: 1, width: 4, length: 4), 2, homes: 3)), "replacement commit at reused ID");
        Stock(ledger, 39); Equal(26L, ledger.GetWindow(ResidentialClass.Low).CommittedQ, "actual same-class spend is net39-13");
        Check(!ledger.TryGet(old, 1, out tombstone), "old tombstone retired after successor");
        Check(!ledger.Release(old, 1), "late old release cannot erase successor");
        Check(!ledger.Cancel(token, context, 1), "committed replacement cannot refund");
    }
    private static void DifferentClassReplacement()
    {
        ResidentialCapacityLedger ledger = New(); object context = new object(); ResidentialLotIdentity old = Identity(3, width: 4, length: 4);
        ledger.Import(Record(old, 1, homes: 3, phase: ResidentialCapacityPhase.Completed)); Publish(ledger);
        ledger.TryOpenWindow(ResidentialClass.High, 1, 6, 1.5f);
        ResidentialCapacityReservation token = Reserve(ledger, context, Spec(index: 1, lotClass: ResidentialClass.High, category: ResidentialNominalCategory.Other), old, 1);
        Equal(5L, token.ReservedQuotaQ, "old Low39 does not credit new High5");
        ledger.Release(old, 1);
        Check(ledger.CommitCreated(token, context, 1, Record(Identity(3, index: 1), 2, ResidentialClass.High, category: ResidentialNominalCategory.Other)), "cross-class replacement commit");
        Stock(ledger, 0, 0, 5); Equal(5L, ledger.GetWindow(ResidentialClass.High).CommittedQ, "new class charged gross capacity");
    }
    private static void ReplacementFloor()
    {
        ResidentialCapacityLedger ledger = New(); object context = new object(); ResidentialLotIdentity old = Identity(1);
        ledger.Import(Record(old, 1, category: ResidentialNominalCategory.Other)); Publish(ledger);
        ledger.TryOpenWindow(ResidentialClass.Low, 1, 6, 1);
        for (ushort i = 2; i <= 5; i++)
        {
            ResidentialCapacityReservation lot = Reserve(ledger, context, Spec(category: ResidentialNominalCategory.Other));
            Check(ledger.CommitCreated(lot, context, 1, Record(Identity(i), i, category: ResidentialNominalCategory.Other)), "spend5Q toward24Q target");
            Publish(ledger);
        }
        Equal(20L, ledger.GetWindow(ResidentialClass.Low).CommittedQ, "four new lots spend20Q");
        ResidentialCapacityLotSpec spec = Spec(index: 1, width: 2, length: 1, category: ResidentialNominalCategory.Other, level: 1);
        ResidentialCapacityReservation token = Reserve(ledger, context, spec, old, 1);
        Equal(5L, token.ReservedQuotaQ, "same-class floor allows gain5 when strict max remaining4");
        ResidentialCapacityReservation nested;
        Check(!ledger.TryReserve(context, 1, Spec(category: ResidentialNominalCategory.Other), null, 0, out nested), "pending floor exhausts remaining intentions before native call");
        Check(ledger.Cancel(token, context, 1), "cancel floor releases exact hold");
        Equal(0L, ledger.Snapshot().ReservedQuotaQ[0], "floor cancellation has no allowance residue");
        Equal(20L, ledger.GetWindow(ResidentialClass.Low).CommittedQ, "floor cancellation does not alter spent quota");
        token = Reserve(ledger, context, spec, old, 1);
        ledger.Release(old, 1);
        Check(ledger.CommitCreated(token, context, 1, Record(Identity(1, index: 1, width: 2, length: 1), 6, homes: 2, category: ResidentialNominalCategory.Other, level: 1)), "floor replacement commits actual nominal10Q");
        Equal(25L, ledger.GetWindow(ResidentialClass.Low).CommittedQ, "floor records1Q excess without clamping");
        Equal(ResidentialCapacityWindowPhase.Exhausted, ledger.GetWindow(ResidentialClass.Low).Phase, "legitimate floor is exhausted, not unexplained reconciliation");
        Stock(ledger, 30);
        Publish(ledger); Check(!ledger.TryReserve(context, 1, Spec(category: ResidentialNominalCategory.Other), null, 0, out nested), "exhausted floor cannot accumulate another allowance");
        ResidentialCapacityLedgerState saved; Check(ledger.TryCapture(out saved), "floor excess captures without schema extension");
        ResidentialCapacityLedger restored = ResidentialCapacityLedger.Restore(ClassGuid, saved, 2); Publish(restored);
        Check(!restored.TryOpenWindow(ResidentialClass.Low, 1, 20, 1.5f), "floor day remains spent after reload");
        Throws<ArgumentException>(delegate { new ResidentialCapacityWindow(ResidentialClass.Low, 1, ResidentialCapacityWindowPhase.Exhausted, 6, 1, 24, 24, 32, 0, 0); }, "normal floor excess8Q rejected");

        ledger = New(); ResidentialLotIdentity existing = Identity(8, width: 2, length: 1);
        ledger.Import(Record(existing, 8, homes: 2, category: ResidentialNominalCategory.Other, level: 1)); Publish(ledger);
        ledger.TryOpenWindow(ResidentialClass.Low, 1, 20, 1.5f);
        Check(!ledger.TryReserve(context, 1, Spec(index: 1), existing, 8, out nested), "cross-native-category gain3Q fails one-person4Q minimum");
        ledger.Abandon(existing, 8); Publish(ledger);
        Check(!ledger.TryReserve(context, 1, Spec(index: 1, width: 4, length: 4), existing, 8, out nested), "unavailable predecessor cannot create fictitious supply credit");
    }
    private static void PredecessorCancellation()
    {
        ResidentialCapacityLedger ledger = New(); object context = new object(); ResidentialLotIdentity old = Identity(1);
        ledger.Import(Record(old, 1)); Publish(ledger); ledger.TryOpenWindow(ResidentialClass.Low, 1, 20, 1.5f);
        ResidentialCapacityReservation token = Reserve(ledger, context, Spec(index: 1, width: 4, length: 4), old, 1);
        Check(ledger.Cancel(token, context, 1), "selection failure before demolition cancels"); Stock(ledger, 13);
        token = Reserve(ledger, context, Spec(index: 1, width: 4, length: 4), old, 1);
        ledger.Release(old, 1); Check(ledger.Cancel(token, context, 1), "failed native creation after demolition retires hold"); Stock(ledger, 0);
        ResidentialCapacityRecord record; Check(!ledger.TryGet(old, 1, out record), "cancel after demolition retires tombstone without resurrecting predecessor");
        Equal(0L, ledger.GetWindow(ResidentialClass.Low).CommittedQ, "failed replacement does not invent expenditure");
        ResidentialCapacityLedgerState state; Check(ledger.TryCapture(out state), "failure state captures without reservation commands");
    }
    private static void RestoreAndReplay()
    {
        ResidentialCapacityLedger ledger = New(epoch: 4, firstDay: 10); ledger.Import(Record(Identity(1), 1)); Publish(ledger);
        Check(!ledger.TryOpenWindow(ResidentialClass.Low, 9, 20, 1.5f), "existing-city baseline waits required first day");
        Check(ledger.TryOpenWindow(ResidentialClass.Low, 10, 20, 1.5f), "required day opens");
        ResidentialCapacityLedgerState state; Check(ledger.TryCapture(out state), "capture stable state");
        ResidentialCapacityLedger restored = ResidentialCapacityLedger.Restore(ClassGuid, state, 5);
        Check(!restored.Snapshot().PublicationCurrent, "new graph must acknowledge restored stock"); Stock(restored, 13);
        Check(!restored.TryOpenWindow(ResidentialClass.Low, 11, 20, 1.5f), "no restored authority from old graph acknowledgment");
        Publish(restored);
        Check(!restored.TryOpenWindow(ResidentialClass.Low, 10, 20, 1.5f), "reload cannot reset same-day watermark");
        Check(!restored.TryOpenWindow(ResidentialClass.Low, 8, 20, 1.5f), "reload cannot exploit rewind");
        Check(restored.TryOpenWindow(ResidentialClass.Low, 11, 20, 1.5f), "strict next day may open");
        Check(!restored.AcknowledgePublication(999, 0), "unrelated graph revision rejected");
        Equal(0, restored.Snapshot().PendingReservations, "restore recreates no token/native command");
    }
    private static void ExternalRevisions()
    {
        ResidentialCapacityLedger ledger = New(); object context = new object(); ledger.TryOpenWindow(ResidentialClass.Low, 1, 20, 1.5f);
        ResidentialCapacityReservation token = Reserve(ledger, context, Spec());
        Check(ledger.AdvanceExternalRevision(7), "external revision advances");
        Equal(0L, ledger.Snapshot().StockRevision, "external inputs do not masquerade as stock mutation");
        Check(!ledger.AdvanceExternalRevision(7), "same external revision idempotent");
        Check(!ledger.AdvanceExternalRevision(6), "stale external revision ignored");
        Equal(ResidentialCapacityWindowPhase.Invalidated, ledger.GetWindow(ResidentialClass.Low).Phase, "external change closes old target");
        Check(ledger.CommitCreated(token, context, 1, Record(Identity(1), 1)), "already physical attempt still accounted after input change");
        Equal(ResidentialCapacityWindowPhase.Invalidated, ledger.GetWindow(ResidentialClass.Low).Phase, "commit does not reopen invalidated window");
        Publish(ledger); Check(!ledger.TryOpenWindow(ResidentialClass.Low, 1, 20, 1.5f), "input/publication changes do not renew day");
        ResidentialCapacityReservation rejected; Check(!ledger.TryReserve(context, 1, Spec(), null, 0, out rejected), "closed window rejects reentrant spend");
        Check(ledger.TryOpenWindow(ResidentialClass.Low, 2, 20, 1.5f), "next day opens with new external revision");
    }
    private static ResidentialCapacityLedgerState State(ResidentialCapacityRecord[] records, ResidentialCapacityWindow[] windows = null,
        long stockRevision = 1, long externalRevision = 0, byte[] hash = null, Guid? guid = null)
    { return new ResidentialCapacityLedgerState(guid ?? ClassGuid, ResidentialCapacityLedger.PolicyName, ResidentialCapacityLedger.PolicyVersion, ResidentialCapacityLedger.PolicyHash,
        ResidentialNominalCapacity.PolicyName, ResidentialNominalCapacity.PolicyVersion,
        hash ?? ResidentialNominalCapacity.PolicyHash, 0, stockRevision, externalRevision, -1, -1, records, windows ?? new ResidentialCapacityWindow[3]); }
    private static void OpenAll(ResidentialCapacityLedger ledger, long day)
    { foreach (ResidentialClass lotClass in new[] { ResidentialClass.Low, ResidentialClass.Medium, ResidentialClass.High }) Check(ledger.TryOpenWindow(lotClass, day, 20, 1.5f), "open each class day"); }
    private static void AllWindows(ResidentialCapacityLedger ledger, ResidentialCapacityWindowPhase phase)
    { foreach (ResidentialClass lotClass in new[] { ResidentialClass.Low, ResidentialClass.Medium, ResidentialClass.High }) Equal(phase, ledger.GetWindow(lotClass).Phase, "cross-segment stock mutation closes all active targets"); }
    private static void ExternalStockChanges()
    {
        ResidentialCapacityLedger ledger = New(); OpenAll(ledger, 1);
        ResidentialLotIdentity old = Identity(1); ResidentialCapacityRecord record = Record(old, 1);
        ledger.Import(record); AllWindows(ledger, ResidentialCapacityWindowPhase.Invalidated);
        Equal(0L, ledger.Snapshot().ExternalRevision, "stock import does not invent an external-input revision");
        Publish(ledger); Check(!ledger.TryOpenWindow(ResidentialClass.Low, 1, 100, 1.5f), "ack after import cannot reopen same day");
        OpenAll(ledger, 2); long revision = ledger.Snapshot().StockRevision;
        Check(!ledger.Import(record), "duplicate import remains idempotent");
        Equal(revision, ledger.Snapshot().StockRevision, "duplicate import no revision"); AllWindows(ledger, ResidentialCapacityWindowPhase.Open);
        ResidentialLotIdentity upgraded = Identity(1, prefab: "Upgrade", width: 4, length: 4);
        Check(ledger.RefreshUpgrade(old, 1, Record(upgraded, 1, homes: 3)), "external verified capacity upgrade");
        AllWindows(ledger, ResidentialCapacityWindowPhase.Invalidated); Stock(ledger, 39);
        Equal(0L, ledger.Snapshot().ExternalRevision, "upgrade is a stock revision, not a fabricated input epoch");
        Publish(ledger); OpenAll(ledger, 3);
        revision = ledger.Snapshot().StockRevision;
        Check(!ledger.RefreshUpgrade(upgraded, 1, Record(upgraded, 1, homes: 3)), "duplicate upgrade remains idempotent");
        Equal(revision, ledger.Snapshot().StockRevision, "duplicate upgrade no revision"); AllWindows(ledger, ResidentialCapacityWindowPhase.Open);
        ledger.Abandon(upgraded, 1); Check(!ledger.Snapshot().PublicationCurrent, "withdrawal invalidates publication before another spend");
        Equal(0L, ledger.GetWindow(ResidentialClass.Low).CommittedQ, "withdrawal never creates negative expenditure or quota refund");
        Publish(ledger);
        Check(ledger.Reoccupy(upgraded, 1, ResidentialCapacityPhase.Completed), "effective reoccupation");
        AllWindows(ledger, ResidentialCapacityWindowPhase.Invalidated); Stock(ledger, 39);
        Publish(ledger); OpenAll(ledger, 4); revision = ledger.Snapshot().StockRevision;
        Check(!ledger.Reoccupy(upgraded, 1, ResidentialCapacityPhase.Completed), "duplicate reoccupation remains idempotent");
        Equal(revision, ledger.Snapshot().StockRevision, "duplicate reoccupation no revision"); AllWindows(ledger, ResidentialCapacityWindowPhase.Open);
    }
    private static void TransactionalRestoreFailures()
    {
        ResidentialCapacityRecord record = Record(Identity(1), 1);
        Throws<ArgumentException>(delegate { ResidentialCapacityLedger.Restore(Guid.NewGuid(), State(new[] { record }), 1); }, "cross-city state rejected");
        Throws<ArgumentException>(delegate { State(new[] { record }, hash: new byte[32]); }, "unknown profile rejected");
        Throws<ArgumentException>(delegate { new ResidentialCapacityLedgerState(ClassGuid, ResidentialCapacityLedger.PolicyName, 1, new byte[32], ResidentialNominalCapacity.PolicyName, 1,
            ResidentialNominalCapacity.PolicyHash, 0, 0, 0, -1, -1, new ResidentialCapacityRecord[0], new ResidentialCapacityWindow[3]); }, "unknown accounting policy rejected separately");
        Throws<ArgumentException>(delegate { ResidentialCapacityLedger.Restore(ClassGuid, State(new[] { record, record }), 1); }, "duplicate IDs rejected transactionally");
        Throws<ArgumentException>(delegate { ResidentialCapacityLedger.Restore(ClassGuid, State(new[] { record, Record(Identity(2), 1) }), 1); }, "duplicate generations rejected");
        Throws<ArgumentException>(delegate { ResidentialCapacityLedger.Restore(ClassGuid, State(new[] { Record(Identity(2), 2, guid: Guid.NewGuid()) }), 1); }, "record class GUID mismatch rejected");
        ResidentialCapacityWindow window = new ResidentialCapacityWindow(ResidentialClass.High, 1, ResidentialCapacityWindowPhase.Open, 20, 1.5f, 80, 120, 0, 0, 0);
        Throws<ArgumentException>(delegate { ResidentialCapacityLedger.Restore(ClassGuid, State(new[] { record }, new[] { window, null, null }), 1); }, "window class slot mismatch rejected");
        window = new ResidentialCapacityWindow(ResidentialClass.Low, 1, ResidentialCapacityWindowPhase.Open, 20, 1.5f, 80, 120, 0, 10, 0);
        Throws<ArgumentException>(delegate { ResidentialCapacityLedger.Restore(ClassGuid, State(new[] { record }, new[] { window, null, null }), 1); }, "future opening stock revision rejected");
        window = new ResidentialCapacityWindow(ResidentialClass.Low, 1, ResidentialCapacityWindowPhase.Open, 20, 1.5f, 80, 120, 0, 0, 0);
        Throws<ArgumentException>(delegate { ResidentialCapacityLedger.Restore(ClassGuid, State(new[] { record }, new[] { window, null, null }, externalRevision: 1), 1); }, "stale external window rejected");
        Throws<ArgumentException>(delegate { new ResidentialCapacityWindow(ResidentialClass.Low, 1, ResidentialCapacityWindowPhase.Open, 20, 1.5f, 80, 120, 80, 0, 0); }, "open exhausted target malformed");
        Throws<ArgumentException>(delegate { new ResidentialCapacityWindow(ResidentialClass.Low, 1, ResidentialCapacityWindowPhase.Invalidated, 5, 1.5f, 0, 0, 5, 0, 0); }, "unqualified forged expenditure rejected");
    }
    private static void ArithmeticAndMutationIsolation()
    {
        ResidentialCapacityLedger ledger = New();
        Throws<ArgumentOutOfRangeException>(delegate { ledger.TryOpenWindow(ResidentialClass.Low, 1, float.NaN, 1.5f); }, "NaN does not poison day watermark");
        Throws<ArgumentOutOfRangeException>(delegate { ledger.TryOpenWindow(ResidentialClass.Low, 1, 20, 0.9f); }, "unverified negative overbuild domain rejected");
        Throws<ArgumentOutOfRangeException>(delegate { ledger.TryOpenWindow(ResidentialClass.Low, 1, float.MaxValue, 1.5f); }, "RAW integer overflow rejected");
        Check(ledger.TryOpenWindow(ResidentialClass.Low, 1, 20, 1.5f), "invalid input left day available");
        ledger.Import(Record(Identity(1), 1)); Publish(ledger);
        ResidentialCapacityLedgerState state; ledger.TryCapture(out state);
        byte[] hash = state.ProfileHash; hash[0] ^= 255;
        byte[] accountingHash = state.AccountingPolicyHash; accountingHash[0] ^= 255;
        ResidentialCapacityRecord[] records = state.Records; records[0] = null;
        ResidentialCapacityWindow[] windows = state.Windows; windows[0] = null;
        Check(state.Records[0] != null && state.Windows[0] != null, "state arrays cloned on output");
        Check(state.ProfileHash[0] != hash[0], "state profile hash cloned");
        Check(state.AccountingPolicyHash[0] != accountingHash[0], "state accounting hash cloned separately");
        Check(ResidentialCapacityLedger.PolicyName != ResidentialNominalCapacity.PolicyName, "capacity profile is not accounting policy");
        ResidentialCapacityLedgerSnapshot snapshot = ledger.Snapshot(); long[] stock = snapshot.StockQ; stock[0] = 999;
        Equal(13L, snapshot.StockQ[0], "diagnostic stock immutable");
        ledger.Release(Identity(1), 1); Equal(13L, snapshot.StockQ[0], "old snapshot isolated from future mutation");
        ResidentialCapacityRecord[] input = { Record(Identity(2), 2) }; ResidentialCapacityLedgerState supplied = State(input); input[0] = null;
        Stock(ResidentialCapacityLedger.Restore(ClassGuid, supplied, 2), 13);
        ResidentialCapacityLedger fullRevision = ResidentialCapacityLedger.Restore(ClassGuid, State(new ResidentialCapacityRecord[0], stockRevision: long.MaxValue), 4);
        Throws<OverflowException>(delegate { fullRevision.Import(Record(Identity(1), 1)); }, "revision exhaustion cannot partially import");
        Equal(0, fullRevision.Snapshot().Records, "failed revision import leaves no record"); Stock(fullRevision, 0);
    }
    private static void CyclicConservation()
    {
        ResidentialCapacityLedger ledger = New();
        for (ulong cycle = 1; cycle <= 200; cycle++)
        {
            ResidentialLotIdentity identity = Identity(8, index: (uint)cycle);
            ledger.Import(Record(identity, cycle)); ledger.Complete(identity, cycle); ledger.Abandon(identity, cycle);
            Check(!ledger.Abandon(identity, cycle), "repeat withdrawal never makes negative stock");
            ledger.Reoccupy(identity, cycle, ResidentialCapacityPhase.Completed); ledger.Release(identity, cycle);
            Equal(0L, ledger.Snapshot().StockQ[0], "import-complete-abandon-reoccupy-release conserves exact Q");
        }
        Equal(0, ledger.Snapshot().Records, "reused IDs leave no retained generations after final release");
    }
    private static void Check(bool condition, string name) { _checks++; if (!condition) throw new Exception(name); }
    private static void Equal<T>(T expected, T actual, string name) { _checks++; if (!object.Equals(expected, actual)) throw new Exception(name + ": expected " + expected + ", got " + actual); }
    private static void Throws<T>(Action action, string name) where T : Exception
    { _checks++; try { action(); } catch (T) { return; } throw new Exception(name + ": expected " + typeof(T).Name); }
}
