# Economic pair persistence harness

This links the production `Runtime/Lifecycle.cs`, `ModRuntime.cs`, `ModRuntime.Civic.cs` and `EconomicSaveEnvelope.cs` into an isolated executable. It uses the real Core/Simulation projects and their reviewed codecs. ICities, Unity, simulation scheduling, EconomicBridge, and CivicFundingBridge are explicit stubs. It does not build the Mod project or validate live bridge ownership/authority; that belongs to the bridge's separate checks/review.

From the project root in PowerShell:

```powershell
. ./scripts/Enter-Project.ps1
dotnet run --project tests/SC4Evolution.Persistence.Tests --artifacts-path .cache/persistence-harness/artifacts
dotnet build tests/SC4Evolution.Persistence.Tests -p:TargetFramework=net35 -p:TargetFrameworks=net35 -r win-x86 --artifacts-path .cache/persistence-harness/net35
```

The net35 executable can also run on the installed bundled Mono, temporarily setting `MONO_PATH` to the game's `Cities_Data/Managed` directory for its System.Core dependency. Restore the prior environment value afterwards. This starts only the console harness, never Cities. Harness runtime/log files are limited to `.cache/persistence-harness/runtime`; build outputs are in the specified cache directories.

Both net10 and bundled Mono/net35 passed **415 checks**. Coverage includes:

- Exact class/capacity byte preservation and binding; legitimate null capacity distinct from corrupt zero-length capacity.
- Every envelope byte individually corrupted, unknown framing/digest failure, oversize input, cross-class binding, partial/truncated authoritative entries.
- Present authoritative envelope always takes precedence; corrupt/read-error entries fail both ledgers without reading stale legacy data. Absent envelope permits legacy-only migration.
- Sticky failures across setter order; valid pending Shadow bytes retained; explicit NewGame/Scenario versus LoadGame evidence passed to the bridge.
- One owner capture per save; unavailable/inflight capture fails before any storage write and reports suspension/status.
- Single pair-key write before independent cabinet key. Simulated storage failures before or after accepting that pair leave either a complete old or complete new envelope; later cabinet failure cannot split its pair. The original legacy key remains untouched.

`SC4Evolution.EconomicPair.v1` is the new authoritative key. The envelope has `SC4EPAR1` magic, version 1, bounded class/capacity lengths (capacity -1 means absent), exact inner payloads and SHA256 framing checksum. Inner class and capacity codecs validate before any envelope is returned. Maximum envelope size is 32 MiB + 50 bytes.

Native `SerializableDataWrapper` catches individual extension exceptions and forwards a visible ModException, so a thrown callback does **not** promise global save abortion or cross-key rollback. The runtime reports failure/suspends economic effects and retains storage bytes; the authoritative class/capacity pair occupies one key precisely because the native API has only per-key assignment.

## Explicit RAW provider preparation boundary

The harness now passes **431 checks** on net10 and bundled Mono/net35 (415 persistence checks plus 16 preparation/session assertions). ModRuntime.Start captures the root and calls the immutable `EconomicBridge.PrepareStart(root)` contract before StateLock, then forwards that exact preparation object to Start(root, preparation) under the lock. No provider activation occurs in the stubbed Shadow path. Real guard activation/certification remains outside this harness.

The bounded callback test starts a dedicated test thread, attempts StateLock with a 500 ms bound and joins within 2 seconds; it reads the exact pending envelope while preparation is active. This verifies the placement of the production Runtime call outside StateLock without initializing a real provider. There is no ThreadPool or manual lock release in production. Tests also cover immutable configuration forwarding despite a callback changing stub configuration, visible activation/config failures, retained class/capacity load errors and payloads, Stop during preparation, reentrant completed Start and unexpected pre-lock exception. The existing session counter gates stale preparation; a rejection is logged and thrown before replacing any economic owner. Lifecycle's existing caller cleanup policy is unchanged.

Reproduction uses fresh isolated outputs `.cache/persistence-harness/start-preparation-net10` and `.cache/persistence-harness/start-preparation-net35` with the same commands above, replacing only artifacts-path. No Mod build, installed binary update, provider activation, game launch, preference/configuration or junction mutation was performed.

## Superseded Loading callback regression

The latest harness passes **446 checks** on net10 and bundled Mono/net35. A typed `ModRuntime.StartSupersededException` separates obsolete preparation from an ordinary owner failure. It is also used when PrepareStart throws after Stop or an inner Start has changed the existing session counter. Loading.OnLevelLoaded logs this obsolete callback without uninstalling UI or stopping the new/current session; normal errors retain their existing cleanup.

Regressions now invoke the production Loading.OnLevelLoaded callback, not only Start directly: completed inner Loading followed by outer return; completed inner Loading followed by a preparation exception; Stop followed by a preparation exception; ordinary preparation failure with unchanged session; and ordinary UI installation failure after a successful owner Start. The tests verify inner UI/payload/bridge survival, absence of duplicate cleanup after Stop and preservation of legitimate Uninstall/Stop behavior.

The contract stub now includes immutable ProviderReady and models readiness-before-activation. Cold RAW readiness failure has zero activation/install calls; retained Shadow makes zero readiness/activation/install calls. Dedicated bounded callbacks prove both readiness and activation preparation placement outside StateLock. These are Runtime contract tests, not execution of real provider constructors or validation of the actual EconomicBridge guard implementation.

Fresh isolated outputs: `.cache/persistence-harness/supersession-net10` and `.cache/persistence-harness/supersession-net35`. No Mod build, installation, provider activation or game operation was performed. SavedData, envelope and roundtrip oracle sources were not changed by this supersession fix.

## Civic funding lifecycle and save-key contract

The current harness passes **487 checks** on net10 and the installed bundled Mono/net35. It links the new production Civic Runtime partial and observes the actual `SavedData` key order/write order through the same isolated storage stub. The Civic owner is deliberately a lifecycle stub: it forwards opaque retained bytes exactly, records calls and allows bounded callbacks for lock/reentrancy checks. Exact disabled-byte preservation, raw corrupt-byte forwarding, and synthetic capture behavior are therefore harness contracts only; they do not validate the Civic ledger codec/backend's failure retention or live hook behavior.

Coverage added:

- The Civic key is read before both valid and invalid authoritative economic envelopes return. An absent key causes no Civic write; an unreadable Civic key blocks writes until `BeginEconomicLoad` and a new known payload, and the loaded owner receives the failure when a city starts.
- Disabled and corrupt-present byte arrays reach the Civic stub unchanged. Save captures all states before the first write, rejects payloads over the Civic limit before any write, forwards save failures to the Civic owner, and surfaces a Civic key failure after earlier independent key writes without claiming cross-key rollback.
- Preparation and disposal execute outside `StateLock`; bridge Start and Stop execute under it; hook release executes outside it. Tests verify the immutable Civic-enabled config snapshot survives callback mutation.
- Superseded startup disposes both preparations outside the lock. Throwing disposal cannot mask typed supersession or remove an inner owner/UI. Reentrant Loading or Stop during post-start disposal or prior-hook release is tested on both success and exception paths; stale outer callbacks must not install/uninstall UI or stop the replacement owner.
- A prior hook-release exception does not mask a successfully published replacement owner.

Fresh isolated outputs: `.cache/persistence-harness/civic-lifecycle-harness/net10`, `.cache/persistence-harness/civic-lifecycle-harness/net35`, and runtime/log files at `.cache/persistence-harness/civic-lifecycle-harness/runtime`. The net35 executable was run under the installed bundled Mono with `MONO_PATH` temporarily set to `Cities_Data/Managed`. No Mod build, Preview build, installation, provider activation, or game launch was performed.

Reproduction from PowerShell at the project root:

```powershell
. ./scripts/Enter-Project.ps1
dotnet run --project tests/SC4Evolution.Persistence.Tests --artifacts-path .cache/persistence-harness/civic-lifecycle-harness/net10
dotnet build tests/SC4Evolution.Persistence.Tests -p:TargetFramework=net35 -p:TargetFrameworks=net35 -r win-x86 --artifacts-path .cache/persistence-harness/civic-lifecycle-harness/net35
$oldMonoPath = $env:MONO_PATH
try { $env:MONO_PATH = 'C:\Program Files (x86)\Steam\steamapps\common\Cities_Skylines\Cities_Data\Managed'; & 'C:\Program Files (x86)\Steam\steamapps\common\Cities_Skylines\Mono\bin\mono.exe' '.cache\persistence-harness\civic-lifecycle-harness\net35\bin\SC4Evolution.Persistence.Tests\debug_net35_win-x86\SC4Evolution.Persistence.Tests.exe' } finally { $env:MONO_PATH = $oldMonoPath }
```
