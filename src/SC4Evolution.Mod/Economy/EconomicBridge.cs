using System;
using System.Collections.Generic;
using System.IO;
using ColossalFramework;
using SC4Evolution.Core;
using SC4Evolution.Core.Decisions;
using SC4Evolution.Mod.Runtime;
using SC4Evolution.Simulation;
using SC4Evolution.Simulation.Neighborhoods;

namespace SC4Evolution.Mod.Economy
{
    // A per-call configuration snapshot and optional provider-initialization
    // receipt. Neither one grants economic authority; certification still does.
    internal sealed class EconomicStartPreparation
    {
        internal readonly string RootSnapshot, FailureReason;
        internal readonly int Stage;
        internal readonly ResidentialSelectionPolicyId Policy;
        internal readonly ResidentialCapacityProviderActivation Activation;
        internal readonly bool ProviderReady, CivicFundingEnabled;
        internal readonly GameplaySprint GameplaySprints;
        internal bool RawOptIn { get { return Stage == 3; } }
        internal bool ConfigurationValid { get { return Stage >= 0; } }
        internal EconomicStartPreparation(string root, int stage, ResidentialSelectionPolicyId policy,
            ResidentialCapacityProviderActivation activation, string failure, bool providerReady = false, bool civicFundingEnabled = false,
            GameplaySprint gameplaySprints = GameplaySprint.None)
        { RootSnapshot = root; Stage = stage; Policy = policy; Activation = activation; FailureReason = failure; ProviderReady = providerReady; CivicFundingEnabled = civicFundingEnabled; GameplaySprints = gameplaySprints; }
    }

    public sealed class SegmentedDemandSnapshot
    {
        private readonly DemandNodeState[] _nodes;
        public CityCensus Census { get; private set; }
        public bool Converged { get; private set; }
        public bool GameplayEnabled { get; private set; }
        public int PropagationPasses { get; private set; }
        public DemandNodeState[] Nodes { get { return (DemandNodeState[])_nodes.Clone(); } }
        internal SegmentedDemandSnapshot(CityCensus census, DemandNodeState[] nodes, bool converged, bool gameplay, int passes)
        { Census = census; _nodes = (DemandNodeState[])nodes.Clone(); Converged = converged; GameplayEnabled = gameplay; PropagationPasses = passes; }
        public bool TryGetDemand(uint key, out int normalized)
        {
            foreach (DemandNodeState node in _nodes)
                if (node.Key == key) { normalized = node.NormalizedDemand; return true; }
            normalized = 0; return false;
        }
        public bool TryGetActiveDemand(uint key, out float raw)
        {
            foreach (DemandNodeState node in _nodes)
                if (node.Key == key) { raw = node.ActiveDemand; return !float.IsNaN(raw) && !float.IsInfinity(raw); }
            raw = 0; return false;
        }
    }

    /// <summary>Session-owned bridge. SampleIfDue must be called on CS1's simulation thread.</summary>
    public sealed partial class EconomicBridge
    {
        private static readonly uint[] BusinessKeys = { 0x3110, 0x3120, 0x3130, 0x3320, 0x3330, 0x4100, 0x4200, 0x4300, 0x4400 };
        private readonly object _sync = new object();
        private DemandGraph _graph;
        private DemandProfile _profile;
        private readonly Dictionary<uint, float> _previous = new Dictionary<uint, float>();
        private volatile SegmentedDemandSnapshot _published;
        private volatile string _status = "Stopped; native gameplay active.";
        private volatile bool _initialComplete;
        private bool _running, _sampled, _aggregate, _gateInstalled, _lotClass, _classPatchesInstalled, _metadataPatchesInstalled;
        private bool _incomingPatchesInstalled;
        private GameplayIntegrationSession _gameplay;
        private volatile GameplayIntegrationSnapshot _publishedGameplay;
        private ShadowTaxLedger _shadowTax;
        private ShadowAccessFreight _shadowAccess;
        private volatile ShadowTaxResult _publishedShadowTax;
        private ShadowPressure _publishedShadowAccess;
        private ShadowPressure _publishedShadowFreight;
        private volatile bool _shadowAccessObserved, _shadowFreightObserved;
        private volatile IncomingDemandBinding _demandContextBinding;
        private byte[] _pendingClassState;
        private string _pendingClassFailure;
        private ResidentialClassBridge _classes;
        private uint _lastFrame;
        private volatile int _session;
        public SegmentedDemandSnapshot Published { get { return _published; } }
        public string Status { get { return _status; } }
        public bool InitialSampleComplete { get { return _initialComplete; } }
        public ResidentialClassStatus PublishedClassStatus { get { ResidentialClassBridge classes = _classes; return classes == null ? null : classes.Published; } }
        public GameplayIntegrationSnapshot PublishedGameplay { get { return _publishedGameplay; } }
        public ShadowTaxResult PublishedShadowTax { get { return _publishedShadowTax; } }
        public ShadowPressure PublishedShadowAccess { get { return _publishedShadowAccess; } }
        public ShadowPressure PublishedShadowFreight { get { return _publishedShadowFreight; } }
        public bool ShadowTaxationEnabled { get { return _shadowTax != null; } }
        public bool ShadowAccessFreightEnabled { get { return _shadowAccess != null; } }
        public bool ShadowAccessObserved { get { return _shadowAccessObserved; } }
        public bool ShadowFreightObserved { get { return _shadowFreightObserved; } }
        internal ResidentialClassBridge Classes { get { return _classes; } }
        internal bool IsLotClassStage { get { return _lotClass; } }
        internal int DemandContextSession { get { return _session; } }
        internal IncomingDemandBinding DemandContextBinding { get { return _demandContextBinding; } }
        internal IncomingDemandSnapshot IncomingDemandDiagnostics
        { get { IncomingDemandBinding binding = _demandContextBinding; return binding == null ? null : binding.Snapshot(); } }
        internal bool CanUseDemand(SegmentedDemandSnapshot snapshot)
        {
            if (!_running || snapshot == null || !ReferenceEquals(snapshot, _published) || !snapshot.Converged || !snapshot.GameplayEnabled) return false;
            if (_capacityModeRequested && (_capacity == null || !_capacity.Ready)) return false;
            return !_lotClass || (_classes != null && _classes.EffectsEnabled && Singleton<SimulationManager>.exists &&
                unchecked(Singleton<SimulationManager>.instance.m_currentFrameIndex - snapshot.Census.Frame) <= 2048u);
        }

        public void SetLoadedClassState(byte[] state)
        {
            if (state != null && state.Length > 16 * 1024 * 1024) throw new InvalidDataException("Residential class payload exceeds limit.");
            lock (_sync)
            {
                if (_running) throw new InvalidOperationException("Load class state before starting a city session.");
                _pendingClassState = state == null ? null : (byte[])state.Clone();
                _pendingClassFailure = null;
            }
        }
        public void SetClassLoadFailure(string reason)
        {
            lock (_sync)
            {
                if (_running) throw new InvalidOperationException("Report class load failure before starting a city session.");
                _pendingClassState = null;
                _pendingClassFailure = string.IsNullOrEmpty(reason) ? "Residential class save could not be loaded." : reason;
            }
        }
        public byte[] GetClassSaveState()
        { lock (_sync) { return _classes == null ? null : _classes.Save(); } }

        // This explicit preparation runs before the runtime and economic owner
        // locks. Official provider constructors may reenter managed callbacks.
        // Parse once: Start consumes this exact stage/policy, even if the file
        // changes between preparation and entering the runtime session lock.
        internal static EconomicStartPreparation PrepareStart(string root)
        {
            string normalized = null;
            int stage = -1;
            bool providerReady = false;
            bool civicFundingEnabled = false;
            GameplaySprint gameplaySprints = GameplaySprint.None;
            ResidentialSelectionPolicyId policy = ResidentialSelectionPolicyId.Unknown;
            try
            {
                if (string.IsNullOrEmpty(root)) throw new InvalidDataException("Project root unavailable.");
                normalized = Path.GetFullPath(root);
                stage = ReadStage(Path.Combine(normalized, "config/economic-bridge.config"), out policy, out civicFundingEnabled, out gameplaySprints);
                if (stage == 0) return new EconomicStartPreparation(normalized, stage, policy, null, null, false, civicFundingEnabled, gameplaySprints);
                // IsHarmonyInstalled invokes Installer.Run, whose constructor can
                // initialize Harmony and RuntimeDetour. Never defer this to Start
                // or a retained-ledger branch under any owner lock.
                providerReady = GrowthGateDependency.Available();
                if (!providerReady) return new EconomicStartPreparation(normalized, stage, policy, null,
                    "Official CitiesHarmony provider readiness preparation failed.", false, civicFundingEnabled, gameplaySprints);
                if (stage != 3) return new EconomicStartPreparation(normalized, stage, policy, null, null, true, civicFundingEnabled, gameplaySprints);
                ResidentialCapacityProviderActivation activation;
                string reason;
                bool prepared = ResidentialCapacityCompatibility.TryActivateOfficialProviderCopies(out activation, out reason);
                if (!prepared || activation == null)
                    return new EconomicStartPreparation(normalized, stage, policy, null,
                        string.IsNullOrEmpty(reason) ? "Official provider preparation did not return a receipt." : reason, providerReady, civicFundingEnabled, gameplaySprints);
                return new EconomicStartPreparation(normalized, stage, policy, activation, null, providerReady, civicFundingEnabled, gameplaySprints);
            }
            catch (Exception error)
            { return new EconomicStartPreparation(normalized, stage, policy, null, error.ToString(), providerReady, civicFundingEnabled, gameplaySprints); }
        }

        // Legacy callers do not implicitly initialize any provider copy.
        public void Start(string root) { Start(root, null); }
        internal void Start(string root, EconomicStartPreparation preparation)
        {
            lock (_sync)
            {
                byte[] loadedClasses = _pendingClassState;
                string classFailure = _pendingClassFailure;
                byte[] loadedCapacity = _pendingCapacityState;
                string capacityFailure = _pendingCapacityFailure;
                bool newCapacityCity = _pendingNewCapacityCity;
                string restorationOnly = null;
                string activationStep = "retained class decode";
                // This is readiness evidence, not economic authority. A receipt
                // still has to pass live certification after all hooks install.
                // Validate before even the optional retained metadata branch.
                bool providerPrepared = false;
                try
                {
                    providerPrepared = preparation != null && preparation.ConfigurationValid && preparation.ProviderReady &&
                        preparation.FailureReason == null && (!preparation.RawOptIn || preparation.Activation != null) &&
                        string.Equals(preparation.RootSnapshot, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
                }
                catch { }
                _pendingClassState = null;
                StopLocked();
                _running = true;
                _classes = new ResidentialClassBridge(this, loadedClasses, false, classFailure);
                if (loadedClasses != null && classFailure == null)
                {
                    ResidentialClassLedger retained = null;
                    try
                    {
                        // Pure decode precedes optional dependency access. Fresh Shadow
                        // never requires or queries Harmony just to observe a city.
                        using (MemoryStream stream = new MemoryStream(loadedClasses, false)) retained = ResidentialClassLedgerCodec.Read(stream);
                    }
                    catch (Exception error)
                    {
                        try { Runtime.ModRuntime.Log("EconomicBridge.Start failed at " + activationStep + "; session=" + _session + Environment.NewLine + error.ToString()); } catch { }
                        classFailure = "Retained class ledger is invalid: " + error.Message; _classes.RejectBinding(classFailure);
                    }
                    if (retained != null && retained.Count != 0)
                        try
                        {
                            activationStep = "retained metadata provider check";
                            if (!providerPrepared) throw new InvalidOperationException("Provider readiness was not prepared outside owner locks; retained ledger is restoration-only: " +
                                (preparation == null ? "No preparation was supplied." : preparation.FailureReason ?? "No usable readiness receipt was supplied."));
                            activationStep = "retained metadata hook installation";
                            ResidentialClassPatchDependency.InstallMetadata(this); _metadataPatchesInstalled = true;
                        }
                        catch (Exception error)
                        {
                            try { Runtime.ModRuntime.Log("EconomicBridge.Start failed at " + activationStep + "; session=" + _session + Environment.NewLine + error.ToString()); } catch { }
                            restorationOnly = error.Message; _classes.RequireRestorationOnly(restorationOnly);
                        }
                }
                try
                {
                    activationStep = "project root validation";
                    if (string.IsNullOrEmpty(root)) throw new InvalidDataException("Project root unavailable.");
                    string control = Path.Combine(root, "config/economic-bridge.config");
                    ResidentialSelectionPolicyId configuredPolicy;
                    activationStep = "economic stage configuration read";
                    int stage;
                    bool ignoredCivicFunding; GameplaySprint gameplaySprints;
                    if (preparation == null) stage = ReadStage(control, out configuredPolicy, out ignoredCivicFunding, out gameplaySprints);
                    else
                    {
                        if (!preparation.ConfigurationValid) throw new InvalidDataException("Economic preparation rejected the configuration: " + preparation.FailureReason);
                        if (!string.Equals(preparation.RootSnapshot, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Economic preparation belongs to a different project root.");
                        stage = preparation.Stage; configuredPolicy = preparation.Policy; gameplaySprints = preparation.GameplaySprints;
                    }
                    _lotClass = stage >= 2; _aggregate = stage != 0;
                    _capacityModeRequested = stage == 3;
                    if (_lotClass && (classFailure != null || restorationOnly != null)) _aggregate = false;
                    activationStep = "residential class adapter initialization";
                    _classes = new ResidentialClassBridge(this, loadedClasses, _lotClass, classFailure);
                    if (restorationOnly != null) _classes.RequireRestorationOnly(restorationOnly);
                    activationStep = "residential water profile load";
                    ResidentialWaterObserver water = configuredPolicy == ResidentialSelectionPolicyId.SC4WaterPrerequisiteV1 || configuredPolicy == ResidentialSelectionPolicyId.SC4WaterRawCapacityV1 ? ResidentialWaterObserver.Load(root) : null;
                    _classes.ConfigureSelectionPolicy(configuredPolicy, water);
                    _gameplay = gameplaySprints == GameplaySprint.None ? null
                        : new GameplayIntegrationSession(cityIdentity: GetGameplayCityIdentity(),
                            config: new GameplayIntegrationConfig(gameplaySprints));
                    _publishedGameplay = null;
                    _shadowTax = (gameplaySprints & GameplaySprint.ShadowTaxation) != GameplaySprint.None
                        ? new ShadowTaxLedger() : null;
                    _shadowAccess = (gameplaySprints & GameplaySprint.ShadowAccessFreight) != GameplaySprint.None
                        ? new ShadowAccessFreight() : null;
                    _publishedShadowTax = null;
                    _publishedShadowAccess = new ShadowPressure();
                    _publishedShadowFreight = new ShadowPressure();
                    _shadowAccessObserved = false;
                    _shadowFreightObserved = false;
                    if (_lotClass && water != null && !water.IsValid) _aggregate = false;
                    activationStep = "residential capacity adapter initialization";
                    _capacity = new ResidentialCapacityBridge(this, _capacityModeRequested, _session, newCapacityCity,
                        loadedCapacity, loadedClasses, capacityFailure ?? classFailure ?? restorationOnly, root, water,
                        preparation == null ? null : preparation.Activation, preparation == null ? null : preparation.FailureReason);
                    string path = Path.Combine(root, "analysis/generated/sc4-demand-profile/demand.sc4e");
                    activationStep = "demand profile validation and decode";
                    if (!File.Exists(path)) throw new InvalidDataException("Extracted demand profile missing.");
                    if (new FileInfo(path).Length > 16 * 1024 * 1024 + 16) throw new InvalidDataException("Demand profile exceeds size limit.");
                    using (FileStream stream = File.OpenRead(path)) _profile = DemandProfileCodec.Read(stream);
                    ValidateAdapterKeys(_profile);
                    activationStep = "demand graph initialization";
                    _graph = new DemandGraph(_profile);
                    if (_aggregate || loadedCapacity != null)
                    {
                        activationStep = "prepared CitiesHarmony readiness validation";
                        if (!providerPrepared) throw new InvalidOperationException("Official CitiesHarmony readiness was not prepared outside owner locks; economic hooks disabled: " +
                            (preparation == null ? "No preparation was supplied." : preparation.FailureReason ?? "No usable readiness receipt was supplied."));
                        activationStep = "incoming residential demand scope installation";
                        IncomingDemandDependency.Install(this); _incomingPatchesInstalled = true;
                        activationStep = "ZoneBlock growth gate installation";
                        GrowthGateDependency.Install(this); _gateInstalled = true;
                        activationStep = "residential class and capacity hook installation";
                        if (_lotClass || loadedCapacity != null) { ResidentialClassPatchDependency.Install(this); _classPatchesInstalled = true; }
                        activationStep = "residential demand session binding";
                        if (_aggregate) _demandContextBinding = new IncomingDemandBinding(this, _session);
                        activationStep = "capacity physical-hook verification and effective-patch certification";
                        if (_capacityModeRequested || loadedCapacity != null)
                            _capacity.ConfirmHookSet(_session, GrowthGateDependency.CapacityHooksInstalled(this) && ResidentialClassPatchDependency.CapacityHooksInstalled(this), "Required native capacity patch set is incomplete.");
                    }
                    _status = (_aggregate ? "Aggregate development experiment" : "Shadow census/graph") + "; awaiting simulation-thread sample; bootstrap classes only.";
                }
                catch (Exception error)
                {
                    // Exception.ToString retains nested reflection/Harmony failures
                    // and stacks. Logging must never interrupt the existing fallback.
                    try { Runtime.ModRuntime.Log("EconomicBridge.Start failed at " + activationStep + "; session=" + _session + Environment.NewLine + error.ToString()); } catch { }
                    _graph = null; _aggregate = false;
                    if (_capacity == null) _capacity = new ResidentialCapacityBridge(this, _capacityModeRequested, _session, newCapacityCity, loadedCapacity, loadedClasses, capacityFailure ?? classFailure ?? restorationOnly, root, null);
                    _capacity.Suspend("Capacity activation unavailable: " + error.Message + "; step=" + activationStep);
                    IncomingDemandBinding failedBinding = _demandContextBinding; _demandContextBinding = null;
                    if (failedBinding != null) failedBinding.Invalidate();
                    if (_classes != null) _classes.Disable("Class activation failed: " + error.Message);
                    if (_classPatchesInstalled) { ResidentialClassPatchDependency.Remove(this); _classPatchesInstalled = false; }
                    if (_gateInstalled) { GrowthGateDependency.Remove(this); _gateInstalled = false; }
                    if (_incomingPatchesInstalled) { IncomingDemandDependency.Remove(this); _incomingPatchesInstalled = false; }
                    _status = "Native gameplay active; graph disabled: " + error.Message + "; step=" + activationStep;
                }
                EconomicDemandExtension.Bind(this);
                if (Singleton<SimulationManager>.exists)
                {
                    int session = _session;
                    Singleton<SimulationManager>.instance.AddAction(delegate
                    {
                        InitialSample(session);
                    });
                }
            }
        }

        private void InitialSample(int session)
        {
            lock (_sync)
            {
                if (!_running || session != _session) return;
                try
                {
                    if (_classes != null && !_classes.Maintain(4096))
                    {
                        Singleton<SimulationManager>.instance.AddAction(delegate { InitialSample(session); });
                        return;
                    }
                    if (_capacity != null && !_capacity.Maintain(4096))
                    { Singleton<SimulationManager>.instance.AddAction(delegate { InitialSample(session); }); return; }
                    SampleLocked();
                    _initialComplete = true;
                }
                catch (Exception error)
                {
                    if (_classes != null) _classes.RejectBinding("Initial class binding rejected: " + error.Message);
                    _published = null; _graph = null; _aggregate = false;
                    _status = "Native gameplay active; initial class binding failed: " + error.Message;
                    _initialComplete = true;
                }
            }
        }

        public void Stop() { lock (_sync) { StopLocked(); } }
        private void StopLocked()
        {
            IncomingDemandBinding stoppedBinding = _demandContextBinding;
            if (stoppedBinding != null) stoppedBinding.Invalidate();
            _demandContextBinding = null;
            ResidentialCapacityBridge stoppedCapacity = _capacity; _capacity = null;
            if (stoppedCapacity != null) stoppedCapacity.Stop();
            _capacityModeRequested = false; _pendingCapacityState = null; _pendingCapacityFailure = null; _pendingNewCapacityCity = false;
            _externalInputs = null; _persistenceFailure = null;
            _running = false; _session++; _sampled = false; _lastFrame = 0;
            _initialComplete = false;
            _published = null; _publishedGameplay = null; _gameplay = null; _graph = null; _profile = null; _previous.Clear(); _aggregate = false; _lotClass = false;
            _shadowTax = null; _shadowAccess = null; _publishedShadowTax = null;
            _publishedShadowAccess = new ShadowPressure(); _publishedShadowFreight = new ShadowPressure();
            _shadowAccessObserved = false; _shadowFreightObserved = false;
            _pendingClassState = null;
            _pendingClassFailure = null;
            ResidentialClassBridge stoppedClasses = _classes; _classes = null;
            if (stoppedClasses != null) stoppedClasses.Disable("Session stopped");
            _status = "Stopped; native gameplay active.";
            EconomicDemandExtension.Unbind(this);
            if (_incomingPatchesInstalled)
            {
                _incomingPatchesInstalled = false;
                try { IncomingDemandDependency.Remove(this); }
                catch (Exception error) { _status = "Native residential demand retained; context hook removal failed: " + error.Message; }
            }
            if (_classPatchesInstalled)
            {
                _classPatchesInstalled = false;
                try { ResidentialClassPatchDependency.Remove(this); }
                catch (Exception error) { _status = "Class effects disabled; owned patch removal failed: " + error.Message; }
            }
            if (_metadataPatchesInstalled)
            {
                _metadataPatchesInstalled = false;
                try { ResidentialClassPatchDependency.RemoveMetadata(this); }
                catch (Exception error) { _status = "Metadata effects disabled; identity hook removal failed: " + error.Message; }
            }
            if (_gateInstalled)
            {
                _gateInstalled = false;
                try { GrowthGateDependency.Remove(this); }
                catch (Exception error) { _status = "Stopped; economic decisions disabled; gate removal failed: " + error.Message; }
            }
        }

        public void SampleIfDue()
        {
            lock (_sync)
            {
                if (!_running || !Singleton<SimulationManager>.exists) return;
                if (_capacity != null && !_capacity.Maintain(4096)) return;
                if (_classes != null)
                {
                    try { if (!_classes.Maintain(4096)) return; }
                    catch (Exception error) { _classes.RejectBinding("Class binding/reconciliation rejected: " + error.Message); _graph = null; _aggregate = false; _status = "Native gameplay active; class binding failed: " + error.Message; return; }
                }
                uint frame = Singleton<SimulationManager>.instance.m_currentFrameIndex;
                if (_sampled && unchecked(frame - _lastFrame) < 1024u) return;
                SampleLocked();
            }
        }

        private void SampleLocked()
        {
            if (!_running) return;
            try
            {
                if (_classes != null && !_classes.Maintain(4096)) return;
                if (_classes != null) _classes.Audit();
                _lastFrame = Singleton<SimulationManager>.instance.m_currentFrameIndex; _sampled = true;
                CityCensus census = CityCensus.Capture(_classes);
                if (_gameplay != null)
                {
                    try { _publishedGameplay = _gameplay.Apply(BuildGameplayObservation(census)); }
                    catch (Exception error)
                    {
                        _publishedGameplay = null;
                        _status = "Native gameplay active; sprint integration sample rejected: " + error.Message;
                    }
                }
                if (_capacityModeRequested && _capacity != null && _capacity.SupplyQ == null)
                {
                    _published = new SegmentedDemandSnapshot(census, new DemandNodeState[0], false, false, 0);
                    _status = _capacity.Published.Status; return;
                }
                if (_graph == null)
                {
                    _published = new SegmentedDemandSnapshot(census, new DemandNodeState[0], false, false, 0);
                    return;
                }
                ApplyCensus(census);
                DemandPropagationResult result = _graph.Propagate(128);
                if (!result.Converged)
                {
                    _published = new SegmentedDemandSnapshot(census, _graph.GetAllStates(), false, false, result.Passes);
                    _graph = null;
                    if (_classes != null) _classes.Disable("Graph failed to converge; native gameplay retained");
                    _status = "Native gameplay active; graph did not converge within 128 passes. Restart the level after correcting the adapter/profile.";
                    return;
                }
                bool gameplay = _aggregate && (!_lotClass || (_classes != null && _classes.EffectsEnabled));
                if (_capacityModeRequested && _capacity != null) { _capacity.AcknowledgeGraph(); gameplay = _aggregate && _capacity.Ready; }
                _published = new SegmentedDemandSnapshot(census, _graph.GetAllStates(), true, gameplay, result.Passes);
                ResidentialClassStatus classes = _classes == null ? null : _classes.Published;
                _status = (_lotClass ? "Residential lot-class experiment; configured=" + classes.ConfiguredPolicy + "; retained=" + classes.RetainedPolicy + "; effective=" + classes.EffectivePolicy : _aggregate ? "Aggregate development experiment active" : "Shadow census/graph active; native gameplay retained") +
                    (_lotClass ? "; imported lots bootstrap; selected lot class independent of native level" : "; native wealth/level bootstrap") +
                    "; household capacity is not person capacity; taxes/economy/regional adapters not applied.";
                if (_capacityModeRequested) _status = _capacity.Published.Status + "; nominal stock Q/4 is separate from census people; global BDS resource pool is not implemented.";
                if (_persistenceFailure != null) _status = _persistenceFailure;
            }
            catch (Exception error)
            {
                // No stale valid snapshot may continue controlling development after a failed census.
                _published = null; _graph = null;
                if (_capacity != null) _capacity.Suspend("Economic graph sample failed: " + error.Message);
                if (_classes != null) _classes.Disable("Economic sample failed; native gameplay retained");
                _status = "Native gameplay active; economic sample failed: " + error.Message;
            }
        }

        private void ApplyCensus(CityCensus census)
        {
            int[] people = _lotClass ? census.ResidentsByLotClass : census.ResidentsByBootstrapWealth;
            int[] workers = _lotClass ? census.WorkforceByLotClassEducation : census.WorkforceByBootstrapWealthEducation;
            int[] capacity = census.JobCapacityBySegment, filled = census.FilledJobsBySegment;
            if (_capacityModeRequested && _capacity != null)
            {
                if (ExternalCensusChanged(people, workers, capacity, filled)) _capacity.ExternalInputsChanged();
            }
            long[] nominal = _capacityModeRequested && _capacity != null ? _capacity.SupplyQ : null;
            for (int wealth = 0; wealth < 3; wealth++)
            {
                // No invented persons-per-home conversion: occupied-person baseline only.
                Supply((uint)(0x1010 + wealth * 0x10), nominal == null ? people[wealth] : ExactNominalSupply(nominal[wealth]));
                Census((uint)(0x1011 + wealth * 0x10), people[wealth]);
                for (int education = 0; education < 4; education++)
                    Census((uint)(0x2111 + wealth * 0x10 + education), workers[wealth * 4 + education]);
            }
            for (int segment = 0; segment < BusinessKeys.Length; segment++)
            {
                Supply(BusinessKeys[segment], capacity[segment]);
                Census(BusinessKeys[segment] + 1, filled[segment]);
            }
        }

        private void Supply(uint key, float value)
        {
            float previous; _previous.TryGetValue(key, out previous);
            if (value != previous) _graph.QueueSupplyChange(key, value - previous);
            _previous[key] = value;
        }
        private void Census(uint key, float value)
        {
            float previous; _previous.TryGetValue(key, out previous);
            float delta = value - previous;
            if (delta != 0)
            {
                foreach (DemandNodeDefinition node in _profile.Nodes)
                    if (node.Key == key)
                    {
                        if (node.RegionUse == 1) _graph.QueueSupplyChange(key, delta);
                        else if (node.RegionUse == 2) _graph.QueueDemandChange(key, delta);
                        break;
                    }
            }
            _previous[key] = value;
        }

        private static void ValidateAdapterKeys(DemandProfile profile)
        {
            Dictionary<uint, bool> keys = new Dictionary<uint, bool>();
            foreach (DemandNodeDefinition node in profile.Nodes) keys.Add(node.Key, true);
            for (int wealth = 0; wealth < 3; wealth++)
            {
                RequireKey(keys, (uint)(0x1010 + wealth * 0x10)); RequireKey(keys, (uint)(0x1011 + wealth * 0x10));
                RequireKey(keys, (uint)(0x21010 + wealth * 0x10));
                for (int education = 0; education < 4; education++) RequireKey(keys, (uint)(0x2111 + wealth * 0x10 + education));
            }
            foreach (uint key in BusinessKeys) { RequireKey(keys, key); RequireKey(keys, key + 1); RequireKey(keys, key + 0x20000); }
        }
        private static void RequireKey(Dictionary<uint, bool> keys, uint key)
        { if (!keys.ContainsKey(key)) throw new InvalidDataException("Profile lacks adapter key 0x" + key.ToString("X") + "."); }

        private static int ReadStage(string path, out ResidentialSelectionPolicyId configuredPolicy, out bool civicFundingEnabled, out GameplaySprint gameplaySprints)
        {
            configuredPolicy = ResidentialSelectionPolicyId.Unknown;
            civicFundingEnabled = false;
            gameplaySprints = GameplaySprint.None;
            if (!File.Exists(path)) return 0;
            if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Economic bridge control exceeds size limit.");
            string[] lines = File.ReadAllLines(path);
            string stage = null, policy = null, civic = null, gameplay = null;
            foreach (string source in lines)
            {
                string line = source.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (line.StartsWith("Stage=", StringComparison.Ordinal) && stage == null) stage = line.Substring(6).Trim();
                else if (line.StartsWith("LocalPolicy=", StringComparison.Ordinal) && policy == null) policy = line.Substring(12).Trim();
                else if (line.StartsWith("CivicFunding=", StringComparison.Ordinal) && civic == null) civic = line.Substring(13).Trim();
                else if (line.StartsWith("Gameplay=", StringComparison.Ordinal) && gameplay == null) gameplay = line.Substring(9).Trim();
                else throw new InvalidDataException("Expected unique Stage and optional LocalPolicy/CivicFunding/Gameplay settings.");
            }
            if (civic != null && civic != "SC4LocalV1") throw new InvalidDataException("Unknown civic funding policy: " + civic);
            civicFundingEnabled = civic == "SC4LocalV1";
            gameplaySprints = ParseGameplaySprints(gameplay);
            if (stage == "lot-class")
            {
                if (policy == "NativeSafetyOnly") configuredPolicy = ResidentialSelectionPolicyId.NativeSafetyOnlyV1;
                else if (policy == "SC4WaterPrerequisiteV1") configuredPolicy = ResidentialSelectionPolicyId.SC4WaterPrerequisiteV1;
                else throw new InvalidDataException("Lot-class stage requires explicit LocalPolicy=NativeSafetyOnly or SC4WaterPrerequisiteV1.");
                return 2;
            }
            if (stage == "capacity-admission")
            {
                if (policy != "SC4WaterRawCapacityV1") throw new InvalidDataException("Capacity admission requires explicit LocalPolicy=SC4WaterRawCapacityV1.");
                configuredPolicy = ResidentialSelectionPolicyId.SC4WaterRawCapacityV1; return 3;
            }
            if (policy != null) throw new InvalidDataException("LocalPolicy is only accepted for Stage=lot-class.");
            if (stage == null || stage == "shadow") return 0;
            if (stage == "aggregate-demand") return 1;
            throw new InvalidDataException("Unknown economic bridge stage: " + stage);
        }

        private static GameplaySprint ParseGameplaySprints(string value)
        {
            if (string.IsNullOrEmpty(value) || value == "Shadow") return GameplaySprint.None;
            GameplaySprint result = GameplaySprint.None;
            string[] parts = value.Split(new[] { ',', ';', '+' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                GameplaySprint flag;
                if (part == "All") flag = GameplaySprint.Employment | GameplaySprint.Neighborhoods | GameplaySprint.Taxation | GameplaySprint.AccessFreight | GameplaySprint.Decisions;
                else if (part == "Employment") flag = GameplaySprint.Employment;
                else if (part == "Neighborhoods") flag = GameplaySprint.Neighborhoods;
                else if (part == "Taxation") flag = GameplaySprint.Taxation;
                else if (part == "AccessFreight") flag = GameplaySprint.AccessFreight;
                else if (part == "Decisions") flag = GameplaySprint.Decisions;
                else if (part == "ShadowTaxation") flag = GameplaySprint.ShadowTaxation;
                else if (part == "ShadowAccessFreight") flag = GameplaySprint.ShadowAccessFreight;
                else throw new InvalidDataException("Unknown Gameplay sprint: " + part);
                if ((result & flag) != GameplaySprint.None) throw new InvalidDataException("Duplicate Gameplay sprint: " + part);
                result |= flag;
            }
            return result;
        }

        private GameplayIntegrationObservation BuildGameplayObservation(CityCensus census)
        {
            int availableHomes = 0;
            ResidentialStockLot[] stockLots = census.ResidentialStock == null ? new ResidentialStockLot[0] : census.ResidentialStock.Lots;
            GameplayNeighborhoodLotObservation[] lots = new GameplayNeighborhoodLotObservation[stockLots.Length];
            for (int i = 0; i < stockLots.Length; i++)
            {
                ResidentialStockLot lot = stockLots[i];
                availableHomes = checked(availableHomes + lot.EmptyHomeUnits);
                lots[i] = new GameplayNeighborhoodLotObservation(lot.BuildingId, lot.ObservedBuildIndex, lot.LotClass,
                    lot.Phase, lot.HomeUnits, lot.EmptyHomeUnits, lot.LiveHomeUnits, lot.OtherNonemptyHomeUnits,
                    lot.SettledPeople, lot.IncomingPeople, lot.PeopleWithoutHomeUnitReference, lot.InvalidMemberReferences,
                    NeighborhoodFactors.Neutral());
            }
            GameplayEmploymentObservation employment = new GameplayEmploymentObservation(
                census.WorkforceByLotClassEducation, census.JobCapacityBySegment, census.FilledJobsBySegment,
                census.UnmappedJobCapacity, census.UnmappedFilledJobs, availableHomes, false);
            GameplayNeighborhoodObservation neighborhoods = new GameplayNeighborhoodObservation(lots, new[] { 0, 0, 0 });
            string cityIdentity = GetGameplayCityIdentity();
            GameplaySprint enabled = _gameplay == null ? GameplaySprint.None : _gameplay.EnabledSprints;
            bool needsCitySnapshot = (enabled & (GameplaySprint.Decisions | GameplaySprint.ShadowTaxation |
                GameplaySprint.ShadowAccessFreight)) != GameplaySprint.None;
            CitySnapshot city = needsCitySnapshot ? SnapshotReader.Capture() : null;
            UpdateShadowObservations(city, census);
            GameplayDecisionObservation[] decisions = _gameplay != null &&
                (enabled & GameplaySprint.Decisions) != GameplaySprint.None
                ? BuildNativeDecisionObservations(city) : new GameplayDecisionObservation[0];
            return new GameplayIntegrationObservation(cityIdentity, census.Frame,
                employment, neighborhoods, null, null, decisions);
        }

        /// <summary>
        /// Updates aggregate shadow results from the same native snapshot/census used
        /// by the bridge. These estimates never feed a native writer. Tax uses one
        /// disclosed population-derived segment; access uses city traffic plus the
        /// visible aggregate job counts. Freight remains unobserved until a real
        /// stock/offer signal exists, so it cannot invent a route or backlog.
        /// </summary>
        private void UpdateShadowObservations(CitySnapshot city, CityCensus census)
        {
            _publishedShadowTax = null;
            _shadowAccessObserved = false;
            _shadowFreightObserved = false;
            _publishedShadowAccess = new ShadowPressure();
            _publishedShadowFreight = new ShadowPressure();

            if (_shadowTax != null && city != null && city.DataValid)
            {
                int multiplierPercent = 100;
                if (!double.IsNaN(city.NativeTaxMultiplier) && !double.IsInfinity(city.NativeTaxMultiplier))
                {
                    double scaled = Math.Round(city.NativeTaxMultiplier * 100.0);
                    if (scaled < ShadowTaxInput.MinimumTaxMultiplierPercent) scaled = ShadowTaxInput.MinimumTaxMultiplierPercent;
                    if (scaled > ShadowTaxInput.MaximumTaxMultiplierPercent) scaled = ShadowTaxInput.MaximumTaxMultiplierPercent;
                    multiplierPercent = (int)scaled;
                }
                long taxableBase = (long)Math.Max(0, city.Population) * 100L;
                ShadowTaxSegment segment = new ShadowTaxSegment("population-estimate", taxableBase, 10);
                // WeeklyBalance is the native accounting signal used as observed
                // deficit; it is read-only and never replaced by the virtual ledger.
                _publishedShadowTax = _shadowTax.Apply(new ShadowTaxInput(
                    city.WeeklyBalance, multiplierPercent, Math.Max(0, city.Population),
                    new[] { segment }));
            }

            if (_shadowAccess != null && city != null && city.DataValid &&
                city.TrafficFlowPercent >= 0 && city.TrafficFlowPercent <= 100)
            {
                int population = Math.Max(0, city.Population);
                int jobCapacity = census == null ? 0 : SumNonNegative(census.JobCapacityBySegment, census.UnmappedJobCapacity);
                int filledJobs = census == null ? 0 : SumNonNegative(census.FilledJobsBySegment, census.UnmappedFilledJobs);
                int demand = population > 0 ? population : Math.Max(jobCapacity, filledJobs);
                if (demand > 0)
                {
                    int offers = Math.Max(jobCapacity, filledJobs);
                    // A missing job signal is unknown, not an empty labour market.
                    // Keep offers neutral in that case so traffic remains the only
                    // observed pressure instead of fabricating a shortage.
                    if (offers == 0) offers = demand;
                    ShadowAccessObservation observation = new ShadowAccessObservation
                    {
                        TrafficLoadPercent = 100 - city.TrafficFlowPercent,
                        DistanceUnits = 0,
                        HasConnection = true,
                        DemandUnits = demand,
                        OfferUnits = offers
                    };
                    _publishedShadowAccess = _shadowAccess.EvaluateAccess(observation);
                    _shadowAccessObserved = true;
                }
            }
        }

        private static int SumNonNegative(int[] values, int extra)
        {
            long total = Math.Max(0, extra);
            if (values != null)
                for (int i = 0; i < values.Length; i++)
                    if (values[i] > 0) total += values[i];
            return total >= int.MaxValue ? int.MaxValue : (int)total;
        }

        // These are read-only city-level signals already exposed by SnapshotReader. They
        // feed the decision engine with evidence and advisor requests; they never mutate
        // native demand, money, buildings, or service state. Tax and freight adapters stay
        // empty until the engine exposes per-account/per-route observations with the same
        // identity guarantees as the census. Shadow estimates are kept in their own
        // fields and are never presented as those native reports.
        private GameplayDecisionObservation[] BuildNativeDecisionObservations(CitySnapshot city)
        {
            if (city == null || !city.DataValid) return new GameplayDecisionObservation[0];
            List<GameplayDecisionObservation> result = new List<GameplayDecisionObservation>(7);
            int abandoned = checked(city.ResidentialAbandoned + city.CommercialAbandoned + city.IndustrialAbandoned + city.OfficeAbandoned);
            result.Add(Decision("city.abandonment", abandoned > 0,
                AdvisorId.UrbanPlanning, DecisionConsequence.NeighborhoodDecline, "Address abandoned buildings",
                "abandonedBuildings", abandoned, 0, DecisionUrgencyFor(abandoned, 1, 8), "Urban Planning"));
            result.Add(Decision("city.cashflow", city.WeeklyBalance < 0,
                AdvisorId.Finance, DecisionConsequence.TaxRevenueLoss, "Review the municipal budget",
                "weeklyBalance", city.WeeklyBalance, 0, city.WeeklyBalance < -10000 ? DecisionUrgency.Urgent : DecisionUrgency.Attention, "Finance"));
            bool healthShortage = city.HealthCapacity > 0 && city.SickCitizens > city.HealthCapacity;
            result.Add(Decision("city.health", healthShortage,
                AdvisorId.EducationHealth, DecisionConsequence.ServiceUnderfunding, "Expand health services",
                "sickCitizens", city.SickCitizens, city.HealthCapacity, DecisionUrgencyFor(city.SickCitizens - city.HealthCapacity, 1, 25), "EducationHealth"));
            bool trafficPoor = city.TrafficFlowPercent >= 0 && city.TrafficFlowPercent < 55;
            result.Add(Decision("city.traffic", trafficPoor,
                AdvisorId.Transport, DecisionConsequence.JobAccessLoss, "Improve transport connections",
                "trafficFlowPercent", city.TrafficFlowPercent, 70, city.TrafficFlowPercent < 35 ? DecisionUrgency.Urgent : DecisionUrgency.Attention, "Transport"));
            result.Add(Decision("city.unemployment", city.UnemploymentPercent >= 12,
                AdvisorId.UrbanPlanning, DecisionConsequence.Unemployment, "Expand employment opportunities",
                "unemploymentPercent", city.UnemploymentPercent, 5, DecisionUrgencyFor(city.UnemploymentPercent - 11, 1, 12), "Urban Planning"));
            if (_shadowAccessObserved && _publishedShadowAccess.PressurePercent >= ShadowAccessFreight.ConstrainedThresholdPercent)
                result.Add(Decision("shadow.access", true, AdvisorId.Transport, DecisionConsequence.JobAccessLoss,
                    "Improve aggregate access", "shadowAccessPressure", _publishedShadowAccess.PressurePercent,
                    ShadowAccessFreight.ConstrainedThresholdPercent,
                    DecisionUrgencyFor(_publishedShadowAccess.PressurePercent, 50, 80), "Transport"));
            if (_publishedShadowTax != null && _publishedShadowTax.FiscalPressurePercent >= ShadowAccessFreight.ConstrainedThresholdPercent)
                result.Add(Decision("shadow.fiscal", true, AdvisorId.Finance, DecisionConsequence.TaxRevenueLoss,
                    "Review the shadow budget", "shadowFiscalPressure", _publishedShadowTax.FiscalPressurePercent,
                    ShadowAccessFreight.ConstrainedThresholdPercent,
                    DecisionUrgencyFor(_publishedShadowTax.FiscalPressurePercent, 50, 80), "Finance"));
            return result.ToArray();
        }

        private static GameplayDecisionObservation Decision(string causeKey, bool active, AdvisorId advisor,
            DecisionConsequence consequence, string action, string indicator, double value, double expected,
            DecisionUrgency urgency, string department)
        {
            AdvisorRecommendation recommendation = AdvisorRecommendation.Create(advisor, department, consequence,
                action, indicator, value, expected, string.Empty);
            return new GameplayDecisionObservation(causeKey, active, recommendation, urgency, department,
                "{0}", "{0}");
        }

        private static DecisionUrgency DecisionUrgencyFor(int severity, int attention, int urgent)
        {
            if (severity >= urgent) return DecisionUrgency.Urgent;
            return severity >= attention ? DecisionUrgency.Attention : DecisionUrgency.Info;
        }

        private string GetGameplayCityIdentity()
        {
            string cityIdentity = Singleton<SimulationManager>.instance.m_metaData == null
                ? "session-" + _session.ToString()
                : Singleton<SimulationManager>.instance.m_metaData.m_gameInstanceIdentifier;
            if (string.IsNullOrEmpty(cityIdentity)) cityIdentity = "session-" + _session.ToString();
            return cityIdentity;
        }
    }
}
