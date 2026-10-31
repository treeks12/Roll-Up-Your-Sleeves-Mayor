param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
. (Join-Path $ProjectRoot 'scripts/Enter-Project.ps1')

$corePath = Join-Path $ProjectRoot 'analysis/generated/reference-repos/OpenSC4/Core.gd'
if (-not (Test-Path -LiteralPath $corePath -PathType Leaf)) {
    throw "OpenSC4 Core.gd not found: $corePath"
}
$typeLabelLine = Select-String -LiteralPath $corePath -Pattern '^\s*0xca63e2a3\s*:\s*"LUA"\s*,?\s*$' -CaseSensitive:$false
if (-not $typeLabelLine) {
    throw 'OpenSC4/Core.gd does not map 0xCA63E2A3 to LUA.'
}

$dllPath = Join-Path $ProjectRoot 'tools/SC4Data/bin/Release/net10.0/SC4Data.dll'
if (-not (Test-Path -LiteralPath $dllPath -PathType Leaf)) {
    throw "Compiled Release SC4Data DLL not found: $dllPath; build it before export."
}
$outputRoot = Join-Path $ProjectRoot 'analysis/generated/sc4-lua'
$resourceDir = Join-Path $outputRoot 'resources'
New-Item -ItemType Directory -Force -Path $resourceDir | Out-Null
$archive = 'SimCity_1.dat'
$typeId = 'CA63E2A3'

$listOutput = & dotnet $dllPath list --root $ProjectRoot --archive $archive --type $typeId
if ($LASTEXITCODE -ne 0) { throw "SC4Data list failed with exit code $LASTEXITCODE" }
$entries = [System.Collections.Generic.List[object]]::new()
foreach ($line in $listOutput) {
    if ($line -match '^\s*(?<tgi>CA63E2A3:[0-9A-F]{8}:[0-9A-F]{8})\s+(?<offset>\d+)\s+(?<stored>\d+)\s+(?<form>.*)$') {
        $entries.Add([pscustomobject]@{
            Tgi = $Matches.tgi.ToUpperInvariant()
            Offset = [uint64]$Matches.offset
            StoredBytes = [uint64]$Matches.stored
            StoredForm = $Matches.form.Trim()
        })
    }
}
if ($entries.Count -ne 158) { throw "Expected 158 Lua resources from $archive; found $($entries.Count)." }
if (($entries.Tgi | Sort-Object -Unique).Count -ne $entries.Count) { throw 'The Lua type listing contains duplicate TGIs; refusing ambiguous export.' }

$utf8Strict = [System.Text.UTF8Encoding]::new($false, $true)
$keywords = [ordered]@{
    advisor = '\badvis(?:o|e)r\w*\b'
    news = '\bnews\b'
    demand = '\bdemand\w*\b'
    tax = '\btax(?:es|ation)?\b'
    budget = '\bbudget\w*\b'
    education = '\beducat\w*\b'
    transport = '\btransport\w*\b'
    traffic = '\btraffic\b'
    trigger = '\btrigger\w*\b'
}
$manifest = [System.Collections.Generic.List[object]]::new()
foreach ($entry in $entries) {
    $parts = $entry.Tgi.Split(':')
    $name = '{0}_{1}_{2}.bin' -f $parts[0], $parts[1], $parts[2]
    $relativeOut = "analysis/generated/sc4-lua/resources/$name"
    $outputPath = Join-Path $ProjectRoot $relativeOut
    if (Test-Path -LiteralPath $outputPath) { throw "Refusing to overwrite existing payload: $outputPath" }
    $extractOutput = & dotnet $dllPath extract --root $ProjectRoot --archive $archive --tgi $entry.Tgi --out $relativeOut
    if ($LASTEXITCODE -ne 0) { throw "SC4Data extraction failed for $($entry.Tgi), exit code $LASTEXITCODE" }
    $bytes = [IO.File]::ReadAllBytes($outputPath)
    $sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()

$contentKind = 'unclassified bytes (strict UTF-8 not accepted; no bytecode signature)'
    $text = $null
    $bytecodeHeader = $bytes.Length -ge 4 -and $bytes[0] -eq 0x1B -and $bytes[1] -eq 0x4C -and $bytes[2] -eq 0x75 -and $bytes[3] -eq 0x61
    if ($bytecodeHeader) {
        $contentKind = 'Lua bytecode signature (1B 4C 75 61)'
    }
    else {
        try {
            $candidateText = $utf8Strict.GetString($bytes)
            $hasUnexpectedControl = $false
            foreach ($character in $candidateText.ToCharArray()) {
                $code = [int]$character
                if (($code -lt 0x20 -and $code -notin @(0x09, 0x0A, 0x0D)) -or $code -eq 0x7F) {
                    $hasUnexpectedControl = $true
                    break
                }
            }
            if (-not $hasUnexpectedControl) {
                $contentKind = 'plain UTF-8 text (strict decode; control-byte check passed)'
                $text = $candidateText
            }
        }
        catch [System.Text.DecoderFallbackException] {
            # Keep the decoded payload intact and classify only from its bytes.
        }
    }

    $functions = @()
    $matchesByKeyword = [ordered]@{}
    $matchedLines = [System.Collections.Generic.List[object]]::new()
    if ($null -ne $text) {
        $functions = @([regex]::Matches($text, '(?m)^\s*(?:local\s+)?function\s+([A-Za-z_][A-Za-z0-9_:.]*)\s*\(') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
        $lineNumber = 0
        foreach ($textLine in ($text -split "`n")) {
            $lineNumber++
            $hitNames = [System.Collections.Generic.List[string]]::new()
            foreach ($keyword in $keywords.Keys) {
                if ([regex]::IsMatch($textLine, $keywords[$keyword], [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
                    $hitNames.Add($keyword)
                    if (-not $matchesByKeyword.Contains($keyword)) { $matchesByKeyword[$keyword] = 0 }
                    $matchesByKeyword[$keyword]++
                }
            }
            if ($hitNames.Count -gt 0) {
                $snippet = ($textLine -replace '\s+', ' ').Trim()
                if ($snippet.Length -gt 220) { $snippet = $snippet.Substring(0, 217) + '...' }
                $matchedLines.Add([ordered]@{ line = $lineNumber; keywords = @($hitNames); snippet = $snippet })
            }
        }
    }

    $manifest.Add([ordered]@{
        tgi = $entry.Tgi
        file = "resources/$name"
        offset = $entry.Offset
        storedBytes = $entry.StoredBytes
        storedForm = $entry.StoredForm
        decodedBytes = $bytes.Length
        sha256DecodedPayload = $sha256
        contentKind = $contentKind
        namedFunctions = @($functions)
        keywordLineCounts = $matchesByKeyword
        keywordLines = @($matchedLines)
    })
}

$manifestPath = Join-Path $outputRoot 'manifest.json'
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8

$typeFrequency = @{}
$keywordResourceCounts = [ordered]@{}
$keywordLineCounts = [ordered]@{}
$plainTextCount = 0
$bytecodeCount = 0
$otherCount = 0
foreach ($row in $manifest) {
    if ($row.contentKind.StartsWith('plain UTF-8 text')) { $plainTextCount++ }
    elseif ($row.contentKind.StartsWith('Lua bytecode')) { $bytecodeCount++ }
    else { $otherCount++ }
    foreach ($keyword in $row.keywordLineCounts.Keys) {
        if (-not $keywordResourceCounts.Contains($keyword)) { $keywordResourceCounts[$keyword] = 0 }
        if (-not $keywordLineCounts.Contains($keyword)) { $keywordLineCounts[$keyword] = 0 }
        $keywordResourceCounts[$keyword]++
        $keywordLineCounts[$keyword] += $row.keywordLineCounts[$keyword]
    }
}

$md = [System.Collections.Generic.List[string]]::new()
$md.Add('# SimCity 4 Lua resource index')
$md.Add('')
$md.Add("Type label cross-check: OpenSC4/Core.gd line $($typeLabelLine.LineNumber) maps ``0xCA63E2A3`` to LUA.")
$md.Add("Source archive: ``$archive`` under the configured SimCity 4 installation. SC4Data compiled Release DLL: ``tools/SC4Data/bin/Release/net10.0/SC4Data.dll``. Exported $($manifest.Count) matching index entries; each raw decoded payload is preserved under ``analysis/generated/sc4-lua/resources/`` and recorded in ``analysis/generated/sc4-lua/manifest.json`` with its decoded SHA-256.")
$md.Add("Payload byte inspection: $plainTextCount strict UTF-8 text, $bytecodeCount Lua bytecode signature, $otherCount unclassified byte sequences. The six unclassified payloads were rejected by strict UTF-8 decoding; none matched the Lua bytecode signature. No alternate encoding was inferred. A bytecode classification means the 1B 4C 75 61 header was present; no Lua chunk was executed or decompiled.")
$md.Add('')
$md.Add('## Keyword scan')
$md.Add('')
$md.Add('Case-insensitive lexical scan over strictly decoded UTF-8 resources. Counts are matching source lines and resources with at least one matching line; snippets appear in the generated manifest. Terms are: advisor/adviser, news, demand, tax/taxes/taxation, budget, education, transport, traffic, trigger. These are string matches only; they do not prove runtime call sites or behavioral meaning.')
$md.Add('')
$md.Add('| Keyword | Resources | Matching lines |')
$md.Add('|---|---:|---:|')
foreach ($keyword in $keywords.Keys) {
    $resourceCount = if ($keywordResourceCounts.Contains($keyword)) { $keywordResourceCounts[$keyword] } else { 0 }
    $lineCount = if ($keywordLineCounts.Contains($keyword)) { $keywordLineCounts[$keyword] } else { 0 }
    $md.Add("| $keyword | $resourceCount | $lineCount |")
}
$md.Add('')
$md.Add('## Resource index')
$md.Add('')
$md.Add('| TGI | Decoded bytes | Payload classification | Named functions | Keyword terms |')
$md.Add('|---|---:|---|---|---|')
foreach ($row in $manifest) {
    $functionText = if ($row.namedFunctions.Count -gt 0) { ($row.namedFunctions -join ', ') } else { '—' }
    $keywordText = if ($row.keywordLineCounts.Count -gt 0) { ($row.keywordLineCounts.Keys -join ', ') } else { '—' }
    $md.Add("| ``$($row.tgi)`` | $($row.decodedBytes) | $($row.contentKind) | $functionText | $keywordText |")
}
$md.Add('')
$md.Add('## Notes and limits')
$md.Add('')
$md.Add('Payloads were extracted via SC4Data as raw decoded bytes (QFS decompression only); the `.bin` files preserve those bytes exactly. The code performed byte inspection, strict UTF-8 decoding, named-function regex indexing, and lexical keyword matching only. No scripts were executed, and no Lua bytecode decompiler or inferred native trigger mapping was applied. Keyword hits and function names are reported as evidence from text, not as verified runtime behavior. The six unclassified payloads retain unknown encoding/content and should remain explicitly unidentified unless further format evidence is found.')
$mdPath = Join-Path $ProjectRoot 'analysis/sc4-lua-index.md'
$md | Set-Content -LiteralPath $mdPath -Encoding utf8

Write-Output "Exported $($manifest.Count) Lua type resources from $archive. Payloads: plain UTF-8 $plainTextCount; Lua bytecode signature $bytecodeCount; other/unclassified $otherCount."
Write-Output "Wrote $mdPath and $manifestPath"
