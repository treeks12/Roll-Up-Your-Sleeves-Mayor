param([string]$RoslynDirectory='C:\Program Files\dotnet\sdk\9.0.308\Roslyn\bincore')
$ErrorActionPreference='Stop'
$qaRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $qaRoot 'scripts/Enter-Project.ps1')
$qaManaged=Join-Path ((Get-Content (Join-Path $qaRoot 'config/local.paths.json') -Raw|ConvertFrom-Json).CitiesSkylines) 'Cities_Data/Managed'
$qaNet35=Join-Path $qaRoot '.cache/nuget/packages/microsoft.netframework.referenceassemblies.net35/1.0.3/build/.NETFramework/v3.5'
$qaOutput=Join-Path $qaRoot '.cache/organic-roundtrip-verification'
New-Item -ItemType Directory -Path $qaOutput -Force | Out-Null
$qaArguments=@('/nologo','/target:library','/langversion:7.3','/nostdlib+','/nowarn:0436,0169,0414',('/out:'+(Join-Path $qaOutput 'DriverSlice.dll')))
foreach($qaName in @('mscorlib.dll','System.dll','System.Core.dll')){$qaArguments+='/r:'+(Join-Path $qaNet35 $qaName)}
foreach($qaName in @('Assembly-CSharp.dll','ColossalManaged.dll','UnityEngine.dll','ICities.dll')){$qaArguments+='/r:'+(Join-Path $qaManaged $qaName)}
foreach($qaName in @('SC4Evolution.Core.dll','SC4Evolution.Simulation.dll','SC4Evolution.dll')){$qaArguments+='/r:'+(Join-Path $qaRoot ('artifacts/mod/'+$qaName))}
foreach($qaName in @('AutonomousTestDriver.cs','AutonomousTestDriver.OrganicResidential.cs','AutonomousTestDriver.OrganicRoundtrip.cs','AutonomousTestDriver.SaveReload.cs','AutonomousTestDriver.NativeDiagnostics.cs','EconomicSaveRoundtripOracle.cs')){$qaArguments+=Join-Path $qaRoot ('src/SC4Evolution.Mod/Diagnostics/'+$qaName)}
$qaArguments+=Join-Path $qaRoot 'src/SC4Evolution.Mod/Runtime/EconomicSaveEnvelope.cs'
$qaArguments+=Join-Path $qaRoot 'src/SC4Evolution.Mod/Economy/ResidentialCapacityObserver.cs'
$qaArguments+=Join-Path $PSScriptRoot 'CompileFacades.cs'
& dotnet (Join-Path $RoslynDirectory 'csc.dll') @qaArguments
if($LASTEXITCODE -ne 0){throw 'Driver slice metadata compile failed.'}
'PASS net35/C#7.3 driver sources + inert internal dependency facades; output never executed; no Mod build/native/provider.'
