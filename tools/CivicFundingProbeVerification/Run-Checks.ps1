param([string]$RoslynDirectory='C:\Program Files\dotnet\sdk\9.0.308\Roslyn\bincore')
$ErrorActionPreference='Stop'
$qaRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $qaRoot 'scripts/Enter-Project.ps1')
$qaOutput=Join-Path $qaRoot '.cache/civic-funding-probe-verification'
New-Item -ItemType Directory -Path $qaOutput -Force|Out-Null
& (Join-Path $PSScriptRoot 'Compile-Metadata.ps1') -RoslynDirectory $RoslynDirectory
Add-Type -Path (Join-Path $RoslynDirectory 'Microsoft.CodeAnalysis.dll')
Add-Type -Path (Join-Path $RoslynDirectory 'Microsoft.CodeAnalysis.CSharp.dll')
function Get-QaNode([string]$path,[string]$kind,[string]$name){
 $qaTree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $qaRoot $path)))
 $qaNodes=@($qaTree.GetRoot().DescendantNodes()|Where-Object {$_.GetType().Name -eq $kind -and $_.Identifier.Text -eq $name})
 if($qaNodes.Count -ne 1){throw "Expected one source node $name"}; $qaNodes[0].ToFullString()
}
$qaDriver='src/SC4Evolution.Mod/Diagnostics/AutonomousTestDriver.cs'
$qaCivic='src/SC4Evolution.Mod/Diagnostics/AutonomousTestDriver.CivicFunding.cs'
$qaMethods=@('ReadRequest','Required','StrictBoolean')|ForEach-Object {Get-QaNode $qaDriver 'MethodDeclarationSyntax' $_}
$qaMethods+=Get-QaNode $qaDriver 'ClassDeclarationSyntax' 'Request'
$qaMethods+=Get-QaNode $qaCivic 'MethodDeclarationSyntax' 'EfficiencyCeiling'
$qaSource=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'PureChecks.cs')).Replace('// METHODS',($qaMethods -join "`n"))
[IO.File]::WriteAllText((Join-Path $qaOutput 'PureChecks.g.cs'),$qaSource)
Add-Type -TypeDefinition $qaSource
$qaCount=[CivicPureChecks]::Run((Join-Path $qaOutput 'pure.request'))
$qaLifeMethods=@('RequestCleanup','CleanupOnSimulation','OwnsScene','BeforeLevelUnload','CancelBeforePluginRemoval','InterruptAndCleanup','Require','Phase','Add')|ForEach-Object{Get-QaNode $qaCivic 'MethodDeclarationSyntax' $_}
$qaLifeMethods+=@('CleanupReady','CanQuitCurrentScene','MayQuitUnderOwnedPause')|ForEach-Object{Get-QaNode $qaCivic 'PropertyDeclarationSyntax' $_}
$qaLifeMethods+=@('OwnedFacility','Budget')|ForEach-Object{Get-QaNode $qaCivic 'ClassDeclarationSyntax' $_}
$qaLifeSource=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'LifecycleChecks.cs')).Replace('// ACTUAL_METHODS',($qaLifeMethods -join "`n"))
[IO.File]::WriteAllText((Join-Path $qaOutput 'LifecycleChecks.g.cs'),$qaLifeSource)
Add-Type -TypeDefinition $qaLifeSource
$qaLifeCount=[CivicLifecycleChecks]::Run()
$qaTokens=$null;$qaErrors=$null;[Management.Automation.Language.Parser]::ParseFile((Join-Path $qaRoot 'scripts/Start-RuntimeTest.ps1'),[ref]$qaTokens,[ref]$qaErrors)|Out-Null
if($qaErrors.Count){throw ($qaErrors|Out-String)}
[ordered]@{scope='Extracted actual parser/rational expectations/lifecycle protocol; inert queue/native-value facades; no engine/provider/native methods';passed=$true;checks=$qaCount;lifecycleChecks=$qaLifeCount;cliParser=$true;nativeInvoked=$false;modBuilt=$false;timestampUtc=[DateTime]::UtcNow.ToString('o');sourceInputs=@(foreach($qaPath in @($qaDriver,$qaCivic,'scripts/Start-RuntimeTest.ps1')){[ordered]@{path=$qaPath;sha256=(Get-FileHash -LiteralPath (Join-Path $qaRoot $qaPath)).Hash}})}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $qaOutput 'result.json') -Encoding utf8
"PASS $qaCount pure extracted-method checks + $qaLifeCount lifecycle checks with inert facades; CLI parser valid; no engine/provider/Mod build."
