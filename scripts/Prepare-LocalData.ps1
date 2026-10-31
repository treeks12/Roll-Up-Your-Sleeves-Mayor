param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Enter-Project.ps1')

function Invoke-LocalExporter {
    param([string]$Project, [string[]]$ExporterArguments)
    Push-Location $Sc4EvolutionRoot
    try {
        & dotnet run --project (Join-Path $Sc4EvolutionRoot $Project) --configuration Release -- @ExporterArguments
        if ($LASTEXITCODE -ne 0) { throw "Local data exporter failed: $Project" }
    }
    finally { Pop-Location }
}

$demandProfile = Join-Path $Sc4EvolutionRoot 'analysis/generated/sc4-demand-profile/demand.sc4e'
if (-not (Test-Path -LiteralPath $demandProfile -PathType Leaf)) {
    Invoke-LocalExporter 'tools/SC4DemandProfile/SC4DemandProfile.csproj' @('--root', $Sc4EvolutionRoot)
}
else { Write-Output 'Reusing the local demand profile; runtime validates its bounded format and checksum.' }

$taxProfile = Join-Path $Sc4EvolutionRoot 'analysis/generated/sc4-tax-profile/tax-demand.s4td'
if (-not (Test-Path -LiteralPath $taxProfile -PathType Leaf)) {
    Invoke-LocalExporter 'tools/SC4TaxProfile/SC4TaxProfile.csproj' @('--root', $Sc4EvolutionRoot)
}
else { Write-Output 'Reusing the local tax tuning profile.' }

$waterProfile = Join-Path $Sc4EvolutionRoot 'analysis/generated/sc4-residential-profile/water-prerequisites.s4ew'
if (-not (Test-Path -LiteralPath $waterProfile -PathType Leaf)) {
    Invoke-LocalExporter 'tools/SC4ResidentialProfile/SC4ResidentialProfile.csproj' @('--root', $Sc4EvolutionRoot, '--water-only')
}
else { Write-Output 'Reusing the source-attributed residential water prerequisite profile.' }

$portraitsRoot = Join-Path $Sc4EvolutionRoot 'analysis/generated/sc4-advisor-assets'
$complete = $true
foreach ($advisor in @('Finance', 'UrbanPlanning', 'EducationHealth', 'Utilities', 'PublicSafety', 'Environment', 'Transport')) {
    foreach ($mood in @('neutral', 'happy', 'attention', 'alarmed')) {
        if (-not (Test-Path -LiteralPath (Join-Path $portraitsRoot ($advisor + '-' + $mood + '.png')) -PathType Leaf)) {
            $complete = $false
        }
    }
}
if ($complete) {
    $manifestPath = Join-Path $portraitsRoot 'manifest.csv'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { $complete = $false }
    else {
        foreach ($record in (Get-Content -LiteralPath $manifestPath | Select-Object -Skip 1)) {
            $columns = $record -split ','
            if ($columns.Count -lt 5 -or $columns[4] -notmatch 'frames=(\d+)') { $complete = $false; break }
            $frames = [int]$Matches[1]
            for ($frame = 0; $frame -lt $frames; $frame++) {
                $frameName = $columns[0] + '-' + $columns[1] + '-f' + $frame.ToString('D3') + '.png'
                if (-not (Test-Path -LiteralPath (Join-Path $portraitsRoot $frameName) -PathType Leaf)) { $complete = $false; break }
            }
            if (-not $complete) { break }
        }
    }
}
if (-not $complete) {
    Invoke-LocalExporter 'tools/SC4AdvisorAssets/SC4AdvisorAssets.csproj' @($Sc4EvolutionRoot)
}
else { Write-Output 'All 28 local advisor portraits are present.' }

Write-Output 'Local SC4 data stays under analysis/generated; do not include original assets in a release package.'
