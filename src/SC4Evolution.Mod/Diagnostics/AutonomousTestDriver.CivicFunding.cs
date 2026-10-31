using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ColossalFramework;
using ColossalFramework.Math;
using ColossalFramework.UI;
using SC4Evolution.Mod.Civic;
using SC4Evolution.Mod.Runtime;
using SC4Evolution.Simulation;
using UnityEngine;

namespace SC4Evolution.Mod.Diagnostics
{
    public sealed partial class AutonomousTestDriver
    {
        // Disposable controlled native service calls, never an organic population test.
        // The bridge and production hooks are exercised, not replaced by these delegates.
        private sealed class NativeCivicFundingProbe
        {
            private delegate void ActiveStep(PlayerBuildingAI ai, ushort id, ref Building data, ref Building.Frame frame);
            private delegate void SchoolProduction(SchoolAI ai, ushort id, ref Building data, ref Building.Frame frame,
                int production, int finalProduction, ref Citizen.BehaviourData behaviour, int aliveWorkers,
                int totalWorkers, int workPlaces, int aliveVisitors, int totalVisitors, int visitPlaces);
            private delegate void HospitalProduction(HospitalAI ai, ushort id, ref Building data, ref Building.Frame frame,
                int production, int finalProduction, ref Citizen.BehaviourData behaviour, int aliveWorkers,
                int totalWorkers, int workPlaces, int aliveVisitors, int totalVisitors, int visitPlaces);
            private sealed class Site
            {
                public BuildingInfo Info;
                public Vector3 Position;
                public float Angle;
                public int Cost;
                public bool MilestoneUnlocked;
            }
            private sealed class OwnedFacility
            {
                public ushort Id;
                public uint BuildIndex;
                public BuildingInfo Info;
                public Vector3 Position;
                public float Angle;
            }
            private sealed class Budget
            {
                public BuildingInfo Info;
                public ItemClass Class;
                public ItemClass.Service Service;
                public ItemClass.SubService SubService;
                public int Day, Night;
            }
            private sealed class OfferAudit
            {
                public TransferManager.TransferReason Reason;
                public ushort[] Counts;
                public string[] Existing;
            }
            private readonly AutonomousTestDriver _owner;
            private readonly object _gate = new object(), _errorGate = new object();
            private readonly List<string> _receipts = new List<string>(), _errors = new List<string>();
            private readonly List<Site> _sites = new List<Site>();
            // Vanilla civic prefabs are roadside buildings; the four-fixture manifest needs
            // one short paid native access road so native placement stays honest. Native
            // creation may split it into several connected segments over uneven terrain.
            private NetInfo _road;
            private Vector3 _roadA, _roadB;
            private readonly List<ushort> _roadSegments = new List<ushort>();
            private readonly List<ushort> _roadNodes = new List<ushort>();
            private int _roadCost;
            private long _roadDebit;
            private readonly List<OwnedFacility> _owned = new List<OwnedFacility>();
            private readonly List<Budget> _budgets = new List<Budget>();
            private SimulationManager _simulation;
            private SimulationMetaData _metadata;
            private BuildingManager _buildingManager;
            private CitizenManager _citizenManager;
            private NetManager _netManager;
            private EconomyManager _economy;
            private Building[] _buildingBuffer;
            private CitizenUnit[] _unitBuffer;
            private Citizen[] _citizenBuffer;
            private NetNode[] _nodeBuffer;
            private NetSegment[] _segmentBuffer;
            private CivicFundingBridge _bridge;
            private ActiveStep _activeStep;
            private SchoolProduction _schoolProduction;
            private HospitalProduction _hospitalProduction;
            private FieldInfo _incomingCounts, _incomingOffers;
            private volatile bool _pending, _interrupted;
            private bool _leaseHeld, _oldForcedPause, _cleanupQueued, _cleanupFinished, _cleaned, _verified, _subscribed;
            private bool _cashShortfallObserved, _staleCommandRejected, _releasedCommandRejected, _canonicalCodec;
            private bool _nativeOffersVerified;
            private bool _activeDisplayRangeObserved;
            private bool _pauseRestorationUnproved;
            private bool _quitUnderOwnedPause;
            private int _state, _case, _revision, _reportedRevision, _session;
            private int _initialClinicVisitUnits;
            private long _constructionDebit, _maintenanceDebit;
            private string _phase = "NotStarted", _failure, _cleanupError, _sourceEvidence;
            private NativeCivicDiagnosticContext _diagnosticContext;
            private string _diagnosticReport;
            private int _optionalToolbarCount;
            private readonly List<string> _optionalToolbarDiagnostics = new List<string>();
            private const long MaximumConstructionDebit = 5000000;
            private static readonly int[] Endpoints = { 0, 50, 100, 120 };

            public NativeCivicFundingProbe(AutonomousTestDriver owner) { _owner = owner; }
            public bool CleanupReady { get { lock (_gate) return _cleanupFinished && !_pending && !_leaseHeld; } }
            public bool CanQuitCurrentScene { get { lock (_gate) return OwnsScene() && (ModRuntime.CurrentCivicFundingBridge == null || ReferenceEquals(ModRuntime.CurrentCivicFundingBridge, _bridge)); } }
            public bool MayQuitUnderOwnedPause
            {
                get { lock (_gate) return _cleanupFinished && !_pending && !_cleanupQueued && !_cleaned && _cleanupError != null
                    && _leaseHeld && CanQuitCurrentScene && _simulation.ForcedSimulationPaused; }
            }
            public void NoteControlledQuit()
            {
                lock (_gate)
                {
                    Require(CanQuitCurrentScene, "Civic controlled quit refused: native scene/bridge changed.");
                    if (_leaseHeld) { Require(MayQuitUnderOwnedPause, "Civic quit under pause is only allowed after terminal cleanup failure."); _quitUnderOwnedPause = true; }
                    try { _owner.WriteReport(); } catch { }
                }
            }

            public void Start()
            {
                if (_owner._request.Mode != "CivicFundingTestCity" || !_owner._createdCity
                    || _owner._selectedAsset != "NorthEurope1FINAL.Green Plains_Data"
                    || _owner._selectedChecksum != "d09cc7008c7f95c0e55b1ebed0a80579")
                    throw new InvalidOperationException("Civic QA requires the explicit disposable Green Plains session.");
                _simulation = Singleton<SimulationManager>.instance;
                _metadata = _simulation.m_metaData;
                _buildingManager = Singleton<BuildingManager>.instance; _citizenManager = Singleton<CitizenManager>.instance;
                _netManager = Singleton<NetManager>.instance; _economy = Singleton<EconomyManager>.instance;
                _buildingBuffer = _buildingManager.m_buildings.m_buffer; _unitBuffer = _citizenManager.m_units.m_buffer;
                _citizenBuffer = _citizenManager.m_citizens.m_buffer; _nodeBuffer = _netManager.m_nodes.m_buffer; _segmentBuffer = _netManager.m_segments.m_buffer;
                _bridge = ModRuntime.CurrentCivicFundingBridge;
                bool noWorkshop = false;
                foreach (string argument in Environment.GetCommandLineArgs())
                    if (string.Equals(argument, "-noWorkshop", StringComparison.OrdinalIgnoreCase)) noWorkshop = true;
                if (!noWorkshop) throw new InvalidOperationException("Civic QA requires a new -noWorkshop process.");
                if (_bridge == null || !_bridge.Ready)
                    throw new InvalidOperationException("CivicFunding=SC4LocalV1 must be explicitly ready: " + (_bridge == null ? "no bridge" : _bridge.Status));
                _sourceEvidence = "{\"build\":" + Json(BuildConfig.applicationVersion) + ",\"game\":" + AssemblyEvidence(typeof(Building).Assembly)
                    + ",\"mod\":" + AssemblyEvidence(typeof(NativeCivicFundingProbe).Assembly) + "}";
                // Same main-thread capture rule as the organic probe: the diagnostic
                // snapshot is taken before the threaded log callback is armed.
                NativeDiagnosticSnapshot diagnostics = NativeDiagnosticSnapshot.CaptureOnMainThread(_owner);
                _diagnosticContext = new NativeCivicDiagnosticContext("CivicFundingTestCity",
                    _owner.OwnsCurrentCity() ? _owner._session : null, diagnostics);
                _diagnosticReport = diagnostics.ReportJson();
                // Subscribe after MAIN-only identity/evidence reads. Threaded log callback is inert.
                Application.logMessageReceivedThreaded += OnLog;
                _subscribed = true;
                Phase("WaitingForNativeModalAndExternalPauseToClear");
            }

            public bool Step()
            {
                lock (_gate)
                {
                    if (_revision != _reportedRevision)
                    {
                        _reportedRevision = _revision;
                        _owner._phase = "CivicFunding:" + _phase;
                        _owner.WriteReport();
                    }
                    lock (_errorGate) if (_errors.Count != 0 && _failure == null) _failure = "Native error: " + _errors[0];
                    if (_failure != null) throw new InvalidOperationException("Civic QA failed: " + _failure);
                    if (_cleanupFinished) return _verified && _cleaned;
                    if (_pending || _interrupted) return false;
                    if (UIView.HasModalInput()) { Phase("WaitingForNativeModalWithoutDismissal"); return false; }
                    if (!_leaseHeld && _simulation.ForcedSimulationPaused) { Phase("WaitingForExternalForcedPause"); return false; }
                    if (_state == 0) Queue(InventoryAndPlan);
                    else if (_state == 1) Queue(BuildFixtures);
                    else if (_state == 2) Queue(EndpointCase);
                    else if (_state == 3) Queue(InheritanceCase);
                    else if (_state == 4) Queue(UnitsMaintenanceAndCodec);
                    else if (_state == 5)
                    {
                        // Keep the four native facilities alive while the owned
                        // save/reload helper audits CivicFunding.v1 on disk.
                        if (_owner._roundtrip == null)
                        {
                            _owner._roundtrip = new NativeSaveRoundtrip(_owner);
                            _owner._roundtrip.StartBorrowedPause();
                            Phase("PopulatedCivicSaveReloadStarted");
                            return false;
                        }
                        if (!_owner._roundtrip.CleanupReady && _owner._roundtrip.Step())
                        {
                            Phase("PopulatedCivicSaveReloadValidatedAwaitingRelease");
                            _state = 6;
                        }
                    }
                    else if (_state == 6)
                    {
                        _owner._roundtrip.RequestCleanup();
                        if (_owner._roundtrip.CleanupReady)
                        {
                            Require(_owner._roundtrip.CivicDiskSaveReloadCoverage,
                                "Native civic save/reload did not prove the populated ledger bytes on disk.");
                            Add("{\"phase\":\"PopulatedCivicSaveReloadValidated\",\"diskSaveReloadVerified\":true}");
                            _state = 7;
                            Phase("NativeCivicAssertionsCompleteAwaitingCleanup");
                        }
                    }
                    else if (_state == 7) RequestCleanup();
                    return false;
                }
            }

            private bool OwnsCity()
            {
                return ReferenceEquals(_active, _owner) && OwnsScene();
            }
            private bool OwnsScene()
            {
                // Cleanup retains this immutable scene lease even if OnDestroy has
                // cleared _active. A session string alone never adopts replacement buffers.
                return _owner._createdCity && _metadata != null && Singleton<SimulationManager>.exists
                    && ReferenceEquals(Singleton<SimulationManager>.instance, _simulation) && _simulation.m_metaData != null
                    && ReferenceEquals(_simulation.m_metaData, _metadata) && _metadata.m_gameInstanceIdentifier == _owner._session
                    && Singleton<BuildingManager>.exists && ReferenceEquals(Singleton<BuildingManager>.instance, _buildingManager)
                    && Singleton<CitizenManager>.exists && ReferenceEquals(Singleton<CitizenManager>.instance, _citizenManager)
                    && Singleton<NetManager>.exists && ReferenceEquals(Singleton<NetManager>.instance, _netManager)
                    && Singleton<EconomyManager>.exists && ReferenceEquals(Singleton<EconomyManager>.instance, _economy)
                    && ReferenceEquals(_buildingManager.m_buildings.m_buffer, _buildingBuffer) && ReferenceEquals(_citizenManager.m_units.m_buffer, _unitBuffer)
                    && ReferenceEquals(_citizenManager.m_citizens.m_buffer, _citizenBuffer) && ReferenceEquals(_netManager.m_nodes.m_buffer, _nodeBuffer)
                    && ReferenceEquals(_netManager.m_segments.m_buffer, _segmentBuffer);
            }
            private void Guard()
            {
                if (_cancelled || _interrupted || !OwnsCity() || !ReferenceEquals(_owner._civicProbe, this)
                    || !ReferenceEquals(ModRuntime.CurrentCivicFundingBridge, _bridge) || !_bridge.Ready)
                    throw new OperationCanceledException("Civic fixture owner/session/bridge changed.");
                lock (_errorGate) if (_errors.Count != 0) throw new InvalidOperationException("Native error: " + _errors[0]);
            }

            // Native LoadLevel recreates manager buffers and the live civic bridge.
            // Rebind the immutable fixture manifest to that new owner before any
            // cleanup command is accepted.
            public void RebindAfterNativeReload()
            {
                lock (_gate)
                {
                    Require(_leaseHeld, "Civic save/reload lost the fixture pause lease.");
                    _simulation = Singleton<SimulationManager>.instance;
                    _metadata = _simulation == null ? null : _simulation.m_metaData;
                    _buildingManager = Singleton<BuildingManager>.instance;
                    _citizenManager = Singleton<CitizenManager>.instance;
                    _netManager = Singleton<NetManager>.instance;
                    _economy = Singleton<EconomyManager>.instance;
                    Require(_simulation != null && _metadata != null && _buildingManager != null && _citizenManager != null
                        && _netManager != null && _economy != null, "Civic save/reload did not restore native managers.");
                    _buildingBuffer = _buildingManager.m_buildings.m_buffer;
                    _unitBuffer = _citizenManager.m_units.m_buffer;
                    _citizenBuffer = _citizenManager.m_citizens.m_buffer;
                    _nodeBuffer = _netManager.m_nodes.m_buffer;
                    _segmentBuffer = _netManager.m_segments.m_buffer;
                    _bridge = ModRuntime.CurrentCivicFundingBridge;
                    Require(_bridge != null && _bridge.Ready, "Civic bridge did not reload from the saved ledger.");
                    // A native LoadGame creates a fresh bridge session while
                    // retaining the ledger bytes. Rebind the probe's owner
                    // token before cleanup snapshots are requested.
                    _session = _bridge.Session;
                    foreach (OwnedFacility facility in _owned)
                        Require(Matches(facility), "Civic facility fingerprint changed across native save/reload.");
                    foreach (ushort segment in _roadSegments)
                        Require(segment < _segmentBuffer.Length && (_segmentBuffer[segment].m_flags & NetSegment.Flags.Created) != 0,
                            "Paid civic access road segment did not survive native save/reload.");
                    Phase("ReboundNativeCivicFixtureAfterSaveReload");
                }
            }
            private void Queue(Action action)
            {
                if (_pending || _interrupted || _cleanupFinished) return;
                _pending = true;
                try
                {
                    _simulation.AddAction(delegate
                    {
                        lock (_gate)
                        {
                            try { Guard(); action(); }
                            catch (Exception error) { _failure = error.ToString(); Phase("Failed"); }
                            finally { _pending = false; }
                        }
                    });
                }
                catch { _pending = false; throw; }
            }
            private void OnLog(string message, string stack, LogType type)
            {
                if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
                NativeCivicDiagnosticContext context = _diagnosticContext;
                NativeDiagnosticSnapshot snapshot = context == null ? null : context.Snapshot;
                string normalized = message == null ? null : message.TrimEnd('\r', '\n');
                string reason = type == LogType.Error && snapshot != null
                    ? snapshot.ClassifyCivicOptionalToolbar(normalized, context) : null;
                // The save/reload helper and this civic probe both observe the
                // same native log stream. During the owned LoadGame handoff,
                // classify the pinned-build baseline diagnostics through the
                // reload context so they do not become duplicate fatal errors.
                if (reason == null && type == LogType.Error && snapshot != null
                    && _owner._roundtrip != null && _owner._roundtrip.CivicOwnedReloadInProgress)
                    reason = snapshot.Classify(normalized,
                        new NativeDiagnosticContext("WaitingForNativeOwnedReload", _owner._session, true));
                lock (_errorGate)
                {
                    if (!ReferenceEquals(context, _diagnosticContext)) reason = null;
                    if (reason != null)
                    {
                        if (_optionalToolbarCount < int.MaxValue) ++_optionalToolbarCount;
                        if (_optionalToolbarDiagnostics.Count < 24)
                            _optionalToolbarDiagnostics.Add("{\"logType\":\"Error\",\"message\":" + Json(Bounded(message, 2048))
                                + ",\"reason\":" + Json(reason) + ",\"exactCallsiteConfirmed\":false}");
                        return;
                    }
                    if (_errors.Count < 16) _errors.Add(Bounded(message, 2048) + "\n" + Bounded(stack, 2048));
                }
            }
            private void Phase(string phase) { if (_phase != phase) { _phase = phase; ++_revision; } }
            private void Add(string receipt) { _receipts.Add(receipt); ++_revision; }
            private static void Require(bool value, string reason) { if (!value) throw new InvalidDataException(reason); }

            private void InventoryAndPlan()
            {
                // Actions run before the native forced-pause gate. Do not replace an external pause.
                Require(!_simulation.ForcedSimulationPaused, "External forced pause appeared before acquisition.");
                _oldForcedPause = _simulation.ForcedSimulationPaused;
                _simulation.ForcedSimulationPaused = true; _leaseHeld = true;
                BuildingInfo school = null, clinic = null;
                List<string> inventory = new List<string>();
                for (uint index = 0; index < PrefabCollection<BuildingInfo>.LoadedCount(); ++index)
                {
                    BuildingInfo info = PrefabCollection<BuildingInfo>.GetLoaded(index);
                    if (info == null || info.m_buildingAI == null || info.m_class == null) continue;
                    Type type = info.m_buildingAI.GetType();
                    if (type != typeof(SchoolAI) && type != typeof(HospitalAI)) continue;
                    if (inventory.Count < 128) inventory.Add(PrefabJson(info));
                    if (!Vanilla(info)) continue;
                    if (info.name == "Elementary School" && type == typeof(SchoolAI) && info.m_class.m_service == ItemClass.Service.Education
                        && info.m_class.m_level == ItemClass.Level.Level1)
                    { Require(school == null, "Ambiguous exact Elementary School prefab."); school = info; }
                    if (info.name == "Medical Clinic" && type == typeof(HospitalAI) && info.m_class.m_service == ItemClass.Service.HealthCare)
                    { Require(clinic == null, "Ambiguous exact Medical Clinic prefab."); clinic = info; }
                }
                Add("{\"phase\":\"LoadedNativePrefabInventory\",\"entries\":[" + string.Join(",", inventory.ToArray()) + "]}");
                Require(school != null && clinic != null, "UnsupportedFixture: exact vanilla Elementary School/Medical Clinic prefabs missing; inspect inventory.");
                foreach (BuildingInfo info in new[] { school, clinic })
                    Require((info.m_placementMode == BuildingInfo.PlacementMode.OnGround || info.m_placementMode == BuildingInfo.PlacementMode.Roadside)
                        && !info.m_buildingAI.WorksAsNet()
                        && (info.m_subBuildings == null || info.m_subBuildings.Length == 0)
                        && (info.m_paths == null || info.m_paths.Length == 0),
                        "UnsupportedFixture: civic prefab requires sub-buildings or native paths outside the four-building manifest.");
                _road = PrefabCollection<NetInfo>.FindLoaded("Basic Road");
                Require(_road != null && VanillaNet(_road) && _road.m_netAI is RoadAI && !_road.m_netAI.RequireDoubleSegments(),
                    "UnsupportedFixture: exact vanilla Basic Road prefab missing for native roadside placement.");
                BindNativeDelegates();
                RememberBudget(school); RememberBudget(clinic);
                int tileX, tileZ; Singleton<GameAreaManager>.instance.GetStartTile(out tileX, out tileZ);
                Vector3 center = Singleton<GameAreaManager>.instance.GetAreaPositionSmooth(tileX, tileZ);
                // One straight paid access road along +X through the start-tile center,
                // at native terrain height (control points carry real world Y).
                TerrainManager terrainManager = Singleton<TerrainManager>.instance;
                _roadA = new Vector3(center.x - 96f, 0, center.z); _roadB = new Vector3(center.x + 96f, 0, center.z);
                _roadA.y = terrainManager.SampleRawHeightSmooth(_roadA);
                _roadB.y = terrainManager.SampleRawHeightSmooth(_roadB);
                Require(DryInArea(_roadA, 24f) && DryInArea(_roadB, 24f) && DryInArea(((_roadA + _roadB) * 0.5f), 24f),
                    "UnsupportedFixture: start-tile access road endpoints are wet or outside the owned area.");
                string roadRejected;
                _roadCost = RoadPreflight(_road, _roadA, _roadB, out roadRejected);
                Require(_roadCost > 0 && _roadCost <= MaximumConstructionDebit, "UnsupportedFixture: native access road preflight rejected: " + roadRejected);
                BuildingInfo[] desired = { school, school, clinic, clinic };
                List<string> plans = new List<string>();
                foreach (BuildingInfo info in desired)
                {
                    // Roadside slots along the planned segment: schools on +Z, clinics on -Z,
                    // each at its own frontage offset; twins far apart along the road.
                    float along = (plans.Count % 2 == 0) ? -64f : 64f;
                    float side = info == school ? 1f : -1f;
                    float offset = _road.m_halfWidth + info.m_cellWidth * 2f + 12f;
                    Vector3 point = new Vector3(center.x + along, 0, center.z + side * offset);
                    bool separate = true;
                    foreach (Site other in _sites)
                    {
                        float dx = Math.Abs(point.x - other.Position.x), dz = Math.Abs(point.z - other.Position.z);
                        float width = (info.m_cellWidth + other.Info.m_cellWidth) * 4f + 16f;
                        float length = (info.m_cellLength + other.Info.m_cellLength) * 4f + 16f;
                        if (dx < width && dz < length) { separate = false; break; }
                    }
                    Require(separate && DryInArea(point, Math.Max(info.m_cellWidth, info.m_cellLength) * 4f + 16f),
                        "UnsupportedFixture: roadside slot overlaps or is wet/outside the owned area.");
                    // Roadside cost/snap are only knowable after the road exists; final native
                    // preflight happens in BuildFixtures immediately before the paid creation.
                    _sites.Add(new Site { Info = info, Position = point, Angle = 0f, Cost = 0, MilestoneUnlocked = ToolsModifierControl.IsUnlocked(info.m_UnlockMilestone) });
                    plans.Add("{\"prefab\":" + Json(info.name) + ",\"position\":" + Position(point) + ",\"costDeferredUntilRoadsidePreflight\":true}");
                }
                Require(_roadCost > 0 && _roadCost <= MaximumConstructionDebit && Singleton<BuildingManager>.instance.CheckLimits()
                    && Singleton<EconomyManager>.instance.PeekResource(EconomyManager.Resource.Construction, _roadCost) == _roadCost,
                    "Native cash/limits or bounded construction cost rejected fixture before spending.");
                Add("{\"phase\":\"NativeFixturePreflight\",\"startTileX\":" + tileX + ",\"startTileZ\":" + tileZ
                    + ",\"plannedAccessRoadCost\":" + _roadCost + ",\"cashBefore\":" + Singleton<EconomyManager>.instance.InternalCashAmount
                    + ",\"plans\":[" + string.Join(",", plans.ToArray()) + "],\"constructionIntent\":\"Controlled QA native construction (one paid access road plus four roadside facilities); milestone state recorded, never unlocked\"}");
                _state = 1; Phase("NativeFixturePlanAcceptedBeforeSpend");
            }

            private static bool Vanilla(BuildingInfo info)
            {
                return !info.m_isCustomContent && info.m_requiredExpansion == default(SteamHelper.ExpansionBitMask)
                    && info.m_requiredModderPack == default(SteamHelper.ModderPackBitMask);
            }
            private static bool VanillaNet(PrefabInfo info)
            {
                return info != null && !info.m_isCustomContent && info.m_requiredExpansion == default(SteamHelper.ExpansionBitMask)
                    && info.m_requiredModderPack == default(SteamHelper.ModderPackBitMask);
            }
            private static bool DryInArea(Vector3 point, float margin)
            {
                TerrainManager terrain = Singleton<TerrainManager>.instance;
                float height = terrain.SampleRawHeightSmooth(point);
                float water = terrain.WaterLevel(new Vector2(point.x, point.z));
                return height > water + 1f && !Singleton<GameAreaManager>.instance.PointOutOfArea(point, margin);
            }
            /// <summary>Straight-segment native road call in test mode; mirrors the organic
            /// driver's bounded CreateNetCall without elevation handling (fresh endpoints).</summary>
            private int RoadPreflight(NetInfo road, Vector3 a, Vector3 b, out string rejection)
            {
                Guard();
                Vector3 direction = b - a; direction.y = 0f; direction.Normalize();
                NetTool.ControlPoint start = new NetTool.ControlPoint { m_node = 0, m_position = a, m_direction = direction, m_elevation = 0f };
                NetTool.ControlPoint end = new NetTool.ControlPoint { m_node = 0, m_position = b, m_direction = direction, m_elevation = 0f };
                NetTool.ControlPoint middle = new NetTool.ControlPoint { m_position = (a + b) * 0.5f, m_direction = direction, m_elevation = 0f };
                FastList<NetTool.NodePosition> positions = new FastList<NetTool.NodePosition>();
                ushort firstNode, lastNode, segment; int cost, production;
                ToolBase.ToolErrors errors = NetTool.CreateNode(road, start, middle, end, positions, 8,
                    true, true, false, true, true, false, false, 0, out firstNode, out lastNode, out segment, out cost, out production);
                rejection = errors.ToString();
                return errors == ToolBase.ToolErrors.None && cost > 0 ? cost : -1;
            }
            /// <summary>Paid real creation of the planned access road. Real-mode out ids/cost
            /// are not trustworthy (the organic driver proved this pattern first): success is
            /// errors==None plus a buffer diff that finds exactly one new owned road segment
            /// and its two endpoint nodes, which are recorded for cleanup.</summary>
            private void BuildAccessRoad()
            {
                Guard();
                string rejection;
                int retested = RoadPreflight(_road, _roadA, _roadB, out rejection);
                Require(retested == _roadCost, "Access road native preflight changed before spending: " + rejection);
                Require(_constructionDebit + _roadCost <= MaximumConstructionDebit && Singleton<NetManager>.instance.CheckLimits(),
                    "Access road construction bound/limits changed.");
                EconomyManager economy = Singleton<EconomyManager>.instance;
                Require(economy.PeekResource(EconomyManager.Resource.Construction, _roadCost) == _roadCost, "Insufficient real native construction cash for the access road.");
                HashSet<ushort> nodesBefore = new HashSet<ushort>(), segmentsBefore = new HashSet<ushort>();
                CollectCreated(_nodeBuffer, NetNode.Flags.Created, nodesBefore);
                CollectCreated(_segmentBuffer, NetSegment.Flags.Created, segmentsBefore);
                Vector3 direction = _roadB - _roadA; direction.y = 0f; direction.Normalize();
                NetTool.ControlPoint start = new NetTool.ControlPoint { m_node = 0, m_position = _roadA, m_direction = direction, m_elevation = 0f };
                NetTool.ControlPoint end = new NetTool.ControlPoint { m_node = 0, m_position = _roadB, m_direction = direction, m_elevation = 0f };
                NetTool.ControlPoint middle = new NetTool.ControlPoint { m_position = (_roadA + _roadB) * 0.5f, m_direction = direction, m_elevation = 0f };
                FastList<NetTool.NodePosition> positions = new FastList<NetTool.NodePosition>();
                ushort firstNode, lastNode, segment; int cost, production;
                long cashBefore = economy.InternalCashAmount;
                ToolBase.ToolErrors errors = NetTool.CreateNode(_road, start, middle, end, positions, 8,
                    false, true, false, true, true, false, false, 0, out firstNode, out lastNode, out segment, out cost, out production);
                long cashAfter = economy.InternalCashAmount;
                _roadDebit = cashBefore - cashAfter;
                HashSet<ushort> nodesAfter = new HashSet<ushort>(), segmentsAfter = new HashSet<ushort>();
                CollectCreated(_nodeBuffer, NetNode.Flags.Created, nodesAfter);
                CollectCreated(_segmentBuffer, NetSegment.Flags.Created, segmentsAfter);
                List<ushort> newSegments = new List<ushort>();
                foreach (ushort id in segmentsAfter) if (!segmentsBefore.Contains(id)) newSegments.Add(id);
                List<ushort> newNodes = new List<ushort>();
                foreach (ushort id in nodesAfter) if (!nodesBefore.Contains(id)) newNodes.Add(id);
                // Diagnostics first: the receipt must survive a failed creation attempt.
                List<string> segmentInfos = new List<string>();
                foreach (ushort id in newSegments)
                    segmentInfos.Add("{\"id\":" + id + ",\"info\":" + Json(_segmentBuffer[id].Info == null ? "null" : _segmentBuffer[id].Info.name) + "}");
                Add("{\"phase\":\"NativeAccessRoadAttempt\",\"errors\":" + Json(errors.ToString()) + ",\"outFirstNode\":" + firstNode + ",\"outLastNode\":" + lastNode
                    + ",\"outSegment\":" + segment + ",\"outCost\":" + cost + ",\"estimate\":" + _roadCost
                    + ",\"cashBefore\":" + cashBefore + ",\"cashAfter\":" + cashAfter + ",\"debit\":" + _roadDebit
                    + ",\"newSegments\":[" + string.Join(",", segmentInfos.ToArray()) + "],\"newNodes\":" + newNodes.Count
                    + ",\"from\":" + Position(_roadA) + ",\"to\":" + Position(_roadB) + "}");
                Require(errors == ToolBase.ToolErrors.None, "Native access road creation failed: " + errors);
                Require(_roadDebit > 0 && _constructionDebit + _roadDebit <= MaximumConstructionDebit,
                    "Access road debit is missing or exceeds the bound: " + _roadDebit);
                // A straight call over uneven terrain legitimately yields a short chain of
                // connected Basic Road segments with interior nodes. Every created id is
                // owned test infrastructure and must be released by cleanup.
                Require(newSegments.Count >= 1 && newSegments.Count <= 8 && newNodes.Count == newSegments.Count + 1,
                    "Access road created an unexpected native geometry: " + newSegments.Count + " segments, " + newNodes.Count + " nodes.");
                foreach (ushort id in newSegments)
                {
                    Require(_segmentBuffer[id].Info == _road, "Access road created a segment with a foreign prefab.");
                    _roadSegments.Add(id);
                }
                _roadNodes.AddRange(newNodes.ToArray());
                Require((_nodeBuffer[newNodes[0]].m_flags & NetNode.Flags.Created) != 0 && (_nodeBuffer[newNodes[newNodes.Count - 1]].m_flags & NetNode.Flags.Created) != 0,
                    "Access road endpoint nodes are not created.");
                _constructionDebit += _roadDebit;
                Add("{\"phase\":\"NativeAccessRoadCreated\",\"segments\":" + _roadSegments.Count + ",\"nodes\":" + _roadNodes.Count
                    + ",\"estimate\":" + _roadCost + ",\"outCost\":" + cost + ",\"cashBefore\":" + cashBefore + ",\"cashAfter\":" + cashAfter
                    + ",\"debit\":" + _roadDebit + ",\"scope\":\"Paid native Basic Road chain; required because vanilla civic prefabs are roadside buildings\"}");
            }

            private static void CollectCreated(NetNode[] buffer, NetNode.Flags flag, HashSet<ushort> into)
            {
                for (ushort id = 1; id < buffer.Length; ++id)
                    if ((buffer[id].m_flags & flag) != 0) into.Add(id);
            }

            private static void CollectCreated(NetSegment[] buffer, NetSegment.Flags flag, HashSet<ushort> into)
            {
                for (ushort id = 1; id < buffer.Length; ++id)
                    if ((buffer[id].m_flags & flag) != 0) into.Add(id);
            }
            private static string PrefabJson(BuildingInfo info)
            {
                SchoolAI school = info.m_buildingAI as SchoolAI; HospitalAI hospital = info.m_buildingAI as HospitalAI;
                return "{\"name\":" + Json(info.name) + ",\"ai\":" + Json(info.m_buildingAI.GetType().FullName)
                    + ",\"service\":" + Json(info.m_class.m_service.ToString()) + ",\"subservice\":" + Json(info.m_class.m_subService.ToString())
                    + ",\"level\":" + Json(info.m_class.m_level.ToString()) + ",\"studentCount\":" + (school == null ? "null" : school.StudentCount.ToString(CultureInfo.InvariantCulture))
                    + ",\"patientCapacity\":" + (hospital == null ? "null" : hospital.PatientCapacity.ToString(CultureInfo.InvariantCulture))
                    + ",\"rawStudentCount\":" + (school == null ? "null" : school.m_studentCount.ToString(CultureInfo.InvariantCulture))
                    + ",\"rawPatientCapacity\":" + (hospital == null ? "null" : hospital.m_patientCapacity.ToString(CultureInfo.InvariantCulture))
                    + ",\"educationAccumulation\":" + (school == null ? "null" : school.m_educationAccumulation.ToString(CultureInfo.InvariantCulture))
                    + ",\"educationRadius\":" + (school == null ? "null" : Number(school.m_educationRadius))
                    + ",\"healthCareAccumulation\":" + (hospital == null ? "null" : hospital.m_healthCareAccumulation.ToString(CultureInfo.InvariantCulture))
                    + ",\"healthCareRadius\":" + (hospital == null ? "null" : Number(hospital.m_healthCareRadius))
                    + ",\"ambulanceCount\":" + (hospital == null ? "null" : hospital.m_ambulanceCount.ToString(CultureInfo.InvariantCulture))
                    + ",\"curingRate\":" + (hospital == null ? "null" : hospital.m_curingRate.ToString(CultureInfo.InvariantCulture))
                    + ",\"maintenanceCost\":" + ((PlayerBuildingAI)info.m_buildingAI).m_maintenanceCost
                    + ",\"width\":" + info.m_cellWidth + ",\"length\":" + info.m_cellLength + ",\"placement\":" + Json(info.m_placementMode.ToString())
                    + ",\"paths\":" + (info.m_paths == null ? 0 : info.m_paths.Length) + ",\"vanilla\":" + Bool(Vanilla(info))
                    + ",\"milestoneUnlocked\":" + Bool(ToolsModifierControl.IsUnlocked(info.m_UnlockMilestone)) + "}";
            }
            private static string CreationMilestones(PlayerBuildingAI ai)
            { return "[" + CreationMilestone(ai.m_createPassMilestone) + "," + CreationMilestone(ai.m_createPassMilestone2) + "," + CreationMilestone(ai.m_createPassMilestone3) + "]"; }
            private static string CreationMilestone(ManualMilestone milestone)
            { return ReferenceEquals(milestone, null) ? "null" : "{\"name\":" + Json(milestone.name) + ",\"unlocked\":" + Bool(ToolsModifierControl.IsUnlocked(milestone)) + "}"; }
            private Site Preflight(BuildingInfo info, Vector3 raw, out string reason)
            {
                TerrainManager terrain = Singleton<TerrainManager>.instance;
                Vector3 point = raw; point.y = terrain.SampleRawHeightSmooth(point);
                float water = terrain.WaterLevel(new Vector2(point.x, point.z));
                if (point.y <= water + 1f || Singleton<GameAreaManager>.instance.PointOutOfArea(point, Math.Max(info.m_cellWidth, info.m_cellLength) * 4f + 16f))
                { reason = "WetOrOutsideOwnedTile"; return null; }
                float angle = 0, min, max, height;
                Building.SampleBuildingHeight(point, angle, info.m_cellWidth, info.m_cellLength, info, out min, out max, out height);
                point.y = height; Segment3 connection = default(Segment3); int rate, cost;
                ToolBase.ToolErrors ai = info.m_buildingAI.CheckBuildPosition(0, ref point, ref angle, water, 0, ref connection, out rate, out cost);
                ToolBase.ToolErrors space = BuildingTool.CheckSpace(info, info.m_placementMode, 0, point, min,
                    height + info.m_collisionHeight, angle, info.m_cellWidth, info.m_cellLength, true,
                    new ulong[(Singleton<NetManager>.instance.m_segments.m_buffer.Length + 63) / 64],
                    new ulong[(Singleton<BuildingManager>.instance.m_buildings.m_buffer.Length + 63) / 64]);
                reason = "AI=" + ai + ";Space=" + space + ";Cost=" + cost + ";Rate=" + rate + ";Angle=" + angle.ToString("0.###", CultureInfo.InvariantCulture)
                    + ";Slope=" + (max - min).ToString("0.##", CultureInfo.InvariantCulture) + "/" + info.m_maxHeightOffset.ToString("0.##", CultureInfo.InvariantCulture)
                    + ";Water=" + (point.y - water).ToString("0.##", CultureInfo.InvariantCulture);
                bool angleAccepted = info.m_placementMode == BuildingInfo.PlacementMode.Roadside ? (!float.IsNaN(angle) && !float.IsInfinity(angle)) : Math.Abs(angle) <= 0.001f;
                // The production rate stays 0 until utilities exist; the probe drives
                // production explicitly, so rate is recorded but never a placement veto
                // (the organic driver's service preflight ignores it the same way).
                if (ai != ToolBase.ToolErrors.None || space != ToolBase.ToolErrors.None || cost < 0
                    || max - min > info.m_maxHeightOffset || !angleAccepted || point.y <= water
                    || Singleton<GameAreaManager>.instance.PointOutOfArea(point, Math.Max(info.m_cellWidth, info.m_cellLength) * 4f + 16f)) return null;
                return new Site { Info = info, Position = point, Angle = angle, Cost = cost, MilestoneUnlocked = ToolsModifierControl.IsUnlocked(info.m_UnlockMilestone) };
            }
            private void RememberBudget(BuildingInfo info)
            {
                ItemClass itemClass = info.m_class;
                foreach (Budget budget in _budgets) if (budget.Class.m_service == itemClass.m_service && budget.Class.m_subService == itemClass.m_subService) return;
                EconomyManager economy = Singleton<EconomyManager>.instance;
                _budgets.Add(new Budget { Info = info, Class = itemClass, Service = itemClass.m_service, SubService = itemClass.m_subService,
                    Day = economy.GetBudget(itemClass, false), Night = economy.GetBudget(itemClass, true) });
            }
            private void SetBudget(ItemClass itemClass, int day, int night)
            {
                EconomyManager economy = Singleton<EconomyManager>.instance;
                economy.SetBudget(itemClass.m_service, itemClass.m_subService, day, false);
                economy.SetBudget(itemClass.m_service, itemClass.m_subService, night, true);
            }
            private void BuildFixtures()
            {
                // Vanilla civic prefabs are roadside buildings: the paid access road must
                // exist before any facility preflight can find native frontage.
                BuildAccessRoad();
                foreach (Site planned in _sites)
                {
                    Guard();
                    // Roadside slot search: the planned slot first, then bounded alternatives
                    // along the road (uneven terrain can reject a fixed slot natively via
                    // slope/water/space). Native CheckSpace arbitrates collisions.
                    List<string> attempts = new List<string>();
                    Site actual = null; string lastReason = null;
                    float side = planned.Position.z >= _roadA.z ? 1f : -1f;
                    float baseOffset = Math.Abs(planned.Position.z - _roadA.z);
                    List<Vector3> candidates = new List<Vector3> { planned.Position };
                    float[] alongAxis = { -80f, -48f, -32f, 32f, 48f, 80f, 0f };
                    foreach (float along in alongAxis)
                        candidates.Add(new Vector3((_roadA.x + _roadB.x) * 0.5f + along, 0, _roadA.z + side * (baseOffset + 12f)));
                    foreach (float along in alongAxis)
                        candidates.Add(new Vector3((_roadA.x + _roadB.x) * 0.5f + along, 0, _roadA.z + side * (baseOffset + 24f)));
                    foreach (Vector3 candidate in candidates)
                    {
                        string reason; Site found = Preflight(planned.Info, candidate, out reason);
                        lastReason = reason;
                        if (found == null) { if (attempts.Count < 12) attempts.Add("{\"position\":" + Position(candidate) + ",\"reason\":" + Json(reason) + "}"); continue; }
                        bool separate = true;
                        foreach (OwnedFacility built in _owned)
                        {
                            float dx = Math.Abs(found.Position.x - built.Position.x), dz = Math.Abs(found.Position.z - built.Position.z);
                            float width = (planned.Info.m_cellWidth + built.Info.m_cellWidth) * 4f + 16f;
                            float length = (planned.Info.m_cellLength + built.Info.m_cellLength) * 4f + 16f;
                            if (dx < width && dz < length) { separate = false; break; }
                        }
                        if (!separate) { if (attempts.Count < 12) attempts.Add("{\"position\":" + Position(candidate) + ",\"reason\":\"OverlapsBuiltFacility\"}"); continue; }
                        actual = found; break;
                    }
                    if (actual == null)
                    {
                        Add("{\"phase\":\"RejectedRoadsideSlots\",\"examples\":[" + string.Join(",", attempts.ToArray()) + "]}");
                        Require(false, "Native civic roadside slot preflight failed: " + lastReason);
                    }
                    Require(_constructionDebit + actual.Cost <= MaximumConstructionDebit && Singleton<BuildingManager>.instance.CheckLimits(), "Civic construction bound/limits changed.");
                    EconomyManager economy = Singleton<EconomyManager>.instance;
                    Require(economy.PeekResource(EconomyManager.Resource.Construction, actual.Cost) == actual.Cost, "Insufficient real native construction cash.");
                    string creationMilestones = CreationMilestones((PlayerBuildingAI)actual.Info.m_buildingAI);
                    long cashBefore = economy.InternalCashAmount;
                    int paid = economy.FetchResource(EconomyManager.Resource.Construction, actual.Cost, actual.Info.m_class);
                    long cashAfter = economy.InternalCashAmount;
                    long debit = cashBefore - cashAfter;
                    _constructionDebit += debit; Require(paid == actual.Cost && debit == actual.Cost, "Native construction availability/debit differs from checked cost.");
                    ushort id = 0; bool success = false; uint index = _simulation.m_currentBuildIndex;
                    try { success = Singleton<BuildingManager>.instance.CreateBuilding(out id, ref _simulation.m_randomizer, actual.Info, actual.Position, actual.Angle, 0, index); }
                    finally
                    {
                        // Capture even a native partial allocation, before propagating an exception.
                        if (id != 0)
                        {
                            Building data = Singleton<BuildingManager>.instance.m_buildings.m_buffer[id];
                            if ((data.m_flags & Building.Flags.Created) != 0 && data.m_buildIndex == index && ReferenceEquals(data.Info, actual.Info))
                                _owned.Add(new OwnedFacility { Id = id, Info = actual.Info, BuildIndex = index, Position = data.m_position, Angle = data.m_angle });
                        }
                    }
                    Require(success && id != 0 && _owned.Count > 0 && _owned[_owned.Count - 1].Id == id, "Native civic CreateBuilding failed.");
                    ++_simulation.m_currentBuildIndex; actual.Info.m_buildingAI.PlacementSucceeded();
                    Require(ToolsModifierControl.IsUnlocked(actual.Info.m_UnlockMilestone) == planned.MilestoneUnlocked,
                        "Native fixture creation changed the selected facility unlock state.");
                    CivicFundingSnapshot snapshot = Snapshot(_owned[_owned.Count - 1]);
                    if (_session == 0) _session = snapshot.Session;
                    Require(snapshot.Session == _session && snapshot.CanApply && snapshot.RawCapacity > 0, "Civic native fixture binding unavailable.");
                    Add("{\"phase\":\"NativeFixtureCreated\",\"snapshot\":" + SnapshotJson(snapshot) + ",\"position\":" + Position(actual.Position)
                        + ",\"constructionAvailability\":" + paid + ",\"cashBefore\":" + cashBefore + ",\"cashAfter\":" + cashAfter + ",\"actualConstructionDebit\":" + debit + "}");
                    Add("{\"phase\":\"NativeCreatePassMilestoneSideEffects\",\"buildingId\":" + id + ",\"before\":" + creationMilestones
                        + ",\"after\":" + CreationMilestones((PlayerBuildingAI)actual.Info.m_buildingAI)
                        + ",\"scope\":\"Native CreateBuilding may unlock its authored create-pass milestones in the disposable city; no claim of globally unchanged unlock state\"}");
                    if (_owned.Count == 3) _initialClinicVisitUnits = VisitUnits(_owned[2]).Count;
                }
                Require(_owned.Count == 4 && ReferenceEquals(_owned[0].Info, _owned[1].Info) && ReferenceEquals(_owned[2].Info, _owned[3].Info), "Twin fixture identity failed.");
                _state = 2; _case = 0; Phase("FourPaidNativeFacilitiesCreated");
            }

            private void EndpointCase()
            {
                int type = _case / 8, axis = (_case / 4) % 2, value = Endpoints[_case % 4];
                OwnedFacility selected = _owned[type == 0 ? 0 : 2], twin = _owned[type == 0 ? 1 : 3];
                SetBudget(selected.Info.m_class, 100, 100);
                string prefabBefore = PrefabJson(selected.Info);
                CivicFundingSnapshot twinBefore = Snapshot(twin), before = Snapshot(selected);
                int? e = axis == 0 ? value : 100, c = axis == 1 ? value : 100;
                Apply(before, e, c);
                CivicFundingSnapshot after = Snapshot(selected);
                Require(after.ResolvedCapacityPercent == e.Value && after.ResolvedCoveragePercent == c.Value, "Endpoint override did not bind exact axes.");
                CivicFundingEvaluation expected = CivicFundingEvaluator.Evaluate(after.Identity.Archetype, e.Value, c.Value, 0);
                Require(Math.Abs(after.EfficiencyFactor - expected.EfficiencyFactor) < 1e-9 && Math.Abs(after.CoverageFactor - expected.CoverageFactor) < 1e-9, "Endpoint curve mismatch.");
                ObserveProduction(selected, "ControlledEndpoint", after);
                CivicFundingSnapshot twinAfter = Snapshot(twin);
                Require(SameFunding(twinBefore, twinAfter) && PrefabJson(selected.Info) == prefabBefore, "Local override mutated twin or shared prefab.");
                string staleReason = "Original pair unchanged; stale negative case not applicable.";
                // A no-op endpoint can leave the original pair current; only changed pairs are stale.
                if (before.CapacityOverridePercent != e || before.CoverageOverridePercent != c)
                { Require(!_bridge.Apply(before, 99, 99, out staleReason), "Stale local command was accepted after another command."); _staleCommandRejected = true; }
                Add("{\"phase\":\"IndependentEndpointAndTwin\",\"axis\":" + Json(axis == 0 ? "Capacity" : "Coverage") + ",\"percent\":" + value
                    + ",\"selected\":" + SnapshotJson(Snapshot(selected)) + ",\"twin\":" + SnapshotJson(twinAfter) + ",\"prefabAndTwinUnchanged\":true,\"staleReason\":" + Json(staleReason) + "}");
                ++_case; if (_case == 16) { _case = 0; _state = 3; Phase("EndpointCasesComplete"); }
            }

            private void InheritanceCase()
            {
                OwnedFacility selected = _owned[_case < 3 ? 0 : 2]; int variant = _case % 3;
                SetBudget(selected.Info.m_class, 50, 120);
                Apply(Snapshot(selected), variant == 1 ? (int?)100 : null, variant == 2 ? (int?)100 : null);
                CivicFundingSnapshot snapshot = Snapshot(selected);
                int current = _simulation.m_isNightTime ? 120 : 50, average = (5 * 50 + 3 * 120) / 8;
                Require(snapshot.GlobalDayPercent == 50 && snapshot.GlobalNightPercent == 120 && snapshot.GlobalCurrentPercent == current
                    && snapshot.GlobalAveragePercent == average && snapshot.ResolvedCapacityPercent == (variant == 1 ? 100 : current)
                    && snapshot.ResolvedCoveragePercent == (variant == 2 ? 100 : current)
                    && snapshot.AverageCapacityPercent == (variant == 1 ? 100 : average)
                    && snapshot.AverageCoveragePercent == (variant == 2 ? 100 : average), "Global independent inheritance/current/weighted-average mismatch.");
                ObserveProduction(selected, "ControlledInheritedAxes", snapshot);
                Add("{\"phase\":\"GlobalInheritance\",\"variant\":" + variant + ",\"nativeNightTime\":" + Bool(_simulation.m_isNightTime) + ",\"snapshot\":" + SnapshotJson(Snapshot(selected)) + "}");
                ++_case; if (_case == 6) { _state = 4; Phase("GlobalInheritanceCasesComplete"); }
            }

            private void Apply(CivicFundingSnapshot snapshot, int? capacity, int? coverage)
            {
                Guard(); string reason;
                Require(_bridge.Apply(snapshot, capacity, coverage, out reason), "Civic command rejected: " + reason);
            }
            private CivicFundingSnapshot Snapshot(OwnedFacility facility)
            {
                Require(Matches(facility), "Civic fixture fingerprint no longer matches.");
                CivicFundingSnapshot snapshot = _bridge.GetSnapshot(facility.Id);
                Require(snapshot.Supported && snapshot.CanApply && snapshot.Identity != null
                    && snapshot.Identity.NativeBuildIndex == facility.BuildIndex && snapshot.Identity.PrefabName == facility.Info.name
                    && snapshot.Identity.AiTypeName == facility.Info.m_buildingAI.GetType().FullName
                    && (_session == 0 || snapshot.Session == _session), "Civic snapshot owner/identity unavailable.");
                return snapshot;
            }
            private static bool SameFunding(CivicFundingSnapshot a, CivicFundingSnapshot b)
            {
                return a.Session == b.Session && a.Identity.Matches(b.Identity) && a.CapacityOverridePercent == b.CapacityOverridePercent
                    && a.CoverageOverridePercent == b.CoverageOverridePercent && a.RawCapacity == b.RawCapacity
                    && a.ResolvedCapacityPercent == b.ResolvedCapacityPercent && a.ResolvedCoveragePercent == b.ResolvedCoveragePercent
                    && a.AverageCapacityPercent == b.AverageCapacityPercent && a.AverageCoveragePercent == b.AverageCoveragePercent;
            }

            private void ObserveProduction(OwnedFacility facility, string context, CivicFundingSnapshot before)
            {
                Guard(); Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
                Building.Frame frame = buildings[facility.Id].GetLastFrameData(); Citizen.BehaviourData behaviour = default(Citizen.BehaviourData);
                byte district = Singleton<DistrictManager>.instance.GetDistrict(buildings[facility.Id].m_position);
                bool school = facility.Info.m_buildingAI.GetType() == typeof(SchoolAI);
                uint priorCapacity = school ? Singleton<DistrictManager>.instance.m_districts.m_buffer[district].m_productionData.m_tempEducation1Capacity
                    : Singleton<DistrictManager>.instance.m_districts.m_buffer[district].m_productionData.m_tempHealCapacity;
                long cash = Singleton<EconomyManager>.instance.InternalCashAmount;
                OfferAudit offers = ReadOffers(school ? TransferManager.TransferReason.Student1 : TransferManager.TransferReason.Sick);
                OfferAudit moveOffers = school ? null : ReadOffers(TransferManager.TransferReason.SickMove);
                if (school) _schoolProduction((SchoolAI)facility.Info.m_buildingAI, facility.Id, ref buildings[facility.Id], ref frame, 100, 100, ref behaviour, 0, 0, 0, 0, 0, 0);
                else _hospitalProduction((HospitalAI)facility.Info.m_buildingAI, facility.Id, ref buildings[facility.Id], ref frame, 100, 100, ref behaviour, 0, 0, 0, 0, 0, 0);
                CivicFundingSnapshot after = Snapshot(facility);
                uint capacity = school ? Singleton<DistrictManager>.instance.m_districts.m_buffer[district].m_productionData.m_tempEducation1Capacity
                    : Singleton<DistrictManager>.instance.m_districts.m_buffer[district].m_productionData.m_tempHealCapacity;
                int expected = EfficiencyCeiling(before.RawCapacity, before.ResolvedCapacityPercent, 1);
                if (school) expected = Math.Min(expected, before.RawCapacity * 5 / 4);
                Require(after.ProductionObservations == before.ProductionObservations + 1 && after.LastFinalProductionRate == 100
                    && after.LastEffectiveCapacity == expected && capacity >= priorCapacity && capacity - priorCapacity == expected,
                    "Real controlled ProduceGoods capacity/district observation mismatch.");
                Require(Singleton<EconomyManager>.instance.InternalCashAmount == cash && after.MaintenanceObservations == before.MaintenanceObservations,
                    "ProduceGoods unexpectedly charged native maintenance.");
                int accumulation; float radius;
                if (school)
                {
                    SchoolAI ai = (SchoolAI)facility.Info.m_buildingAI; accumulation = ai.EducationAccumulation; radius = ai.m_educationRadius;
                    DistrictPolicies.Services policy = Singleton<DistrictManager>.instance.m_districts.m_buffer[district].m_servicePolicies;
                    if ((policy & DistrictPolicies.Services.EducationalBlimps) != 0) accumulation = (accumulation * 21 + 10) / 20;
                }
                else { HospitalAI ai = (HospitalAI)facility.Info.m_buildingAI; accumulation = ai.HealthCareAccumulation; radius = ai.m_healthCareRadius; }
                Require(accumulation > 0, "Controlled resource proof requires positive native accumulation.");
                int expectedResource = EfficiencyCeiling(accumulation, before.AverageCapacityPercent, 1);
                double expectedRadius = radius * before.AverageCoverageFactor;
                Require(after.LastResourceAmount == expectedResource && after.LastResourceRadius.HasValue
                    && Math.Abs(after.LastResourceRadius.Value - expectedRadius) <= 0.01, "Real native resource amount/radius event mismatch.");
                int students = -1, studentCapacity = -1, globalStudents = -1;
                if (school)
                {
                    SchoolAI ai = (SchoolAI)facility.Info.m_buildingAI;
                    ai.GetStudentCount(facility.Id, ref buildings[facility.Id], out students, out studentCapacity, out globalStudents);
                    Require(studentCapacity == expected && ai.StudentCount == before.RawCapacity, "Native GetStudentCount output/shared raw capacity mismatch.");
                    VerifyOffers(offers, facility.Id, expected > 0 ? 1 : 0, Math.Min(255, expected), 7, false);
                }
                else
                {
                    HospitalAI ai = (HospitalAI)facility.Info.m_buildingAI;
                    int ambulance = EfficiencyCeiling(ai.AmbulanceCount, before.ResolvedCapacityPercent, 1);
                    int cure = EfficiencyCeiling(checked((long)ai.CuringRate * 10000), before.ResolvedCapacityPercent, before.RawCapacity);
                    Require(after.LastAmbulanceTarget == ambulance && after.LastCureThreshold == cure && ai.PatientCapacity == before.RawCapacity,
                        "Native health ambulance/cure observation or unchanged raw patient capacity mismatch.");
                    // No synthetic patients/vehicles are introduced. Native admission
                    // offers are observable structure, not delivery/cure outcomes.
                    Require(behaviour.m_sickCount == 0, "Empty-fixture health offer expectation requires no actual sick visitors.");
                    bool active = ambulance > 0;
                    VerifyOffers(offers, facility.Id, expected >= 2 ? 1 : 0,
                        Math.Min(255, active ? Math.Min(expected >> 1, ambulance) : expected >> 1), active ? 7 : 1, active);
                    VerifyOffers(moveOffers, facility.Id, expected >= 1 ? 1 : 0, Math.Min(255, (expected + 1) >> 1), 7, false);
                }
                float nativeRange = facility.Info.m_buildingAI.GetCurrentRange(facility.Id, ref buildings[facility.Id]);
                Require(Math.Abs(nativeRange - after.CurrentRange) <= 0.01f, "Native GetCurrentRange does not match local coverage snapshot.");
                _activeDisplayRangeObserved |= nativeRange > 0;
                Add("{\"phase\":\"RealControlledProduceGoods\",\"context\":" + Json(context) + ",\"injectedProductionRate\":100,\"injectedFinalProductionRate\":100"
                    + ",\"districtId\":" + district + ",\"nativeDistrictCapacityBefore\":" + priorCapacity + ",\"nativeDistrictCapacityAfter\":" + capacity
                    + ",\"nativeStudentCount\":" + students + ",\"nativeStudentCapacity\":" + studentCapacity + ",\"nativeGlobalStudents\":" + globalStudents
                    + ",\"nativeCurrentRange\":" + Number(nativeRange) + ",\"snapshot\":" + SnapshotJson(after) + "}");
                Add("{\"phase\":\"NativeFixtureFlagsAtControlledOutput\",\"buildingId\":" + facility.Id
                    + ",\"flags\":" + Json(buildings[facility.Id].m_flags.ToString()) + ",\"productionRate\":" + buildings[facility.Id].m_productionRate
                    + ",\"nativeDisplayRange\":" + Number(nativeRange) + ",\"coverageResourceRadiusObservedSeparately\":true}");
            }

            // Read only the verified native incoming-offer layout on SIM, bounded
            // to eight priority buckets (256 entries each) for one exact reason.
            private OfferAudit ReadOffers(TransferManager.TransferReason reason)
            {
                ushort[] counts = (ushort[])_incomingCounts.GetValue(Singleton<TransferManager>.instance);
                TransferManager.TransferOffer[] offers = (TransferManager.TransferOffer[])_incomingOffers.GetValue(Singleton<TransferManager>.instance);
                Require(counts != null && counts.Length == 1024 && offers != null && offers.Length == 262144,
                    "Native incoming-offer array layout differs from verified TransferManager.Awake.");
                int start = checked((int)reason * 8);
                Require(start >= 0 && start + 7 < counts.Length, "Native offer reason outside bounded layout.");
                OfferAudit audit = new OfferAudit { Reason = reason, Counts = new ushort[8], Existing = new string[8] };
                for (int priority = 0; priority < 8; ++priority)
                {
                    int bucket = start + priority; Require(counts[bucket] <= 256, "Native offer count exceeds per-priority capacity.");
                    audit.Counts[priority] = counts[bucket]; StringBuilder existing = new StringBuilder();
                    for (int index = 0; index < counts[bucket]; ++index) existing.Append(OfferIdentity(offers[bucket * 256 + index])).Append(';');
                    audit.Existing[priority] = existing.ToString();
                }
                return audit;
            }
            private void VerifyOffers(OfferAudit before, ushort building, int expectedAdded, int amount, int priority, bool active)
            {
                OfferAudit after = ReadOffers(before.Reason);
                TransferManager.TransferOffer[] offers = (TransferManager.TransferOffer[])_incomingOffers.GetValue(Singleton<TransferManager>.instance);
                List<string> appended = new List<string>(); int added = 0;
                for (int p = 0; p < 8; ++p)
                {
                    int bucket = (int)before.Reason * 8 + p;
                    Require(after.Counts[p] >= before.Counts[p] && after.Counts[p] - before.Counts[p] <= 1,
                        "Native offer bucket changed unexpectedly during the single controlled call.");
                    StringBuilder existing = new StringBuilder();
                    for (int index = 0; index < before.Counts[p]; ++index) existing.Append(OfferIdentity(offers[bucket * 256 + index])).Append(';');
                    Require(existing.ToString() == before.Existing[p], "Pre-existing native offers changed during controlled call.");
                    for (int index = before.Counts[p]; index < after.Counts[p]; ++index)
                    {
                        TransferManager.TransferOffer offer = offers[bucket * 256 + index]; ++added;
                        Require(offer.Building == building && offer.Priority == priority && offer.Amount == amount && offer.Active == active,
                            "Observed new native offer differs from real empty-fixture capacity/admission output.");
                        appended.Add("{\"buildingId\":" + offer.Building + ",\"bucketPriority\":" + p + ",\"offerPriority\":" + offer.Priority
                            + ",\"amount\":" + offer.Amount + ",\"active\":" + Bool(offer.Active) + ",\"position\":" + Position(offer.Position) + "}");
                    }
                }
                Require(added == expectedAdded, "Native controlled offer delta mismatch or saturated offer buckets.");
                _nativeOffersVerified = true;
                Add("{\"phase\":\"ObservedNativeIncomingOfferDelta\",\"reason\":" + Json(before.Reason.ToString())
                    + ",\"beforePriorityCounts\":[" + string.Join(",", Array.ConvertAll(before.Counts, delegate(ushort count) { return count.ToString(CultureInfo.InvariantCulture); }))
                    + "],\"afterPriorityCounts\":[" + string.Join(",", Array.ConvertAll(after.Counts, delegate(ushort count) { return count.ToString(CultureInfo.InvariantCulture); }))
                    + "],\"appended\":[" + string.Join(",", appended.ToArray()) + "],\"deliveryOrPatientOutcomeVerified\":false}");
            }
            private static string OfferIdentity(TransferManager.TransferOffer offer)
            { return offer.m_flags + ":" + offer.m_object.RawData + ":" + offer.m_isLocalPark; }

            private void UnitsMaintenanceAndCodec()
            {
                OwnedFacility clinic = _owned[2];
                Apply(Snapshot(clinic), 120, 100); _bridge.Reconcile();
                Dictionary<uint, string> expanded = VisitUnits(clinic);
                int target = Snapshot(clinic).BudgetTargetCapacity;
                Require(expanded.Count * 5 >= target, "Native visit unit pool did not expand to the 110% capacity target.");
                Require(expanded.Count >= _initialClinicVisitUnits && ((target + 4) / 5 <= _initialClinicVisitUnits || expanded.Count > _initialClinicVisitUnits),
                    "Native hospital visit-unit expansion since fixture creation was not observed.");
                Apply(Snapshot(clinic), 0, 100); _bridge.Reconcile();
                Dictionary<uint, string> reduced = VisitUnits(clinic);
                foreach (KeyValuePair<uint, string> item in expanded)
                    Require(reduced.ContainsKey(item.Key) && reduced[item.Key] == item.Value, "Capacity reduction expelled or altered an existing visit unit/member.");
                Add("{\"phase\":\"NativeVisitUnitPoolGrowthAndReduction\",\"buildingId\":" + clinic.Id + ",\"targetAt120Percent\":" + target
                    + ",\"visitUnitsBeforeReduction\":" + expanded.Count + ",\"visitUnitsAfterReduction\":" + reduced.Count
                    + ",\"nativeVisitUnitsAtCreation\":" + _initialClinicVisitUnits
                    + ",\"existingUnitsAndMembersUnchanged\":true,\"populatedNoExpulsionCoverage\":false}");
                foreach (OwnedFacility facility in new[] { _owned[0], _owned[2] })
                {
                    SetBudget(facility.Info.m_class, 100, 100); Apply(Snapshot(facility), 120, 50);
                    CivicFundingSnapshot before = Snapshot(facility);
                    Building[] buffer = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
                    Require((buffer[facility.Id].m_flags & (Building.Flags.Original | Building.Flags.Evacuating)) == 0 && buffer[facility.Id].m_productionRate == 100,
                        "Controlled native maintenance fixture is not a normal production-100 building.");
                    Building.Frame frame = buffer[facility.Id].GetLastFrameData();
                    long cash = Singleton<EconomyManager>.instance.InternalCashAmount;
                    _activeStep((PlayerBuildingAI)facility.Info.m_buildingAI, facility.Id, ref buffer[facility.Id], ref frame);
                    CivicFundingSnapshot after = Snapshot(facility); long debit = cash - Singleton<EconomyManager>.instance.InternalCashAmount;
                    Require(after.MaintenanceObservations == before.MaintenanceObservations + 1 && after.LastNativeMaintenanceCost.HasValue
                        && after.MaintenanceFetchCalls == before.MaintenanceFetchCalls + 1 && after.LastRequestedMaintenanceCost.HasValue
                        && after.LastRequestedMaintenanceCost > 0 && after.LastMaintenanceAvailability.HasValue,
                        "Single real SimulationStepActive did not observe exactly one native maintenance charge path.");
                    CivicFundingEvaluation expected = CivicFundingEvaluator.Evaluate(after.Identity.Archetype, 120, 50, after.LastNativeMaintenanceCost.Value);
                    // Native EconomyManager debits the requested maintenance amount;
                    // its original PeekResource return separately controls affordability.
                    Require(after.LastRequestedMaintenanceCost == expected.MaintenanceCost && debit == expected.MaintenanceCost
                        && after.LastMaintenanceAvailability >= 0 && after.LastMaintenanceAvailability <= expected.MaintenanceCost,
                        "Native maintenance actual debit/request/original Fetch return mismatch.");
                    _cashShortfallObserved |= after.LastMaintenanceAvailability < expected.MaintenanceCost; _maintenanceDebit += debit;
                    Add("{\"phase\":\"RealSingleSimulationStepActive\",\"buildingId\":" + facility.Id + ",\"cashBefore\":" + cash
                        + ",\"cashAfter\":" + Singleton<EconomyManager>.instance.InternalCashAmount + ",\"cashDelta\":" + debit
                        + ",\"maintenanceObservationDelta\":" + (after.MaintenanceObservations - before.MaintenanceObservations)
                        + ",\"actualNativeFetchCallDelta\":" + (after.MaintenanceFetchCalls - before.MaintenanceFetchCalls)
                        + ",\"cashShortfallObserved\":" + Bool(after.LastMaintenanceAvailability < expected.MaintenanceCost)
                        + ",\"finalProductionMayBeLimitedByRealMissingUtilities\":true,\"snapshot\":" + SnapshotJson(after) + "}");
                }
                byte[] saved = _bridge.CaptureSave(); Require(saved != null, "Civic CaptureSave payload unavailable.");
                CivicFundingLedger ledger; using (MemoryStream input = new MemoryStream(saved, false)) ledger = CivicFundingLedgerCodec.Read(input);
                byte[] canonical; using (MemoryStream output = new MemoryStream()) { CivicFundingLedgerCodec.Write(output, ledger); canonical = output.ToArray(); }
                Require(Equal(saved, canonical) && ledger.Count > 0, "Actual civic save bytes did not roundtrip canonically."); _canonicalCodec = true;
                Add("{\"phase\":\"ActualCivicCaptureSaveCanonicalCodec\",\"byteCount\":" + saved.Length + ",\"sha256\":" + Json(Hash(saved))
                    + ",\"ledgerId\":" + Json(ledger.LedgerId.ToString("D")) + ",\"records\":" + ledger.Count + ",\"diskSaveReloadVerified\":false}");
                _verified = true; _state = 5; Phase("NativeCivicAssertionsCompleteAwaitingCleanup");
            }

            private Dictionary<uint, string> VisitUnits(OwnedFacility facility)
            {
                Require(Matches(facility), "Visit unit owner fingerprint changed.");
                CitizenUnit[] units = Singleton<CitizenManager>.instance.m_units.m_buffer;
                Dictionary<uint, string> result = new Dictionary<uint, string>(); HashSet<uint> seen = new HashSet<uint>();
                uint id = Singleton<BuildingManager>.instance.m_buildings.m_buffer[facility.Id].m_citizenUnits;
                while (id != 0)
                {
                    Require(id < units.Length && seen.Add(id) && seen.Count <= 65536, "Invalid native civic citizen-unit chain.");
                    CitizenUnit unit = units[id];
                    Require((unit.m_flags & CitizenUnit.Flags.Created) != 0 && unit.m_building == facility.Id, "Native citizen-unit identity is invalid.");
                    if ((unit.m_flags & CitizenUnit.Flags.Visit) != 0)
                        result.Add(id, unit.m_flags + ":" + unit.m_building + ":" + unit.m_citizen0 + ":" + unit.m_citizen1 + ":" + unit.m_citizen2 + ":" + unit.m_citizen3 + ":" + unit.m_citizen4);
                    id = unit.m_nextUnit;
                }
                return result;
            }
            private bool Matches(OwnedFacility facility)
            {
                Building data = Singleton<BuildingManager>.instance.m_buildings.m_buffer[facility.Id];
                return (data.m_flags & (Building.Flags.Created | Building.Flags.Deleted)) == Building.Flags.Created
                    && data.m_buildIndex == facility.BuildIndex && ReferenceEquals(data.Info, facility.Info)
                    && (data.m_position - facility.Position).sqrMagnitude <= 0.01f && Math.Abs(data.m_angle - facility.Angle) < 0.001f;
            }

            public void BeforeLevelUnload()
            {
                if (_quitCommitted) return;
                // NativeSaveRoundtrip owns the intentional LoadGame handoff;
                // retain the civic fixture and its borrowed pause until the
                // reloaded buffers have been rebound and audited.
                if (_owner._roundtrip != null && _owner._roundtrip.CivicOwnedReloadInProgress) return;
                try { InterruptAndCleanup("External level unload interrupted civic QA."); } catch { }
            }
            public void CancelBeforePluginRemoval() { try { InterruptAndCleanup("Plugin disabled during civic QA."); } catch { } }
            private void InterruptAndCleanup(string reason)
            {
                lock (_gate)
                {
                    _interrupted = true; _verified = false;
                    if (_failure == null) _failure = reason;
                    RequestCleanup();
                }
            }
            public void RequestCleanup()
            {
                lock (_gate)
                {
                    if (_cleanupFinished || _cleanupQueued) return;
                    _interrupted = true; _cleanupQueued = true;
                    // FIFO after every accepted action, even if cancellation arrived before acquisition.
                    if (_simulation == null) { _cleanupFinished = true; _cleanupQueued = false; _cleaned = _owned.Count == 0; return; }
                    try { _simulation.AddAction(CleanupOnSimulation); }
                    catch (Exception error)
                    {
                        // No retry of an ambiguously accepted engine operation. The
                        // parent may quit this exact disposable scene while still paused.
                        _cleanupQueued = false; _cleanupFinished = true; _cleaned = false; _verified = false;
                        _cleanupError = "Cleanup queue rejected: " + error; if (_failure == null) _failure = _cleanupError;
                        if (_subscribed) { Application.logMessageReceivedThreaded -= OnLog; _subscribed = false; }
                        Phase("CleanupQueueRejected");
                    }
                }
            }

            private void CleanupOnSimulation()
            {
                lock (_gate)
                {
                    try
                    {
                        Require(OwnsScene(), "CleanupIncomplete: native scene/metadata/buffers changed; no foreign IDs, budgets or pause modified.");
                        Require(ModRuntime.CurrentCivicFundingBridge == null || ReferenceEquals(ModRuntime.CurrentCivicFundingBridge, _bridge),
                            "CleanupIncomplete: a different live civic bridge replaced the owner.");
                        Require(_leaseHeld || _owned.Count == 0, "CleanupIncomplete: civic pause lease lost.");
                        if (_owned.Count > 0)
                        {
                            // A stopped bridge is normal after OnLevelUnloading. Native
                            // fingerprints and the immutable scene lease authorize release.
                            foreach (OwnedFacility facility in _owned) Require(Matches(facility), "CleanupIncomplete: civic fingerprint changed; release refused.");
                            CivicFundingSnapshot stale = ReferenceEquals(ModRuntime.CurrentCivicFundingBridge, _bridge) && _bridge.Ready
                                ? Snapshot(_owned[0]) : null;
                            for (int index = _owned.Count - 1; index >= 0; --index)
                            {
                                Require(OwnsScene(), "CleanupIncomplete: scene changed inside native release.");
                                OwnedFacility facility = _owned[index]; List<uint> units = OwnedUnitIds(facility);
                                _buildingManager.ReleaseBuilding(facility.Id);
                                RequireReleasedFacility(facility, units);
                            }
                            _cleaned = true;
                            if (stale != null)
                            {
                                string reason; _releasedCommandRejected = !_bridge.Apply(stale, 50, 50, out reason);
                                Require(_releasedCommandRejected, "Released native identity still accepted a civic command.");
                                Add("{\"phase\":\"ReleasedIdentityCommandRejected\",\"buildingId\":" + stale.BuildingId + ",\"reason\":" + Json(reason) + "}");
                            }
                            else Add("{\"phase\":\"StoppedBridgeNativeCleanup\",\"releasedCommandCoverage\":false,\"exactNativeSceneAndFingerprints\":true}");
                        }
                        if (_roadSegments.Count > 0)
                        {
                            // The paid access road is owned test infrastructure: release it
                            // only after every facility is gone, all segments before any node.
                            Require(OwnsScene(), "CleanupIncomplete: scene changed inside access road release.");
                            foreach (ushort id in _roadSegments)
                            {
                                Require((_segmentBuffer[id].m_flags & NetSegment.Flags.Created) != 0,
                                    "CleanupIncomplete: access road segment flags changed before release.");
                                _netManager.ReleaseSegment(id, true);
                                Require((_segmentBuffer[id].m_flags & NetSegment.Flags.Created) == 0,
                                    "CleanupIncomplete: access road segment release did not finish.");
                            }
                            foreach (ushort id in _roadNodes)
                            {
                                NetNode nodeData = _nodeBuffer[id];
                                Require((nodeData.m_flags & NetNode.Flags.Created) != 0 && nodeData.CountSegments() == 0,
                                    "CleanupIncomplete: access road node still referenced before release.");
                                _netManager.ReleaseNode(id);
                                Require((_nodeBuffer[id].m_flags & NetNode.Flags.Created) == 0,
                                    "CleanupIncomplete: access road node release did not finish.");
                            }
                            _roadSegments.Clear(); _roadNodes.Clear();
                            Add("{\"phase\":\"NativeAccessRoadReleased\",\"segmentsReleased\":true,\"nodesReleased\":true}");
                        }
                        _cleaned = true;
                    }
                    catch (Exception error) { _cleanupError = error.ToString(); _cleaned = false; _verified = false; if (_failure == null) _failure = _cleanupError; }
                    finally
                    {
                        if (OwnsScene() && (ModRuntime.CurrentCivicFundingBridge == null || ReferenceEquals(ModRuntime.CurrentCivicFundingBridge, _bridge)))
                        {
                            try
                            {
                                // Validate every captured key before writing any global budget.
                                foreach (Budget budget in _budgets)
                                    Require(ReferenceEquals(budget.Info.m_class, budget.Class) && budget.Class.m_service == budget.Service
                                        && budget.Class.m_subService == budget.SubService, "Captured budget class/key identity changed.");
                                foreach (Budget budget in _budgets)
                                {
                                    _economy.SetBudget(budget.Service, budget.SubService, budget.Day, false);
                                    _economy.SetBudget(budget.Service, budget.SubService, budget.Night, true);
                                    Require(_economy.GetBudget(budget.Class, false) == budget.Day && _economy.GetBudget(budget.Class, true) == budget.Night,
                                        "Native budget restoration did not preserve original day/night values.");
                                }
                            }
                            catch (Exception error) { _cleanupError = (_cleanupError ?? "") + " Budget restore failed: " + error; _cleaned = false; _verified = false; }
                        }
                        // Resume only after verified release + budget restoration.
                        // Terminal own-scene failure retains the pause for controlled
                        // process exit; foreign ownership never authorizes a bool write.
                        if (_leaseHeld && OwnsScene() && _cleaned && _cleanupError == null)
                        {
                            try { _simulation.ForcedSimulationPaused = _oldForcedPause; _leaseHeld = false; }
                            catch (Exception error) { _cleaned = false; _verified = false; _cleanupError = "Forced-pause restore failed: " + error; }
                        }
                        else if (_leaseHeld && !OwnsScene())
                        { _leaseHeld = false; _pauseRestorationUnproved = true; _cleanupError = (_cleanupError ?? "") + " Forced-pause ownership lost; restoration unproved."; }
                        _cleanupFinished = true; _cleanupQueued = false;
                        if (_subscribed) { Application.logMessageReceivedThreaded -= OnLog; _subscribed = false; }
                        Phase(_cleaned && _cleanupError == null ? (_verified ? "VerifiedAndReleased" : "ReleasedAfterFailure") : "CleanupIncomplete");
                    }
                }
            }

            private List<uint> OwnedUnitIds(OwnedFacility facility)
            {
                List<uint> units = new List<uint>(); HashSet<uint> seen = new HashSet<uint>();
                uint id = _buildingBuffer[facility.Id].m_citizenUnits;
                while (id != 0)
                {
                    Require(id < _unitBuffer.Length && seen.Add(id) && seen.Count <= 65536, "CleanupIncomplete: invalid native unit chain.");
                    CitizenUnit unit = _unitBuffer[id];
                    Require((unit.m_flags & CitizenUnit.Flags.Created) != 0 && unit.m_building == facility.Id, "CleanupIncomplete: native unit owner differs.");
                    units.Add(id); id = unit.m_nextUnit;
                }
                return units;
            }
            private void RequireReleasedFacility(OwnedFacility facility, List<uint> units)
            {
                Building released = _buildingBuffer[facility.Id];
                // Deleted alone is the IN-PROGRESS native release marker (5439),
                // not proof the callback reached None/ReleaseItem (5517+).
                Require(released.m_flags == Building.Flags.None && released.m_citizenUnits == 0,
                    "CleanupIncomplete: native building release did not finish.");
                foreach (uint id in units) Require((_unitBuffer[id].m_flags & CitizenUnit.Flags.Created) == 0,
                    "CleanupIncomplete: a captured native citizen unit remains allocated.");
                TransferManager.TransferReason[] reasons = facility.Info.m_buildingAI.GetType() == typeof(SchoolAI)
                    ? new[] { TransferManager.TransferReason.Student1 }
                    : new[] { TransferManager.TransferReason.Sick, TransferManager.TransferReason.SickMove };
                foreach (TransferManager.TransferReason reason in reasons)
                {
                    OfferAudit state = ReadOffers(reason);
                    TransferManager.TransferOffer[] offers = (TransferManager.TransferOffer[])_incomingOffers.GetValue(Singleton<TransferManager>.instance);
                    for (int priority = 0; priority < 8; ++priority) for (int index = 0; index < state.Counts[priority]; ++index)
                        Require(offers[((int)reason * 8 + priority) * 256 + index].Building != facility.Id,
                            "CleanupIncomplete: released civic facility still owns a native transfer offer.");
                }
                Add("{\"phase\":\"NativeReleaseCompleted\",\"buildingId\":" + facility.Id + ",\"flagsNone\":true,\"formerCitizenUnitsReleased\":"
                    + units.Count + ",\"ownedTransferOffersRemaining\":0}");
            }

            private void BindNativeDelegates()
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                MethodInfo active = typeof(PlayerBuildingAI).GetMethod("SimulationStepActive", flags);
                MethodInfo school = typeof(SchoolAI).GetMethod("ProduceGoods", flags), hospital = typeof(HospitalAI).GetMethod("ProduceGoods", flags);
                Require(active != null && school != null && hospital != null, "Native protected service methods missing.");
                _activeStep = (ActiveStep)Delegate.CreateDelegate(typeof(ActiveStep), active);
                _schoolProduction = (SchoolProduction)Delegate.CreateDelegate(typeof(SchoolProduction), school);
                _hospitalProduction = (HospitalProduction)Delegate.CreateDelegate(typeof(HospitalProduction), hospital);
                _incomingCounts = typeof(TransferManager).GetField("m_incomingCount", BindingFlags.NonPublic | BindingFlags.Instance);
                _incomingOffers = typeof(TransferManager).GetField("m_incomingOffers", BindingFlags.NonPublic | BindingFlags.Instance);
                Require(_incomingCounts != null && _incomingCounts.FieldType == typeof(ushort[]) && _incomingOffers != null
                    && _incomingOffers.FieldType == typeof(TransferManager.TransferOffer[]), "Verified native incoming-offer fields differ.");
                Add("{\"phase\":\"NativeOpenInstanceDelegateBinding\",\"methods\":[" + MethodEvidence(active) + "," + MethodEvidence(school) + "," + MethodEvidence(hospital)
                    + "," + MethodEvidence(typeof(TransferManager).GetMethod("AddIncomingOffer", BindingFlags.Public | BindingFlags.Instance))
                    + "],\"refBuildingUsesActualNativeBuffer\":true,\"callsAreControlledQa\":true"
                    + ",\"offerAudit\":\"Read-only m_incomingCount ushort[1024], m_incomingOffers TransferOffer[262144]; reason*8+priority, bucket*256+index; no writes\"}");
            }
            private static string MethodEvidence(MethodInfo method)
            {
                return "{\"type\":" + Json(method.DeclaringType.FullName) + ",\"method\":" + Json(method.ToString())
                    + ",\"mvid\":" + Json(method.Module.ModuleVersionId.ToString("D")) + ",\"originalMethodBodyIlSha256\":" + Json(Hash(method.GetMethodBody().GetILAsByteArray())) + "}";
            }
            private static string AssemblyEvidence(Assembly assembly)
            {
                string location = null, hash = null, reason = null;
                try { location = assembly.Location; if (!string.IsNullOrEmpty(location) && File.Exists(location)) hash = Hash(File.ReadAllBytes(location)); else reason = "Loaded assembly location unavailable; no claim about loaded file bytes."; }
                catch (Exception error) { reason = error.GetType().Name + ":" + error.Message; }
                return "{\"fullName\":" + Json(assembly.FullName) + ",\"mvid\":" + Json(assembly.ManifestModule.ModuleVersionId.ToString("D"))
                    + ",\"location\":" + Json(location) + ",\"diskFileSha256\":" + Json(hash) + ",\"hashUnavailableReason\":" + Json(reason) + "}";
            }
            private static bool Equal(byte[] left, byte[] right)
            { if (left == null || right == null || left.Length != right.Length) return false; for (int i = 0; i < left.Length; ++i) if (left[i] != right[i]) return false; return true; }
            // Exact rational SC4 efficiency curve; avoid binary-double ceil noise
            // at 110% (e.g. 100*1.1 can otherwise round upward to 111).
            private static int EfficiencyCeiling(long raw, int percent, int divisor)
            {
                if (raw < 0 || percent < 0 || percent > 255 || divisor <= 0) throw new InvalidDataException("Invalid controlled efficiency expectation.");
                int numerator = percent <= 100 ? percent * 2 : Math.Min(percent, 120) + 100;
                long denominator = checked(200L * divisor);
                return checked((int)((checked(raw * numerator) + denominator - 1) / denominator));
            }
            private static string Hash(byte[] value) { using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(value)).Replace("-", ""); }
            private static string Number(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
            private static string Nullable(int? value) { return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "null"; }
            private static string Position(Vector3 value) { return "{\"x\":" + Number(value.x) + ",\"y\":" + Number(value.y) + ",\"z\":" + Number(value.z) + "}"; }
            private static string Bounded(string text, int max) { return text == null || text.Length <= max ? text : text.Substring(0, max); }
            private static string SnapshotJson(CivicFundingSnapshot s)
            {
                return "{\"buildingId\":" + s.BuildingId + ",\"session\":" + s.Session + ",\"status\":" + Json(s.Status)
                    + ",\"supported\":" + Bool(s.Supported) + ",\"canApply\":" + Bool(s.CanApply)
                    + ",\"prefab\":" + Json(s.Identity == null ? null : s.Identity.PrefabName)
                    + ",\"nativeBuildIndex\":" + (s.Identity == null ? "null" : s.Identity.NativeBuildIndex.ToString(CultureInfo.InvariantCulture))
                    + ",\"capacityOverridePercent\":" + Nullable(s.CapacityOverridePercent) + ",\"coverageOverridePercent\":" + Nullable(s.CoverageOverridePercent)
                    + ",\"globalDay\":" + s.GlobalDayPercent + ",\"globalNight\":" + s.GlobalNightPercent + ",\"globalCurrent\":" + s.GlobalCurrentPercent + ",\"globalAverage\":" + s.GlobalAveragePercent
                    + ",\"resolvedCapacity\":" + s.ResolvedCapacityPercent + ",\"resolvedCoverage\":" + s.ResolvedCoveragePercent
                    + ",\"averageCapacity\":" + s.AverageCapacityPercent + ",\"averageCoverage\":" + s.AverageCoveragePercent
                    + ",\"efficiencyFactor\":" + Number(s.EfficiencyFactor) + ",\"coverageFactor\":" + Number(s.CoverageFactor)
                    + ",\"averageEfficiencyFactor\":" + Number(s.AverageEfficiencyFactor) + ",\"averageCoverageFactor\":" + Number(s.AverageCoverageFactor)
                    + ",\"rawCapacity\":" + s.RawCapacity + ",\"budgetTargetCapacity\":" + s.BudgetTargetCapacity + ",\"currentRange\":" + Number(s.CurrentRange)
                    + ",\"observationSequence\":" + s.ObservationSequence + ",\"maintenanceObservations\":" + s.MaintenanceObservations + ",\"productionObservations\":" + s.ProductionObservations
                    + ",\"maintenanceFetchCalls\":" + s.MaintenanceFetchCalls
                    + ",\"lastFinalProductionRate\":" + Nullable(s.LastFinalProductionRate) + ",\"lastEffectiveCapacity\":" + Nullable(s.LastEffectiveCapacity)
                    + ",\"lastNativeMaintenanceCost\":" + Nullable(s.LastNativeMaintenanceCost) + ",\"lastMaintenanceAvailability\":" + Nullable(s.LastMaintenanceAvailability)
                    + ",\"lastRequestedMaintenanceCost\":" + Nullable(s.LastRequestedMaintenanceCost)
                    + ",\"lastResourceAmount\":" + Nullable(s.LastResourceAmount) + ",\"lastResourceRadius\":" + (s.LastResourceRadius.HasValue ? Number(s.LastResourceRadius.Value) : "null")
                    + ",\"lastCureThreshold\":" + Nullable(s.LastCureThreshold) + ",\"lastAmbulanceTarget\":" + Nullable(s.LastAmbulanceTarget) + "}";
            }
            public string ReportJson()
            {
                lock (_gate) lock (_errorGate)
                    return "{\"phase\":" + Json(_phase) + ",\"verifiedControlledCivicFunding\":" + Bool(_verified && _cleaned && _cleanupError == null && !_cancelled)
                        + ",\"cleaned\":" + Bool(_cleaned) + ",\"cleanupFinished\":" + Bool(_cleanupFinished) + ",\"forcedPauseHeld\":" + Bool(_leaseHeld)
                        + ",\"forcedPauseRestorationUnproved\":" + Bool(_pauseRestorationUnproved)
                        + ",\"controlledQuitUnderOwnedPauseAfterCleanupFailure\":" + Bool(_quitUnderOwnedPause)
                        + ",\"positiveNativeDisplayRangeObserved\":" + Bool(_activeDisplayRangeObserved)
                        + ",\"displayRangeLimit\":\"An inactive freshly created facility can report zero range; nonzero coverage radius is separately asserted at real Resource.Add calls, without forcing Active flags.\""
                        + ",\"error\":" + Json(_failure) + ",\"cleanupError\":" + Json(_cleanupError) + ",\"constructionDebit\":" + _constructionDebit + ",\"maintenanceDebit\":" + _maintenanceDebit
                        + ",\"cashShortfallObserved\":" + Bool(_cashShortfallObserved) + ",\"staleCommandRejected\":" + Bool(_staleCommandRejected)
                        + ",\"releasedIdentityCommandRejected\":" + Bool(_releasedCommandRejected) + ",\"canonicalCaptureSaveCodec\":" + Bool(_canonicalCodec)
                        + ",\"organicOrPatientOutcomeCoverage\":false,\"diskSaveReloadCoverage\":" + Bool(_owner._roundtrip != null && _owner._roundtrip.CivicDiskSaveReloadCoverage) + ",\"foreignOwnerReuseCoverage\":false,\"nativeTransferOfferCoverage\":" + Bool(_nativeOffersVerified)
                        + ",\"proofScope\":\"Four paid native facilities on one paid native access road; real protected ProduceGoods with explicit 100/100 inputs; real single SimulationStepActive and native debit. No artificial people, cash, utility state, RNG or prefabs. Empty visit-unit structural proof only.\""
                        + ",\"lifecycleLimit\":\"Exclusive disposable city only; foreign programmatic loading/owner replacement is refused, not a proven forced-pause restoration path.\""
                        + ",\"optionalToolbarDiagnostics\":" + _optionalToolbarCount + ",\"optionalToolbarSamples\":[" + string.Join(",", _optionalToolbarDiagnostics.ToArray()) + "]"
                        + ",\"diagnosticSnapshot\":" + (_diagnosticReport ?? "null")
                        + ",\"sourceEvidence\":" + (_sourceEvidence ?? "null") + ",\"nativeErrors\":[" + JsonList(_errors) + "],\"receipts\":[" + string.Join(",", _receipts.ToArray()) + "]}";
            }
        }
    }
}
