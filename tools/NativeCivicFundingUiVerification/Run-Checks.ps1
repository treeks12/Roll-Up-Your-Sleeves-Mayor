$ErrorActionPreference = 'Stop'
$civicUiRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $civicUiRoot 'scripts/Enter-Project.ps1')
$civicUiPaths = Get-Content (Join-Path $civicUiRoot 'config/local.paths.json') -Raw | ConvertFrom-Json
$civicUiManaged = [IO.Path]::GetFullPath((Join-Path $civicUiPaths.CitiesSkylines 'Cities_Data/Managed'))
foreach ($civicUiInput in @('Assembly-CSharp.dll', 'ColossalManaged.dll', 'UnityEngine.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $civicUiManaged $civicUiInput) -PathType Leaf)) {
        throw "Missing read-only CS1 API reference: $civicUiInput"
    }
}
$civicUiWork = Join-Path $civicUiRoot '.cache/native-civic-funding-ui-verification'
function Build-CivicUiCheck([string]$name, [string]$project) {
    $civicUiOut = Join-Path $civicUiWork "$name/out"
    $civicUiObj = (Join-Path $civicUiWork "$name/obj") + [IO.Path]::DirectorySeparatorChar
    New-Item -ItemType Directory -Path $civicUiOut, $civicUiObj -Force | Out-Null
    & dotnet build (Join-Path $PSScriptRoot $project) --configuration Release --output $civicUiOut "-p:BaseIntermediateOutputPath=$civicUiObj" "-p:GameManagedPath=$civicUiManaged" --nologo
    if ($LASTEXITCODE -ne 0) { throw "Isolated $name compilation failed: $LASTEXITCODE" }
}
Build-CivicUiCheck 'behaviour' 'Behaviour.csproj'
& dotnet (Join-Path $civicUiWork 'behaviour/out/Behaviour.dll')
if ($LASTEXITCODE -ne 0) { throw "Facade UI behaviour checks failed: $LASTEXITCODE" }
Build-CivicUiCheck 'native-api' 'NativeCompile.csproj'
Write-Output 'Native CS1 UI API compile PASS (net35; compiled only, not runtime loaded).'
Write-Output 'No Mod/Core/Simulation build, provider load/query/activation, game, installation or configuration change.'
