using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using ColossalFramework;
using ColossalFramework.UI;

namespace SC4Evolution.Mod.Diagnostics
{
    public sealed partial class AutonomousTestDriver
    {
        // Immutable, captured on Unity's main thread BEFORE subscribing to native logs.
        // The threaded log callback must not invoke Steam, Unity, or type resolution.
        private sealed class NativeDiagnosticSnapshot
        {
            private const string ExpectedGameHash = "DF557994A03C93D8BB4A14F80B1FBCC0FB29D547658442445D078D069BCDD9FE";
            private const string ExpectedColossalHash = "34701F61DEF31F5F9DF79D573CE19E0BC52575879BCC395D51B39F6D746A58B9";
            public readonly string Version, GameHash, ColossalHash, OwnedSession, CaptureError;
            public readonly bool ServicePointAbsent, HotelAbsent, RaceAbsent;
            public readonly bool PlazasUnowned, HotelsUnowned, RacesUnowned, TelemetryNotStarted;

            private NativeDiagnosticSnapshot(string version, string gameHash, string colossalHash, string ownedSession,
                bool servicePointAbsent, bool hotelAbsent, bool raceAbsent, bool plazasUnowned, bool hotelsUnowned,
                bool racesUnowned, bool telemetryNotStarted, string captureError)
            {
                Version = version; GameHash = gameHash; ColossalHash = colossalHash; OwnedSession = ownedSession;
                ServicePointAbsent = servicePointAbsent; HotelAbsent = hotelAbsent; RaceAbsent = raceAbsent;
                PlazasUnowned = plazasUnowned; HotelsUnowned = hotelsUnowned; RacesUnowned = racesUnowned;
                TelemetryNotStarted = telemetryNotStarted; CaptureError = captureError;
            }

            public static NativeDiagnosticSnapshot CaptureOnMainThread(AutonomousTestDriver owner)
            {
                string version = BuildConfig.applicationVersion;
                string gameHash = null, colossalHash = null;
                try
                {
                    Assembly game = typeof(LoadingManager).Assembly;
                    gameHash = HashAssemblyFile(game);
                    colossalHash = HashAssemblyFile(typeof(UIView).Assembly);
                    string owned = owner.OwnsCurrentCity() ? owner._session : null;
                    // Assembly.GetType scopes the absence check to the actual game DLL,
                    // avoiding the native TypeResolve logging probe being classified.
                    return new NativeDiagnosticSnapshot(version, gameHash, colossalHash, owned,
                        game.GetType("ServicePointGroupPanel", false) == null,
                        game.GetType("HotelGroupPanel", false) == null,
                        game.GetType("RaceGroupPanel", false) == null,
                        !SteamHelper.IsDLCOwned(SteamHelper.DLC.PlazasAndPromenadesDLC),
                        !SteamHelper.IsDLCOwned(SteamHelper.DLC.HotelDLC),
                        !SteamHelper.IsDLCOwned(SteamHelper.DLC.RacesAndParadesDLC),
                        Singleton<PopsManager>.exists && !Singleton<PopsManager>.instance.telemetryStarted, null);
                }
                catch (Exception error)
                {
                    return new NativeDiagnosticSnapshot(version, gameHash, colossalHash, null,
                        false, false, false, false, false, false, false, error.Message);
                }
            }

            private bool PinnedBuild
            {
                get
                {
                    return CaptureError == null && Version == "1.21.1-f9" && !string.IsNullOrEmpty(OwnedSession)
                        && string.Equals(GameHash, ExpectedGameHash, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(ColossalHash, ExpectedColossalHash, StringComparison.OrdinalIgnoreCase);
                }
            }

            // Pure string/snapshot classification only. Unknown text/context stays fatal.
            public string Classify(string message, NativeDiagnosticContext context)
            {
                if (!PinnedBuild || context == null || !context.AcceptedOwnedReload || context.OwnedSession != OwnedSession
                    || (context.Phase != "WaitingForNativeOwnedReload" && context.Phase != "ValidatingRestoredStorageBeforeAnySecondSave")) return null;
                if (ServicePointAbsent && PlazasUnowned && message == "Assembly resolution failure. No assembly named 'ServicePointGroupPanel' was found.  [Serialization]")
                    return "Native optional toolbar type probe: absent ServicePointGroupPanel with PlazasAndPromenades DLC unowned; native SpawnSubEntry returns null.";
                if (HotelAbsent && HotelsUnowned && message == "Assembly resolution failure. No assembly named 'HotelGroupPanel' was found.  [Serialization]")
                    return "Native optional toolbar type probe: absent HotelGroupPanel with Hotel DLC unowned; native SpawnSubEntry returns null.";
                if (RaceAbsent && RacesUnowned && message == "Assembly resolution failure. No assembly named 'RaceGroupPanel' was found.  [Serialization]")
                    return "Native optional toolbar type probe: absent RaceGroupPanel with RacesAndParades DLC unowned; native SpawnSubEntry returns null.";
                if (TelemetryNotStarted && message == "Pushing before telemetry started.")
                    return "Native PopsManager.Push logged because the main-thread snapshot had telemetry not started; native Playthrough invokes Push after load. No consent or telemetry state was changed.";
                return null;
            }

            // Separate organic context: this never asserts that a reload was accepted.
            // The exact callsite was not present in the native callback's stack.
            public string ClassifyOrganicOptionalToolbar(string message, NativeOrganicDiagnosticContext context)
            {
                if (!PinnedBuild || context == null || (context.Mode != "OrganicResidentialTestCity" && context.Mode != "OrganicResidentialSaveReloadTestCity") || context.Phase != "OrganicResidentialActive"
                    || string.IsNullOrEmpty(context.OwnedSession) || context.OwnedSession != OwnedSession) return null;
                if (ServicePointAbsent && PlazasUnowned && message == "Assembly resolution failure. No assembly named 'ServicePointGroupPanel' was found.  [Serialization]")
                    return "SourceConsistentOptionalToolbarLookup; exactCallsiteUnconfirmed; absent ServicePointGroupPanel with PlazasAndPromenades DLC unowned; native SpawnSubEntry permits null.";
                if (HotelAbsent && HotelsUnowned && message == "Assembly resolution failure. No assembly named 'HotelGroupPanel' was found.  [Serialization]")
                    return "SourceConsistentOptionalToolbarLookup; exactCallsiteUnconfirmed; absent HotelGroupPanel with Hotel DLC unowned; native SpawnSubEntry permits null.";
                if (RaceAbsent && RacesUnowned && message == "Assembly resolution failure. No assembly named 'RaceGroupPanel' was found.  [Serialization]")
                    return "SourceConsistentOptionalToolbarLookup; exactCallsiteUnconfirmed; absent RaceGroupPanel with RacesAndParades DLC unowned; native SpawnSubEntry permits null.";
                return null;
            }

            // Civic context: same pinned-build absence evidence, scoped to the owned
            // CivicFundingTestCity session (any probe phase; native panel lookups occur
            // throughout the owned civic session). Exact callsite stays unconfirmed.
            public string ClassifyCivicOptionalToolbar(string message, NativeCivicDiagnosticContext context)
            {
                if (!PinnedBuild || context == null || context.Mode != "CivicFundingTestCity"
                    || string.IsNullOrEmpty(context.OwnedSession) || context.OwnedSession != OwnedSession) return null;
                if (ServicePointAbsent && PlazasUnowned && message == "Assembly resolution failure. No assembly named 'ServicePointGroupPanel' was found.  [Serialization]")
                    return "SourceConsistentOptionalToolbarLookup; exactCallsiteUnconfirmed; absent ServicePointGroupPanel with PlazasAndPromenades DLC unowned; native SpawnSubEntry permits null.";
                if (HotelAbsent && HotelsUnowned && message == "Assembly resolution failure. No assembly named 'HotelGroupPanel' was found.  [Serialization]")
                    return "SourceConsistentOptionalToolbarLookup; exactCallsiteUnconfirmed; absent HotelGroupPanel with Hotel DLC unowned; native SpawnSubEntry permits null.";
                if (RaceAbsent && RacesUnowned && message == "Assembly resolution failure. No assembly named 'RaceGroupPanel' was found.  [Serialization]")
                    return "SourceConsistentOptionalToolbarLookup; exactCallsiteUnconfirmed; absent RaceGroupPanel with RacesAndParades DLC unowned; native SpawnSubEntry permits null.";
                return null;
            }

            public string ReportJson()
            {
                return "{\"version\":" + Json(Version) + ",\"assemblyCSharpSha256\":" + Json(GameHash)
                    + ",\"colossalManagedSha256\":" + Json(ColossalHash) + ",\"pinnedBuild\":" + Bool(PinnedBuild)
                    + ",\"ownedSession\":" + Json(OwnedSession) + ",\"captureError\":" + Json(CaptureError)
                    + ",\"servicePointTypeAbsent\":" + Bool(ServicePointAbsent) + ",\"plazasDlcUnowned\":" + Bool(PlazasUnowned)
                    + ",\"hotelTypeAbsent\":" + Bool(HotelAbsent) + ",\"hotelDlcUnowned\":" + Bool(HotelsUnowned)
                    + ",\"raceTypeAbsent\":" + Bool(RaceAbsent) + ",\"racesDlcUnowned\":" + Bool(RacesUnowned)
                    + ",\"popsExistsAndTelemetryNotStarted\":" + Bool(TelemetryNotStarted) + "}";
            }

            private static string HashAssemblyFile(Assembly assembly)
            {
                if (string.IsNullOrEmpty(assembly.Location)) throw new IOException("Native assembly has no verifiable on-disk location.");
                using (FileStream file = new FileStream(assembly.Location, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", string.Empty);
            }
        }

        private sealed class NativeDiagnosticContext
        {
            public readonly string Phase, OwnedSession;
            public readonly bool AcceptedOwnedReload;
            public NativeDiagnosticContext(string phase, string ownedSession, bool acceptedOwnedReload)
            { Phase = phase; OwnedSession = ownedSession; AcceptedOwnedReload = acceptedOwnedReload; }
        }

        private sealed class NativeOrganicDiagnosticContext
        {
            public readonly string Mode, Phase, OwnedSession;
            public readonly NativeDiagnosticSnapshot Snapshot;
            public NativeOrganicDiagnosticContext(string mode, string phase, string ownedSession, NativeDiagnosticSnapshot snapshot)
            { Mode = mode; Phase = phase; OwnedSession = ownedSession; Snapshot = snapshot; }
        }

        private sealed class NativeCivicDiagnosticContext
        {
            public readonly string Mode, OwnedSession;
            public readonly NativeDiagnosticSnapshot Snapshot;
            public NativeCivicDiagnosticContext(string mode, string ownedSession, NativeDiagnosticSnapshot snapshot)
            { Mode = mode; OwnedSession = ownedSession; Snapshot = snapshot; }
        }
    }
}
