# Native civic funding UI verification

From the repository root:

```powershell
./tools/NativeCivicFundingUiVerification/Run-Checks.ps1
```

The wrapper calls `scripts/Enter-Project.ps1`, reads the existing `config/local.paths.json` CitiesSkylines path, and puts every compiler output/intermediate under `.cache/native-civic-funding-ui-verification`. The project NuGet/cache/environment settings come from Enter-Project. No installed/preview Mod artifact is needed. The runner does not modify configuration or game files and does not build Mod, Core or Simulation.

## Two deliberately different checks

`Behaviour.csproj` compiles the actual production `NativeCivicFundingUI.cs` source into a net10 executable with explicit UI, Unity, runtime and Civic backend **facades**. Its 42 assertions exercise SIM-queue ordering, per-axis preservation/reset, single-pending commands, stale selection/bridge/modal/OFF guards, cancellation/release, unsupported and unavailable display, screen fit and ownership-aware cleanup. The fake backend rejects off-SIM calls. This is an executable ordering/layout/cleanup regression test, not proof of rendering, Unity lifetime semantics, concurrency scheduling or native gameplay.

`NativeCompile.csproj` compiles the actual `NativeCivicFundingUI.cs` **and `CabinetUI.cs`** against the installed read-only `Assembly-CSharp.dll`, `ColossalManaged.dll` and `UnityEngine.dll`, targeting net35. This validates those source files' native UI/game API names and signatures. Its backend/runtime/municipal UI/Core types are facades; it does not validate their production integration or execute the compiled DLL. No Core artifact dependency is required. Game references have `Private=false` and are not copied to outputs.

Neither project references, loads or queries Harmony/the provider. No native assembly is runtime-loaded in net10, no provider initializer or native activation runs, and neither check launches the game. Actual production integration compilation and in-game visual/behaviour QA remain separate work.

## Maintained sources

The projects use relative source links to the current production UI; the three C# test/facade sources live here, not solely in a transient cache. There are no project references or source links to production simulation/backend code. Changes to native selection APIs, control callbacks or cleanup require rerunning both checks.
