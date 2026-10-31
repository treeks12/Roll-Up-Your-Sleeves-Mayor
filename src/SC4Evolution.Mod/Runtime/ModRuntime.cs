using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using ColossalFramework;
using ColossalFramework.Plugins;
using SC4Evolution.Core;
using SC4Evolution.Mod.Economy;
using SC4Evolution.Mod.Civic;
using UnityEngine;

namespace SC4Evolution.Mod.Runtime
{
    public static partial class ModRuntime
    {
        private static readonly object StateLock = new object();
        private static readonly object LogLock = new object();
        private static volatile string _root;
        private static string Root
        {
            get
            {
                string root = _root;
                if (string.IsNullOrEmpty(root))
                {
                    root = FindProjectRoot();
                    if (!string.IsNullOrEmpty(root)) _root = root;
                }
                return root ?? string.Empty;
            }
        }
        private static volatile CabinetReport _report;
        private static volatile bool _loaded;
        private static AdvisorEngine _engine;
        private static volatile EconomicBridge _economy;
        private static byte[] _pendingState;
        private static byte[] _pendingClassState;
        private static string _pendingClassLoadFailure;
        private static byte[] _pendingCapacityState;
        private static string _pendingCapacityLoadFailure;
        private static volatile string _persistenceStatus = "No economic save captured.";
        private static bool _sampled;
        private static uint _lastFrame;
        private static DateTime _lastError = DateTime.MinValue;
        private static DateTime _lastStatusWrite = DateTime.MinValue;
        private static int _session;

        public static CabinetReport CurrentReport { get { return _report; } }
        public static bool IsLoaded { get { return _loaded; } }
        public static string ProjectRoot { get { return Root; } }
        public static string PersistenceStatus { get { return _persistenceStatus; } }
        public static SegmentedDemandSnapshot CurrentDemand { get { EconomicBridge bridge = _economy; return bridge == null ? null : bridge.Published; } }
        public static object CurrentGameplay
        {
            get
            {
                EconomicBridge bridge = _economy;
                if (bridge == null) return null;
                PropertyInfo property = bridge.GetType().GetProperty("PublishedGameplay", BindingFlags.Instance | BindingFlags.Public);
                return property == null ? null : property.GetValue(bridge, null);
            }
        }
        public static string EconomicStatus { get { EconomicBridge bridge = _economy; return bridge == null ? "Simulação econômica aguardando cidade." : bridge.Status; } }
        public static ResidentialClassStatus CurrentResidentialClasses { get { EconomicBridge bridge = _economy; return bridge == null ? null : bridge.PublishedClassStatus; } }
        internal static EconomicBridge CurrentEconomicBridge { get { return _loaded ? _economy : null; } }

        internal sealed class StartSupersededException : InvalidOperationException
        {
            internal StartSupersededException(Exception cause)
                : base("Economic start preparation was superseded by a session change.", cause) { }
        }

        internal static void Start(bool isNewCity)
        {
            // Provider static constructors can invoke RuntimeDetour callbacks. No
            // runtime/economic/class/capacity owner lock may span this preparation.
            int preparingSession = System.Threading.Interlocked.CompareExchange(ref _session, 0, 0);
            string root;
            EconomicStartPreparation preparation;
            CivicFundingPreparation civicPreparation = null;
            CivicFundingBridge previousCivic = null;
            int startedSession = -1;
            bool publishedSession = false;
            EconomicBridge startedEconomy = null;
            CivicFundingBridge startedCivic = null;
            try
            {
                try
                {
                    try
                    {
                        root = Root;
                        preparation = EconomicBridge.PrepareStart(root);
                        civicPreparation = CivicFundingBridge.Prepare(preparation.ConfigurationValid && preparation.CivicFundingEnabled, root);
                    }
                    catch (Exception error)
                    {
                        lock (StateLock)
                        {
                            if (_session != preparingSession)
                            {
                                Log("Economic start preparation failed after its session was superseded: " + error);
                                throw new StartSupersededException(error);
                            }
                        }
                        throw;
                    }
                    int thisSession;
                    lock (StateLock)
                    {
                        if (_session != preparingSession)
                        {
                            Log("Economic start preparation rejected: its session changed during provider preparation.");
                            throw new StartSupersededException(null);
                        }
                        _loaded = false;
                        _report = null;
                        _engine = new AdvisorEngine();
                        if (_economy != null) _economy.Stop();
                        previousCivic = _civic;
                        if (previousCivic != null) previousCivic.Stop();
                        _economy = new EconomicBridge();
                        if (_pendingClassLoadFailure != null) _economy.SetClassLoadFailure(_pendingClassLoadFailure);
                        else _economy.SetLoadedClassState(_pendingClassState);
                        if (_pendingCapacityLoadFailure != null) _economy.SetCapacityLoadFailure(_pendingCapacityLoadFailure);
                        else _economy.SetLoadedCapacityState(_pendingCapacityState);
                        _economy.SetNewCapacityCity(isNewCity);
                        _economy.Start(root, preparation);
                        _civic = new CivicFundingBridge();
                        _civic.Start(_pendingCivicState, preparation.ConfigurationValid && preparation.CivicFundingEnabled,
                        civicPreparation, _pendingCivicLoadFailure);
                        _pendingCivicState = null;
                        _pendingCivicLoadFailure = null;
                        _pendingClassState = null;
                        _pendingClassLoadFailure = null;
                        _pendingCapacityState = null;
                        _pendingCapacityLoadFailure = null;
                        if (_pendingState != null)
                        {
                            try { _engine.LoadState(_pendingState); }
                            catch (Exception error) { Log("Cabinet history was rejected; starting fresh: " + error.Message); }
                        }
                        _pendingState = null;
                        _sampled = false;
                        _lastFrame = 0;
                        _lastStatusWrite = DateTime.MinValue;
                        _loaded = true;
                        thisSession = ++_session;
                        startedSession = thisSession;
                        startedEconomy = _economy;
                        startedCivic = _civic;
                        publishedSession = true;
                    }

                    // All game statistics are read on the simulation thread, including the first sample
                    // in a paused city. A queued sample cannot leak into a different loaded city.
                    Singleton<SimulationManager>.instance.AddAction(delegate
                    {
                        CaptureInitialReport(thisSession, 0);
                    });
                }
                finally
                {
                    // Detour constructors and unpatching may invoke external code.
                    // Both preparation and lease release stay outside StateLock.
                    try { if (civicPreparation != null) civicPreparation.Dispose(); }
                    catch (Exception error) { Log("Civic preparation lease release failed: " + error); }
                    try { if (previousCivic != null) previousCivic.ReleaseHooks(); }
                    catch (Exception error) { Log("Previous civic hook lease release failed: " + error); }
                }
            }
            catch (Exception error)
            {
                // The failed start may still have released a hook lease that
                // reentered loading. Classify ownership after that cleanup too.
                lock (StateLock)
                {
                    bool superseded = publishedSession
                    ? !_loaded || _session != startedSession || !ReferenceEquals(_economy, startedEconomy) || !ReferenceEquals(_civic, startedCivic)
                    : _session != preparingSession;
                    if (superseded && !(error is StartSupersededException))
                    throw new StartSupersededException(error);
                }
                throw;
            }
            lock (StateLock)
            {
                // Lease cleanup can itself reenter loading or Stop. A completed
                // inner session owns its UI; an obsolete Loading callback must
                // not continue to install UI or run generic failure cleanup.
                if (!_loaded || _session != startedSession || !ReferenceEquals(_economy, startedEconomy) || !ReferenceEquals(_civic, startedCivic))
                throw new StartSupersededException(null);
            }
        }

        private static void CaptureInitialReport(int session, int attempt)
        {
            lock (StateLock)
            {
                if (!_loaded || _session != session) return;
                EconomicBridge bridge = _economy;
                if (bridge != null && !bridge.InitialSampleComplete && attempt < 1024)
                {
                    Singleton<SimulationManager>.instance.AddAction(delegate { CaptureInitialReport(session, attempt + 1); });
                    return;
                }
                if (bridge != null && !bridge.InitialSampleComplete)
                    Log("Initial economic binding exceeded the report wait bound; cabinet statistics will still be sampled.");
                // A paused city may have no simulation frames after the queued class
                // reconciliation. Publish its completed first sample immediately.
                _lastStatusWrite = DateTime.MinValue;
                CaptureLocked();
            }
        }

        internal static void Stop()
        {
            CivicFundingBridge civic;
            lock (StateLock)
            {
                _loaded = false;
                ++_session;
                _report = null;
                _engine = null;
                if (_economy != null) _economy.Stop();
                _economy = null;
                civic = _civic;
                _civic = null;
                if (civic != null) civic.Stop();
                _sampled = false;
                _pendingState = null;
                _pendingClassState = null;
                _pendingClassLoadFailure = null;
                _pendingCapacityState = null;
                _pendingCapacityLoadFailure = null;
                _pendingCivicState = null;
                _pendingCivicLoadFailure = null;
            }
            try { if (civic != null) civic.ReleaseHooks(); }
            catch (Exception error) { Log("Civic hook lease release failed after disarming its owner: " + error); }
            Log("Cabinet session ended; economic bridge stopped and owned growth gate removed.");
        }

        internal static void SetLoadedState(byte[] state)
        {
            lock (StateLock)
            {
                _pendingState = state == null ? null : (byte[])state.Clone();
            }
        }

        internal static byte[] GetSaveState()
        {
            lock (StateLock)
            {
                if (_loaded && _engine != null) return _engine.SaveState();
                return _pendingState == null ? null : (byte[])_pendingState.Clone();
            }
        }

        internal static void SetLoadedResidentialClassState(byte[] state)
        {
            if (state != null && state.Length > 16 * 1024 * 1024)
                throw new InvalidDataException("Residential class payload exceeds the supported size.");
            lock (StateLock)
            {
                _pendingClassState = state == null ? null : (byte[])state.Clone();
                // Failures are sticky until the next explicit BeginEconomicLoad.
            }
        }

        internal static void SetResidentialClassLoadFailure(string reason)
        {
            lock (StateLock)
            {
                _pendingClassLoadFailure = string.IsNullOrEmpty(reason) ? "Residential class save data could not be read." : reason;
                _persistenceStatus = "Residential class save rejected; effects suspended: " + _pendingClassLoadFailure;
            }
        }

        internal static void BeginEconomicLoad()
        {
            lock (StateLock)
            {
                _pendingClassState = null; _pendingCapacityState = null;
                _pendingClassLoadFailure = null; _pendingCapacityLoadFailure = null;
                _pendingCivicState = null; _pendingCivicLoadFailure = null;
                _persistenceStatus = "Reading economic save data.";
            }
        }

        internal static void SetLoadedEconomicPair(byte[] classes, byte[] capacity)
        {
            if (classes == null || classes.Length > EconomicSaveEnvelope.MaximumPartBytes ||
                (capacity != null && capacity.Length > EconomicSaveEnvelope.MaximumPartBytes))
                throw new InvalidDataException("Economic pair payload is missing or oversized.");
            lock (StateLock)
            {
                _pendingClassState = (byte[])classes.Clone();
                _pendingCapacityState = capacity == null ? null : (byte[])capacity.Clone();
                if (_pendingClassLoadFailure == null && _pendingCapacityLoadFailure == null)
                    _persistenceStatus = "Bound economic pair retained; awaiting city validation.";
            }
        }

        internal static void SetEconomicLoadFailure(string reason)
        {
            lock (StateLock)
            {
                string message = string.IsNullOrEmpty(reason) ? "Economic save could not be read." : reason;
                _pendingClassLoadFailure = message; _pendingCapacityLoadFailure = message;
                _persistenceStatus = "Economic save rejected; effects suspended: " + message;
            }
        }

        internal static byte[] GetEconomicSaveEnvelope()
        {
            lock (StateLock)
            {
                if (_pendingClassLoadFailure != null || _pendingCapacityLoadFailure != null)
                    throw new InvalidDataException("Economic save unavailable because loaded state was rejected.");
                if (_loaded)
                {
                    if (_economy == null) throw new InvalidOperationException("Economic owner is unavailable.");
                    EconomicSavePair pair; string reason;
                    if (!_economy.TryGetSavePair(out pair, out reason) || pair == null)
                        throw new InvalidOperationException("Economic save unavailable: " + reason);
                    return EconomicSaveEnvelope.Encode(pair.ClassPayload, pair.CapacityPayload);
                }
                return _pendingClassState == null ? null : EconomicSaveEnvelope.Encode(_pendingClassState, _pendingCapacityState);
            }
        }

        internal static void ReportPersistenceFailure(string reason)
        {
            lock (StateLock)
            {
                _persistenceStatus = "Economic save failed; effects suspended: " + reason;
                if (_economy != null) _economy.ReportPersistenceFailure(reason);
                if (_civic != null) _civic.ReportPersistenceFailure(reason);
            }
            Log(_persistenceStatus);
        }

        internal static void ReportPersistenceSuccess()
        {
            _persistenceStatus = "Economic pair save callback completed; native game save result is separate.";
        }

        internal static byte[] GetResidentialClassSaveState()
        {
            byte[] envelope = GetEconomicSaveEnvelope();
            if (envelope == null) return null;
            byte[] classes, capacity; EconomicSaveEnvelope.Decode(envelope, out classes, out capacity);
            return classes;
        }
        internal static void SampleIfDue()
        {
            if (!_loaded || !Singleton<SimulationManager>.exists) return;
            EconomicBridge bridge = _economy;
            if (bridge != null) bridge.SampleIfDue();
            CivicFundingBridge civic = _civic;
            if (civic != null) civic.Reconcile();
            uint frame = Singleton<SimulationManager>.instance.m_currentFrameIndex;
            lock (StateLock)
            {
                if (!_loaded || (_sampled && unchecked(frame - _lastFrame) < 256u)) return;
                CaptureLocked();
            }
        }

        private static void CaptureLocked()
        {
            try
            {
                CitySnapshot snapshot = SnapshotReader.Capture();
                if (!snapshot.DataValid) return;
                CabinetReport report = _engine.Evaluate(snapshot);
                _report = report;
                _lastFrame = Singleton<SimulationManager>.instance.m_currentFrameIndex;
                _sampled = true;
                if ((DateTime.UtcNow - _lastStatusWrite).TotalSeconds >= 15)
                {
                    _lastStatusWrite = DateTime.UtcNow;
                    WriteStatus(report);
                }
            }
            catch (Exception error)
            {
                if ((DateTime.UtcNow - _lastError).TotalSeconds >= 30)
                {
                    _lastError = DateTime.UtcNow;
                    Log("City sample failed (previous valid report retained): " + error);
                }
            }
        }

        private static void WriteStatus(CabinetReport report)
        {
            if (string.IsNullOrEmpty(Root)) return;
            try
            {
                CitySnapshot city = report.Snapshot;
                string directory = Path.Combine(Root, "logs");
                Directory.CreateDirectory(directory);
                StringBuilder text = new StringBuilder();
                text.AppendLine("SC4Evolution 0.3 runtime");
                text.AppendLine("RecordedUtc=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                text.AppendLine("CS1=" + BuildConfig.applicationVersion);
                text.AppendLine("ReportSequence=" + report.Sequence);
                text.AppendLine("SimulationDate=" + city.SimulationDate.ToString("o", CultureInfo.InvariantCulture));
                text.AppendLine("City=" + city.CityName);
                text.AppendLine("Population=" + city.Population);
                text.AppendLine("Cash=" + city.Cash);
                text.AppendLine("AccountingBalance=" + city.WeeklyBalance);
                text.AppendLine("TrafficFlowPercent=" + city.TrafficFlowPercent);
                text.AppendLine("NativeTaxMultiplier=" + city.NativeTaxMultiplier.ToString("0.0000", CultureInfo.InvariantCulture));
                text.AppendLine("Advisors=" + (report.Briefings == null ? 0 : report.Briefings.Length));
                text.AppendLine("EconomicBridge=" + EconomicStatus);
                text.AppendLine("EconomicPersistence=" + PersistenceStatus);
                text.AppendLine("CivicFunding=" + CivicFundingStatus);
                ResidentialClassStatus classes = CurrentResidentialClasses;
                if (classes != null)
                {
                    text.AppendLine("ResidentialClassEffectsEnabled=" + classes.Enabled);
                    text.AppendLine("ResidentialClassLocalPolicy=" + classes.Policy);
                    text.AppendLine("ResidentialClassImported=" + classes.ImportedCount);
                    text.AppendLine("ResidentialClassSelected=" + classes.SelectedCount);
                    text.AppendLine("ResidentialClassUnboundResidents=" + classes.UnboundResidents);
                    text.AppendLine("ResidentialClassStatus=" + classes.Status);
                }
                SegmentedDemandSnapshot demand = CurrentDemand;
                if (demand != null)
                {
                    text.AppendLine("DemandNodes=" + demand.Nodes.Length);
                    text.AppendLine("DemandConverged=" + demand.Converged);
                    text.AppendLine("DemandPasses=" + demand.PropagationPasses);
                    text.AppendLine("EconomicGameplayEnabled=" + demand.GameplayEnabled);
                    text.AppendLine("CensusHouseholds=" + demand.Census.HomeHouseholds);
                    text.AppendLine("CensusOccupiedHouseholds=" + demand.Census.OccupiedHomeHouseholds);
                    text.AppendLine("CensusUnmappedJobCapacity=" + demand.Census.UnmappedJobCapacity);
                    text.AppendLine("CensusUnmappedFilledJobs=" + demand.Census.UnmappedFilledJobs);
                    foreach (SC4Evolution.Simulation.DemandNodeState node in demand.Nodes)
                        if (node.Key == 0x21010 || node.Key == 0x21020 || node.Key == 0x21030 ||
                            node.Key == 0x23110 || node.Key == 0x23120 || node.Key == 0x23130 ||
                            node.Key == 0x23320 || node.Key == 0x23330 || node.Key == 0x24100 ||
                            node.Key == 0x24200 || node.Key == 0x24300 || node.Key == 0x24400)
                            text.AppendLine("Demand." + node.Key.ToString("X5", CultureInfo.InvariantCulture) + "=" +
                                node.ActiveDemand.ToString("R", CultureInfo.InvariantCulture) + ";normalized=" + node.NormalizedDemand);
                }
                AppendGameplayStatus(text, CurrentGameplay);
                AppendShadowGameplayStatus(text, CurrentEconomicBridge);
                File.WriteAllText(Path.Combine(directory, "runtime-status.txt"), text.ToString(), Encoding.UTF8);
            }
            catch (Exception error) { Log("Could not write local runtime status: " + error.Message); }
        }

        private static void AppendGameplayStatus(StringBuilder text, object gameplay)
        {
            if (gameplay == null) return;
            Type type = gameplay.GetType();
            object flags = ReadProperty(type, gameplay, "EnabledSprints");
            object frame = ReadProperty(type, gameplay, "Frame");
            object employment = ReadProperty(type, gameplay, "Employment");
            object neighborhoods = ReadProperty(type, gameplay, "Neighborhoods");
            object taxation = ReadProperty(type, gameplay, "Taxation");
            object access = ReadProperty(type, gameplay, "AccessFreight");
            object notices = ReadProperty(type, gameplay, "DecisionNotices");
            object missing = ReadProperty(type, gameplay, "NeighborhoodLotsWithoutFactorSamples");
            text.AppendLine("GameplaySprints=" + (flags ?? "unknown"));
            text.AppendLine("GameplayFrame=" + (frame ?? "unknown"));
            text.AppendLine("GameplayEmploymentReport=" + (employment == null ? "false" : "true"));
            text.AppendLine("GameplayNeighborhoodReport=" + (neighborhoods == null ? "false" : "true"));
            text.AppendLine("GameplayTaxReport=" + (taxation == null ? "false" : "true"));
            text.AppendLine("GameplayAccessFreightReport=" + (access == null ? "false" : "true"));
            Array noticeArray = notices as Array;
            text.AppendLine("GameplayDecisionNotices=" + (noticeArray == null ? "unknown" : noticeArray.Length.ToString(CultureInfo.InvariantCulture)));
            text.AppendLine("GameplayNeighborhoodMissingFactors=" + (missing ?? "unknown"));
            Type neighborhoodType = Type.GetType("SC4Evolution.Mod.Economy.NativeNeighborhoodLevelUpExtension, SC4Evolution");
            text.AppendLine("NativeNeighborhoodObservations=" + ReadStaticInt(neighborhoodType, "NativeObservations"));
            text.AppendLine("NativeNeighborhoodApplications=" + ReadStaticInt(neighborhoodType, "NativeApplications"));
            text.AppendLine("NativeNeighborhoodLastBuilding=" + ReadStaticInt(neighborhoodType, "LastBuildingId"));
            text.AppendLine("NativeNeighborhoodLastLandValue=" + ReadStaticInt(neighborhoodType, "LastLandValue"));
            text.AppendLine("NativeNeighborhoodLastPollution=" + ReadStaticInt(neighborhoodType, "LastPollution"));
            text.AppendLine("NativeNeighborhoodLastServices=" + ReadStaticInt(neighborhoodType, "LastServices"));
            text.AppendLine("NativeNeighborhoodLastAttractiveness=" + ReadStaticInt(neighborhoodType, "LastAttractiveness"));
            Type workplaceType = Type.GetType("SC4Evolution.Mod.Economy.WorkplaceDemandDiagnostics, SC4Evolution");
            text.AppendLine("NativeWorkplaceCallbacks=" + ReadStaticInt(workplaceType, "Callbacks"));
            text.AppendLine("NativeWorkplaceAggregated=" + ReadStaticInt(workplaceType, "Aggregated"));
            text.AppendLine("NativeWorkplaceLastInput=" + ReadStaticInt(workplaceType, "LastInput"));
            text.AppendLine("NativeWorkplaceLastReturn=" + ReadStaticInt(workplaceType, "LastReturn"));
            text.AppendLine("NativeWorkplaceLastThread=" + ReadStaticInt(workplaceType, "LastThread"));
        }

        // Keep the persistence harness independent from the optional shadow model
        // types: the bridge is inspected by reflection just like the other optional
        // diagnostics above.
        private static void AppendShadowGameplayStatus(StringBuilder text, object bridge)
        {
            if (bridge == null) return;
            Type type = bridge.GetType();
            object taxEnabled = ReadProperty(type, bridge, "ShadowTaxationEnabled");
            object accessEnabled = ReadProperty(type, bridge, "ShadowAccessFreightEnabled");
            if (taxEnabled == null && accessEnabled == null) return;
            bool taxOn = taxEnabled is bool && (bool)taxEnabled;
            bool accessOn = accessEnabled is bool && (bool)accessEnabled;
            if (!taxOn && !accessOn) return;

            text.AppendLine("ShadowTaxationEnabled=" + (taxEnabled ?? "unknown"));
            object tax = ReadProperty(type, bridge, "PublishedShadowTax");
            text.AppendLine("ShadowTaxationObserved=" + (tax == null ? "false" : "true"));
            if (tax != null)
            {
                Type taxType = tax.GetType();
                text.AppendLine("ShadowTaxPressurePercent=" + (ReadProperty(taxType, tax, "FiscalPressurePercent") ?? "unknown"));
                text.AppendLine("ShadowTaxConfidencePercent=" + (ReadProperty(taxType, tax, "ConfidencePercent") ?? "unknown"));
                text.AppendLine("ShadowTaxVirtualBalance=" + (ReadProperty(taxType, tax, "VirtualBalance") ?? "unknown"));
            }
            else
            {
                text.AppendLine("ShadowTaxPressurePercent=unknown");
                text.AppendLine("ShadowTaxConfidencePercent=unknown");
                text.AppendLine("ShadowTaxVirtualBalance=unknown");
            }

            text.AppendLine("ShadowAccessFreightEnabled=" + (accessEnabled ?? "unknown"));
            object accessObserved = ReadProperty(type, bridge, "ShadowAccessObserved");
            object freightObserved = ReadProperty(type, bridge, "ShadowFreightObserved");
            text.AppendLine("ShadowAccessObserved=" + (accessObserved ?? "unknown"));
            text.AppendLine("ShadowFreightObserved=" + (freightObserved ?? "unknown"));
            AppendShadowPressureStatus(text, "ShadowAccess", ReadProperty(type, bridge, "PublishedShadowAccess"), accessObserved);
            AppendShadowPressureStatus(text, "ShadowFreight", ReadProperty(type, bridge, "PublishedShadowFreight"), freightObserved);
        }

        private static void AppendShadowPressureStatus(StringBuilder text, string prefix, object value, object observed)
        {
            bool isObserved = observed is bool && (bool)observed;
            if (!isObserved || value == null)
            {
                text.AppendLine(prefix + "PressurePercent=unknown");
                text.AppendLine(prefix + "ConfidencePercent=unknown");
                text.AppendLine(prefix + "Signals=none");
                return;
            }
            Type pressureType = value.GetType();
            text.AppendLine(prefix + "PressurePercent=" + (ReadProperty(pressureType, value, "PressurePercent") ?? "unknown"));
            text.AppendLine(prefix + "ConfidencePercent=" + (ReadProperty(pressureType, value, "ConfidencePercent") ?? "unknown"));
            text.AppendLine(prefix + "Signals=" + (ReadProperty(pressureType, value, "Signals") ?? "unknown"));
        }

        private static object ReadProperty(Type type, object value, string name)
        {
            PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            return property == null ? null : property.GetValue(value, null);
        }

        private static string ReadStaticInt(Type type, string name)
        {
            if (type == null) return "unknown";
            try
            {
                PropertyInfo property = type.GetProperty(name, BindingFlags.Static | BindingFlags.Public);
                object value = property == null ? null : property.GetValue(null, null);
                return value == null ? "unknown" : Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            }
            catch { return "unknown"; }
        }

        public static void Log(string message)
        {
            lock (LogLock)
            {
                try
                {
                    if (string.IsNullOrEmpty(Root))
                    {
                        Debug.Log("[SC4Evolution] " + message);
                        return;
                    }
                    string directory = Path.Combine(Root, "logs");
                    Directory.CreateDirectory(directory);
                    string path = Path.Combine(directory, "SC4Evolution.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 1048576)
                    {
                        // One bounded predecessor, always inside the project root.
                        File.Copy(path, Path.Combine(directory, "SC4Evolution.previous.log"), true);
                        File.WriteAllText(path, string.Empty);
                    }
                    File.AppendAllText(path, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine, Encoding.UTF8);
                }
                catch { Debug.LogWarning("[SC4Evolution] " + message); }
            }
        }

        private static string FindProjectRoot()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            // CS1 PluginManager.LoadPlugin uses Assembly.Load(byte[]), whose Location
            // is empty. The native plugin registration retains its actual mod path.
            try
            {
                if (Singleton<PluginManager>.exists)
                {
                    PluginManager.PluginInfo plugin = Singleton<PluginManager>.instance.FindPluginInfo(assembly);
                    string root = plugin == null ? string.Empty : FindRootFromDirectory(plugin.modPath);
                    if (!string.IsNullOrEmpty(root)) return root;
                }
            }
            catch { }
            try
            {
                if (!string.IsNullOrEmpty(assembly.Location))
                    return FindRootFromDirectory(Path.GetDirectoryName(assembly.Location));
            }
            catch { }
            return string.Empty;
        }

        private static string FindRootFromDirectory(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return string.Empty;
            try
            {
                string pointer = Path.Combine(directory, "project-root.path");
                if (File.Exists(pointer))
                {
                    string root = Path.GetFullPath(File.ReadAllText(pointer).Trim());
                    if (File.Exists(Path.Combine(root, "SC4Evolution.slnx"))) return root;
                }
                DirectoryInfo ancestor = new DirectoryInfo(directory);
                for (int depth = 0; ancestor != null && depth < 8; depth++, ancestor = ancestor.Parent)
                    if (File.Exists(Path.Combine(ancestor.FullName, "SC4Evolution.slnx"))) return ancestor.FullName;
            }
            catch { }
            return string.Empty;
        }
    }
}
