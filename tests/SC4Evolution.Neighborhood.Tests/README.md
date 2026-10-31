# Neighborhood cycle checks

Console checker (net10) for the sprint-02 neighborhood model. Runs the actual
`SC4Evolution.Simulation` project and compiles the real production adapter source
(`src/SC4Evolution.Mod/Economy/NeighborhoodCycleAdapter.cs`) directly into the harness;
no game assembly is loaded and nothing native executes.

Coverage: distinct constructing/vacant/abandoned phases; attractiveness math (authored
weight table, monotone per factor, range validation); threshold validation (abandonment
bar at or below maintenance, positive durations); construction completion with and without
residents; maintenance grace (an attractiveness collapse never vacates on the same update,
recovery inside the grace keeps residents); abandonment after vacancy duration at low
attractiveness, and never under acceptable attractiveness; reoccupation requiring demand
plus the attractiveness bar; rehabilitation from abandoned requiring both again; observed
vacancy/reoccupation causes; resident replacement raising class only, never level;
generation reset by buildIndex reuse; report partitions (phase counts, reason counts, no
transition without cause); codec round-trip (byte-identical re-save) and rejection of
truncation, corruption, trailing bytes and bad magic; restored-model trajectory equality;
adapter opt-in/restore and stock conversion with missing-factor accounting; determinism.

Run from the repository root:

```powershell
dotnet run --project tests/SC4Evolution.Neighborhood.Tests -c Release
```
