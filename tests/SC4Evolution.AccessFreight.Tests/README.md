# Access and freight checks

Console checker (net10) for the sprint-05 access/freight metrics. Runs the actual
`SC4Evolution.Simulation` project and compiles the real production adapter source
(`src/SC4Evolution.Mod/Economy/AccessFreightAdapter.cs`) directly into the harness; no
game assembly is loaded, nothing native executes and no traffic AI is touched.

Coverage: the four handoff scenarios — good connection (completion × reachable free
capacity × time budget), congestion (monotone time penalty, no cliff), missing external
connection (closed outlet with stock scores zero; internal flow without stock survives)
and destination without capacity (full destinations score zero; monotone in free
capacity); route-found vs trip-completed vs delivery-made separation (a visual route
never scores); road existence alone never scores; input validation; effect bands
(levels, linear productivity/demand multipliers, band-order validation); gradual
effects (monotone, continuous at boundaries); adapter opt-in and batch evaluation with
per-origin order; determinism.

Run from the repository root:

```powershell
dotnet run --project tests/SC4Evolution.AccessFreight.Tests -c Release
```
