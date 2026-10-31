using System;
using System.Threading;
using ICities;

namespace SC4Evolution.Mod.Economy
{
    /// <summary>Official aggregate compatibility output; this does not impose household eligibility.</summary>
    public sealed class EconomicDemandExtension : DemandExtensionBase
    {
        private static readonly object BindingLock = new object();
        private static volatile EconomicBridge _active;
        internal static EconomicBridge Active { get { return _active; } }
        internal static void Bind(EconomicBridge bridge) { lock (BindingLock) { WorkplaceDemandDiagnostics.Reset(); _active = bridge; } }
        internal static void Unbind(EconomicBridge bridge)
        { lock (BindingLock) { if (ReferenceEquals(_active, bridge)) { _active = null; WorkplaceDemandDiagnostics.Reset(); } } }
        public override int OnCalculateResidentialDemand(int originalDemand)
        {
            EconomicBridge bridge = _active;
            IncomingDemandBinding binding = bridge == null ? null : bridge.DemandContextBinding;
            bool capacityRequested = bridge != null && bridge.Capacity != null && bridge.Capacity.ModeRequested;
            bool allowed = bridge != null && !capacityRequested && IncomingResidentDemandContext.CanAggregate(binding, bridge, bridge.DemandContextSession);
            bool aggregated = false;
            int result = allowed ? Aggregate(originalDemand, new uint[] { 0x21010, 0x21020, 0x21030 }, bridge, out aggregated) : originalDemand;
            // Rebinding/unload may invalidate a captured scope while a callback runs.
            if (!ReferenceEquals(bridge, _active) || (allowed && !IncomingResidentDemandContext.CanAggregate(binding, bridge, bridge.DemandContextSession)))
            { result = originalDemand; aggregated = false; }
            IncomingResidentDemandContext.RecordCallback(binding, originalDemand, result, aggregated);
            return result;
        }
        public override int OnCalculateCommercialDemand(int originalDemand)
        { return Aggregate(originalDemand, new uint[] { 0x23110, 0x23120, 0x23130 }); }
        public override int OnCalculateWorkplaceDemand(int originalDemand)
        {
            bool aggregated;
            int result = Aggregate(originalDemand, new uint[] { 0x23320, 0x23330, 0x24100, 0x24200, 0x24300, 0x24400 }, _active, out aggregated);
            WorkplaceDemandDiagnostics.Record(originalDemand, result, aggregated);
            return result;
        }
        public override int OnUpdateDemand(int lastDemand, int nextDemand, int targetDemand) { return nextDemand; }

        private static int Aggregate(int original, uint[] keys)
        { bool aggregated; return Aggregate(original, keys, _active, out aggregated); }
        private static int Aggregate(int original, uint[] keys, EconomicBridge bridge, out bool aggregated)
        {
            aggregated = false;
            SegmentedDemandSnapshot snapshot = bridge == null ? null : bridge.Published;
            if (bridge == null || !bridge.CanUseDemand(snapshot)) return original;
            int maximum = 0;
            foreach (uint key in keys)
            {
                int demand;
                if (!snapshot.TryGetDemand(key, out demand)) return original;
                maximum = Math.Max(maximum, demand);
            }
            // Keep the strongest positive segment as the shared native eligibility signal.
            // Per-lot filtering is separate; negative segments must not cancel positive peers.
            aggregated = true;
            return ToNativeDemand(maximum);
        }

        internal static int ToNativeDemand(int normalized)
        {
            if (normalized <= 0) return 0;
            // Positive segmented demand must remain eligible for the native RNG gate,
            // including the installed empty-city R$ bootstrap value of three.
            int bounded = Math.Min(1000, normalized);
            return Math.Max(1, (bounded + 5) / 10);
        }
    }

    /// <summary>Bounded read-only evidence for the native workplace-demand callback.
    /// It records the callback path and output without changing the native result.</summary>
    public static class WorkplaceDemandDiagnostics
    {
        private static int _callbacks, _aggregated, _lastInput, _lastReturn, _lastThread;
        public static int Callbacks { get { return Interlocked.CompareExchange(ref _callbacks, 0, 0); } }
        public static int Aggregated { get { return Interlocked.CompareExchange(ref _aggregated, 0, 0); } }
        public static int LastInput { get { return Interlocked.CompareExchange(ref _lastInput, 0, 0); } }
        public static int LastReturn { get { return Interlocked.CompareExchange(ref _lastReturn, 0, 0); } }
        public static int LastThread { get { return Interlocked.CompareExchange(ref _lastThread, 0, 0); } }
        internal static void Record(int input, int result, bool aggregated)
        {
            Interlocked.Increment(ref _callbacks);
            if (aggregated) Interlocked.Increment(ref _aggregated);
            Interlocked.Exchange(ref _lastInput, input);
            Interlocked.Exchange(ref _lastReturn, result);
            Interlocked.Exchange(ref _lastThread, Thread.CurrentThread.ManagedThreadId);
        }
        internal static void Reset()
        {
            Interlocked.Exchange(ref _callbacks, 0); Interlocked.Exchange(ref _aggregated, 0);
            Interlocked.Exchange(ref _lastInput, 0); Interlocked.Exchange(ref _lastReturn, 0); Interlocked.Exchange(ref _lastThread, 0);
        }
    }
}
