$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Enter-Project.ps1')

if (@(Get-Process -Name 'Cities' -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'Close Cities: Skylines before installing/updating its mod link. This script does not stop the game.'
}
$modBuild = [IO.Path]::GetFullPath((Join-Path $Sc4EvolutionRoot 'artifacts/mod'))
foreach ($required in @('SC4Evolution.dll', 'SC4Evolution.Core.dll', 'SC4Evolution.Simulation.dll', 'CitiesHarmony.API.dll', 'project-root.path')) {
    if (-not (Test-Path -LiteralPath (Join-Path $modBuild $required) -PathType Leaf)) {
        throw "Build is incomplete ($required missing). Run scripts/Build.ps1 first."
    }
}
$recordedRoot = [IO.Path]::GetFullPath((Get-Content -LiteralPath (Join-Path $modBuild 'project-root.path') -Raw).Trim())
if ($recordedRoot -ne $Sc4EvolutionRoot) { throw 'The build points to a different project directory. Rebuild here first.' }

$modParent = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Colossal Order/Cities_Skylines/Addons/Mods'))
$modLink = [IO.Path]::GetFullPath((Join-Path $modParent 'SC4Evolution'))
if ([IO.Path]::GetDirectoryName($modLink) -ne $modParent -or [IO.Path]::GetFileName($modLink) -ne 'SC4Evolution') {
    throw 'The intended game-side mod link did not resolve to the expected named directory.'
}

if (Test-Path -LiteralPath $modLink) {
    $existing = Get-Item -LiteralPath $modLink -Force
    $existingTargets = @($existing.Target)
    if ($existing.LinkType -ne 'Junction' -or $existingTargets.Count -ne 1 -or [IO.Path]::GetFullPath([string]$existingTargets[0]) -ne $modBuild) {
        throw "An existing path is not this project's junction and will not be overwritten: $modLink"
    }
    Write-Output "Project mod link already installed: $modLink -> $modBuild"
    exit 0
}

New-Item -ItemType Directory -Path $modParent -Force | Out-Null
New-Item -ItemType Junction -Path $modLink -Target $modBuild -ErrorAction Stop | Out-Null
Write-Output "Installed one game-side junction: $modLink -> $modBuild"
Write-Output 'No game files were replaced. Enable SC4Evolution in Content Manager > Mods to load the cabinet foundation.'

