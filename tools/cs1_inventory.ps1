$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$pathsFile = Join-Path $projectRoot 'config/local.paths.json'
$paths = Get-Content -LiteralPath $pathsFile -Raw | ConvertFrom-Json
$gameRoot = [string]$paths.CitiesSkylines
$steamAppsPath = Split-Path -Parent (Split-Path -Parent $gameRoot)
$manifestPath = Join-Path $steamAppsPath 'appmanifest_255710.acf'
$managedPath = Join-Path $gameRoot 'Cities_Data/Managed'
$localAddons = Join-Path $env:LOCALAPPDATA 'Colossal Order/Cities_Skylines/Addons'

function Get-AcfValue([string]$Text, [string]$Key) {
    $match = [regex]::Match($Text, '(?m)^\s*"' + [regex]::Escape($Key) + '"\s+"([^"]*)"\s*$')
    if ($match.Success) { return $match.Groups[1].Value }
    return $null
}

$manifest = $null
if (Test-Path -LiteralPath $manifestPath) {
    $acfText = Get-Content -LiteralPath $manifestPath -Raw
    $manifest = [ordered]@{
        present = $true
        appId = Get-AcfValue $acfText 'appid'
        name = Get-AcfValue $acfText 'name'
        stateFlags = Get-AcfValue $acfText 'StateFlags'
        installDir = Get-AcfValue $acfText 'installdir'
        buildId = Get-AcfValue $acfText 'buildid'
        lastUpdatedUnix = Get-AcfValue $acfText 'LastUpdated'
        sizeOnDiskBytes = Get-AcfValue $acfText 'SizeOnDisk'
    }
} else {
    $manifest = [ordered]@{ present = $false }
}

$exePath = Join-Path $gameRoot 'Cities.exe'
$exeInfo = $null
if (Test-Path -LiteralPath $exePath) {
    $file = Get-Item -LiteralPath $exePath
    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
    $exeInfo = [ordered]@{
        file = $file.Name
        sizeBytes = $file.Length
        fileVersion = $version.FileVersion
        productVersion = $version.ProductVersion
    }
}

$keyDlls = @('Assembly-CSharp.dll','Assembly-CSharp-firstpass.dll','ColossalManaged.dll','ICities.dll','UnityEngine.dll','UnityEngine.UI.dll','UnityEngine.Networking.dll')
$managed = @()
foreach ($name in $keyDlls) {
    $dll = Join-Path $managedPath $name
    if (Test-Path -LiteralPath $dll) {
        $file = Get-Item -LiteralPath $dll
        $ver = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll)
        $assemblyName = $null
        $assemblyError = $null
        try { $assemblyName = [System.Reflection.AssemblyName]::GetAssemblyName($dll).FullName }
        catch { $assemblyError = $_.Exception.GetType().Name }
        $managed += [ordered]@{
            name = $file.Name
            sizeBytes = $file.Length
            fileVersion = $ver.FileVersion
            productVersion = $ver.ProductVersion
            assemblyIdentity = $assemblyName
            assemblyMetadataError = $assemblyError
            sha256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
}

$modsPath = Join-Path $localAddons 'Mods'
$mods = @()
if (Test-Path -LiteralPath $modsPath) {
    $mods = @(Get-ChildItem -LiteralPath $modsPath -Directory | Sort-Object Name | ForEach-Object { $_.Name })
}
$processes = @(Get-Process -Name 'Cities' -ErrorAction SilentlyContinue | ForEach-Object {
    [ordered]@{ processName = $_.ProcessName; pid = $_.Id }
})

$inventory = [ordered]@{
    collectedAtLocal = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz')
    gameRoot = $gameRoot
    steam = $manifest
    executable = $exeInfo
    managedDlls = $managed
    localMods = [ordered]@{ root = $modsPath; folderNames = $mods }
    gameProcess = [ordered]@{ running = ($processes.Count -gt 0); processes = $processes }
    method = 'Read-only file metadata, assembly identity query, and SHA-256 hashes. No game DLL was loaded for execution.'
}

$jsonPath = Join-Path $projectRoot 'analysis/generated/cs1-inventory.json'
$reportPath = Join-Path $projectRoot 'analysis/cs1-inventory.md'
New-Item -ItemType Directory -Path (Split-Path -Parent $jsonPath) -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $reportPath) -Force | Out-Null
$inventory | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding utf8

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# CS1 installed-file inventory')
$lines.Add('')
$lines.Add("Collected: $($inventory.collectedAtLocal)")
$lines.Add('')
$lines.Add('## Steam install')
$lines.Add('')
if ($manifest.present) {
    $lines.Add("- App: $($manifest.name) (AppID $($manifest.appId)); StateFlags $($manifest.stateFlags)")
    $lines.Add("- Build ID: $($manifest.buildId); last updated (Unix): $($manifest.lastUpdatedUnix)")
    $lines.Add("- Manifest size on disk: $($manifest.sizeOnDiskBytes) bytes; install directory: $($manifest.installDir)")
} else { $lines.Add('- Steam app manifest not found.') }
if ($exeInfo) { $lines.Add("- Cities.exe: $($exeInfo.fileVersion) (product $($exeInfo.productVersion)), $($exeInfo.sizeBytes) bytes") }
$lines.Add('')
$lines.Add('## Managed DLLs')
$lines.Add('')
$lines.Add('| File | Size (bytes) | File version | Assembly identity | SHA-256 |')
$lines.Add('| --- | ---: | --- | --- | --- |')
foreach ($dll in $managed) {
    $identity = if ($dll.assemblyIdentity) { $dll.assemblyIdentity } else { 'unavailable' }
    $lines.Add("| $($dll.name) | $($dll.sizeBytes) | $($dll.fileVersion) | $identity | ``$($dll.sha256)`` |")
}
$lines.Add('')
$lines.Add('## Local mods and process')
$lines.Add('')
$lines.Add("- Local Mods directory: ``$modsPath``")
if ($mods.Count) { $lines.Add("- Mod folder names: $($mods -join ', ')") } else { $lines.Add('- Mod folder names: none found') }
$lines.Add("- Cities process running: $($processes.Count -gt 0)")
$lines.Add('')
$lines.Add('Inventory uses file metadata and SHA-256; assembly identity queries read assembly metadata only. DLL code was not executed. Steam owner/account identifiers are omitted.')
$lines | Set-Content -LiteralPath $reportPath -Encoding utf8

Write-Output "JSON: $jsonPath"
Write-Output "Report: $reportPath"
Write-Output "Build ID: $($manifest.buildId)"
Write-Output "Managed DLLs: $($managed.Count)"
Write-Output "Local mod folders: $($mods.Count)"
Write-Output "Game process running: $($processes.Count -gt 0)"
