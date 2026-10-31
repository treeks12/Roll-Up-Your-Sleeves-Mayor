param(
    [ValidateSet('Assembly-CSharp.dll', 'ICities.dll', 'ColossalManaged.dll', 'UnityEngine.dll', 'UnityEngine.UI.dll', 'UnityEngine.Networking.dll')]
    [string]$AssemblyName,
    [string[]]$Types = @()
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Enter-Project.ps1')

$pathsFile = Join-Path $Sc4EvolutionRoot 'config/local.paths.json'
if (-not (Test-Path -LiteralPath $pathsFile -PathType Leaf)) {
    throw "Local game paths are missing: $pathsFile"
}
try { $paths = Get-Content -LiteralPath $pathsFile -Raw | ConvertFrom-Json }
catch { throw "Could not parse config/local.paths.json: $($_.Exception.Message)" }
$citiesProperty = $paths.PSObject.Properties['CitiesSkylines']
if ($null -eq $citiesProperty -or [string]::IsNullOrWhiteSpace([string]$citiesProperty.Value)) {
    throw "config/local.paths.json must contain a non-empty 'CitiesSkylines' path."
}
$citiesRootValue = [string]$citiesProperty.Value
if (-not [IO.Path]::IsPathRooted($citiesRootValue)) { throw 'The configured CitiesSkylines path must be absolute.' }
$managedPath = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetFullPath($citiesRootValue)) 'Cities_Data/Managed'))
if (-not (Test-Path -LiteralPath $managedPath -PathType Container)) {
    throw "Cities: Skylines managed folder was not found: $managedPath"
}

$defaultTargets = [ordered]@{
    'Assembly-CSharp.dll' = @(
        'BuildConfig', 'ChirpPanel', 'DemandWrapper', 'District', 'EconomyManager',
        'EconomyPanel', 'GameAreaManager', 'InfoManager', 'SimulationManager', 'ZoneManager',
        'BuildingManager', 'CitizenManager', 'DistrictManager', 'ImmaterialResourceManager', 'LoadingManager', 'ToolManager'
    )
    'ICities.dll' = @(
        'ICities.ChirperExtensionBase', 'ICities.DemandExtensionBase', 'ICities.EconomyExtensionBase',
        'ICities.IEconomy', 'ICities.IUserMod', 'ICities.LoadingExtensionBase', 'ICities.LoadMode',
        'ICities.SerializableDataExtensionBase', 'ICities.ThreadingExtensionBase'
    )
}

$targets = [System.Collections.Generic.List[object]]::new()
if ($Types.Count -gt 0) {
    if ([string]::IsNullOrWhiteSpace($AssemblyName)) {
        throw 'Pass -AssemblyName when selecting -Types. Example: -AssemblyName ICities.dll -Types ICities.IUserMod'
    }
    foreach ($typeName in $Types) {
        if ([string]::IsNullOrWhiteSpace($typeName)) { throw 'Type names must not be empty.' }
        $targets.Add([pscustomobject]@{ AssemblyName = $AssemblyName; TypeName = $typeName })
    }
}
elseif (-not [string]::IsNullOrWhiteSpace($AssemblyName)) {
    if (-not $defaultTargets.Contains($AssemblyName)) {
        throw "No default selected-type list exists for $AssemblyName; pass one or more -Types explicitly."
    }
    foreach ($typeName in $defaultTargets[$AssemblyName]) {
        $targets.Add([pscustomobject]@{ AssemblyName = $AssemblyName; TypeName = $typeName })
    }
}
else {
    foreach ($assembly in $defaultTargets.Keys) {
        foreach ($typeName in $defaultTargets[$assembly]) {
            $targets.Add([pscustomobject]@{ AssemblyName = $assembly; TypeName = $typeName })
        }
    }
}

$safeNameCounts = @{}
foreach ($target in $targets) {
    $safeName = $target.TypeName -replace '[^A-Za-z0-9._-]', '_'
    if (-not $safeNameCounts.ContainsKey($safeName)) { $safeNameCounts[$safeName] = 0 }
    $safeNameCounts[$safeName]++
}
$usedOutputNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

$toolPath = Join-Path $Sc4EvolutionRoot 'tools/bin/ilspycmd.exe'
if (-not (Test-Path -LiteralPath $toolPath -PathType Leaf)) {
    throw "Local ilspycmd is missing: $toolPath. Run scripts/Bootstrap.ps1 first."
}
$outputDirectory = Join-Path $Sc4EvolutionRoot 'analysis/generated/cs1'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

foreach ($target in $targets) {
    $assemblyPath = [IO.Path]::GetFullPath((Join-Path $managedPath $target.AssemblyName))
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        throw "Managed game assembly was not found: $assemblyPath"
    }
    if ([IO.Path]::GetDirectoryName($assemblyPath) -ne $managedPath) {
        throw "Assembly path must resolve directly inside the configured Managed folder: $assemblyPath"
    }

    $safeAssembly = [IO.Path]::GetFileNameWithoutExtension($target.AssemblyName) -replace '[^A-Za-z0-9._-]', '_'
    $safeType = $target.TypeName -replace '[^A-Za-z0-9._-]', '_'
    if ([string]::IsNullOrWhiteSpace($safeType)) { throw "Type name cannot produce a safe output filename: $($target.TypeName)" }
    $outputName = if ($safeNameCounts[$safeType] -gt 1) { "{0}.{1}.cs" -f $safeAssembly, $safeType } else { "$safeType.cs" }
    if (-not $usedOutputNames.Add($outputName)) {
        throw "Selected types map to the same safe output filename '$outputName'; choose distinct type names or assemblies."
    }
    $outputPath = Join-Path $outputDirectory $outputName

    $ilspyArguments = @('--disable-updatecheck', '-r', $managedPath, '--type', $target.TypeName, $assemblyPath)
    $decompileOutput = @(& $toolPath @ilspyArguments 2>&1)
    $decompileExitCode = $LASTEXITCODE
    if ($decompileExitCode -ne 0) {
        throw "ilspycmd failed for $($target.AssemblyName) type '$($target.TypeName)' with exit code ${decompileExitCode}: $($decompileOutput -join ' ')"
    }
    $source = ($decompileOutput | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    if ([string]::IsNullOrWhiteSpace($source)) {
        throw "ilspycmd produced no source for $($target.AssemblyName) type '$($target.TypeName)'."
    }
    Set-Content -LiteralPath $outputPath -Value $source -Encoding utf8
    Write-Output "Wrote $outputPath"
}

Write-Output "Decompiled $($targets.Count) selected type(s); no full-assembly output was requested."
