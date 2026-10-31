$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $projectRoot 'scripts/Enter-Project.ps1')

$projectPath = Join-Path $PSScriptRoot 'SC4NativeInventory.csproj'
Push-Location $Sc4EvolutionRoot
try {
    & dotnet run --project $projectPath --configuration Release -- --root $Sc4EvolutionRoot
    $runExitCode = $LASTEXITCODE
}
finally { Pop-Location }

if ($runExitCode -ne 0) {
    throw "SC4 native inventory failed with exit code $runExitCode."
}
