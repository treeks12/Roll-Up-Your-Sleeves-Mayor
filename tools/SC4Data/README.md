# SC4Data

SC4Data is a read-only console tool for inspecting DBPF 1.0 archives from the configured SimCity 4 Deluxe install. It lists index entries, selects one entry by TGI, inspects LTEXT, Exemplar/Cohort, and PNG signatures, and extracts a single resource. It does not edit or redistribute the installed archives.

Run it from the project root after dot-sourcing the project environment script:

```powershell
. .\scripts\Enter-Project.ps1
dotnet run --project .\tools\SC4Data\SC4Data.csproj -- list --archive SimCity_1.dat
dotnet run --project .\tools\SC4Data\SC4Data.csproj -- inspect --archive SimCity_1.dat --tgi 2026960B:00000000:00000001
dotnet run --project .\tools\SC4Data\SC4Data.csproj -- extract --archive SimCity_1.dat --tgi 2026960B:00000000:00000001 --out analysis/generated/example.ltext
dotnet run --project .\tools\SC4Data\SC4Data.csproj -- extract --archive SimCity_1.dat --tgi 2026960B:00000000:00000001 --out analysis/generated/example.txt --text-utf8
dotnet run --project .\tools\SC4Data\SC4Data.csproj -- ltext-index --archive SimCityLocale.DAT --out analysis/generated/sc4-text/SimCityLocale.DAT.ltext.jsonl --max-entries 10000 --preview-chars 180
dotnet run --project .\tools\SC4Data\SC4Data.csproj -- inspect --archive SimCity_1.dat --tgi 6534284A:88CD66E9:00000001
dotnet run --project .\tools\SC4Data\SC4Data.csproj -- exemplar-index --archive SimCity_1.dat --out analysis/generated/sc4-exemplars/SimCity_1.exemplars.jsonl --max-entries 10000
```

`--archive` accepts a path relative to the configured `SimCity4` directory or an absolute path inside that directory. `--root` can be supplied when launching from another working directory. The TGI format is three hexadecimal 32-bit values separated by colons. `list --type` filters by a raw hexadecimal type ID.

Extraction requires an explicit `--out` path under `analysis/generated/`. Existing output files are never overwritten. By default, extraction writes the resource payload bytes after QFS decompression, preserving LTEXT's UTF-16LE payload structure. `--text-utf8` is available only for LTEXT and writes its decoded text as UTF-8 without a byte-order mark.

`ltext-index` writes a bounded UTF-8 JSON Lines inventory containing each LTEXT TGI, sizes, compression state, control field, and a limited preview. It inspects at most 10,000 entries and allows at most 512 preview characters per entry. Invalid LTEXT payloads are recorded with a `decodeError` and no preview; direct inspection and `--text-utf8` extraction reject them. The command does not write full archive payloads.

Resources of type `6534284A` are decoded as Exemplar/Cohort binary records. `inspect` shows the signature, parent cohort TGI, the catalog-confirmed name/type fields when unambiguous, and a bounded display of the typed properties. `exemplar-index` writes UTF-8 JSON Lines under `analysis/generated/sc4-exemplars/` with every decoded numeric scalar/repetition, string, type code, and property ID. It checks at most 10,000 entries and caps the output at 512 MiB. Property IDs remain numeric in the property list; only top-level name and type are read from dictionary-confirmed IDs `0x20` and `0x10`.

The parser accepts DBPF major/minor version 1.0, index type version 7, and index versions 0 and 2. It checks the 96-byte header, declared index and hole ranges, entry ranges, index stride, duplicate selection, and the compression directory before exposing resources. Per-resource stored and decoded payloads are capped at 64 MiB; the index is capped at one million entries.

SC4 compression is determined from the special directory resource TGI `E86B1EEF:E86B1EEF:286B1F03`. Its rows contain a resource TGI followed by the decoded byte count, using four little-endian 32-bit fields. A listed resource must have the SC4 9-byte QFS wrapper: a 4-byte little-endian size prefix, `10 FB`, and a 3-byte big-endian decoded size. The size in the wrapper must agree with the indexed stored size and directory decoded size. The RefPack operation decoder checks all input and output bounds, requires a valid end marker, checks back-reference distance, and copies overlap byte by byte.

LTEXT is decoded only when the type ID is `2026960B`. The documented payload has a little-endian 16-bit code-unit count, a 16-bit control field, and UTF-16LE code units. Standard entries in the installed `SimCityLocale.DAT` use control value `0x0010` (bytes `00 10`). One entry uses `0x0000`, declares 144 code units, and contains only 144 remaining bytes with a plain-ASCII localization-suppression marker; this violates the documented UTF-16 length. SC4Data records that entry as `decodeError` in `ltext-index` and does not guess an alternate encoding. The control field is retained as metadata, while count and byte bounds are checked before decoding. No advisor names or other semantic resource IDs are inferred. PNG is reported only when the decoded payload begins with the PNG signature bytes `89 50 4E 47 0D 0A 1A 0A`.

Exemplar/Cohort data uses a 4-byte `EQZB` or `CQZB` signature, the `0x23232331` parent marker, a 12-byte parent TGI, a 32-bit property count, and then typed properties. Each property contains a 32-bit numeric ID, a zero reserved byte, and a 32-bit type code. The low nibble selects the value format; any nonzero bits in `0xF000` select a following repetition count for numeric arrays. Supported formats are unsigned 8/16/32/64-bit integers (`1`–`4`), signed 8/16/32/64-bit integers (`5`–`8`), IEEE float32/float64 (`9`/`A`), booleans (`B`), and strings (`C`). String values use the repeated wire form, where the following 32-bit count is a byte length. ASCII strings decode directly. A known-length string containing non-ASCII bytes is marked as a partial/unsupported string, its original bytes are retained as Base64, and the remaining properties continue to decode; its encoding is not guessed. Unknown formats or type-code bits, nonzero reserved bytes, and unexpected headers are reported as unsupported; malformed counts, truncation, or trailing bytes are reported as invalid.

Exemplar bounds are 65,535 properties per record, 1,000,000 numeric repetitions per property, 1 MiB per string, and the shared 64 MiB per-resource DBPF limit. The installed `SimCity_1.dat` contains 8,957 resources of type `6534284A`: the bounded index decoded 8,956 fully and retained one record as partial because of an unrecognized high-byte string. All 8,957 observed signatures were `EQZB`; `CQZB` is covered by the synthetic test fixture but was not observed in that archive.

The installed exemplar set contains negative values in the decoded signed 32-bit and signed 64-bit properties (120 and 1 occurrences respectively), consistent with those type tags. Non-finite floats were not present in the indexed archive.

## Format references

These links identify the source material used to implement the reader; SC4Data contains an independent implementation and does not copy source code from these projects.

- [Simtropolis: DBPF format, post #2](https://community.simtropolis.com/forums/topic/14323-dbpf-format/?comment=1086945&do=findComment) documents the 96-byte SC4 header, index type 7, 20-byte index-version-0 entries, 24-byte index-version-2 entries, and field order.
- [OpenSC4 `DBPF.gd`](https://github.com/Open-Source-Modding/OpenSC4/blob/main/addons/dbpf/DBPF.gd#L54-L87) parses the SC4 index and loads the compression directory; [`DBDF.gd`](https://github.com/Open-Source-Modding/OpenSC4/blob/main/addons/dbpf/DBDF.gd#L8-L11) and [`DBDFEntry.gd`](https://github.com/Open-Source-Modding/OpenSC4/blob/main/addons/dbpf/DBDFEntry.gd#L8-L12) show the directory's 16-byte TGI-plus-final-size rows.
- [OpenSC4 `DBPFSubfile.gd`](https://github.com/Open-Source-Modding/OpenSC4/blob/main/addons/dbpf/DBPFSubfile.gd#L42-L107) documents the SC4 9-byte resource wrapper and QFS literal/back-reference operations. Its code is used as a format cross-check, not as code copied into this tool.
- [DBPFSharp `QfsCompression.cs`](https://github.com/0xC0000054/DBPFSharp/blob/main/src/DBPFSharp/QfsCompression.cs#L31-L40) describes QFS/RefPack in SC4 DBPF files; its [decoder](https://github.com/0xC0000054/DBPFSharp/blob/main/src/DBPFSharp/QfsCompression.cs#L88-L217) documents the variable-length header fields and four RefPack control-code families.
- [OpenSC4 `LTEXTSubfile.gd`](https://github.com/Open-Source-Modding/OpenSC4/blob/main/addons/dbpf/LTEXTSubfile.gd#L23-L39) reads the character count, `0010` control value, and 16-bit Unicode code units used by SC4 LTEXT.
- [OpenSC4 `ExemplarSubfile.gd`](https://github.com/OpenSC4-org/OpenSC4/blob/fdc90c33e6e012b8c825f20ee10c51f55796df39/addons/dbpf/ExemplarSubfile.gd) is the public format cross-check for the `EQZB` signature, parent marker/TGI, property count and layout, type-word flags, numeric widths, and string byte-length handling. SC4Data independently applies explicit bounds, strict unknown-format reporting, and signed integer decoding.
- [OpenSC4 `exemplar_types.dict`](https://github.com/OpenSC4-org/OpenSC4/blob/fdc90c33e6e012b8c825f20ee10c51f55796df39/exemplar_types.dict) confirms that property IDs `0x10` and `0x20` are named “Exemplar Type” and “Exemplar Name”; remaining property IDs are preserved numerically.
- [W3C PNG Specification, §5.2](https://www.w3.org/TR/png-3/#5PNG-file-signature) defines the eight-byte PNG signature checked by `inspect`.

The installed reference was also checked directly, read-only. In `SimCity_1.dat`, the compression-directory resource has the expected TGI and its first row referenced `5AD0E817:BADB57F1:009C0410`: the DBPF entry stored 3,116 bytes, the QFS prefix contained 3,116, the QFS header declared 6,412 decoded bytes, and the DIR row declared 6,412. This sample validates the wrapper size checks against actual installed data.

The tool does not decode FSH, S3D, LUA, or other resources; identify portraits/advisors from IDs; infer missing property semantics; handle non-SC4 compression headers; or write modified DBPF archives. `inspect` reports unknown payloads as raw and only labels PNG when its signature is present.

## Headless checks

```powershell
. .\scripts\Enter-Project.ps1
dotnet run --project .\tests\SC4Data.Tests\SC4Data.Tests.csproj
```

The checks use synthetic archives and QFS streams; no game resources are included in the test project.
