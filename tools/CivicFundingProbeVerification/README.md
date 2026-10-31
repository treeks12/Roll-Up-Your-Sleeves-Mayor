# Civic funding probe verification

Run from the project root:

```powershell
./tools/CivicFundingProbeVerification/Run-Checks.ps1
```

The wrapper dot-sources `scripts/Enter-Project.ps1`; generated sources, requests, DLL and JSON results stay in `.cache/civic-funding-probe-verification`. It reads the installed game location from `config/local.paths.json`. The default compiler is the local .NET 9.0.308 Roslyn; `-RoslynDirectory` can select another installed Roslyn directory.

There are 3,089 checks of the **actual extracted** strict request parser and exact rational efficiency expectation, including all existing city modes, rejected Civic UI/Organic fields, bounds, and the 110% integer-ceiling edge. There are 24 checks of the **actual extracted** Civic cleanup/scene ownership/quit predicates with inert native-value and FIFO facades. They cover clean release, cancel and unload after bridge stop, rejected cleanup queue, release failure retaining pause, same-GUID metadata replacement, buffer replacement, different live bridge, changed budget keys and prior accepted work draining first. The lifecycle resource-release body is facaded; these checks do not prove that the game releases its buffers or offers. That is asserted separately by the native test.

`Compile-Metadata.ps1` compiles the actual parent driver, Civic partial, existing Organic/SaveReload sources, actual Civic bridge and pure ledger/codec against .NET 3.5 and installed game DLL **metadata**. `CompileFacades.cs` supplies inert internal hook/lifecycle/UI dependencies; methods throw or remain unavailable. The output DLL is never executed. Existing `artifacts/mod` assemblies are references only and are never overwritten. This is not a Mod/Preview build, provider run, or gameplay test.

The opt-in runtime mode is `CivicFundingTestCity`, launched by the main operator through `scripts/Start-RuntimeTest.ps1 -Mode CivicFundingTestCity -NoWorkshop` with `CivicFunding=SC4LocalV1` configured separately. It uses four paid native service fixtures and explicitly controlled production inputs. It does not create residents/patients, alter cash, force utility flags, seed RNG, bypass native placement/collision/fees, or claim organic growth or patient recovery. Native direct fixture creation may use locked facilities and trigger the prefab's authored create-pass milestones in this disposable city; those states are recorded separately.

The runtime report separates native maintenance request, original availability return, full native cash debit and actual Fetch-call count. A naturally absent shortage remains `cashShortfallObserved=false`. Unit expansion/no-expulsion is empty-unit structural coverage. Nonzero resource radius is checked on real resource calls; a freshly created inactive facility may have zero native display range. Canonical real `CaptureSave` bytes are checked without claiming disk save/reload.

Cleanup must validate the original scene/metadata/managers/buffer references, four fingerprints and captured budget keys. A stopped original bridge does not prevent native release; a different live bridge is refused. Resource release must finish before unpause. Terminal cleanup failure can authorize controlled exit of this exact disposable scene **while its owned pause remains held**; it cannot claim normal cleanup or quit a foreign scene. Programmatic foreign loading/owner replacement is not a proven forced-pause restoration path and can leave the persistent forced-pause bool unproved; the report records this explicitly. No concurrent foreign load, plugin disable or cancellation is part of the successful native proof.
