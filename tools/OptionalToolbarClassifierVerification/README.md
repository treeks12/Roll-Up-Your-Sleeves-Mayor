# Optional toolbar classifier verification

Run from a fresh PowerShell 7 process:

```powershell
pwsh -NoProfile -File ./tools/OptionalToolbarClassifierVerification/Verify-OptionalToolbarClassifier.ps1
```

The entry point dot-sources `scripts/Enter-Project.ps1`, keeping compiler temporary files and caches under the project. Roslyn defaults to the installed .NET SDK 9.0.308 directory; pass `-RoslynDirectory` to select another installed Roslyn `bincore` directory. A fresh process is required to prevent previously compiled harness types from supplying stale evidence.

The runner extracts the actual `OnLog`, `OptionalToolbarDiagnosticsJson` and `BoundedModalText` methods from the organic partial, plus the driver's actual JSON helpers, and compiles them with the complete actual `AutonomousTestDriver.NativeDiagnostics.cs` in memory. The stand-in `LoadingManager`, `BuildConfig`, `SteamHelper`, `PopsManager`, `Singleton`, `UIView` and `LogType` types are inert test fixtures. Snapshot flags/pins are supplied through the real private snapshot constructor; runtime capture is not invoked. The Steam and ownership stubs throw if called.

The 55 assertions cover exact messages, preserved raw CR/LF, negative text/type/context/pin/DLC cases, context invalidation, bounded diagnostics, JSON and unchanged SaveReload classification. The explicit OrganicResidentialSaveReloadTestCity mode accepts the same three messages only in OrganicResidentialActive; foreign-session/reload-phase contexts and telemetry remain rejected by this classifier. These checks establish pure classification and callback behavior only. They do not validate main-thread snapshot capture, native caller identity, installed UI, infrastructure, water delivery or organic growth. No mod DLL is built, no installed game assembly is loaded and no engine/native API is invoked.

Results go to `.cache/optional-toolbar-classifier-verification/result.json`, including the source/harness hashes and explicit scope. The generated result is not a source file or a runtime pass.
