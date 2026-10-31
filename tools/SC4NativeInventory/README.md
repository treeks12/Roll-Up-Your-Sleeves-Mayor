# SC4 native executable inventory

Run `tools/SC4NativeInventory/Run.ps1` from any PowerShell directory. It reads `SimCity 4.exe` from `config/local.paths.json`, does not execute it, and writes a JSON inventory under `analysis/generated/sc4-native/` plus the readable report at `analysis/sc4-native-inventory.md`.

The tool uses the .NET 10 `PEReader` for PE headers and the debug directory. Its bounded import/export reader maps RVAs through file-backed section ranges and checks every offset before reading. Startup runs synthetic boundary checks for RVA mapping, truncated values, and unterminated strings. String matches are bounded ASCII/UTF-16LE candidates; they are search clues only, not reconstructed simulator rules or evidence that a string is used.
