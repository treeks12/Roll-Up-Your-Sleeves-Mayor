using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Threading;
using ColossalFramework;
using SC4Evolution.Simulation;
using UnityEngine;

namespace SC4Evolution.Mod.Economy
{
    public sealed class EconomicSavePair
    {
        private readonly byte[] _classes, _capacity;
        public byte[] ClassPayload { get { return (byte[])_classes.Clone(); } }
        public byte[] CapacityPayload { get { return _capacity == null ? null : (byte[])_capacity.Clone(); } }
        internal EconomicSavePair(byte[] classes, byte[] capacity)
        { if (classes == null) throw new ArgumentNullException("classes"); _classes = (byte[])classes.Clone(); _capacity = capacity == null ? null : (byte[])capacity.Clone(); }
    }

    public sealed class ResidentialCapacityStatus
    {
        private readonly long[] _stock;
        public bool ModeRequested { get; private set; }
        public bool Ready { get; private set; }
        public bool HooksReady { get; private set; }
        public bool CompatibilityKnown { get; private set; }
        public string Status { get; private set; }
        public string NominalProfile { get { return ResidentialNominalCapacity.PolicyName; } }
        public string AccountingPolicy { get { return ResidentialCapacityLedger.PolicyName; } }
        public long[] StockQ { get { return (long[])_stock.Clone(); } }
        public long StockRevision { get; private set; }
        public long ExternalRevision { get; private set; }
        public long Day { get; private set; }
        public int UnknownBuildings { get; private set; }
        public int PendingReservations { get; private set; }
        public int UnresolvedCreations { get; private set; }
        public int PendingMutations { get; private set; }
        public int UnresolvedMutations { get; private set; }
        public long ControlledIntents { get; private set; }
        public long RejectedIntents { get; private set; }
        public long ReservedAttempts { get; private set; }
        public long CommittedCreations { get; private set; }
        public int LastNativeRoll { get; private set; }
        public int LastNativeThreshold { get; private set; }
        internal ResidentialCapacityStatus(bool requested, bool ready, bool hooks, bool compatibility, string status,
            ResidentialCapacityLedgerSnapshot state, long day, int unknown, long controlled, long rejected, long reserved, long committed, int roll, int threshold,
            int mutations, int unresolvedMutations)
        { ModeRequested = requested; Ready = ready; HooksReady = hooks; CompatibilityKnown = compatibility; Status = status; Day = day; UnknownBuildings = unknown;
            _stock = state == null ? new long[3] : state.StockQ; StockRevision = state == null ? -1 : state.StockRevision;
            ExternalRevision = state == null ? -1 : state.ExternalRevision; PendingReservations = state == null ? 0 : state.PendingReservations;
            UnresolvedCreations = state == null ? 0 : state.UnresolvedCreations; ControlledIntents = controlled; RejectedIntents = rejected;
            ReservedAttempts = reserved; CommittedCreations = committed; LastNativeRoll = roll; LastNativeThreshold = threshold;
            PendingMutations = mutations; UnresolvedMutations = unresolvedMutations; }
    }

    internal enum ResidentialCapacityIntentDecision { NativeFallback = 0, ControlledReject = 1, Continue = 2 }
    internal enum ResidentialCapacityAttemptDecision { NativeFallback = 0, ControlledReject = 1, Reserved = 2 }
    internal sealed class ResidentialCapacityIntent
    {
        internal readonly ResidentialCapacityBridge Owner;
        internal readonly int Epoch, ThreadId;
        internal readonly long Day;
        internal readonly long ExternalRevision;
        internal readonly List<ResidentialCapacityAttempt> Attempts = new List<ResidentialCapacityAttempt>();
        internal bool Ended;
        internal ResidentialCapacityIntentDecision Decision { get; private set; }
        internal ResidentialCapacityIntent(ResidentialCapacityBridge owner, ResidentialCapacityIntentDecision decision, int epoch, long day, long externalRevision)
        { Owner = owner; Decision = decision; Epoch = epoch; Day = day; ExternalRevision = externalRevision; ThreadId = Thread.CurrentThread.ManagedThreadId; }
    }
    internal sealed class ResidentialCapacityAttempt
    {
        internal readonly ResidentialCapacityIntent Intent;
        internal readonly ResidentialCapacityReservation Token;
        internal readonly ushort Predecessor;
        internal bool NativeCreated, Finished;
        internal bool CreateScopeClaimed;
        internal ResidentialCapacityAttemptDecision Decision { get; private set; }
        internal ResidentialClass Class { get; private set; }
        internal ResidentialCapacityAttempt(ResidentialCapacityIntent intent, ResidentialCapacityAttemptDecision decision,
            ResidentialClass lotClass, ResidentialCapacityReservation token, ushort predecessor = 0)
        { Intent = intent; Decision = decision; Class = lotClass; Token = token; Predecessor = predecessor; }
    }

    internal enum ResidentialCapacityMutationKind { Critical = 0, Phase = 1 }
    internal sealed class ResidentialCapacityMutation
    {
        internal readonly ResidentialCapacityBridge Owner;
        internal readonly int Epoch, ThreadId;
        internal readonly ushort Building;
        internal readonly ResidentialCapacityMutationKind Kind;
        internal readonly bool DeferredCreation;
        internal bool Ended;
        internal ResidentialCapacityMutation(ResidentialCapacityBridge owner, int epoch, ushort building, ResidentialCapacityMutationKind kind, bool deferred)
        { Owner = owner; Epoch = epoch; Building = building; Kind = kind; DeferredCreation = deferred; ThreadId = Thread.CurrentThread.ManagedThreadId; }
    }

    // All mutable state is serialized by EconomicBridge.CapacitySync. Native reads
    // are additionally simulation-thread gated. Save capture never enters native code.
    internal sealed class ResidentialCapacityBridge
    {
        private readonly EconomicBridge _owner;
        private readonly ResidentialWaterObserver _water;
        private readonly ResidentialCapacityProviderActivation _providerActivation;
        private readonly string _providerActivationFailure;
        private readonly bool _newCity;
        private readonly Dictionary<ushort, ResidentialCapacityRecord> _known = new Dictionary<ushort, ResidentialCapacityRecord>();
        private readonly HashSet<ushort> _dirty = new HashSet<ushort>();
        private readonly HashSet<ushort> _deferredCreated = new HashSet<ushort>();
        private readonly HashSet<ResidentialCapacityMutation> _mutations = new HashSet<ResidentialCapacityMutation>();
        private int _unresolvedMutations;
        private ResidentialCapacityLedgerState _loaded;
        private ResidentialCapacityLedger _ledger;
        private int _cursor = 1;
        private bool _baseline, _hooks, _compatibility, _stopped;
        private bool _reconcileOnActivation = true;
        private ResidentialCapacityPatchCertificate _certificate;
        private string _failure, _unavailable = "Waiting for simulation-thread capacity baseline.";
        private float _overbuild;
        private long _day;
        private uint _auditFrame = uint.MaxValue;
        private long _controlledIntents, _rejectedIntents, _reservedAttempts, _committedCreations;
        private int _lastNativeRoll = -1, _lastNativeThreshold = -1;
        private volatile ResidentialCapacityStatus _published;
        internal bool ModeRequested { get; private set; }
        internal int SessionEpoch { get; private set; }
        internal ResidentialCapacityStatus Published { get { return _published; } }
        internal bool HasRetainedState { get { return _loaded != null || _ledger != null; } }
        internal bool Ready { get { ResidentialCapacityStatus state = _published; return state != null && state.Ready; } }

        internal ResidentialCapacityBridge(EconomicBridge owner, bool requested, int epoch, bool newCity,
            byte[] payload, byte[] exactClassPayload, string failure, string root, ResidentialWaterObserver water,
            ResidentialCapacityProviderActivation providerActivation = null, string providerActivationFailure = null)
        {
            _owner = owner; ModeRequested = requested; SessionEpoch = epoch; _newCity = newCity; _water = water; _failure = failure;
            _providerActivation = providerActivation; _providerActivationFailure = providerActivationFailure;
            if (payload != null && failure == null)
                try
                {
                    if (exactClassPayload == null) throw new InvalidDataException("Capacity data has no paired class payload.");
                    using (MemoryStream stream = new MemoryStream(payload, false)) _loaded = ResidentialCapacityLedgerCodec.ReadBound(stream, exactClassPayload);
                }
                catch (Exception error) { _failure = "Capacity save pair rejected: " + error.Message; }
            if (requested)
                try { _overbuild = ResidentialCapacityTuning.Load(root); }
                catch (Exception error) { _failure = "Capacity tuning unavailable: " + error.Message; }
            Publish();
        }

        internal static bool OnSimulationThread()
        { return Singleton<SimulationManager>.exists && ReferenceEquals(Thread.CurrentThread, Singleton<SimulationManager>.instance.m_simulationThread); }
        private bool Current { get { return !_stopped && SessionEpoch == _owner.DemandContextSession && ReferenceEquals(this, _owner.Capacity); } }
        private bool HasAuthorityPrerequisites
        { get { return Current && ModeRequested && _failure == null && _hooks && _compatibility && _baseline && _dirty.Count == 0 &&
            _ledger != null && _water != null && _water.IsValid && _owner.Classes != null && _owner.Classes.CapacityPolicyBound; } }
        private bool CanControl
        {
            get
            {
                if (!HasAuthorityPrerequisites) return false;
                ResidentialCapacityLedgerSnapshot state = _ledger.Snapshot();
                return state.SessionValid && state.PublicationCurrent && state.UnresolvedCreations == 0 && _deferredCreated.Count == 0 && AdmissionScopesOnly();
            }
        }
        internal void ConfirmHookSet(int epoch, bool complete, string reason)
        {
            lock (_owner.CapacitySync)
            {
                if (!Current || epoch != SessionEpoch) return;
                _hooks = complete; _compatibility = false;
                _certificate = null;
                _unavailable = complete ? "Awaiting verified effective native compatibility." : "Required atomic hook set unavailable: " + reason;
                if (complete)
                    try
                    {
                        ResidentialCapacityCompatibilityResult result;
                        if (ModeRequested)
                        {
                            if (_providerActivation == null)
                            {
                                _unavailable = "Explicit RAW provider preparation unavailable: " +
                                    (_providerActivationFailure ?? "No preparation receipt was supplied.");
                                Publish(); return;
                            }
                            result = ResidentialCapacityCompatibility.CertifyForControl(_providerActivation, out _certificate);
                        }
                        else result = ResidentialCapacityCompatibility.CertifyForControl(out _certificate);
                        _compatibility = result.Kind == ResidentialCapacityCompatibilityKind.Known && result.OriginalMethodsKnown && result.EffectiveMetadataInspected &&
                            result.EffectiveConfiguration == ResidentialCapacityEffectiveConfiguration.OwnRawCapacityV1 && _certificate != null;
                        if (!_compatibility) _unavailable = result.Reason;
                    }
                    catch (Exception error) { _unavailable = "Compatibility inspection failed: " + error.Message; }
                Publish();
            }
        }
        internal void Suspend(string reason)
        {
            lock (_owner.CapacitySync)
            { if (_failure == null) _failure = reason; Publish(); }
        }
        internal void Stop()
        { _stopped = true; if (_ledger != null) _ledger.InvalidateSession(); Publish(); }

        // Baseline is bounded over building slots; a failed observation remains dirty,
        // never silently omitted from supply. New control cannot begin before it completes.
        internal bool Maintain(int budget)
        {
            if (!OnSimulationThread() || !Current) return false;
            if (!ModeRequested && _loaded == null && _ledger == null) return true;
            if (_failure != null) { Publish(); return true; }
            // Unknown effective compatibility is not permission to invoke a
            // possibly patched native calculation merely for a baseline.
            if (!_hooks || !_compatibility || !Revalidate()) { Publish(); return true; }
            if (!_owner.Classes.Bound) return false;
            if (ModeRequested && !_owner.Classes.CapacityPolicyBound)
            { Suspend("The retained class selection policy is not authorized for RAW capacity admission."); return true; }
            _day = Singleton<SimulationManager>.instance.m_currentGameTime.Date.Ticks / TimeSpan.TicksPerDay;
            if (_ledger == null)
            {
                Guid id = _owner.Classes.LedgerId;
                _ledger = _loaded == null ? new ResidentialCapacityLedger(id, SessionEpoch, checked(_day + (_newCity ? 0L : 1L))) :
                    ResidentialCapacityLedger.Restore(id, _loaded, SessionEpoch);
                if (_loaded != null) foreach (ResidentialCapacityRecord record in _loaded.Records) _known.Add(record.Identity.BuildingId, record);
                _loaded = null;
            }
            Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            int end = Math.Min(buildings.Length, _cursor + budget);
            for (; _cursor < end; _cursor++) ObserveCore((ushort)_cursor, true);
            _baseline = _cursor >= buildings.Length;
            if (_baseline && _dirty.Count != 0)
            {
                ushort[] retry = new ushort[Math.Min(budget, _dirty.Count)]; int n = 0;
                foreach (ushort id in _dirty) { retry[n++] = id; if (n == retry.Length) break; }
                foreach (ushort id in retry) ObserveCore(id, true);
            }
            Publish(); return _baseline;
        }
        internal long[] SupplyQ
        {
            get
            {
                if (_ledger == null || !_baseline || _dirty.Count != 0 || _deferredCreated.Count != 0 ||
                    _unresolvedMutations != 0 || !AdmissionScopesOnly()) return null;
                ResidentialCapacityLedgerSnapshot state = _ledger.Snapshot();
                return state.UnresolvedCreations == 0 ? state.StockQ : null;
            }
        }
        internal void ExternalInputsChanged()
        { if (_ledger != null) { ResidentialCapacityLedgerSnapshot s = _ledger.Snapshot(); _ledger.AdvanceExternalRevision(checked(s.ExternalRevision + 1)); Publish(); } }
        internal bool AcknowledgeGraph()
        {
            if (_ledger == null || !_baseline || _dirty.Count != 0 || _failure != null) { Publish(); return false; }
            ResidentialCapacityLedgerSnapshot s = _ledger.Snapshot();
            bool result = _ledger.AcknowledgePublication(s.StockRevision, s.ExternalRevision); Publish(); return result;
        }

        internal ResidentialCapacityIntent BeginIntent(SegmentedDemandSnapshot snapshot, int nativeRoll, int nativeThreshold, ItemClass.Zone zone, ushort blockId)
        {
            lock (_owner.CapacitySync)
            {
                ResidentialCapacityIntentDecision decision = ResidentialCapacityIntentDecision.NativeFallback;
                if (OnSimulationThread() && Current && ModeRequested && (zone == ItemClass.Zone.ResidentialLow || zone == ItemClass.Zone.ResidentialHigh))
                {
                    _lastNativeRoll = nativeRoll; _lastNativeThreshold = nativeThreshold;
                    if (_hooks && _compatibility) Revalidate();
                    FlushPhaseScopes();
                    AuditBeforeSpend();
                    if (CanControl && _owner.CanUseCapacityDemand(snapshot))
                    {
                        _day = Singleton<SimulationManager>.instance.m_currentGameTime.Date.Ticks / TimeSpan.TicksPerDay;
                        for (int i = 0; i < 3; i++)
                        {
                            float raw;
                            if (!snapshot.TryGetActiveDemand((uint)(0x21010 + 0x10 * i), out raw)) { Suspend("Missing finite residential RAW node."); break; }
                            _ledger.TryOpenWindow((ResidentialClass)(i + 1), _day, raw, _overbuild);
                        }
                        if (CanControl) decision = EligibleMask() == 0 ? ResidentialCapacityIntentDecision.ControlledReject : ResidentialCapacityIntentDecision.Continue;
                    }
                }
                if (decision != ResidentialCapacityIntentDecision.NativeFallback) Saturate(ref _controlledIntents);
                if (decision == ResidentialCapacityIntentDecision.ControlledReject) Saturate(ref _rejectedIntents);
                Publish(); return new ResidentialCapacityIntent(this, decision, SessionEpoch, _day, _ledger == null ? -1 : _ledger.Snapshot().ExternalRevision);
            }
        }
        internal ResidentialCapacityAttempt TryReserveNew(ResidentialCapacityIntent intent, BuildingInfo info, Vector3 position, int length, uint buildIndex)
        { return Reserve(intent, 0, info, position, length, buildIndex); }
        internal ResidentialCapacityAttempt TryReserveRedevelopment(ResidentialCapacityIntent intent, ushort predecessor, BuildingInfo info, Vector3 position, int length, uint buildIndex)
        { return Reserve(intent, predecessor, info, position, length, buildIndex); }
        private ResidentialCapacityAttempt Reserve(ResidentialCapacityIntent intent, ushort predecessor, BuildingInfo info, Vector3 position, int length, uint buildIndex)
        {
            lock (_owner.CapacitySync)
            {
                ResidentialCapacityAttemptDecision decision = ResidentialCapacityAttemptDecision.NativeFallback;
                ResidentialCapacityReservation token = null; ResidentialClass selected = 0;
                ushort retiredPredecessor = 0;
                if (Owns(intent)) FlushPhaseScopes();
                if (Owns(intent) && intent.Decision == ResidentialCapacityIntentDecision.Continue && CanControl && Revalidate() && intent.Day == CurrentDay() &&
                    intent.ExternalRevision == _ledger.Snapshot().ExternalRevision)
                {
                    ResidentialNominalCategory category;
                    if (!TryCategory(info, out category) || length < 1 || length > 255 || info.m_cellWidth < 1 || info.m_cellWidth > 255)
                        _unavailable = "Prospective prefab/AI/geometry is unsupported; native fallback.";
                    else
                    {
                        ResidentialWaterObservation water = _water.Observe(position);
                        if (water.Evidence == ResidentialWaterEvidence.Unknown) _unavailable = "Prospective water query unknown: " + water.Reason;
                        else
                        {
                            ResidentialClassMask mask = EligibleMask();
                            if (water.Evidence == ResidentialWaterEvidence.Unserved)
                            {
                                if (_water.Profile.LowMaximumStage == 0) mask &= ~ResidentialClassMask.Low;
                                if (_water.Profile.MediumMaximumStage == 0) mask &= ~ResidentialClassMask.Medium;
                                if (_water.Profile.HighMaximumStage == 0) mask &= ~ResidentialClassMask.High;
                            }
                            ResidentialCapacityRecord old = null;
                            bool predecessorKnown = predecessor == 0 || (_known.TryGetValue(predecessor, out old) &&
                                _owner.Classes.MatchesAttempt(old.Identity, old.Generation));
                            if (predecessorKnown)
                            {
                                float[] raw = WindowRaw(); decision = ResidentialCapacityAttemptDecision.ControlledReject;
                                // Fit the real Hmax envelope. An exhausted/too-small
                                // higher-ranked window must not hide a lower-ranked fit.
                                while (mask != 0)
                                {
                                    selected = ResidentialSelectionPolicy.ChooseRawWindow(raw[0], raw[1], raw[2], mask);
                                    ResidentialCapacityLotSpec spec = new ResidentialCapacityLotSpec(buildIndex,
                                        ResidentialClassBridge.FloatBits(position.x), ResidentialClassBridge.FloatBits(position.z), info.name,
                                        (int)info.m_class.m_service, (int)info.m_class.m_subService, (byte)info.m_cellWidth, (byte)length,
                                        selected, category, (int)info.m_class.m_level);
                                    bool activePredecessor = old != null && old.IsActive;
                                    if (_ledger.TryReserve(intent, SessionEpoch, spec, activePredecessor ? old.Identity : null, activePredecessor ? old.Generation : 0, out token))
                                    {
                                        decision = ResidentialCapacityAttemptDecision.Reserved;
                                        retiredPredecessor = activePredecessor ? predecessor : (ushort)0;
                                        break;
                                    }
                                    mask = (ResidentialClassMask)((int)mask & ~(1 << ((int)selected - 1)));
                                }
                            }
                        }
                    }
                }
                ResidentialCapacityAttempt attempt = new ResidentialCapacityAttempt(intent, decision, selected, token, retiredPredecessor);
                if (decision == ResidentialCapacityAttemptDecision.Reserved) Saturate(ref _reservedAttempts);
                if (Owns(intent)) intent.Attempts.Add(attempt);
                Publish(); return attempt;
            }
        }
        internal void MarkNativeCreated(ResidentialCapacityAttempt attempt)
        {
            // Physical success must survive a stale epoch or a later validation error.
            // This flag is deliberately set before any lock, manager read or validation.
            if (attempt == null || attempt.Intent == null || !ReferenceEquals(attempt.Intent.Owner, this) ||
                attempt.Intent.ThreadId != Thread.CurrentThread.ManagedThreadId || attempt.Token == null) return;
            attempt.NativeCreated = true;
            lock (_owner.CapacitySync)
            {
                if (attempt.Token != null && ReferenceEquals(attempt.Intent.Owner, this) && _ledger != null)
                    _ledger.MarkNativeCreated(attempt.Token, attempt.Intent, SessionEpoch);
                Publish();
            }
        }
        internal bool CommitNative(ResidentialCapacityAttempt attempt, ushort id, uint expectedIndex, BuildingInfo expectedPrefab)
        {
            lock (_owner.CapacitySync)
            {
                if (attempt == null || !attempt.NativeCreated || !Owns(attempt.Intent) || attempt.Finished || attempt.Token == null) return false;
                try
                {
                    if (!Revalidate()) throw new InvalidOperationException("Effective patches changed after native success; physical stock remains unresolved.");
                    Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
                    if (id == 0 || id >= buildings.Length || buildings[id].m_buildIndex != expectedIndex || !ReferenceEquals(buildings[id].Info, expectedPrefab))
                        throw new InvalidOperationException("Native created identity differs from the reservation.");
                    ResidentialCapacityObservation observed = ResidentialCapacityObserver.Observe(id, ref buildings[id]);
                    if (observed.Status != ResidentialCapacityObservationStatus.Known) throw new InvalidOperationException("Created HOME chain not coherent: " + observed.Reason);
                    ResidentialLotRecord classes = _owner.Classes.CommitCapacityClass(id, expectedIndex, expectedPrefab, attempt.Class);
                    if (classes == null) throw new InvalidOperationException("Created class fingerprint could not commit.");
                    ResidentialCapacityRecord actual = MakeRecord(classes, observed);
                    if (!_ledger.CommitCreated(attempt.Token, attempt.Intent, SessionEpoch, actual))
                    {
                        // Real native stock is never erased to make accounting look clean.
                        if (!_ledger.ReconcileCreated(attempt.Token, attempt.Intent, SessionEpoch, actual))
                            throw new InvalidOperationException("Native stock remains unresolved; save capture is blocked.");
                        _failure = "Physical creation required explicit reconciliation; control suspended.";
                    }
                    if (attempt.Predecessor != 0) { _known.Remove(attempt.Predecessor); _dirty.Remove(attempt.Predecessor); }
                    _known[id] = actual; _dirty.Remove(id); _deferredCreated.Remove(id); attempt.Finished = true; Saturate(ref _committedCreations);
                    if (!_owner.PublishCapacityMutation()) throw new InvalidOperationException("Stock graph publication failed after native success.");
                    Publish(); return _failure == null;
                }
                catch (Exception error) { Suspend("Native success was not rolled back: " + error.Message); return false; }
            }
        }
        internal void Cancel(ResidentialCapacityAttempt attempt)
        {
            lock (_owner.CapacitySync)
            {
                if (attempt == null || attempt.NativeCreated || attempt.Finished || !Owns(attempt.Intent)) return;
                if (attempt.Token != null) _ledger.Cancel(attempt.Token, attempt.Intent, SessionEpoch);
                attempt.Finished = true; Publish();
            }
        }
        internal void EndIntent(ResidentialCapacityIntent intent)
        {
            lock (_owner.CapacitySync)
            {
                if (!Owns(intent)) return;
                foreach (ResidentialCapacityAttempt attempt in intent.Attempts) Cancel(attempt);
                intent.Ended = true; Publish();
            }
        }

        internal ResidentialCapacityMutation BeginMutation(ushort building, ResidentialCapacityMutationKind kind = ResidentialCapacityMutationKind.Critical)
        {
            lock (_owner.CapacitySync)
            { return BeginMutationCore(building, kind, false); }
        }
        internal ResidentialCapacityMutation BeginCreateMutation(ResidentialCapacityAttempt controlledAttempt)
        {
            lock (_owner.CapacitySync)
            {
                bool deferred = controlledAttempt != null && Owns(controlledAttempt.Intent) && controlledAttempt.Token != null &&
                    controlledAttempt.Decision == ResidentialCapacityAttemptDecision.Reserved && !controlledAttempt.Finished &&
                    !controlledAttempt.NativeCreated && !controlledAttempt.CreateScopeClaimed;
                if (deferred) controlledAttempt.CreateScopeClaimed = true;
                return BeginMutationCore(0, ResidentialCapacityMutationKind.Critical, deferred);
            }
        }
        private ResidentialCapacityMutation BeginMutationCore(ushort building, ResidentialCapacityMutationKind kind, bool deferred)
        {
            if (!Current || !OnSimulationThread() || (!ModeRequested && !HasRetainedState)) return null;
            if (kind != ResidentialCapacityMutationKind.Critical && kind != ResidentialCapacityMutationKind.Phase)
            { Suspend("Unknown native mutation scope kind."); return null; }
            if (kind == ResidentialCapacityMutationKind.Phase && building == 0)
            { Suspend("A phase mutation requires a real building ID."); return null; }
            if (_mutations.Count >= 128) { Suspend("Native mutation nesting exceeds 128 scopes."); return null; }
            ResidentialCapacityMutation token = new ResidentialCapacityMutation(this, SessionEpoch, building, kind, deferred);
            _mutations.Add(token); Publish(); return token;
        }
        internal void EndMutation(ResidentialCapacityMutation token, ushort finalBuilding, Exception error)
        {
            lock (_owner.CapacitySync)
            {
                if (token == null || token.Ended || !ReferenceEquals(token.Owner, this) || token.Epoch != SessionEpoch ||
                    token.ThreadId != Thread.CurrentThread.ManagedThreadId || !Current || !_mutations.Contains(token)) return;
                token.Ended = true;
                if (token.Building != 0 && finalBuilding != token.Building)
                { _unresolvedMutations++; Suspend("Native mutation finalizer identity differs from its prefix."); return; }
                if (error != null)
                {
                    // Retain the scope as unresolved physical history. It cannot be
                    // serialized or erased by a second finalizer/cancellation.
                    _unresolvedMutations++;
                    Suspend("Native mutation failed after it may have changed the city: " + error.Message); return;
                }
                _mutations.Remove(token);
                try
                {
                    if (_compatibility && !Revalidate()) return;
                    if (token.DeferredCreation)
                    {
                        // Separate from dirty baseline observations: this very short
                        // create/commit barrier must not restart citizen mirroring.
                        // A normal native allocation failure has out-ID0. The
                        // wrapper cancels its untouched reservation afterwards.
                        if (finalBuilding != 0) _deferredCreated.Add(finalBuilding);
                    }
                    else if (_ledger != null && finalBuilding != 0 && !HasOtherMutation(finalBuilding) &&
                        _compatibility && Revalidate())
                    {
                        if (!OnSimulationThread()) { Suspend("Mutation finalizer left its simulation thread."); return; }
                        ObserveCore(finalBuilding, true);
                        if (_baseline) _owner.PublishCapacityMutation();
                    }
                }
                catch (Exception failure)
                { _unresolvedMutations++; Suspend("Native mutation reconciliation failed: " + failure.Message); }
                finally { Publish(); }
            }
        }
        private bool HasOtherMutation(ushort building)
        {
            foreach (ResidentialCapacityMutation token in _mutations)
                if (!token.Ended && token.Building == building) return true;
            return false;
        }
        private bool AdmissionScopesOnly()
        {
            if (_unresolvedMutations != 0) return false;
            foreach (ResidentialCapacityMutation token in _mutations)
                if (!ReferenceEquals(token.Owner, this) || token.Ended || token.Building == 0 || token.Kind != ResidentialCapacityMutationKind.Phase || token.Epoch != SessionEpoch ||
                    token.ThreadId != Thread.CurrentThread.ManagedThreadId) return false;
            return true;
        }
        private void FlushPhaseScopes()
        {
            if (!HasAuthorityPrerequisites || !AdmissionScopesOnly() || !OnSimulationThread()) return;
            long revision = _ledger.Snapshot().StockRevision; int unknown = _dirty.Count;
            HashSet<ushort> seen = new HashSet<ushort>();
            foreach (ResidentialCapacityMutation token in _mutations)
                if (token.Building != 0 && seen.Add(token.Building)) ObserveCore(token.Building, true);
            if (revision != _ledger.Snapshot().StockRevision || unknown != _dirty.Count) _owner.PublishCapacityMutation();
            Publish();
        }

        internal void BeforeRelease(ushort id)
        {
            lock (_owner.CapacitySync)
            {
                if (!OnSimulationThread() || !Current || _ledger == null) return;
                if (!_compatibility || !Revalidate()) return;
                ResidentialCapacityRecord old;
                if (_known.TryGetValue(id, out old))
                {
                    _ledger.Release(old.Identity, old.Generation); _known.Remove(id); _dirty.Remove(id);
                    _owner.PublishCapacityMutation();
                }
                Publish();
            }
        }
        internal void ObserveChanged(ushort id)
        {
            lock (_owner.CapacitySync)
            {
                if (!OnSimulationThread() || !Current || _ledger == null) return;
                if (!_compatibility || !Revalidate()) return;
                ObserveCore(id, true);
                if (_baseline) _owner.PublishCapacityMutation();
                Publish();
            }
        }
        private void ObserveCore(ushort id, bool import)
        {
            Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            ResidentialCapacityRecord old; _known.TryGetValue(id, out old);
            if (id == 0 || id >= buildings.Length) return;
            Building value = buildings[id];
            if ((value.m_flags & (Building.Flags.Created | Building.Flags.Deleted)) != Building.Flags.Created)
            {
                if (old != null) { _ledger.Release(old.Identity, old.Generation); _known.Remove(id); }
                _dirty.Remove(id); return;
            }
            BuildingInfo info = value.Info;
            if (info == null || info.m_class == null || info.m_class.m_service != ItemClass.Service.Residential)
            {
                if (old != null) { _ledger.Release(old.Identity, old.Generation); _known.Remove(id); }
                _dirty.Remove(id); return;
            }
            // A predecessor can remain Created|Demolishing until native release.
            // Its replacement already owns the new supply; never bootstrap it again.
            if ((value.m_flags & Building.Flags.Demolishing) != 0)
            {
                if (old != null) { _ledger.Release(old.Identity, old.Generation); _known.Remove(id); }
                _dirty.Remove(id); return;
            }
            ResidentialCapacityObservation observation = ResidentialCapacityObserver.Observe(id, ref buildings[id]);
            ResidentialLotRecord classes;
            bool bound = _owner.Classes.TryRecord(id, out classes);
            if (observation.Status != ResidentialCapacityObservationStatus.Known)
            {
                if (old != null && bound && classes.Generation == old.Generation && SameIdentity(classes.Identity, old.Identity) &&
                    observation.Phase == ResidentialCapacityObservationPhase.Unavailable && SameIdentity(observation.Identity, old.Identity))
                {
                    _ledger.Abandon(old.Identity, old.Generation);
                    _known[id] = WithPhase(old, ResidentialCapacityPhase.Unavailable); _dirty.Remove(id); return;
                }
                _dirty.Add(id); _unavailable = "Residential capacity unknown for building " + id + ": " + observation.Reason; return;
            }
            if (!bound && import) classes = _owner.Classes.ImportObserved(observation.Identity);
            if (classes == null || !SameIdentity(classes.Identity, observation.Identity))
            { _dirty.Add(id); _unavailable = "Native residential lot has no validated class generation."; return; }
            ResidentialCapacityRecord actual = MakeRecord(classes, observation);
            if (old == null) _ledger.Import(actual);
            else if (old.Generation != actual.Generation)
            {
                _ledger.Release(old.Identity, old.Generation); _ledger.Import(actual);
            }
            else if (!old.IsActive)
            {
                if (!SameCapacity(old, actual)) { _dirty.Add(id); _unavailable = "Unavailable stock changed capacity before reoccupation."; return; }
                _ledger.Reoccupy(old.Identity, old.Generation, actual.Phase);
            }
            else if (!SameCapacity(old, actual))
            {
                if (!_ledger.RefreshUpgrade(old.Identity, old.Generation, actual))
                { _dirty.Add(id); _unavailable = "Upgrade capacity identity could not reconcile."; return; }
            }
            else if (old.Phase != actual.Phase)
            {
                if (actual.Phase == ResidentialCapacityPhase.Completed) _ledger.Complete(actual.Identity, actual.Generation);
                else { _dirty.Add(id); _unavailable = "Unexpected backward native construction phase."; return; }
            }
            _known[id] = actual; _dirty.Remove(id);
        }
        private void AuditBeforeSpend()
        {
            if (!HasAuthorityPrerequisites) return;
            uint frame = Singleton<SimulationManager>.instance.m_currentFrameIndex;
            if (_auditFrame == frame) return;
            _auditFrame = frame;
            // Hooks perform full observations. This cheap independent phase/identity
            // sweep catches missed phase changes before a different frame can spend.
            Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            List<ushort> changed = new List<ushort>();
            foreach (KeyValuePair<ushort, ResidentialCapacityRecord> entry in _known)
            {
                ushort id = entry.Key; ResidentialCapacityRecord old = entry.Value;
                Building b = buildings[id];
                bool unavailable = (b.m_flags & (Building.Flags.Abandoned | Building.Flags.Collapsed | Building.Flags.Deleted)) != 0;
                bool upgrading = (b.m_flags & (Building.Flags.Upgrading | Building.Flags.Downgrading)) != 0;
                if (!FastIdentity(id, ref b, old.Identity) || b.m_level != old.NativeLevel ||
                    unavailable != !old.IsActive || upgrading || ((b.m_flags & Building.Flags.Completed) != 0) != (old.Phase == ResidentialCapacityPhase.Completed)) changed.Add(id);
            }
            foreach (ushort id in changed) ObserveCore(id, true);
            if (changed.Count != 0) _owner.PublishCapacityMutation();
        }
        internal bool TryCapture(byte[] exactClasses, out byte[] payload, out string reason)
        {
            payload = null; reason = null;
            if (_failure != null) { reason = _failure; return false; }
            if (_mutations.Count != 0 || _unresolvedMutations != 0 || _deferredCreated.Count != 0)
            { reason = "Native capacity mutation is in flight or unresolved."; return false; }
            if (!ModeRequested && _ledger == null && _loaded == null) return true;
            if (!_hooks || !_compatibility || !_baseline || _dirty.Count != 0 || _ledger == null)
            { reason = "Capacity pair is retained but observation/compatibility/baseline is not certified."; return false; }
            ResidentialCapacityLedgerState state;
            if (!_ledger.TryCapture(out state)) { reason = "Capacity reservation or physical creation is still in flight."; return false; }
            try
            {
                using (MemoryStream stream = new MemoryStream()) { ResidentialCapacityLedgerCodec.Write(stream, state, exactClasses); payload = stream.ToArray(); }
                // Write and ReadBound share validation; do not emit a pair whose class
                // identity/generation has drifted since capacity reconciliation.
                using (MemoryStream stream = new MemoryStream(payload, false)) ResidentialCapacityLedgerCodec.ReadBound(stream, exactClasses);
                return true;
            }
            catch (Exception error) { payload = null; reason = "Capacity/class pair validation failed: " + error.Message; return false; }
        }
        private bool Owns(ResidentialCapacityIntent intent)
        { return intent != null && ReferenceEquals(intent.Owner, this) && !intent.Ended && intent.Epoch == SessionEpoch &&
            intent.ThreadId == Thread.CurrentThread.ManagedThreadId && Current && OnSimulationThread(); }
        private long CurrentDay() { return Singleton<SimulationManager>.instance.m_currentGameTime.Date.Ticks / TimeSpan.TicksPerDay; }
        private ResidentialClassMask EligibleMask()
        {
            if (_ledger == null) return 0;
            ResidentialCapacityLedgerSnapshot state = _ledger.Snapshot(); long[] held = state.ReservedQuotaQ; int mask = 0;
            for (int i = 0; i < 3; i++)
            {
                ResidentialCapacityWindow w = _ledger.GetWindow((ResidentialClass)(i + 1));
                if (w != null && w.Day == _day && w.Phase == ResidentialCapacityWindowPhase.Open &&
                    w.OpeningExternalRevision == state.ExternalRevision && w.CommittedQ + held[i] < w.DesiredQ) mask |= 1 << i;
            }
            return (ResidentialClassMask)mask;
        }
        private float[] WindowRaw()
        {
            float[] raw = new float[3];
            for (int i = 0; i < 3; i++) { ResidentialCapacityWindow w = _ledger.GetWindow((ResidentialClass)(i + 1)); if (w != null) raw[i] = w.OpeningRaw; }
            return raw;
        }
        private ResidentialCapacityRecord MakeRecord(ResidentialLotRecord classes, ResidentialCapacityObservation observation)
        { return new ResidentialCapacityRecord(_ledger.ClassLedgerId, classes.Identity, classes.Generation, classes.OccupantClass,
            observation.Category, observation.NativeLevel, observation.ObservedHomeUnits,
            observation.Phase == ResidentialCapacityObservationPhase.Constructing ? ResidentialCapacityPhase.Constructing : ResidentialCapacityPhase.Completed, classes.Origin); }
        private static ResidentialCapacityRecord WithPhase(ResidentialCapacityRecord record, ResidentialCapacityPhase phase)
        { return new ResidentialCapacityRecord(record.ClassLedgerId, record.Identity, record.Generation, record.Class, record.Category, record.NativeLevel, record.HomeCount, phase, record.Origin); }
        private static bool SameCapacity(ResidentialCapacityRecord a, ResidentialCapacityRecord b)
        { return SameIdentity(a.Identity, b.Identity) && a.Category == b.Category && a.NativeLevel == b.NativeLevel && a.HomeCount == b.HomeCount && a.Class == b.Class && a.Origin == b.Origin; }
        private static bool SameIdentity(ResidentialLotIdentity a, ResidentialLotIdentity b)
        { return a != null && b != null && a.BuildingId == b.BuildingId && a.NativeBuildIndex == b.NativeBuildIndex && a.OriginXBits == b.OriginXBits && a.OriginZBits == b.OriginZBits &&
            a.PrefabName == b.PrefabName && a.Service == b.Service && a.SubService == b.SubService && a.Width == b.Width && a.Length == b.Length; }
        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits { [FieldOffset(0)] internal float Value; [FieldOffset(0)] internal int Bits; }
        private static bool FastIdentity(ushort id, ref Building building, ResidentialLotIdentity identity)
        {
            if ((building.m_flags & (Building.Flags.Created | Building.Flags.Deleted)) != Building.Flags.Created || identity.BuildingId != id ||
                building.m_buildIndex != identity.NativeBuildIndex || building.Width != identity.Width || building.Length != identity.Length) return false;
            FloatBits x = new FloatBits { Value = building.m_position.x }, z = new FloatBits { Value = building.m_position.z };
            BuildingInfo info = building.Info;
            return x.Bits == identity.OriginXBits && z.Bits == identity.OriginZBits && info != null && info.m_class != null && info.name == identity.PrefabName &&
                (int)info.m_class.m_service == identity.Service && (int)info.m_class.m_subService == identity.SubService && info.m_buildingAI != null &&
                info.m_buildingAI.GetType() == typeof(ResidentialBuildingAI) && ReferenceEquals(info.m_buildingAI.m_info, info);
        }
        private static bool TryCategory(BuildingInfo info, out ResidentialNominalCategory category)
        {
            category = 0;
            if (info == null || info.m_class == null || info.m_class.m_service != ItemClass.Service.Residential || info.m_buildingAI == null ||
                info.m_buildingAI.GetType() != typeof(ResidentialBuildingAI) || !ReferenceEquals(info.m_buildingAI.m_info, info)) return false;
            category = info.m_class.isResidentialLowGeneric ? ResidentialNominalCategory.LowGeneric : info.m_class.isResidentialLowEco ? ResidentialNominalCategory.LowEco : ResidentialNominalCategory.Other;
            return true;
        }
        private void Publish()
        {
            bool ready = CanControl;
            // No effect starts before graph readiness. A short physical-success /
            // publication barrier toggles authority without restarting a million-
            // citizen sweep; initial activation and real suspension do reconcile.
            bool prerequisites = HasAuthorityPrerequisites;
            if (!prerequisites) _reconcileOnActivation = true;
            if (_owner.Classes != null) _owner.Classes.SetCapacityEffectsReady(ready, ready ? _reconcileOnActivation : !prerequisites);
            if (ready) _reconcileOnActivation = false;
            string status = StatusReason(ready);
            _published = new ResidentialCapacityStatus(ModeRequested, ready, _hooks, _compatibility, status, _ledger == null ? null : _ledger.Snapshot(), _day, _dirty.Count + _deferredCreated.Count,
                _controlledIntents, _rejectedIntents, _reservedAttempts, _committedCreations, _lastNativeRoll, _lastNativeThreshold, _mutations.Count, _unresolvedMutations);
        }
        private static void Saturate(ref long count) { if (count != long.MaxValue) count++; }
        private string StatusReason(bool ready)
        {
            if (_failure != null) return _failure;
            if (!Current) return "Stopped.";
            if (!ModeRequested && !HasRetainedState) return "Capacity admission is not requested; native gameplay retained.";
            if (!_hooks || !_compatibility) return _unavailable;
            if (!_baseline) return "Waiting for a complete simulation-thread building baseline.";
            if (_dirty.Count != 0) return _unavailable;
            if (_deferredCreated.Count != 0) return "Physical creation awaits reserved class/capacity commit.";
            if (_unresolvedMutations != 0) return "Native mutations remain unresolved.";
            if (!AdmissionScopesOnly()) return "Native critical mutation is in progress.";
            if (!ModeRequested) return "Retained nominal stock observation only; native gameplay and resident wealth retained.";
            if (ready) return "RAW/Q residential V1 active; global BDS pool is not implemented.";
            return "Waiting for a fresh converged stock/external-input graph publication.";
        }
        private bool Revalidate()
        {
            string reason = "No approved effective patch certificate.";
            try { if (_certificate != null && ResidentialCapacityCompatibility.RevalidateForControl(_certificate, out reason)) return true; }
            catch (Exception error) { reason = "Effective metadata inspection failed: " + error.Message; }
            _compatibility = false; _certificate = null; Suspend("Capacity patch certificate invalidated: " + reason); return false;
        }
    }

    internal static class ResidentialCapacityTuning
    {
        private const string PayloadHash = "415ee2e86587f8126567191701e1b5ec4ce3cd614041df575d78b201e7885204";
        internal static float Load(string root)
        {
            string path = Path.Combine(root, "analysis/generated/sc4-overbuild-tuning/building-development.exemplar.bin");
            if (!File.Exists(path) || new FileInfo(path).Length != 563) throw new InvalidDataException("Pinned development exemplar is absent or not 563 bytes.");
            byte[] bytes = File.ReadAllBytes(path);
            byte[] hash; using (SHA256 algorithm = SHA256.Create()) hash = algorithm.ComputeHash(bytes);
            if (BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant() != PayloadHash) throw new InvalidDataException("Development tuning SHA256 differs from the independently extracted source.");
            // Full payload pin establishes the TGI/provenance; parse its real scalar.
            if (BitConverter.ToUInt32(bytes, 258) != 0x47e2c500u || bytes[262] != 0 || BitConverter.ToUInt32(bytes, 263) != 9u)
                throw new InvalidDataException("Overbuild property layout differs.");
            float value = BitConverter.ToSingle(bytes, 267);
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 1f) throw new InvalidDataException("Overbuild scalar is not finite and >=1.");
            return value;
        }
    }
}
