param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Enter-Project.ps1')

$cities = @(Get-Process -Name 'Cities' -ErrorAction SilentlyContinue)
if ($cities.Count -ne 1) { throw 'Open one CS1 city with SC4Evolution enabled before checking runtime evidence.' }
$statusPath = Join-Path $Sc4EvolutionRoot 'logs/runtime-status.txt'
if (-not (Test-Path -LiteralPath $statusPath -PathType Leaf)) {
    throw 'No city sample has been written yet. The game may still be loading, the mod may be disabled, or initialization may have failed.'
}
$lines = Get-Content -LiteralPath $statusPath
$values = @{}
foreach ($line in $lines) {
    $separator = $line.IndexOf('=')
    if ($separator -gt 0) { $values[$line.Substring(0, $separator)] = $line.Substring($separator + 1) }
}
foreach ($required in @('RecordedUtc', 'CS1', 'ReportSequence', 'Population', 'DemandNodes', 'DemandConverged', 'EconomicGameplayEnabled')) {
    if (-not $values.ContainsKey($required)) { throw "Runtime evidence is missing $required. Inspect logs/SC4Evolution.log." }
}
$recorded = [DateTime]::Parse($values.RecordedUtc, [Globalization.CultureInfo]::InvariantCulture).ToUniversalTime()
if ($recorded -lt $cities[0].StartTime.ToUniversalTime()) { throw 'The sample belongs to an earlier game process.' }
if (([DateTime]::UtcNow - $recorded).TotalMinutes -gt 5) { throw 'The last runtime sample is over five minutes old.' }
if ($values.CS1 -ne '1.21.1-f9') { throw "Unexpected game version: $($values.CS1)" }
if ([long]$values.ReportSequence -lt 1 -or [long]$values.Population -lt 0) { throw 'Invalid cabinet sample counters.' }
if ([int]$values.DemandNodes -ne 173 -or $values.DemandConverged -ne 'True') { throw 'The installed-data demand graph is unavailable or has not converged.' }

Write-Output ('Observed city: ' + $values.City)
Write-Output ('Fresh simulation sample: ' + $recorded.ToString('o'))
Write-Output ('Population: ' + $values.Population + '; demand nodes: ' + $values.DemandNodes)
Write-Output ('Economic gameplay enabled: ' + $values.EconomicGameplayEnabled)
Write-Output 'This proves a fresh runtime sample and converged graph, not visual layout, save/reload behavior or complete economic conversion.'
