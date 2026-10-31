param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$dictPath = Join-Path $ProjectRoot 'analysis\generated\reference-repos\OpenSC4\exemplar_types.dict'
$parserPath = Join-Path $ProjectRoot 'analysis\generated\reference-repos\OpenSC4\addons\dbpf\ExemplarSubfile.gd'
$schemaPath = Join-Path $ProjectRoot 'analysis\generated\sc4-sdk-reference\New_Properties.xml\new_properties.xml'
$outputDir = Join-Path $ProjectRoot 'analysis\generated\sc4-property-catalog'
$outputPath = Join-Path $outputDir 'catalog.json'

foreach ($path in @($dictPath, $parserPath, $schemaPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required read-only source is missing: $path"
    }
}

# exemplar_types.dict is a GDScript/Python-like integer-to-string map, not JSON.
$dictText = [IO.File]::ReadAllText($dictPath, [Text.Encoding]::UTF8)
$dictMatches = [regex]::Matches($dictText, '(?m)^\s*(\d+)\s*:\s*"((?:[^"\\]|\\.)*)"\s*,?\s*$')
$schema = [xml][IO.File]::ReadAllText($schemaPath, [Text.Encoding]::UTF8)

$schemaById = @{}
foreach ($property in $schema.SelectNodes('//PROPERTY')) {
    $rawId = [string]$property.GetAttribute('ID')
    if ($rawId -notmatch '^0[xX][0-9a-fA-F]+$') { continue }
    $numericId = [Convert]::ToUInt32($rawId.Substring(2), 16)
    $helpNode = $property.SelectSingleNode('HELP')
    $help = if ($null -eq $helpNode) { '' } else { ($helpNode.InnerText -replace '\s+', ' ').Trim() }
    $candidate = [ordered]@{
        id_raw = $rawId
        name = [string]$property.GetAttribute('Name')
        type = [string]$property.GetAttribute('Type')
        description = $help
    }
    if (-not $schemaById.ContainsKey($numericId)) { $schemaById[$numericId] = @() }
    $schemaById[$numericId] += ,$candidate
}

$categoryPatterns = [ordered]@{
    demand_caps = '(?i)demand|caps? demand|demand cap|initial demand'
    residential_wealth = '(?i)wealth|residential|R\$|R\$\$|R\$\$\$'
    jobs_workforce = '(?i)job|workforce|employment|worker|commute|trip|travel'
    taxes_budget = '(?i)tax|budget|funds?|loan|bond|interest|income|revenue'
    education_health = '(?i)education|school|health|hospital|EQ|HQ|life expectancy'
    services = '(?i)service|police|fire|crime|garbage|water|power|utility|coverage|funding|strike'
    pollution = '(?i)pollution|radiation|garbage|air|water pollution'
    transport_commute = '(?i)transport|transit|commute|travel|traffic|road|rail|train|bus|trip|freight'
    desirability = '(?i)desirab|land value|crime effect|pollution effect|school effect|hospital effect|traffic effect|park effect'
}

$relevantPattern = '(?i)demand|wealth|job|workforce|employment|tax|budget|funds?|loan|bond|income|education|school|health|hospital|EQ|HQ|pollution|radiation|commute|travel|desirab|service|crime|fire|police|garbage|water|power|traffic|transit|funding|coverage|land value|population|capacity'
$rows = [Collections.Generic.List[object]]::new()
foreach ($match in $dictMatches) {
    $decimalId = [uint32]::Parse($match.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
    $name = [regex]::Unescape($match.Groups[2].Value)
    if ($name -notmatch $relevantPattern) { continue }

    $schemaCandidates = @()
    if ($schemaById.ContainsKey($decimalId)) { $schemaCandidates = @($schemaById[$decimalId]) }
    $exactMatches = @($schemaCandidates | Where-Object {
        ([string]$_.name).Trim() -and ([string]$_.name).Trim() -ceq $name
    })
    $mappingStatus = if ($exactMatches.Count -gt 0) { 'dictionary_name_and_schema_name_match' } elseif ($schemaCandidates.Count -gt 0) { 'dictionary_name_only_schema_conflicts' } else { 'dictionary_name_only_no_schema_entry' }

    $categories = @(
        foreach ($category in $categoryPatterns.Keys) {
            if ($name -match $categoryPatterns[$category]) { $category }
        }
    )
    $row = [ordered]@{
        property_id_decimal = $decimalId
        property_id_hex = ('0x{0:X8}' -f $decimalId)
        exemplar_types_dict_name = $name
        categories = $categories
        mapping_status = $mappingStatus
        schema_exact_match = if ($exactMatches.Count -gt 0) { $exactMatches[0] } else { $null }
        schema_candidates_same_id = $schemaCandidates
        type_from_exemplar_parser = 'per-instance encoding in ExemplarSubfile.gd; this key dictionary does not declare a property type'
    }
    $rows.Add($row)
}

New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$catalog = [ordered]@{
    schema_version = 1
    generated_utc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    scope = 'Selective catalog of property IDs associated with demand caps, residential wealth, jobs/workforce, tax/budget, education/health, municipal services, pollution, transport/commute, and desirability.'
    sources = @(
        [ordered]@{ role = 'property_id_and_name_index'; path = 'analysis/generated/reference-repos/OpenSC4/exemplar_types.dict'; repository = 'https://github.com/OpenSC4-org/OpenSC4'; revision = 'fdc90c33e6e012b8c825f20ee10c51f55796df39' },
        [ordered]@{ role = 'observed_exemplar_binary_encoding'; path = 'analysis/generated/reference-repos/OpenSC4/addons/dbpf/ExemplarSubfile.gd'; repository = 'https://github.com/OpenSC4-org/OpenSC4'; revision = 'fdc90c33e6e012b8c825f20ee10c51f55796df39' },
        [ordered]@{ role = 'optional_name_type_help_cross_check'; path = 'analysis/generated/sc4-sdk-reference/New_Properties.xml/new_properties.xml'; note = 'Community property-editor schema; source README describes it as used by iLives Reader/PIM-X, and as vanilla/hybrid oriented. Same-ID candidates are kept separate; names are considered a match only when identical.' }
    )
    records = @($rows | Sort-Object property_id_decimal)
}
$catalog | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $outputPath -Encoding UTF8
Write-Output "Wrote $($rows.Count) records to $outputPath"

