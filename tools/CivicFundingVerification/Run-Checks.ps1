param([string]$WorkDirectory)
$ErrorActionPreference = 'Stop'
$civicRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $civicRoot 'scripts/Enter-Project.ps1')
$civicPaths = Get-Content (Join-Path $civicRoot 'config/local.paths.json') -Raw | ConvertFrom-Json
$civicManaged = Join-Path $civicPaths.CitiesSkylines 'Cities_Data/Managed'
$civicMono = Join-Path $civicPaths.CitiesSkylines 'Mono/bin/mono.exe'
if ([string]::IsNullOrEmpty($WorkDirectory)) { $WorkDirectory = Join-Path $civicRoot '.cache/civic-funding-verification' }
elseif (-not [IO.Path]::IsPathRooted($WorkDirectory)) { $WorkDirectory = Join-Path $civicRoot $WorkDirectory }
$civicWork = [IO.Path]::GetFullPath($WorkDirectory)
if (-not $civicWork.StartsWith($civicRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Civic verification output must stay inside the project.' }
foreach ($civicRequired in @($civicMono, (Join-Path $civicManaged 'Assembly-CSharp.dll'))) {
    if (-not (Test-Path -LiteralPath $civicRequired -PathType Leaf)) { throw "Required installed read-only metadata/runtime input unavailable: $civicRequired" }
}
$civicOutput = Join-Path $civicWork 'out'
$civicObj = (Join-Path $civicWork 'obj') + [IO.Path]::DirectorySeparatorChar
New-Item -ItemType Directory -Path $civicOutput, $civicObj -Force | Out-Null
& dotnet build (Join-Path $PSScriptRoot 'CivicFundingVerification.csproj') --configuration Release --output $civicOutput "-p:BaseIntermediateOutputPath=$civicObj" "-p:CitiesSkylinesPath=$($civicPaths.CitiesSkylines)" --nologo
if ($LASTEXITCODE -ne 0) { throw "Isolated civic compilation failed: $LASTEXITCODE" }
$civicSavedMonoPath = $env:MONO_PATH
try {
    $env:MONO_PATH = $civicManaged + ';' + (Join-Path $civicRoot 'tools/CitiesHarmony/provider/CitiesHarmony')
    & $civicMono (Join-Path $civicOutput 'CivicFundingVerification.exe')
    if ($LASTEXITCODE -ne 0) { throw "Civic checks failed: $LASTEXITCODE" }
} finally { $env:MONO_PATH = $civicSavedMonoPath }
Get-FileHash -LiteralPath (Join-Path $civicRoot 'src/SC4Evolution.Mod/Civic/CivicFundingBridge.cs'), (Join-Path $civicRoot 'src/SC4Evolution.Mod/Civic/CivicFundingPatches.cs') -Algorithm SHA256 | ForEach-Object { '{0}  {1}' -f $_.Hash, $_.Path }
Write-Output 'No Mod/Simulation project build, installation, provider initialization/query/patch, game launch or live AI/manager execution was performed.'
