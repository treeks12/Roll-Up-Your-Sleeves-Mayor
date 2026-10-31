# Employment segmentation checks

Console checker (net10) for the sprint-01 segmented employment model. Runs the actual
`SC4Evolution.Simulation` project and compiles the real production adapter source
(`src/SC4Evolution.Mod/Economy/EmploymentSegmentAdapter.cs`) directly into the harness,
the same pattern as `SC4Evolution.IncomingDemand.Tests`; no game assembly is loaded and
nothing native executes.

Coverage: segment/cell keys and validation; qualification table (authored default,
monotone in education, wealth never gates eligibility, injection and rejection);
input immutability and validation; zero city; conservation of workforce and per-segment
filled jobs with an explicit mismatch channel; the four handoff scenarios (lack of jobs,
lack of qualified workers, vacancies without residents, residents without vacancies);
education-floor isolation (new office/high-tech never absorbs lower-education
unemployment); immigration gated by compatible vacancies + free home units + open entry
path with distinct blocking reasons; monotonicity for every segment; determinism across
identical histories; adapter opt-in (disabled returns null).

Run from the repository root (Enter-Project is dot-sourced by `scripts/Test.ps1`, which
discovers this project automatically):

```powershell
dotnet run --project tests/SC4Evolution.Employment.Tests -c Release
```
