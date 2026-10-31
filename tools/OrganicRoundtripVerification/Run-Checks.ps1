param([string]$RoslynDirectory='C:\Program Files\dotnet\sdk\9.0.308\Roslyn\bincore')
$ErrorActionPreference='Stop'
$qaRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $qaRoot 'scripts/Enter-Project.ps1')
$qaOutput=Join-Path $qaRoot '.cache/organic-roundtrip-verification'
New-Item -ItemType Directory -Path $qaOutput -Force|Out-Null
& (Join-Path $PSScriptRoot 'Compile-DriverSlice.ps1') -RoslynDirectory $RoslynDirectory
Add-Type -Path (Join-Path $RoslynDirectory 'Microsoft.CodeAnalysis.dll')
Add-Type -Path (Join-Path $RoslynDirectory 'Microsoft.CodeAnalysis.CSharp.dll')
function Get-QaNode([string]$path,[string]$kind,[string]$name){
 $qaTree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $qaRoot $path)))
 $qaNodes=@($qaTree.GetRoot().DescendantNodes()|Where-Object {$_.GetType().Name -eq $kind -and $_.Identifier.Text -eq $name})
 if($qaNodes.Count -ne 1){throw "Expected one real source node: $name"};$qaNodes[0].ToFullString()
}
$qaOrganic='src/SC4Evolution.Mod/Diagnostics/AutonomousTestDriver.OrganicRoundtrip.cs';$qaSave='src/SC4Evolution.Mod/Diagnostics/AutonomousTestDriver.SaveReload.cs';$qaDriver='src/SC4Evolution.Mod/Diagnostics/AutonomousTestDriver.cs'
$qaSource=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ProtocolChecks.cs'))
$qaMethods=@('ArmOwnedReload','RevokeUnusedReloadPermit','ConsumeOwnedReloadPermit','ConsumeRoundtripUnloadAcknowledgment','RequireInverseHomeCoverage','AppendBuildingPrefabContract','AppendNetPrefabContract','AppendItemClass','SameInputs','AllZero','RequireCleanupOwner','InterruptRoundtrip')|ForEach-Object{Get-QaNode $qaOrganic 'MethodDeclarationSyntax' $_}
$qaOrganicBase='src/SC4Evolution.Mod/Diagnostics/AutonomousTestDriver.OrganicResidential.cs'
$qaMethods+=@('RequestCleanup','CleanupOwned')|ForEach-Object{Get-QaNode $qaOrganicBase 'MethodDeclarationSyntax' $_}
$qaMethods+=Get-QaNode $qaOrganicBase 'MethodDeclarationSyntax' 'BeforeLevelUnload'
$qaMethods+=Get-QaNode $qaOrganicBase 'PropertyDeclarationSyntax' 'CleanupReady'
$qaMethods+=Get-QaNode $qaOrganic 'PropertyDeclarationSyntax' 'OwnedReloadWasObserved'
$qaSaveMethods=@('IsSubmittingOwnedReload','ConsumeOwnedUnload','CancelBeforePluginRemoval','BeforeLevelUnload','RequestCleanup','QueueForcedRelease','StopAfterCurrentWork')|ForEach-Object{Get-QaNode $qaSave 'MethodDeclarationSyntax' $_}
$qaSaveMethods+=@('NativeWorkSettled','FixtureCleanupSafe','CleanupReady')|ForEach-Object{Get-QaNode $qaSave 'PropertyDeclarationSyntax' $_}
$qaDriverMethods=@('ReadRequest','Required','StrictBoolean','RoundtripCleanupReady')|ForEach-Object{Get-QaNode $qaDriver 'MethodDeclarationSyntax' $_}
$qaSource=$qaSource.Replace('// ORGANIC_METHODS',($qaMethods -join "`n")).Replace('// SAVE_METHODS',($qaSaveMethods -join "`n")).Replace('// DRIVER_METHODS',($qaDriverMethods -join "`n")).Replace('// REQUEST_CLASS',(Get-QaNode $qaDriver 'ClassDeclarationSyntax' 'Request'))
[IO.File]::WriteAllText((Join-Path $qaOutput 'ProtocolChecks.g.cs'),$qaSource)
Add-Type -TypeDefinition $qaSource -WarningAction SilentlyContinue
$qaCount=[SC4Evolution.Mod.Diagnostics.AutonomousTestDriver]::Run((Join-Path $qaOutput 'pure.request'))
$qaInputs=@($qaOrganic,$qaSave,$qaDriver,'src/SC4Evolution.Mod/Diagnostics/AutonomousTestDriver.OrganicResidential.cs','src/SC4Evolution.Mod/Diagnostics/AutonomousTestDriver.NativeDiagnostics.cs','scripts/Start-RuntimeTest.ps1')
[ordered]@{scope='ActualExtractedProtocolMethodsWithInertValueStubsAndMetadataOnlyDriverCompilation';checks=$qaCount;passed=$true;nativeInvoked=$false;providerInvoked=$false;modBuilt=$false;recordedUtc=[DateTime]::UtcNow.ToString('o');sourceInputs=@(foreach($qaPath in $qaInputs){[ordered]@{path=$qaPath;sha256=(Get-FileHash -LiteralPath (Join-Path $qaRoot $qaPath)).Hash}})}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $qaOutput 'result.json') -Encoding utf8
"PASS protocol checks=$qaCount; metadata-only driver compile; no engine/provider/Mod build."
