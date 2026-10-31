# GameplayIntegration.Tests

This is an isolated, pure harness for `GameplayIntegrationSession` (sprints 01, 02,
04, 05 and 06). It links the four existing sprint adapters and the orchestrator into
the test executable, so it does not reference Cities Skylines, Harmony, Unity or a
native simulation provider.

The executable covers:

- explicit opt-in OFF neutrality;
- each sprint enabled in isolation;
- all five sprints enabled together;
- deterministic reports and decision notices;
- immutable DTO validation (sizes, finite factors, non-negative observations,
  ordered identities and cause keys);
- city identity and stale-frame rejection.

Run from the repository root:

```powershell
dotnet run --project tests/SC4Evolution.GameplayIntegration.Tests/SC4Evolution.GameplayIntegration.Tests.csproj --configuration Release --no-launch-profile
dotnet build tests/SC4Evolution.GameplayIntegration.Tests/SC4Evolution.GameplayIntegration.Net35.csproj
```

No test invokes the game or writes native money, population, demand or simulation
state. `GameplayIntegration.Net35.csproj` is an isolated compilation target (a
library so the console test runner does not execute it); the production Mod project
still owns the normal game references.
