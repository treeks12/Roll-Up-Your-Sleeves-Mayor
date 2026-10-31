using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ColossalFramework;
using ColossalFramework.Packaging;
using ColossalFramework.UI;
using SC4Evolution.Mod.Runtime;
using SC4Evolution.Mod.UI;
using UnityEngine;

namespace SC4Evolution.Mod.Diagnostics
{
    /// <summary>Explicit, one-shot QA instrumentation. Call TryStart on Unity's main thread.</summary>
    public sealed partial class AutonomousTestDriver : MonoBehaviour
    {
        private static bool _started;
        private static volatile bool _cancelled;
        private static bool _quitCommitted;
        private static AutonomousTestDriver _active;
        private Request _request;
        private string _projectRoot;
        private NativeSaveRoundtrip _roundtrip;
        private NativeResidentialClassProbe _classProbe;
        private NativeOrganicResidentialProbe _organicProbe;
        private NativeCivicFundingProbe _civicProbe;
        private string _directory, _session, _phase, _error, _selectedAsset, _selectedChecksum;
        private DateTime _startedUtc;
        private float _startedAt, _captureAt;
        private int _stage, _tab, _settleFrames;
        private bool _createdCity, _quitRequested, _reportValid, _demandValid, _uiVisible;
        private bool _renderPreconditionsValid;
        private string _renderPath, _renderView, _blockingModal;
        private float _loadingOverlayClearSince = -1f;
        private bool _loadingOverlayClear;
        private string _loadingOverlayState;
        private LoadingManager _pausedLoading;
        private string _capturePath;
        private readonly List<string> _screenshots = new List<string>();
        private readonly List<string> _maps = new List<string>();
        private readonly List<string> _inventoryErrors = new List<string>();
        private readonly List<string> _renderChecks = new List<string>();

        /// <summary>No request means no file writes, scene changes, UI changes, or GameObject.</summary>
        public static void TryStart(string projectRoot)
        {
            if (_started || _cancelled || string.IsNullOrEmpty(projectRoot)) return;
            GameObject host = null;
            string directory = null;
            try
            {
                string root = Path.GetFullPath(projectRoot);
                string config = Path.Combine(root, "config");
                string requestPath = Path.Combine(config, "runtime-test.request");
                if (!File.Exists(requestPath)) return;
                // Project-local outputs must not follow a redirected config/log directory.
                RejectRedirectedPath(root);
                RejectRedirectedPath(config);
                RejectRedirectedPath(requestPath);
                string logs = Path.Combine(root, "logs");
                string tests = Path.Combine(logs, "runtime-tests");
                RejectRedirectedPath(logs);
                RejectRedirectedPath(tests);
                string session = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
                directory = Path.Combine(tests, session);
                Directory.CreateDirectory(directory);
                // A rename on the same project volume claims the request exactly once, before
                // parsing or any engine action. A concurrent caller loses this move safely.
                string consumed = Path.Combine(directory, "request.consumed");
                File.Move(requestPath, consumed);
                _started = true;
                Request request = ReadRequest(consumed);
                host = new GameObject("SC4EvolutionAutonomousTestDriver");
                UnityEngine.Object.DontDestroyOnLoad(host);
                AutonomousTestDriver driver = host.AddComponent<AutonomousTestDriver>();
                driver._request = request;
                driver._projectRoot = root;
                driver._directory = directory;
                driver._session = session;
                driver._startedUtc = DateTime.UtcNow;
                driver._startedAt = Time.realtimeSinceStartup;
                driver._phase = "WaitingForMainMenu";
                _active = driver;
                driver.WriteReport();
            }
            catch (Exception exception)
            {
                _active = null;
                if (host != null) UnityEngine.Object.Destroy(host);
                // A malformed opt-in must not affect normal mod/game startup.
                if (directory != null)
                {
                    try { File.WriteAllText(Path.Combine(directory, "request-rejected.txt"), DateTime.UtcNow.ToString("o") + "\n" + exception); }
                    catch { }
                }
            }
        }

        /// <summary>Call on Unity's main thread from Mod.OnDisabled. Irreversible for this process.</summary>
        public static void CancelPendingTest()
        {
            // Native PluginManager unloads mods during an accepted Application.Quit.
            // That teardown is not an operator cancellation of completed evidence.
            if (_quitCommitted) return;
            // Latch first: disabling before a request/host exists also prevents a later
            // OnEnabled from claiming a request in the same process.
            _cancelled = true;
            AutonomousTestDriver active = _active;
            if (ReferenceEquals(active, null)) return;
            if (active._roundtrip != null) active._roundtrip.CancelBeforePluginRemoval();
            if (active._classProbe != null) active._classProbe.RequestCleanup();
            if (active._civicProbe != null) active._civicProbe.CancelBeforePluginRemoval();
            if (active._organicProbe != null)
            {
                if (active.IsOrganicRoundtripMode()) active._organicProbe.InterruptRoundtrip();
                else active._organicProbe.CancelBeforePluginRemoval();
            }
            active._stage = 99;
            active._phase = "Cancelled";
            active._error = "Mod disabled; all pending test actions cancelled.";
            active._quitRequested = false;
            try { active.WriteReport(); } catch { }
            // A native load/screenshot already submitted cannot be recalled. Only retain
            // a minimal counter-cleanup guard if this driver acquired an autosave pause.
            try
            {
                if (active._pausedLoading == null && active.RoundtripCleanupReady()) active.DisposeHost();
                else active.enabled = true;
            }
            catch { /* A retained cancelled host can retry only counter cleanup. */ }
        }

        /// <summary>Call first in Loading.OnLevelUnloading, before ModRuntime.Stop.</summary>
        public static void OnLevelUnloading()
        {
            AutonomousTestDriver active = _active;
            if (ReferenceEquals(active, null)) return;
            if (active._civicProbe != null) active._civicProbe.BeforeLevelUnload();
            if (active._organicProbe != null) active._organicProbe.BeforeLevelUnload();
            if (active._classProbe != null)
            {
                try { active._classProbe.BeforeLevelUnload(); }
                catch { /* Never break native level unloading for a test. */ }
            }
            if (active._roundtrip != null)
            {
                try { active._roundtrip.BeforeLevelUnload(); }
                catch { /* Test cleanup must not break native level unloading. */ }
            }
        }

        private void Update()
        {
            if (_request == null) return;
            if (_roundtrip != null && !IsOrganicRoundtripMode() && (_cancelled || _stage == 99)) _roundtrip.RequestCleanup();
            if (_classProbe != null && !_classProbe.CleanupReady && (_cancelled || _stage == 99)) _classProbe.RequestCleanup();
            if (_civicProbe != null && !_civicProbe.CleanupReady && (_cancelled || _stage == 99)) _civicProbe.RequestCleanup();
            if (_organicProbe != null && !IsOrganicRoundtripMode() && !_organicProbe.CleanupReady && (_cancelled || _stage == 99)) _organicProbe.RequestCleanup();
            if (_cancelled)
            {
                CleanupCancelledTest();
                return;
            }
            if (_stage == 99)
            {
                // Preserve the autosave pause for the full disposable test session, even
                // after a failure. Restore the original counter when back at the menu.
                try
                {
                    if (!RoundtripCleanupReady())
                    {
                        if (_civicProbe != null && _request.ControlledQuit && _civicProbe.MayQuitUnderOwnedPause
                            && Singleton<LoadingManager>.exists && !Singleton<LoadingManager>.instance.m_currentlyLoading
                            && !Singleton<LoadingManager>.instance.m_applicationQuitting)
                        {
                            _quitRequested = true; try { WriteReport(); } catch { }
                            if (!_cancelled) CommitControlledQuit();
                        }
                        return;
                    }
                    if (CanReleaseAutosavePause()) { ReleaseAutosavePause(); DisposeHost(); }
                    else if (_request.ControlledQuit && OwnsCurrentCity() && Singleton<LoadingManager>.exists
                        && !Singleton<LoadingManager>.instance.m_currentlyLoading && !Singleton<LoadingManager>.instance.m_applicationQuitting)
                    {
                        _quitRequested = true;
                        try { WriteReport(); } catch { }
                        if (!_cancelled) CommitControlledQuit();
                    }
                }
                catch { /* Retain the cleanup guard even if reporting or quit fails. */ }
                return;
            }
            try
            {
                if (_roundtrip != null && _stage != 4) _roundtrip.ThrowIfFailure();
                if (Time.realtimeSinceStartup - _startedAt > _request.TimeoutSeconds)
                    throw new TimeoutException("Runtime test exceeded its total time bound in phase " + _phase + ".");
                if (_stage == 0) WaitForMainMenu();
                else if (_stage == 1) WaitForCity();
                else if (_stage == 2) PrepareCapture();
                else if (_stage == 3) WaitForCapture();
                else if (_stage == 4)
                {
                    if (_roundtrip.Step())
                    {
                        if (IsOrganicRoundtripMode()) Finish("Completed", null);
                        else { _stage = 1; _phase = "WaitingForReloadedCityAndRuntime"; WriteReport(); }
                    }
                }
                else if (_stage == 5)
                {
                    if (_classProbe.Step()) Finish("Completed", null);
                }
                else if (_stage == 6)
                {
                    bool completed = _organicProbe.Step();
                    if (IsOrganicRoundtripMode() && _organicProbe.CheckpointReady)
                    {
                        _roundtrip = new NativeSaveRoundtrip(this, _organicProbe);
                        _roundtrip.Start(); _stage = 4; _phase = "AcquiringPopulatedOrganicSaveCheckpoint"; WriteReport();
                    }
                    else if (completed) Finish(_organicProbe.DiagnosticOnly ? "OrganicInventoryComplete" : "Completed", null);
                }
                else if (_stage == 7)
                {
                    if (_civicProbe.Step()) Finish("Completed", null);
                }
            }
            catch (Exception exception) { _renderPreconditionsValid = false; Finish("Failed", exception.ToString()); }
        }

        private static bool HasActiveLevel()
        {
            return ModRuntime.IsLoaded || (Singleton<LoadingManager>.exists &&
                (Singleton<LoadingManager>.instance.m_simulationDataLoaded || Singleton<LoadingManager>.instance.m_loadingComplete));
        }

        private static bool MainMenuReady()
        {
            return Singleton<LoadingManager>.exists && !Singleton<LoadingManager>.instance.m_currentlyLoading
                && !Singleton<LoadingManager>.instance.m_applicationQuitting && !HasActiveLevel()
                && MainMenu.m_MainMenu != null && MainMenu.m_MainMenu.isVisible;
        }

        private void WaitForMainMenu()
        {
            if (_cancelled) return;
            // Never replace a city/editor, including an unmodified or unsaved session.
            if (HasActiveLevel()) throw new InvalidOperationException("Refused: a city or editor is already active.");
            if (!MainMenuReady()) return;
            List<MapMetaData> available = InventoryMaps();
            if (_request.Mode == "InventoryMaps") { Finish("InventoryComplete", null); return; }
            MapMetaData selected = null;
            foreach (MapMetaData map in available)
            {
                if (map.assetRef.fullName == _request.AssetName && map.assetRef.checksum == _request.AssetChecksum)
                {
                    if (selected != null) throw new InvalidDataException("Map identifier is ambiguous.");
                    selected = map;
                }
            }
            if (selected == null) throw new InvalidDataException("The explicit built-in map asset/checksum was not available. Inspect mapCandidates in this report.");
            SimulationMetaData metadata = new SimulationMetaData();
            metadata.m_CityName = "SC4Evolution QA " + _session;
            metadata.m_gameInstanceIdentifier = _session;
            metadata.m_invertTraffic = SimulationMetaData.MetaBool.False;
            metadata.m_disableAchievements = SimulationMetaData.MetaBool.True;
            metadata.m_startingDateTime = DateTime.Now;
            metadata.m_currentDateTime = DateTime.Now;
            metadata.m_newGameAppVersion = 222554384u;
            metadata.m_updateMode = SimulationManager.UpdateMode.NewGameFromMap;
            metadata.m_MapName = selected.mapName;
            // Native NewGamePanel leaves environment unset so the map's serialized
            // environment survives Merge. It instantiates the referenced default theme.
            if (!string.IsNullOrEmpty(selected.mapThemeRef))
            {
                Package.Asset theme = PackageManager.FindAssetByName(selected.mapThemeRef, UserAssetType.MapThemeMetaData);
                if (theme == null) throw new InvalidDataException("The map's default theme is unavailable.");
                metadata.m_MapThemeMetaData = theme.Instantiate<MapThemeMetaData>();
                if (metadata.m_MapThemeMetaData == null) throw new InvalidDataException("The map's default theme could not instantiate.");
                metadata.m_MapThemeMetaData.SetSelfRef(theme);
            }
            if (!MainMenuReady()) throw new InvalidOperationException("Main menu changed before native load; request refused.");
            _selectedAsset = selected.assetRef.fullName;
            _selectedChecksum = selected.assetRef.checksum;
            _phase = "StartingNativeNewGame";
            WriteReport();
            if (_cancelled) return;
            // Native AutoSaveRoutine waits on this counter before every SavePanel.AutoSave,
            // including an interval of zero. Pause survives its later Stop/Start calls.
            _pausedLoading = Singleton<LoadingManager>.instance;
            _pausedLoading.autoSaveTimer.Pause();
            if (_cancelled) return;
            if (Singleton<LoadingManager>.instance.LoadLevel(selected.assetRef, "Game", "InGame", metadata) == null)
                throw new InvalidOperationException("Native LoadLevel rejected the new game.");
            _createdCity = true;
            _stage = 1;
            _phase = "WaitingForOwnedCityAndRuntime";
            WriteReport();
        }

        private List<MapMetaData> InventoryMaps()
        {
            List<MapMetaData> result = new List<MapMetaData>();
            foreach (Package.Asset item in PackageManager.FilterAssets(UserAssetType.MapMetaData))
            {
                if (item == null || !item.isEnabled) continue;
                try
                {
                    MapMetaData map = item.Instantiate<MapMetaData>();
                    if (map == null || !map.builtin || !map.isPublished || map.assetRef == null || !map.assetRef.isEnabled) continue;
                    string mapsRoot = Path.GetFullPath(Path.Combine(ColossalFramework.IO.DataLocation.gameContentPath, "Maps")) + Path.DirectorySeparatorChar;
                    string packagePath = Path.GetFullPath(item.package.packagePath);
                    // Stronger path boundary than the native IsBuiltinMap prefix check.
                    if (!packagePath.StartsWith(mapsRoot, StringComparison.OrdinalIgnoreCase)
                        || !map.IsBuiltinMap(item.package.packagePath) || map.assetRef.package != item.package) continue;
                    bool playable = !NewGamePanel.IsUnplayableNDScenarioMap(map.mapName)
                        && SteamHelper.IsDLCOwned(NewGamePanel.GetRequiredDLCFromMapName(item.package, map))
                        && (map.environment != "Winter" || SteamHelper.IsDLCOwned(SteamHelper.DLC.SnowFallDLC));
                    _maps.Add("{\"mapName\":" + Json(map.mapName) + ",\"environment\":" + Json(map.environment)
                        + ",\"assetName\":" + Json(map.assetRef.fullName) + ",\"assetChecksum\":" + Json(map.assetRef.checksum)
                        + ",\"metadataAssetName\":" + Json(item.fullName) + ",\"metadataChecksum\":" + Json(item.checksum)
                        + ",\"playable\":" + Bool(playable) + "}");
                    if (playable) result.Add(map);
                }
                catch (Exception exception) { _inventoryErrors.Add(item.fullName + ": " + exception.GetType().Name + ": " + exception.Message); }
            }
            return result;
        }

        private bool OwnsCurrentCity()
        {
            return _createdCity && Singleton<SimulationManager>.exists && Singleton<SimulationManager>.instance.m_metaData != null
                && Singleton<SimulationManager>.instance.m_metaData.m_gameInstanceIdentifier == _session;
        }

        private bool CanReleaseAutosavePause()
        {
            if (MainMenuReady()) return true;
            // A user can load an existing city directly without passing through the main
            // menu. Once that load settles, restore our counter in the foreign session.
            return Singleton<LoadingManager>.exists && !Singleton<LoadingManager>.instance.m_currentlyLoading
                && !Singleton<LoadingManager>.instance.m_applicationQuitting && Singleton<LoadingManager>.instance.m_renderDataReady
                && Singleton<LoadingManager>.instance.m_loadingComplete && Singleton<LoadingManager>.instance.m_simulationDataLoaded
                && Singleton<SimulationManager>.exists && Singleton<SimulationManager>.instance.m_metaData != null
                && !string.IsNullOrEmpty(Singleton<SimulationManager>.instance.m_metaData.m_gameInstanceIdentifier)
                && Singleton<SimulationManager>.instance.m_metaData.m_gameInstanceIdentifier != _session;
        }

        private void CleanupCancelledTest()
        {
            try
            {
                if (!RoundtripCleanupReady()) return;
                if (_pausedLoading == null || CanReleaseAutosavePause())
                {
                    ReleaseAutosavePause();
                    DisposeHost();
                }
            }
            catch { /* Keep the owned pause while the native session state is uncertain. */ }
        }

        private void DisposeHost()
        {
            if (_civicProbe != null) _civicProbe.RequestCleanup();
            if (_organicProbe != null) _organicProbe.RequestCleanup();
            if (_roundtrip != null) _roundtrip.DetachErrorCapture();
            if (_classProbe != null) _classProbe.RequestCleanup();
            if (ReferenceEquals(_active, this)) _active = null;
            enabled = false;
            UnityEngine.Object.Destroy(gameObject);
        }

        private void OnDestroy()
        {
            if (_civicProbe != null) _civicProbe.RequestCleanup();
            if (_organicProbe != null) _organicProbe.RequestCleanup();
            if (_roundtrip != null) _roundtrip.DetachErrorCapture();
            if (_classProbe != null) _classProbe.RequestCleanup();
            if (ReferenceEquals(_active, this)) _active = null;
        }

        private void WaitForCity()
        {
            if (_cancelled) return;
            LoadingManager loading = Singleton<LoadingManager>.instance;
            if (loading.m_currentlyLoading || !loading.m_loadingComplete || !loading.m_simulationDataLoaded || !loading.m_renderDataReady) return;
            if (!OwnsCurrentCity()) throw new InvalidOperationException("Loaded session no longer belongs to this test.");
            if (!ModRuntime.IsLoaded || ModRuntime.CurrentReport == null || ModRuntime.CurrentReport.Snapshot == null
                || !ModRuntime.CurrentReport.Snapshot.DataValid || ModRuntime.CurrentDemand == null || ModRuntime.CurrentDemand.Census == null) return;
            _reportValid = true;
            _demandValid = ValidateDemand();
            if (!_demandValid) throw new InvalidDataException("Published demand graph or census failed validity checks.");
            if (_request.Mode == "CivicFundingTestCity")
            {
                if (_civicProbe != null) throw new InvalidOperationException("Civic probe cannot be restarted.");
                _civicProbe = new NativeCivicFundingProbe(this);
                _stage = 7; _phase = "WaitingForNativeCivicFundingProbe";
                _civicProbe.Start(); WriteReport(); return;
            }
            // This mode observes simulation data only. It neither opens nor dismisses UI.
            if (_request.Mode == "ResidentialClassTestCity")
            {
                if (_classProbe != null) throw new InvalidOperationException("Residential class probe cannot be restarted.");
                _classProbe = new NativeResidentialClassProbe(this);
                _stage = 5;
                _phase = "WaitingForResidentialClassDataProbe";
                _classProbe.Start();
                WriteReport();
                return;
            }
            if (IsOrganicMode())
            {
                if (_organicProbe != null) throw new InvalidOperationException("Organic probe cannot be restarted.");
                _organicProbe = new NativeOrganicResidentialProbe(this);
                _stage = 6;
                _phase = "WaitingForOrganicResidentialInventory";
                _organicProbe.Start();
                WriteReport();
                return;
            }
            EnsureNoModal();
            if (_request.Mode == "SaveReloadTestCity" && _roundtrip == null)
            {
                _roundtrip = new NativeSaveRoundtrip(this);
                _roundtrip.Start();
                _stage = 4;
                _phase = "AcquiringSaveCheckpoint";
                WriteReport();
                return;
            }
            CabinetRoot root = FindRoot();
            if (_cancelled) return;
            if (root == null || root.CouncilPanel == null || !root.CouncilExpanded || !root.CouncilPanel.isVisible) CabinetUI.Toggle();
            root = RequireCouncil();
            EnsureRenderPreconditions(root.CouncilPanel);
            _uiVisible = true;
            _tab = 0;
            _settleFrames = 0;
            _stage = 2;
            _phase = "CabinetReady";
            WriteReport();
        }

        private static CabinetRoot FindRoot()
        {
            UIView view = UIView.GetAView();
            return view == null ? null : UIView.Find<CabinetRoot>("SC4EvolutionCabinetRoot");
        }

        private static bool ValidateDemand()
        {
            var demand = ModRuntime.CurrentDemand;
            if (demand == null || demand.Census == null || !demand.Converged) return false;
            var nodes = demand.Nodes;
            // The reviewed SC4-derived profile contains exactly 173 graph nodes.
            if (nodes.Length != 173) return false;
            HashSet<uint> keys = new HashSet<uint>();
            foreach (var node in nodes)
            {
                if (!keys.Add(node.Key) || node.Minimum > node.Maximum || node.NormalizedDemand < -1000 || node.NormalizedDemand > 1000
                    || !Finite(node.Minimum) || !Finite(node.Maximum) || !Finite(node.Demand) || !Finite(node.Supply)
                    || !Finite(node.ActiveDemand) || !Finite(node.PendingSupply) || !Finite(node.PendingDemand)
                    || !Finite(node.EconomyModifier) || !Finite(node.TaxModifier) || !Finite(node.DemandCap)) return false;
            }
            var census = demand.Census;
            return census.HomeHouseholds >= 0 && census.OccupiedHomeHouseholds >= 0
                && census.OccupiedHomeHouseholds <= census.HomeHouseholds
                && Nonnegative(census.ResidentsByBootstrapWealth) && Nonnegative(census.WorkforceByBootstrapWealthEducation)
                && Nonnegative(census.JobCapacityBySegment) && Nonnegative(census.FilledJobsBySegment);
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        private static bool Nonnegative(int[] values) { foreach (int value in values) if (value < 0) return false; return true; }

        private void EnsureNoModal()
        {
            _blockingModal = null;
            if (!UIView.HasModalInput()) return;
            _renderPreconditionsValid = false;
            UIComponent modal = UIView.GetModalComponent();
            _blockingModal = modal == null ? "Native modal stack is nonempty but its top component is unavailable."
                : modal.name + " (" + modal.GetType().FullName + ")";
            throw new InvalidOperationException("Render precondition failed: native modal input blocks cabinet evidence: "
                + _blockingModal + ". The test does not dismiss or modify modals.");
        }

        private void EnsureRenderPreconditions(UIComponent target)
        {
            _renderPreconditionsValid = false;
            _renderPath = null;
            _renderView = null;
            List<UIComponent> ancestors = new List<UIComponent>();
            UIComponent current = target;
            EnsureVisualTargetIdentity(target);
            while (current != null && ancestors.Count < 32)
            {
                if (ancestors.Contains(current)) throw new InvalidOperationException("Render precondition failed: cyclic UI parent chain.");
                ancestors.Add(current);
                current = current.parent;
            }
            if (current != null) throw new InvalidOperationException("Render precondition failed: UI parent chain exceeds 32 components.");
            if (ancestors.Count == 0) throw new InvalidOperationException("Render precondition failed: target has no UI hierarchy.");
            List<string> path = new List<string>();
            float opacity = 1f;
            string failure = null;
            // UIView.RenderComponent multiplies opacity from the outside in and returns
            // before visiting children when the product is <= 0.005. Reproduce that gate,
            // including invisible, disabled, and inactive ancestors, using public APIs.
            for (int i = ancestors.Count - 1; i >= 0; --i)
            {
                UIComponent component = ancestors[i];
                float effective;
                bool survives = OpacitySurvivesNativeCull(opacity, component.opacity, out effective);
                opacity = effective;
                bool active = component.gameObject.activeSelf;
                path.Add("{\"name\":" + Json(component.name) + ",\"type\":" + Json(component.GetType().FullName)
                    + ",\"enabled\":" + Bool(component.enabled) + ",\"activeSelf\":" + Bool(active)
                    + ",\"visibleSelf\":" + Bool(component.isVisibleSelf)
                    + ",\"opacity\":" + JsonFloat(component.opacity) + ",\"effectiveOpacity\":" + JsonFloat(effective) + "}");
                if (failure == null && (!component.enabled || !active || !component.isVisibleSelf || !survives))
                    failure = component.name + " (enabled=" + component.enabled + ", activeSelf=" + active
                        + ", visibleSelf=" + component.isVisibleSelf + ", effectiveOpacity=" + effective.ToString("R", CultureInfo.InvariantCulture) + ")";
            }
            _renderPath = "[" + string.Join(",", path.ToArray()) + "]";
            EnsureViewCanRender(target, ancestors[ancestors.Count - 1]);
            if (failure != null) throw new InvalidOperationException("Render precondition failed: native UIView would cull the cabinet hierarchy at "
                + failure + ". See renderPreconditionPath in the report.");
            EnsureNoModal();
            _renderPreconditionsValid = true;
        }

        private void EnsureViewCanRender(UIComponent target, UIComponent outermost)
        {
            UIView view = target.GetUIView();
            Camera camera = view == null ? null : view.uiCamera;
            // GetUIView can fall back to GetAView for detached components. Native
            // GetTopLevelComponents renders only direct transform children of the view.
            bool attached = view != null && outermost.transform.parent == view.transform;
            _renderView = "{\"name\":" + Json(view == null ? null : view.name)
                + ",\"enabled\":" + Bool(view != null && view.enabled)
                + ",\"activeInHierarchy\":" + Bool(view != null && view.gameObject.activeInHierarchy)
                + ",\"topLevelAttached\":" + Bool(attached)
                + ",\"cameraName\":" + Json(camera == null ? null : camera.name)
                + ",\"cameraEnabled\":" + Bool(camera != null && camera.enabled)
                + ",\"cameraActiveInHierarchy\":" + Bool(camera != null && camera.gameObject.activeInHierarchy) + "}";
            if (view == null || !view.enabled || !view.gameObject.activeInHierarchy || !attached
                || camera == null || !camera.enabled || !camera.gameObject.activeInHierarchy)
                throw new InvalidOperationException("Render precondition failed: cabinet is detached from an active enabled UIView or its active enabled UI camera. See renderView in the report.");
        }

        private static bool OpacitySurvivesNativeCull(float inherited, float local, out float effective)
        {
            effective = inherited * local;
            return Finite(inherited) && Finite(local) && local >= 0f && local <= 1f
                && Finite(effective) && effective > 0.005f;
        }

        private static string JsonFloat(float value)
        {
            return Finite(value) ? value.ToString("R", CultureInfo.InvariantCulture) : "null";
        }

        private bool LoadingOverlayReadyForCapture()
        {
            // Native LoadLevelComplete marks readiness and calls Complete(), which only
            // starts a fade. LoadingAnimation.Update disables itself at alpha < 0.001;
            // its OnDisable then disables this same public Camera component.
            LoadingAnimation animation = Singleton<LoadingManager>.instance.LoadingAnimationComponent;
            Camera camera = animation == null ? null : animation.GetComponent<Camera>();
            bool animationRendering = animation != null && animation.enabled && animation.gameObject.activeInHierarchy;
            bool cameraRendering = camera != null && camera.enabled && camera.gameObject.activeInHierarchy;
            float now = Time.realtimeSinceStartup;
            if (animation == null || camera == null || animationRendering || cameraRendering)
                _loadingOverlayClearSince = -1f;
            else if (_loadingOverlayClearSince < 0f) _loadingOverlayClearSince = now;
            _loadingOverlayClear = _loadingOverlayClearSince >= 0f && now - _loadingOverlayClearSince >= 0.25f;
            if (!_loadingOverlayClear) _renderPreconditionsValid = false;
            _loadingOverlayState = "{\"componentPresent\":" + Bool(animation != null)
                + ",\"componentEnabled\":" + Bool(animation != null && animation.enabled)
                + ",\"componentActiveInHierarchy\":" + Bool(animation != null && animation.gameObject.activeInHierarchy)
                + ",\"cameraPresent\":" + Bool(camera != null) + ",\"cameraEnabled\":" + Bool(camera != null && camera.enabled)
                + ",\"cameraActiveInHierarchy\":" + Bool(camera != null && camera.gameObject.activeInHierarchy)
                + ",\"requiredStableSeconds\":0.25,\"observedStableSeconds\":"
                + JsonFloat(_loadingOverlayClearSince < 0f ? 0f : now - _loadingOverlayClearSince) + "}";
            if (animation == null || camera == null)
                throw new InvalidOperationException("Native loading-animation component/camera is unavailable; overlay clearance cannot be established.");
            return _loadingOverlayClear;
        }

        private void EnsureOwnedReady()
        {
            if (!OwnsCurrentCity() || !ModRuntime.IsLoaded || Singleton<LoadingManager>.instance.m_currentlyLoading)
                throw new InvalidOperationException("Test session changed during cabinet capture.");
        }

        private static bool CompletePng(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (stream.Length < 45) return false;
                    byte[] header = new byte[8];
                    if (stream.Read(header, 0, 8) != 8 || header[0] != 137 || header[1] != 80 || header[2] != 78 || header[3] != 71
                        || header[4] != 13 || header[5] != 10 || header[6] != 26 || header[7] != 10) return false;
                    stream.Seek(-12, SeekOrigin.End);
                    byte[] tail = new byte[12];
                    if (stream.Read(tail, 0, 12) != 12) return false;
                    return tail[0] == 0 && tail[1] == 0 && tail[2] == 0 && tail[3] == 0
                        && tail[4] == 73 && tail[5] == 69 && tail[6] == 78 && tail[7] == 68
                        && tail[8] == 174 && tail[9] == 66 && tail[10] == 96 && tail[11] == 130;
                }
            }
            catch (IOException) { return false; }
        }

        private void Finish(string phase, string error)
        {
            if (_cancelled) { CleanupCancelledTest(); return; }
            _stage = 99;
            _phase = phase;
            _error = error;
            try
            {
                bool cleanupReady = RoundtripCleanupReady();
                _quitRequested = (cleanupReady || _civicProbe != null && _civicProbe.MayQuitUnderOwnedPause) && !_cancelled && _request.ControlledQuit && Singleton<LoadingManager>.exists
                    && !Singleton<LoadingManager>.instance.m_currentlyLoading && !Singleton<LoadingManager>.instance.m_applicationQuitting
                    && ((_request.Mode == "InventoryMaps" && MainMenuReady()) || OwnsCurrentCity());
                try { WriteReport(); } catch { }
                if (_quitRequested && !_cancelled) CommitControlledQuit();
            }
            catch { /* Fail closed: test instrumentation must never propagate into the game. */ }
            try
            {
                if (RoundtripCleanupReady() && (_pausedLoading == null || CanReleaseAutosavePause()))
                {
                    ReleaseAutosavePause();
                    DisposeHost();
                }
            }
            catch { /* Retain the cleanup guard while native session state is uncertain. */ }
        }

        private bool RoundtripCleanupReady()
        {
            if (IsOrganicRoundtripMode() && _roundtrip != null && _organicProbe != null)
            {
                // Preserve native work and the forced pause until the fixture has
                // either been safely released or explicitly refused stale cleanup.
                _roundtrip.StopAfterCurrentWork();
                if (!_roundtrip.FixtureCleanupSafe) return false;
                if (!_organicProbe.CleanupReady) _organicProbe.RequestCleanup();
                if (!_organicProbe.CleanupReady) return false;
                _roundtrip.RequestCleanup(); return _roundtrip.CleanupReady;
            }
            bool ready = true;
            if (_civicProbe != null)
            {
                if (!_civicProbe.CleanupReady) _civicProbe.RequestCleanup();
                ready = _civicProbe.CleanupReady;
            }
            if (_roundtrip != null)
            {
                _roundtrip.RequestCleanup();
                ready = _roundtrip.CleanupReady;
            }
            if (_classProbe != null)
            {
                if (!_classProbe.CleanupReady) _classProbe.RequestCleanup();
                ready = ready && _classProbe.CleanupReady;
            }
            if (_organicProbe != null)
            {
                if (!_organicProbe.CleanupReady) _organicProbe.RequestCleanup();
                ready = ready && _organicProbe.CleanupReady;
            }
            return ready;
        }

        private void ReleaseAutosavePause()
        {
            LoadingManager paused = _pausedLoading;
            _pausedLoading = null;
            if (paused != null) paused.autoSaveTimer.UnPause();
        }

        private void CommitControlledQuit()
        {
            if (_cancelled) return;
            if (_civicProbe != null) _civicProbe.NoteControlledQuit();
            _quitCommitted = true;
            try { Application.Quit(); enabled = false; }
            catch { _quitCommitted = false; throw; }
        }

        private bool IsOrganicMode() { return _request.Mode == "OrganicResidentialTestCity" || IsOrganicRoundtripMode(); }
        private bool IsOrganicRoundtripMode() { return _request.Mode == "OrganicResidentialSaveReloadTestCity"; }
        private bool IsDataOnlyMode() { return _request.Mode == "ResidentialClassTestCity" || IsOrganicMode() || _request.Mode == "CivicFundingTestCity"; }

        private void WriteReport()
        {
            var report = ModRuntime.CurrentReport;
            var demand = ModRuntime.CurrentDemand;
            StringBuilder json = new StringBuilder();
            json.Append("{\n\"schema\":1,\"session\":").Append(Json(_session)).Append(",\"startedUtc\":").Append(Json(_startedUtc.ToString("o")))
                .Append(",\"updatedUtc\":").Append(Json(DateTime.UtcNow.ToString("o"))).Append(",\"phase\":").Append(Json(_phase))
                .Append(",\"mode\":").Append(Json(_request.Mode)).Append(",\"createdCity\":").Append(Bool(_createdCity))
                .Append(",\"cancelled\":").Append(Bool(_cancelled))
                .Append(",\"quitRequested\":").Append(Bool(_quitRequested)).Append(",\"error\":").Append(Json(_error))
                .Append(",\"selectedAsset\":").Append(Json(_selectedAsset)).Append(",\"selectedChecksum\":").Append(Json(_selectedChecksum))
                .Append(",\"runtimeLoaded\":").Append(Bool(ModRuntime.IsLoaded)).Append(",\"reportValid\":").Append(Bool(_reportValid))
                .Append(",\"demandValid\":").Append(Bool(_demandValid)).Append(",\"cabinetVisible\":").Append(Bool(_uiVisible))
                .Append(",\"renderPreconditionsRequired\":").Append(IsDataOnlyMode() ? "[]" : "[\"exactNativeHostAndOwnedCouncil\",\"exactManualNoticeAndRealBriefing\",\"viewportAndAncestorClipping\",\"enabledActiveVisibleAncestors\",\"effectiveOpacityAbove0.005\",\"attachedActiveEnabledViewAndCamera\",\"noNativeModalInput\",\"nativeLoadingOverlayClear\"]")
                .Append(",\"renderPreconditionsValid\":").Append(Bool(_renderPreconditionsValid))
                .Append(",\"renderPreconditionPath\":").Append(_renderPath ?? "null").Append(",\"blockingModal\":").Append(Json(_blockingModal))
                .Append(",\"renderView\":").Append(_renderView ?? "null")
                .Append(",\"loadingOverlayClear\":").Append(Bool(_loadingOverlayClear)).Append(",\"loadingOverlayState\":").Append(_loadingOverlayState ?? "null")
                .Append(",\"nativeVisualQa\":").Append(IsOrganicMode() || _request.Mode == "CivicFundingTestCity" ? "null" : NativeVisualReportJson())
                .Append(",\"saveRoundtrip\":").Append(_roundtrip == null ? "null" : _roundtrip.ReportJson())
                .Append(",\"residentialClassProbe\":").Append(_classProbe == null ? "null" : _classProbe.ReportJson())
                .Append(",\"organicResidential\":").Append(_organicProbe == null ? "null" : _organicProbe.ReportJson())
                .Append(",\"civicFundingProbe\":").Append(_civicProbe == null ? "null" : _civicProbe.ReportJson())
                .Append(",\"visualReview\":").Append(Json(IsDataOnlyMode() ? "Not requested: simulation data probe" : "Required: engine PNGs are evidence, not a visual pass"))
                .Append(",\"reportSequence\":").Append(report == null ? "null" : report.Sequence.ToString(CultureInfo.InvariantCulture))
                .Append(",\"population\":").Append(report == null || report.Snapshot == null ? "null" : report.Snapshot.Population.ToString(CultureInfo.InvariantCulture))
                .Append(",\"graphNodeCount\":").Append(demand == null ? "null" : demand.Nodes.Length.ToString(CultureInfo.InvariantCulture))
                .Append(",\"graphConverged\":").Append(Bool(demand != null && demand.Converged))
                .Append(",\"censusFrame\":").Append(demand == null || demand.Census == null ? "null" : demand.Census.Frame.ToString(CultureInfo.InvariantCulture))
                .Append(",\"mapCandidates\":[").Append(string.Join(",", _maps.ToArray())).Append("]")
                .Append(",\"inventoryErrors\":[").Append(JsonList(_inventoryErrors)).Append("]")
                .Append(",\"capturedRenderChecks\":[").Append(string.Join(",", _renderChecks.ToArray())).Append("]")
                .Append(",\"engineScreenshots\":[").Append(JsonList(_screenshots)).Append("]\n}\n");
            string target = Path.Combine(_directory, "runtime-test-report.json");
            string temporary = Path.Combine(_directory, "runtime-test-report.tmp");
            File.WriteAllText(temporary, json.ToString(), new UTF8Encoding(false));
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
        }

        private static string Bool(bool value) { return value ? "true" : "false"; }
        private static string JsonList(List<string> values) { return string.Join(",", values.ConvertAll<string>(Json).ToArray()); }
        private static string Json(string value)
        {
            if (value == null) return "null";
            StringBuilder text = new StringBuilder("\"");
            foreach (char c in value)
            {
                if (c == '"' || c == '\\') text.Append('\\').Append(c);
                else if (c < 32) text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else text.Append(c);
            }
            return text.Append('"').ToString();
        }

        private static void RejectRedirectedPath(string path)
        {
            if ((Directory.Exists(path) || File.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Runtime test paths must not be reparse points: " + path);
        }

        private sealed class Request
        {
            public string Mode, AssetName, AssetChecksum;
            public int TimeoutSeconds;
            public bool ControlledQuit, CaptureAllTabs, OrganicInventoryOnly;
        }

        private static Request ReadRequest(string path)
        {
            if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Request exceeds 4096 bytes.");
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.Length == 0) continue;
                int split = line.IndexOf('=');
                if (split < 1 || line.Length > 1024 || line.IndexOf('\0') >= 0) throw new InvalidDataException("Malformed request line.");
                string key = line.Substring(0, split);
                string value = line.Substring(split + 1);
                if (key != "schema" && key != "mode" && key != "assetName" && key != "assetChecksum" && key != "timeoutSeconds"
                    && key != "controlledQuit" && key != "captureAllTabs" && key != "organicInventoryOnly") throw new InvalidDataException("Unknown request field: " + key);
                if (values.ContainsKey(key)) throw new InvalidDataException("Duplicate request field: " + key);
                values.Add(key, value);
            }
            Request request = new Request();
            if (Required(values, "schema") != "1") throw new InvalidDataException("Unsupported request schema.");
            request.Mode = Required(values, "mode");
            if (request.Mode != "InventoryMaps" && request.Mode != "NewTestCity" && request.Mode != "SaveReloadTestCity" && request.Mode != "ResidentialClassTestCity"
                && request.Mode != "OrganicResidentialTestCity" && request.Mode != "OrganicResidentialSaveReloadTestCity" && request.Mode != "CivicFundingTestCity") throw new InvalidDataException("Unknown test mode.");
            int maximumTimeout = request.Mode == "InventoryMaps" ? 120 : 600;
            if (!int.TryParse(Required(values, "timeoutSeconds"), NumberStyles.None, CultureInfo.InvariantCulture, out request.TimeoutSeconds)
                || request.TimeoutSeconds < 15 || request.TimeoutSeconds > maximumTimeout)
                throw new InvalidDataException("Timeout must be 15.." + maximumTimeout.ToString(CultureInfo.InvariantCulture) + " seconds for this mode.");
            request.ControlledQuit = StrictBoolean(Required(values, "controlledQuit"));
            request.CaptureAllTabs = StrictBoolean(Required(values, "captureAllTabs"));
            if (request.Mode == "ResidentialClassTestCity" && request.CaptureAllTabs)
                throw new InvalidDataException("ResidentialClassTestCity does not request UI captures.");
            if (request.Mode == "CivicFundingTestCity" && request.CaptureAllTabs)
                throw new InvalidDataException("CivicFundingTestCity does not request UI captures.");
            if (request.Mode == "OrganicResidentialTestCity" || request.Mode == "OrganicResidentialSaveReloadTestCity")
            {
                request.OrganicInventoryOnly = StrictBoolean(Required(values, "organicInventoryOnly"));
                if (request.CaptureAllTabs) throw new InvalidDataException("OrganicResidentialTestCity does not request UI captures.");
                if (request.Mode == "OrganicResidentialSaveReloadTestCity" && request.OrganicInventoryOnly)
                    throw new InvalidDataException("Organic save/reload requires the full populated fixture, not inventory-only.");
            }
            else if (values.ContainsKey("organicInventoryOnly")) throw new InvalidDataException("organicInventoryOnly is exclusive to OrganicResidentialTestCity.");
            if (request.Mode != "InventoryMaps")
            {
                request.AssetName = Required(values, "assetName");
                request.AssetChecksum = Required(values, "assetChecksum");
            }
            else if (values.ContainsKey("assetName") || values.ContainsKey("assetChecksum") || request.CaptureAllTabs)
                throw new InvalidDataException("InventoryMaps cannot contain map selection or captureAllTabs=true.");
            return request;
        }

        private static string Required(Dictionary<string, string> values, string key)
        {
            string value;
            if (!values.TryGetValue(key, out value) || value.Length == 0 || value != value.Trim()) throw new InvalidDataException("Missing or invalid request field: " + key);
            return value;
        }
        private static bool StrictBoolean(string value)
        {
            if (value == "true") return true;
            if (value == "false") return false;
            throw new InvalidDataException("Boolean values must be true or false.");
        }
    }
}
