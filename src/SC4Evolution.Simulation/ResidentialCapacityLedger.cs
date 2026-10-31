using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace SC4Evolution.Simulation
{
    // No native authority is carried by this token. Only this ledger can construct it.
    public sealed class ResidentialCapacityReservation
    {
        internal readonly ResidentialCapacityLedger Owner;
        internal readonly object Context;
        internal readonly long Epoch;
        internal readonly ResidentialCapacityLotSpec Spec;
        internal readonly ResidentialCapacityRecord Predecessor;
        internal readonly ResidentialCapacityWindow Window;
        internal bool Used, NativeCreated, PredecessorReleased;
        public long ReservedQuotaQ { get; private set; }
        public long ReservedCapacityQ { get { return Spec.ReserveQ; } }
        internal ResidentialCapacityReservation(ResidentialCapacityLedger owner, object context, long epoch,
            ResidentialCapacityLotSpec spec, ResidentialCapacityRecord predecessor, ResidentialCapacityWindow window, long quota)
        { Owner = owner; Context = context; Epoch = epoch; Spec = spec; Predecessor = predecessor; Window = window; ReservedQuotaQ = quota; }
    }

    /// <summary>Pure instance accounting. Inputs must already be verified by the native adapter.
    /// Q is quarter nominal people, never a citizen count. No global BDS resource pool is implemented.</summary>
    public sealed class ResidentialCapacityLedger
    {
        public const string PolicyName = "ResidentialRawDailyAccountingV1";
        public const uint PolicyVersion = 1;
        public const string CanonicalPolicyMaterial =
            "SC4Evolution.ResidentialCapacityLedger;policy=ResidentialRawDailyAccountingV1;version=1" +
            ";nominal-profile=NativeArrivalNominalV1;profile-sha256=1b496dfd9c278bd5c405a9af514279c659a8c751e6bcf9a66316ac3af7e2d2ce" +
            ";unit=quarter-nominal-person;arithmetic=checked-int64;raw-domain=finite-truncated-int32;overbuild=caller-verified-finite-ge1" +
            ";window=one-per-class-effective-calendar-day-no-backlog;qualification=trunc(raw)>5;desiredQ=4*trunc(raw)" +
            ";desiredMaxQ=4*trunc(float32(trunc(raw))*overbuild);rewind-reload-publication=no-reopen-same-or-earlier-day" +
            ";reservation=opaque-single-use-ledger-context-epoch;precreate=Hmax;commit=Hactual;native-success=uncancellable-until-commit-or-reconcile" +
            ";replacement=same-class-net,cross-class-gross;same-class-lo=oldQ+4;hi=max(desiredMaxQ-heldQ+oldQ,lo+4)" +
            ";same-class-actual-envelope=minimum-ge-lo,maximum-le-hi;normal-floor-excess=at-most7Q-exhausted-only" +
            ";predecessor=active-before-demolition-retained-through-release-until-token-consumed" +
            ";new-lot-release=daily-quota-not-refunded;reconcile=account-real-stock-close-window-record-overrun" +
            ";supply=constructing-completed-upgrading;unavailable=retained-no-supply;completion=retain-supply" +
            ";publication=exact-stock-and-external-revisions;external-change=close-window-no-renewal" +
            ";effective-import-upgrade-reoccupy=close-all-open-windows-without-external-revision-change;idempotent-repeat=no-change" +
            ";save=no-inflight-or-unresolved;restore=no-tokens-requires-new-graph-ack;profile-class-guid-generation=validated" +
            ";native-authorization=none;native-fallback=caller-prospective-original-roll-no-rewind;global-BDS-resource-pool=not-implemented";
        private static readonly byte[] AccountingHash = HashPolicy();
        public static byte[] PolicyHash { get { return (byte[])AccountingHash.Clone(); } }
        private readonly object _sync = new object();
        private readonly SortedDictionary<ushort, ResidentialCapacityRecord> _records = new SortedDictionary<ushort, ResidentialCapacityRecord>();
        private readonly HashSet<ulong> _generations = new HashSet<ulong>();
        private readonly HashSet<ResidentialCapacityReservation> _pending = new HashSet<ResidentialCapacityReservation>();
        private readonly Dictionary<ushort, ResidentialCapacityReservation> _predecessors = new Dictionary<ushort, ResidentialCapacityReservation>();
        private readonly ResidentialCapacityWindow[] _windows = new ResidentialCapacityWindow[3];
        private readonly long[] _stock = new long[3], _reserved = new long[3];
        private readonly long _epoch, _firstDay;
        private bool _valid = true;
        private int _unresolved;
        private long _stockRevision, _externalRevision, _publishedStock = -1, _publishedExternal = -1;
        public Guid ClassLedgerId { get; private set; }
        public long Epoch { get { return _epoch; } }

        public ResidentialCapacityLedger(Guid classLedgerId, long epoch, long firstPlanningDay)
        {
            if (classLedgerId == Guid.Empty) throw new ArgumentException("Class ledger identity is required.", "classLedgerId");
            if (epoch < 0 || firstPlanningDay < 0) throw new ArgumentOutOfRangeException("epoch");
            ClassLedgerId = classLedgerId; _epoch = epoch; _firstDay = firstPlanningDay;
        }

        // Restore is data-only. It never restores a token, calls native code or creates a lot.
        public static ResidentialCapacityLedger Restore(Guid expectedClassLedgerId, ResidentialCapacityLedgerState state, long newEpoch)
        {
            if (state == null) throw new ArgumentNullException("state");
            if (state.ClassLedgerId != expectedClassLedgerId) throw new ArgumentException("Capacity state belongs to another class ledger.");
            ResidentialCapacityLedger result = new ResidentialCapacityLedger(expectedClassLedgerId, newEpoch, state.FirstPlanningDay);
            foreach (ResidentialCapacityRecord record in state.Records)
            {
                result.ValidateRecord(record);
                if (result._records.ContainsKey(record.Identity.BuildingId) || !result._generations.Add(record.Generation)) throw new ArgumentException("Duplicate building or generation in restored capacity state.");
                result._records.Add(record.Identity.BuildingId, record);
                if (record.IsActive) result._stock[Index(record.Class)] = checked(result._stock[Index(record.Class)] + record.CapacityQ);
            }
            ResidentialCapacityWindow[] windows = state.Windows;
            for (int i = 0; i < 3; i++)
            {
                ResidentialCapacityWindow window = windows[i];
                if (window != null && (Index(window.Class) != i || window.Day < state.FirstPlanningDay || window.OpeningStockRevision > state.StockRevision || window.OpeningExternalRevision > state.ExternalRevision))
                    throw new ArgumentException("Restored window identity/revision is inconsistent.");
                if (window != null && window.Phase == ResidentialCapacityWindowPhase.Open && window.OpeningExternalRevision != state.ExternalRevision)
                    throw new ArgumentException("An open window cannot refer to stale external inputs.");
                result._windows[i] = window;
            }
            result._stockRevision = state.StockRevision; result._externalRevision = state.ExternalRevision;
            // The persisted acknowledgment is evidence, not permission in a new runtime.
            // A freshly initialized graph must acknowledge the restored stock first.
            return result;
        }

        public ResidentialCapacityLedgerSnapshot Snapshot()
        { lock (_sync) { return new ResidentialCapacityLedgerSnapshot(_stockRevision, _externalRevision, IsCurrent(), _valid, _pending.Count, _unresolved, _records.Count, _stock, _reserved); } }

        public bool TryCapture(out ResidentialCapacityLedgerState state)
        {
            lock (_sync)
            {
                state = null;
                if (_pending.Count != 0 || _unresolved != 0) return false;
                ResidentialCapacityRecord[] records = new ResidentialCapacityRecord[_records.Count]; int i = 0;
                foreach (ResidentialCapacityRecord record in _records.Values) records[i++] = record;
                state = new ResidentialCapacityLedgerState(ClassLedgerId, PolicyName, PolicyVersion, PolicyHash, ResidentialNominalCapacity.PolicyName,
                    ResidentialNominalCapacity.PolicyVersion, ResidentialNominalCapacity.PolicyHash, _firstDay,
                    _stockRevision, _externalRevision, _publishedStock, _publishedExternal, records, _windows);
                return true;
            }
        }
        public bool TryGet(ResidentialLotIdentity identity, ulong generation, out ResidentialCapacityRecord record)
        { lock (_sync) { return Find(identity, generation, out record); } }
        public ResidentialCapacityWindow GetWindow(ResidentialClass lotClass)
        { lock (_sync) { return _windows[Index(lotClass)]; } }

        public bool Import(ResidentialCapacityRecord record)
        {
            lock (_sync)
            {
                RequireValid(); ValidateRecord(record);
                ResidentialCapacityRecord old;
                if (_records.TryGetValue(record.Identity.BuildingId, out old))
                {
                    if (SameRecord(old, record)) return false;
                    throw new InvalidOperationException("An existing building/generation cannot be overwritten by import.");
                }
                CheckAdd(record, null);
                Replace(null, record);
                CloseOpenWindows();
                return true;
            }
        }

        public bool AcknowledgePublication(long stockRevision, long externalRevision)
        {
            lock (_sync)
            {
                if (!_valid || stockRevision != _stockRevision || externalRevision != _externalRevision || _unresolved != 0) return false;
                _publishedStock = stockRevision; _publishedExternal = externalRevision; return true;
            }
        }
        public bool AdvanceExternalRevision(long revision)
        {
            lock (_sync)
            {
                RequireValid();
                if (revision < 0) throw new ArgumentOutOfRangeException("revision");
                if (revision <= _externalRevision) return false;
                _externalRevision = revision;
                for (int i = 0; i < 3; i++) if (_windows[i] != null && _windows[i].Phase == ResidentialCapacityWindowPhase.Open)
                    _windows[i] = _windows[i].Change(ResidentialCapacityWindowPhase.Invalidated, _windows[i].CommittedQ);
                return true;
            }
        }

        // Day is the caller's effective simulation-calendar ordinal, never a frame or wall-clock value.
        public bool TryOpenWindow(ResidentialClass lotClass, long day, float raw, float verifiedOverbuild)
        {
            lock (_sync)
            {
                int index = Index(lotClass);
                long desired, maximum; CalculateQuota(raw, verifiedOverbuild, out desired, out maximum);
                if (!_valid || !IsCurrent() || day < _firstDay || _pending.Count != 0 || _unresolved != 0 || (_windows[index] != null && day <= _windows[index].Day)) return false;
                _windows[index] = new ResidentialCapacityWindow(lotClass, day,
                    desired == 0 ? ResidentialCapacityWindowPhase.NotQualified : ResidentialCapacityWindowPhase.Open,
                    raw, verifiedOverbuild, desired, maximum, 0, _stockRevision, _externalRevision);
                return true; // An unqualified day still consumes its watermark.
            }
        }

        public bool TryReserve(object context, long epoch, ResidentialCapacityLotSpec spec,
            ResidentialLotIdentity predecessorIdentity, ulong predecessorGeneration, out ResidentialCapacityReservation token)
        {
            lock (_sync)
            {
                token = null;
                if (context == null || spec == null) throw new ArgumentNullException(context == null ? "context" : "spec");
                if (!_valid || epoch != _epoch || !IsCurrent() || _unresolved != 0 || _pending.Count >= ResidentialClassLedger.MaximumRecords) return false;
                int index = Index(spec.Class); ResidentialCapacityWindow window = _windows[index];
                if (window == null || window.Phase != ResidentialCapacityWindowPhase.Open || window.OpeningExternalRevision != _externalRevision) return false;
                ResidentialCapacityRecord predecessor = null;
                if (predecessorIdentity != null)
                {
                    if (!Find(predecessorIdentity, predecessorGeneration, out predecessor) || !predecessor.IsActive || !spec.ReplacesAt(predecessor.Identity) || _predecessors.ContainsKey(predecessor.Identity.BuildingId)) return false;
                }
                else if (predecessorGeneration != 0) return false;
                long credit = predecessor != null && predecessor.Class == spec.Class ? predecessor.CapacityQ : 0;
                long quota = checked(spec.ReserveQ - credit);
                long held = checked(window.CommittedQ + _reserved[index]);
                if (held >= window.DesiredQ) return false;
                if (credit != 0)
                {
                    // Native capacity-window floor is a person step, hence 4 Q.
                    // held<desired<=max makes the floor's net gain at most8 Q,
                    // so it can exceed max by at most7 Q and exhausts the window.
                    long low = checked(credit + 4L);
                    long high = Math.Max(checked(window.DesiredMaximumQ - held + credit), checked(low + 4L));
                    if (spec.MinimumQ < low || spec.ReserveQ > high) return false;
                }
                else if (quota > window.DesiredMaximumQ - held) return false;
                long reserved = checked(_reserved[index] + quota);
                token = new ResidentialCapacityReservation(this, context, epoch, spec, predecessor, window, quota);
                _pending.Add(token); _reserved[index] = reserved;
                if (predecessor != null) _predecessors.Add(predecessor.Identity.BuildingId, token);
                return true;
            }
        }
        public bool MarkNativeCreated(ResidentialCapacityReservation token, object context, long epoch)
        {
            lock (_sync)
            {
                if (!Owns(token, context, epoch) || token.NativeCreated) return false;
                token.NativeCreated = true; _unresolved++; return true;
            }
        }
        public bool Cancel(ResidentialCapacityReservation token, object context, long epoch)
        {
            lock (_sync)
            {
                if (!Owns(token, context, epoch) || token.NativeCreated) return false;
                FinishToken(token); return true;
            }
        }

        // Calling this announces native success even when validation fails. A failed
        // commit remains unresolved and cannot be erased through Cancel or saving.
        public bool CommitCreated(ResidentialCapacityReservation token, object context, long epoch, ResidentialCapacityRecord actual)
        {
            lock (_sync)
            {
                if (!Owns(token, context, epoch)) return false;
                EnsureNativeCreated(token);
                if (actual == null || actual.ClassLedgerId != ClassLedgerId || !token.Spec.Matches(actual) || !actual.IsActive ||
                    (token.Predecessor != null && actual.Generation == token.Predecessor.Generation)) return false;
                return CommitCore(token, actual, false);
            }
        }

        // Explicit authoritative reconciliation of a real created lot. It may record
        // an unexpected identity/amount, closes the window, and never fabricates a
        // rollback of the physical lot. The caller must verify that observation.
        public bool ReconcileCreated(ResidentialCapacityReservation token, object context, long epoch, ResidentialCapacityRecord observed)
        {
            lock (_sync)
            {
                if (!Owns(token, context, epoch) || !token.NativeCreated || observed == null || observed.ClassLedgerId != ClassLedgerId || observed.Class != token.Spec.Class || !observed.IsActive ||
                    (token.Predecessor != null && observed.Generation == token.Predecessor.Generation)) return false;
                return CommitCore(token, observed, true);
            }
        }

        private bool CommitCore(ResidentialCapacityReservation token, ResidentialCapacityRecord actual, bool reconciled)
        {
            ResidentialCapacityRecord old = null, collision;
            if (token.Predecessor != null && !Find(token.Predecessor.Identity, token.Predecessor.Generation, out old)) return false;
            if (_records.TryGetValue(actual.Identity.BuildingId, out collision) && !ReferenceEquals(collision, old)) return false;
            ResidentialCapacityReservation other;
            if (_predecessors.TryGetValue(actual.Identity.BuildingId, out other) && !ReferenceEquals(other, token)) return false;
            if (_generations.Contains(actual.Generation)) return false;
            int index = Index(token.Spec.Class);
            ResidentialCapacityWindow window = _windows[index];
            // Pending tokens cannot be carried into a newly opened day.
            if (window == null || window.Day != token.Window.Day) return false;
            long credit = token.Predecessor != null && token.Predecessor.Class == actual.Class ? token.Predecessor.CapacityQ : 0;
            long spent = Math.Max(0, checked(actual.CapacityQ - credit));
            long committed = checked(window.CommittedQ + spent);
            if (!reconciled && spent > token.ReservedQuotaQ) return false;
            ResidentialCapacityWindowPhase phase = reconciled || window.Phase == ResidentialCapacityWindowPhase.Invalidated ? ResidentialCapacityWindowPhase.Invalidated :
                committed >= window.DesiredQ ? ResidentialCapacityWindowPhase.Exhausted : ResidentialCapacityWindowPhase.Open;
            ResidentialCapacityWindow updated = window.Change(phase, committed);
            CheckAdd(actual, old);
            Replace(old, actual); // all checked arithmetic completes before mutation
            _windows[index] = updated;
            FinishToken(token);
            return true;
        }

        public bool Complete(ResidentialLotIdentity identity, ulong generation)
        { return ChangePhase(identity, generation, ResidentialCapacityPhase.Completed, false); }
        public bool BeginUpgrade(ResidentialLotIdentity identity, ulong generation)
        { return ChangePhase(identity, generation, ResidentialCapacityPhase.Upgrading, false); }
        public bool Abandon(ResidentialLotIdentity identity, ulong generation)
        { return ChangePhase(identity, generation, ResidentialCapacityPhase.Unavailable, false); }
        public bool Reoccupy(ResidentialLotIdentity identity, ulong generation, ResidentialCapacityPhase phase)
        {
            ResidentialCapacityRecord.ValidatePhase(phase);
            if (phase == ResidentialCapacityPhase.Unavailable) throw new ArgumentException("Reoccupation requires an active phase.");
            return ChangePhase(identity, generation, phase, true);
        }
        private bool ChangePhase(ResidentialLotIdentity identity, ulong generation, ResidentialCapacityPhase phase, bool reoccupy)
        {
            lock (_sync)
            {
                RequireValid(); ResidentialCapacityRecord old;
                if (!Find(identity, generation, out old) || _predecessors.ContainsKey(identity.BuildingId) || old.Phase == phase) return false;
                if (reoccupy ? old.IsActive : (!old.IsActive && phase != ResidentialCapacityPhase.Unavailable)) return false;
                Replace(old, old.WithPhase(phase));
                if (reoccupy) CloseOpenWindows();
                return true;
            }
        }
        public bool Release(ResidentialLotIdentity identity, ulong generation)
        {
            lock (_sync)
            {
                RequireValid(); ResidentialCapacityRecord old;
                if (!Find(identity, generation, out old)) return false;
                ResidentialCapacityReservation token;
                if (_predecessors.TryGetValue(identity.BuildingId, out token))
                {
                    if (token.PredecessorReleased) return false;
                    Replace(old, old.WithPhase(ResidentialCapacityPhase.Unavailable));
                    token.PredecessorReleased = true; // retained tombstone until token finishes
                }
                else Replace(old, null);
                return true;
            }
        }
        public bool RefreshUpgrade(ResidentialLotIdentity before, ulong generation, ResidentialCapacityRecord after)
        {
            lock (_sync)
            {
                RequireValid(); ValidateRecord(after); ResidentialCapacityRecord old;
                if (!Find(before, generation, out old) || _predecessors.ContainsKey(before.BuildingId) || after.Generation != generation || after.Class != old.Class || after.Origin != old.Origin ||
                    after.Identity.BuildingId != before.BuildingId || after.Identity.NativeBuildIndex != before.NativeBuildIndex ||
                    after.Identity.OriginXBits != before.OriginXBits || after.Identity.OriginZBits != before.OriginZBits || after.Identity.Service != before.Service ||
                    !old.IsActive || !after.IsActive || SameRecord(old, after)) return false;
                Replace(old, after); CloseOpenWindows(); return true;
            }
        }

        public void InvalidateSession()
        { lock (_sync) { _valid = false; } } // old tokens retain their holds; only a new restored instance may resume

        internal static void CalculateQuota(float raw, float overbuild, out long desiredQ, out long maximumQ)
        {
            if (float.IsNaN(raw) || float.IsInfinity(raw) || float.IsNaN(overbuild) || float.IsInfinity(overbuild) || overbuild < 1f) throw new ArgumentOutOfRangeException("raw", "Finite RAW and caller-verified overbuild >= 1 are required.");
            double demand = Math.Truncate((double)raw);
            if (demand <= 5) { desiredQ = maximumQ = 0; return; }
            if (demand > int.MaxValue) throw new ArgumentOutOfRangeException("raw");
            float maximum = (float)((float)demand * overbuild);
            if (float.IsInfinity(maximum) || (double)maximum > int.MaxValue || maximum < demand) throw new ArgumentOutOfRangeException("overbuild");
            desiredQ = checked((long)demand * 4L); maximumQ = checked((long)Math.Truncate((double)maximum) * 4L);
        }

        private bool Owns(ResidentialCapacityReservation token, object context, long epoch)
        { return _valid && token != null && ReferenceEquals(token.Owner, this) && ReferenceEquals(token.Context, context) && token.Epoch == epoch && epoch == _epoch && !token.Used && _pending.Contains(token); }
        private void EnsureNativeCreated(ResidentialCapacityReservation token)
        { if (!token.NativeCreated) { token.NativeCreated = true; _unresolved++; } }
        private void FinishToken(ResidentialCapacityReservation token)
        {
            // Precompute tombstone removal revision before releasing any hold.
            ResidentialCapacityRecord tombstone = null;
            if (token.PredecessorReleased && token.Predecessor != null)
                Find(token.Predecessor.Identity, token.Predecessor.Generation, out tombstone);
            if (tombstone != null) Replace(tombstone, null);
            int index = Index(token.Spec.Class); _reserved[index] -= token.ReservedQuotaQ;
            _pending.Remove(token); token.Used = true;
            if (token.NativeCreated) _unresolved--;
            if (token.Predecessor != null) _predecessors.Remove(token.Predecessor.Identity.BuildingId);
        }
        private bool IsCurrent()
        { return _publishedStock == _stockRevision && _publishedExternal == _externalRevision; }
        private void CloseOpenWindows()
        {
            for (int i = 0; i < 3; i++)
                if (_windows[i] != null && _windows[i].Phase == ResidentialCapacityWindowPhase.Open)
                    _windows[i] = _windows[i].Change(ResidentialCapacityWindowPhase.Invalidated, _windows[i].CommittedQ);
        }
        private void RequireValid() { if (!_valid) throw new InvalidOperationException("This accounting session has been invalidated."); }
        private void ValidateRecord(ResidentialCapacityRecord record)
        { if (record == null || record.ClassLedgerId != ClassLedgerId) throw new ArgumentException("Record belongs to another class ledger or is absent."); }
        private void CheckAdd(ResidentialCapacityRecord record, ResidentialCapacityRecord removing)
        {
            ValidateRecord(record);
            if (removing == null && _records.Count >= ResidentialClassLedger.MaximumRecords) throw new InvalidOperationException("Capacity ledger is full.");
            if (_generations.Contains(record.Generation) && (removing == null || removing.Generation != record.Generation)) throw new InvalidOperationException("Generation is already owned by another record.");
        }
        private void Replace(ResidentialCapacityRecord old, ResidentialCapacityRecord value)
        {
            long revision = checked(_stockRevision + 1);
            long[] totals = (long[])_stock.Clone();
            if (old != null && old.IsActive) totals[Index(old.Class)] = checked(totals[Index(old.Class)] - old.CapacityQ);
            if (value != null && value.IsActive) totals[Index(value.Class)] = checked(totals[Index(value.Class)] + value.CapacityQ);
            if (old != null) { _records.Remove(old.Identity.BuildingId); _generations.Remove(old.Generation); }
            if (value != null) { _records.Add(value.Identity.BuildingId, value); _generations.Add(value.Generation); }
            Array.Copy(totals, _stock, 3); _stockRevision = revision;
        }
        private bool Find(ResidentialLotIdentity identity, ulong generation, out ResidentialCapacityRecord record)
        {
            record = null; ResidentialCapacityRecord found;
            if (identity == null || generation == 0 || !_records.TryGetValue(identity.BuildingId, out found) || found.Generation != generation || !found.Identity.Matches(identity)) return false;
            record = found; return true;
        }
        private static bool SameRecord(ResidentialCapacityRecord left, ResidentialCapacityRecord right)
        { return left.ClassLedgerId == right.ClassLedgerId && left.Generation == right.Generation && left.Identity.Matches(right.Identity) && left.Class == right.Class &&
            left.Category == right.Category && left.NativeLevel == right.NativeLevel && left.HomeCount == right.HomeCount && left.Phase == right.Phase && left.Origin == right.Origin; }
        private static int Index(ResidentialClass lotClass)
        { ResidentialClassLedger.ValidateClass(lotClass, "lotClass"); return (int)lotClass - 1; }
        private static byte[] HashPolicy()
        { using (SHA256 sha = SHA256.Create()) return sha.ComputeHash(Encoding.UTF8.GetBytes(CanonicalPolicyMaterial)); }
    }
}
