using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ColossalFramework;
using ColossalFramework.Math;
using ColossalFramework.UI;
using SC4Evolution.Mod.Economy;
using SC4Evolution.Mod.Runtime;
using SC4Evolution.Simulation;
using UnityEngine;

namespace SC4Evolution.Mod.Diagnostics
{
    public sealed partial class AutonomousTestDriver
    {
        // This probe does not use the scripted class fixture/offset/citizen APIs.
        private sealed partial class NativeOrganicResidentialProbe
        {
            private readonly AutonomousTestDriver _owner;
            private readonly object _gate = new object();
            private readonly List<string> _receipts = new List<string>(), _errors = new List<string>();
            private readonly Dictionary<ushort, NetStamp> _nodes = new Dictionary<ushort, NetStamp>(), _segments = new Dictionary<ushort, NetStamp>();
            private readonly Dictionary<ushort, BuildingStamp> _buildings = new Dictionary<ushort, BuildingStamp>();
            private readonly HashSet<ushort> _baselineNodes = new HashSet<ushort>(), _baselineSegments = new HashSet<ushort>();
            private readonly HashSet<ushort> _callNodes = new HashSet<ushort>(), _callSegments = new HashSet<ushort>();
            private readonly HashSet<ushort> _zoningRoads = new HashSet<ushort>(), _zonedBlocks = new HashSet<ushort>();
            private readonly List<ushort> _grownHomes = new List<ushort>();
            private SimulationManager _simulation;
            private EconomicBridge _bridge;
            private NetInfo _road, _pipe, _power;
            private BuildingInfo _wind, _tower, _drain;
            private ushort _stubA, _stubB;
            private Vector3 _head, _end, _forward, _side;
            private Placement _windSite, _towerSite, _drainSite, _outletWindSite;
            private LinePlan _localPower, _shorePower;
            private volatile bool _pending, _interrupted, _done;
            private bool _cleanupQueued, _speedHeld, _oldPaused, _verified, _cleaned, _subscribed;
            private int _oldSpeed, _state, _revision, _reportedRevision, _zonedCells;
            private int _lastReservedNodes, _lastReservedSegments;
            private float _nextObservation;
            private string _failure, _phase = "NotStarted";
            private string _lastPlacementDiagnostic;
            private string _lastNetworkProfile = "[]";
            private long _initialCash, _constructionDebit;
            private uint _firstSettledFrame;
            private string _qualifiedSignature;
            private uint _qualifiedFrame, _qualifiedBuildIndex;
            private ushort _qualifiedBuilding;
            private int _qualifiedClass;
            private UIComponent _lastObservedModal;
            private bool _observedModalStack, _modalDiagnosticsCapped;
            private int _modalDiagnosticCount;
            private bool _capacityObservationKnown;
            private string _capacityObservation = "null";
            private bool _capacityModeRequested, _capacityAdmissionVerified;
            private string _capacityAdmission = "null";
            private NativeDiagnosticSnapshot _optionalToolbarSnapshot;
            private volatile NativeOrganicDiagnosticContext _optionalToolbarContext;
            private readonly List<string> _baselineOptionalToolbarDiagnostics = new List<string>();
            private int _optionalToolbarDiagnosticCount, _optionalServicePointCount, _optionalHotelCount, _optionalRaceCount;
            private const long MaximumConstructionDebit = 4500000; // Native internal money units.

            private sealed class NetStamp { public NetInfo Info; public Vector3 Position; public uint BuildIndex; public ushort Start, End; }
            private sealed class BuildingStamp { public BuildingInfo Info; public Vector3 Position; public uint BuildIndex; }
            private sealed class Placement { public BuildingInfo Info; public Vector3 Position, InputPosition; public float Angle, WaterHeight; public int Cost, Production; public bool Shoreline; }
            private sealed class ShoreCandidate { public Vector3 Query, Shore, Direction; public float Water, Distance; }
            private sealed class ZoneCandidate { public ushort Block, Segment; public Vector3 Center; public float WindDistance; }
            private sealed class PipePort { public Vector3 Position; public float Elevation; public NetInfo Info; public int PathIndex; public bool FixedHeight; }
            private sealed class LinePlan { public Vector3 A, B; public int Cost; }

            public NativeOrganicResidentialProbe(AutonomousTestDriver owner) { _owner = owner; }
            public bool CleanupReady { get { return _done && !_speedHeld && !_pending; } }
            public bool DiagnosticOnly { get { return _owner._request.OrganicInventoryOnly; } }

            public void Start()
            {
                if (!_owner.IsOrganicMode() || _owner._selectedAsset != "NorthEurope1FINAL.Green Plains_Data"
                    || _owner._selectedChecksum != "d09cc7008c7f95c0e55b1ebed0a80579")
                    throw new InvalidOperationException("Organic QA only supports this explicit built-in Green Plains asset/checksum.");
                _simulation = Singleton<SimulationManager>.instance;
                _bridge = ModRuntime.CurrentEconomicBridge;
                if (_bridge == null) throw new InvalidOperationException("Organic QA requires the real economic bridge publication.");
                _initialOwnerEpoch = _bridge.DemandContextSession;
                // Unity/Steam/type/hash/session reads occur only here, on MAIN, before subscribing.
                _optionalToolbarSnapshot = NativeDiagnosticSnapshot.CaptureOnMainThread(_owner);
                _optionalToolbarContext = new NativeOrganicDiagnosticContext(_owner._request.Mode, "OrganicResidentialActive",
                    OwnsCity() ? _owner._session : null, _optionalToolbarSnapshot);
                Application.logMessageReceivedThreaded += OnLog;
                _subscribed = true;
                SetPhase("WaitingForUnobstructedNativeInventory");
            }

            public bool Step()
            {
                lock (_gate)
                {
                    if (_revision != _reportedRevision)
                    {
                        _reportedRevision = _revision;
                        _owner._phase = "Organic:" + _phase;
                        _owner.WriteReport();
                    }
                    lock (_errors) if (_errors.Count != 0 && _failure == null) _failure = "Native error during organic QA: " + _errors[0];
                    if (_failure != null) throw new InvalidOperationException("Organic residential QA failed: " + _failure);
                    if (_done) return _cleaned && (DiagnosticOnly || _verified);
                    if (_handoffRequested) return false;
                    if (_pending || _interrupted || Time.realtimeSinceStartup < _nextObservation) return false;
                    if (UIView.HasModalInput())
                    {
                        ObserveBlockingModal();
                        SetPhase("WaitingForNativeModalWithoutDismissal");
                        return false;
                    }
                    _observedModalStack = false;
                    _lastObservedModal = null;
                    _owner._blockingModal = null;
                    _nextObservation = Time.realtimeSinceStartup + 1f;
                    if (_simulation.ForcedSimulationPaused) { SetPhase("WaitingForExternalForcedPause"); return false; }
                    if (_state == 0) Queue(InventoryAndPlan);
                    else if (_state == 1)
                    {
                        if (DiagnosticOnly) RequestCleanup();
                        else Queue(BuildInfrastructure);
                    }
                    else if (_state == 2) Queue(ObserveOrganicGrowth);
                    return false;
                }
            }

            private bool OwnsCity()
            {
                return _owner._createdCity && ReferenceEquals(_active, _owner) && Singleton<SimulationManager>.exists
                    && ReferenceEquals(Singleton<SimulationManager>.instance, _simulation) && _simulation.m_metaData != null
                    && _simulation.m_metaData.m_gameInstanceIdentifier == _owner._session;
            }
            private void Guard()
            {
                if (_interrupted || _cancelled || !OwnsCity() || !ReferenceEquals(_owner._organicProbe, this)
                    || !ReferenceEquals(ModRuntime.CurrentEconomicBridge, _bridge))
                    throw new OperationCanceledException("Organic QA epoch/session/bridge no longer owns execution.");
                lock (_errors) if (_errors.Count != 0) throw new InvalidOperationException("Native error during organic QA: " + _errors[0]);
            }
            private void Queue(Action operation)
            {
                lock (_gate)
                {
                    if (_pending || _done || _interrupted) return;
                    _pending = true;
                    try
                    {
                        _simulation.AddAction(delegate
                        {
                            lock (_gate)
                            {
                                try { Guard(); operation(); }
                                catch (Exception error) { _failure = error.ToString(); SetPhase("Failed"); }
                                finally { _pending = false; }
                            }
                        });
                    }
                    catch { _pending = false; throw; }
                }
            }
            private void OnLog(string message, string stack, LogType type)
            {
                if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
                NativeOrganicDiagnosticContext context = _optionalToolbarContext;
                string normalized = message == null ? null : message.TrimEnd('\r', '\n');
                string reason = type == LogType.Error && context != null && context.Snapshot != null
                    ? context.Snapshot.ClassifyOrganicOptionalToolbar(normalized, context) : null;
                lock (_errors)
                {
                    if (!ReferenceEquals(context, _optionalToolbarContext)) reason = null;
                    if (reason != null)
                    {
                        if (_optionalToolbarDiagnosticCount < int.MaxValue) ++_optionalToolbarDiagnosticCount;
                        if (normalized.IndexOf("'ServicePointGroupPanel'", StringComparison.Ordinal) >= 0) { if (_optionalServicePointCount < int.MaxValue) ++_optionalServicePointCount; }
                        else if (normalized.IndexOf("'HotelGroupPanel'", StringComparison.Ordinal) >= 0) { if (_optionalHotelCount < int.MaxValue) ++_optionalHotelCount; }
                        else if (_optionalRaceCount < int.MaxValue) ++_optionalRaceCount;
                        if (_baselineOptionalToolbarDiagnostics.Count < 24)
                            _baselineOptionalToolbarDiagnostics.Add("{\"logType\":\"Error\",\"message\":" + Json(BoundedModalText(message, 4096))
                                + ",\"messageTruncated\":" + Bool(message != null && message.Length > 4096) + ",\"stack\":" + Json(BoundedModalText(stack, 4096))
                                + ",\"stackTruncated\":" + Bool(stack != null && stack.Length > 4096) + ",\"reason\":" + Json(reason)
                                + ",\"mode\":" + Json(context.Mode) + ",\"scopePhase\":" + Json(context.Phase) + ",\"ownedSession\":" + Json(context.OwnedSession)
                                + ",\"exactCallsiteConfirmed\":false}");
                        return;
                    }
                    if (_errors.Count < 24) _errors.Add(message + "\n" + stack);
                }
            }
            private void SetPhase(string phase) { if (_phase != phase) { _phase = phase; ++_revision; } }
            private void Add(string receipt) { if (_receipts.Count >= 1024) throw new InvalidOperationException("Organic receipt bound reached."); _receipts.Add(receipt); ++_revision; }

            // Main-thread, read-only evidence. Identity changes produce one receipt;
            // changing label text on the same modal never triggers repeated captures.
            private void ObserveBlockingModal()
            {
                UIComponent modal = null; string topReadError = null;
                try { modal = UIView.GetModalComponent(); }
                catch (Exception error) { topReadError = BoundedModalText(error.GetType().Name + ": " + error.Message, 512); }
                if (_observedModalStack && ReferenceEquals(modal, _lastObservedModal)) return;
                _observedModalStack = true; _lastObservedModal = modal;
                bool record = _modalDiagnosticCount < 16;
                if (record) ++_modalDiagnosticCount;
                else if (!_modalDiagnosticsCapped) { _modalDiagnosticsCapped = true; ++_revision; }
                try
                {
                    if (topReadError != null)
                    {
                        _owner._blockingModal = "Modal stack is nonempty; top diagnostic read unavailable.";
                        if (record) Add("{\"phase\":\"NativeModalWaitObservationUnavailable\",\"readOnly\":true,\"error\":" + Json(topReadError) + "}");
                        return;
                    }
                    string name = modal == null ? null : BoundedModalText(modal.name, 128);
                    string type = modal == null ? null : BoundedModalText(modal.GetType().FullName, 256);
                    _owner._blockingModal = modal == null ? "Modal stack is nonempty; top component unavailable."
                        : name + " (" + type + ")";
                    if (!record) return;
                    List<string> path = new List<string>(), labels = new List<string>();
                    UIComponent ancestor = modal; int pathDepth = 0;
                    while (ancestor != null && pathDepth++ < 24)
                    {
                        path.Add(BoundedModalText(ancestor.name, 96)); ancestor = ancestor.parent;
                    }
                    path.Reverse();
                    Queue<UIComponent> pending = new Queue<UIComponent>(); HashSet<UIComponent> visited = new HashSet<UIComponent>();
                    if (modal != null) pending.Enqueue(modal);
                    int enqueued = pending.Count, inspected = 0, textBudget = 2048;
                    while (pending.Count != 0 && inspected < 128 && labels.Count < 8 && textBudget > 0)
                    {
                        UIComponent component = pending.Dequeue();
                        if (component == null || !visited.Add(component)) continue;
                        ++inspected;
                        // Textual labels and button captions only; no editable fields.
                        UILabel label = component as UILabel; UIButton button = component as UIButton;
                        string value = label != null ? label.text : button != null ? button.text : null;
                        if (!string.IsNullOrEmpty(value) && component.isVisible)
                        {
                            string text = BoundedModalText(value, Math.Min(512, textBudget)); textBudget -= text.Length;
                            labels.Add("{\"name\":" + Json(BoundedModalText(component.name, 128)) + ",\"type\":" + Json(BoundedModalText(component.GetType().FullName, 256))
                                + ",\"text\":" + Json(text) + ",\"textTruncated\":" + Bool(value.Length > text.Length) + "}");
                        }
                        IList<UIComponent> children = component.components;
                        for (int i = 0; i < children.Count && enqueued < 128; ++i) { pending.Enqueue(children[i]); ++enqueued; }
                    }
                    Add("{\"phase\":\"NativeModalWaitObservation\",\"observation\":" + _modalDiagnosticCount + ",\"source\":\"UIView.GetModalComponent\",\"readOnly\":true"
                        + ",\"name\":" + Json(name) + ",\"type\":" + Json(type) + ",\"instanceId\":" + (modal == null ? "null" : modal.GetInstanceID().ToString(CultureInfo.InvariantCulture))
                        + ",\"visible\":" + Bool(modal != null && modal.isVisible) + ",\"ancestorNames\":[" + JsonList(path) + "],\"pathTruncated\":" + Bool(ancestor != null)
                        + ",\"componentsInspected\":" + inspected + ",\"scanMayBeTruncated\":" + Bool(enqueued >= 128 || pending.Count != 0 || labels.Count >= 8 || textBudget == 0)
                        + ",\"labels\":[" + string.Join(",", labels.ToArray()) + "],\"modalDismissed\":false}");
                }
                catch (Exception error)
                {
                    // A diagnostic read cannot turn waiting into a click or interfere
                    // with the original timeout/cleanup and native modal ownership.
                    if (record)
                    {
                        Add("{\"phase\":\"NativeModalWaitObservationUnavailable\",\"readOnly\":true,\"error\":" + Json(BoundedModalText(error.GetType().Name + ": " + error.Message, 512)) + "}");
                    }
                }
            }
            private static string BoundedModalText(string value, int maximum)
            {
                if (value == null || value.Length <= maximum) return value;
                int length = maximum;
                if (length > 0 && char.IsHighSurrogate(value[length - 1])) --length;
                return value.Substring(0, length);
            }

            private static bool Vanilla(PrefabInfo info)
            {
                return info != null && !info.m_isCustomContent && info.m_requiredExpansion == default(SteamHelper.ExpansionBitMask)
                    && info.m_requiredModderPack == default(SteamHelper.ModderPackBitMask);
            }
            private void InventoryAndPlan()
            {
                SetPhase("InventoryAndGeometryBeforeInfrastructure");
                _initialCash = Singleton<EconomyManager>.instance.InternalCashAmount;
                var demand = ModRuntime.CurrentDemand;
                ResidentialStockSnapshot initialStock = demand == null || demand.Census == null ? null : demand.Census.ResidentialStock;
                Add("{\"phase\":\"PublishedInitialResidentialStockBeforePlan\",\"status\":" + Json(initialStock == null ? "Unknown" : "ObservedPublishedSnapshot")
                    + ",\"censusFrame\":" + (demand == null || demand.Census == null ? "null" : demand.Census.Frame.ToString(CultureInfo.InvariantCulture))
                    + ",\"residentialStock\":" + StockJson(initialStock) + ",\"populatedCoverageVerified\":false,\"organicGrowthVerified\":false}");
                if (demand == null || demand.Census == null || demand.Census.HomeHouseholds != 0 || demand.Census.OccupiedHomeHouseholds != 0)
                    throw new InvalidOperationException("Organic baseline must have no existing HOME household stock.");
                Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
                for (int i = 1; i < buildings.Length; ++i)
                    if ((buildings[i].m_flags & (Building.Flags.Created | Building.Flags.Deleted)) == Building.Flags.Created
                        && buildings[i].Info != null && buildings[i].Info.m_buildingAI is ResidentialBuildingAI)
                        throw new InvalidOperationException("Organic baseline already contains a residential building.");
                NetManager net = Singleton<NetManager>.instance;
                for (ushort i = 1; i != 0; ++i)
                {
                    if (i < net.m_nodes.m_buffer.Length && (net.m_nodes.m_buffer[i].m_flags & NetNode.Flags.Created) != 0) _baselineNodes.Add(i);
                    if (i < net.m_segments.m_buffer.Length && (net.m_segments.m_buffer[i].m_flags & NetSegment.Flags.Created) != 0) _baselineSegments.Add(i);
                }
                List<ushort> stubs = new List<ushort>();
                List<string> candidates = new List<string>();
                foreach (ushort id in _baselineNodes)
                {
                    NetNode node = net.m_nodes.m_buffer[id];
                    if ((node.m_flags & NetNode.Flags.Outside) != 0 || node.CountSegments() != 1
                        || node.Info == null || !(node.Info.m_netAI is RoadBaseAI)
                        || Singleton<GameAreaManager>.instance.PointOutOfArea(node.m_position, 24f)) continue;
                    ushort outside = FindOutside(id);
                    if (outside == 0) continue;
                    stubs.Add(id);
                    candidates.Add("{\"node\":" + id + ",\"position\":" + PositionJson(node.m_position) + ",\"flags\":" + Json(node.m_flags.ToString())
                        + ",\"outsideNodeByExistingRoadGraph\":" + outside + "}");
                }
                _road = PrefabCollection<NetInfo>.FindLoaded("Basic Road");
                _pipe = PrefabCollection<NetInfo>.FindLoaded("Water Pipe");
                _power = PrefabCollection<NetInfo>.FindLoaded("Power Line");
                _wind = PrefabCollection<BuildingInfo>.FindLoaded("Wind Turbine");
                _tower = PrefabCollection<BuildingInfo>.FindLoaded("Water Tower");
                _drain = PrefabCollection<BuildingInfo>.FindLoaded("Water Outlet");
                Add("{\"phase\":\"Inventory\",\"frame\":" + _simulation.m_currentFrameIndex + ",\"cashInternal\":" + _initialCash
                    + ",\"roadStubs\":[" + string.Join(",", candidates.ToArray()) + "],\"prefabs\":" + PrefabInventoryJson() + "}");
                if (!Vanilla(_road) || !(_road.m_netAI is RoadAI) || !((RoadAI)_road.m_netAI).m_enableZoning
                    || !Vanilla(_pipe) || !(_pipe.m_netAI is WaterPipeAI) || !Vanilla(_power)
                    || !Vanilla(_wind) || !(_wind.m_buildingAI is WindTurbineAI) || !Vanilla(_tower) || !(_tower.m_buildingAI is WaterFacilityAI)
                    || !Vanilla(_drain) || !(_drain.m_buildingAI is WaterFacilityAI))
                    throw new InvalidOperationException("Required exact vanilla infrastructure prefabs are missing/unsupported; inspect Inventory receipt.");
                foreach (NetInfo info in new NetInfo[] { _road, _pipe, _power })
                    if (!ToolsModifierControl.IsUnlocked(info.m_UnlockMilestone)) throw new InvalidOperationException("Native infrastructure milestone remains locked: " + info.name);
                foreach (BuildingInfo info in new BuildingInfo[] { _wind, _tower, _drain })
                    if (!ToolsModifierControl.IsUnlocked(info.m_UnlockMilestone)) throw new InvalidOperationException("Native infrastructure milestone remains locked: " + info.name);
                stubs.Sort(delegate(ushort a, ushort b) { return net.m_nodes.m_buffer[a].m_position.sqrMagnitude.CompareTo(net.m_nodes.m_buffer[b].m_position.sqrMagnitude); });
                for (int a = 0; a < stubs.Count && _stubA == 0; ++a) for (int b = a + 1; b < stubs.Count; ++b)
                {
                    NetNode one = net.m_nodes.m_buffer[stubs[a]], two = net.m_nodes.m_buffer[stubs[b]];
                    bool opposed = ((one.m_flags & NetNode.Flags.OneWayIn) != 0 && (two.m_flags & NetNode.Flags.OneWayOut) != 0)
                        || ((one.m_flags & NetNode.Flags.OneWayOut) != 0 && (two.m_flags & NetNode.Flags.OneWayIn) != 0);
                    if (!opposed || Vector3.Distance(one.m_position, two.m_position) > 160f) continue;
                    Vector3 f = StubForward(stubs[a]) + StubForward(stubs[b]); f.y = 0f;
                    if (f.sqrMagnitude < 1f) continue;
                    _forward = f.normalized; _side = new Vector3(_forward.z, 0f, -_forward.x);
                    _head = Terrain((one.m_position + two.m_position) * 0.5f + _forward * 128f);
                    _end = Terrain(_head + _forward * 256f);
                    if (Singleton<GameAreaManager>.instance.PointOutOfArea(_head, 160f) || Singleton<GameAreaManager>.instance.PointOutOfArea(_end, 96f)) continue;
                    _stubA = stubs[a]; _stubB = stubs[b]; break;
                }
                if (_stubA == 0) throw new InvalidOperationException("No bounded pair of opposite one-way road stubs with real outside graph connections was found.");
                RecordGeometryInventory(net);
                _towerSite = PreflightBuilding(_tower, _head + _side * 64f + _forward * 48f, 0f, false);
                ValidateSiteAgainstPlans(_towerSite, new Placement[0], null);
                int initialWindAttempts = 0; List<string> initialWindRejections = new List<string>();
                // Keep the tower near the residential street. Search a small owned
                // landward band for Wind1 rather than reusing the crowded old site.
                foreach (float roadOffset in new float[] { 160f, 136f, 184f })
                {
                    foreach (float forwardOffset in new float[] { 72f, 40f, 104f })
                    {
                        ++initialWindAttempts;
                        try
                        {
                            Placement wind = PreflightBuilding(_wind, _head + _side * roadOffset + _forward * forwardOffset, 0f, false);
                            ValidateSiteAgainstPlans(wind, new Placement[] { _towerSite }, null);
                            _windSite = wind; break;
                        }
                        catch (InvalidOperationException error)
                        {
                            if (initialWindRejections.Count < 4) initialWindRejections.Add("side " + JsonFloat(roadOffset) + ", forward " + JsonFloat(forwardOffset) + ": " + error.Message);
                        }
                    }
                    if (_windSite != null) break;
                }
                Add("{\"phase\":\"InitialServiceSitePlan\",\"attempts\":" + initialWindAttempts + ",\"maximumWindCandidates\":9,\"accepted\":" + Bool(_windSite != null)
                    + ",\"tower\":" + PlacementJson(_towerSite) + ",\"wind\":" + (_windSite == null ? "null" : PlacementJson(_windSite))
                    + ",\"rejectionExamples\":[" + JsonList(initialWindRejections) + "]}");
                if (_windSite == null) throw new InvalidOperationException("No mutually clear native initial wind site in the bounded street-local search; no infrastructure spent.");
                Add("{\"phase\":\"LocalServicesPreflightAccepted\",\"wind\":" + PlacementJson(_windSite) + ",\"tower\":" + PlacementJson(_towerSite) + "}");
                Dictionary<string, int> shoreReasons = new Dictionary<string, int>(StringComparer.Ordinal);
                List<string> shoreExamples = new List<string>(); int shoreAttempts = 0, shoreFound = 0;
                int tileX, tileZ; float minX, minZ, maxX, maxZ;
                GameAreaManager area = Singleton<GameAreaManager>.instance;
                area.GetStartTile(out tileX, out tileZ); area.GetAreaBounds(tileX, tileZ, out minX, out minZ, out maxX, out maxZ);
                float maximumHeadDistance = 0f;
                foreach (Vector3 corner in new Vector3[] { new Vector3(minX, 0f, minZ), new Vector3(minX, 0f, maxZ), new Vector3(maxX, 0f, minZ), new Vector3(maxX, 0f, maxZ) })
                    maximumHeadDistance = Math.Max(maximumHeadDistance, HorizontalDistance(corner, _head));
                int stepsX = Mathf.CeilToInt((maxX - minX - 64f) / 96f), stepsZ = Mathf.CeilToInt((maxZ - minZ - 64f) / 96f);
                if (stepsX < 1 || stepsZ < 1 || (stepsX + 1) * (stepsZ + 1) > 441) throw new InvalidOperationException("Start-tile shoreline grid exceeds the bounded query contract.");
                List<ShoreCandidate> candidatesByDistance = new List<ShoreCandidate>(); HashSet<string> seenShores = new HashSet<string>();
                for (int z = 0; z <= stepsZ; ++z) for (int x = 0; x <= stepsX; ++x)
                {
                    ++shoreAttempts;
                    Vector3 query = new Vector3(Mathf.Lerp(minX + 32f, maxX - 32f, x / (float)stepsX), 0f, Mathf.Lerp(minZ + 32f, maxZ - 32f, z / (float)stepsZ));
                    Vector3 shore, direction; float water;
                    if (!Singleton<TerrainManager>.instance.GetShorePos(query, 64f, out shore, out direction, out water))
                    { RecordShoreReason(shoreReasons, shoreExamples, "NoShoreWithin64m", query, null); continue; }
                    ++shoreFound;
                    float distance = HorizontalDistance(shore, _head);
                    if (area.PointOutOfArea(shore) || shore.x < minX || shore.x > maxX || shore.z < minZ || shore.z > maxZ)
                    { RecordShoreReason(shoreReasons, shoreExamples, "NativeShoreOutsideOwnedStartTile", query, PositionJson(shore)); continue; }
                    if (distance > maximumHeadDistance || distance < 160f)
                    { RecordShoreReason(shoreReasons, shoreExamples, "Outside160mToTileCornerDistanceGuard", query, PositionJson(shore)); continue; }
                    string shoreKey = FloatBits(shore.x) + ":" + FloatBits(shore.z) + ":" + FloatBits(direction.x) + ":" + FloatBits(direction.z);
                    if (!seenShores.Add(shoreKey)) { RecordShoreReason(shoreReasons, shoreExamples, "DuplicateNativeShore", query, PositionJson(shore)); continue; }
                    candidatesByDistance.Add(new ShoreCandidate { Query = query, Shore = shore, Direction = direction, Water = water, Distance = distance });
                }
                candidatesByDistance.Sort(delegate(ShoreCandidate a, ShoreCandidate b)
                {
                    int comparison = a.Distance.CompareTo(b.Distance);
                    if (comparison != 0) return comparison;
                    comparison = a.Shore.x.CompareTo(b.Shore.x); return comparison != 0 ? comparison : a.Shore.z.CompareTo(b.Shore.z);
                });
                foreach (ShoreCandidate candidate in candidatesByDistance)
                {
                    Vector3 query = candidate.Query, shore = candidate.Shore, direction = candidate.Direction; float water = candidate.Water;
                    string nativeShore = "{\"query\":" + PositionJson(query) + ",\"nativeShore\":" + PositionJson(shore) + ",\"direction\":" + PositionJson(direction)
                        + ",\"shoreWaterHeight\":" + JsonFloat(water) + ",\"horizontalDistanceFromHead\":" + JsonFloat(candidate.Distance) + "}";
                    string stage = "OutletPreflight";
                    try
                    {
                        Placement drain = PreflightBuilding(_drain, shore, Mathf.Atan2(direction.x, -direction.z), true);
                        ValidateSiteAgainstPlans(drain, new Placement[] { _windSite, _towerSite }, null);
                        stage = "NearbyWindPreflight";
                        Placement wind = null;
                        // Anchor to the FINAL native outlet placement, not the initial
                        // shoreline query which resnap/offset can move toward the wind.
                        Vector3 land = new Vector3(-Mathf.Sin(drain.Angle), 0f, Mathf.Cos(drain.Angle));
                        Vector3 tangent = new Vector3(land.z, 0f, -land.x);
                        foreach (float distance in new float[] { 96f, 128f, 160f })
                        {
                            foreach (float along in new float[] { 0f, -48f, 48f })
                            {
                                try
                                {
                                    Placement candidateWind = PreflightBuilding(_wind, drain.Position + land * distance + tangent * along, 0f, false);
                                    ValidateSiteAgainstPlans(candidateWind, new Placement[] { _windSite, _towerSite, drain }, null);
                                    wind = candidateWind; break;
                                }
                                catch (InvalidOperationException error)
                                {
                                    RecordShoreReason(shoreReasons, shoreExamples, "NearbyWindCandidate:" + error.Message, query,
                                        "{\"shore\":" + nativeShore + ",\"outlet\":" + PlacementJson(drain)
                                        + ",\"landDistance\":" + JsonFloat(distance) + ",\"tangentOffset\":" + JsonFloat(along)
                                        + ",\"placement\":" + (_lastPlacementDiagnostic ?? "null") + "}");
                                }
                            }
                            if (wind != null) break;
                        }
                        if (wind == null) throw new InvalidOperationException("No mutually clear native wind site in the bounded outlet-local search.");
                        _drainSite = drain; _outletWindSite = wind; break;
                    }
                    catch (InvalidOperationException error)
                    {
                        RecordShoreReason(shoreReasons, shoreExamples, stage + ":" + error.Message, query,
                            "{\"shore\":" + nativeShore + ",\"placement\":" + (_lastPlacementDiagnostic ?? "null") + "}");
                    }
                }
                List<string> reasonCounts = new List<string>();
                foreach (KeyValuePair<string, int> reason in shoreReasons) reasonCounts.Add("{\"reason\":" + Json(reason.Key) + ",\"count\":" + reason.Value + "}");
                Add("{\"phase\":\"BoundedShorelineSearch\",\"attempts\":" + shoreAttempts + ",\"nativeShoreFound\":" + shoreFound
                    + ",\"uniqueOwnedCandidates\":" + candidatesByDistance.Count + ",\"accepted\":" + Bool(_drainSite != null)
                    + ",\"queryGridSpacingX\":" + JsonFloat((maxX - minX - 64f) / stepsX) + ",\"queryGridSpacingZ\":" + JsonFloat((maxZ - minZ - 64f) / stepsZ)
                    + ",\"queryRadius\":64,\"minimumHorizontalHeadDistance\":160,\"maximumHorizontalHeadDistance\":" + JsonFloat(maximumHeadDistance)
                    + ",\"distanceLimitSource\":\"FarthestStartTileCorner\",\"candidateOrder\":\"HorizontalDistanceThenXThenZ\""
                    + ",\"reasonCounts\":[" + string.Join(",", reasonCounts.ToArray()) + "],\"examples\":[" + string.Join(",", shoreExamples.ToArray()) + "]}");
                if (_drainSite == null) throw new InvalidOperationException("No valid owned shoreline outlet in the bounded start-tile search; no infrastructure spent.");
                ValidateAllServicePlans();
                int roadCost = TestNet(_road, _stubA, net.m_nodes.m_buffer[_stubA].m_position, 0, _head)
                    + TestNet(_road, _stubB, net.m_nodes.m_buffer[_stubB].m_position, 0, _head) + TestNet(_road, 0, _head, 0, _end);
                Vector3 pipeHead = NativeGroundPipePosition(_head), pipeEnd = NativeGroundPipePosition(_end);
                PipePort towerPort = PlannedPipePort(_towerSite), drainPort = PlannedPipePort(_drainSite);
                ValidatePlannedPipeJunction(pipeHead, pipeEnd, towerPort.Position, drainPort.Position);
                int utilitiesCost = TestNet(_pipe, 0, pipeHead, 0, pipeEnd)
                    + TestNet(_pipe, 0, towerPort.Position, 0, pipeHead, towerPort.Elevation) + TestNet(_pipe, 0, drainPort.Position, 0, pipeHead, drainPort.Elevation);
                _localPower = PlanLocalPowerLink("LocalWindToTower", _windSite, _towerSite);
                _shorePower = PlanLocalPowerLink("ShoreWindToOutlet", _outletWindSite, _drainSite);
                utilitiesCost += _localPower.Cost + _shorePower.Cost;
                foreach (Placement site in new Placement[] { _windSite, _towerSite, _drainSite, _outletWindSite }) CheckServiceReservation(site.Info);
                long fixedCost = (long)_windSite.Cost + _towerSite.Cost + _drainSite.Cost + _outletWindSite.Cost;
                Add("{\"phase\":\"GeometryPlan\",\"stubA\":" + _stubA + ",\"stubB\":" + _stubB + ",\"head\":" + PositionJson(_head)
                    + ",\"end\":" + PositionJson(_end) + ",\"roadEstimate\":" + roadCost + ",\"utilitiesEstimate\":" + utilitiesCost + ",\"fixedServiceEstimate\":" + fixedCost
                    + ",\"serviceSites\":[" + PlacementJson(_windSite) + "," + PlacementJson(_towerSite) + "," + PlacementJson(_drainSite) + "," + PlacementJson(_outletWindSite) + "]}");
                if (roadCost < 0 || fixedCost < 0 || utilitiesCost < 0 || roadCost + fixedCost + utilitiesCost > Math.Min(_initialCash, MaximumConstructionDebit))
                    throw new InvalidOperationException("Native construction estimate exceeds the private footprint/budget bound.");
                _state = 1; SetPhase("InventoryAndPlanReadyBeforeSpend");
            }

            private Placement[] AllServiceSites() { return new Placement[] { _windSite, _towerSite, _drainSite, _outletWindSite }; }
            private static Quad2 ServiceFootprint(Placement site, float margin, bool reserve)
            {
                // BuildingTool.CheckSpaceImpl / Building.OverlapQuad axes and cells.
                // Reservation uses the full rectangle even for circular prefabs.
                float width = site.Info.m_cellWidth * 4f, length = site.Info.m_cellLength * 4f;
                if (!reserve && (site.Info.m_placementMode == BuildingInfo.PlacementMode.Roadside || site.Info.m_placementMode == BuildingInfo.PlacementMode.PathsideOrGround))
                { width -= 0.8f; length -= 0.8f; }
                if (!reserve && site.Info.m_circular) { width *= 0.7f; length *= 0.7f; }
                Vector2 across = new Vector2(Mathf.Cos(site.Angle), Mathf.Sin(site.Angle)) * (width + margin);
                Vector2 along = new Vector2(across.y, -across.x).normalized * (length + margin);
                Vector2 center = new Vector2(site.Position.x, site.Position.z);
                return new Quad2 { a = center - across - along, b = center + across - along, c = center + across + along, d = center - across + along };
            }
            private static Quad2 NetworkCorridor(Vector3 a, Vector3 b, float halfWidth)
            {
                Vector2 direction = new Vector2(b.x - a.x, b.z - a.z);
                if (direction.sqrMagnitude < 0.01f) throw new InvalidOperationException("Planned network corridor has no length.");
                Vector2 normal = new Vector2(direction.y, -direction.x).normalized * halfWidth;
                Vector2 start = new Vector2(a.x, a.z), end = new Vector2(b.x, b.z);
                return new Quad2 { a = start - normal, b = end - normal, c = end + normal, d = start + normal };
            }
            private Quad2[] PlannedRoadCorridors()
            {
                NetManager net = Singleton<NetManager>.instance; float halfWidth = _road.m_halfWidth + 4f;
                return new Quad2[] { NetworkCorridor(net.m_nodes.m_buffer[_stubA].m_position, _head, halfWidth),
                    NetworkCorridor(net.m_nodes.m_buffer[_stubB].m_position, _head, halfWidth), NetworkCorridor(_head, _end, halfWidth) };
            }
            private void ValidateSiteAgainstPlans(Placement site, Placement[] others, Placement excluded)
            {
                Quad2 reserved = ServiceFootprint(site, 8f, true);
                foreach (Placement other in others)
                    if (other != null && !ReferenceEquals(other, excluded) && reserved.Intersect(ServiceFootprint(other, 8f, true)))
                        throw new InvalidOperationException("Planned service footprints overlap with 8m margins: " + site.Info.name + " / " + other.Info.name);
                foreach (Quad2 road in PlannedRoadCorridors())
                    if (reserved.Intersect(road)) throw new InvalidOperationException("Planned service footprint overlaps the reserved road corridor: " + site.Info.name);
            }
            private void ValidateAllServicePlans()
            {
                Placement[] sites = AllServiceSites();
                foreach (Placement site in sites) ValidateSiteAgainstPlans(site, sites, site);
                Add("{\"phase\":\"MutualInfrastructurePlanChecked\",\"serviceMarginMetres\":8,\"roadCorridorExtraHalfWidthMetres\":4"
                    + ",\"verticalSeparationAssumed\":false,\"sites\":[" + PlacementJson(sites[0]) + "," + PlacementJson(sites[1]) + "," + PlacementJson(sites[2]) + "," + PlacementJson(sites[3]) + "]}");
            }
            private bool PowerCorridorClear(Vector3 a, Vector3 b)
            {
                Quad2 corridor = NetworkCorridor(a, b, _power.m_halfWidth + 4f);
                foreach (Placement site in AllServiceSites()) if (corridor.Intersect(ServiceFootprint(site, 8f, true))) return false;
                foreach (Quad2 road in PlannedRoadCorridors()) if (corridor.Intersect(road)) return false;
                return true;
            }
            private LinePlan PlanLocalPowerLink(string name, Placement source, Placement target)
            {
                Vector3 direction = target.Position - source.Position; direction.y = 0f; direction.Normalize();
                Vector3 normal = new Vector3(direction.z, 0f, -direction.x); List<string> rejected = new List<string>(); int attempts = 0;
                foreach (float offset in new float[] { 0f, 32f, -32f, 48f, -48f, 64f, -64f, 80f, -80f, 96f, -96f })
                {
                    ++attempts; Vector3 a = source.Position + normal * offset, b = target.Position + normal * offset;
                    a.y = NetSegment.SampleTerrainHeight(_power, a, false, 0f); b.y = NetSegment.SampleTerrainHeight(_power, b, false, 0f);
                    if (!PowerCorridorClear(a, b)) { if (rejected.Count < 4) rejected.Add("offset " + JsonFloat(offset) + ": reserved footprint/corridor overlap"); continue; }
                    try
                    {
                        int cost = TestNet(_power, 0, a, 0, b);
                        Add("{\"phase\":\"LocalPowerCorridorPlan\",\"name\":" + Json(name) + ",\"attempts\":" + attempts + ",\"offsetMetres\":" + JsonFloat(offset)
                            + ",\"from\":" + PositionJson(a) + ",\"to\":" + PositionJson(b) + ",\"costInternal\":" + cost
                            + ",\"nativePreflightAccepted\":true,\"serviceCoverageAsserted\":false,\"rejectionExamples\":[" + JsonList(rejected) + "]}");
                        return new LinePlan { A = a, B = b, Cost = cost };
                    }
                    catch (InvalidOperationException error) { if (rejected.Count < 4) rejected.Add("offset " + JsonFloat(offset) + ": " + error.Message); }
                }
                Add("{\"phase\":\"LocalPowerCorridorPlan\",\"name\":" + Json(name) + ",\"attempts\":" + attempts + ",\"nativePreflightAccepted\":false,\"rejectionExamples\":[" + JsonList(rejected) + "]}");
                throw new InvalidOperationException("No bounded mutually clear native power corridor for " + name);
            }

            private void RecordGeometryInventory(NetManager net)
            {
                GameAreaManager area = Singleton<GameAreaManager>.instance; TerrainManager terrain = Singleton<TerrainManager>.instance;
                int tileX, tileZ; float minX, minZ, maxX, maxZ;
                area.GetStartTile(out tileX, out tileZ); area.GetAreaBounds(tileX, tileZ, out minX, out minZ, out maxX, out maxZ);
                List<string> samples = new List<string>();
                for (int z = 0; z < 9; ++z) for (int x = 0; x < 9; ++x)
                {
                    Vector3 point = new Vector3(Mathf.Lerp(minX + 32f, maxX - 32f, x / 8f), 0f, Mathf.Lerp(minZ + 32f, maxZ - 32f, z / 8f));
                    float ground = terrain.SampleRawHeightSmooth(point), water = terrain.WaterLevel(new Vector2(point.x, point.z));
                    samples.Add("{\"x\":" + JsonFloat(point.x) + ",\"z\":" + JsonFloat(point.z) + ",\"terrainHeight\":" + JsonFloat(ground)
                        + ",\"waterLevel\":" + JsonFloat(water) + ",\"waterMinusTerrain\":" + JsonFloat(water - ground)
                        + ",\"outsideOwnedArea\":" + Bool(area.PointOutOfArea(point)) + "}");
                }
                Add("{\"phase\":\"GeometryInventoryBeforeServicePreflight\",\"startTileX\":" + tileX + ",\"startTileZ\":" + tileZ
                    + ",\"startTileBounds\":{\"minX\":" + JsonFloat(minX) + ",\"minZ\":" + JsonFloat(minZ) + ",\"maxX\":" + JsonFloat(maxX) + ",\"maxZ\":" + JsonFloat(maxZ) + "}"
                    + ",\"stubA\":" + _stubA + ",\"stubB\":" + _stubB + ",\"head\":" + PositionJson(_head) + ",\"end\":" + PositionJson(_end)
                    + ",\"forward\":" + PositionJson(_forward) + ",\"side\":" + PositionJson(_side)
                    + ",\"plannedRoads\":[{\"from\":" + PositionJson(net.m_nodes.m_buffer[_stubA].m_position) + ",\"to\":" + PositionJson(_head)
                    + "},{\"from\":" + PositionJson(net.m_nodes.m_buffer[_stubB].m_position) + ",\"to\":" + PositionJson(_head)
                    + "},{\"from\":" + PositionJson(_head) + ",\"to\":" + PositionJson(_end) + "}],\"startTileTerrainWaterSamples\":[" + string.Join(",", samples.ToArray()) + "]}");
            }
            private static void RecordShoreReason(Dictionary<string, int> counts, List<string> examples, string reason, Vector3 query, string detail)
            {
                int count; counts.TryGetValue(reason, out count); counts[reason] = count + 1;
                // At most two examples per reason and twelve overall, not 81 exception stacks.
                if (count < 2 && examples.Count < 12) examples.Add("{\"reason\":" + Json(reason) + ",\"query\":" + PositionJson(query) + ",\"detail\":" + (detail ?? "null") + "}");
            }

            private string PrefabInventoryJson()
            {
                List<string> values = new List<string>();
                for (uint i = 0; i < PrefabCollection<BuildingInfo>.LoadedCount() && values.Count < 48; ++i)
                {
                    BuildingInfo info = PrefabCollection<BuildingInfo>.GetLoaded(i);
                    if (Vanilla(info) && (info.m_buildingAI is WindTurbineAI || info.m_buildingAI is WaterFacilityAI))
                        values.Add("{\"name\":" + Json(info.name) + ",\"ai\":" + Json(info.m_buildingAI.GetType().Name) + "}");
                }
                return "[" + string.Join(",", values.ToArray()) + "]";
            }
            private ushort FindOutside(ushort start)
            {
                NetManager net = Singleton<NetManager>.instance;
                HashSet<ushort> seen = new HashSet<ushort>(); Queue<ushort> queue = new Queue<ushort>(); queue.Enqueue(start); seen.Add(start);
                while (queue.Count != 0 && seen.Count <= 65535)
                {
                    ushort id = queue.Dequeue(); NetNode node = net.m_nodes.m_buffer[id];
                    if ((node.m_flags & NetNode.Flags.Outside) != 0) return id;
                    for (int i = 0; i < 8; ++i)
                    {
                        ushort segmentId = node.GetSegment(i); if (segmentId == 0) continue;
                        NetSegment segment = net.m_segments.m_buffer[segmentId];
                        if ((segment.m_flags & (NetSegment.Flags.Created | NetSegment.Flags.Deleted)) != NetSegment.Flags.Created
                            || segment.Info == null || !(segment.Info.m_netAI is RoadBaseAI)) continue;
                        ushort next = segment.m_startNode == id ? segment.m_endNode : segment.m_startNode;
                        if (next != 0 && seen.Add(next)) queue.Enqueue(next);
                    }
                }
                return 0;
            }
            private static Vector3 StubForward(ushort id)
            {
                NetManager net = Singleton<NetManager>.instance; NetNode node = net.m_nodes.m_buffer[id]; NetSegment segment = net.m_segments.m_buffer[node.GetSegment(0)];
                return -(segment.m_startNode == id ? segment.m_startDirection : segment.m_endDirection);
            }
            private static Vector3 Terrain(Vector3 point) { point.y = Singleton<TerrainManager>.instance.SampleRawHeightSmooth(point); return point; }
            private static float HorizontalDistance(Vector3 a, Vector3 b) { float x = a.x - b.x, z = a.z - b.z; return Mathf.Sqrt(x * x + z * z); }

            private Placement PreflightBuilding(BuildingInfo info, Vector3 raw, float angle, bool shoreline)
            {
                Guard();
                _lastPlacementDiagnostic = "{\"prefab\":" + Json(info.name) + ",\"nativePlacementMode\":" + Json(info.m_placementMode.ToString())
                    + ",\"rawPosition\":" + PositionJson(raw) + ",\"angle\":" + JsonFloat(angle) + ",\"shorelineRequested\":" + Bool(shoreline) + "}";
                if (info.m_buildingAI is ResidentialBuildingAI || !(info.m_buildingAI is PlayerBuildingAI) || info.m_subBuildings != null && info.m_subBuildings.Length != 0
                    || info.m_buildingAI.WorksAsNet()) throw new InvalidOperationException("Unsupported service infrastructure prefab.");
                if (!shoreline && info.m_placementMode != BuildingInfo.PlacementMode.OnGround) throw new InvalidOperationException("Service is not a native OnGround placement.");
                bool supportedShoreMode = info.m_placementMode == BuildingInfo.PlacementMode.Shoreline
                    || info == _drain && info.name == "Water Outlet" && info.m_buildingAI is WaterFacilityAI && info.m_placementMode == BuildingInfo.PlacementMode.ShorelineOrGround;
                if (shoreline && !supportedShoreMode) throw new InvalidOperationException("Outlet does not support the verified native terrain-shore branch.");
                Vector3 point = Terrain(raw); float water;
                if (shoreline)
                {
                    // BuildingTool's terrain-shore branch only. It explicitly supports
                    // ShorelineOrGround, but this probe never falls back to ground/canal.
                    Vector3 snapPosition, snapDirection; bool isQuay;
                    if (BuildingTool.SnapToCanal(raw, out snapPosition, out snapDirection, out isQuay, 40f, false))
                        throw new InvalidOperationException("Canal/quay snap is outside the supported terrain-shore fixture.");
                    Vector3 firstShore, shoreDirection;
                    if (!Singleton<TerrainManager>.instance.GetShorePos(snapPosition, 50f, out firstShore, out shoreDirection, out water)
                        || !Singleton<TerrainManager>.instance.GetShorePos(firstShore, 50f, out point, out shoreDirection, out water))
                        throw new InvalidOperationException("Native terrain-shore resnap did not find a shore.");
                    point += shoreDirection.normalized * info.m_placementOffset;
                    angle = Mathf.Atan2(shoreDirection.x, -shoreDirection.z);
                }
                else water = Singleton<TerrainManager>.instance.WaterLevel(new Vector2(point.x, point.z));
                if (Singleton<GameAreaManager>.instance.PointOutOfArea(point, Math.Max(info.m_cellWidth, info.m_cellLength) * 4f + 16f))
                    throw new InvalidOperationException("Service footprint crosses the owned tile.");
                float shoreY = point.y;
                float min, max, height; Building.SampleBuildingHeight(point, angle, info.m_cellWidth, info.m_cellLength, info, out min, out max, out height);
                if (shoreline) { min = Math.Min(water, min); height = Math.Max(shoreY, height); }
                point.y = height;
                Segment3 connection = default(Segment3); int rate, cost;
                ToolBase.ToolErrors aiErrors = info.m_buildingAI.CheckBuildPosition(0, ref point, ref angle, water, 0f, ref connection, out rate, out cost);
                BuildingInfo.PlacementMode effectiveMode = shoreline ? BuildingInfo.PlacementMode.Shoreline : BuildingInfo.PlacementMode.OnGround;
                ToolBase.ToolErrors spaceErrors = BuildingTool.CheckSpace(info, effectiveMode, 0, point, min,
                    height + info.m_collisionHeight, angle, info.m_cellWidth, info.m_cellLength, true,
                    new ulong[(Singleton<NetManager>.instance.m_segments.m_buffer.Length + 63) / 64],
                    new ulong[(Singleton<BuildingManager>.instance.m_buildings.m_buffer.Length + 63) / 64]);
                ToolBase.ToolErrors errors = aiErrors | spaceErrors;
                if (shoreline && shoreY - water > 128f) errors |= ToolBase.ToolErrors.HeightTooHigh;
                if (shoreline && height <= water)
                    errors = (errors & ~(ToolBase.ToolErrors.HeightTooHigh | ToolBase.ToolErrors.CannotConnect | ToolBase.ToolErrors.CannotBuildOnWater)) | ToolBase.ToolErrors.ShoreNotFound;
                if (!shoreline && max - min > info.m_maxHeightOffset) errors |= ToolBase.ToolErrors.SlopeTooSteep;
                if (Singleton<GameAreaManager>.instance.PointOutOfArea(point, Math.Max(info.m_cellWidth, info.m_cellLength) * 4f + 16f)) errors |= ToolBase.ToolErrors.OutOfArea;
                _lastPlacementDiagnostic = "{\"prefab\":" + Json(info.name) + ",\"nativePlacementMode\":" + Json(info.m_placementMode.ToString())
                    + ",\"rawPosition\":" + PositionJson(raw) + ",\"terrainPosition\":" + PositionJson(Terrain(raw)) + ",\"nativeCheckedPosition\":" + PositionJson(point)
                    + ",\"nativeCheckedAngle\":" + JsonFloat(angle) + ",\"sampleMinY\":" + JsonFloat(min) + ",\"sampleMaxY\":" + JsonFloat(max)
                    + ",\"sampleBuildingY\":" + JsonFloat(height) + ",\"waterHeightArgument\":" + JsonFloat(water)
                    + ",\"effectiveCollisionMode\":" + Json(effectiveMode.ToString()) + ",\"placementBranch\":" + Json(shoreline ? "NativeTerrainShoreNoGroundFallback" : "NativeOnGround")
                    + ",\"shoreYBeforeBuildingHeight\":" + JsonFloat(shoreY)
                    + ",\"aiErrors\":" + Json(aiErrors.ToString()) + ",\"spaceErrors\":" + Json(spaceErrors.ToString()) + ",\"combinedErrors\":" + Json(errors.ToString())
                    + ",\"productionRate\":" + rate + ",\"constructionCost\":" + cost + "}";
                if (errors != ToolBase.ToolErrors.None || cost < 0 || rate <= 0) throw new InvalidOperationException("Native service preflight rejected " + info.name + ": " + errors);
                return new Placement { Info = info, InputPosition = raw, Position = point, Angle = angle, Cost = cost, Production = rate, Shoreline = shoreline, WaterHeight = water };
            }

            private Vector3 NativeGroundPipePosition(Vector3 point)
            {
                // NetTool.GetElevation returns zero for the default/min==max setting.
                // SampleTerrainHeight already includes the prefab's m_buildHeight.
                point.y = NetSegment.SampleTerrainHeight(_pipe, point, false, 0f); return point;
            }
            private PipePort PlannedPipePort(Placement site)
            {
                PipePort selected = null; List<string> points = new List<string>(); bool adoptsExisting = false;
                CheckServiceReservation(site.Info);
                if (site.Info.m_paths != null) for (int pathIndex = 0; pathIndex < site.Info.m_paths.Length; ++pathIndex)
                {
                    BuildingInfo.PathInfo path = site.Info.m_paths[pathIndex];
                    if (path.m_finalNetInfo == null || !(path.m_finalNetInfo.m_netAI is WaterPipeAI) || path.m_nodes == null || path.m_nodes.Length == 0) continue;
                    for (int nodeIndex = 0; nodeIndex < path.m_nodes.Length; ++nodeIndex)
                    {
                        Vector3 local = path.m_nodes[nodeIndex];
                        bool fixedHeight = RequiresFixedPathHeight(site.Info, path.m_finalNetInfo, local);
                        Vector3 world = Building.CalculatePosition(site.Position, site.Angle, local);
                        Vector3 calculated = world;
                        if (!fixedHeight) world.y = NetSegment.SampleTerrainHeight(path.m_finalNetInfo, world, false, local.y);
                        NetTool.ControlPoint control;
                        bool snapped = NetTool.MakeControlPoint(new Ray(world + new Vector3(0f, 8f, 0f), Vector3.down), 16f, path.m_finalNetInfo,
                            true, NetNode.Flags.Untouchable, NetSegment.Flags.Untouchable, Building.Flags.All,
                            local.y - path.m_finalNetInfo.m_buildHeight, true, out control);
                        if (snapped)
                        {
                            Vector3 offset = control.m_position - world; if (!fixedHeight) offset.y = 0f;
                            if (offset.sqrMagnitude > path.m_maxSnapDistance * path.m_maxSnapDistance)
                            {
                                control.m_position = world; if (!path.m_keepElevation) control.m_elevation = 0f;
                                control.m_node = control.m_segment = 0;
                            }
                            else control.m_position.y = world.y;
                        }
                        else control.m_position = world;
                        float existingElevation = ProjectedNodeElevation(path.m_finalNetInfo, control);
                        points.Add("{\"path\":" + pathIndex + ",\"node\":" + nodeIndex + ",\"netPrefab\":" + Json(path.m_finalNetInfo.name)
                            + ",\"netUseFixedHeight\":" + Bool(path.m_finalNetInfo.m_useFixedHeight) + ",\"requireFixedHeight\":" + Bool(fixedHeight)
                            + ",\"local\":" + PositionJson(local) + ",\"calculated\":" + PositionJson(calculated) + ",\"heightResolved\":" + PositionJson(world)
                            + ",\"controlPosition\":" + PositionJson(control.m_position) + ",\"controlElevation\":" + JsonFloat(control.m_elevation)
                            + ",\"projectedExistingNodeElevation\":" + JsonFloat(existingElevation)
                            + ",\"netBuildHeight\":" + JsonFloat(path.m_finalNetInfo.m_buildHeight) + ",\"nativeSnapFound\":" + Bool(snapped)
                            + ",\"maxSnapDistance\":" + JsonFloat(path.m_maxSnapDistance) + ",\"keepElevation\":" + Bool(path.m_keepElevation)
                            + ",\"snapNode\":" + control.m_node + ",\"snapSegment\":" + control.m_segment + "}");
                        if (control.m_node != 0 || control.m_segment != 0) adoptsExisting = true;
                        if (selected == null)
                        {
                            selected = new PipePort { Position = control.m_position, Elevation = existingElevation, Info = path.m_finalNetInfo, PathIndex = pathIndex, FixedHeight = fixedHeight };
                        }
                    }
                }
                Add("{\"phase\":\"NativeServicePipePathProfile\",\"prefab\":" + Json(site.Info.name) + ",\"buildingPosition\":" + PositionJson(site.Position)
                    + ",\"buildingAngle\":" + JsonFloat(site.Angle) + ",\"prefabFixedHeight\":" + Bool(site.Info.m_fixedHeight)
                    + ",\"selectedPath\":" + (selected == null ? "null" : selected.PathIndex.ToString(CultureInfo.InvariantCulture))
                    + ",\"points\":[" + string.Join(",", points.ToArray()) + "]}");
                if (selected == null) throw new InvalidOperationException("Service has no native WaterPipeAI path endpoint to preflight.");
                if (adoptsExisting) throw new InvalidOperationException("Service path preflight would adopt an existing network endpoint.");
                return selected;
            }

            private static float ProjectedNodeElevation(NetInfo info, NetTool.ControlPoint control)
            {
                // NetTool.CreateNode stores this byte from the path control. Normal
                // connections later read its signed form, not the original control.
                if (info.m_netAI.IsUnderground()) return -Mathf.Clamp(Mathf.RoundToInt(-control.m_elevation), 0, 255);
                if (info.m_netAI.IsOverground() && (control.m_elevation > 0.1f
                    || control.m_position.y - Singleton<TerrainManager>.instance.SampleRawHeightSmooth(control.m_position) > 0.1f))
                    return Mathf.Clamp(Mathf.RoundToInt(control.m_elevation), 1, 255);
                return 0f;
            }

            // Read-only equivalent of BuildingDecoration.RequireFixedHeight. Fixed
            // nodes retain building-relative Y; coincident compatible paths share it.
            private static bool RequiresFixedPathHeight(BuildingInfo building, NetInfo info, Vector3 position)
            {
                if (info.m_useFixedHeight) return true;
                ItemClass.Service service = info.m_class.m_service, intersectService = ItemClass.Service.None;
                ItemClass.SubService subService = info.m_class.m_subService, intersectSubService = ItemClass.SubService.None;
                ItemClass.Layer layer = info.m_class.m_layer, intersectLayer = ItemClass.Layer.Default;
                if (info.m_intersectClass != null)
                { intersectService = info.m_intersectClass.m_service; intersectSubService = info.m_intersectClass.m_subService; intersectLayer = info.m_intersectClass.m_layer; }
                if (info.m_netAI.SupportUnderground() || info.m_netAI.IsUnderground())
                { layer |= ItemClass.Layer.Default | ItemClass.Layer.MetroTunnels; intersectLayer |= ItemClass.Layer.Default | ItemClass.Layer.MetroTunnels; }
                foreach (BuildingInfo.PathInfo other in building.m_paths)
                {
                    NetInfo fixedInfo = other.m_finalNetInfo;
                    if (fixedInfo == null || !fixedInfo.m_useFixedHeight || other.m_nodes == null) continue;
                    foreach (Vector3 fixedPoint in other.m_nodes)
                    {
                        if ((position - fixedPoint).sqrMagnitude >= 0.001f) continue;
                        ItemClass connection = fixedInfo.GetConnectionClass();
                        if ((service == ItemClass.Service.None || connection.m_service == service)
                            && (subService == ItemClass.SubService.None || connection.m_subService == subService)
                            && (layer == ItemClass.Layer.None || (connection.m_layer & layer) != ItemClass.Layer.None)) return true;
                        if (fixedInfo.m_intersectClass != null && (service == ItemClass.Service.None || fixedInfo.m_intersectClass.m_service == service)
                            && (subService == ItemClass.SubService.None || fixedInfo.m_intersectClass.m_subService == subService)
                            && (layer == ItemClass.Layer.None || (fixedInfo.m_intersectClass.m_layer & layer) != ItemClass.Layer.None)) return true;
                        if (fixedInfo.m_netAI.CanIntersect(info) && connection.m_service == intersectService
                            && (intersectSubService == ItemClass.SubService.None || connection.m_subService == intersectSubService)
                            && (intersectLayer == ItemClass.Layer.None || (connection.m_layer & intersectLayer) != ItemClass.Layer.None)) return true;
                    }
                }
                return false;
            }

            private void ValidatePlannedPipeJunction(Vector3 head, Vector3 end, Vector3 tower, Vector3 outlet)
            {
                Vector3 trunkDirection = HorizontalDirection(end - head), towerDirection = HorizontalDirection(tower - head), outletDirection = HorizontalDirection(outlet - head);
                float trunkTower = Vector3.Dot(trunkDirection, towerDirection), trunkOutlet = Vector3.Dot(trunkDirection, outletDirection), towerOutlet = Vector3.Dot(towerDirection, outletDirection);
                bool allowed = trunkTower <= 0.75f && trunkOutlet <= 0.75f && towerOutlet <= 0.75f;
                Add("{\"phase\":\"PlannedPipeJunctionAngles\",\"junctionChoice\":\"Head\",\"position\":" + PositionJson(head)
                    + ",\"trunkDirection\":" + PositionJson(trunkDirection) + ",\"towerDirection\":" + PositionJson(towerDirection) + ",\"outletDirection\":" + PositionJson(outletDirection)
                    + ",\"trunkTowerDot\":" + JsonFloat(trunkTower) + ",\"trunkOutletDot\":" + JsonFloat(trunkOutlet) + ",\"towerOutletDot\":" + JsonFloat(towerOutlet)
                    + ",\"nativeMaximumDirectionDot\":0.75,\"angleGuardAccepted\":" + Bool(allowed) + "}");
                if (!allowed) throw new InvalidOperationException("Planned service pipes violate the native junction angle limit before spend.");
            }
            private static Vector3 HorizontalDirection(Vector3 direction)
            {
                direction.y = 0f;
                if (direction.sqrMagnitude < 0.001f) throw new InvalidOperationException("Pipe junction has a degenerate horizontal direction.");
                direction.Normalize(); return direction;
            }
            private void RequireOwnedPipeNode(ushort id)
            {
                NetManager net = Singleton<NetManager>.instance; NetStamp stamp;
                if (id == 0 || id >= net.m_nodes.m_buffer.Length || !_nodes.TryGetValue(id, out stamp))
                    throw new InvalidOperationException("Native pipe endpoint is outside the owned allocation manifest.");
                NetNode node = net.m_nodes.m_buffer[id];
                if ((node.m_flags & NetNode.Flags.Created) == 0 || node.Info != _pipe || node.Info != stamp.Info || node.m_buildIndex != stamp.BuildIndex
                    || node.m_position != stamp.Position)
                    throw new InvalidOperationException("Native pipe endpoint no longer matches its owned generation.");
            }
            private void RecordPipeJunction(string link, ushort service, ushort junction)
            {
                RequireOwnedPipeNode(service); RequireOwnedPipeNode(junction);
                NetManager net = Singleton<NetManager>.instance;
                NetNode node = net.m_nodes.m_buffer[junction], serviceNode = net.m_nodes.m_buffer[service];
                Vector3 requestedDirection = HorizontalDirection(serviceNode.m_position - node.m_position);
                List<string> segments = new List<string>();
                for (int i = 0; i < 8; ++i)
                {
                    ushort id = node.GetSegment(i); if (id == 0) continue;
                    NetSegment segment = net.m_segments.m_buffer[id];
                    Vector3 direction = segment.m_startNode == junction ? segment.m_startDirection : segment.m_endDirection;
                    float dot = requestedDirection.x * direction.x + requestedDirection.z * direction.z;
                    segments.Add("{\"segment\":" + id + ",\"startNode\":" + segment.m_startNode + ",\"endNode\":" + segment.m_endNode
                        + ",\"flags\":" + Json(segment.m_flags.ToString()) + ",\"direction\":" + PositionJson(direction) + ",\"requestedDirectionDot\":" + JsonFloat(dot)
                        + ",\"exceedsNativeAngleLimit\":" + Bool(dot > 0.75f) + "}");
                }
                Add("{\"phase\":\"NativePipeJunctionBeforeConnection\",\"link\":" + Json(link) + ",\"serviceNode\":" + service + ",\"junctionNode\":" + junction
                    + ",\"servicePosition\":" + PositionJson(serviceNode.m_position) + ",\"serviceElevation\":" + JsonFloat(SignedNodeElevation(service))
                    + ",\"serviceFlags\":" + Json(serviceNode.m_flags.ToString()) + ",\"junctionPosition\":" + PositionJson(node.m_position)
                    + ",\"junctionElevation\":" + JsonFloat(SignedNodeElevation(junction)) + ",\"junctionFlags\":" + Json(node.m_flags.ToString())
                    + ",\"requestedDirection\":" + PositionJson(requestedDirection) + ",\"existingSegments\":[" + string.Join(",", segments.ToArray()) + "]}");
            }
            private int TestNet(NetInfo info, ushort from, Vector3 a, ushort to, Vector3 b, float? fromElevation = null)
            {
                ushort node, segment; int cost, production;
                ToolBase.ToolErrors errors = CreateNetCall(info, from, a, to, b, true, out node, out segment, out cost, out production, fromElevation);
                Add("{\"phase\":\"NativeNetworkPreflight\",\"prefab\":" + Json(info.name) + ",\"from\":" + PositionJson(a) + ",\"to\":" + PositionJson(b)
                    + ",\"fromNode\":" + from + ",\"toNode\":" + to + ",\"costInternal\":" + cost + ",\"errors\":" + Json(errors.ToString()) + ",\"reservedNodes\":" + _lastReservedNodes
                    + ",\"reservedSegments\":" + _lastReservedSegments + ",\"heightProfile\":" + _lastNetworkProfile + "}");
                if (errors != ToolBase.ToolErrors.None || cost < 0) throw new InvalidOperationException("Native network preflight rejected " + info.name + ": " + errors);
                return cost;
            }
            private ToolBase.ToolErrors CreateNetCall(NetInfo info, ushort from, Vector3 a, ushort to, Vector3 b, bool test, out ushort node, out ushort segment, out int cost, out int production, float? fromElevation = null)
            {
                ushort firstNode;
                return CreateNetCall(info, from, a, to, b, test, out firstNode, out node, out segment, out cost, out production, fromElevation);
            }
            private ToolBase.ToolErrors CreateNetCall(NetInfo info, ushort from, Vector3 a, ushort to, Vector3 b, bool test, out ushort firstNode, out ushort node, out ushort segment, out int cost, out int production, float? fromElevation = null)
            {
                Guard(); Vector3 direction = b - a; direction.y = 0f; direction.Normalize();
                NetTool.ControlPoint start = new NetTool.ControlPoint { m_node = from, m_position = a, m_direction = direction };
                NetTool.ControlPoint end = new NetTool.ControlPoint { m_node = to, m_position = b, m_direction = direction };
                NetTool.ControlPoint middle = new NetTool.ControlPoint { m_position = (a + b) * 0.5f, m_direction = direction };
                start.m_elevation = fromElevation ?? 0f;
                if (from != 0) start.m_elevation = SignedNodeElevation(from);
                if (to != 0) end.m_elevation = SignedNodeElevation(to);
                middle.m_elevation = (start.m_elevation + end.m_elevation) * 0.5f;
                FastList<NetTool.NodePosition> positions = new FastList<NetTool.NodePosition>();
                ushort lastNode;
                ToolBase.ToolErrors errors = NetTool.CreateNode(info, start, middle, end, positions, 8,
                    test, true, false, true, true, false, false, 0, out firstNode, out lastNode, out segment, out cost, out production);
                // Preserve the shorter public overload's return semantics for existing callers.
                node = errors == ToolBase.ToolErrors.None ? (lastNode != 0 ? lastNode : firstNode) : (ushort)0;
                if (test)
                {
                    _lastReservedNodes = _lastReservedSegments = 0;
                    List<string> profile = new List<string>();
                    for (int i = 0; i < positions.m_size; ++i)
                    {
                        NetTool.NodePosition point = positions.m_buffer[i];
                        profile.Add("{\"position\":" + PositionJson(point.m_position) + ",\"elevation\":" + JsonFloat(point.m_elevation)
                            + ",\"terrainHeight\":" + JsonFloat(point.m_terrainHeight) + ",\"double\":" + Bool(point.m_double) + "}");
                    }
                    _lastNetworkProfile = "[" + string.Join(",", profile.ToArray()) + "]";
                }
                if (test && errors == ToolBase.ToolErrors.None)
                {
                    if (info.m_netAI.RequireDoubleSegments() || positions.m_size < 2) throw new InvalidOperationException("Unsupported native network allocation geometry.");
                    // NetTool clears m_nodeInfo before returning. Allocation counts use
                    // its retained final positions, not that transient private result.
                    for (int i = 0; i < positions.m_size; ++i)
                        if (positions.m_buffer[i].m_double) throw new InvalidOperationException("Native double-node layout is outside the bounded fixture contract.");
                    // At most one replacement at each existing endpoint through TryMoveNode.
                    _lastReservedNodes = positions.m_size + 2;
                    _lastReservedSegments = positions.m_size - 1 + 2;
                    CheckNetworkReservation(_lastReservedNodes, _lastReservedSegments);
                }
                return errors;
            }

            private void CheckNetworkReservation(int nodes, int segments)
            {
                if (nodes < 0 || segments < 0 || _nodes.Count + nodes > 48 || _segments.Count + segments > 40)
                    throw new InvalidOperationException("Native allocation would exceed the pre-commit node/segment reservation.");
            }
            private void CheckServiceReservation(BuildingInfo info)
            {
                int nodes = 0, segments = 0;
                if (info.m_paths != null) foreach (BuildingInfo.PathInfo path in info.m_paths)
                {
                    if (path.m_finalNetInfo == null || path.m_nodes == null || path.m_nodes.Length == 0) continue;
                    if (!(path.m_finalNetInfo.m_netAI is WaterPipeAI) || path.m_finalNetInfo.m_netAI.RequireDoubleSegments() || path.m_nodes.Length > 4)
                        throw new InvalidOperationException("Unsupported service path allocation geometry.");
                    // Each path point may snap/split: reserve its node plus both endpoint
                    // moves and split nodes, and replacement/path segments conservatively.
                    nodes += path.m_nodes.Length * 4;
                    segments += path.m_nodes.Length * 5;
                }
                CheckNetworkReservation(nodes, segments);
            }

            private void BuildInfrastructure()
            {
                Guard();
                if (_simulation.ForcedSimulationPaused) { SetPhase("WaitingForExternalForcedPause"); return; }
                ValidateAllServicePlans();
                _oldPaused = _simulation.SimulationPaused; _oldSpeed = _simulation.SelectedSimulationSpeed; _speedHeld = true;
                _simulation.SelectedSimulationSpeed = 3; _simulation.SimulationPaused = false;
                SetPhase("BuildingPaidNativeInfrastructure");
                NetManager net = Singleton<NetManager>.instance;
                ushort junction = BuildNet(_road, _stubA, net.m_nodes.m_buffer[_stubA].m_position, 0, _head, false);
                BuildNet(_road, _stubB, net.m_nodes.m_buffer[_stubB].m_position, junction, net.m_nodes.m_buffer[junction].m_position, false);
                BuildNet(_road, junction, net.m_nodes.m_buffer[junction].m_position, 0, _end, true);
                ushort wind = BuildService(_windSite), tower = BuildService(_towerSite), drain = BuildService(_drainSite), outletWind = BuildService(_outletWindSite);
                Vector3 pipeHead = NativeGroundPipePosition(_head), pipeEnd = NativeGroundPipePosition(_end);
                ushort pipeJunction;
                ushort pipeStreetEnd = BuildNet(_pipe, 0, pipeHead, 0, pipeEnd, false, out pipeJunction);
                RequireOwnedPipeNode(pipeJunction); RequireOwnedPipeNode(pipeStreetEnd);
                Add("{\"phase\":\"NativePipeStreetEndpoints\",\"junctionChoice\":\"Head\",\"headNode\":" + pipeJunction
                    + ",\"endNode\":" + pipeStreetEnd + ",\"headPosition\":" + PositionJson(net.m_nodes.m_buffer[pipeJunction].m_position)
                    + ",\"endPosition\":" + PositionJson(net.m_nodes.m_buffer[pipeStreetEnd].m_position) + "}");
                ushort towerNode = PipeNode(tower, PlannedPipePort(_towerSite)), drainNode = PipeNode(drain, PlannedPipePort(_drainSite));
                RecordPipeJunction("TowerToStreetHead", towerNode, pipeJunction);
                pipeJunction = BuildNet(_pipe, towerNode, net.m_nodes.m_buffer[towerNode].m_position, pipeJunction, net.m_nodes.m_buffer[pipeJunction].m_position, false);
                RecordPipeJunction("OutletToStreetHead", drainNode, pipeJunction);
                pipeJunction = BuildNet(_pipe, drainNode, net.m_nodes.m_buffer[drainNode].m_position, pipeJunction, net.m_nodes.m_buffer[pipeJunction].m_position, false);
                if (!PowerCorridorClear(_localPower.A, _localPower.B) || !PowerCorridorClear(_shorePower.A, _shorePower.B))
                    throw new InvalidOperationException("Native service placement changed into a reserved power corridor.");
                BuildNet(_power, 0, _localPower.A, 0, _localPower.B, false);
                BuildNet(_power, 0, _shorePower.A, 0, _shorePower.B, false);
                Add("{\"phase\":\"InfrastructureBuilt\",\"wind\":" + wind + ",\"tower\":" + tower + ",\"drain\":" + drain
                    + ",\"outletWind\":" + outletWind + ",\"constructionDebitInternal\":" + _constructionDebit
                    + ",\"cashInternal\":" + Singleton<EconomyManager>.instance.InternalCashAmount + ",\"newSegments\":" + _segments.Count + ",\"newNodes\":" + _nodes.Count + "}");
                _state = 2; SetPhase("WaitingForNativeZoneBlocksAndOrganicGrowth");
            }
            private ushort BuildService(Placement planned)
            {
                Guard(); Placement actual = PreflightBuilding(planned.Info, planned.InputPosition, planned.Angle, planned.Shoreline);
                ValidateSiteAgainstPlans(actual, AllServiceSites(), planned);
                planned.Position = actual.Position; planned.Angle = actual.Angle; planned.WaterHeight = actual.WaterHeight;
                CheckServiceReservation(actual.Info);
                BuildingManager manager = Singleton<BuildingManager>.instance;
                if (!manager.CheckLimits()) throw new InvalidOperationException("Native building limit rejected infrastructure.");
                CheckDebit(actual.Cost);
                EconomyManager economy = Singleton<EconomyManager>.instance;
                if (economy.PeekResource(EconomyManager.Resource.Construction, actual.Cost) != actual.Cost) throw new InvalidOperationException("Insufficient native construction resources.");
                int paid = economy.FetchResource(EconomyManager.Resource.Construction, actual.Cost, actual.Info.m_class); _constructionDebit += paid;
                if (paid != actual.Cost) throw new InvalidOperationException("Native service construction was not fully funded.");
                ushort id = 0;
                BeginNativeAllocation();
                try
                {
                    if (!manager.CreateBuilding(out id, ref _simulation.m_randomizer, actual.Info, actual.Position, actual.Angle, 0, _simulation.m_currentBuildIndex) || id == 0)
                        throw new InvalidOperationException("Native service allocation failed after its genuine debit.");
                    if (actual.Info.m_fixedHeight) manager.m_buildings.m_buffer[id].m_flags |= Building.Flags.FixedHeight;
                }
                finally
                {
                    if (id != 0)
                    {
                        Building data = manager.m_buildings.m_buffer[id];
                        _buildings[id] = new BuildingStamp { Info = data.Info, Position = data.m_position, BuildIndex = data.m_buildIndex };
                    }
                    CaptureNewNetwork(false);
                }
                ++_simulation.m_currentBuildIndex;
                actual.Info.m_buildingAI.PlacementSucceeded();
                Add("{\"phase\":\"ServiceConstruction\",\"building\":" + id + ",\"prefab\":" + Json(actual.Info.name) + ",\"paidInternal\":" + paid + ",\"position\":" + PositionJson(actual.Position) + "}");
                return id;
            }
            private ushort BuildNet(NetInfo info, ushort from, Vector3 a, ushort to, Vector3 b, bool zoningRoad)
            {
                ushort firstNode;
                return BuildNet(info, from, a, to, b, zoningRoad, out firstNode);
            }
            private ushort BuildNet(NetInfo info, ushort from, Vector3 a, ushort to, Vector3 b, bool zoningRoad, out ushort firstNode)
            {
                int estimate = TestNet(info, from, a, to, b); CheckDebit(estimate);
                if (!Singleton<NetManager>.instance.CheckLimits()) throw new InvalidOperationException("Native network limit reached.");
                long before = Singleton<EconomyManager>.instance.InternalCashAmount;
                ushort node, segment; int cost, production;
                ToolBase.ToolErrors errors;
                BeginNativeAllocation();
                long debit = 0;
                try { errors = CreateNetCall(info, from, a, to, b, false, out firstNode, out node, out segment, out cost, out production); }
                finally
                {
                    debit = before - Singleton<EconomyManager>.instance.InternalCashAmount;
                    if (debit > 0) _constructionDebit += debit;
                    CaptureNewNetwork(zoningRoad);
                }
                if (debit < 0) throw new InvalidOperationException("Unexpected native construction credit.");
                Add("{\"phase\":\"NetworkConstruction\",\"prefab\":" + Json(info.name) + ",\"estimateInternal\":" + estimate
                    + ",\"paidInternal\":" + debit + ",\"errors\":" + Json(errors.ToString()) + ",\"from\":" + PositionJson(a) + ",\"to\":" + PositionJson(b)
                    + ",\"firstNode\":" + firstNode + ",\"lastOrOnlyNode\":" + node + "}");
                if (errors != ToolBase.ToolErrors.None || node == 0 || _segments.Count > 40 || _nodes.Count > 48 || _constructionDebit > MaximumConstructionDebit)
                    throw new InvalidOperationException("Native network construction failed/exceeded the bounded fixture manifest.");
                return node;
            }
            private void CheckDebit(int cost)
            {
                Guard(); if (cost < 0 || _constructionDebit + cost > MaximumConstructionDebit) throw new InvalidOperationException("Organic fixture construction budget exceeded.");
            }
            private void CaptureNewNetwork(bool zoningRoad)
            {
                NetManager net = Singleton<NetManager>.instance;
                for (ushort i = 1; i != 0; ++i)
                {
                    if (i < net.m_nodes.m_buffer.Length && !_callNodes.Contains(i) && !_baselineNodes.Contains(i) && !_nodes.ContainsKey(i))
                    { NetNode node = net.m_nodes.m_buffer[i]; if ((node.m_flags & NetNode.Flags.Created) != 0) _nodes.Add(i, new NetStamp { Info = node.Info, Position = node.m_position, BuildIndex = node.m_buildIndex }); }
                    if (i < net.m_segments.m_buffer.Length && !_callSegments.Contains(i) && !_baselineSegments.Contains(i) && !_segments.ContainsKey(i))
                    { NetSegment segment = net.m_segments.m_buffer[i]; if ((segment.m_flags & NetSegment.Flags.Created) != 0) { _segments.Add(i, new NetStamp { Info = segment.Info, Position = default(Vector3), BuildIndex = segment.m_buildIndex, Start = segment.m_startNode, End = segment.m_endNode }); if (zoningRoad && segment.Info == _road) _zoningRoads.Add(i); } }
                }
            }
            private void BeginNativeAllocation()
            {
                _callNodes.Clear(); _callSegments.Clear(); NetManager net = Singleton<NetManager>.instance;
                for (ushort id = 1; id != 0; ++id)
                {
                    if (id < net.m_nodes.m_buffer.Length && (net.m_nodes.m_buffer[id].m_flags & NetNode.Flags.Created) != 0) _callNodes.Add(id);
                    if (id < net.m_segments.m_buffer.Length && (net.m_segments.m_buffer[id].m_flags & NetSegment.Flags.Created) != 0) _callSegments.Add(id);
                }
            }
            private static float SignedNodeElevation(ushort id)
            {
                NetNode node = Singleton<NetManager>.instance.m_nodes.m_buffer[id];
                return node.Info.m_netAI.IsUnderground() ? -node.m_elevation : node.m_elevation;
            }
            private ushort PipeNode(ushort building, PipePort expected)
            {
                ushort id = Singleton<BuildingManager>.instance.m_buildings.m_buffer[building].m_netNode; NetManager net = Singleton<NetManager>.instance;
                List<string> nodes = new List<string>(); ushort selected = 0; HashSet<ushort> seen = new HashSet<ushort>();
                for (int i = 0; id != 0 && i < 32; ++i, id = net.m_nodes.m_buffer[id].m_nextBuildingNode)
                {
                    if (!seen.Add(id)) throw new InvalidOperationException("Native service node chain has a cycle.");
                    NetNode node = net.m_nodes.m_buffer[id];
                    nodes.Add("{\"node\":" + id + ",\"position\":" + PositionJson(node.m_position) + ",\"elevation\":" + JsonFloat(SignedNodeElevation(id))
                        + ",\"prefab\":" + Json(node.Info == null ? null : node.Info.name) + ",\"flags\":" + Json(node.m_flags.ToString()) + "}");
                    if (node.Info == expected.Info && (node.m_position - expected.Position).sqrMagnitude < 0.001f
                        && Math.Abs(SignedNodeElevation(id) - expected.Elevation) < 0.01f) selected = id;
                }
                Add("{\"phase\":\"NativeServicePipeNodes\",\"building\":" + building + ",\"expectedPosition\":" + PositionJson(expected.Position)
                    + ",\"expectedElevation\":" + JsonFloat(expected.Elevation) + ",\"selectedNode\":" + selected + ",\"nodes\":[" + string.Join(",", nodes.ToArray()) + "]}");
                if (id != 0 || selected == 0) throw new InvalidOperationException("Native service did not create the matching planned water-pipe endpoint.");
                return selected;
            }

            private void ObserveOrganicGrowth()
            {
                Guard();
                if (_simulation.ForcedSimulationPaused || _simulation.SimulationPaused) { SetPhase("WaitingForUnmodifiedNativePauseState"); return; }
                ZoneManager zones = Singleton<ZoneManager>.instance;
                List<ZoneCandidate> zoneCandidates = new List<ZoneCandidate>(); HashSet<ushort> seenBlocks = new HashSet<ushort>();
                foreach (ushort road in _zoningRoads)
                {
                    NetSegment segment = Singleton<NetManager>.instance.m_segments.m_buffer[road];
                    foreach (ushort id in new ushort[] { segment.m_blockStartLeft, segment.m_blockStartRight, segment.m_blockEndLeft, segment.m_blockEndRight })
                    {
                        if (id == 0 || _zonedBlocks.Contains(id) || _zonedCells >= 96 || !seenBlocks.Add(id)) continue;
                        ZoneBlock block = zones.m_blocks.m_buffer[id];
                        if ((block.m_flags & 3u) != 1u || block.m_valid == 0) continue;
                        // ZoneTool uses (x-3.5)*across + (row-3.5)*along for cell centers.
                        Vector3 across = new Vector3(Mathf.Cos(block.m_angle), 0f, Mathf.Sin(block.m_angle)) * 8f;
                        Vector3 along = new Vector3(across.z, 0f, -across.x);
                        Vector3 center = block.m_position - across * 2f + along * (block.RowCount * 0.5f - 4f);
                        zoneCandidates.Add(new ZoneCandidate { Block = id, Segment = road, Center = center, WindDistance = HorizontalDistance(center, _windSite.Position) });
                    }
                }
                zoneCandidates.Sort(delegate(ZoneCandidate a, ZoneCandidate b)
                { int comparison = a.WindDistance.CompareTo(b.WindDistance); return comparison != 0 ? comparison : a.Block.CompareTo(b.Block); });
                foreach (ZoneCandidate candidate in zoneCandidates)
                {
                        if (_zonedCells >= 96) break;
                        ushort id = candidate.Block; ref ZoneBlock block = ref zones.m_blocks.m_buffer[id];
                        int beforeCells = _zonedCells;
                        bool changed = false;
                        for (int z = 0; z < block.RowCount && _zonedCells < 96; ++z) for (int x = 0; x < 4 && _zonedCells < 96; ++x)
                        {
                            ulong bit = 1UL << ((z << 3) | x);
                            if ((block.m_valid & bit) == 0 || ((block.m_occupied1 | block.m_occupied2) & bit) != 0 || block.GetZone(x, z) != ItemClass.Zone.Unzoned) continue;
                            if (block.SetZone(x, z, ItemClass.Zone.ResidentialLow)) { ++_zonedCells; changed = true; }
                        }
                        if (changed)
                        {
                            block.RefreshZoning(id); _zonedBlocks.Add(id);
                            Add("{\"phase\":\"NativeZonePaint\",\"block\":" + id + ",\"segment\":" + candidate.Segment + ",\"center\":" + PositionJson(candidate.Center)
                                + ",\"distanceToLocalWind\":" + JsonFloat(candidate.WindDistance) + ",\"distanceToTower\":" + JsonFloat(HorizontalDistance(candidate.Center, _towerSite.Position))
                                + ",\"distanceToHead\":" + JsonFloat(HorizontalDistance(candidate.Center, _head)) + ",\"distanceToEnd\":" + JsonFloat(HorizontalDistance(candidate.Center, _end))
                                + ",\"selection\":\"OwnedFinalStreetBlocksNearestLocalWind\",\"cellsPainted\":" + (_zonedCells - beforeCells)
                                + ",\"totalCells\":" + _zonedCells + ",\"frame\":" + _simulation.m_currentFrameIndex + "}");
                        }
                }
                Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
                int completed = 0, settled = 0, movingIn = 0, bound = 0, serviced = 0, qualifyingHomes = 0;
                byte[] bytes = _bridge.GetClassSaveState(); ResidentialClassLedger ledger = null;
                if (bytes != null) using (MemoryStream stream = new MemoryStream(bytes, false)) ledger = ResidentialClassLedgerCodec.Read(stream);
                List<string> homes = new List<string>();
                string candidateSignature = null; ushort candidateBuilding = 0; uint candidateBuildIndex = 0; int candidateClass = 0;
                string capacityObservation = "null"; bool capacityKnown = false;
                for (int i = 1; i < buildings.Length; ++i)
                {
                    ref Building home = ref buildings[i];
                    if ((home.m_flags & (Building.Flags.Created | Building.Flags.Deleted)) != Building.Flags.Created || home.Info == null || !(home.Info.m_buildingAI is ResidentialBuildingAI)) continue;
                    ushort id = (ushort)i;
                    if (!_grownHomes.Contains(id))
                    {
                        if (Vector3.Distance(home.m_position, _head) > 448f) throw new InvalidOperationException("Unexpected growable outside the fixture footprint.");
                        _grownHomes.Add(id); _buildings.Add(id, new BuildingStamp { Info = home.Info, Position = home.m_position, BuildIndex = home.m_buildIndex });
                    }
                    bool done = (home.m_flags & Building.Flags.Completed) != 0; if (done) ++completed;
                    bool electricity, water, sewage; byte pollution;
                    Singleton<ElectricityManager>.instance.CheckElectricity(home.m_position, out electricity);
                    Singleton<WaterManager>.instance.CheckWater(home.m_position, out water, out sewage, out pollution);
                    bool utilities = electricity && water && sewage; if (utilities) ++serviced;
                    ResidentialLotRecord record = null;
                    ResidentialLotIdentity identity = new ResidentialLotIdentity(id, home.m_buildIndex, FloatBits(home.m_position.x), FloatBits(home.m_position.z),
                        home.Info.name, (int)home.Info.m_class.m_service, (int)home.Info.m_class.m_subService, (byte)home.Width, (byte)home.Length);
                    bool selected = ledger != null && ledger.TryGet(identity, out record) && record.Origin == ClassOrigin.Selected;
                    bool roadAccess = false;
                    if (home.m_accessSegment != 0 && _zoningRoads.Contains(home.m_accessSegment))
                    {
                        NetSegment access = Singleton<NetManager>.instance.m_segments.m_buffer[home.m_accessSegment];
                        roadAccess = (access.m_flags & (NetSegment.Flags.Created | NetSegment.Flags.Deleted)) == NetSegment.Flags.Created
                            && FindOutside(access.m_startNode) != 0 && FindOutside(access.m_endNode) != 0;
                    }
                    int homeSettled = 0, homeIncoming = 0, homeBound = 0, settledFamilies = 0; uint unit = home.m_citizenUnits;
                    List<string> families = new List<string>();
                    HashSet<uint> uniqueMembers = new HashSet<uint>();
                    HashSet<uint> seen = new HashSet<uint>();
                    while (unit != 0 && seen.Count < 4096 && seen.Add(unit))
                    {
                        if (unit >= Singleton<CitizenManager>.instance.m_units.m_buffer.Length) throw new InvalidOperationException("HOME unit id is outside the native buffer.");
                        CitizenUnit data = Singleton<CitizenManager>.instance.m_units.m_buffer[unit];
                        if (data.m_building != id) throw new InvalidOperationException("Native HOME chain has a foreign building reference.");
                        int unitSettled = 0; int family = -1; bool coherentFamily = true;
                        List<string> settledIds = new List<string>();
                        if ((data.m_flags & (CitizenUnit.Flags.Created | CitizenUnit.Flags.Home)) == (CitizenUnit.Flags.Created | CitizenUnit.Flags.Home)) for (int c = 0; c < 5; ++c)
                        {
                            uint citizenId = data.GetCitizen(c); if (citizenId == 0) continue;
                            if (!uniqueMembers.Add(citizenId)) throw new InvalidOperationException("A native HOME member id occurs more than once in this building.");
                            if (citizenId >= Singleton<CitizenManager>.instance.m_citizens.m_buffer.Length) throw new InvalidOperationException("HOME citizen id is outside the native buffer.");
                            Citizen citizen = Singleton<CitizenManager>.instance.m_citizens.m_buffer[citizenId];
                            if ((citizen.m_flags & Citizen.Flags.Created) == 0 || citizen.m_homeBuilding != id) throw new InvalidOperationException("Native HOME/citizen relationship is invalid.");
                            if ((citizen.m_flags & Citizen.Flags.MovingIn) != 0) ++homeIncoming;
                            else if ((citizen.m_flags & Citizen.Flags.Dead) == 0)
                            {
                                ++homeSettled;
                                ++unitSettled; settledIds.Add(citizenId.ToString(CultureInfo.InvariantCulture));
                                if (family == -1) family = citizen.m_family;
                                else if (family != citizen.m_family) coherentFamily = false;
                                if (selected && (int)citizen.WealthLevel + 1 == (int)record.OccupantClass) { ++bound; ++homeBound; }
                            }
                        }
                        if (unitSettled >= 2 && coherentFamily)
                        {
                            ++settledFamilies;
                            families.Add("{\"homeUnit\":" + unit + ",\"nativeFamily\":" + family + ",\"uniqueSettledCitizenIds\":[" + string.Join(",", settledIds.ToArray()) + "]}");
                        }
                        unit = data.m_nextUnit;
                    }
                    if (unit != 0) throw new InvalidOperationException("Native HOME chain exceeds its bounded/cycle-safe scan.");
                    settled += homeSettled; movingIn += homeIncoming;
                    if (done && utilities && roadAccess && settledFamilies > 0 && homeBound == homeSettled)
                    {
                        ++qualifyingHomes;
                        if (candidateSignature == null)
                        {
                            candidateBuilding = id; candidateBuildIndex = home.m_buildIndex;
                            candidateClass = (int)record.OccupantClass;
                            candidateSignature = id + ":" + home.m_buildIndex + ":" + record.Generation + ":" + record.OccupantClass + ":" + string.Join(",", families.ToArray());
                            // Additional read-only evidence on this exact qualified
                            // building. It never gates the baseline organic criterion.
                            try
                            {
                                ResidentialCapacityObservation observation = ResidentialCapacityObserver.Observe(id, ref home);
                                capacityKnown = observation != null && observation.Status == ResidentialCapacityObservationStatus.Known;
                                capacityObservation = CapacityObservationJson(observation);
                            }
                            catch (Exception error)
                            {
                                capacityKnown = false;
                                capacityObservation = "{\"status\":\"Unknown\",\"reason\":\"DiagnosticInvocationFailed\",\"buildingId\":" + id
                                    + ",\"detail\":" + Json(BoundedModalText(error.GetType().Name + ": " + error.Message, 512))
                                    + ",\"compatibility\":\"Uncertified\",\"capacityControlEnabled\":false}";
                            }
                        }
                    }
                    homes.Add("{\"building\":" + id + ",\"buildIndex\":" + home.m_buildIndex + ",\"prefab\":" + Json(home.Info.name)
                        + ",\"completed\":" + Bool(done) + ",\"settled\":" + homeSettled + ",\"movingIn\":" + homeIncoming
                        + ",\"electricity\":" + Bool(electricity) + ",\"water\":" + Bool(water) + ",\"sewage\":" + Bool(sewage)
                        + ",\"waterPollution\":" + pollution + ",\"outsideConnectedRoadAccess\":" + Bool(roadAccess) + ",\"selectedLedgerRecord\":" + Bool(selected)
                        + ",\"settledFamilies\":[" + string.Join(",", families.ToArray()) + "]"
                        + ",\"class\":" + Json(selected ? record.OccupantClass.ToString() : "Unbound") + ",\"position\":" + PositionJson(home.m_position) + "}");
                }
                if (settled > 0 && _firstSettledFrame == 0) _firstSettledFrame = _simulation.m_currentFrameIndex;
                _capacityObservation = capacityObservation; _capacityObservationKnown = capacityKnown;
                var demand = ModRuntime.CurrentDemand;
                var status = ModRuntime.CurrentResidentialClasses;
                ResidentialStockSnapshot stock = demand == null || demand.Census == null ? null : demand.Census.ResidentialStock;
                if (candidateSignature != _qualifiedSignature)
                {
                    _qualifiedSignature = candidateSignature; _qualifiedFrame = _simulation.m_currentFrameIndex;
                    _qualifiedBuilding = candidateBuilding; _qualifiedBuildIndex = candidateBuildIndex;
                    _qualifiedClass = candidateClass;
                }
                bool publishedSameLot = false;
                if (candidateSignature != null && stock != null && stock.Frame >= _qualifiedFrame)
                    foreach (ResidentialStockLot lot in stock.Lots)
                        if (lot.BuildingId == _qualifiedBuilding && lot.ObservedBuildIndex == _qualifiedBuildIndex && lot.LotClass == _qualifiedClass
                            && lot.Phase == ResidentialStockPhase.Completed && lot.SettledPeople >= 2 && lot.LiveHomeUnits > 0
                            && lot.InvalidMemberReferences == 0 && lot.PeopleWithoutHomeUnitReference == 0) publishedSameLot = true;
                _capacityAdmission = CapacityAdmissionJson(out _capacityAdmissionVerified);
                Add("{\"phase\":\"OrganicObservation\",\"frame\":" + _simulation.m_currentFrameIndex + ",\"nativeDateTicks\":" + _simulation.m_metaData.m_currentDateTime.Ticks
                    + ",\"zonedCells\":" + _zonedCells + ",\"completed\":" + completed + ",\"settled\":" + settled + ",\"movingIn\":" + movingIn
                    + ",\"boundSettled\":" + bound + ",\"fullyServicedHomes\":" + serviced + ",\"nativeResidentialDemand\":" + Singleton<ZoneManager>.instance.m_actualResidentialDemand
                    + ",\"publishedCensusFrame\":" + (demand == null ? "null" : demand.Census.Frame.ToString(CultureInfo.InvariantCulture))
                    + ",\"qualifyingSameLotHomes\":" + qualifyingHomes + ",\"qualifiedBuilding\":" + _qualifiedBuilding + ",\"qualifiedBuildIndex\":" + _qualifiedBuildIndex
                    + ",\"qualifiedObservationFrame\":" + _qualifiedFrame + ",\"qualifiedFamilySignature\":" + Json(_qualifiedSignature)
                    + ",\"firstSettledObservationFrame\":" + _firstSettledFrame
                    + ",\"publishedSameLot\":" + Bool(publishedSameLot) + ",\"publishedResidentialStock\":" + StockJson(stock)
                    + ",\"capacityObservationKnown\":" + Bool(capacityKnown) + ",\"capacityObservation\":" + capacityObservation
                    + ",\"capacityAdmission\":" + _capacityAdmission
                    + ",\"ledgerId\":" + Json(ledger == null ? null : ledger.LedgerId.ToString()) + ",\"homes\":[" + string.Join(",", homes.ToArray()) + "]}");
                if (qualifyingHomes > 0 && settled >= 2 && bound == settled && status != null && status.Enabled && status.UnboundResidents == 0
                    && demand != null && demand.Converged && demand.Census.Frame >= _qualifiedFrame && demand.Census.OccupiedHomeHouseholds > 0
                    && stock != null && publishedSameLot && stock.Totals.SettledPeople >= 2
                    && stock.Totals.InvalidMemberReferences == 0 && stock.Totals.PeopleWithoutHomeUnitReference == 0
                    && (!_capacityModeRequested || _capacityAdmissionVerified))
                {
                    _verified = true;
                    if (_owner.IsOrganicRoundtripMode()) { _handoffRequested = true; SetPhase("OrganicCheckpointReadyForPausedHandoff"); }
                    else { SetPhase("OrganicGrowthAndSettledBindingsVerified"); RequestCleanup(); }
                }
                else SetPhase("ObservingNativeConstructionImmigrationAndBindings");
            }

            // Called only inside the owned simulation action. ReportJson reads the
            // cached string; it never captures economic state from the UI thread.
            private string CapacityAdmissionJson(out bool verified)
            {
                verified = false;
                ResidentialCapacityBridge capacity = _bridge.Capacity;
                _capacityModeRequested = capacity != null && capacity.ModeRequested;
                if (!_capacityModeRequested) return "{\"requested\":false,\"verified\":false}";
                ResidentialCapacityStatus status = capacity.Published;
                if (status == null) return "{\"requested\":true,\"verified\":false,\"reason\":\"NoPublishedCapacityStatus\"}";
                string prefix = "{\"requested\":true,\"ready\":" + Bool(status.Ready) + ",\"hooksReady\":" + Bool(status.HooksReady)
                    + ",\"compatibilityKnown\":" + Bool(status.CompatibilityKnown) + ",\"status\":" + Json(status.Status)
                    + ",\"day\":" + status.Day + ",\"stockRevision\":" + status.StockRevision + ",\"externalRevision\":" + status.ExternalRevision
                    + ",\"unknownBuildings\":" + status.UnknownBuildings + ",\"pendingReservations\":" + status.PendingReservations
                    + ",\"unresolvedCreations\":" + status.UnresolvedCreations + ",\"pendingMutations\":" + status.PendingMutations
                    + ",\"unresolvedMutations\":" + status.UnresolvedMutations + ",\"controlledIntents\":" + status.ControlledIntents
                    + ",\"rejectedIntents\":" + status.RejectedIntents + ",\"reservedAttempts\":" + status.ReservedAttempts
                    + ",\"committedCreations\":" + status.CommittedCreations + ",\"lastNativeRoll\":" + status.LastNativeRoll
                    + ",\"lastNativeThreshold\":" + status.LastNativeThreshold;
                EconomicSavePair pair; string reason;
                if (!_bridge.TryGetSavePair(out pair, out reason))
                    return prefix + ",\"pairAvailable\":false,\"reason\":" + Json(reason) + ",\"verified\":false,\"saveReloadVerified\":false}";
                byte[] classes = pair.ClassPayload, payload = pair.CapacityPayload;
                if (payload == null) return prefix + ",\"pairAvailable\":true,\"reason\":\"CapacityPayloadAbsent\",\"verified\":false,\"saveReloadVerified\":false}";
                ResidentialClassLedger classLedger;
                ResidentialCapacityLedgerState state;
                using (MemoryStream stream = new MemoryStream(classes, false)) classLedger = ResidentialClassLedgerCodec.Read(stream);
                using (MemoryStream stream = new MemoryStream(payload, false)) state = ResidentialCapacityLedgerCodec.ReadBound(stream, classes);
                long[] stock = new long[3]; bool sameQualifiedRecord = false;
                foreach (ResidentialCapacityRecord record in state.Records)
                {
                    if (record.IsActive) stock[(int)record.Class - 1] = checked(stock[(int)record.Class - 1] + record.CapacityQ);
                    ResidentialLotRecord selected;
                    if (_qualifiedBuilding != 0 && record.Identity.BuildingId == _qualifiedBuilding && record.Identity.NativeBuildIndex == _qualifiedBuildIndex
                        && (int)record.Class == _qualifiedClass && record.Phase == ResidentialCapacityPhase.Completed && record.Origin == ClassOrigin.Selected
                        && classLedger.TryGet(record.Identity, out selected) && selected.Generation == record.Generation
                        && selected.OccupantClass == record.Class && selected.Origin == ClassOrigin.Selected) sameQualifiedRecord = true;
                }
                long[] publishedStock = status.StockQ;
                bool coherent = state.ClassLedgerId == classLedger.LedgerId && state.StockRevision == status.StockRevision
                    && state.ExternalRevision == status.ExternalRevision && state.PublishedStockRevision == state.StockRevision
                    && state.PublishedExternalRevision == state.ExternalRevision && publishedStock.Length == 3;
                for (int i = 0; coherent && i < 3; ++i) coherent = stock[i] == publishedStock[i];
                List<string> windows = new List<string>();
                foreach (ResidentialCapacityWindow window in state.Windows)
                    if (window != null) windows.Add("{\"class\":" + Json(window.Class.ToString()) + ",\"day\":" + window.Day + ",\"phase\":" + Json(window.Phase.ToString())
                        + ",\"openingRaw\":" + JsonFloat(window.OpeningRaw) + ",\"overbuild\":" + JsonFloat(window.Overbuild)
                        + ",\"desiredQ\":" + window.DesiredQ + ",\"desiredMaximumQ\":" + window.DesiredMaximumQ + ",\"committedQ\":" + window.CommittedQ + "}");
                verified = coherent && sameQualifiedRecord && status.Ready && status.HooksReady && status.CompatibilityKnown
                    && status.UnknownBuildings == 0 && status.PendingReservations == 0 && status.UnresolvedCreations == 0
                    && status.PendingMutations == 0 && status.UnresolvedMutations == 0
                    && status.CommittedCreations > 0 && status.RejectedIntents > 0
                    && ReferenceEquals(capacity, _bridge.Capacity) && ReferenceEquals(status, capacity.Published);
                return prefix + ",\"pairAvailable\":true,\"pairCoherent\":" + Bool(coherent) + ",\"sameQualifiedCapacityRecord\":" + Bool(sameQualifiedRecord)
                    + ",\"classLedgerId\":" + Json(state.ClassLedgerId.ToString()) + ",\"records\":" + state.Records.Length
                    + ",\"stockQ\":[" + stock[0] + "," + stock[1] + "," + stock[2] + "],\"windows\":[" + string.Join(",", windows.ToArray())
                    + "],\"verified\":" + Bool(verified) + ",\"saveReloadVerified\":false}";
            }

            private static string CapacityObservationJson(ResidentialCapacityObservation observation)
            {
                if (observation == null) return "null";
                ResidentialLotIdentity identity = observation.Identity;
                string identityJson = identity == null ? "null" : "{\"buildingId\":" + identity.BuildingId + ",\"nativeBuildIndex\":" + identity.NativeBuildIndex
                    + ",\"originXBits\":" + identity.OriginXBits + ",\"originZBits\":" + identity.OriginZBits + ",\"prefabName\":" + Json(identity.PrefabName)
                    + ",\"service\":" + identity.Service + ",\"subService\":" + identity.SubService + ",\"width\":" + identity.Width + ",\"length\":" + identity.Length + "}";
                byte[] profileHash = observation.ProfileHash;
                return "{\"status\":" + Json(observation.Status.ToString()) + ",\"reason\":" + Json(observation.Reason.ToString())
                    + ",\"detail\":" + Json(observation.Detail) + ",\"compatibility\":" + Json(observation.Compatibility)
                    + ",\"profile\":{\"name\":" + Json(observation.ProfileName) + ",\"version\":" + observation.ProfileVersion
                    + ",\"hash\":" + Json(profileHash == null ? null : BitConverter.ToString(profileHash).Replace("-", string.Empty).ToLowerInvariant()) + "}"
                    + ",\"identity\":" + identityJson + ",\"buildingId\":" + observation.BuildingId + ",\"frame\":" + observation.Frame
                    + ",\"phase\":" + Json(observation.Phase.ToString()) + ",\"category\":" + Json(observation.Category.ToString()) + ",\"nativeLevel\":" + observation.NativeLevel
                    + ",\"expectedHomeUnits\":" + observation.ExpectedHomeUnits + ",\"observedHomeUnits\":" + observation.ObservedHomeUnits
                    + ",\"inspectedUnits\":" + observation.InspectedUnits + ",\"capacityQ\":" + observation.CapacityQ
                    + ",\"capacityQMeaning\":\"Nominal profile quantity, not residents\",\"capacityControlEnabled\":false,\"controlScope\":\"Read-only observer call; session admission is reported separately\",\"gatesVerifiedOrganic\":false}";
            }

            private static string StockJson(ResidentialStockSnapshot stock)
            {
                if (stock == null) return "null";
                ResidentialStockTotals totals = stock.Totals;
                List<string> buckets = new List<string>();
                for (int cls = 0; cls <= 3; ++cls) for (int phase = 1; phase <= 4; ++phase)
                {
                    ResidentialStockTotals bucket = stock.GetBucket(cls, (ResidentialStockPhase)phase);
                    if (bucket.Lots != 0) buckets.Add("{\"class\":" + cls + ",\"phase\":" + Json(((ResidentialStockPhase)phase).ToString())
                        + ",\"lots\":" + bucket.Lots + ",\"homeUnits\":" + bucket.HomeUnits + ",\"settledPeople\":" + bucket.SettledPeople + ",\"incomingPeople\":" + bucket.IncomingPeople + "}");
                }
                return "{\"frame\":" + stock.Frame + ",\"lots\":" + totals.Lots + ",\"homeUnits\":" + totals.HomeUnits
                    + ",\"emptyHomeUnits\":" + totals.EmptyHomeUnits + ",\"liveHomeUnits\":" + totals.LiveHomeUnits + ",\"otherNonemptyHomeUnits\":" + totals.OtherNonemptyHomeUnits
                    + ",\"settledPeople\":" + totals.SettledPeople + ",\"incomingPeople\":" + totals.IncomingPeople
                    + ",\"peopleWithoutHomeUnitReference\":" + totals.PeopleWithoutHomeUnitReference + ",\"invalidMemberReferences\":" + totals.InvalidMemberReferences
                    + ",\"buckets\":[" + string.Join(",", buckets.ToArray()) + "]}";
            }

            public void RequestCleanup()
            {
                lock (_gate)
                {
                    if (_done || _cleanupQueued) return;
                    _interrupted = true;
                    lock (_errors) _optionalToolbarContext = null;
                    if (_owner.IsOrganicRoundtripMode() && _owner._roundtrip != null && !_owner._roundtrip.FixtureCleanupSafe) return;
                    if (_simulation == null)
                    {
                        _done = _cleaned = true; Detach();
                        return;
                    }
                    _cleanupQueued = true;
                    try
                    {
                        _simulation.AddAction(delegate
                        {
                            lock (_gate)
                            {
                                try { CleanupOwned(); _cleaned = true; }
                                catch (Exception error) { _failure = (_failure ?? string.Empty) + "\nOrganic cleanup: " + error; }
                                finally
                                {
                                    _done = true;
                                    bool noNativeErrors; lock (_errors) noNativeErrors = _errors.Count == 0;
                                    bool cancelledPopulated = _owner.IsOrganicRoundtripMode() && _cancelled;
                                    SetPhase(!_cleaned ? "CleanupIncomplete" : _verified && _failure == null && noNativeErrors && !cancelledPopulated ? "VerifiedAndReleased"
                                        : DiagnosticOnly && _failure == null && noNativeErrors ? "InventoryOnlyReleased" : "FailedOrCancelledReleased");
                                    Detach();
                                }
                            }
                        });
                    }
                    catch (Exception error) { _cleanupQueued = false; _failure = (_failure ?? string.Empty) + "\nCleanup enqueue: " + error; }
                }
            }
            // Synchronously interrupts pending acquisition under the same gate, then
            // queues release before native unload can enqueue foreign deserialization.
            public void BeforeLevelUnload() { try { if (!ConsumeOwnedReloadPermit()) RequestCleanup(); } catch { try { RequestCleanup(); } catch { } } }
            public void CancelBeforePluginRemoval() { try { RequestCleanup(); } catch { } }

            private void CleanupOwned()
            {
                try { CleanupOwnedResources(); }
                finally
                {
                    if (_speedHeld)
                    {
                        if (OwnsCity()) ReleaseSpeedLease();
                        else
                        {
                            _speedHeld = false;
                            Add("{\"phase\":\"SpeedLeaseOwnershipLost\",\"restored\":false,\"foreignStateChanged\":false}");
                        }
                    }
                }
            }
            private void CleanupOwnedResources()
            {
                if (_owner.IsOrganicRoundtripMode()) RequireCleanupOwner();
                if (_handoffReloadSeen && !_handoffAdopted)
                    throw new InvalidOperationException("Reloaded fixture was not explicitly adopted after exact restoration; stale cleanup is refused.");
                if (!OwnsCity())
                {
                    if (_speedHeld || _buildings.Count != 0 || _segments.Count != 0 || _nodes.Count != 0)
                        throw new InvalidOperationException("Owned resources lost their native session before ordered cleanup; no foreign mutation authorized.");
                    return;
                }
                BuildingManager buildings = Singleton<BuildingManager>.instance;
                // A timeout can fall between native growth and our next observation.
                // Baseline was residential-empty; inventory those new bounded homes now.
                for (int id = 1; id < buildings.m_buildings.m_buffer.Length; ++id)
                {
                    Building current = buildings.m_buildings.m_buffer[id];
                    if (_state >= 2 && (current.m_flags & (Building.Flags.Created | Building.Flags.Deleted)) == Building.Flags.Created
                        && current.Info != null && current.Info.m_buildingAI is ResidentialBuildingAI && Vector3.Distance(current.m_position, _head) <= 448f
                        && !_buildings.ContainsKey((ushort)id))
                        _buildings.Add((ushort)id, new BuildingStamp { Info = current.Info, Position = current.m_position, BuildIndex = current.m_buildIndex });
                }
                List<string> failures = new List<string>();
                try
                {
                    foreach (KeyValuePair<ushort, BuildingStamp> item in _buildings)
                    {
                        try
                        {
                            Building current = buildings.m_buildings.m_buffer[item.Key]; BuildingStamp stamp = item.Value;
                            if ((current.m_flags & Building.Flags.Created) != 0 && current.m_buildIndex == stamp.BuildIndex && current.Info == stamp.Info && current.m_position == stamp.Position)
                                buildings.ReleaseBuilding(item.Key);
                            else if ((current.m_flags & Building.Flags.Created) != 0) failures.Add("Building fingerprint changed: " + item.Key);
                            if ((buildings.m_buildings.m_buffer[item.Key].m_flags & Building.Flags.Created) != 0) failures.Add("Building remains live after cleanup: " + item.Key);
                        }
                        catch (Exception error) { failures.Add("Building " + item.Key + ": " + error.Message); }
                    }
                    NetManager net = Singleton<NetManager>.instance;
                    foreach (KeyValuePair<ushort, NetStamp> item in _segments)
                    {
                        try
                        {
                            NetSegment current = net.m_segments.m_buffer[item.Key];
                            if ((current.m_flags & NetSegment.Flags.Created) != 0 && current.Info == item.Value.Info && current.m_buildIndex == item.Value.BuildIndex
                                && current.m_startNode == item.Value.Start && current.m_endNode == item.Value.End) net.ReleaseSegment(item.Key, true);
                            else if ((current.m_flags & NetSegment.Flags.Created) != 0) failures.Add("Segment fingerprint changed: " + item.Key);
                            if ((net.m_segments.m_buffer[item.Key].m_flags & NetSegment.Flags.Created) != 0) failures.Add("Segment remains live after cleanup: " + item.Key);
                        }
                        catch (Exception error) { failures.Add("Segment " + item.Key + ": " + error.Message); }
                    }
                    foreach (KeyValuePair<ushort, NetStamp> item in _nodes)
                    {
                        try
                        {
                            NetNode current = net.m_nodes.m_buffer[item.Key];
                            if ((current.m_flags & NetNode.Flags.Created) != 0 && current.Info == item.Value.Info && current.m_buildIndex == item.Value.BuildIndex
                                && current.m_position == item.Value.Position && current.CountSegments() == 0) net.ReleaseNode(item.Key);
                            else if ((current.m_flags & NetNode.Flags.Created) != 0) failures.Add("Node fingerprint changed or still connected: " + item.Key);
                            if ((net.m_nodes.m_buffer[item.Key].m_flags & NetNode.Flags.Created) != 0) failures.Add("Node remains live after cleanup: " + item.Key);
                        }
                        catch (Exception error) { failures.Add("Node " + item.Key + ": " + error.Message); }
                    }
                }
                finally { ReleaseSpeedLease(); }
                if (failures.Count != 0) throw new InvalidOperationException(string.Join("; ", failures.ToArray()));
            }
            private void Detach() { lock (_errors) _optionalToolbarContext = null; if (_subscribed) { Application.logMessageReceivedThreaded -= OnLog; _subscribed = false; } }
            public string ReportJson()
            {
                lock (_gate) return "{\"phase\":" + Json(_phase) + ",\"diagnosticOnly\":" + Bool(DiagnosticOnly) + ",\"verifiedOrganic\":" + Bool(_verified)
                    + ",\"cleaned\":" + Bool(_cleaned) + ",\"pendingSimulationAction\":" + Bool(_pending) + ",\"speedLeaseHeld\":" + Bool(_speedHeld)
                    + ",\"constructionDebitInternal\":" + _constructionDebit + ",\"maximumConstructionDebitInternal\":" + MaximumConstructionDebit
                    + ",\"modalDiagnosticCount\":" + _modalDiagnosticCount + ",\"modalDiagnosticsCapped\":" + Bool(_modalDiagnosticsCapped)
                    + ",\"capacityObservationKnown\":" + Bool(_capacityObservationKnown) + ",\"capacityObservation\":" + _capacityObservation
                    + ",\"capacityAdmissionRequested\":" + Bool(_capacityModeRequested) + ",\"capacityAdmissionVerified\":" + Bool(_capacityAdmissionVerified)
                    + ",\"capacityAdmission\":" + _capacityAdmission
                    + ",\"saveReloadHandoff\":" + RoundtripHandoffJson()
                    + ",\"optionalToolbarDiagnosticSnapshot\":" + (_optionalToolbarSnapshot == null ? "null" : _optionalToolbarSnapshot.ReportJson())
                    + ",\"baselineOptionalToolbarDiagnostics\":" + OptionalToolbarDiagnosticsJson()
                    + ",\"driverCreatedGrowables\":false,\"driverCreatedCitizens\":false,\"driverAssignedHomes\":false,\"driverStartedTransfers\":false,\"demandOffsetsApplied\":false,\"rngReseeded\":false"
                    + ",\"error\":" + Json(_failure) + ",\"nativeErrors\":[" + ErrorsJson() + "],\"receipts\":[" + string.Join(",", _receipts.ToArray()) + "]}";
            }
            private string ErrorsJson() { lock (_errors) return JsonList(_errors); }
            private string OptionalToolbarDiagnosticsJson()
            {
                lock (_errors) return "{\"count\":" + _optionalToolbarDiagnosticCount + ",\"servicePointCount\":" + _optionalServicePointCount
                    + ",\"hotelCount\":" + _optionalHotelCount + ",\"raceCount\":" + _optionalRaceCount
                    + ",\"recordLimit\":24,\"recordsCapped\":" + Bool(_optionalToolbarDiagnosticCount > _baselineOptionalToolbarDiagnostics.Count)
                    + ",\"records\":[" + string.Join(",", _baselineOptionalToolbarDiagnostics.ToArray()) + "]}";
            }
            private static int FloatBits(float value) { return BitConverter.ToInt32(BitConverter.GetBytes(value), 0); }
            private static string PositionJson(Vector3 value) { return "{\"x\":" + JsonFloat(value.x) + ",\"y\":" + JsonFloat(value.y) + ",\"z\":" + JsonFloat(value.z) + "}"; }
            private static string QuadJson(Quad2 value) { return "[" + PointJson(value.a) + "," + PointJson(value.b) + "," + PointJson(value.c) + "," + PointJson(value.d) + "]"; }
            private static string PointJson(Vector2 value) { return "{\"x\":" + JsonFloat(value.x) + ",\"z\":" + JsonFloat(value.y) + "}"; }
            private static string PlacementJson(Placement value) { return "{\"prefab\":" + Json(value.Info.name) + ",\"inputPosition\":" + PositionJson(value.InputPosition) + ",\"position\":" + PositionJson(value.Position) + ",\"angle\":" + JsonFloat(value.Angle) + ",\"cellWidth\":" + value.Info.m_cellWidth + ",\"cellLength\":" + value.Info.m_cellLength + ",\"circular\":" + Bool(value.Info.m_circular) + ",\"nativeFootprint\":" + QuadJson(ServiceFootprint(value, 0f, false)) + ",\"reservedFootprintMargin8\":" + QuadJson(ServiceFootprint(value, 8f, true)) + ",\"shorelineBranch\":" + Bool(value.Shoreline) + ",\"nativeWaterHeight\":" + JsonFloat(value.WaterHeight) + ",\"costInternal\":" + value.Cost + ",\"productionEstimate\":" + value.Production + "}"; }
        }
    }
}
