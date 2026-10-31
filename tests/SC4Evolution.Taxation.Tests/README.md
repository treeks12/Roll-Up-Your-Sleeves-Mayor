# Tax accounting checks

Console checker (net10) for the sprint-04 segmented tax accounting model. Runs the actual
`SC4Evolution.Simulation` project and compiles the real production adapter source
(`src/SC4Evolution.Mod/Economy/TaxAccountingAdapter.cs`) directly into the harness; no
game assembly is loaded and nothing native executes.

Coverage: the twelve account keys (three residential wealth, nine business segments);
windowed reaction history (inclusive-window maximum, immediate reaction to raises,
delayed recovery after cuts, duplicate/out-of-order rejection, capacity trim); input
validation and immutability; neutrality (modifier 1, collectible = base × rate, cash =
observed collection); rate increase/decrease responses with distinct residential and
business curves; the reaction delay (cut keeps the window maximum until the old high
expires); zero-collection periods (cash stays zero, shortfall recorded as delta); cash
grows only from observed collections, never from collectible income or meters (including
over-collection recorded as negative delta); curve validation (invalid factors stop the
model); codec round-trip with byte-identical re-save and restore-trajectory equality;
rejection of truncation/corruption/trailing bytes; adapter opt-in; determinism.

Run from the repository root:

```powershell
dotnet run --project tests/SC4Evolution.Taxation.Tests -c Release
```
