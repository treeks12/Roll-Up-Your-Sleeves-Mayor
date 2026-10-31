param(
    [ValidateSet('Installed', 'Preview')]
    [string]$Destination = 'Installed'
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
if (-not [IO.Path]::IsPathRooted($citiesRootValue)) {
    throw "The configured CitiesSkylines path must be absolute: $citiesRootValue"
}
$citiesRoot = [IO.Path]::GetFullPath($citiesRootValue)
if (-not (Test-Path -LiteralPath $citiesRoot -PathType Container)) {
    throw "Cities: Skylines install directory does not exist: $citiesRoot"
}

$managedPath = [IO.Path]::GetFullPath((Join-Path $citiesRoot 'Cities_Data/Managed'))
if ($managedPath.Contains(';')) {
    throw "The Cities: Skylines managed path contains a semicolon, which MSBuild uses to separate properties: $managedPath"
}
foreach ($referenceName in @('ICities.dll', 'Assembly-CSharp.dll', 'ColossalManaged.dll', 'UnityEngine.dll')) {
    $referencePath = Join-Path $managedPath $referenceName
    if (-not (Test-Path -LiteralPath $referencePath -PathType Leaf)) {
        throw "A required Cities: Skylines managed reference was not found: $referencePath"
    }
}

$runningCities = @(Get-Process -Name 'Cities' -ErrorAction SilentlyContinue)
if ($runningCities.Count -gt 0 -and $Destination -eq 'Installed') {
    $processIds = ($runningCities | ForEach-Object { $_.Id }) -join ', '
    throw "Cities: Skylines is running (process ID(s): $processIds). Close the game before building; this script will not stop it."
}

$projectPath = Join-Path $Sc4EvolutionRoot 'src/SC4Evolution.Mod/SC4Evolution.Mod.csproj'
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Mod project was not found: $projectPath"
}
$outputDirectory = Join-Path $Sc4EvolutionRoot $(if ($Destination -eq 'Preview') { 'artifacts/preview' } else { 'artifacts/mod' })
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$commercialReferences = @('ICities.dll', 'Assembly-CSharp.dll', 'ColossalManaged.dll', 'UnityEngine.dll')
$allowedAssemblies = @('SC4Evolution.dll', 'SC4Evolution.Core.dll', 'SC4Evolution.Simulation.dll', 'CitiesHarmony.API.dll')
foreach ($assembly in @(Get-ChildItem -LiteralPath $outputDirectory -Filter '*.dll' -File)) {
    if ($assembly.Name -notin $allowedAssemblies) {
        throw "Unexpected assembly already exists in the mod output: $($assembly.FullName). Keep only project assemblies and CitiesHarmony.API."
    }
}
foreach ($name in $commercialReferences) {
    if (Test-Path -LiteralPath (Join-Path $outputDirectory $name) -PathType Leaf) {
        throw "A game reference already exists in the mod output directory: $(Join-Path $outputDirectory $name). Remove the stale generated copy before building."
    }
}

$buildArguments = @(
    'build', $projectPath,
    '--configuration', 'Release',
    '--framework', 'net35',
    '--output', $outputDirectory,
    ('--property:GameManagedPath=' + $managedPath)
)
Push-Location $Sc4EvolutionRoot
try {
    & dotnet @buildArguments
    $buildExitCode = $LASTEXITCODE
}
finally { Pop-Location }
if ($buildExitCode -ne 0) {
    throw "Mod build failed with exit code $buildExitCode."
}

foreach ($name in $commercialReferences) {
    if (Test-Path -LiteralPath (Join-Path $outputDirectory $name) -PathType Leaf) {
        throw "Build output contains a commercial game reference: $(Join-Path $outputDirectory $name)"
    }
}
foreach ($assembly in @(Get-ChildItem -LiteralPath $outputDirectory -Filter '*.dll' -File)) {
    if ($assembly.Name -notin $allowedAssemblies) {
        throw "Build output contains an unexpected runtime dependency: $($assembly.FullName)"
    }
}

$rootMarker = Join-Path $outputDirectory 'project-root.path'
Set-Content -LiteralPath $rootMarker -Value $Sc4EvolutionRoot -NoNewline -Encoding utf8
Write-Output "Build output: $outputDirectory"
Write-Output "Runtime project-root marker: $rootMarker"
