using System;
using System.Collections.Generic;
using System.IO;
using ColossalFramework;
using SC4Evolution.Simulation;
using UnityEngine;

namespace SC4Evolution.Mod.Economy
{
    public sealed class ResidentialClassStatus
    {
        public bool Enabled { get; private set; }
        public string Policy { get; private set; }
        public string ConfiguredPolicy { get; private set; }
        public string RetainedPolicy { get; private set; }
        public string EffectivePolicy { get; private set; }
        public ResidentialLocalDecisionStatus LocalDecisions { get; private set; }
        public int ImportedCount { get; private set; }
        public int SelectedCount { get; private set; }
        public int UnboundResidents { get; private set; }
        public string Status { get; private set; }
        internal ResidentialClassStatus(bool enabled, int imported, int selected, int unbound, string status,
            string configured, string retained, string effective, ResidentialLocalDecisionStatus decisions)
        { Enabled = enabled; Policy = EffectivePolicy = effective; ConfiguredPolicy = configured; RetainedPolicy = retained;
            LocalDecisions = decisions; ImportedCount = imported; SelectedCount = selected; UnboundResidents = unbound; Status = status; }
    }

    // Only the simulation thread inspects game buffers. Save callbacks serialize the
    // already validated ledger under its lock, never inspecting a live city.
    internal sealed class ResidentialClassBridge
    {
        private readonly object _sync = new object();
        private readonly EconomicBridge _owner;
        private ResidentialClassLedger _ledger;
        private byte[] _pending;
        private bool _bound, _effects, _requested, _selectionAllowed;
        private bool _capacityEffectsReady;
        private int _cursor;
        private int _unbound;
        private int _importedCount, _selectedCount;
        private readonly Queue<ushort> _dirtyHomes = new Queue<ushort>();
        private readonly HashSet<ushort> _dirtyHomeSet = new HashSet<ushort>();
        private ushort _activeDirtyHome;
        private uint _nextDirtyUnit;
        private int _dirtyChainSteps;
        private string _message = "Waiting for simulation-thread binding.";
        private string _loadFailure;
        private string _restorationOnlyReason;
        private bool _suppressSave;
        private ResidentialSelectionPolicyId _configuredPolicy = ResidentialSelectionPolicyId.NativeSafetyOnlyV1;
        private ResidentialWaterObserver _water;
        private readonly ulong[] _decisions = new ulong[10];
        private ResidentialLocalDecision _lastDecision;
        private volatile ResidentialClassStatus _published;
        internal ResidentialClassStatus Published { get { return _published; } }
        internal bool EffectsEnabled { get { lock (_sync) { return _effects; } } }
        internal Guid LedgerId { get { lock (_sync) { return _ledger == null ? Guid.Empty : _ledger.LedgerId; } } }
        internal bool Bound { get { lock (_sync) { return _bound && _ledger != null; } } }
        internal bool CapacityPolicyBound { get { lock (_sync) { return _ledger != null && _selectionAllowed && ResidentialSelectionPolicy.Identify(_ledger) == ResidentialSelectionPolicyId.SC4WaterRawCapacityV1; } } }
        internal ResidentialLotRecord[] Records() { lock (_sync) { return _ledger == null ? new ResidentialLotRecord[0] : _ledger.GetRecords(); } }
        internal bool TryRecord(ushort id, out ResidentialLotRecord record)
        { lock (_sync) { record = null; return _ledger != null && TryRecordLocked(id, out record); } }
        internal void SetCapacityEffectsReady(bool ready, bool reconcileResidents = true)
        {
            lock (_sync)
            {
                _capacityEffectsReady = ready;
                if (_configuredPolicy != ResidentialSelectionPolicyId.SC4WaterRawCapacityV1) return;
                bool effects = ready && _requested && _selectionAllowed;
                if (_effects != effects) { _effects = effects; if (reconcileResidents) _cursor = 1; }
                PublishLocked();
            }
        }
        internal static readonly byte[] PolicyHash = ResidentialSelectionPolicy.PolicyHash(ResidentialSelectionPolicyId.NativeSafetyOnlyV1);
        internal const uint PolicyVersion = 1;

        internal ResidentialClassBridge(EconomicBridge owner, byte[] pending, bool requested, string loadFailure = null)
        {
            _owner = owner; _pending = pending; _requested = requested; _loadFailure = loadFailure;
            if (loadFailure != null) { _bound = true; _pending = null; _requested = false; _message = "Class load failed; effects disabled, save key preserved: " + loadFailure; }
            PublishLocked();
        }

        internal void ConfigureSelectionPolicy(ResidentialSelectionPolicyId policy, ResidentialWaterObserver water)
        {
            lock (_sync)
            {
                if (_ledger != null) throw new InvalidOperationException("Configure selection before city binding.");
                _configuredPolicy = policy; _water = water;
                if (_requested && policy != ResidentialSelectionPolicyId.NativeSafetyOnlyV1 && (water == null || !water.IsValid))
                    RequireRestorationOnly("Water profile unavailable: " + (water == null ? "not loaded" : water.FailureReason));
                PublishLocked();
            }
        }

        internal bool Maintain(int budget)
        {
            lock (_sync)
            {
                if (!_bound) BindLocked();
                if (_ledger == null) return true;
                Citizen[] people = Singleton<CitizenManager>.instance.m_citizens.m_buffer;
                if (_ledger.Count == 0) { _cursor = people.Length; return true; }
                // Affected homes get a small separate share. Construction/upgrades
                // cannot reset or consume the entire full-sweep budget indefinitely.
                int spent = ReconcileDirtyHomesLocked(Math.Min(512, budget / 4));
                int end = Math.Min(people.Length, _cursor + budget - spent);
                for (; _cursor < end; _cursor++) MirrorCitizenLocked(ref people[_cursor], _effects);
                bool done = _cursor >= people.Length;
                PublishLocked();
                return done;
            }
        }

        private void QueueHomeLocked(ushort building)
        { if (_dirtyHomeSet.Add(building)) _dirtyHomes.Enqueue(building); }
        private int ReconcileDirtyHomesLocked(int budget)
        {
            int spent = 0;
            CitizenManager manager = Singleton<CitizenManager>.instance;
            CitizenUnit[] units = manager.m_units.m_buffer;
            while (spent + 5 <= budget)
            {
                if (_activeDirtyHome == 0)
                {
                    if (_dirtyHomes.Count == 0) break;
                    ushort home = _dirtyHomes.Dequeue();
                    if (!_dirtyHomeSet.Contains(home)) continue;
                    ResidentialLotRecord record;
                    if (!TryRecordLocked(home, out record)) { _dirtyHomeSet.Remove(home); continue; }
                    _activeDirtyHome = home;
                    _nextDirtyUnit = Singleton<BuildingManager>.instance.m_buildings.m_buffer[home].m_citizenUnits;
                    _dirtyChainSteps = 0;
                }
                if (_nextDirtyUnit == 0 || _nextDirtyUnit >= units.Length || ++_dirtyChainSteps > units.Length)
                { _dirtyHomeSet.Remove(_activeDirtyHome); _activeDirtyHome = 0; continue; }
                CitizenUnit unit = units[_nextDirtyUnit];
                _nextDirtyUnit = unit.m_nextUnit;
                spent += 5;
                if (unit.m_building != _activeDirtyHome ||
                    (unit.m_flags & (CitizenUnit.Flags.Created | CitizenUnit.Flags.Home)) != (CitizenUnit.Flags.Created | CitizenUnit.Flags.Home)) continue;
                for (int i = 0; i < 5; i++)
                {
                    uint id = unit.GetCitizen(i);
                    if (id != 0 && id < manager.m_citizens.m_buffer.Length && manager.m_citizens.m_buffer[id].m_homeBuilding == _activeDirtyHome)
                        MirrorCitizenLocked(ref manager.m_citizens.m_buffer[id], _effects);
                }
            }
            return spent;
        }

        private void BindLocked()
        {
            if (!Singleton<BuildingManager>.exists || !Singleton<CitizenManager>.exists)
                throw new InvalidOperationException("Residential class managers are not ready.");
            ResidentialClassLedger candidate;
            if (_pending == null)
            {
                ResidentialSelectionPolicyId freshPolicy = _requested && _configuredPolicy != ResidentialSelectionPolicyId.NativeSafetyOnlyV1 && _water != null && _water.IsValid ?
                    _configuredPolicy : ResidentialSelectionPolicyId.NativeSafetyOnlyV1;
                candidate = new ResidentialClassLedger(Guid.NewGuid(), ResidentialSelectionPolicy.Version(freshPolicy), ResidentialSelectionPolicy.PolicyHash(freshPolicy));
            }
            else
            {
                using (MemoryStream stream = new MemoryStream(_pending, false)) candidate = ResidentialClassLedgerCodec.Read(stream);
                int length = Singleton<BuildingManager>.instance.m_buildings.m_buffer.Length;
                if (candidate.Count >= length) throw new InvalidDataException("Class ledger count exceeds city building buffer.");
                foreach (ResidentialLotRecord record in candidate.GetRecords())
                    if (record.Identity.BuildingId >= length) throw new InvalidDataException("Class ledger ID exceeds city building buffer.");
            }
            ResidentialWaterProfile waterProfile = _water == null ? null : _water.Profile;
            candidate = PreparePolicyLedger(candidate, _requested, _restorationOnlyReason != null, _configuredPolicy, waterProfile);
            bool selectionAllowed = SelectionPolicyMatches(candidate, _restorationOnlyReason != null, _configuredPolicy, waterProfile);
            foreach (ResidentialLotRecord record in candidate.GetRecords())
            {
                ResidentialLotIdentity identity; ResidentialLotRecord found;
                if (!TryIdentity(record.Identity.BuildingId, out identity) || !candidate.TryGet(identity, out found)) candidate.Remove(record.Identity.BuildingId);
            }
            if (_requested && selectionAllowed)
            {
                Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
                for (int id = 1; id < buildings.Length; id++)
                {
                    ResidentialLotIdentity identity;
                    if (TryIdentity((ushort)id, out identity))
                        candidate.ImportExisting(identity, Bootstrap(buildings[id].Info.m_class.m_level));
                }
            }
            // Decode, city validation and all imports finish in a temporary ledger.
            // Generation overflow or another failed import never partially binds it.
            ReplaceLedgerLocked(candidate); _pending = null; _bound = true; _selectionAllowed = selectionAllowed;
            _effects = _requested && _selectionAllowed && (_configuredPolicy != ResidentialSelectionPolicyId.SC4WaterRawCapacityV1 || _capacityEffectsReady);
            _message = _restorationOnlyReason != null ? "Restoration only; saving suppressed to preserve the original key: " + _restorationOnlyReason :
                !_selectionAllowed ? "Retained policy is not enabled by this configuration; preserved valid records, new selection and mirrors disabled." :
                (_effects ? "Lot classes enabled; initial resident reconciliation in progress; LocalPolicy=" + ResidentialSelectionPolicy.Name(_configuredPolicy) + "." : "Ledger retained; restoring known residential mirrors to native home-prefab class.");
            _cursor = 1;
        }

        // Kept pure so opt-in/migration and fallback are verifiable without live buffers.
        private static ResidentialClassLedger PreparePolicyLedger(ResidentialClassLedger ledger, bool requested, bool restorationOnly,
            ResidentialSelectionPolicyId configured, ResidentialWaterProfile water)
        {
            ResidentialSelectionPolicyId retained = ResidentialSelectionPolicy.Identify(ledger);
            if (requested && !restorationOnly && configured == ResidentialSelectionPolicyId.SC4WaterRawCapacityV1 && water != null && retained != ResidentialSelectionPolicyId.Unknown)
                return ResidentialSelectionPolicy.MigrateKnownToRawCapacityV1(ledger, water);
            if (requested && !restorationOnly && configured == ResidentialSelectionPolicyId.SC4WaterPrerequisiteV1 && water != null &&
                ResidentialSelectionPolicy.Identify(ledger) == ResidentialSelectionPolicyId.NativeSafetyOnlyV1)
                return ResidentialSelectionPolicy.MigrateKnownNativeSafetyV1ToWaterV1(ledger, water);
            return ledger;
        }
        private static bool SelectionPolicyMatches(ResidentialClassLedger ledger, bool restorationOnly, ResidentialSelectionPolicyId configured, ResidentialWaterProfile water)
        {
            if (restorationOnly || configured == ResidentialSelectionPolicyId.Unknown || ResidentialSelectionPolicy.Identify(ledger) != configured) return false;
            if (configured == ResidentialSelectionPolicyId.NativeSafetyOnlyV1) return true;
            if (water == null) return false;
            try { ResidentialSelectionPolicy.ValidateWaterProfile(water); return true; }
            catch (ArgumentException) { return false; }
            catch (InvalidDataException) { return false; }
        }

        internal void Disable(string reason)
        {
            lock (_sync) { _effects = false; _requested = false; _selectionAllowed = false; _cursor = 1; _message = reason + "; restoring known resident mirrors to native."; PublishLocked(); }
        }
        internal void RejectBinding(string reason)
        {
            lock (_sync)
            {
                Disable(reason);
                if (!_bound) { _loadFailure = reason; _pending = null; _bound = true; }
            }
        }
        internal void RequireRestorationOnly(string reason)
        {
            lock (_sync)
            {
                // Valid payload remains pending for simulation-thread identity binding.
                // Missing optional metadata hooks suppress selection/saving, not repair.
                _restorationOnlyReason = reason;
                _suppressSave = true; _effects = false; _requested = false; _selectionAllowed = false;
                _message = "Restoration only; saving suppressed to preserve the original key: " + reason;
                PublishLocked();
            }
        }

        internal byte[] Save()
        {
            lock (_sync)
            {
                if (_loadFailure != null || _suppressSave) return null;
                if (_ledger == null) return null; // Initial binding has not validated this city's identities yet.
                using (MemoryStream stream = new MemoryStream())
                { ResidentialClassLedgerCodec.Write(stream, _ledger); return stream.ToArray(); }
            }
        }

        internal void Audit()
        {
            lock (_sync)
            {
                if (_bound && _ledger != null)
                {
                    AuditLocked();
                    if (_cursor >= Singleton<CitizenManager>.instance.m_citizens.m_buffer.Length) _cursor = 1;
                }
            }
        }
        private void AuditLocked()
        {
            bool changed = false;
            try
            {
                foreach (ResidentialLotRecord record in _ledger.GetRecords())
                {
                    ResidentialLotIdentity identity; ResidentialLotRecord found;
                    if (!TryIdentity(record.Identity.BuildingId, out identity) || !_ledger.TryGet(identity, out found))
                    { changed |= _ledger.Remove(record.Identity.BuildingId); _message = "Removed stale/relocated/category-changed residential fingerprint; left unbound."; }
                }
            }
            finally { if (changed) RecountOriginsLocked(); }
        }

        internal bool TryClass(ushort building, out ResidentialClass value)
        {
            lock (_sync)
            {
                ResidentialLotRecord record;
                if (_bound && _ledger != null && TryRecordLocked(building, out record))
                { value = record.OccupantClass; return true; }
                value = 0; return false;
            }
        }
        internal int[] CensusClasses()
        {
            lock (_sync)
            {
                int[] result = new int[Singleton<BuildingManager>.instance.m_buildings.m_buffer.Length];
                if (_ledger != null) foreach (ResidentialLotRecord record in _ledger.GetRecords())
                {
                    ResidentialLotRecord valid;
                    if (TryRecordLocked(record.Identity.BuildingId, out valid)) result[record.Identity.BuildingId] = (int)valid.OccupantClass;
                }
                return result;
            }
        }
        private bool TryRecordLocked(ushort building, out ResidentialLotRecord record)
        {
            ResidentialLotIdentity identity;
            if (TryIdentity(building, out identity)) return _ledger.TryGet(identity, out record);
            record = null; return false;
        }

        internal ResidentialClassChoice ChooseAt(SegmentedDemandSnapshot snapshot, Vector3 position)
        {
            lock (_sync)
            {
                // The RAW policy is authorized only by a capacity reservation. Never
                // silently reuse normalized demand through a legacy caller.
                if (_configuredPolicy == ResidentialSelectionPolicyId.SC4WaterRawCapacityV1) return ResidentialWaterPrerequisite.NativeFallback();
                ResidentialWaterObservation observation = new ResidentialWaterObservation(ResidentialWaterEvidence.Unknown, false, 0, "DemandUnavailable");
                ResidentialClassChoice choice;
                if (!_effects || !_selectionAllowed || snapshot == null || !snapshot.Converged || !snapshot.GameplayEnabled ||
                    !Singleton<SimulationManager>.exists || unchecked(Singleton<SimulationManager>.instance.m_currentFrameIndex - snapshot.Census.Frame) > 2048u)
                    choice = ResidentialWaterPrerequisite.NativeFallback();
                else if (_configuredPolicy == ResidentialSelectionPolicyId.SC4WaterPrerequisiteV1)
                {
                    // Exactly one read before native creation/demolition; never at commit.
                    observation = _water.Observe(position);
                    choice = ResidentialWaterPrerequisite.Choose(snapshot.Nodes, _water.Profile, observation.Evidence);
                    if (choice.Kind == ResidentialChoiceKind.NativeFallback && observation.Evidence != ResidentialWaterEvidence.Unknown)
                        observation.Reason = "InvalidResidentialDemand";
                    Increment((int)observation.Evidence == 2 ? 0 : (int)observation.Evidence == 1 ? 1 : 2);
                    if (observation.Reason == "UnsupportedQueryDomain") Increment(3);
                    if (observation.Evidence == ResidentialWaterEvidence.Unserved && _water.Profile.HighMaximumStage == 0)
                        foreach (DemandNodeState node in snapshot.Nodes)
                            if (node.Key == 0x21030 && node.ActiveDemand > 0 && node.NormalizedDemand > 0) { Increment(4); break; }
                }
                else
                {
                    observation.Reason = "NativeSafetyOnly";
                    choice = ResidentialWaterPrerequisite.ChoosePermitted(snapshot.Nodes, ResidentialClassMask.All, ResidentialWaterEvidence.Unknown);
                }
                if (choice.Kind == ResidentialChoiceKind.Selected) Increment(4 + (int)choice.Class);
                else Increment(choice.Kind == ResidentialChoiceKind.ControlledReject ? 8 : 9);
                _lastDecision = new ResidentialLocalDecision(Singleton<SimulationManager>.exists ? Singleton<SimulationManager>.instance.m_currentFrameIndex : 0,
                    position, ResidentialSelectionPolicy.Name(_configuredPolicy), snapshot == null ? new DemandNodeState[0] : snapshot.Nodes, observation, choice);
                PublishLocked();
                return choice;
            }
        }
        private void Increment(int index) { if (_decisions[index] != ulong.MaxValue) _decisions[index]++; }
        internal static ResidentialClass ChooseClass(DemandNodeState[] states)
        {
            return ResidentialWaterPrerequisite.ChoosePermitted(states, ResidentialClassMask.All, ResidentialWaterEvidence.Unknown).Class;
        }

        internal void Commit(ushort building, uint expectedIndex, BuildingInfo expectedPrefab, ResidentialClass selected)
        {
            lock (_sync)
            {
                ResidentialLotIdentity identity;
                if (!_effects || selected == 0 || !TryIdentity(building, out identity) || identity.NativeBuildIndex != expectedIndex ||
                    !ReferenceEquals(Singleton<BuildingManager>.instance.m_buildings.m_buffer[building].Info, expectedPrefab))
                { _message = "Created lot did not match pending identity; left unbound."; PublishLocked(); return; }
                CommitRecordLocked(identity, selected);
                QueueHomeLocked(building);
                _message = "Selected lot class committed independently of native level; LocalPolicy=" + ResidentialSelectionPolicy.Name(_configuredPolicy) + ".";
                PublishLocked();
            }
        }

        internal void Released(ushort building)
        {
            lock (_owner.CapacitySync)
            {
                ResidentialCapacityBridge capacity = _owner.Capacity;
                if (capacity != null) capacity.BeforeRelease(building);
                lock (_sync) { if (_ledger != null) { RemoveRecordLocked(building); _dirtyHomeSet.Remove(building); PublishLocked(); } }
            }
        }

        // Native fallback observations are explicitly bootstrap imports, never Selected.
        // Caller must first obtain a Known local capacity observation on simulation thread.
        internal ResidentialLotRecord ImportObserved(ResidentialLotIdentity identity)
        {
            lock (_sync)
            {
                if (_ledger == null) return null;
                ResidentialLotRecord record;
                if (_ledger.TryGet(identity, out record)) return record;
                Building building = Singleton<BuildingManager>.instance.m_buildings.m_buffer[identity.BuildingId];
                ResidentialLotIdentity current;
                if (!TryIdentity(identity.BuildingId, out current) || !SameIdentity(identity, current)) return null;
                _ledger.ImportExisting(identity, Bootstrap(building.Info.m_class.m_level));
                RecountOriginsLocked(); PublishLocked();
                return _ledger.TryGet(identity, out record) ? record : null;
            }
        }
        internal ResidentialLotRecord CommitCapacityClass(ushort id, uint index, BuildingInfo prefab, ResidentialClass selected)
        {
            lock (_sync)
            {
                ResidentialLotIdentity identity;
                if (!_requested || !_selectionAllowed || _configuredPolicy != ResidentialSelectionPolicyId.SC4WaterRawCapacityV1 ||
                    !TryIdentity(id, out identity) || identity.NativeBuildIndex != index ||
                    !ReferenceEquals(Singleton<BuildingManager>.instance.m_buildings.m_buffer[id].Info, prefab)) return null;
                CommitRecordLocked(identity, selected); QueueHomeLocked(id); PublishLocked();
                ResidentialLotRecord record; return _ledger.TryGet(identity, out record) ? record : null;
            }
        }

        internal ulong Generation(ResidentialLotIdentity identity)
        {
            lock (_sync) { ResidentialLotRecord record; return _ledger != null && _ledger.TryGet(identity, out record) ? record.Generation : 0; }
        }
        internal bool MatchesAttempt(ResidentialLotIdentity before, ulong generation)
        {
            lock (_sync)
            {
                ResidentialLotIdentity now;
                if (!TryIdentity(before.BuildingId, out now) || !SameIdentity(before, now)) return false;
                return Generation(now) == generation;
            }
        }
        private static bool SameIdentity(ResidentialLotIdentity a, ResidentialLotIdentity b)
        { return a.BuildingId == b.BuildingId && a.NativeBuildIndex == b.NativeBuildIndex && a.OriginXBits == b.OriginXBits && a.OriginZBits == b.OriginZBits &&
            string.Equals(a.PrefabName, b.PrefabName, StringComparison.Ordinal) && a.Service == b.Service && a.SubService == b.SubService && a.Width == b.Width && a.Length == b.Length; }

        internal ResidentialLotIdentity BeforeUpgrade(ushort building)
        {
            lock (_sync)
            {
                if (!_bound)
                    try { BindLocked(); }
                    catch (Exception error) { RejectBinding("Upgrade encountered rejected retained class data: " + error.Message); return null; }
                ResidentialLotRecord record;
                return _ledger != null && TryRecordLocked(building, out record) ? record.Identity : null;
            }
        }
        internal void AfterUpgrade(ResidentialLotIdentity before)
        {
            if (before == null) return;
            lock (_sync)
            {
                ResidentialLotIdentity after;
                if (!TryIdentity(before.BuildingId, out after) || !_ledger.RefreshUpgradeIdentity(before, after))
                { RemoveRecordLocked(before.BuildingId); _message = "Upgrade identity changed unexpectedly; record invalidated, lot left unbound."; }
                QueueHomeLocked(before.BuildingId); PublishLocked();
            }
        }

        internal void Mirror(ref Citizen citizen)
        { lock (_sync) { if (_effects) MirrorCitizenLocked(ref citizen, true); } }
        private void MirrorCitizenLocked(ref Citizen citizen, bool controlled)
        {
            const Citizen.Flags excluded = Citizen.Flags.Tourist | Citizen.Flags.DummyTraffic | Citizen.Flags.Dead;
            if ((citizen.m_flags & (Citizen.Flags.Created | excluded)) != Citizen.Flags.Created || citizen.m_homeBuilding == 0) return;
            ResidentialLotRecord record;
            if (!TryRecordLocked(citizen.m_homeBuilding, out record)) return;
            Citizen.Wealth desired;
            if (TryMirrorValue(controlled, citizen.WealthLevel, record.OccupantClass,
                Singleton<BuildingManager>.instance.m_buildings.m_buffer[citizen.m_homeBuilding].Info.m_class.m_level, out desired)) citizen.WealthLevel = desired;
        }
        internal static bool TryMirrorValue(bool controlled, Citizen.Wealth current, ResidentialClass lotClass, ItemClass.Level nativeLevel, out Citizen.Wealth desired)
        {
            desired = current;
            Citizen.Wealth mirror = (Citizen.Wealth)((int)lotClass - 1);
            if (!controlled && current != mirror) return false;
            desired = controlled ? mirror : (Citizen.Wealth)((int)Bootstrap(nativeLevel) - 1);
            return true;
        }
        internal void MirrorUnit(ref CitizenUnit unit)
        {
            lock (_sync)
            {
                if (!_effects || (unit.m_flags & (CitizenUnit.Flags.Created | CitizenUnit.Flags.Home)) != (CitizenUnit.Flags.Created | CitizenUnit.Flags.Home)) return;
                Citizen[] people = Singleton<CitizenManager>.instance.m_citizens.m_buffer;
                for (int i = 0; i < 5; i++)
                {
                    uint id = unit.GetCitizen(i);
                    if (id == 0 || id >= people.Length) continue;
                    if (people[id].m_homeBuilding != unit.m_building) { _message = "Stale HOME member reference ignored."; continue; }
                    MirrorCitizenLocked(ref people[id], true);
                }
            }
        }

        internal void SetUnboundCount(int count)
        { lock (_sync) { _unbound = count; PublishLocked(); } }
        private void ReplaceLedgerLocked(ResidentialClassLedger ledger)
        { _ledger = ledger; RecountOriginsLocked(); }
        private void CommitRecordLocked(ResidentialLotIdentity identity, ResidentialClass selected)
        {
            bool changed = _ledger.Remove(identity.BuildingId);
            try { _ledger.CommitCreated(identity, selected); changed = true; }
            // A generation-exhaustion failure after removal must not leave stale counts.
            finally { if (changed) RecountOriginsLocked(); }
        }
        private void RemoveRecordLocked(ushort building)
        { if (_ledger.Remove(building)) RecountOriginsLocked(); }
        private void RecountOriginsLocked()
        {
            _importedCount = _selectedCount = 0;
            if (_ledger != null) foreach (ResidentialLotRecord record in _ledger.GetRecords())
                if (record.Origin == ClassOrigin.ImportedNative) _importedCount++; else _selectedCount++;
        }
        private void PublishLocked()
        {
            // Called for every candidate, including repeated rejection/fallback.
            // Read cached counts; enumeration belongs only to actual ledger mutations.
            _published = new ResidentialClassStatus(_effects, _importedCount, _selectedCount, _unbound, _message,
                _configuredPolicy == ResidentialSelectionPolicyId.Unknown ? "None" : ResidentialSelectionPolicy.Name(_configuredPolicy),
                _ledger == null ? "None" : ResidentialSelectionPolicy.Name(ResidentialSelectionPolicy.Identify(_ledger)),
                _effects ? ResidentialSelectionPolicy.Name(_configuredPolicy) : "None", new ResidentialLocalDecisionStatus(_decisions, _lastDecision));
        }

        internal static bool TryIdentity(ushort id, out ResidentialLotIdentity identity)
        {
            identity = null;
            if (id == 0 || !Singleton<BuildingManager>.exists) return false;
            Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            if (id >= buildings.Length) return false;
            Building building = buildings[id];
            if ((building.m_flags & (Building.Flags.Created | Building.Flags.Deleted)) != Building.Flags.Created) return false;
            BuildingInfo info = building.Info;
            if (info == null || info.m_class == null || info.m_class.m_service != ItemClass.Service.Residential || !(info.m_buildingAI is ResidentialBuildingAI)) return false;
            try
            {
                identity = new ResidentialLotIdentity(id, building.m_buildIndex, FloatBits(building.m_position.x), FloatBits(building.m_position.z),
                    info.name, (int)info.m_class.m_service, (int)info.m_class.m_subService, checked((byte)building.Width), checked((byte)building.Length));
                return true;
            }
            catch (ArgumentException) { return false; }
            catch (OverflowException) { return false; }
        }
        internal static int FloatBits(float value) { return BitConverter.ToInt32(BitConverter.GetBytes(value), 0); }
        private static ResidentialClass Bootstrap(ItemClass.Level level)
        { return level == ItemClass.Level.Level1 ? ResidentialClass.Low : level <= ItemClass.Level.Level3 ? ResidentialClass.Medium : ResidentialClass.High; }
    }
}
