param([string]$SimulationAssemblyPath)
$ErrorActionPreference = 'Stop'
$observerRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $observerRoot 'scripts/Enter-Project.ps1')
$observerPaths = Get-Content (Join-Path $observerRoot 'config/local.paths.json') -Raw | ConvertFrom-Json
$observerManaged = Join-Path $observerPaths.CitiesSkylines 'Cities_Data/Managed'
$observerMono = Join-Path $observerPaths.CitiesSkylines 'Mono/bin/mono.exe'
if ([string]::IsNullOrEmpty($SimulationAssemblyPath)) {
    $SimulationAssemblyPath = Join-Path $observerRoot 'src/SC4Evolution.Simulation/bin/Release/net35/SC4Evolution.Simulation.dll'
}
$SimulationAssemblyPath = [IO.Path]::GetFullPath($SimulationAssemblyPath)
foreach ($observerRequired in @($SimulationAssemblyPath, $observerMono, (Join-Path $observerManaged 'Assembly-CSharp.dll'))) {
    if (-not (Test-Path -LiteralPath $observerRequired -PathType Leaf)) { throw "Required existing input unavailable: $observerRequired. This script does not build/install the Mod or Simulation, launch the game, or modify game files." }
}
$observerWork = Join-Path $observerRoot '.cache/capacity-observer-verification/repo'
$observerInput = Join-Path $observerWork 'input'
$observerOutput = Join-Path $observerWork 'out'
$observerObj = (Join-Path $observerWork 'obj') + [IO.Path]::DirectorySeparatorChar
New-Item -ItemType Directory -Path $observerInput, $observerOutput, $observerObj -Force | Out-Null
# Snapshot an already-built project-owned dependency. No project reference can
# trigger a Simulation/Mod build or share its obj directory during these checks.
$observerSimulation = Join-Path $observerInput 'SC4Evolution.Simulation.dll'
Copy-Item -LiteralPath $SimulationAssemblyPath -Destination $observerSimulation -Force
$observerArguments = @('build', (Join-Path $PSScriptRoot 'CapacityObserver.csproj'), '--configuration', 'Release', '--output', $observerOutput,
    "-p:BaseIntermediateOutputPath=$observerObj", "-p:CitiesSkylinesPath=$($observerPaths.CitiesSkylines)", "-p:SimulationAssemblyPath=$observerSimulation", '--nologo')
& dotnet @observerArguments
if ($LASTEXITCODE -ne 0) { throw "Isolated capacity observer compilation failed: $LASTEXITCODE" }
$observerExecutable = Join-Path $observerOutput 'CapacityObserver.exe'
& (Join-Path $PSScriptRoot 'Check-Observer.ps1') -AssemblyPath $observerExecutable
$observerSavedMonoPath = $env:MONO_PATH
try {
    $env:MONO_PATH = $observerManaged
    & $observerMono $observerExecutable
    if ($LASTEXITCODE -ne 0) { throw "Isolated capacity observer checks failed: $LASTEXITCODE" }
} finally { $env:MONO_PATH = $observerSavedMonoPath }
Write-Output ('Observer source SHA256: ' + (Get-FileHash -LiteralPath (Join-Path $observerRoot 'src/SC4Evolution.Mod/Economy/ResidentialCapacityObserver.cs') -Algorithm SHA256).Hash)
Write-Output ('Existing Simulation input SHA256: ' + (Get-FileHash -LiteralPath $observerSimulation -Algorithm SHA256).Hash)
Write-Output 'PASS isolated observer verification only. No Mod/Simulation project build, installation, game launch, live manager access or Observe Known city proof.'
