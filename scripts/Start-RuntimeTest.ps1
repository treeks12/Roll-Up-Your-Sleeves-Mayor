[CmdletBinding()]
param(
    [ValidateSet('InventoryMaps', 'NewTestCity', 'SaveReloadTestCity', 'ResidentialClassTestCity', 'OrganicResidentialTestCity', 'OrganicResidentialSaveReloadTestCity', 'CivicFundingTestCity')]
    [string]$Mode = 'NewTestCity',
    [ValidateRange(15, 600)]
    [int]$TimeoutSeconds = 300,
    [switch]$NoWorkshop,
    [switch]$OrganicInventoryOnly
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Enter-Project.ps1')

if (Get-Process -Name 'Cities' -ErrorAction SilentlyContinue) {
    throw 'CS1 is already running. The test must start from a new process.'
}
if ($Mode -eq 'InventoryMaps' -and $TimeoutSeconds -gt 120) {
    throw 'Map inventory permits at most 120 seconds.'
}
if ($OrganicInventoryOnly -and $Mode -ne 'OrganicResidentialTestCity') {
    throw 'OrganicInventoryOnly is exclusive to OrganicResidentialTestCity.'
}
if ($Mode -eq 'CivicFundingTestCity' -and -not $NoWorkshop) {
    throw 'CivicFundingTestCity requires an exclusive new -NoWorkshop process and CivicFunding=SC4LocalV1 configured separately.'
}

$configDirectory = Join-Path $Sc4EvolutionRoot 'config'
if (-not (Test-Path -LiteralPath $configDirectory -PathType Container) -or
    ((Get-Item -LiteralPath $configDirectory).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'The project config directory must exist and must not be a directory link.'
}
$requestPath = Join-Path $configDirectory 'runtime-test.request'
if (Test-Path -LiteralPath $requestPath) {
    throw 'A runtime test request already exists. Inspect it before starting another test.'
}

$lines = @('schema=1', ('mode=' + $Mode))
if ($Mode -ne 'InventoryMaps') {
    # Verified in the native inventory of this installed Steam build. The driver
    # independently checks exact built-in location, DLC access and checksum again.
    $lines += 'assetName=NorthEurope1FINAL.Green Plains_Data'
    $lines += 'assetChecksum=d09cc7008c7f95c0e55b1ebed0a80579'
}
$lines += ('timeoutSeconds=' + $TimeoutSeconds)
$lines += 'controlledQuit=true'
$lines += ('captureAllTabs=' + $(if ($Mode -eq 'InventoryMaps' -or $Mode -eq 'ResidentialClassTestCity' -or $Mode -eq 'OrganicResidentialTestCity' -or $Mode -eq 'OrganicResidentialSaveReloadTestCity' -or $Mode -eq 'CivicFundingTestCity') { 'false' } else { 'true' }))
if ($Mode -eq 'OrganicResidentialTestCity' -or $Mode -eq 'OrganicResidentialSaveReloadTestCity') {
    $lines += ('organicInventoryOnly=' + $OrganicInventoryOnly.IsPresent.ToString().ToLowerInvariant())
}
$requestBytes = [Text.Encoding]::ASCII.GetBytes(($lines -join "`n") + "`n")
$stream = [IO.File]::Open($requestPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $stream.Write($requestBytes, 0, $requestBytes.Length) }
finally { $stream.Dispose() }

# A launch failure deliberately leaves the one-shot request for inspection. This
# script never retries, attaches to another process, or terminates a city.
& (Join-Path $PSScriptRoot 'Launch-TestGame.ps1') -NoWorkshop:$NoWorkshop
Write-Output ('Requested engine-native test: ' + $Mode)
Write-Output 'Evidence is written under logs/runtime-tests/. Completion must be checked in the report and screenshots.'
