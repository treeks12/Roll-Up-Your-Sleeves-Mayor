# Civic funding factors and weighted maintenance

This pure adapter implements the verified school/health efficiency and coverage curves, with the four explicitly mapped SC4 expense pairs used as dimensionless weights. It never charges money or changes a native facility. A college cost split, native service hooks, persistence and UI are not implemented here. Evidence and integration boundaries are in `analysis/sc4-civic-funding-disassembly.md` and `analysis/cs1-civic-funding-integration-plan.md`.

Inputs are integer percentages from 0 through 120, an explicit adapter bound. Maintenance combines the two weighted items before one integer division and rejects an Int32 result overflow; this is a declared rounding policy, not a claim of bit-exact SC4 cash posting. Factors use double precision and are not native seat/patient counts.

The tests compare every supported percentage against an independent table interpolator, check independent effects and cost weights, final-only rounding, validation, overflow and monotonicity. From the project root:

```powershell
. ./scripts/Enter-Project.ps1
dotnet run --project tests/SC4Evolution.CivicFunding.Tests --artifacts-path .cache/civic-funding-verification/net10
dotnet build tests/SC4Evolution.CivicFunding.Tests -p:TargetFramework=net35 -p:TargetFrameworks=net35 -r win-x86 --artifacts-path .cache/civic-funding-verification/net35
```

The net35 executable can run on the installed game's `Mono/bin/mono.exe` with a temporary `MONO_PATH` pointing to `Cities_Data/Managed`, restored afterward. No game/provider/native method is invoked by these tests.
