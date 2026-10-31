# Organic populated roundtrip driver verification

Run in a fresh PowerShell 7 process:

```powershell
pwsh -NoProfile -File ./tools/OrganicRoundtripVerification/Run-Checks.ps1
pwsh -NoProfile -File ./tools/OptionalToolbarClassifierVerification/Verify-OptionalToolbarClassifier.ps1
```

Both runners dot-source `scripts/Enter-Project.ps1`. Generated source, test requests, compilation output and result JSON stay under `.cache/organic-roundtrip-verification` or the classifier's existing project cache. They do not build the Mod project, write `artifacts/mod`, execute the provider, load installed game assemblies for execution or start Cities.

`Compile-DriverSlice.ps1` compiles the actual parent driver, Organic/OrganicRoundtrip/SaveReload/NativeDiagnostics partials, observer, envelope and pure oracle as an isolated net35/C#7.3 library. It references installed game DLL **metadata** and existing Core/Simulation/Mod metadata, with inert internal runtime/economic/UI facades in `CompileFacades.cs`. That library is never executed. Warnings for facade/source name overlap and fields used only by omitted visual partials are suppressed; the compiler still checks actual native API signatures used by these driver sources. This does not prove the real bridge lifecycle or behavior of the omitted modules.

`Run-Checks.ps1` extracts the actual protocol/coverage/prefab-contract/request-parser methods with Roslyn and executes them using the inert value fixtures in `ProtocolChecks.cs`. The checks cover the pending unload permit beyond the synchronous submission, one-time consumption/acknowledgment, cancellation, ownership/epoch/path/hash/pause and revocation failures; inverse HOME/citizen coverage including incoming/dead orphans; canonical prefab references and same-name changed AI/class/geometry/placement; and explicit request parsing with legacy inventory preserved. They prove those pure branches, not Unity timing, native serialization, live water/electricity, restoration of a family, or end-to-end cleanup. The driver audit and real native I/O still require independent source review and an engine run.

The classifier runner adds the explicit populated mode to the same pinned Organic phase only. It tests the exact three optional-toolbar messages and rejects reload-phase, foreign-session and telemetry messages in the Organic classifier. SaveReload's separate existing context rules remain unchanged.

The driver uses the existing observational `CityCensus.Capture` in its paused SIM action. That is an additional observation, not a graph publication: the production publication may have an earlier frame because of its sampling cadence. The driver requires the fresh frame to equal the paused native frame, exact equality of all four arrays actually used by `ApplyCensus`, coherent stock/revisions/Ack, zero invalid/orphan/unbound/unmapped observations, and unchanged class/capacity bytes before/after this capture. Divergence fails without forcing a sample, advancing simulation or repairing the graph. Both input sets are recorded in bounded receipts for review.

The JSON results record source hashes and narrow scopes. They are verification artifacts, not a runtime pass.

Cancellation regression checks execute the actual extracted cancel/unload handlers, native-work-settled predicate, parent cleanup drain, fixture cleanup wrapper/owner guard and forced-pause release callback with inert SIM FIFO and lifecycle/resource facades. They cover save/reload/checkpoint/loading still pending, stopped/changed economic owners, cleanup refusal, release ordering, lost speed ownership, foreign write refusal and legacy cancellation dispatch. Destruction itself is a counted facade; these checks do not run native release APIs or simulate native file I/O.

An accepted limitation remains open for this milestone: a **programmatic foreign LoadLevel concurrent with native I/O already accepted by the harness**, including after plugin disable removes the Loading extension, may carry the harness's global `ForcedSimulationPaused=true` into the foreign city. Native deserialization preserves that flag. The guard refuses to mutate foreign resource IDs, pause or speed and reports terminal `CleanupIncomplete`/`cleanupError`; that refusal is not proof that the lease was restored, and the foreign city may remain paused. A settled release attempt is therefore reported separately from confirmed pause restoration. No universal cancellation/concurrency safety is claimed.

The scoped runtime run accepted by the main task is a fresh `-NoWorkshop` process, one exclusive disposable city, no disable/cancel/external load during the proof, and only the harness's own save/reload. This acceptance does not make the harness a general native I/O controller or close the open concurrency limitation. Normal populated cancellation still drains accepted work, completes or safely refuses fixture cleanup, and only then releases the pause while its native session is still owned. Legacy SaveReload behavior is preserved.
