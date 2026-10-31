using System;
using System.Threading;

namespace SC4Evolution.Mod.Economy
{
    internal enum ResidentialDemandKind { None, Construction, Incoming }

    internal sealed class IncomingDemandSnapshot
    {
        internal readonly object Owner;
        internal readonly int Session, CurrentDepth;
        internal readonly bool BindingValid, LastAggregated;
        internal readonly long ConstructionEntries, IncomingEntries, AggregatedCallbacks, PreservedCallbacks, Finalizers, Mismatches;
        internal readonly ResidentialDemandKind LastKind;
        internal readonly int LastInput, LastReturn;
        internal readonly int LastCallbackThreadId, LastFinalizerThreadId;
        internal IncomingDemandSnapshot(object owner, int session, bool valid, int depth, long construction, long incoming,
            long aggregated, long preserved, long finalizers, long mismatches, ResidentialDemandKind kind, int input, int result, bool lastAggregated,
            int callbackThreadId, int finalizerThreadId)
        { Owner = owner; Session = session; BindingValid = valid; CurrentDepth = depth; ConstructionEntries = construction; IncomingEntries = incoming;
            AggregatedCallbacks = aggregated; PreservedCallbacks = preserved; Finalizers = finalizers; Mismatches = mismatches;
            LastKind = kind; LastInput = input; LastReturn = result; LastAggregated = lastAggregated;
            LastCallbackThreadId = callbackThreadId; LastFinalizerThreadId = finalizerThreadId; }
    }

    // One immutable owner/epoch identity, with explicit invalidation before unbind.
    // Only bounded counters/latest primitives are retained, never an attempt log.
    internal sealed class IncomingDemandBinding
    {
        internal readonly object Owner;
        internal readonly int Session;
        private volatile bool _valid = true;
        private readonly object _sync = new object();
        private int _input, _result, _callbackThreadId, _finalizerThreadId;
        private long _construction, _incoming, _aggregated, _preserved, _finalizers, _mismatches;
        private ResidentialDemandKind _kind;
        private bool _lastAggregated;
        internal IncomingDemandBinding(object owner, int session)
        { if (owner == null) throw new ArgumentNullException("owner"); Owner = owner; Session = session; }
        internal bool IsValid { get { return _valid; } }
        internal void Invalidate() { _valid = false; }
        internal void Enter(ResidentialDemandKind kind, int depth)
        { lock (_sync) { if (kind == ResidentialDemandKind.Construction) Increment(ref _construction); else Increment(ref _incoming); } }
        internal void Leave(int depth, bool matched)
        { lock (_sync) { Increment(ref _finalizers); if (!matched) Increment(ref _mismatches); _finalizerThreadId = Thread.CurrentThread.ManagedThreadId; } }
        internal void Callback(ResidentialDemandKind kind, int input, int result, bool aggregated)
        {
            lock (_sync)
            {
                if (aggregated) Increment(ref _aggregated); else Increment(ref _preserved);
                _kind = kind; _input = input; _result = result; _lastAggregated = aggregated;
                _callbackThreadId = Thread.CurrentThread.ManagedThreadId;
            }
        }
        internal IncomingDemandSnapshot Snapshot()
        { lock (_sync) { return new IncomingDemandSnapshot(Owner, Session, _valid, IncomingResidentDemandContext.CurrentDepth(this), _construction, _incoming,
            _aggregated, _preserved, _finalizers, _mismatches, _kind, _input, _result, _lastAggregated, _callbackThreadId, _finalizerThreadId); } }
        private static void Increment(ref long value) { if (value != long.MaxValue) value++; }
    }

    // No Harmony/game dependencies: the same helper is exercised by the isolated harness.
    internal static class IncomingResidentDemandContext
    {
        internal sealed class Frame
        {
            internal readonly IncomingDemandBinding Binding;
            internal readonly ResidentialDemandKind Kind;
            internal readonly Frame Previous;
            internal readonly int Depth;
            internal Frame(IncomingDemandBinding binding, ResidentialDemandKind kind, Frame previous)
            { Binding = binding; Kind = kind; Previous = previous; Depth = previous == null ? 1 : checked(previous.Depth + 1); }
        }
        [ThreadStatic] private static Frame _current;
        // The caller's TLS scope, never another thread's last observed depth.
        internal static int CurrentDepth(IncomingDemandBinding binding)
        { Frame frame = _current; return frame != null && ReferenceEquals(frame.Binding, binding) ? frame.Depth : 0; }
        internal static Frame Enter(IncomingDemandBinding binding, ResidentialDemandKind kind)
        {
            if (kind != ResidentialDemandKind.Construction && kind != ResidentialDemandKind.Incoming) throw new ArgumentOutOfRangeException("kind");
            Frame frame = new Frame(binding, kind, _current);
            _current = frame;
            if (binding != null) binding.Enter(kind, frame.Depth);
            return frame;
        }
        internal static Exception Finish(Exception error, Frame frame)
        {
            bool matched = frame != null && ReferenceEquals(_current, frame);
            // A mismatched/double/cross-thread finalizer must never restore authority.
            _current = matched ? frame.Previous : null;
            if (frame != null && frame.Binding != null)
                frame.Binding.Leave(_current != null && ReferenceEquals(_current.Binding, frame.Binding) ? _current.Depth : 0, matched);
            return error;
        }
        internal static bool CanAggregate(IncomingDemandBinding binding, object owner, int session)
        {
            Frame frame = _current;
            return frame != null && frame.Kind == ResidentialDemandKind.Construction && binding != null && binding.IsValid &&
                ReferenceEquals(frame.Binding, binding) && ReferenceEquals(binding.Owner, owner) && binding.Session == session;
        }
        internal static void RecordCallback(IncomingDemandBinding binding, int input, int result, bool aggregated)
        {
            if (binding == null) return;
            Frame frame = _current;
            ResidentialDemandKind kind = frame != null && ReferenceEquals(frame.Binding, binding) ? frame.Kind : ResidentialDemandKind.None;
            binding.Callback(kind, input, result, aggregated);
        }
    }
}
