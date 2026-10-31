[CmdletBinding()]
param(
    [string]$SourceDirectory = (Join-Path $PSScriptRoot '..\analysis\generated\sc4-lua\resources'),
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\analysis\generated\sc4-advice-catalog\advice-records.jsonl')
)

$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath($SourceDirectory)
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $outputFullPath
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$generatedRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'analysis\generated'))
if (-not $outputFullPath.StartsWith($generatedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Output must stay under $generatedRoot"
}
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

# Each entry names the spelling used by the Lua source and the department
# category represented by that source file. The environment spelling
# intentionally preserves the source typo "environemt".
$sourceFunctions = @{
    'FF20C5FD' = @{ category = 'ENVIRONMENT'; function = 'environemt' }
    'FF4CE096' = @{ category = 'HEALTH_EDUCATION'; function = 'hq' }
    'FFF88AB4' = @{ category = 'CITY_PLANNING'; function = 'cityplanning' }
    'FF713106' = @{ category = 'FINANCES'; function = 'finances' }
    'FFE69CFC' = @{ category = 'TRANSPORTATION'; function = 'transportation' }
    'FF1D93C8' = @{ category = 'UTILITIES'; function = 'utilities' }
    'FFA5A138' = @{ category = 'SAFETY'; function = 'safety' }
}

$wantedFields = @('trigger', 'event', 'once', 'mood', 'title', 'message', 'priority', 'frequency', 'timeout')
$records = [System.Collections.Generic.List[object]]::new()

foreach ($resourceId in ($sourceFunctions.Keys | Sort-Object)) {
    $resource = Get-ChildItem -LiteralPath $sourceRoot -File -Filter "*_$resourceId.bin" | Select-Object -First 1
    if (-not $resource) { throw "Missing Lua resource ending in _$resourceId.bin under $sourceRoot" }

    $sourceFile = $resource.Name
    $config = $sourceFunctions[$resourceId]
    $lines = [IO.File]::ReadAllLines($resource.FullName, [Text.Encoding]::UTF8)
    $current = $null

    for ($index = 0; $index -lt $lines.Length; $index++) {
        $line = $lines[$index]
        $startPattern = '^\s*a\s*=\s*create_advice_' + [regex]::Escape($config.function) + '\s*\(\s*''([^'']+)'''
        $start = [regex]::Match($line, $startPattern)
        if ($start.Success) {
            if ($null -ne $current) { $records.Add($current) }
            $current = [ordered]@{
                category = $config.category
                adviceFunction = "create_advice_$($config.function)"
                recordId = $start.Groups[1].Value
                source = $sourceFile
                startLine = $index + 1
                fields = [ordered]@{}
                assignments = [System.Collections.Generic.List[object]]::new()
            }
            continue
        }

        if ($null -eq $current) { continue }
        $fieldMatch = [regex]::Match($line, '^\s*a\.(trigger|event|once|mood|title|message|priority|frequency|timeout)\s*=\s*(.*)$')
        if (-not $fieldMatch.Success) { continue }

        $fieldName = $fieldMatch.Groups[1].Value
        if ($fieldName -notin $wantedFields) { continue }
        $rhs = $fieldMatch.Groups[2].Value
        $fieldStartLine = $index + 1

        # Preserve multi-line Lua long strings exactly when the opening [[ and
        # closing ]] occur on separate physical source lines.
        $opens = ([regex]::Matches($rhs, '\[\[')).Count
        $closes = ([regex]::Matches($rhs, '\]\]')).Count
        $parts = [System.Collections.Generic.List[string]]::new()
        $parts.Add($rhs)
        while ($opens -gt $closes -and ($index + 1) -lt $lines.Length) {
            $index++
            $parts.Add($lines[$index])
            $opens += ([regex]::Matches($lines[$index], '\[\[')).Count
            $closes += ([regex]::Matches($lines[$index], '\]\]')).Count
        }
        $raw = $parts -join "`n"
        $textReferences = @([regex]::Matches($raw, '(?i)text@(?:0x)?([0-9a-f]{8})') | ForEach-Object { $_.Groups[1].Value.ToUpperInvariant() } | Select-Object -Unique)
        $assignment = [ordered]@{
            field = $fieldName
            line = $fieldStartLine
            raw = $raw
            textInstanceIds = $textReferences
        }
        $current.assignments.Add($assignment)
        # Keep a convenient latest-value view while preserving every source
        # assignment, including any overwritten fields, in `assignments`.
        $current.fields[$fieldName] = $assignment
    }
    if ($null -ne $current) { $records.Add($current) }
}

$writer = [IO.StreamWriter]::new($outputFullPath, $false, [Text.UTF8Encoding]::new($false))
try {
    foreach ($record in $records) {
        $writer.WriteLine(($record | ConvertTo-Json -Depth 8 -Compress))
    }
}
finally { $writer.Dispose() }

Write-Output "Wrote $($records.Count) explicit advisor records from seven Lua resources to $outputFullPath"
