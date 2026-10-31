using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using ColossalFramework;
using ColossalFramework.Math;
using SC4Evolution.Mod.Economy;
using SC4Evolution.Mod.Runtime;
using SC4Evolution.Simulation;
using UnityEngine;

namespace SC4Evolution.Mod.Diagnostics
{
    public sealed partial class AutonomousTestDriver
    {
        private sealed class NativeResidentialClassProbe
        {
            private readonly AutonomousTestDriver _owner;
            private readonly object _sync = new object();
            private readonly List<string> _checkpoints = new List<string>(), _errors = new List<string>();
            private readonly List<uint> _citizens = new List<uint>();
            private readonly List<uint> _homeUnits = new List<uint>();
            private volatile bool _interrupted, _done;
            private volatile string _failure, _phase = "NotStarted";
            private bool _started, _subscribed, _verified, _cleaned;
            private int _epoch, _acceptedEpoch;
            private EconomicBridge _bridge;
            private QaCityLease _lease;
            private QaConstructionReceipt _low, _high;
            private volatile QaIncomingDemandReceipt _incomingDemand;
            private CityCensus _baseline;
            private BuildingInfo _prefab;
            private readonly HashSet<CitizenInfo> _vanillaCitizens = new HashSet<CitizenInfo>();
            private string _provider = "[]";

            public NativeResidentialClassProbe(AutonomousTestDriver owner) { _owner = owner; }
            public bool CleanupReady { get { return !_started || _done; } }

            public void Start()
            {
                if (_started) throw new InvalidOperationException("Residential class probe already started.");
                _started = true;
                _acceptedEpoch = _epoch;
                try
                {
                    if (!ExecutionAllowed()) throw new InvalidOperationException("Residential class probe is not authorized for this owned mode/session.");
                    _bridge = ModRuntime.CurrentEconomicBridge;
                    if (_bridge == null) throw new InvalidOperationException("Residential economic bridge unavailable.");
                    _provider = ProviderJson();
                    Application.logMessageReceivedThreaded += OnLog;
                    _subscribed = true;
                    _phase = "QueuedSingleSimulationAction";
                    Singleton<SimulationManager>.instance.AddAction(Run);
                }
                catch (Exception error) { _failure = error.ToString(); _done = true; Detach(); throw; }
            }

            public bool Step()
            {
                if (!_done) return false;
                Detach();
                ThrowErrors();
                if (_failure != null) throw new InvalidOperationException("Residential class probe failed: " + _failure);
                if (!_verified || !_cleaned || _interrupted) throw new InvalidOperationException("Residential class probe did not finish verified cleanup.");
                return true;
            }

            public void RequestCleanup()
            {
                Detach();
                if (_done) return;
                _interrupted = true; Interlocked.Increment(ref _epoch);
            }
            public void BeforeLevelUnload()
            { if (!_done) { _interrupted = true; Interlocked.Increment(ref _epoch); } }
            private void Detach()
            {
                if (!_subscribed) return;
                Application.logMessageReceivedThreaded -= OnLog;
                _subscribed = false;
            }
            private bool OwnsNativeCity()
            {
                return _owner._createdCity && Singleton<SimulationManager>.exists &&
                    Singleton<SimulationManager>.instance.m_metaData != null &&
                    Singleton<SimulationManager>.instance.m_metaData.m_gameInstanceIdentifier == _owner._session;
            }
            private bool ExecutionAllowed()
            {
                return !_interrupted && !_cancelled && _acceptedEpoch == _epoch && ReferenceEquals(_active, _owner) &&
                    ReferenceEquals(_owner._classProbe, this) && _owner._request != null &&
                    _owner._request.Mode == "ResidentialClassTestCity" && OwnsNativeCity() &&
                    (_bridge == null || ReferenceEquals(ModRuntime.CurrentEconomicBridge, _bridge));
            }
            // Old-city cleanup survives plugin/bridge teardown; never authorizes new allocations.
            private bool CleanupAllowed() { return !_done && OwnsNativeCity(); }
            private void Guard() { if (!ExecutionAllowed()) throw new OperationCanceledException("Residential QA epoch/session invalidated."); ThrowErrors(); }
            private void OnLog(string message, string stack, LogType type)
            {
                if (_done || (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)) return;
                lock (_sync) if (_errors.Count < 32) _errors.Add(message + "\n" + stack);
            }
            private void ThrowErrors()
            { lock (_sync) if (_errors.Count != 0) throw new InvalidOperationException("Native error during residential QA: " + _errors[0]); }

            private void Run()
            {
                try
                {
                    Guard();
                    _phase = "SelectingVanillaFixture";
                    _lease = _bridge.BeginQaResidentialSession(_owner._session, ExecutionAllowed, CleanupAllowed);
                    _phase = "QueryingNativeIncomingResidentDemand";
                    _incomingDemand = _bridge.ProbeQaIncomingResidentDemand(_lease);
                    if (!_incomingDemand.Verified) throw new InvalidOperationException("Incoming resident demand proof failed: " + _incomingDemand.Error);
                    Guard();
                    _phase = "SelectingVanillaFixture";
                    _prefab = ChoosePrefab();
                    InventoryCitizenPrefabs();
                    Vector3 a, b; FindSites(out a, out b);
                    QaDemandReceipt demand = Demand(1000, 0);
                    CityCensus baseline = _baseline = demand.Snapshot.Census;
                    Checkpoint("Baseline", baseline);
                    _phase = "CreatingLowCandidate"; Guard();
                    Randomizer randomizer = new Randomizer(0x534334u);
                    _low = _bridge.ConstructQaResidentialCandidate(_lease, _prefab, a, 0f, _prefab.m_cellLength, ref randomizer);
                    RequireRecord(_low, ResidentialClass.Low);
                    uint unitLow = EmptyHome(_low.Building);
                    _phase = "CreatingNativeAdults";
                    for (int i = 0; i < 2; i++)
                    {
                        Guard(); uint id = 0;
                        try
                        {
                            if (!Singleton<CitizenManager>.instance.CreateCitizen(out id, 90 + i * 10, 17, ref randomizer) || id == 0)
                                throw new InvalidOperationException("Native adult allocation failed.");
                        }
                        finally { if (id != 0 && !_citizens.Contains(id)) _citizens.Add(id); }
                        ValidateCitizen(id, 0, null);
                        CitizenInfo info = Singleton<CitizenManager>.instance.m_citizens.m_buffer[id].GetCitizenInfo(id);
                        if (info == null || !_vanillaCitizens.Contains(info) || !(info.m_citizenAI is ResidentAI))
                            throw new InvalidOperationException("Native adult resolved to an unsupported/nonvanilla resident prefab.");
                        Guard();
                        Singleton<CitizenManager>.instance.m_citizens.m_buffer[id].SetHome(id, 0, unitLow);
                    }
                    CheckFamily(_low, null, ResidentialClass.Low);
                    demand = Demand(0, 1000); // Captures the actual two Low residents before choosing High.
                    CheckCensus(demand.Snapshot.Census, baseline, ResidentialClass.Low, 2);
                    Checkpoint("LowHome", demand.Snapshot.Census);
                    _phase = "CreatingHighCandidate"; Guard();
                    _high = _bridge.ConstructQaResidentialCandidate(_lease, _prefab, b, 0f, _prefab.m_cellLength, ref randomizer);
                    RequireRecord(_high, ResidentialClass.High); EmptyHome(_high.Building);
                    _phase = "MovingFamilyHigh"; MoveFamily(_high.Building);
                    CheckFamily(_high, _low, ResidentialClass.High);
                    demand = Demand(0, 1000); CheckCensus(demand.Snapshot.Census, baseline, ResidentialClass.High, 2);
                    Checkpoint("HighHome", demand.Snapshot.Census);
                    _phase = "MovingFamilyLow"; MoveFamily(_low.Building);
                    CheckFamily(_low, _high, ResidentialClass.Low);
                    demand = Demand(0, 1000); CheckCensus(demand.Snapshot.Census, baseline, ResidentialClass.Low, 2);
                    Checkpoint("LowHomeReturned", demand.Snapshot.Census);
                    Guard(); _verified = true;
                }
                catch (Exception error) { _failure = error.ToString(); }
                finally
                {
                    _phase = "CleaningOwnedFixture";
                    try { Cleanup(); _cleaned = true; }
                    catch (Exception error) { _failure = (_failure ?? string.Empty) + "\nCleanup failed: " + error; }
                    try { ThrowErrors(); } catch (Exception error) { _failure = (_failure ?? string.Empty) + "\n" + error; }
                    _phase = _failure == null && _verified && !_interrupted ? "Verified" : "FailedOrInterrupted";
                    _done = true; // Release publication comes strictly after all native cleanup.
                }
            }

            private QaDemandReceipt Demand(float low, float high)
            {
                Guard(); QaDemandReceipt receipt = _bridge.SetQaResidentialDemandOffsets(_lease, low, 0, high);
                DemandNodeState[] nodes = receipt.Snapshot.Nodes;
                ResidentialClass expected = low == 1000 ? ResidentialClass.Low : ResidentialClass.High;
                if (ResidentialClassBridge.ChooseClass(nodes) != expected)
                    throw new InvalidOperationException("Propagated scripted fixture did not select the expected class.");
                StringBuilder states = new StringBuilder();
                foreach (DemandNodeState state in nodes) if (state.Key == 0x21010 || state.Key == 0x21020 || state.Key == 0x21030)
                { if (states.Length != 0) states.Append(','); states.Append("{\"key\":").Append(state.Key).Append(",\"active\":").Append(state.ActiveDemand.ToString(CultureInfo.InvariantCulture)).Append(",\"normalized\":").Append(state.NormalizedDemand).Append(",\"cap\":").Append(state.DemandCap.ToString(CultureInfo.InvariantCulture)).Append('}'); }
                Add("{\"phase\":\"Demand\",\"source\":\"ScriptedFixture\",\"before\":" + FloatsJson(receipt.Before) + ",\"desired\":" + FloatsJson(receipt.Desired) + ",\"delta\":" + FloatsJson(receipt.Delta) + ",\"frame\":" + receipt.Snapshot.Census.Frame + ",\"sampleAttempts\":" + receipt.SampleAttempts + ",\"passes\":" + receipt.Snapshot.PropagationPasses + ",\"converged\":" + Bool(receipt.Snapshot.Converged) + ",\"gameplayEnabled\":" + Bool(receipt.Snapshot.GameplayEnabled) + ",\"nodes\":[" + states + "]}");
                return receipt;
            }

            private ResidentialClassLedger Ledger()
            {
                byte[] bytes = _bridge.GetClassSaveState();
                if (bytes == null) throw new InvalidOperationException("Live class ledger was not serializable.");
                using (MemoryStream stream = new MemoryStream(bytes, false)) return ResidentialClassLedgerCodec.Read(stream);
            }
            private void RequireRecord(QaConstructionReceipt item, ResidentialClass expected)
            {
                Guard(); ResidentialLotRecord record; ResidentialClassLedger ledger = Ledger();
                if (item == null || !item.NativeSuccess || item.Building == 0 || item.Identity == null ||
                    !ReferenceEquals(item.Prefab, _prefab) || !ledger.TryGet(item.Identity, out record) ||
                    record.Origin != ClassOrigin.Selected || record.DesignClass != expected || record.OccupantClass != expected)
                    throw new InvalidOperationException("Native candidate did not produce the expected selected class record.");
                Building building = Singleton<BuildingManager>.instance.m_buildings.m_buffer[item.Building];
                if (!ReferenceEquals(building.Info, _prefab) || building.m_level != (byte)ItemClass.Level.Level1)
                    throw new InvalidOperationException("Fixture prefab/native Level1 changed.");
                Add("{\"phase\":\"SelectedRecord\",\"ledgerId\":" + Json(ledger.LedgerId.ToString()) + ",\"policyVersion\":" + ledger.PolicyVersion + ",\"building\":" + item.Building + ",\"buildIndex\":" + item.BuildIndex + ",\"generation\":" + record.Generation + ",\"class\":" + Json(expected.ToString()) + ",\"origin\":\"Selected\",\"xBits\":" + item.Identity.OriginXBits + ",\"zBits\":" + item.Identity.OriginZBits + ",\"prefab\":" + Json(_prefab.name) + ",\"nativeLevel\":1,\"completed\":" + Bool((building.m_flags & Building.Flags.Completed) != 0) + "}");
            }
            private uint EmptyHome(ushort building)
            {
                uint unit = Singleton<BuildingManager>.instance.m_buildings.m_buffer[building].GetEmptyCitizenUnit(CitizenUnit.Flags.Home);
                CitizenUnit[] units = Singleton<CitizenManager>.instance.m_units.m_buffer;
                if (unit == 0 || unit >= units.Length || units[unit].m_building != building ||
                    (units[unit].m_flags & (CitizenUnit.Flags.Created | CitizenUnit.Flags.Home)) != (CitizenUnit.Flags.Created | CitizenUnit.Flags.Home))
                    throw new InvalidOperationException("Native empty HOME unit unavailable.");
                for (int i = 0; i < 5; i++) if (units[unit].GetCitizen(i) != 0) throw new InvalidOperationException("Expected empty HOME unit.");
                if (!_homeUnits.Contains(unit)) _homeUnits.Add(unit);
                return unit;
            }
            private void ValidateCitizen(uint id, ushort home, ResidentialClass? wealth)
            {
                Citizen person = Singleton<CitizenManager>.instance.m_citizens.m_buffer[id];
                const Citizen.Flags forbidden = Citizen.Flags.Dead | Citizen.Flags.Tourist | Citizen.Flags.DummyTraffic | Citizen.Flags.MovingIn | Citizen.Flags.Student;
                int index = _citizens.IndexOf(id);
                if ((person.m_flags & (Citizen.Flags.Created | forbidden)) != Citizen.Flags.Created || person.Age != 90 + index * 10 ||
                    person.m_family != 17 || (int)person.EducationLevel != 0 || person.m_homeBuilding != home ||
                    (wealth.HasValue && (int)person.WealthLevel != (int)wealth.Value - 1))
                    throw new InvalidOperationException("Citizen identity/home/education/wealth invariant failed for " + id);
            }
            private void CheckFamily(QaConstructionReceipt target, QaConstructionReceipt empty, ResidentialClass wealth)
            {
                Guard(); RequireRecord(target, wealth);
                CitizenManager cm = Singleton<CitizenManager>.instance;
                uint unit = Singleton<BuildingManager>.instance.m_buildings.m_buffer[target.Building].FindCitizenUnit(CitizenUnit.Flags.Home, _citizens[0]);
                if (unit == 0 || cm.m_units.m_buffer[unit].m_building != target.Building) throw new InvalidOperationException("Family HOME membership missing.");
                int found = 0;
                for (int i = 0; i < 5; i++) { uint member = cm.m_units.m_buffer[unit].GetCitizen(i); if (member != 0) { if (!_citizens.Contains(member)) throw new InvalidOperationException("Unexpected family member."); found++; } }
                if (found != 2 || cm.m_units.m_buffer[unit].GetCitizen(0) == 0) throw new InvalidOperationException("Two-member family not preserved.");
                foreach (uint id in _citizens)
                {
                    ValidateCitizen(id, target.Building, wealth);
                    if (Singleton<BuildingManager>.instance.m_buildings.m_buffer[target.Building].FindCitizenUnit(CitizenUnit.Flags.Home, id) != unit)
                        throw new InvalidOperationException("Family split across HOME units.");
                    if (empty != null && Singleton<BuildingManager>.instance.m_buildings.m_buffer[empty.Building].FindCitizenUnit(CitizenUnit.Flags.Home, id) != 0)
                        throw new InvalidOperationException("Citizen remained in old HOME unit.");
                }
                if (!_homeUnits.Contains(unit)) _homeUnits.Add(unit);
            }
            private void MoveFamily(ushort destination)
            {
                Guard(); EmptyHome(destination);
                uint id = _citizens[0]; CitizenManager cm = Singleton<CitizenManager>.instance;
                CitizenInfo info = cm.m_citizens.m_buffer[id].GetCitizenInfo(id);
                if (info == null || !_vanillaCitizens.Contains(info) || !(info.m_citizenAI is ResidentAI)) throw new InvalidOperationException("Native resident dispatch prefab unavailable.");
                Guard(); info.m_citizenAI.StartTransfer(id, ref cm.m_citizens.m_buffer[id], TransferManager.TransferReason.Family0,
                    new TransferManager.TransferOffer { Building = destination });
            }

            private void CheckCensus(CityCensus census, CityCensus before, ResidentialClass wealth, int count)
            {
                int[] actual = census.ResidentsByLotClass, baseline = before.ResidentsByLotClass;
                for (int i = 0; i < 3; i++) if (actual[i] != baseline[i] + (i == (int)wealth - 1 ? count : 0))
                    throw new InvalidOperationException("Real lot-class census differs from the fixture contribution.");
                int[] workers = census.WorkforceByLotClassEducation, previous = before.WorkforceByLotClassEducation;
                for (int i = 0; i < 12; i++) if (workers[i] != previous[i] + (i == ((int)wealth - 1) * 4 ? count : 0))
                    throw new InvalidOperationException("Real workforce/education census differs from the fixture contribution.");
            }
            private void Checkpoint(string name, CityCensus census)
            {
                StringBuilder people = new StringBuilder(); CitizenManager cm = Singleton<CitizenManager>.instance;
                foreach (uint id in _citizens)
                { Citizen p = cm.m_citizens.m_buffer[id]; if (people.Length != 0) people.Append(','); people.Append("{\"id\":").Append(id).Append(",\"age\":").Append(p.Age).Append(",\"family\":").Append(p.m_family).Append(",\"education\":").Append((int)p.EducationLevel).Append(",\"wealth\":").Append((int)p.WealthLevel).Append(",\"home\":").Append(p.m_homeBuilding).Append('}'); }
                Add("{\"phase\":" + Json(name) + ",\"frame\":" + census.Frame + ",\"date\":" + Json(census.SimulationDate.ToString("o")) + ",\"homes\":" + census.HomeHouseholds + ",\"occupiedHomes\":" + census.OccupiedHomeHouseholds + ",\"lotResidents\":" + IntsJson(census.ResidentsByLotClass) + ",\"workforceByClassEducation\":" + IntsJson(census.WorkforceByLotClassEducation) + ",\"homeUnits\":" + HomesJson() + ",\"people\":[" + people + "]}");
            }
            private string HomesJson()
            {
                StringBuilder result = new StringBuilder(); CitizenUnit[] units = Singleton<CitizenManager>.instance.m_units.m_buffer;
                foreach (uint id in _homeUnits)
                {
                    if (result.Length != 0) result.Append(',');
                    result.Append("{\"id\":").Append(id).Append(",\"building\":").Append(units[id].m_building).Append(",\"flags\":").Append((int)units[id].m_flags).Append(",\"members\":[");
                    for (int i = 0; i < 5; i++) { if (i != 0) result.Append(','); result.Append(units[id].GetCitizen(i)); }
                    result.Append("]}");
                }
                return "[" + result + "]";
            }
            private static string IntsJson(int[] values)
            { return "[" + string.Join(",", Array.ConvertAll<int, string>(values, delegate(int value) { return value.ToString(CultureInfo.InvariantCulture); })) + "]"; }
            private static string FloatsJson(float[] values)
            { return "[" + string.Join(",", Array.ConvertAll<float, string>(values, delegate(float value) { return value.ToString(CultureInfo.InvariantCulture); })) + "]"; }

            private void Cleanup()
            {
                if (_lease == null) return;
                if (!CleanupAllowed()) throw new InvalidOperationException("Cleanup refused: native city ownership changed.");
                Exception failure = null;
                try { _bridge.ClearQaResidentialDemandOffsets(_lease); }
                catch (Exception error) { failure = error; }
                // Release the occupied building first, exercising native unlinking rather than hiding it.
                List<QaConstructionReceipt> items = new List<QaConstructionReceipt>(_lease.Constructions);
                items.Sort(delegate(QaConstructionReceipt x, QaConstructionReceipt y)
                { return IsOccupied(y.Building).CompareTo(IsOccupied(x.Building)); });
                foreach (QaConstructionReceipt item in items)
                    try { ReleaseBuilding(item); } catch (Exception error) { failure = error; }
                foreach (uint id in _citizens)
                    try
                    {
                        if (!CleanupAllowed()) throw new InvalidOperationException("Cleanup city changed.");
                        CitizenManager cm = Singleton<CitizenManager>.instance; Citizen p = cm.m_citizens.m_buffer[id];
                        if ((p.m_flags & Citizen.Flags.Created) == 0) continue;
                        if (p.Age != 90 + _citizens.IndexOf(id) * 10 || p.m_family != 17 || p.m_homeBuilding != 0)
                            throw new InvalidOperationException("Cleanup citizen identity/home no longer belongs to fixture.");
                        cm.ReleaseCitizen(id);
                        if ((cm.m_citizens.m_buffer[id].m_flags & Citizen.Flags.Created) != 0) throw new InvalidOperationException("Citizen release did not clear native slot.");
                    }
                    catch (Exception error) { failure = error; }
                if (failure != null) throw new InvalidOperationException("Owned fixture cleanup was incomplete.", failure);
                QaDemandReceipt final = _bridge.ClearQaResidentialDemandOffsets(_lease);
                if (_baseline != null) CheckCensus(final.Snapshot.Census, _baseline, ResidentialClass.Low, 0);
                Checkpoint("AfterNativeRelease", final.Snapshot.Census);
                Add("{\"phase\":\"NativeReleaseVerified\",\"citizensReleased\":" + _citizens.Count + ",\"buildingsReleased\":" + items.Count + "}");
            }
            private bool IsOccupied(ushort id)
            { foreach (uint citizen in _citizens) if (Singleton<CitizenManager>.instance.m_citizens.m_buffer[citizen].m_homeBuilding == id && id != 0) return true; return false; }
            private void ReleaseBuilding(QaConstructionReceipt item)
            {
                if (item.Building == 0) return;
                if (!CleanupAllowed()) throw new InvalidOperationException("Cleanup city changed.");
                BuildingManager bm = Singleton<BuildingManager>.instance; Building b = bm.m_buildings.m_buffer[item.Building];
                if ((b.m_flags & Building.Flags.Created) == 0) return;
                if (b.m_buildIndex != item.BuildIndex || !ReferenceEquals(b.Info, item.Prefab) ||
                    b.m_position.x != item.Position.x || b.m_position.z != item.Position.z)
                    throw new InvalidOperationException("Cleanup refused a reused/changed building identity.");
                bm.ReleaseBuilding(item.Building);
                foreach (uint id in _citizens) if (Singleton<CitizenManager>.instance.m_citizens.m_buffer[id].m_homeBuilding == item.Building)
                    throw new InvalidOperationException("Native building release left a citizen HOME link.");
                if ((bm.m_buildings.m_buffer[item.Building].m_flags & Building.Flags.Created) != 0)
                    throw new InvalidOperationException("Native building release left Created slot.");
                foreach (uint unit in _homeUnits) if (Singleton<CitizenManager>.instance.m_units.m_buffer[unit].m_building == item.Building &&
                    (Singleton<CitizenManager>.instance.m_units.m_buffer[unit].m_flags & CitizenUnit.Flags.Created) != 0)
                    throw new InvalidOperationException("Native release left HOME unit allocated.");
                byte[] bytes = _bridge.GetClassSaveState();
                if (bytes != null) using (MemoryStream stream = new MemoryStream(bytes, false))
                    foreach (ResidentialLotRecord record in ResidentialClassLedgerCodec.Read(stream).GetRecords())
                        if (record.Identity.BuildingId == item.Building) throw new InvalidOperationException("Release left a class ledger record.");
            }

            private BuildingInfo ChoosePrefab()
            {
                List<BuildingInfo> candidates = new List<BuildingInfo>();
                for (uint i = 0; i < PrefabCollection<BuildingInfo>.LoadedCount(); i++)
                {
                    BuildingInfo p = PrefabCollection<BuildingInfo>.GetLoaded(i);
                    if (p == null || !Vanilla(p) || p.m_class == null || !(p.m_buildingAI is ResidentialBuildingAI) ||
                        p.m_class.m_service != ItemClass.Service.Residential || p.m_class.m_subService != ItemClass.SubService.ResidentialLow ||
                        p.m_class.m_level != ItemClass.Level.Level1 || p.m_placementMode != BuildingInfo.PlacementMode.Roadside ||
                        p.m_zoningMode != BuildingInfo.ZoningMode.Straight || (p.m_availableIn & ItemClass.Availability.Game) == 0 ||
                        p.m_cellWidth < 1 || p.m_cellWidth > 4 || p.m_cellLength < 1 || p.m_cellLength > 4) continue;
                    if (((ResidentialBuildingAI)p.m_buildingAI).CalculateHomeCount(ItemClass.Level.Level1, new Randomizer(17), p.m_cellWidth, p.m_cellLength) > 0) candidates.Add(p);
                }
                candidates.Sort(delegate(BuildingInfo a, BuildingInfo b) { return StringComparer.Ordinal.Compare(a.name, b.name); });
                if (candidates.Count == 0) throw new InvalidOperationException("UnsupportedFixture: no loaded vanilla ResidentialLow Level1 prefab.");
                return candidates[0];
            }
            private static bool Vanilla(PrefabInfo p)
            { return !p.m_isCustomContent && p.m_requiredExpansion == default(SteamHelper.ExpansionBitMask) && p.m_requiredModderPack == default(SteamHelper.ModderPackBitMask); }
            private void InventoryCitizenPrefabs()
            {
                for (uint i = 0; i < PrefabCollection<CitizenInfo>.LoadedCount(); i++)
                { CitizenInfo p = PrefabCollection<CitizenInfo>.GetLoaded(i); if (p != null && Vanilla(p) && p.m_citizenAI is ResidentAI && (p.m_availableIn & ItemClass.Availability.Game) != 0) _vanillaCitizens.Add(p); }
                if (_vanillaCitizens.Count == 0) throw new InvalidOperationException("UnsupportedFixture: no loaded vanilla ResidentAI citizen prefabs.");
            }
            private void FindSites(out Vector3 a, out Vector3 b)
            {
                int x, z; Singleton<GameAreaManager>.instance.GetStartTile(out x, out z);
                Vector3 center = Singleton<GameAreaManager>.instance.GetAreaPositionSmooth(x, z);
                float centerX = center.x, centerZ = center.z;
                for (int iz = -6; iz <= 6; iz++) for (int ix = -6; ix <= 6; ix++)
                { Guard(); a = new Vector3(centerX + ix * 96f, 0, centerZ + iz * 96f); b = a + new Vector3(64f, 0, 0); if (ValidSite(ref a) && ValidSite(ref b)) return; }
                throw new InvalidOperationException("UnsupportedFixture: bounded start-area scan found no dry nonoverlapping pair.");
            }
            private bool ValidSite(ref Vector3 p)
            {
                if (Singleton<GameAreaManager>.instance.PointOutOfArea(p, 32f)) return false;
                TerrainManager terrain = Singleton<TerrainManager>.instance; float sx, sz;
                p.y = terrain.SampleDetailHeight(p, out sx, out sz);
                float min = p.y, max = p.y;
                for (int z = -1; z <= 1; z++) for (int x = -1; x <= 1; x++)
                { Vector3 corner = p + new Vector3(x * 24f, 0, z * 24f); float h = terrain.SampleDetailHeight(corner, out sx, out sz); if (float.IsNaN(h) || float.IsInfinity(h) || h <= terrain.WaterLevel(new Vector2(corner.x, corner.z)) + 1f) return false; min = Mathf.Min(min, h); max = Mathf.Max(max, h); }
                if (max - min > 2f) return false; // Fixture geometry tolerance, not SC4 suitability.
                Quad2 quad = new Quad2 { a = new Vector2(p.x - 24f, p.z - 24f), b = new Vector2(p.x + 24f, p.z - 24f),
                    c = new Vector2(p.x + 24f, p.z + 24f), d = new Vector2(p.x - 24f, p.z + 24f) };
                if (Singleton<NetManager>.instance.OverlapQuad(quad, p.y - 8f, p.y + 32f, ItemClass.CollisionType.Terrain,
                    ItemClass.Layer.Default, 0, 0, 0)) return false;
                Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
                for (int i = 1; i < buildings.Length; i++) if ((buildings[i].m_flags & Building.Flags.Created) != 0)
                { Vector3 d = buildings[i].m_position - p; float radius = 40f + 4f * (buildings[i].Width + buildings[i].Length); if (d.x * d.x + d.z * d.z < radius * radius) return false; }
                return true;
            }
            private static string ProviderJson()
            {
                List<string> entries = new List<string>();
                foreach (System.Reflection.Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                    if (a.GetName().Name.StartsWith("CitiesHarmony", StringComparison.Ordinal))
                    {
                        string location = null, sourcePath = null, hash = null, error = null;
                        string status = "UnavailableLocationEmpty";
                        try
                        {
                            // PluginManager may use Assembly.Load(byte[]); Location is
                            // then empty. Never guess a plugin folder or hash loaded bytes.
                            location = a.Location;
                            if (!string.IsNullOrEmpty(location))
                            {
                                if (!Path.IsPathRooted(location)) status = "UnavailableNonAbsoluteLocation";
                                else if (!string.Equals(Path.GetFileName(location), a.GetName().Name + ".dll", StringComparison.OrdinalIgnoreCase))
                                    status = "UnavailableSourceNameMismatch";
                                else
                                {
                                    sourcePath = Path.GetFullPath(location);
                                    if (!File.Exists(sourcePath)) status = "UnavailableSourceFileMissing";
                                    else
                                    {
                                        using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                                        using (FileStream stream = File.OpenRead(sourcePath))
                                            hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
                                        status = "MeasuredSourceFileViaAssemblyLocation";
                                    }
                                }
                            }
                        }
                        catch (Exception exception)
                        { status = "UnavailableSourceFileRead"; error = exception.GetType().FullName + ": " + exception.Message; }
                        entries.Add("{\"assembly\":" + Json(a.FullName) + ",\"location\":" + Json(location) +
                            ",\"sourceFilePath\":" + Json(sourcePath) + ",\"sourceFileSha256\":" + Json(hash) +
                            ",\"sourceFileHashStatus\":" + Json(status) + ",\"sourceFileHashError\":" + Json(error) +
                            ",\"loadedBytesHashAvailable\":false}");
                    }
                return "[" + string.Join(",", entries.ToArray()) + "]";
            }
            private void Add(string checkpoint) { lock (_sync) _checkpoints.Add(checkpoint); }
            private string IncomingDemandJson()
            {
                QaIncomingDemandReceipt r = _incomingDemand;
                if (r == null) return "{\"verified\":false,\"queryAttempted\":false,\"status\":\"NotExercised\"}";
                StringBuilder nodes = new StringBuilder();
                foreach (DemandNodeState node in r.ResidentialNodes)
                {
                    if (nodes.Length != 0) nodes.Append(',');
                    nodes.Append("{\"key\":").Append(node.Key).Append(",\"activeRaw\":")
                        .Append(node.ActiveDemand.ToString(CultureInfo.InvariantCulture)).Append(",\"normalized\":").Append(node.NormalizedDemand).Append('}');
                }
                return "{\"coverage\":\"NativeIncomingResidentDemandQuery\",\"verified\":" + Bool(r.Verified) +
                    ",\"probeThreadId\":" + r.ProbeThreadId +
                    ",\"queryAttempted\":" + Bool(r.QueryAttempted) + ",\"getterReturned\":" + Bool(r.GetterReturned) +
                    ",\"getterResult\":" + (r.GetterReturned ? r.GetterResult.ToString(CultureInfo.InvariantCulture) : "null") +
                    ",\"error\":" + Json(r.Error) + ",\"before\":" + IncomingBindingJson(r.Before) + ",\"after\":" + IncomingBindingJson(r.After) +
                    ",\"frameBefore\":" + r.FrameBefore + ",\"frameAfter\":" + r.FrameAfter +
                    ",\"rngSeedBefore\":" + Json(r.RngSeedBefore.ToString(CultureInfo.InvariantCulture)) +
                    ",\"rngSeedAfter\":" + Json(r.RngSeedAfter.ToString(CultureInfo.InvariantCulture)) + ",\"rngStructEquals\":" + Bool(r.RngUnchanged) +
                    ",\"internalCashBefore\":" + r.CashBefore.ToString(CultureInfo.InvariantCulture) + ",\"internalCashAfter\":" + r.CashAfter.ToString(CultureInfo.InvariantCulture) +
                    ",\"lastCashBefore\":" + r.LastCashBefore.ToString(CultureInfo.InvariantCulture) + ",\"lastCashAfter\":" + r.LastCashAfter.ToString(CultureInfo.InvariantCulture) +
                    ",\"serializedDateTicksBefore\":" + r.DateTicksBefore.ToString(CultureInfo.InvariantCulture) + ",\"serializedDateTicksAfter\":" + r.DateTicksAfter.ToString(CultureInfo.InvariantCulture) +
                    ",\"publication\":{\"fixtureOffsetsApplied\":false,\"censusFrame\":" + r.CensusFrame + ",\"converged\":" + Bool(r.PublicationConverged) +
                    ",\"gameplayEnabled\":" + Bool(r.PublicationGameplayEnabled) + ",\"passes\":" + r.PublicationPasses + ",\"residentialNodes\":[" + nodes + "]}}";
            }
            private string IncomingBindingJson(IncomingDemandSnapshot s)
            {
                if (s == null) return "null";
                return "{\"ownerMatchesBridge\":" + Bool(ReferenceEquals(s.Owner, _bridge)) + ",\"session\":" + s.Session + ",\"bindingValid\":" + Bool(s.BindingValid) +
                    ",\"currentDepth\":" + s.CurrentDepth + ",\"constructionEntries\":" + s.ConstructionEntries + ",\"incomingEntries\":" + s.IncomingEntries +
                    ",\"aggregatedCallbacks\":" + s.AggregatedCallbacks + ",\"preservedCallbacks\":" + s.PreservedCallbacks + ",\"finalizers\":" + s.Finalizers +
                    ",\"lastCallbackThreadId\":" + s.LastCallbackThreadId + ",\"lastFinalizerThreadId\":" + s.LastFinalizerThreadId +
                    ",\"mismatches\":" + s.Mismatches + ",\"lastKind\":" + Json(s.LastKind.ToString()) + ",\"lastInput\":" + s.LastInput +
                    ",\"lastReturn\":" + s.LastReturn + ",\"lastAggregated\":" + Bool(s.LastAggregated) + "}";
            }
            public string ReportJson()
            {
                lock (_sync) return "{\"coverage\":\"WrapperCandidateConstruction\",\"demandInput\":\"ScriptedFixture\",\"localPolicy\":\"NativeSafetyOnly\",\"organicGrowth\":false,\"zoneBlockExecuted\":false,\"persistence\":\"NotExercised\",\"phase\":" + Json(_phase) + ",\"done\":" + Bool(_done) + ",\"verified\":" + Bool(_done && _verified && _failure == null && !_interrupted && _errors.Count == 0) + ",\"cleanupVerified\":" + Bool(_done && _cleaned) + ",\"interrupted\":" + Bool(_interrupted) + ",\"error\":" + Json(_failure) + ",\"providerAssemblies\":" + _provider + ",\"incomingResidentDemandProbe\":" + IncomingDemandJson() + ",\"checkpoints\":[" + string.Join(",", _checkpoints.ToArray()) + "],\"nativeErrors\":[" + JsonList(new List<string>(_errors)) + "]}";
            }
        }
    }
}
