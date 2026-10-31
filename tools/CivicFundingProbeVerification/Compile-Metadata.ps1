param([string]$RoslynDirectory='C:\Program Files\dotnet\sdk\9.0.308\Roslyn\bincore')
$ErrorActionPreference='Stop'
$qaRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $qaRoot 'scripts/Enter-Project.ps1')
$qaOutput=Join-Path $qaRoot '.cache/civic-funding-probe-verification'
New-Item -ItemType Directory -Path $qaOutput -Force|Out-Null
$qaManaged=Join-Path ((Get-Content (Join-Path $qaRoot 'config/local.paths.json') -Raw|ConvertFrom-Json).CitiesSkylines) 'Cities_Data/Managed'
$qaNet35=Join-Path $qaRoot '.cache/nuget/packages/microsoft.netframework.referenceassemblies.net35/1.0.3/build/.NETFramework/v3.5'
$qaArguments=@('/nologo','/target:library','/langversion:7.3','/nostdlib+','/nowarn:0436,0169,0414,0649',('/out:'+(Join-Path $qaOutput 'CivicDriverSlice.dll')))
foreach($qaName in @('mscorlib.dll','System.dll','System.Core.dll')){$qaArguments+='/r:'+(Join-Path $qaNet35 $qaName)}
foreach($qaName in @('Assembly-CSharp.dll','ColossalManaged.dll','UnityEngine.dll','ICities.dll')){$qaArguments+='/r:'+(Join-Path $qaManaged $qaName)}
foreach($qaName in @('SC4Evolution.Core.dll','SC4Evolution.Simulation.dll','SC4Evolution.dll')){$qaArguments+='/r:'+(Join-Path $qaRoot ('artifacts/mod/'+$qaName))}
$qaInputs=@(
 'src/SC4Evolution.Mod/Diagnostics/AutonomousTestDriver.cs',
 'src/SC4Evolution.Mod/Diagnostics/AutonomousTestDriver.CivicFunding.cs',
 'src/SC4Evolution.Mod/Diagnostics/AutonomousTestDriver.OrganicResidential.cs',
 'src/SC4Evolution.Mod/Diagnostics/AutonomousTestDriver.OrganicRoundtrip.cs',
 'src/SC4Evolution.Mod/Diagnostics/AutonomousTestDriver.SaveReload.cs',
 'src/SC4Evolution.Mod/Diagnostics/AutonomousTestDriver.NativeDiagnostics.cs',
 'src/SC4Evolution.Mod/Diagnostics/EconomicSaveRoundtripOracle.cs',
 'src/SC4Evolution.Mod/Runtime/EconomicSaveEnvelope.cs',
 'src/SC4Evolution.Mod/Economy/ResidentialCapacityObserver.cs',
 'src/SC4Evolution.Mod/Civic/CivicFundingBridge.cs',
 'src/SC4Evolution.Simulation/CivicFundingLedger.cs',
 'src/SC4Evolution.Simulation/CivicFundingLedgerCodec.cs',
 'src/SC4Evolution.Simulation/CivicFundingEvaluation.cs',
 'tools/CivicFundingProbeVerification/CompileFacades.cs')
foreach($qaPath in $qaInputs){$qaArguments+=Join-Path $qaRoot $qaPath}
& dotnet (Join-Path $RoslynDirectory 'csc.dll') @qaArguments
if($LASTEXITCODE -ne 0){throw 'Civic source slice metadata compile failed.'}
[ordered]@{scope='net35 C#7.3 actual driver/bridge/ledger/codec sources against installed native metadata; hook/runtime internal dependencies facaded; output never executed';passed=$true;nativeInvoked=$false;providerInvoked=$false;modBuilt=$false;timestampUtc=[DateTime]::UtcNow.ToString('o');sourceInputs=@(foreach($qaPath in $qaInputs){[ordered]@{path=$qaPath;sha256=(Get-FileHash -LiteralPath (Join-Path $qaRoot $qaPath)).Hash}})}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $qaOutput 'metadata-result.json') -Encoding utf8
'PASS Civic source slice net35/C#7.3 metadata compile; output not executed; no Mod build/native/provider.'
