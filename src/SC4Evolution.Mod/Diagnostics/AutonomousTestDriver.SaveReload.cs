using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ColossalFramework;
using SC4Evolution.Core;
using SC4Evolution.Mod.Economy;
using SC4Evolution.Mod.Runtime;
using SC4Evolution.Simulation;
using UnityEngine;

namespace SC4Evolution.Mod.Diagnostics
{
    public sealed partial class AutonomousTestDriver
    {
        // This helper is reachable only through the explicit SaveReloadTestCity request.
        private sealed class NativeSaveRoundtrip
        {
            private const string CabinetKey = "SC4Evolution.Cabinet.v1";
            private const string ClassKey = "SC4Evolution.ResidentialClasses.v1";
            private const string EconomicPairKey = SavedData.EconomicPairKey;
            // Civic funding is persisted by Lifecycle.OnSaveData under the same
            // SerializableDataWrapper as the cabinet/economic envelopes. Keep it
            // in this native roundtrip checkpoint so a real SaveLevel/LoadLevel
            // can prove the on-disk bytes, rather than only exercising the
            // in-memory CivicFundingBridge codec.
            private const string CivicFundingKey = SavedData.CivicFundingKey;
            private readonly AutonomousTestDriver _owner;
            private readonly object _errorLock = new object();
            private readonly object _leaseLock = new object();
            private readonly List<string> _nativeErrors = new List<string>();
            private readonly List<string> _baselineNativeDiagnostics = new List<string>();
            private NativeDiagnosticSnapshot _diagnosticSnapshot;
            private volatile NativeDiagnosticContext _diagnosticContext;
            private Signal _pending;
            private Checkpoint _before, _saved, _restored;
            private int _step;
            private float _deadline;
            private string _path, _fileHash, _buildHash, _manifestDirectory, _coverage, _cleanupError;
            private long _fileLength;
            private AsyncTaskBase _saveTask;
            private volatile bool _forcedLease, _releaseQueued, _releaseDone, _interrupted;
            private bool _borrowedPause, _previousForced, _insideOwnReload, _saveStarted, _reloadStarted;
            private volatile bool _cleanupRequested;
            private bool _saveDone, _reloadDone, _subscribed, _cleanupReported, _validated;
            private SimulationManager _leaseManager;
            private readonly NativeOrganicResidentialProbe _organic;
            private byte[] _expectedRestoredLive;
            private string _expectedRestoredLiveHash;
            private bool _organicUnloadPending;

            public NativeSaveRoundtrip(AutonomousTestDriver owner)
            {
                _owner = owner;
            }
            public NativeSaveRoundtrip(AutonomousTestDriver owner, NativeOrganicResidentialProbe organic)
            { _owner = owner; _organic = organic; }
            public bool FixtureCleanupSafe { get { return NativeWorkSettled; } }
            public void StopAfterCurrentWork() { _cleanupRequested = true; }
            public bool IsSubmittingOwnedReload(string path, string hash)
            { return _organic != null && _organicUnloadPending && !_cancelled && !_cleanupRequested && !_interrupted && _forcedLease
                && path == _path && hash == _fileHash && _owner.OwnsCurrentCity(); }
            public void ConsumeOwnedUnload() { _organicUnloadPending = false; }
            public bool CivicOwnedReloadInProgress
            {
                // LoadingManager invokes the unload callback synchronously from
                // LoadLevel, before it returns the Coroutine and before
                // _reloadStarted can be assigned. Keep the civic fixture alive
                // during that handoff as soon as the borrowed checkpoint enters
                // its own reload call.
                get { return _borrowedPause && (_insideOwnReload || (_reloadStarted && !_reloadDone)) && !_cleanupRequested && !_interrupted; }
            }

            public void Start()
            {
                _diagnosticSnapshot = NativeDiagnosticSnapshot.CaptureOnMainThread(_owner);
                _diagnosticContext = new NativeDiagnosticContext("OwnedInitialCityBeforeSave", _owner._session, false);
                Application.logMessageReceivedThreaded += OnNativeLog;
                _subscribed = true;
                _pending = QueueCheckpoint(true);
            }

            // CivicFundingTestCity already owns the simulation pause while its
            // native fixtures and populated ledger are alive. Borrow that exact
            // lease for the save/reload so the roundtrip can audit the real
            // persisted civic payload without releasing the fixtures first.
            public void StartBorrowedPause()
            {
                _diagnosticSnapshot = NativeDiagnosticSnapshot.CaptureOnMainThread(_owner);
                _diagnosticContext = new NativeDiagnosticContext("OwnedCivicCityBeforeSave", _owner._session, false);
                Application.logMessageReceivedThreaded += OnNativeLog;
                _subscribed = true;
                SimulationManager simulation = Singleton<SimulationManager>.instance;
                if (simulation == null || !_owner.OwnsCurrentCity() || !simulation.ForcedSimulationPaused)
                    throw new InvalidOperationException("Civic save/reload requires the existing owned forced-pause lease.");
                _borrowedPause = true;
                _leaseManager = simulation;
                _previousForced = true;
                _forcedLease = true;
                _pending = QueueCheckpoint(false);
            }

            private sealed class Signal
            {
                public volatile bool Done;
                public Checkpoint Value;
                public string Error;
            }

            private sealed class Checkpoint
            {
                public uint Frame;
                public long DateTicks, Cash, LastCash;
                public string Session, CityName, MapName, Environment;
                public SimulationManager.UpdateMode Mode;
                public MapThemeMetaData Theme;
                public byte[] StoredCabinet, StoredLegacyClasses, StoredEconomic, StoredPairClasses, StoredCapacity, StoredCivic;
                public byte[] LiveCabinet, LiveEconomic, LiveClasses, LiveCapacity, LiveCivic;
                public CabinetReport Report;
                public ResidentialClassStatus ClassStatus;
            }

            public bool CleanupReady
            {
                get { return NativeWorkSettled && !_releaseQueued && !_forcedLease; }
            }

            private bool NativeWorkSettled
            {
                get
                {
                    return (!_saveStarted || _saveDone) && (!_reloadStarted || _reloadDone)
                        && (_pending == null || _pending.Done)
                        && Singleton<LoadingManager>.exists && !Singleton<LoadingManager>.instance.m_currentlyLoading
                        && !Singleton<LoadingManager>.instance.m_applicationQuitting;
                }
            }

            public bool Step()
            {
                if (_cancelled || _cleanupRequested) return false;
                if (_interrupted) throw new InvalidOperationException("Save test interrupted by an external unload; no further test I/O will be started.");
                ThrowNativeErrors();
                if (_step == 0)
                {
                    if (!ReadSignal()) return false;
                    _before = _pending.Value;
                    _pending = QueueCheckpoint(false);
                    SetPhase("ConfirmingPausedSaveCheckpoint");
                    _step = 1;
                }
                else if (_step == 1)
                {
                    if (!ReadSignal()) return false;
                    RequireExactNative(_before, _pending.Value);
                    _before = _pending.Value;
                    if (_before.StoredCabinet != null || _before.StoredEconomic != null)
                        throw new InvalidDataException("Fresh built-in QA map unexpectedly already contained cabinet or authoritative economic save keys.");
                    ValidateCabinet(_before.LiveCabinet);
                    EconomicSaveRoundtripOracle.DecodeRequired(_before.LiveEconomic, out _before.LiveClasses, out _before.LiveCapacity);
                    ValidateCivicCheckpoint(_before, "before-save");
                    if (_organic != null)
                    {
                        _expectedRestoredLive = EconomicSaveRoundtripOracle.PrepareStableCapacityRestore(_before.LiveEconomic, true);
                        _expectedRestoredLiveHash = HashBytes(_expectedRestoredLive);
                        // Persist the precomputed expectation BEFORE submitting reload.
                        using (FileStream expected = new FileStream(Path.Combine(_owner._directory, "expected-restored-live.economic"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            expected.Write(_expectedRestoredLive, 0, _expectedRestoredLive.Length);
                    }
                    EnsureNativeIdle();
                    PrepareOwnedPath();
                    WriteManifest("manifest.start.json");
                    if (File.Exists(_path) || Directory.Exists(_path)) throw new IOException("Owned save target appeared before SaveLevel; refusing overwrite.");
                    _deadline = Time.realtimeSinceStartup + 120f;
                    Coroutine native = Singleton<LoadingManager>.instance.SaveLevel(_path);
                    if (native == null) throw new InvalidOperationException("Native SaveLevel rejected the owned save.");
                    // Both native coroutines yield before queuing simulation I/O. A
                    // synchronous rejection/exception has not accepted that work.
                    _saveStarted = true;
                    _owner.StartCoroutine(Observe(native, true));
                    SetPhase("WaitingForNativeSave");
                    _step = 2;
                }
                else if (_step == 2)
                {
                    AsyncTaskBase task = LoadSaveStatus.activeTask;
                    if (task != null && task.name == "Saving") _saveTask = task;
                    if (_saveTask != null && _saveTask.failed) throw new InvalidDataException("Native Saving AsyncTask reported failure.");
                    if (Time.realtimeSinceStartup > _deadline) throw new TimeoutException("Native save exceeded 120 seconds; its coroutine is retained, not aborted.");
                    if (!_saveDone) return false;
                    _pending = QueueCheckpoint(false);
                    SetPhase("ValidatingActualSavedStorage");
                    _step = 3;
                }
                else if (_step == 3)
                {
                    if (!ReadSignal()) return false;
                    _saved = _pending.Value;
                    RequireExactNative(_before, _saved);
                    if (!BytesEqual(_before.LiveCabinet, _saved.StoredCabinet))
                        throw new InvalidDataException("Actual native cabinet storage does not match the paused live checkpoint after save callbacks.");
                    ValidateCabinet(_saved.StoredCabinet);
                    EconomicSaveRoundtripOracle.RequireSaved(_before.LiveEconomic, _saved.StoredEconomic,
                        _before.StoredLegacyClasses, _saved.StoredLegacyClasses);
                    RequireCivicSaved(_before, _saved);
                    ValidateSavedFile();
                    WriteManifest("manifest.saved.json");
                    EnsureNativeIdle();
                    SimulationMetaData reload = new SimulationMetaData();
                    reload.m_updateMode = SimulationManager.UpdateMode.LoadGame;
                    reload.m_CityName = _saved.CityName;
                    reload.m_MapThemeMetaData = _saved.Theme;
                    // Deliberately DO NOT supply ID, frame, date, environment, or money.
                    // Those values must be restored from the actual native .ccs stream.
                    _deadline = Time.realtimeSinceStartup + 180f;
                    if (_organic != null && HashFile(_path) != _fileHash) throw new InvalidDataException("Owned save file changed before intentional reload.");
                    if (_organic != null) { _organic.ArmOwnedReload(this, _path, _fileHash); _organicUnloadPending = true; }
                    _insideOwnReload = true;
                    try
                    {
                        Coroutine native = Singleton<LoadingManager>.instance.LoadLevel(_path, "Game", "InGame", reload);
                        if (native == null) throw new InvalidOperationException("Native filename LoadLevel rejected the owned reload.");
                        _reloadStarted = true;
                        _owner.StartCoroutine(Observe(native, false));
                    }
                    finally
                    {
                        _insideOwnReload = false;
                        if (_organic != null && !_reloadStarted) { _organicUnloadPending = false; _organic.RevokeUnusedReloadPermit(); }
                    }
                    SetPhase("WaitingForNativeOwnedReload");
                    _step = 4;
                }
                else if (_step == 4)
                {
                    if (Time.realtimeSinceStartup > _deadline) throw new TimeoutException("Native reload exceeded 180 seconds; its coroutine is retained, not aborted.");
                    LoadingManager loading = Singleton<LoadingManager>.instance;
                    if (!_reloadDone || loading.m_currentlyLoading || !loading.m_loadingComplete
                        || !loading.m_simulationDataLoaded || !loading.m_renderDataReady) return false;
                    if (!_owner.OwnsCurrentCity()) throw new InvalidDataException("Reload did not restore the saved native session identifier.");
                    if (!ModRuntime.IsLoaded || ModRuntime.CurrentReport == null || ModRuntime.CurrentReport.Snapshot == null
                        || !ModRuntime.CurrentReport.Snapshot.DataValid || !ValidateDemand()) return false;
                    _owner.EnsureNoModal();
                    _pending = QueueCheckpoint(false);
                    SetPhase("ValidatingRestoredStorageBeforeAnySecondSave");
                    _step = 5;
                }
                else if (_step == 5)
                {
                    if (!ReadSignal()) return false;
                    _restored = _pending.Value;
                    if (_organic != null && HashFile(_path) != _fileHash) throw new InvalidDataException("Owned save file changed during reload.");
                    RequireExactNative(_saved, _restored);
                    if (_restored.Mode != SimulationManager.UpdateMode.LoadGame || _restored.CityName != _saved.CityName
                        || _restored.MapName != _saved.MapName || _restored.Environment != _saved.Environment)
                        throw new InvalidDataException("Native reload metadata did not preserve the owned city.");
                    if (!BytesEqual(_saved.StoredCabinet, _restored.StoredCabinet))
                        throw new InvalidDataException("Restored native storage bytes differ before any second save.");
                    RequireCivicRestored(_saved, _restored);
                    if (!_borrowedPause)
                    {
                        if (_organic == null)
                            EconomicSaveRoundtripOracle.RequireRestored(_saved.StoredEconomic, _restored.StoredEconomic, _restored.LiveEconomic,
                                _saved.StoredLegacyClasses, _restored.StoredLegacyClasses);
                        else
                            EconomicSaveRoundtripOracle.RequireRestoredWithExpectedLive(_saved.StoredEconomic, _restored.StoredEconomic, _expectedRestoredLive, _restored.LiveEconomic,
                                _saved.StoredLegacyClasses, _restored.StoredLegacyClasses);
                    }
                    ValidateCabinetRestoration();
                    if (!_borrowedPause) ValidateClassRestoration();
                    ThrowNativeErrors();
                    if (_borrowedPause && _owner._civicProbe != null)
                        _owner._civicProbe.RebindAfterNativeReload();
                    if (_organic != null)
                    {
                        _pending = QueueOrganicAdoption();
                        SetPhase("AdoptingExactRestoredOrganicFixture"); _step = 6; return false;
                    }
                    _validated = true;
                    WriteManifest("manifest.reloaded.json");
                    SetPhase("NativeSaveReloadValidated");
                    _step = 6;
                    return true;
                }
                else if (_step == 6 && _organic != null && !_validated)
                {
                    if (!ReadSignal()) return false;
                    ThrowNativeErrors(); _validated = true;
                    _coverage += " Organic populated fixture restored exactly under pause; RAW live capacity matched the precomputed first-census transformation; no new admission counters inferred.";
                    WriteManifest("manifest.reloaded.json");
                    _organic.RequestCleanup(); SetPhase("ReleasingAdoptedOrganicFixtureUnderOwnedPause"); _step = 7; return false;
                }
                else if (_step == 7 && _organic != null)
                {
                    if (!_organic.CleanupReady) return false;
                    _organic.RequireSuccessfulFixtureRelease(); ThrowNativeErrors();
                    SetPhase("NativeOrganicSaveReloadValidatedAndFixtureReleased"); _step = 8; return true;
                }
                return _step == 6;
            }

            private bool ReadSignal()
            {
                if (!_pending.Done) return false;
                if (_pending.Error != null) throw new InvalidOperationException(_pending.Error);
                return true;
            }

            private IEnumerator Observe(Coroutine native, bool save)
            {
                yield return native;
                if (save) _saveDone = true; else _reloadDone = true;
            }

            private Signal QueueCheckpoint(bool acquirePause)
            {
                Signal result = new Signal();
                Singleton<SimulationManager>.instance.AddAction("SC4Evolution QA checkpoint", delegate
                {
                    try
                    {
                        SimulationManager sim = Singleton<SimulationManager>.instance;
                        lock (_leaseLock)
                        {
                            if (_cancelled || _interrupted || _cleanupRequested) throw new InvalidOperationException("Checkpoint cancelled before execution.");
                            if (!_owner.OwnsCurrentCity()) throw new InvalidOperationException("Checkpoint refused outside the owned test session.");
                            if (acquirePause)
                            {
                                if (sim.ForcedSimulationPaused) throw new InvalidOperationException("Forced pause is already owned by native UI or another component; test refused.");
                                _leaseManager = sim;
                                _previousForced = sim.ForcedSimulationPaused;
                                sim.ForcedSimulationPaused = true;
                                _forcedLease = true;
                                if (_organic != null) _organic.FreezeForSave();
                            }
                            if (!_forcedLease) throw new InvalidOperationException("Owned forced-pause lease was lost; exact checkpoint refused.");
                            if (!sim.ForcedSimulationPaused)
                            {
                                if (!_borrowedPause) throw new InvalidOperationException("Owned forced-pause lease was lost; exact checkpoint refused.");
                                // Native LoadLevel can recreate the simulation
                                // object without preserving this diagnostic pause.
                                // The civic owner still holds the lease, so restore
                                // it before reading the post-reload checkpoint.
                                _leaseManager = sim;
                                sim.ForcedSimulationPaused = true;
                            }
                        }
                        Checkpoint value = new Checkpoint();
                        value.Frame = sim.m_currentFrameIndex;
                        value.DateTicks = sim.m_metaData.m_currentDateTime.Ticks;
                        value.Cash = Singleton<EconomyManager>.instance.InternalCashAmount;
                        value.LastCash = Singleton<EconomyManager>.instance.LastCashAmount;
                        value.Session = sim.m_metaData.m_gameInstanceIdentifier;
                        value.CityName = sim.m_metaData.m_CityName;
                        value.MapName = sim.m_metaData.m_MapName;
                        value.Environment = sim.m_metaData.m_environment;
                        value.Mode = sim.m_metaData.m_updateMode;
                        value.Theme = sim.m_metaData.m_MapThemeMetaData;
                        value.StoredCabinet = CopyStorage(sim, CabinetKey, 262144);
                        value.StoredLegacyClasses = CopyStorage(sim, ClassKey, 16 * 1024 * 1024);
                        value.StoredEconomic = CopyStorage(sim, EconomicPairKey, EconomicSaveEnvelope.MaximumBytes);
                        value.StoredCivic = CopyStorage(sim, CivicFundingKey, CivicFundingLedgerCodec.MaximumPayloadBytes);
                        value.LiveCabinet = ModRuntime.GetSaveState();
                        value.LiveEconomic = ModRuntime.GetEconomicSaveEnvelope();
                        value.LiveCivic = ModRuntime.GetCivicSaveState();
                        EconomicSaveRoundtripOracle.DecodeRequired(value.LiveEconomic, out value.LiveClasses, out value.LiveCapacity);
                        if (value.StoredEconomic != null)
                            EconomicSaveRoundtripOracle.DecodeRequired(value.StoredEconomic, out value.StoredPairClasses, out value.StoredCapacity);
                        value.Report = ModRuntime.CurrentReport;
                        value.ClassStatus = ModRuntime.CurrentResidentialClasses;
                        ValidateCivicCheckpoint(value, "checkpoint");
                        if (value.LiveClasses != null) AuditLiveLedger(DecodeLedger(value.LiveClasses), value.ClassStatus);
                        if (_organic != null) _organic.AuditPausedFixture(value.LiveEconomic, _reloadStarted);
                        result.Value = value;
                    }
                    catch (Exception error) { result.Error = error.ToString(); }
                    finally { result.Done = true; }
                });
                return result;
            }
            private Signal QueueOrganicAdoption()
            {
                Signal result = new Signal();
                Singleton<SimulationManager>.instance.AddAction("SC4Evolution QA fixture adoption", delegate
                {
                    try
                    {
                        lock (_leaseLock)
                        {
                            if (_cancelled || _interrupted || _cleanupRequested || !_forcedLease || !_owner.OwnsCurrentCity()
                                || !ReferenceEquals(Singleton<SimulationManager>.instance, _leaseManager) || !_leaseManager.ForcedSimulationPaused)
                                throw new InvalidOperationException("Fixture adoption lost its owned pause/session.");
                            _organic.AdoptRestoredFixture(_expectedRestoredLive);
                        }
                    }
                    catch (Exception error) { result.Error = error.ToString(); }
                    finally { result.Done = true; }
                });
                return result;
            }

            private static byte[] CopyStorage(SimulationManager sim, string key, int maximum)
            {
                byte[] value = sim.m_SerializableDataWrapper.LoadData(key);
                if (value == null) return null;
                if (value.Length > maximum) throw new InvalidDataException("Native storage key exceeds bound: " + key);
                return (byte[])value.Clone();
            }

            private static void RequireExactNative(Checkpoint expected, Checkpoint actual)
            {
                if (actual.Session != expected.Session || actual.Frame != expected.Frame || actual.DateTicks != expected.DateTicks || actual.Cash != expected.Cash)
                    throw new InvalidDataException("Exact native roundtrip failed: frame " + expected.Frame + " -> " + actual.Frame
                        + ", date ticks " + expected.DateTicks + " -> " + actual.DateTicks + ", InternalCashAmount " + expected.Cash + " -> " + actual.Cash + ". No tolerance or balance repair is applied.");
            }

            private static AdvisorEngine ValidateCabinet(byte[] bytes)
            {
                if (bytes == null) throw new InvalidDataException("Cabinet key/state is absent.");
                AdvisorEngine engine = new AdvisorEngine();
                engine.LoadState(bytes); // Validates size, version, canonical payload, and CRC.
                if (!BytesEqual(engine.SaveState(), bytes)) throw new InvalidDataException("Cabinet codec roundtrip is not canonical.");
                return engine;
            }

            private static CivicFundingLedger DecodeCivicLedger(byte[] bytes)
            {
                if (bytes == null) return null;
                using (MemoryStream stream = new MemoryStream(bytes, false)) return CivicFundingLedgerCodec.Read(stream);
            }

            private static void ValidateCivicBytes(byte[] bytes, string scope)
            {
                if (bytes == null) return;
                CivicFundingLedger ledger = DecodeCivicLedger(bytes);
                if (ledger == null) throw new InvalidDataException("Civic funding " + scope + " payload decoded to null.");
                byte[] canonical;
                using (MemoryStream output = new MemoryStream())
                {
                    CivicFundingLedgerCodec.Write(output, ledger);
                    canonical = output.ToArray();
                }
                if (!BytesEqual(bytes, canonical))
                    throw new InvalidDataException("Civic funding " + scope + " payload is not canonical.");
            }

            private static void ValidateCivicCheckpoint(Checkpoint checkpoint, string scope)
            {
                ValidateCivicBytes(checkpoint.StoredCivic, scope + " stored");
                ValidateCivicBytes(checkpoint.LiveCivic, scope + " live");
            }

            private static void RequireCivicSaved(Checkpoint before, Checkpoint saved)
            {
                // Lifecycle.OnSaveData must persist exactly the live bridge bytes
                // observed at the paused checkpoint. A disabled bridge has no key;
                // an enabled bridge may legitimately persist a new empty ledger on
                // the first save, so only the live -> stored equality is asserted.
                ValidateCivicCheckpoint(saved, "after-save");
                if (!BytesEqual(before.LiveCivic, saved.StoredCivic))
                    throw new InvalidDataException("Actual native civic funding storage does not match the paused live checkpoint after save callbacks.");
            }

            private static void RequireCivicRestored(Checkpoint saved, Checkpoint restored)
            {
                ValidateCivicCheckpoint(restored, "after-reload");
                if (!BytesEqual(saved.StoredCivic, restored.StoredCivic))
                    throw new InvalidDataException("Restored native civic funding storage bytes differ before any second save.");
                if (!BytesEqual(saved.StoredCivic, restored.LiveCivic))
                    throw new InvalidDataException("Restored live civic funding bytes differ from the bytes loaded from the native save.");
                CivicFundingLedger stored = DecodeCivicLedger(saved.StoredCivic);
                CivicFundingLedger live = DecodeCivicLedger(restored.LiveCivic);
                if (stored != null && live != null && (stored.LedgerId != live.LedgerId || stored.Count != live.Count))
                    throw new InvalidDataException("Restored civic funding ledger identity/count differs from the saved payload.");
            }

            private void ValidateCabinetRestoration()
            {
                AdvisorEngine expected = ValidateCabinet(_saved.StoredCabinet);
                ValidateCabinet(_restored.StoredCabinet);
                ValidateCabinet(_restored.LiveCabinet);
                long savedSequence = BitConverter.ToInt64(_saved.StoredCabinet, 10);
                long samples = _restored.Report.Sequence - savedSequence;
                if (samples < 1 || samples > 16) throw new InvalidDataException("Live cabinet did not resume a bounded report sequence from saved history.");
                for (long i = 0; i < samples; ++i) expected.Evaluate(_restored.Report.Snapshot);
                if (!BytesEqual(expected.SaveState(), _restored.LiveCabinet))
                    throw new InvalidDataException("Live cabinet history/state does not match the decoded restored checkpoint plus its actual paused-city samples.");
            }

            private static ResidentialClassLedger DecodeLedger(byte[] bytes)
            {
                using (MemoryStream stream = new MemoryStream(bytes, false)) return ResidentialClassLedgerCodec.Read(stream);
            }

            private void ValidateClassRestoration()
            {
                ResidentialClassLedger saved = DecodeLedger(_saved.StoredPairClasses);
                ResidentialClassLedger stored = DecodeLedger(_restored.StoredPairClasses);
                if (_restored.LiveClasses == null) throw new InvalidDataException("Restored class ledger failed live binding or suppressed saving.");
                ResidentialClassLedger live = DecodeLedger(_restored.LiveClasses);
                if (saved.LedgerId != stored.LedgerId || saved.LedgerId != live.LedgerId
                    || !BytesEqual(_saved.StoredPairClasses, _restored.LiveClasses))
                    throw new InvalidDataException("Live class ledger identity/policy/generations/records differ from the restored authoritative envelope.");
                if (_before.ClassStatus == null || _restored.ClassStatus == null || _before.ClassStatus.Enabled != _restored.ClassStatus.Enabled)
                    throw new InvalidDataException("Live class bridge did not preserve its validated stage/effects state.");
                _coverage = saved.Count == 0
                    ? "Empty-state cabinet and authoritative economic-envelope persistence only; no populated lots, class selection, abandonment, or real-tax validation."
                    : _restored.ClassStatus.Enabled
                        ? "Cabinet and populated class ledger persisted; fingerprints audited against native buildings; active lot-class resident mirrors checked."
                        : "Cabinet and populated ledger persisted in Shadow/restoration mode; fingerprints audited; active lot-class mirroring not exercised.";
                _coverage += _saved.StoredCapacity == null
                    ? " Capacity payload was legitimately absent; capacity-ledger persistence was not exercised."
                    : " Exact bound capacity payload persisted; no RAW/Q gameplay or populated-capacity success is inferred.";
            }

            private static void AuditLiveLedger(ResidentialClassLedger ledger, ResidentialClassStatus status)
            {
                if (status == null || status.ImportedCount + status.SelectedCount != ledger.Count)
                    throw new InvalidDataException("Live class bridge count does not match its decoded ledger.");
                Dictionary<ushort, ResidentialLotRecord> records = new Dictionary<ushort, ResidentialLotRecord>();
                foreach (ResidentialLotRecord record in ledger.GetRecords())
                {
                    ResidentialLotIdentity current;
                    if (!ResidentialClassBridge.TryIdentity(record.Identity.BuildingId, out current) || !SameIdentity(record.Identity, current))
                        throw new InvalidDataException("Live native lot fingerprint differs for building " + record.Identity.BuildingId + ".");
                    records.Add(record.Identity.BuildingId, record);
                }
                if (!status.Enabled || ledger.Count == 0) return;
                Citizen[] people = Singleton<CitizenManager>.instance.m_citizens.m_buffer;
                const Citizen.Flags excluded = Citizen.Flags.Tourist | Citizen.Flags.DummyTraffic | Citizen.Flags.Dead;
                for (int i = 1; i < people.Length; ++i)
                {
                    if ((people[i].m_flags & (Citizen.Flags.Created | excluded)) != Citizen.Flags.Created) continue;
                    ResidentialLotRecord record;
                    if (records.TryGetValue(people[i].m_homeBuilding, out record)
                        && people[i].WealthLevel != (Citizen.Wealth)((int)record.OccupantClass - 1))
                        throw new InvalidDataException("Live lot-class citizen mirror differs from its restored home ledger.");
                }
            }

            private static bool SameIdentity(ResidentialLotIdentity a, ResidentialLotIdentity b)
            {
                return b != null && a.BuildingId == b.BuildingId && a.NativeBuildIndex == b.NativeBuildIndex
                    && a.OriginXBits == b.OriginXBits && a.OriginZBits == b.OriginZBits && a.PrefabName == b.PrefabName
                    && a.Service == b.Service && a.SubService == b.SubService && a.Width == b.Width && a.Length == b.Length;
            }

            private void EnsureNativeIdle()
            {
                _owner.EnsureOwnedReady();
                _owner.EnsureNoModal();
                AsyncTaskBase active = LoadSaveStatus.activeTask;
                if (SavePanel.isSaving || (active != null && !active.completedOrFailed))
                    throw new InvalidOperationException("Native save/load activity is already in progress; test refused.");
                if (!_forcedLease) throw new InvalidOperationException("No owned forced-pause lease.");
            }

            private void PrepareOwnedPath()
            {
                _manifestDirectory = Path.GetFullPath(Path.Combine(_owner._projectRoot,
                    "analysis/generated/runtime-tests/" + _owner._session + "/save-roundtrip"));
                string root = Path.GetFullPath(_owner._projectRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!_manifestDirectory.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Owned save escaped the project.");
                string ancestor = _manifestDirectory;
                while (!string.IsNullOrEmpty(ancestor) && (ancestor + Path.DirectorySeparatorChar).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                { RejectRedirectedPath(ancestor); ancestor = Path.GetDirectoryName(ancestor); }
                RejectRedirectedPath(_owner._projectRoot);
                if (Directory.Exists(_manifestDirectory) || File.Exists(_manifestDirectory)) throw new IOException("Owned save directory already exists; no reuse is permitted.");
                Directory.CreateDirectory(_manifestDirectory);
                _path = Path.Combine(_manifestDirectory, Guid.NewGuid().ToString("N") + ".ccs");
                if (File.Exists(_path) || Directory.Exists(_path)) throw new IOException("Owned save already exists; no overwrite is permitted.");
                _buildHash = HashFile(Path.Combine(_owner._projectRoot, "artifacts/mod/SC4Evolution.dll"));
            }

            private void ValidateSavedFile()
            {
                if (!File.Exists(_path)) throw new IOException("Native save did not create the owned file.");
                RejectRedirectedPath(_path);
                _fileLength = new FileInfo(_path).Length;
                if (_fileLength < 16 || _fileLength > 512L * 1024 * 1024) throw new InvalidDataException("Native save file size is outside the QA bound.");
                using (FileStream stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    uint version = (uint)(stream.ReadByte() << 24 | stream.ReadByte() << 16 | stream.ReadByte() << 8 | stream.ReadByte());
                    if (version != BuildConfig.SAVE_DATA_FORMAT_VERSION) throw new InvalidDataException("Owned file is not the expected native simulation format.");
                    stream.Position = 0;
                    using (SHA256 sha = SHA256.Create()) _fileHash = Hex(sha.ComputeHash(stream));
                }
            }

            private void WriteManifest(string name)
            {
                using (FileStream file = new FileStream(Path.Combine(_manifestDirectory, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (StreamWriter writer = new StreamWriter(file, new UTF8Encoding(false))) writer.Write(ReportJson());
            }

            public void BeforeLevelUnload()
            {
                try
                {
                    lock (_leaseLock)
                    {
                        if ((_insideOwnReload || (_organic != null && _organic.OwnedReloadWasObserved)
                            || (_borrowedPause && _reloadStarted && !_reloadDone)) && !_cancelled
                            && (_borrowedPause || _owner.OwnsCurrentCity()))
                        {
                            if (_organic != null && !_organic.ConsumeRoundtripUnloadAcknowledgment()) { _interrupted = true; }
                            else return;
                        }
                        // Atomic with acquisition's last check and flag mutation: an
                        // already queued acquisition either finishes before this lock
                        // (and gets a FIFO release), or observes interruption and cannot
                        // acquire after this callback saw leaseHeld=false.
                        _interrupted = true;
                        _diagnosticContext = new NativeDiagnosticContext("ExternalUnloadOrCancellation", _owner._session, false);
                    }
                    if (_organic != null) return; // Populated cleanup drains accepted work before releasing its pause.
                    // Native LoadLevel has not yet started its coroutine; a release
                    // queued here precedes deserialization into the foreign city.
                    QueueForcedRelease();
                }
                catch (Exception error) { _interrupted = true; _cleanupError = error.ToString(); }
            }

            public void RequestCleanup()
            {
                _cleanupRequested = true;
                try
                {
                    if (NativeWorkSettled && (_organic == null || _organic.CleanupReady)) QueueForcedRelease();
                    if (_releaseDone && !_cleanupReported)
                    {
                        if (_cleanupError != null) { _owner._phase = "Failed"; _owner._error = _cleanupError; }
                        _owner.WriteReport();
                        _cleanupReported = true;
                    }
                }
                catch { /* Keep the minimal guard; no native I/O/coroutine is cancelled. */ }
            }

            public void CancelBeforePluginRemoval()
            {
                if (_organic != null)
                {
                    // The populated fixture owns resources as well as a pause.
                    // Invalidate acquisition/submission atomically, but retain the
                    // pause until accepted native work AND fixture cleanup/refusal
                    // settle. The retained parent host owns that ordered drain.
                    lock (_leaseLock)
                    {
                        _cleanupRequested = true; _interrupted = true; _organicUnloadPending = false;
                        _diagnosticContext = new NativeDiagnosticContext("ExternalUnloadOrCancellation", _owner._session, false);
                    }
                    return;
                }
                // PluginManager removes Loading extensions after OnDisabled returns.
                // Cancel acquisition and queue release NOW, before that hook disappears,
                // even if a native save/watcher has not settled. The simulation queue
                // still orders the release after prior actions and before a future load.
                _cleanupRequested = true;
                BeforeLevelUnload();
            }

            private void QueueForcedRelease()
            {
                if (!_forcedLease || _releaseQueued || _releaseDone) return;
                _releaseQueued = true;
                Singleton<SimulationManager>.instance.AddAction("SC4Evolution QA pause cleanup", delegate
                {
                    try
                    {
                        SimulationManager sim = Singleton<SimulationManager>.instance;
                        if (!ReferenceEquals(sim, _leaseManager) || !_owner.OwnsCurrentCity())
                            _cleanupError = "Forced-pause cleanup missed the owned session; no foreign pause state was changed.";
                        else if (!_borrowedPause && sim.ForcedSimulationPaused) sim.ForcedSimulationPaused = _previousForced;
                        _forcedLease = false;
                    }
                    catch (Exception error) { _cleanupError = error.ToString(); }
                    finally { _releaseDone = true; _releaseQueued = false; }
                });
            }

            private void OnNativeLog(string message, string stack, LogType type)
            {
                if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
                NativeDiagnosticContext context = _diagnosticContext;
                string exact = message == null ? string.Empty : message.TrimEnd('\r', '\n');
                string reason = type == LogType.Error && _diagnosticSnapshot != null ? _diagnosticSnapshot.Classify(exact, context) : null;
                lock (_errorLock)
                {
                    if (reason != null)
                    {
                        if (_baselineNativeDiagnostics.Count < 32)
                            _baselineNativeDiagnostics.Add("{\"logType\":\"Error\",\"message\":" + Json(message) + ",\"stack\":" + Json(stack)
                                + ",\"reason\":" + Json(reason) + ",\"phase\":" + Json(context.Phase)
                                + ",\"ownedSession\":" + Json(context.OwnedSession) + ",\"capturedUtc\":" + Json(DateTime.UtcNow.ToString("o")) + "}");
                    }
                    else if (_nativeErrors.Count < 32) _nativeErrors.Add(message + "\n" + stack);
                }
            }

            private void ThrowNativeErrors()
            {
                lock (_errorLock) if (_nativeErrors.Count != 0) throw new InvalidOperationException("Native error during save/reload: " + _nativeErrors[0]);
            }

            public void ThrowIfFailure() { ThrowNativeErrors(); }

            public void DetachErrorCapture()
            {
                if (!_subscribed) return;
                Application.logMessageReceivedThreaded -= OnNativeLog;
                _subscribed = false;
            }

            private void SetPhase(string phase)
            {
                bool acceptedReload = _reloadStarted && !_interrupted && !_cancelled && _owner.OwnsCurrentCity()
                    && (phase == "WaitingForNativeOwnedReload" || phase == "ValidatingRestoredStorageBeforeAnySecondSave");
                _diagnosticContext = new NativeDiagnosticContext(phase, _owner._session, acceptedReload);
                _owner._phase = phase;
                _owner.WriteReport();
            }

            public string ReportJson()
            {
                List<string> errors;
                List<string> baseline;
                lock (_errorLock) { errors = new List<string>(_nativeErrors); baseline = new List<string>(_baselineNativeDiagnostics); }
                return "{\"route\":\"LoadingManager.SaveLevel(project .ccs) -> filename LoadLevel(LoadGame)\",\"validated\":" + Bool(_validated)
                    + ",\"oracleLayout\":\"EconomicPair.v1 authoritative; Cabinet.v1; optional legacy class key preserved; CivicFunding.v1 exact live/storage bytes\""
                    + ",\"civicFundingKey\":\"" + CivicFundingKey + "\""
                    + ",\"civicDiskSaveReloadCoverage\":" + Bool(_before != null && _saved != null && _restored != null
                        && (_before.LiveCivic != null || _saved.StoredCivic != null || _restored.StoredCivic != null || _restored.LiveCivic != null)
                        && BytesEqual(_before.LiveCivic, _saved.StoredCivic)
                        && BytesEqual(_saved.StoredCivic, _restored.StoredCivic)
                        && BytesEqual(_saved.StoredCivic, _restored.LiveCivic))
                    + ",\"expectedLiveRule\":" + Json(_organic == null ? "StrictExactSavedLive" : "RawStableCompletedFirstCensusV1")
                    + ",\"expectedLivePreparedBeforeReload\":" + Bool(_expectedRestoredLive != null) + ",\"expectedLiveSha256\":" + Json(_expectedRestoredLiveHash)
                    + ",\"cancellationPolicy\":" + Json(_organic == null ? "ExistingSaveReloadImmediateOrderedRelease" : "InvalidateImmediately;WaitAcceptedNativeWork;ResolveFixtureCleanupOrRefusal;ReleaseOwnedPause")
                    + ",\"openConcurrencyLimitation\":" + Json(_organic == null ? null : "A programmatic foreign LoadLevel concurrent with accepted native I/O, including after plugin disable removes the lifecycle hook, may inherit our global ForcedSimulationPaused flag. Refusing foreign writes does not prove lease restoration. This opt-in harness is not a general I/O controller; covered runtime scope is one exclusive new process with only its own reload and no concurrent disable/cancel/external load.")
                    + ",\"session\":" + Json(_owner._session) + ",\"mapAsset\":" + Json(_owner._selectedAsset) + ",\"mapChecksum\":" + Json(_owner._selectedChecksum)
                    + ",\"nativeSaveFormat\":" + BuildConfig.SAVE_DATA_FORMAT_VERSION.ToString(CultureInfo.InvariantCulture)
                    + ",\"saveDeadlineSeconds\":120,\"reloadDeadlineSeconds\":180,\"totalDeadlineSeconds\":" + _owner._request.TimeoutSeconds.ToString(CultureInfo.InvariantCulture)
                    + ",\"ownedFile\":" + Json(_path) + ",\"fileBytes\":" + _fileLength.ToString(CultureInfo.InvariantCulture)
                    + ",\"fileSha256\":" + Json(_fileHash) + ",\"modFileSha256\":" + Json(_buildHash)
                    + ",\"moduleVersionId\":" + Json(typeof(AutonomousTestDriver).Module.ModuleVersionId.ToString())
                    + ",\"saveCompleted\":" + Bool(_saveDone) + ",\"reloadCompleted\":" + Bool(_reloadDone)
                    + ",\"saveAccepted\":" + Bool(_saveStarted) + ",\"reloadAccepted\":" + Bool(_reloadStarted)
                    + ",\"observedSaveTask\":" + Bool(_saveTask != null) + ",\"observedSaveTaskFailed\":" + Bool(_saveTask != null && _saveTask.failed)
                    + ",\"forcedPauseLeaseHeld\":" + Bool(_forcedLease) + ",\"forcedPauseReleased\":" + Bool(_releaseDone && (_organic == null || _cleanupError == null))
                    + ",\"forcedPauseReleaseAttemptSettled\":" + Bool(_releaseDone)
                    + ",\"cleanupError\":" + Json(_cleanupError) + ",\"coverage\":" + Json(_coverage)
                    + ",\"before\":" + CheckpointJson(_before) + ",\"afterSave\":" + CheckpointJson(_saved) + ",\"afterReload\":" + CheckpointJson(_restored)
                    + ",\"nativeDiagnosticBaseline\":" + (_diagnosticSnapshot == null ? "null" : _diagnosticSnapshot.ReportJson())
                    + ",\"baselineNativeDiagnostics\":[" + string.Join(",", baseline.ToArray()) + "]"
                    + ",\"nativeErrors\":[" + JsonList(errors) + "]}";
            }

            public bool CivicDiskSaveReloadCoverage
            {
                get
                {
                    return _before != null && _saved != null && _restored != null
                        && (_before.LiveCivic != null || _saved.StoredCivic != null || _restored.StoredCivic != null || _restored.LiveCivic != null)
                        && BytesEqual(_before.LiveCivic, _saved.StoredCivic)
                        && BytesEqual(_saved.StoredCivic, _restored.StoredCivic)
                        && BytesEqual(_saved.StoredCivic, _restored.LiveCivic);
                }
            }

            private static string CheckpointJson(Checkpoint value)
            {
                if (value == null) return "null";
                ResidentialClassLedger ledger = null;
                string decodeError = null;
                try { if (value.StoredPairClasses != null || value.LiveClasses != null) ledger = DecodeLedger(value.StoredPairClasses ?? value.LiveClasses); }
                catch (Exception error) { decodeError = error.Message; }
                int[] classes = new int[3];
                if (ledger != null) foreach (ResidentialLotRecord record in ledger.GetRecords()) ++classes[(int)record.OccupantClass - 1];
                return "{\"session\":" + Json(value.Session) + ",\"frame\":" + value.Frame.ToString(CultureInfo.InvariantCulture)
                    + ",\"dateTicks\":" + value.DateTicks.ToString(CultureInfo.InvariantCulture)
                    + ",\"internalCashAmount\":" + value.Cash.ToString(CultureInfo.InvariantCulture)
                    + ",\"lastCashAmount\":" + value.LastCash.ToString(CultureInfo.InvariantCulture)
                    + ",\"mode\":" + Json(value.Mode.ToString()) + ",\"cabinetKey\":" + KeyJson(value.StoredCabinet)
                    + ",\"liveReportSequence\":" + (value.Report == null ? "null" : value.Report.Sequence.ToString(CultureInfo.InvariantCulture))
                    + ",\"economicPairKey\":" + KeyJson(value.StoredEconomic)
                    + ",\"civicFundingKey\":" + KeyJson(value.StoredCivic)
                    + ",\"liveCivicFunding\":" + KeyJson(value.LiveCivic)
                    + ",\"civicLedgerId\":" + Json(CivicLedgerId(value.StoredCivic) ?? CivicLedgerId(value.LiveCivic))
                    + ",\"civicLedgerRecords\":" + CivicLedgerCountJson(value.StoredCivic, value.LiveCivic)
                    + ",\"liveEconomicEnvelope\":" + KeyJson(value.LiveEconomic)
                    + ",\"legacyClassKey\":" + KeyJson(value.StoredLegacyClasses)
                    + ",\"storedClassPayload\":" + KeyJson(value.StoredPairClasses)
                    + ",\"liveClassPayload\":" + KeyJson(value.LiveClasses)
                    + ",\"storedCapacityPayload\":" + KeyJson(value.StoredCapacity)
                    + ",\"liveCapacityPayload\":" + KeyJson(value.LiveCapacity)
                    + ",\"ledgerId\":" + Json(ledger == null ? null : ledger.LedgerId.ToString())
                    + ",\"ledgerDecodeError\":" + Json(decodeError)
                    + ",\"policyVersion\":" + (ledger == null ? "null" : ledger.PolicyVersion.ToString(CultureInfo.InvariantCulture))
                    + ",\"policySha256\":" + Json(ledger == null ? null : Hex(ledger.PolicyHash))
                    + ",\"classCounts\":[" + classes[0] + "," + classes[1] + "," + classes[2] + "]"
                    + ",\"classBridgeEnabled\":" + Bool(value.ClassStatus != null && value.ClassStatus.Enabled)
                    + ",\"ledgerRecords\":" + (ledger == null ? "null" : ledger.Count.ToString(CultureInfo.InvariantCulture)) + "}";
            }

            private static string KeyJson(byte[] bytes)
            { return bytes == null ? "null" : "{\"bytes\":" + bytes.Length.ToString(CultureInfo.InvariantCulture) + ",\"sha256\":" + Json(HashBytes(bytes)) + "}"; }
            private static string CivicLedgerId(byte[] bytes)
            {
                try
                {
                    CivicFundingLedger ledger = DecodeCivicLedger(bytes);
                    return ledger == null ? null : ledger.LedgerId.ToString("D");
                }
                catch { return null; }
            }
            private static string CivicLedgerCountJson(byte[] stored, byte[] live)
            {
                try
                {
                    CivicFundingLedger ledger = DecodeCivicLedger(stored) ?? DecodeCivicLedger(live);
                    return ledger == null ? "null" : ledger.Count.ToString(CultureInfo.InvariantCulture);
                }
                catch { return "null"; }
            }
            private static bool BytesEqual(byte[] a, byte[] b)
            {
                if (a == null || b == null) return a == b;
                if (a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; ++i) if (a[i] != b[i]) return false;
                return true;
            }
            private static string HashBytes(byte[] bytes) { using (SHA256 sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes)); }
            private static string HashFile(string path)
            { using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) using (SHA256 sha = SHA256.Create()) return Hex(sha.ComputeHash(file)); }
            private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant(); }
        }
    }
}
