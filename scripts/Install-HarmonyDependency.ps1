[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$CheckOnly,
    [string[]]$AdditionalWorkshopContentRoots = @()
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Enter-Project.ps1')

if (@(Get-Process -Name 'Cities' -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'Close Cities: Skylines before installing its Harmony provider link. This script does not stop the game.'
}

$dependencyRoot = [IO.Path]::GetFullPath((Join-Path $Sc4EvolutionRoot 'tools/CitiesHarmony'))
$providerRoot = [IO.Path]::GetFullPath((Join-Path $dependencyRoot 'provider/CitiesHarmony'))
if (-not $providerRoot.StartsWith($Sc4EvolutionRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The provider must remain inside this project.'
}
$manifest = Get-Content -LiteralPath (Join-Path $dependencyRoot 'manifest.json') -Raw | ConvertFrom-Json
if ($manifest.schema -ne 1 -or $manifest.providerRelease -ne '2.2.2-0' -or $manifest.apiPackageVersion -ne '2.2.0') {
    throw 'The dependency manifest is not the reviewed pinned release/API pair.'
}
if (-not (Test-Path -LiteralPath $providerRoot -PathType Container)) { throw 'Prepared provider folder is missing.' }
if ((Get-Item -LiteralPath $providerRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'The project-owned provider folder must be a real directory, not another link.'
}

$expectedFiles = @($manifest.providerFiles)
$actualFiles = @(Get-ChildItem -LiteralPath $providerRoot -Force)
if ($expectedFiles.Count -ne 6 -or $actualFiles.Count -ne $expectedFiles.Count) {
    throw 'Provider contents differ from the six files in the reviewed archive.'
}
foreach ($record in $expectedFiles) {
    $name = [string]$record.path
    if ([IO.Path]::GetFileName($name) -ne $name -or [string]::IsNullOrEmpty($name)) { throw 'Unsafe provider manifest path.' }
    $file = Join-Path $providerRoot $name
    $item = Get-Item -LiteralPath $file -Force
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Provider file is not a regular file: $name" }
    if ($item.Length -ne $record.size -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $record.sha256) {
        throw "Provider integrity check failed: $name"
    }
}
$apiFile = Join-Path $dependencyRoot 'api/2.2.0/lib/net35/CitiesHarmony.API.dll'
if ((Get-FileHash -LiteralPath $apiFile -Algorithm SHA256).Hash -ne $manifest.apiDllSha256) { throw 'API integrity check failed.' }

$modParent = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Colossal Order/Cities_Skylines/Addons/Mods'))
$modLink = [IO.Path]::GetFullPath((Join-Path $modParent 'CitiesHarmony'))
if ([IO.Path]::GetDirectoryName($modLink) -ne $modParent -or [IO.Path]::GetFileName($modLink) -ne 'CitiesHarmony') {
    throw 'The intended provider link did not resolve to the expected named directory.'
}
$existingLink = Get-Item -LiteralPath $modLink -Force -ErrorAction SilentlyContinue
if ($null -ne $existingLink) {
    $targets = @($existingLink.Target)
    if ($existingLink.LinkType -ne 'Junction' -or $targets.Count -ne 1 -or [IO.Path]::GetFullPath([string]$targets[0]) -ne $providerRoot) {
        throw "Existing path is not this project's provider junction; nothing will be overwritten: $modLink"
    }
}

# Check every known Steam library plus explicitly supplied additional content roots.
# A root here is steamapps/workshop/content/255710, not an individual item folder.
$workshopRoots = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$steamappsRoots = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$paths = Get-Content -LiteralPath (Join-Path $Sc4EvolutionRoot 'config/local.paths.json') -Raw | ConvertFrom-Json
$gameRoot = [IO.Path]::GetFullPath([string]$paths.CitiesSkylines)
$gameSteamapps = [IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($gameRoot))
[void]$steamappsRoots.Add($gameSteamapps)
$steamRegistry = Get-ItemProperty -LiteralPath 'HKCU:\Software\Valve\Steam' -Name SteamPath -ErrorAction SilentlyContinue
if ($null -ne $steamRegistry -and -not [string]::IsNullOrEmpty([string]$steamRegistry.SteamPath)) {
    [void]$steamappsRoots.Add([IO.Path]::GetFullPath((Join-Path $steamRegistry.SteamPath 'steamapps')))
}
foreach ($steamapps in @($steamappsRoots)) {
    $libraryFile = Join-Path $steamapps 'libraryfolders.vdf'
    if (Test-Path -LiteralPath $libraryFile -PathType Leaf) {
        $libraryText = Get-Content -LiteralPath $libraryFile -Raw
        foreach ($match in [regex]::Matches($libraryText, '"path"\s+"([^"\r\n]+)"')) {
            $libraryPath = $match.Groups[1].Value.Replace('\\', '\')
            [void]$steamappsRoots.Add([IO.Path]::GetFullPath((Join-Path $libraryPath 'steamapps')))
        }
    }
}
foreach ($steamapps in $steamappsRoots) { [void]$workshopRoots.Add((Join-Path $steamapps 'workshop/content/255710')) }
foreach ($extraRoot in $AdditionalWorkshopContentRoots) { [void]$workshopRoots.Add([IO.Path]::GetFullPath($extraRoot)) }

$duplicates = New-Object 'System.Collections.Generic.List[string]'
foreach ($contentRoot in $workshopRoots) {
    if (-not (Test-Path -LiteralPath $contentRoot -PathType Container)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $contentRoot -Filter '*.dll' -File -Recurse -ErrorAction Stop) {
        if ($file.Name -eq 'CitiesHarmony.dll' -or $file.Name -eq 'CitiesHarmony.Harmony.dll') { $duplicates.Add($file.FullName) }
    }
}
if (Test-Path -LiteralPath $modParent -PathType Container) {
    foreach ($directory in Get-ChildItem -LiteralPath $modParent -Directory -Force) {
        if ($directory.FullName -eq $modLink) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $directory.FullName -Filter '*.dll' -File -Recurse -ErrorAction Stop) {
            if ($file.Name -eq 'CitiesHarmony.dll' -or $file.Name -eq 'CitiesHarmony.Harmony.dll') { $duplicates.Add($file.FullName) }
        }
    }
}
if ($duplicates.Count -gt 0) {
    throw ("An existing Workshop/local Harmony provider was detected. No duplicate provider will be installed. Inspect/reuse that provider or resolve it manually first. No subscription was changed:`n" + ($duplicates -join "`n"))
}

if ($null -ne $existingLink) {
    Write-Output "Provider junction already installed and validated: $modLink -> $providerRoot"
    return
}
if ($CheckOnly) {
    Write-Output "Validated provider 2.2.2-0/API 2.2.0; no existing provider found. Ready: $modLink -> $providerRoot"
    Write-Output 'CheckOnly: no game-side directory or junction was created.'
    return
}
if ($PSCmdlet.ShouldProcess($modLink, "Create provider junction to $providerRoot")) {
    New-Item -ItemType Directory -Path $modParent -Force | Out-Null
    New-Item -ItemType Junction -Path $modLink -Target $providerRoot -ErrorAction Stop | Out-Null
    $created = Get-Item -LiteralPath $modLink -Force
    $createdTargets = @($created.Target)
    if ($created.LinkType -ne 'Junction' -or $createdTargets.Count -ne 1 -or [IO.Path]::GetFullPath([string]$createdTargets[0]) -ne $providerRoot) {
        throw 'Junction creation did not produce the expected link; inspect the reported path before retrying.'
    }
    Write-Output "Installed provider junction: $modLink -> $providerRoot"
    Write-Output 'All actual provider files remain under the project. No game files or subscriptions were changed.'
    Write-Output 'Enable Harmony in Content Manager > Mods. SC4Evolution is a separate project-owned mod junction.'
}
