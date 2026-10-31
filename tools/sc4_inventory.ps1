param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$configPath = Join-Path $ProjectRoot 'config/local.paths.json'
$paths = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$gameRoot = [string]$paths.SimCity4
if (-not (Test-Path -LiteralPath $gameRoot -PathType Container)) {
    throw "Configured SimCity4 directory does not exist: $gameRoot"
}

$exePath = Join-Path $gameRoot 'Apps/SimCity 4.exe'
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw "Expected executable was not found: $exePath"
}
$exe = Get-Item -LiteralPath $exePath

function Read-U32([System.IO.BinaryReader]$Reader) {
    return [uint64]$Reader.ReadUInt32()
}

$files = [System.Collections.Generic.List[object]]::new()
foreach ($file in (Get-ChildItem -LiteralPath $gameRoot -File -Filter '*.dat' | Sort-Object Name)) {
    $stream = [System.IO.File]::Open($file.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $reader = [System.IO.BinaryReader]::new($stream)
        try {
            $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            $length = [uint64]$stream.Length
            if ($length -lt 96) { throw "DBPF header shorter than 96 bytes: $($file.Name)" }
            $magic = [System.Text.Encoding]::ASCII.GetString($reader.ReadBytes(4))
            if ($magic -ne 'DBPF') { throw "Unexpected DBPF magic '$magic' in $($file.Name)" }

            $major = Read-U32 $reader
            $minor = Read-U32 $reader
            $null = $reader.ReadBytes(12)
            $created = Read-U32 $reader
            $modified = Read-U32 $reader
            $indexTypeVersion = Read-U32 $reader
            $indexCount = Read-U32 $reader
            $indexOffset = Read-U32 $reader
            $indexSize = Read-U32 $reader
            $holeCount = Read-U32 $reader
            $holeOffset = Read-U32 $reader
            $holeSize = Read-U32 $reader
            $indexVersion = Read-U32 $reader
            $null = $reader.ReadBytes(32)

            if ($indexTypeVersion -ne 7) { throw "Unsupported/unexpected index type version $indexTypeVersion in $($file.Name)" }
            if ($indexVersion -notin @(0, 2)) { throw "Unsupported index version $indexVersion in $($file.Name)" }
            $entrySize = if ($indexVersion -eq 2) { [uint64]24 } else { [uint64]20 }
            if ($indexCount -gt [uint64]::MaxValue / $entrySize) { throw "Index count overflow in $($file.Name)" }
            $requiredIndexBytes = $indexCount * $entrySize
            if ($requiredIndexBytes -gt $indexSize) { throw "Index count/size mismatch in $($file.Name): need $requiredIndexBytes, declared $indexSize" }
            if ($indexOffset -gt $length -or $indexSize -gt ($length - $indexOffset)) { throw "Index extends past end of $($file.Name)" }
            if ($holeCount -gt 0 -and ($holeOffset -gt $length -or $holeSize -gt ($length - $holeOffset))) { throw "Hole record extends past end of $($file.Name)" }

            $counts = [System.Collections.Generic.SortedDictionary[string, int]]::new([System.StringComparer]::Ordinal)
            $stream.Position = [long]$indexOffset
            for ([uint64]$i = 0; $i -lt $indexCount; $i++) {
                $typeId = Read-U32 $reader
                $null = Read-U32 $reader # group ID; retained structurally, not interpreted
                $null = Read-U32 $reader # instance ID
                if ($indexVersion -eq 2) { $null = Read-U32 $reader } # resource ID
                $location = Read-U32 $reader
                $size = Read-U32 $reader
                if ($location -gt $length -or $size -gt ($length - $location)) {
                    throw "Resource $i range extends past end of $($file.Name): offset $location size $size file $length"
                }
                $key = '0x{0:X8}' -f $typeId
                if (-not $counts.ContainsKey($key)) { $counts[$key] = 0 }
                $counts[$key]++
            }

            $typeFrequencies = @(
                foreach ($key in $counts.Keys) {
                    [ordered]@{ typeId = $key; count = $counts[$key] }
                }
            )
            $files.Add([ordered]@{
                name = $file.Name
                bytes = $length
                sha256 = $hash
                dbpf = [ordered]@{
                    headerBytes = 96
                    majorVersion = $major
                    minorVersion = $minor
                    createdUnixSeconds = $created
                    modifiedUnixSeconds = $modified
                    indexTypeVersion = $indexTypeVersion
                    indexEntryCount = $indexCount
                    indexOffset = $indexOffset
                    indexSize = $indexSize
                    holeCount = $holeCount
                    holeOffset = $holeOffset
                    holeSize = $holeSize
                    indexVersion = $indexVersion
                    indexEntryBytes = $entrySize
                    typeFrequencies = $typeFrequencies
                    validation = 'header, index and all resource offset/size bounds passed'
                }
            })
        }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
}

$report = [ordered]@{
    generatedUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    configuredInstallPath = $gameRoot
    executable = [ordered]@{
        path = $exePath
        fileVersion = $exe.VersionInfo.FileVersion
        productVersion = $exe.VersionInfo.ProductVersion
        bytes = [uint64]$exe.Length
    }
    dbpfFormatNotes = [ordered]@{
        byteOrder = 'little-endian'
        headerBytes = 96
        indexTypeVersionExpected = 7
        indexVersionEntryBytes = [ordered]@{ '0' = 20; '2' = 24 }
        typeFrequencies = 'Raw TypeID counts only; IDs are not assigned semantic names.'
    }
    files = @($files)
}

$generatedDir = Join-Path $ProjectRoot 'analysis/generated'
$analysisDir = Join-Path $ProjectRoot 'analysis'
New-Item -ItemType Directory -Force -Path $generatedDir, $analysisDir | Out-Null
$jsonPath = Join-Path $generatedDir 'sc4-inventory.json'
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $jsonPath -Encoding utf8

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# Installed SimCity 4 inventory')
$lines.Add('')
$lines.Add("Generated: $($report.generatedUtc)")
$lines.Add("Configured install: ``$gameRoot``")
$lines.Add("Executable: ``Apps/SimCity 4.exe``; file version ``$($exe.VersionInfo.FileVersion)``; product version ``$($exe.VersionInfo.ProductVersion)``; $($exe.Length) bytes.")
$lines.Add('')
$lines.Add('Inventory scope is top-level `*.dat` files only. Each DAT is SHA-256 hashed and read-only parsed. Header layout uses 96 bytes; index type version must be 7; index version 0 uses 20-byte entries and version 2 uses 24-byte entries. Index and every resource range were checked against file bounds. Resource type IDs are reported as raw hex values without semantic labels.')
$lines.Add('')
$lines.Add('| File | Bytes | SHA-256 | DBPF version | Index entries | Index v. | TypeID frequencies |')
$lines.Add('|---|---:|---|---|---:|---:|---|')
foreach ($item in $files) {
    $freq = ($item.dbpf.typeFrequencies | ForEach-Object { "$($_.typeId): $($_.count)" }) -join ', '
    $lines.Add("| $($item.name) | $($item.bytes) | ``$($item.sha256)`` | $($item.dbpf.majorVersion).$($item.dbpf.minorVersion) | $($item.dbpf.indexEntryCount) | $($item.dbpf.indexVersion) | $freq |")
}
$lines.Add('')
$lines.Add('Unknowns: timestamps are recorded as raw unsigned Unix seconds; their validity/calendar interpretation was not independently checked. TypeID meanings are not inferred. This inventory does not inspect archive payloads or copy commercial files.')
$lines.Add('')
$lines.Add('Format reference: [Simtropolis, “DBPF format” (SC4 Modding discussion)](https://community.simtropolis.com/forums/topic/14323-dbpf-format/), post #2 describes the SC4 header and index fields. The installed archives themselves were used to validate the actual offsets, counts and bounds.')
$markdownPath = Join-Path $analysisDir 'sc4-inventory.md'
$lines | Set-Content -LiteralPath $markdownPath -Encoding utf8

Write-Output "Wrote $jsonPath"
Write-Output "Wrote $markdownPath"
Write-Output "Parsed $($files.Count) top-level DAT files; total index entries: $(($files | ForEach-Object { $_.dbpf.indexEntryCount } | Measure-Object -Sum).Sum)."
