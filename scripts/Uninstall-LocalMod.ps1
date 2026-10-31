$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Enter-Project.ps1')

if (@(Get-Process -Name 'Cities' -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'Close Cities: Skylines before removing its mod link. This script does not stop the game.'
}
$modBuild = [IO.Path]::GetFullPath((Join-Path $Sc4EvolutionRoot 'artifacts/mod'))
$modParent = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Colossal Order/Cities_Skylines/Addons/Mods'))
$modLink = [IO.Path]::GetFullPath((Join-Path $modParent 'SC4Evolution'))
if ([IO.Path]::GetDirectoryName($modLink) -ne $modParent -or [IO.Path]::GetFileName($modLink) -ne 'SC4Evolution') {
    throw 'The intended game-side link did not resolve to the expected named directory.'
}
if (-not (Test-Path -LiteralPath $modLink)) {
    Write-Output 'No SC4Evolution game-side junction is installed.'
    exit 0
}
$existing = Get-Item -LiteralPath $modLink -Force
$existingTargets = @($existing.Target)
if ($existing.LinkType -ne 'Junction' -or $existingTargets.Count -ne 1 -or [IO.Path]::GetFullPath([string]$existingTargets[0]) -ne $modBuild) {
    throw "This is not the project's own junction; nothing was removed: $modLink"
}

# DirectoryInfo.Delete() on this validated reparse-point removes the junction entry only.
# Never recurse into the build target or the game mod directory.
$existing.Delete()
Write-Output "Removed only the game-side junction: $modLink"
Write-Output 'Project files, builds, and game saves are preserved.'

