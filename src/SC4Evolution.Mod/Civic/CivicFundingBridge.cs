using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ColossalFramework;
using SC4Evolution.Simulation;
using UnityEngine;

namespace SC4Evolution.Mod.Civic
{
    public sealed class CivicFundingSnapshot
    {
        internal readonly CivicFundingBridge Owner;
        public ushort BuildingId { get; private set; }
        public int Session { get; private set; }
        public CivicFacilityIdentity Identity { get; private set; }
        public bool Supported { get; private set; }
        public bool CanApply { get; private set; }
        public string Status { get; private set; }
        public int? CapacityOverridePercent { get; private set; }
        public int? CoverageOverridePercent { get; private set; }
        public int GlobalDayPercent { get; private set; }
        public int GlobalNightPercent { get; private set; }
        public int GlobalCurrentPercent { get; private set; }
        public int GlobalAveragePercent { get; private set; }
        public int ResolvedCapacityPercent { get; private set; }
        public int ResolvedCoveragePercent { get; private set; }
        public int AverageCapacityPercent { get; private set; }
        public int AverageCoveragePercent { get; private set; }
        public double EfficiencyFactor { get; private set; }
        public double CoverageFactor { get; private set; }
        public double AverageEfficiencyFactor { get; private set; }
        public double AverageCoverageFactor { get; private set; }
        public int RawCapacity { get; private set; }
        public int BudgetTargetCapacity { get; private set; }
        public float CurrentRange { get; private set; }
        public int? LastFinalProductionRate { get; private set; }
        public int? LastEffectiveCapacity { get; private set; }
        public int? LastNativeMaintenanceCost { get; private set; }
        public int? LastRequestedMaintenanceCost { get; private set; }
        public int? LastMaintenanceAvailability { get; private set; }
        public long ObservationSequence { get; private set; }
        public long MaintenanceObservations { get; private set; }
        public long ProductionObservations { get; private set; }
        public long MaintenanceFetchCalls { get; private set; }
        public int? LastResourceAmount { get; private set; }
        public float? LastResourceRadius { get; private set; }
        public int? LastCureThreshold { get; private set; }
        public int? LastAmbulanceTarget { get; private set; }
        internal CivicFundingSnapshot(CivicFundingBridge owner, int session, ushort id, CivicFundingSettings settings,
            bool ready, string status, CivicFundingObservation observed, ref Building data)
        {
            Owner = owner; Session = session; BuildingId = id; Supported = settings != null; CanApply = ready && Supported; Status = status;
            if (settings != null)
            {
                Identity = settings.Identity; CapacityOverridePercent = settings.CapacityOverride; CoverageOverridePercent = settings.CoverageOverride;
                GlobalDayPercent = settings.Day; GlobalNightPercent = settings.Night; GlobalCurrentPercent = settings.Current; GlobalAveragePercent = settings.Average;
                ResolvedCapacityPercent = settings.Capacity; ResolvedCoveragePercent = settings.Coverage;
                AverageCapacityPercent = settings.AverageCapacity; AverageCoveragePercent = settings.AverageCoverage;
                EfficiencyFactor = settings.CurrentEvaluation.EfficiencyFactor; CoverageFactor = settings.CurrentEvaluation.CoverageFactor;
                AverageEfficiencyFactor = settings.AverageEvaluation.EfficiencyFactor; AverageCoverageFactor = settings.AverageEvaluation.CoverageFactor;
                RawCapacity = settings.RawCapacity; BudgetTargetCapacity = CivicFundingMath.Capacity(settings, 100);
                CurrentRange = CivicFundingMath.Range(settings, ref data);
            }
            if (observed != null)
            {
                LastFinalProductionRate = observed.FinalProduction; LastEffectiveCapacity = observed.Capacity;
                LastNativeMaintenanceCost = observed.NativeCost; LastRequestedMaintenanceCost = observed.RequestedCost; LastMaintenanceAvailability = observed.Availability;
                ObservationSequence = observed.Sequence; MaintenanceObservations = observed.MaintenanceCount; ProductionObservations = observed.ProductionCount;
                MaintenanceFetchCalls = observed.FetchCount;
                LastResourceAmount = observed.ResourceAmount; LastResourceRadius = observed.ResourceRadius;
                LastCureThreshold = observed.Cure; LastAmbulanceTarget = observed.Ambulances;
            }
        }
    }

    internal sealed class CivicFundingSettings
    {
        internal readonly CivicFacilityIdentity Identity;
        internal readonly BuildingAI Ai;
        internal readonly BuildingInfo Prefab;
        internal readonly int? CapacityOverride, CoverageOverride;
        internal readonly int Day, Night, Current, Average, Capacity, Coverage, AverageCapacity, AverageCoverage, RawCapacity;
        internal readonly float Radius;
        internal readonly CivicFundingEvaluation CurrentEvaluation, AverageEvaluation;
        internal CivicFundingSettings(CivicFacilityIdentity identity, BuildingAI ai, int? capacity, int? coverage,
            int day, int night, int current, int rawCapacity, float radius, int dayLength = 5, int nightLength = 3)
        {
            Identity = identity; Ai = ai; Prefab = ai.m_info; CapacityOverride = capacity; CoverageOverride = coverage; Day = day; Night = night; Current = current;
            // Native day/night weights are mutable static fields. Preserve their
            // live values, and reject arithmetic outside the native int domain.
            int total = checked(dayLength + nightLength), weighted = checked(day * dayLength + night * nightLength);
            if (dayLength < 0 || nightLength < 0 || total <= 0) throw new InvalidDataException("Native civic day/night weights are unsupported.");
            Average = weighted / total; Capacity = capacity ?? current; Coverage = coverage ?? current;
            AverageCapacity = capacity ?? Average; AverageCoverage = coverage ?? Average; RawCapacity = rawCapacity; Radius = radius;
            CurrentEvaluation = CivicFundingEvaluator.Evaluate(identity.Archetype, Capacity, Coverage, 0);
            AverageEvaluation = CivicFundingEvaluator.Evaluate(identity.Archetype, AverageCapacity, AverageCoverage, 0);
        }
    }
    internal sealed class CivicFundingObservation
    {
        internal CivicFacilityIdentity Identity;
        internal long Sequence, MaintenanceCount, ProductionCount, FetchCount;
        internal int? NativeCost, RequestedCost, Availability, FinalProduction, Capacity, ResourceAmount, Cure, Ambulances;
        internal float? ResourceRadius;
    }
    internal static class CivicFundingMath
    {
        // Exact half-percent curve units. Binary floating point is reserved for
        // dimensionless display/radius; it must not make ceil(100 * 1.1) be 111.
        internal static int EfficiencyUnits(int percent)
        { if (percent < 0 || percent > 255) throw new ArgumentOutOfRangeException("percent"); return percent <= 100 ? 2 * percent : percent >= 120 ? 220 : 100 + percent; }
        internal static int CeilingRatio(long numerator, long denominator)
        { if (numerator < 0 || denominator <= 0) throw new ArgumentOutOfRangeException("numerator"); return checked((int)(checked(numerator + denominator - 1) / denominator)); }
        internal static int ResourceAmount(int amount, int percent)
        { return CeilingRatio(checked((long)amount * EfficiencyUnits(percent)), 200); }
        internal static int CureThreshold(int rawCapacity, int curingRate, int finalProduction, int percent)
        { return rawCapacity == 0 ? 0 : CeilingRatio(checked((long)curingRate * finalProduction * 100 * EfficiencyUnits(percent)), checked((long)rawCapacity * 200)); }
        internal static int AmbulanceTarget(int count, int finalProduction, int percent)
        { return CeilingRatio(checked((long)count * finalProduction * EfficiencyUnits(percent)), 100 * 200); }
        internal static int Capacity(CivicFundingSettings settings, int finalProduction)
        {
            bool school = settings.Ai.GetType() == typeof(SchoolAI);
            int value = CeilingRatio(checked((long)settings.RawCapacity * (school ? finalProduction : 100) * EfficiencyUnits(settings.Capacity)), 100 * 200);
            return school ? Math.Min(value, checked(settings.RawCapacity * 5 / 4)) : value;
        }
        internal static float Range(CivicFundingSettings settings, ref Building data)
        {
            int rate = data.m_productionRate;
            if ((data.m_flags & (Building.Flags.Active | Building.Flags.Evacuating)) != Building.Flags.Active) rate = 0;
            else if ((data.m_flags & Building.Flags.RateReduced) != 0) rate = Math.Min(rate, 50);
            return checked((float)(rate * settings.Radius * settings.CurrentEvaluation.CoverageFactor / 100.0));
        }
    }

    // Pure identity predicate used by an admitted native cycle. Null active owner
    // means Stop detached it; a different active owner must never inherit it.
    internal sealed class CivicFundingCycle
    {
        private readonly object _owner, _ai, _prefab, _manager, _buffer;
        private readonly int _session;
        private readonly ushort _id;
        private readonly uint _index;
        internal CivicFundingCycle(object owner, int session, ushort id, uint index, object ai, object prefab, object manager, object buffer)
        { _owner = owner; _session = session; _id = id; _index = index; _ai = ai; _prefab = prefab; _manager = manager; _buffer = buffer; }
        internal bool Allows(object active, int ownerSession, ushort id, uint index, object ai, object prefab, object aiPrefab, object manager, object buffer, bool eligible)
        {
            return eligible && (active == null || ReferenceEquals(active, _owner)) && ownerSession == _session && id == _id && index == _index &&
                ReferenceEquals(ai, _ai) && ReferenceEquals(prefab, _prefab) && ReferenceEquals(aiPrefab, _prefab) && ReferenceEquals(manager, _manager) && ReferenceEquals(buffer, _buffer);
        }
    }

    internal sealed class CivicFundingPreparation : IDisposable
    {
        internal readonly bool Enabled, Ready;
        internal readonly string Root, Status;
        private CivicFundingHookLease _lease;
        internal CivicFundingPreparation(bool enabled, string root, CivicFundingHookLease lease, string status)
        { Enabled = enabled; Root = root; _lease = lease; Ready = lease != null; Status = status; }
        internal CivicFundingHookLease Consume()
        { return Interlocked.Exchange(ref _lease, null); }
        public void Dispose()
        { CivicFundingHookLease lease = Interlocked.Exchange(ref _lease, null); if (lease != null) lease.Dispose(); }
    }

    /// <summary>Opt-in instance funding. All native observation/commands run on the simulation thread.</summary>
    public sealed class CivicFundingBridge
    {
        private static int _nextSession;
        private static CivicFundingBridge _active;
        private readonly object _sync = new object();
        private readonly Dictionary<ushort, CivicFundingSettings> _cache = new Dictionary<ushort, CivicFundingSettings>();
        private readonly Dictionary<ushort, CivicFundingObservation> _observations = new Dictionary<ushort, CivicFundingObservation>();
        private CivicFundingLedger _ledger;
        private byte[] _retained;
        private CivicFundingHookLease _lease;
        private string _failure;
        private volatile bool _enabled;
        private bool _started, _loadRejected, _everActive;
        private int _session, _cursor = 1;
        private long _sequence;
        private volatile string _status = "Civic funding disabled.";
        private volatile bool _ready;
        internal static CivicFundingBridge Active { get { return _active; } }
        internal static bool TryBindActive(CivicFundingBridge owner) { return Interlocked.CompareExchange(ref _active, owner, null) == null; }
        public bool Ready { get { return _ready; } }
        public bool Enabled { get { return _enabled; } }
        public string Status { get { return _status; } }
        internal int Session { get { return _session; } }

        internal static CivicFundingPreparation Prepare(bool enabled, string root)
        {
            if (!enabled) return new CivicFundingPreparation(false, root, null, "Civic funding disabled; retained bytes preserved.");
            try
            {
                if (string.IsNullOrEmpty(root)) throw new InvalidOperationException("Project root unavailable.");
                CivicFundingNativeShape.Require();
                // This API and every patch operation are intentionally outside all
                // runtime/bridge locks. A preparation owns inert hooks until consumed.
                return new CivicFundingPreparation(true, Path.GetFullPath(root), CivicFundingPatches.Acquire(), "SC4LocalV1 native hook preparation ready.");
            }
            catch (Exception error) { return new CivicFundingPreparation(true, root, null, "Civic preparation unavailable: " + error); }
        }
        internal void Start(byte[] retainedPayload, bool enabled, CivicFundingPreparation preparation, string loadFailure = null)
        {
            lock (_sync)
            {
                if (_started) throw new InvalidOperationException("Civic bridge already started.");
                _started = true; _session = Interlocked.Increment(ref _nextSession); _enabled = enabled;
                _retained = retainedPayload == null ? null : (byte[])retainedPayload.Clone(); _failure = loadFailure;
                if (_failure == null && _retained != null)
                    try { using (MemoryStream stream = new MemoryStream(_retained, false)) _ledger = CivicFundingLedgerCodec.Read(stream); }
                    catch (Exception error) { _failure = "Civic funding payload rejected: " + error.Message; }
                _loadRejected = _failure != null;
                if (!enabled) { _status = _failure ?? (_retained == null ? "Civic funding disabled; no retained payload." : "Civic funding disabled; retained bytes preserved."); return; }
                if (_failure != null) { _status = _failure; return; }
                if (preparation == null || !preparation.Enabled || !preparation.Ready || ( _lease = preparation.Consume()) == null)
                { _failure = "Civic funding has no idle, valid outside-lock hook preparation."; _status = _failure; return; }
                if (_ledger == null) _ledger = new CivicFundingLedger(Guid.NewGuid());
                if (!_lease.TryBind(this)) { _failure = "Another civic session/cycle is active or the prepared hooks changed."; _status = _failure; return; }
                _everActive = true; _ready = true; _status = "SC4LocalV1 active; exact elementary/high SchoolAI and authored exact-name clinic/hospital mapping only.";
            }
        }
        public void Stop()
        { lock (_sync) { _ready = false; _started = false; Interlocked.CompareExchange(ref _active, null, this); _cache.Clear(); _status = "Civic session stopped; ledger retained."; } }
        // The runtime calls this only after leaving StateLock. Stop never calls Harmony.
        public void ReleaseHooks()
        { CivicFundingHookLease lease; lock (_sync) { _ready = false; Interlocked.CompareExchange(ref _active, null, this); lease = _lease; _lease = null; } if (lease != null) lease.Dispose(); }
        public byte[] CaptureSave()
        {
            lock (_sync)
            {
                if (!_enabled || _loadRejected || !_everActive)
                { if (_failure != null && _retained == null) throw new InvalidDataException(_failure); return _retained == null ? null : (byte[])_retained.Clone(); }
                using (MemoryStream stream = new MemoryStream()) { CivicFundingLedgerCodec.Write(stream, _ledger); return stream.ToArray(); }
            }
        }
        internal static bool OnSimulationThread()
        { return Singleton<SimulationManager>.exists && ReferenceEquals(Thread.CurrentThread, Singleton<SimulationManager>.instance.m_simulationThread); }
        internal void Suspend(string reason)
        { lock (_sync) { _ready = false; if (_failure == null) _failure = reason; _status = _failure; } }
        public void ReportPersistenceFailure(string reason)
        { Suspend("Civic persistence failed; current valid ledger retained for retry: " + reason); }
        internal bool TrySettings(ushort id, ref Building data, out CivicFundingSettings settings, bool observeInactive = false)
        {
            settings = null;
            if (!observeInactive && (!_ready || !ReferenceEquals(_active, this) || _lease == null || !_lease.Current)) return false;
            // These are the supplied native arguments, not an off-thread manager
            // lookup. A stale UI cache must never make event/deleted objects managed.
            if (id == 0 || (data.m_flags & (Building.Flags.Created | Building.Flags.Deleted)) != Building.Flags.Created || data.m_eventIndex != 0) return false;
            if (!OnSimulationThread())
            {
                lock (_sync) return _cache.TryGetValue(id, out settings) && settings.Identity.NativeBuildIndex == data.m_buildIndex &&
                    ReferenceEquals(data.Info, settings.Prefab) && ReferenceEquals(settings.Ai.m_info, settings.Prefab) && ReferenceEquals(data.Info.m_buildingAI, settings.Ai);
            }
            CivicFacilityIdentity identity; BuildingAI ai; int raw; float radius; string reason;
            if (!TryIdentity(id, ref data, out identity, out ai, out raw, out radius, out reason)) return false;
            CivicFundingRecord record = null; lock (_sync) if (_ledger != null) _ledger.TryGet(identity, out record);
            EconomyManager economy = Singleton<EconomyManager>.instance;
            int day = economy.GetBudget(ai.m_info.m_class, false), night = economy.GetBudget(ai.m_info.m_class, true), current = economy.GetBudget(ai.m_info.m_class);
            if (day < 0 || day > 255 || night < 0 || night > 255 || current < 0 || current > 255)
            { Suspend("Native civic budget is outside the supported 0..255 saved domain."); return false; }
            try
            {
                settings = new CivicFundingSettings(identity, ai, record == null ? null : record.CapacityFundingPercent,
                    record == null ? null : record.CoverageFundingPercent, day, night, current, raw, radius,
                    SimulationManager.RELATIVE_DAY_LENGTH, SimulationManager.RELATIVE_NIGHT_LENGTH);
            }
            catch (Exception error) { Suspend("Native civic budget weighting is unsupported: " + error.Message); return false; }
            lock (_sync)
            {
                CivicFundingSettings old;
                if (_cache.TryGetValue(id, out old) && !old.Identity.Matches(identity)) _observations.Remove(id);
                _cache[id] = settings;
            }
            return true;
        }
        internal static bool TryIdentity(ushort id, ref Building data, out CivicFacilityIdentity identity,
            out BuildingAI ai, out int rawCapacity, out float radius, out string reason)
        {
            identity = null; ai = null; rawCapacity = 0; radius = 0; reason = "Unsupported civic facility.";
            if (id == 0 || (data.m_flags & (Building.Flags.Created | Building.Flags.Deleted)) != Building.Flags.Created || data.m_eventIndex != 0) return false;
            BuildingInfo info = data.Info;
            if (info == null || info.m_class == null || (ai = info.m_buildingAI) == null || !ReferenceEquals(ai.m_info, info)) return false;
            CivicFundingArchetype archetype;
            if (ai.GetType() == typeof(SchoolAI) && info.m_class.m_service == ItemClass.Service.Education)
            {
                if (info.m_class.m_level == ItemClass.Level.Level1) archetype = CivicFundingArchetype.ElementarySchool;
                else if (info.m_class.m_level == ItemClass.Level.Level2) archetype = CivicFundingArchetype.HighSchool;
                else { reason = "University and derived school funding are not mapped."; return false; }
                SchoolAI school = (SchoolAI)ai; rawCapacity = school.StudentCount; radius = school.m_educationRadius;
            }
            else if (ai.GetType() == typeof(HospitalAI) && info.m_class.m_service == ItemClass.Service.HealthCare)
            {
                // Authored finite mapping, not a native level/capacity inference.
                // Names must still be demonstrated by the installed prefab inventory.
                if (info.name == "Medical Clinic") archetype = CivicFundingArchetype.Clinic;
                else if (info.name == "Hospital") archetype = CivicFundingArchetype.Hospital;
                else { reason = "Exact HospitalAI prefab is outside the authored Medical Clinic/Hospital name map."; return false; }
                HospitalAI hospital = (HospitalAI)ai; rawCapacity = hospital.PatientCapacity; radius = hospital.m_healthCareRadius;
            }
            else return false;
            if (rawCapacity < 0 || rawCapacity > 10000000 || float.IsNaN(radius) || float.IsInfinity(radius) || radius < 0)
            { reason = "Civic native capacity/radius is outside the supported arithmetic domain."; return false; }
            if (!ReferenceEquals(data.Info, info) || !ReferenceEquals(info.m_buildingAI, ai) || !ReferenceEquals(ai.m_info, info))
            { reason = "Civic prefab/AI binding changed during observation."; return false; }
            identity = new CivicFacilityIdentity(id, data.m_buildIndex, info.name, ai.GetType().FullName, archetype); reason = "Supported SC4LocalV1 facility."; return true;
        }
        public CivicFundingSnapshot GetSnapshot(ushort id)
        {
            Building data = default(Building); CivicFundingSettings settings = null; string reason = _status;
            if (OnSimulationThread() && Singleton<BuildingManager>.exists && id > 0 && id < Singleton<BuildingManager>.instance.m_buildings.m_buffer.Length)
            { data = Singleton<BuildingManager>.instance.m_buildings.m_buffer[id]; if (!TrySettings(id, ref data, out settings, true)) reason = "Unavailable or unsupported facility; " + _status; }
            else reason = "Civic snapshots require the current simulation thread.";
            lock (_sync)
            {
                CivicFundingObservation observation; _observations.TryGetValue(id, out observation);
                if (settings == null || observation == null || !settings.Identity.Matches(observation.Identity)) observation = null;
                return new CivicFundingSnapshot(this, _session, id, settings, _ready, reason, observation, ref data);
            }
        }
        public bool Apply(CivicFundingSnapshot expected, int? capacity, int? coverage, out string reason)
        {
            reason = "Civic command is stale or unavailable.";
            if (!OnSimulationThread() || expected == null || !ReferenceEquals(expected.Owner, this) || expected.Session != _session || !expected.CanApply || !_ready) return false;
            if ((capacity.HasValue && (capacity < 0 || capacity > 120)) || (coverage.HasValue && (coverage < 0 || coverage > 120)))
            { reason = "Local funding must be 0..120 or inherit."; return false; }
            Building[] buffer = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            if (expected.BuildingId == 0 || expected.BuildingId >= buffer.Length) return false;
            CivicFundingSettings settings;
            if (!TrySettings(expected.BuildingId, ref buffer[expected.BuildingId], out settings) || !expected.Identity.Matches(settings.Identity)) return false;
            lock (_sync)
            {
                if (!_ready || expected.Session != _session || !ReferenceEquals(_active, this)) return false;
                CivicFundingRecord current; _ledger.TryGet(settings.Identity, out current);
                if ((current == null ? null : current.CapacityFundingPercent) != expected.CapacityOverridePercent ||
                    (current == null ? null : current.CoverageFundingPercent) != expected.CoverageOverridePercent)
                { reason = "A newer civic command already changed this facility."; return false; }
                _ledger.Set(settings.Identity, capacity, coverage);
            }
            try { if (TrySettings(expected.BuildingId, ref buffer[expected.BuildingId], out settings)) EnsureHospitalUnits(settings, expected.BuildingId, ref buffer[expected.BuildingId]); }
            catch (Exception error) { Suspend("Civic unit reconciliation failed: " + error.Message); reason = _status; return false; }
            reason = "Civic funding applied."; return true;
        }
        public void Reconcile()
        {
            if (!_ready || !OnSimulationThread() || !Singleton<BuildingManager>.exists) return;
            Building[] buffer = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            int end = Math.Min(buffer.Length, _cursor + 512);
            for (; _cursor < end; ++_cursor)
            {
                if (!_ready) break;
                ushort id = (ushort)_cursor; CivicFundingSettings settings;
                if (TrySettings(id, ref buffer[_cursor], out settings))
                    try { EnsureHospitalUnits(settings, id, ref buffer[_cursor]); }
                    catch (Exception error) { Suspend("Civic unit reconciliation failed: " + error.Message); break; }
                else if ((buffer[_cursor].m_flags & (Building.Flags.Created | Building.Flags.Deleted)) != Building.Flags.Created)
                    Forget(id);
            }
            if (_cursor >= buffer.Length) _cursor = 1;
        }
        public void Observe(ushort id)
        {
            if (!_ready || !OnSimulationThread() || !Singleton<BuildingManager>.exists) return;
            Building[] buffer = Singleton<BuildingManager>.instance.m_buildings.m_buffer; if (id == 0 || id >= buffer.Length) return;
            CivicFundingSettings settings; if (TrySettings(id, ref buffer[id], out settings))
                try { EnsureHospitalUnits(settings, id, ref buffer[id]); }
                catch (Exception error) { Suspend("Civic unit reconciliation failed: " + error.Message); }
            else if ((buffer[id].m_flags & (Building.Flags.Created | Building.Flags.Deleted)) != Building.Flags.Created) Forget(id);
        }
        private void Forget(ushort id)
        { lock (_sync) { if (_ledger != null) _ledger.Remove(id); _cache.Remove(id); _observations.Remove(id); } }
        internal void EnsureHospitalUnits(CivicFundingSettings settings, ushort id, ref Building data)
        {
            if (settings.Ai.GetType() != typeof(HospitalAI) || !OnSimulationThread()) return;
            if ((data.m_flags & (Building.Flags.Abandoned | Building.Flags.Collapsed)) != 0) return;
            int target = CivicFundingMath.Capacity(settings, 100);
            if (CountVisitStorage(id, ref data) >= target) return;
            CivicFundingNativeShape.EnsureVisits(settings.Ai, id, ref data, target);
            if (CountVisitStorage(id, ref data) < target) throw new InvalidDataException("Native visit-unit allocation did not provide the effective patient capacity.");
        }
        private static int CountVisitStorage(ushort id, ref Building data)
        {
            if (!Singleton<CitizenManager>.exists) throw new InvalidDataException("Citizen unit buffer unavailable.");
            CitizenUnit[] units = Singleton<CitizenManager>.instance.m_units.m_buffer;
            HashSet<uint> seen = new HashSet<uint>(); int slots = 0; uint current = data.m_citizenUnits;
            while (current != 0)
            {
                if (current >= units.Length || !seen.Add(current) || seen.Count > units.Length) throw new InvalidDataException("Civic citizen-unit chain is incoherent.");
                CitizenUnit unit = units[current];
                if (unit.m_building != id || (unit.m_flags & CitizenUnit.Flags.Home) != 0) throw new InvalidDataException("Civic citizen-unit ownership/type differs.");
                if ((unit.m_flags & CitizenUnit.Flags.Visit) != 0) slots = checked(slots + 5);
                current = unit.m_nextUnit;
            }
            return slots;
        }
        internal void Record(CivicFundingSettings settings, Action<CivicFundingObservation> update)
        {
            if (!OnSimulationThread()) return;
            lock (_sync)
            {
                CivicFundingObservation observation;
                if (!_observations.TryGetValue(settings.Identity.BuildingId, out observation) || !observation.Identity.Matches(settings.Identity))
                { observation = new CivicFundingObservation { Identity = settings.Identity }; _observations[settings.Identity.BuildingId] = observation; }
                observation.Sequence = _sequence == long.MaxValue ? long.MaxValue : ++_sequence; update(observation);
            }
        }
        internal int? LastAmbulanceTarget(CivicFundingSettings settings)
        { lock (_sync) { CivicFundingObservation observed; return _observations.TryGetValue(settings.Identity.BuildingId, out observed) && observed.Identity.Matches(settings.Identity) ? observed.Ambulances : null; } }
        internal static long Increment(long count) { return count == long.MaxValue ? count : count + 1; }
    }
}
