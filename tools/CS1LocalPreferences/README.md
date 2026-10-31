# CS1LocalPreferences

Run `./tools/CS1LocalPreferences/Invoke-LocalPreferences.ps1` from the project root to inspect SC4Evolution's local enable state. Default execution is read only. `-SelfTest` uses synthetic bytes only. `-Enable` is reserved for an authorized offline enable after Cities has exited; it only enables the exact local SC4Evolution key and preserves every unrelated preference byte.

See `analysis/cs1-local-preferences.md` for runtime validation, observed key, backup behavior and constraints. Do not run the assembly directly before the wrapper has compiled its native hash helper.
