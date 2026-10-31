# SC4 demand-tax tuning extractor

Run from the project root after dot-sourcing `scripts/Enter-Project.ps1`:

```powershell
. .\scripts\Enter-Project.ps1
dotnet run --project .\tools\SC4TaxProfile\SC4TaxProfile.csproj -- --help
dotnet run --project .\tools\SC4TaxProfile\SC4TaxProfile.csproj -- --out analysis/generated/sc4-tax-profile/tax-demand.s4td
```

The tool reads one resource from the configured, read-only `SimCity_1.dat`: `6534284A:E7E2C2DB:4A1F3CE6`. It requires unique supported properties `0x4A1F38B5` and `0x4A1F38B6` as repeated `Float32` curves and `0x4A1F38B7` as a scalar `UInt32` penalty-day count. It validates both curves before writing a profile. Output is restricted to `analysis/generated/sc4-tax-profile/`, is create-only, and includes a provenance JSON with archive hash and property type/count metadata. Numeric tuning remains in the ignored generated `.s4td` file, not in this source tree.

The curve loading/evaluation rules and tax formula follow the clean-room implementation specification at pinned commit [`82feac1f6b5addb278e58d3815a66aee0de75623`](https://github.com/tloriato/OpenSC4/blob/82feac1f6b5addb278e58d3815a66aee0de75623/docs/specs/demand.md) (§§8.1, 11.1) and its response-curve foundation at [`foundations.md`](https://github.com/tloriato/OpenSC4/blob/82feac1f6b5addb278e58d3815a66aee0de75623/docs/specs/foundations.md) (§4). The game’s history stores tenths of a percent; the simulation API accepts ordinary percent points (for example, `9.0` means 9%).
