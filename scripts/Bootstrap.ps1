param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Enter-Project.ps1')

$pathsFile = Join-Path $Sc4EvolutionRoot 'config/local.paths.json'
if (-not (Test-Path -LiteralPath $pathsFile -PathType Leaf)) {
    throw "Local game paths are missing: $pathsFile. Copy config/paths.example.json to config/local.paths.json and set both install paths."
}

try {
    $paths = Get-Content -LiteralPath $pathsFile -Raw | ConvertFrom-Json
}
catch {
    throw "Could not parse config/local.paths.json: $($_.Exception.Message)"
}

function Get-ConfiguredGamePath {
    param([Parameter(Mandatory = $true)][string]$Name)

    $property = $paths.PSObject.Properties[$Name]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        throw "config/local.paths.json must contain a non-empty '$Name' path."
    }

    $value = [string]$property.Value
    if (-not [IO.Path]::IsPathRooted($value)) {
        throw "The configured '$Name' path must be absolute: $value"
    }

    try { return [IO.Path]::GetFullPath($value) }
    catch { throw "The configured '$Name' path is invalid: $value" }
}

$citiesRoot = Get-ConfiguredGamePath -Name 'CitiesSkylines'
$simCityRoot = Get-ConfiguredGamePath -Name 'SimCity4'
foreach ($entry in @(@{ Name = 'Cities: Skylines'; Path = $citiesRoot }, @{ Name = 'SimCity 4'; Path = $simCityRoot })) {
    if (-not (Test-Path -LiteralPath $entry.Path -PathType Container)) {
        throw "$($entry.Name) install directory does not exist: $($entry.Path)"
    }
}

$requiredGameFiles = @(
    (Join-Path $citiesRoot 'Cities.exe'),
    (Join-Path $citiesRoot 'Cities_Data/Managed/ICities.dll'),
    (Join-Path $citiesRoot 'Cities_Data/Managed/Assembly-CSharp.dll'),
    (Join-Path $citiesRoot 'Cities_Data/Managed/ColossalManaged.dll'),
    (Join-Path $citiesRoot 'Cities_Data/Managed/UnityEngine.dll'),
    (Join-Path $simCityRoot 'Apps/SimCity 4.exe')
)
foreach ($file in $requiredGameFiles) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "A required installed game reference was not found: $file"
    }
}

$managedPath = Join-Path $citiesRoot 'Cities_Data/Managed'
if ($managedPath.Contains(';')) {
    throw "The Cities: Skylines managed path contains a semicolon, which MSBuild uses to separate properties: $managedPath"
}

& (Join-Path $PSScriptRoot 'Prepare-Harmony.ps1')

$toolVersion = '9.1.0.7988'
$toolDirectory = Join-Path $Sc4EvolutionRoot 'tools/bin'
$toolPath = Join-Path $toolDirectory 'ilspycmd.exe'
New-Item -ItemType Directory -Path $toolDirectory -Force | Out-Null

function Get-InstalledIlspyVersion {
    param([Parameter(Mandatory = $true)][string]$Executable)

    $versionOutput = @(& $Executable '--version' 2>&1)
    $versionExitCode = $LASTEXITCODE
    if ($versionExitCode -ne 0) {
        throw "Could not read ilspycmd version (exit $versionExitCode): $($versionOutput -join ' ')"
    }

    foreach ($line in $versionOutput) {
        if ([string]$line -match 'ilspycmd:\s*([0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?)') {
            return $Matches[1]
        }
    }
    throw "ilspycmd did not report its version: $($versionOutput -join ' ')"
}

if (Test-Path -LiteralPath $toolPath -PathType Leaf) {
    $installedVersion = Get-InstalledIlspyVersion -Executable $toolPath
    if ($installedVersion -ne $toolVersion) {
        throw "ilspycmd $installedVersion is present in tools/bin; expected pinned version $toolVersion. Bootstrap leaves existing tools untouched."
    }
}
else {
    $installArguments = @('tool', 'install', 'ilspycmd', '--tool-path', $toolDirectory, '--version', $toolVersion)
    Push-Location $Sc4EvolutionRoot
    try {
        & dotnet @installArguments
        $installExitCode = $LASTEXITCODE
    }
    finally { Pop-Location }
    if ($installExitCode -ne 0) {
        throw "dotnet tool install failed with exit code $installExitCode."
    }
    if (-not (Test-Path -LiteralPath $toolPath -PathType Leaf)) {
        throw "Pinned ilspycmd installation completed without creating $toolPath"
    }
    $installedVersion = Get-InstalledIlspyVersion -Executable $toolPath
    if ($installedVersion -ne $toolVersion) {
        throw "Installed ilspycmd $installedVersion; expected pinned version $toolVersion."
    }
}

$solutionPath = Join-Path $Sc4EvolutionRoot 'SC4Evolution.slnx'
$nugetConfig = Join-Path $Sc4EvolutionRoot 'NuGet.Config'
if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
    throw "Project solution was not found: $solutionPath"
}
$restoreArguments = @(
    'restore', $solutionPath,
    '--configfile', $nugetConfig,
    ('--property:GameManagedPath=' + $managedPath)
)
Push-Location $Sc4EvolutionRoot
try {
    & dotnet @restoreArguments
    $restoreExitCode = $LASTEXITCODE
}
finally { Pop-Location }
if ($restoreExitCode -ne 0) {
    throw "dotnet restore failed with exit code $restoreExitCode."
}

Write-Output "Validated Cities: Skylines references under $managedPath"
Write-Output "Validated SimCity 4 executable under $simCityRoot"
Write-Output "ilspycmd $toolVersion is available in tools/bin"
Write-Output 'Project dependencies restored into the project-local cache.'
