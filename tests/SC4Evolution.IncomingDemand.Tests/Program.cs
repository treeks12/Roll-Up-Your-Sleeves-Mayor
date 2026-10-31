using System;
using System.Threading;
using SC4Evolution.Mod.Economy;

internal static class Program
{
    private static int _checks;
    private static readonly EconomicDemandExtension Extension = new EconomicDemandExtension();
    private static int Main()
    {
        try
        {
            NestedContexts(); ExceptionsAndMismatch(); BindingAndEpoch(); OtherThread(); ExtensionOrderAndReentrancy(); FallbackAndDiagnosticBounds(); CapacityModePassThrough();
            Console.WriteLine("PASS: " + _checks + " assertions across 7 isolated incoming-demand scenarios; actual context/extension source, explicit game adapters, no native execution.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); return 1; }
    }
    private static void NestedContexts()
    {
        EconomicBridge owner = new EconomicBridge(1); EconomicDemandExtension.Bind(owner);
        Equal(91, Extension.OnCalculateResidentialDemand(91), "outside calculators native");
        IncomingResidentDemandContext.Frame construction = IncomingResidentDemandContext.Enter(owner.DemandContextBinding, ResidentialDemandKind.Construction);
        Equal(1, Extension.OnCalculateResidentialDemand(91), "construction uses unchanged normalized mapping");
        IncomingResidentDemandContext.Frame incoming = IncomingResidentDemandContext.Enter(owner.DemandContextBinding, ResidentialDemandKind.Incoming);
        Equal(77, Extension.OnCalculateResidentialDemand(77), "inner incoming overrides outer construction authority");
        Equal(2, owner.DemandContextBinding.Snapshot().CurrentDepth, "nested depth");
        IncomingResidentDemandContext.Finish(null, incoming);
        Equal(1, Extension.OnCalculateResidentialDemand(77), "outer construction restored");
        IncomingResidentDemandContext.Finish(null, construction);
        Equal(77, Extension.OnCalculateResidentialDemand(77), "outer finalizer leaves no authority");
        Equal(0, owner.DemandContextBinding.Snapshot().CurrentDepth, "depth restored to zero");

        incoming = IncomingResidentDemandContext.Enter(owner.DemandContextBinding, ResidentialDemandKind.Incoming);
        Equal(88, Extension.OnCalculateResidentialDemand(88), "incoming preserves input");
        construction = IncomingResidentDemandContext.Enter(owner.DemandContextBinding, ResidentialDemandKind.Construction);
        Equal(1, Extension.OnCalculateResidentialDemand(88), "inner construction uses own tag");
        IncomingResidentDemandContext.Finish(null, construction);
        Equal(88, Extension.OnCalculateResidentialDemand(88), "incoming restored after inner construction");
        IncomingResidentDemandContext.Finish(null, incoming);
        IncomingDemandSnapshot stats = owner.DemandContextBinding.Snapshot();
        Equal(2L, stats.ConstructionEntries, "construction entries"); Equal(2L, stats.IncomingEntries, "incoming entries");
        Equal(4L, stats.Finalizers, "finalizers"); Equal(0L, stats.Mismatches, "ordinary nesting matches");
        Equal(3L, stats.AggregatedCallbacks, "aggregated count"); Equal(5L, stats.PreservedCallbacks, "preserved count");
        Equal(ResidentialDemandKind.Incoming, stats.LastKind, "last callback kind"); Equal(88, stats.LastInput, "last input"); Equal(88, stats.LastReturn, "last return");
        Equal(false, stats.LastAggregated, "incoming never marked aggregated");
    }
    private static void ExceptionsAndMismatch()
    {
        EconomicBridge owner = new EconomicBridge(2); EconomicDemandExtension.Bind(owner);
        IncomingResidentDemandContext.Frame outer = IncomingResidentDemandContext.Enter(owner.DemandContextBinding, ResidentialDemandKind.Construction);
        IncomingResidentDemandContext.Frame inner = IncomingResidentDemandContext.Enter(owner.DemandContextBinding, ResidentialDemandKind.Incoming);
        Exception original = new InvalidOperationException("original native failure");
        Equal(true, ReferenceEquals(original, IncomingResidentDemandContext.Finish(original, inner)), "finalizer preserves exact exception");
        Equal(1, Extension.OnCalculateResidentialDemand(80), "exception restores outer tag");
        IncomingResidentDemandContext.Finish(null, outer);
        Equal(80, Extension.OnCalculateResidentialDemand(80), "exception scope fully cleaned");
        outer = IncomingResidentDemandContext.Enter(owner.DemandContextBinding, ResidentialDemandKind.Construction);
        inner = IncomingResidentDemandContext.Enter(owner.DemandContextBinding, ResidentialDemandKind.Incoming);
        IncomingResidentDemandContext.Finish(null, outer); // Deliberately wrong LIFO: fail native.
        Equal(80, Extension.OnCalculateResidentialDemand(80), "mismatch cannot restore authority");
        IncomingResidentDemandContext.Finish(null, inner);
        Equal(80, Extension.OnCalculateResidentialDemand(80), "later stale finalizer remains native");
        Equal(2L, owner.DemandContextBinding.Snapshot().Mismatches, "mismatches diagnosed");
        IncomingResidentDemandContext.Finish(null, null);
        Equal(80, Extension.OnCalculateResidentialDemand(80), "null state cannot leak authority");
    }
    private static void BindingAndEpoch()
    {
        EconomicBridge first = new EconomicBridge(3), second = new EconomicBridge(4);
        EconomicDemandExtension.Bind(first);
        IncomingResidentDemandContext.Frame frame = IncomingResidentDemandContext.Enter(first.DemandContextBinding, ResidentialDemandKind.Construction);
        Equal(1, Extension.OnCalculateResidentialDemand(90), "first owner controls own scope");
        EconomicDemandExtension.Bind(second);
        Equal(90, Extension.OnCalculateResidentialDemand(90), "second owner cannot inherit first scope");
        EconomicDemandExtension.Unbind(first);
        Equal(true, ReferenceEquals(second, EconomicDemandExtension.Active), "old unbind cannot remove new owner");
        EconomicDemandExtension.Bind(first); first.DemandContextBinding.Invalidate();
        Equal(90, Extension.OnCalculateResidentialDemand(90), "invalidated session cannot control");
        IncomingDemandBinding old = first.DemandContextBinding;
        first.DemandContextSession = 5; first.DemandContextBinding = new IncomingDemandBinding(first, 5);
        Equal(90, Extension.OnCalculateResidentialDemand(90), "same owner rebinding cannot reuse stale frame");
        IncomingResidentDemandContext.Finish(null, frame);
        frame = IncomingResidentDemandContext.Enter(first.DemandContextBinding, ResidentialDemandKind.Construction);
        first.DemandContextSession = 6;
        Equal(90, Extension.OnCalculateResidentialDemand(90), "epoch mismatch fails native");
        first.DemandContextSession = 5;
        Equal(1, Extension.OnCalculateResidentialDemand(90), "valid own binding still works");
        EconomicDemandExtension.Unbind(first);
        Equal(90, Extension.OnCalculateResidentialDemand(90), "unbound callback native");
        IncomingResidentDemandContext.Finish(null, frame);
        Equal(false, old.Snapshot().BindingValid, "old diagnostic identity remains invalid");
        EconomicDemandExtension.Bind(second);
        frame = IncomingResidentDemandContext.Enter(second.DemandContextBinding, ResidentialDemandKind.Construction);
        second.DuringCanUse = delegate { second.DemandContextBinding.Invalidate(); };
        Equal(90, Extension.OnCalculateResidentialDemand(90), "invalidation during calculation restores original input");
        IncomingResidentDemandContext.Finish(null, frame);
        EconomicBridge third = new EconomicBridge(6); EconomicDemandExtension.Bind(third);
        frame = IncomingResidentDemandContext.Enter(third.DemandContextBinding, ResidentialDemandKind.Construction);
        third.DuringCanUse = delegate { EconomicDemandExtension.Bind(first); };
        Equal(90, Extension.OnCalculateResidentialDemand(90), "rebinding during calculation restores original input");
        IncomingResidentDemandContext.Finish(null, frame);
    }
    private static void OtherThread()
    {
        EconomicBridge owner = new EconomicBridge(7); EconomicDemandExtension.Bind(owner);
        IncomingResidentDemandContext.Frame outer = IncomingResidentDemandContext.Enter(owner.DemandContextBinding, ResidentialDemandKind.Construction);
        Exception threadError = null;
        int probeThreadId = Thread.CurrentThread.ManagedThreadId;
        int foreignThreadId = 0;
        Thread thread = new Thread(delegate()
        {
            try
            {
                foreignThreadId = Thread.CurrentThread.ManagedThreadId;
                Equal(0, owner.DemandContextBinding.Snapshot().CurrentDepth, "foreign snapshot cannot inherit caller depth");
                Equal(83, Extension.OnCalculateResidentialDemand(83), "other thread cannot inherit construction");
                IncomingResidentDemandContext.Frame inner = IncomingResidentDemandContext.Enter(owner.DemandContextBinding, ResidentialDemandKind.Incoming);
                try { Equal(1, owner.DemandContextBinding.Snapshot().CurrentDepth, "foreign own scope depth"); Equal(83, Extension.OnCalculateResidentialDemand(83), "other thread own incoming native"); }
                finally { IncomingResidentDemandContext.Finish(null, inner); }
                Equal(0, owner.DemandContextBinding.Snapshot().CurrentDepth, "foreign scope cleaned independently");
            }
            catch (Exception error) { threadError = error; }
        });
        thread.Start(); thread.Join(); if (threadError != null) throw threadError;
        IncomingDemandSnapshot foreign = owner.DemandContextBinding.Snapshot();
        Equal(1, foreign.CurrentDepth, "caller snapshot retains its own active scope");
        Equal(ResidentialDemandKind.Incoming, foreign.LastKind, "foreign event can otherwise look like incoming proof");
        Equal(foreignThreadId, foreign.LastCallbackThreadId, "callback carries actual foreign thread");
        Equal(foreignThreadId, foreign.LastFinalizerThreadId, "finalizer carries actual foreign thread");
        Equal(false, foreign.LastCallbackThreadId == probeThreadId, "foreign callback cannot satisfy caller proof");
        IncomingResidentDemandContext.Frame suppressed = IncomingResidentDemandContext.Enter(owner.DemandContextBinding, ResidentialDemandKind.Incoming);
        IncomingResidentDemandContext.Finish(null, suppressed); // Simulate another extension suppressing our callback.
        IncomingDemandSnapshot missingCallback = owner.DemandContextBinding.Snapshot();
        Equal(probeThreadId, missingCallback.LastFinalizerThreadId, "caller finalizer observed without callback");
        Equal(foreignThreadId, missingCallback.LastCallbackThreadId, "foreign callback remains distinguishable after caller finalizer");
        Equal(false, missingCallback.LastCallbackThreadId == probeThreadId && missingCallback.LastFinalizerThreadId == probeThreadId, "mixed-thread events cannot prove caller pass-through");
        Equal(1, Extension.OnCalculateResidentialDemand(83), "other thread did not remove original authority");
        IncomingResidentDemandContext.Finish(null, outer);
        IncomingDemandSnapshot local = owner.DemandContextBinding.Snapshot();
        Equal(probeThreadId, local.LastCallbackThreadId, "actual caller callback identifies thread");
        Equal(probeThreadId, local.LastFinalizerThreadId, "actual caller finalizer identifies thread");
        Equal(0, local.CurrentDepth, "caller finally leaves zero TLS depth");
    }
    private static void ExtensionOrderAndReentrancy()
    {
        EconomicBridge owner = new EconomicBridge(8); EconomicDemandExtension.Bind(owner);
        IncomingResidentDemandContext.Frame incoming = IncomingResidentDemandContext.Enter(owner.DemandContextBinding, ResidentialDemandKind.Incoming);
        int before = 10 + 7;
        int ours = Extension.OnCalculateResidentialDemand(before);
        int after = ours * 2;
        Equal(17, ours, "incoming preserves previous extension result"); Equal(34, after, "later extension can still transform result");
        IncomingResidentDemandContext.Frame construction = IncomingResidentDemandContext.Enter(owner.DemandContextBinding, ResidentialDemandKind.Construction);
        // Another extension reenters the incoming calculator while construction is active.
        IncomingResidentDemandContext.Frame reentrant = IncomingResidentDemandContext.Enter(owner.DemandContextBinding, ResidentialDemandKind.Incoming);
        try { Equal(123, Extension.OnCalculateResidentialDemand(123), "reentrant incoming preserves result"); }
        finally { IncomingResidentDemandContext.Finish(null, reentrant); }
        Equal(2, Extension.OnCalculateResidentialDemand(before) * 2, "construction override remains in wrapper order");
        IncomingResidentDemandContext.Finish(null, construction);
        Equal(34, Extension.OnCalculateResidentialDemand(before) * 2, "outer incoming still preserved after reentry");
        IncomingResidentDemandContext.Finish(null, incoming);
    }
    private static void FallbackAndDiagnosticBounds()
    {
        EconomicBridge owner = new EconomicBridge(9); EconomicDemandExtension.Bind(owner);
        IncomingDemandSnapshot before = owner.DemandContextBinding.Snapshot();
        IncomingResidentDemandContext.Frame frame = IncomingResidentDemandContext.Enter(owner.DemandContextBinding, ResidentialDemandKind.Construction);
        owner.Published.Valid = false; Equal(75, Extension.OnCalculateResidentialDemand(75), "invalid snapshot preserves input");
        owner.Published.Valid = true; owner.Published.Missing = true; Equal(75, Extension.OnCalculateResidentialDemand(75), "missing key preserves input");
        owner.Published.Missing = false;
        Equal(1, Extension.OnCalculateResidentialDemand(75), "valid construction unchanged");
        IncomingResidentDemandContext.Finish(null, frame);
        Equal(1, Extension.OnCalculateCommercialDemand(75), "commercial unchanged outside residential scope");
        Equal(1, Extension.OnCalculateWorkplaceDemand(75), "workplace unchanged outside residential scope");
        Equal(23, Extension.OnUpdateDemand(2, 23, 99), "update demand unchanged");
        foreach (int input in new[] { int.MinValue, -1, 0, 1, 100, int.MaxValue })
        {
            frame = IncomingResidentDemandContext.Enter(owner.DemandContextBinding, ResidentialDemandKind.Incoming);
            try { Equal(input, Extension.OnCalculateResidentialDemand(input), "incoming exact input preservation"); }
            finally { IncomingResidentDemandContext.Finish(null, frame); }
        }
        IncomingDemandSnapshot after = owner.DemandContextBinding.Snapshot();
        Equal(0L, before.IncomingEntries, "published diagnostic is immutable"); Equal(6L, after.IncomingEntries, "bounded aggregate count");
        Equal(0, after.CurrentDepth, "final diagnostic depth"); Equal(0L, after.Mismatches, "no mismatches");
        owner.DemandContextBinding = null;
        frame = IncomingResidentDemandContext.Enter(null, ResidentialDemandKind.Construction);
        Equal(75, Extension.OnCalculateResidentialDemand(75), "unavailable dependency binding cannot authorize override");
        IncomingResidentDemandContext.Finish(null, frame);
        // Demonstrate the native integer truncation being prevented, without game execution.
        foreach (int capacity in new[] { 62, 63 }) { Equal(0, 1 * capacity / 100, "old bridge1 zeroed incoming offers"); Equal(capacity, 100 * capacity / 100, "unmodified example input retains offers"); }
    }
    private static void CapacityModePassThrough()
    {
        EconomicBridge owner = new EconomicBridge(10); EconomicDemandExtension.Bind(owner);
        owner.Capacity = new ResidentialCapacityBridge { ModeRequested = true };
        IncomingResidentDemandContext.Frame construction = IncomingResidentDemandContext.Enter(owner.DemandContextBinding, ResidentialDemandKind.Construction);
        foreach (int input in new[] { int.MinValue, -1, 0, 1, 100, int.MaxValue })
            Equal(input, Extension.OnCalculateResidentialDemand(input), "capacity admission preserves original construction input before its own gate");
        IncomingResidentDemandContext.Frame incoming = IncomingResidentDemandContext.Enter(owner.DemandContextBinding, ResidentialDemandKind.Incoming);
        Equal(87, Extension.OnCalculateResidentialDemand(87), "capacity mode preserves nested incoming input");
        IncomingResidentDemandContext.Finish(null, incoming);
        Equal(0L, owner.DemandContextBinding.Snapshot().AggregatedCallbacks, "capacity mode never records a normalized override");
        Equal(7L, owner.DemandContextBinding.Snapshot().PreservedCallbacks, "capacity pass-through stays visible in diagnostics");
        owner.Capacity.ModeRequested = false;
        Equal(1, Extension.OnCalculateResidentialDemand(87), "legacy construction mapping remains available outside capacity mode");
        IncomingResidentDemandContext.Finish(null, construction);
        Equal(0, owner.DemandContextBinding.Snapshot().CurrentDepth, "capacity mode leaves no TLS scope behind");
    }
    private static void Equal<T>(T expected, T actual, string name)
    { _checks++; if (!object.Equals(expected, actual)) throw new Exception(name + ": expected " + expected + ", got " + actual); }
}
