param(
 [string]$RoslynDirectory='C:\Program Files\dotnet\sdk\9.0.308\Roslyn\bincore',
 [string]$CoreAssemblyPath='artifacts/mod/SC4Evolution.Core.dll',
 [string]$SimulationAssemblyPath='artifacts/mod/SC4Evolution.Simulation.dll'
)
$ErrorActionPreference='Stop'
$qaRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $qaRoot 'scripts/Enter-Project.ps1')
$qaOutput=Join-Path $qaRoot '.cache/growth-gate-verification'
New-Item -ItemType Directory -Path $qaOutput -Force | Out-Null
$qaCore=if([IO.Path]::IsPathRooted($CoreAssemblyPath)){$CoreAssemblyPath}else{Join-Path $qaRoot $CoreAssemblyPath}
$qaSimulation=if([IO.Path]::IsPathRooted($SimulationAssemblyPath)){$SimulationAssemblyPath}else{Join-Path $qaRoot $SimulationAssemblyPath}
$qaPaths=Get-Content (Join-Path $qaRoot 'config/local.paths.json') -Raw | ConvertFrom-Json
$qaManaged=Join-Path ([string]$qaPaths.CitiesSkylines) 'Cities_Data/Managed'
$qaOwned=@('src/SC4Evolution.Mod/Economy/NewLotGrowthGate.cs','src/SC4Evolution.Mod/Economy/ResidentialClassPatches.cs','src/SC4Evolution.Mod/Economy/ResidentialGrowthNativeBoundary.cs','src/SC4Evolution.Mod/Economy/ResidentialCapacityMutationHooks.cs','src/SC4Evolution.Mod/Economy/ResidentialCapacityCreationHooks.cs','src/SC4Evolution.Mod/Economy/EconomicDemandExtension.cs')
# Compile only these boundary sources and bridge facades against real API metadata.
# This does not build the Mod project or execute any installed game/provider code.
$qaNet35=Join-Path $qaRoot '.cache/nuget/packages/microsoft.netframework.referenceassemblies.net35/1.0.3/build/.NETFramework/v3.5'
$qaCompile=@('/nologo','/target:library','/langversion:7.3','/nostdlib+',('/out:'+(Join-Path $qaOutput 'ActualApiBoundaryCheck.dll')))
foreach($qaLibrary in @('mscorlib.dll','System.dll','System.Core.dll')){$qaCompile+='/reference:'+(Join-Path $qaNet35 $qaLibrary)}
foreach($qaLibrary in @('Assembly-CSharp.dll','ColossalManaged.dll','UnityEngine.dll','ICities.dll')){$qaCompile+='/reference:'+(Join-Path $qaManaged $qaLibrary)}
foreach($qaLibrary in @($qaCore,$qaSimulation,(Join-Path $qaRoot 'tools/CitiesHarmony/provider/CitiesHarmony/CitiesHarmony.Harmony.dll'),(Join-Path $qaRoot 'tools/CitiesHarmony/api/2.2.0/lib/net35/CitiesHarmony.API.dll'))){$qaCompile+='/reference:'+$qaLibrary}
$qaCompile+=@($qaOwned | ForEach-Object {Join-Path $qaRoot $_})
$qaCompile+=Join-Path $PSScriptRoot 'Facades.cs'
& dotnet (Join-Path $RoslynDirectory 'csc.dll') @qaCompile
if($LASTEXITCODE -ne 0){throw 'Isolated net35 boundary API metadata compilation failed.'}
if('SC4Evolution.Mod.Economy.GrowthGateBehaviorChecks' -as [type]){throw 'Run in a fresh PowerShell process.'}
[void][Reflection.Assembly]::LoadFrom($qaCore)
[void][Reflection.Assembly]::LoadFrom($qaSimulation)
$qaFiles=@('NativeStubs.cs','Facades.cs','BehaviorChecks.cs') | ForEach-Object {Join-Path $PSScriptRoot $_}
$qaFiles+=@($qaOwned | ForEach-Object {Join-Path $qaRoot $_})
$qaRefs=@(Get-ChildItem -LiteralPath (Join-Path $PSHOME ref) -Filter '*.dll' | ForEach-Object FullName)
$qaRefs+=@($qaSimulation,$qaCore)
Add-Type -Path $qaFiles -ReferencedAssemblies $qaRefs -CompilerOptions '/langversion:7.3' -WarningAction SilentlyContinue
$qaBehavior=[SC4Evolution.Mod.Economy.GrowthGateBehaviorChecks]::Run()
Add-Type -LiteralPath (Join-Path $qaRoot 'analysis/generated/ui-native-notifications/tools/.store/ilspycmd/11.1.0.9782/ilspycmd/11.1.0.9782/tools/net10.0/any/Mono.Cecil.dll')
$qaFile=Join-Path $qaManaged 'Assembly-CSharp.dll'
$qaModule=[Mono.Cecil.ModuleDefinition]::ReadModule($qaFile)
$qaStream=[IO.File]::OpenRead($qaFile)
$qaPe=[System.Reflection.PortableExecutable.PEReader]::new($qaStream)
$qaAssembly=[SC4Evolution.Mod.Economy.GrowthGateBehaviorChecks].Assembly
$qaFlags=[Reflection.BindingFlags]'Static,NonPublic'
$qaGate=$qaAssembly.GetType('SC4Evolution.Mod.Economy.NewLotGrowthGate',$true)
$qaClass=$qaAssembly.GetType('SC4Evolution.Mod.Economy.ResidentialClassPatches',$true)
$qaOpCodes=@{}
foreach($qaField in [Reflection.Emit.OpCodes].GetFields([Reflection.BindingFlags]'Public,Static')){$qaCode=$qaField.GetValue($null);$qaOpCodes[$qaCode.Name]=$qaCode}
$script:qaMetadata=0
function Assert-Qa($qaCondition,[string]$qaName){if(!$qaCondition){throw $qaName};$script:qaMetadata++}
function Convert-QaBody($qaNative){
 $qaGenerator=[Reflection.Emit.DynamicMethod]::new('ShapeOnly',[void],[type[]]@()).GetILGenerator();$qaLabels=@{};$qaByOffset=@{}
 foreach($qaInstruction in $qaNative.Body.Instructions){$qaLabels[$qaInstruction.Offset]=[Collections.Generic.List[Reflection.Emit.Label]]::new()}
 $qaBody=[Collections.Generic.List[HarmonyLib.CodeInstruction]]::new()
 foreach($qaInstruction in $qaNative.Body.Instructions){
  $qaOperand=$qaInstruction.Operand
  if($qaOperand -is [Mono.Cecil.MethodReference]){
   if($qaOperand.DeclaringType.FullName -eq 'BuildingManager' -and $qaOperand.Name -eq 'CreateBuilding'){$qaOperand=$qaGate.GetField('Create',$qaFlags).GetValue($null)}
   elseif($qaOperand.DeclaringType.FullName -eq 'BuildingManager' -and $qaOperand.Name -eq 'GetRandomBuildingInfo'){$qaOperand=$qaClass.GetField('Select',$qaFlags).GetValue($null)}
   elseif($qaOperand.DeclaringType.FullName -eq 'ColossalFramework.Math.Randomizer' -and $qaOperand.Name -eq 'Int32' -and $qaOperand.Parameters.Count -eq 1 -and $qaOperand.Parameters[0].ParameterType.FullName -eq 'System.UInt32'){$qaOperand=$qaGate.GetField('NativeRoll',$qaFlags).GetValue($null)}
   elseif($qaOperand.DeclaringType.FullName -eq 'ZoneBlock' -and $qaOperand.Name -eq 'IsGoodPlace'){$qaOperand=[ZoneBlock].GetMethod('IsGoodPlace')}
   elseif($qaOperand.DeclaringType.FullName -eq 'ColossalFramework.Singleton`1<SimulationManager>' -and $qaOperand.Name -eq 'get_instance'){$qaOperand=[ColossalFramework.Singleton[SimulationManager]].GetProperty('instance').GetGetMethod()}
  }elseif($qaOperand -is [Mono.Cecil.FieldReference]){
   $qaType=$qaAssembly.GetType($qaOperand.DeclaringType.FullName.Replace('/','+'))
   if($null -ne $qaType){$qaResolved=$qaType.GetField($qaOperand.Name);if($null -ne $qaResolved){$qaOperand=$qaResolved}}
  }elseif($qaOperand -is [Mono.Cecil.Cil.VariableDefinition]){$qaOperand=[int]$qaOperand.Index}
  elseif($qaOperand -is [Mono.Cecil.Cil.Instruction]){
   # Actual Harmony MethodBodyReader.FinalizeILCodes defines a fresh label for
   # EACH branch, attaching distinct labels to a shared target instruction.
   $qaBranchLabel=$qaGenerator.DefineLabel();$qaLabels[$qaOperand.Offset].Add($qaBranchLabel);$qaOperand=$qaBranchLabel
  }elseif($qaOperand -is [Mono.Cecil.Cil.Instruction[]]){
   $qaSwitchLabels=[Collections.Generic.List[Reflection.Emit.Label]]::new()
   foreach($qaTarget in $qaOperand){$qaBranchLabel=$qaGenerator.DefineLabel();$qaLabels[$qaTarget.Offset].Add($qaBranchLabel);$qaSwitchLabels.Add($qaBranchLabel)}
   $qaOperand=$qaSwitchLabels.ToArray()
  }
  $qaConverted=[HarmonyLib.CodeInstruction]::new($qaOpCodes[$qaInstruction.OpCode.Name],$qaOperand)
  $qaByOffset[$qaInstruction.Offset]=$qaConverted;$qaBody.Add($qaConverted)
 }
 foreach($qaInstruction in $qaNative.Body.Instructions){$qaByOffset[$qaInstruction.Offset].labels.AddRange($qaLabels[$qaInstruction.Offset])}
 return ,$qaBody
}
function Copy-QaBody($qaBody){$qaCopy=[Collections.Generic.List[HarmonyLib.CodeInstruction]]::new();foreach($qaInstruction in $qaBody){$qaCopy.Add([HarmonyLib.CodeInstruction]::new($qaInstruction))};return ,$qaCopy}
function Invoke-QaTranspiler($qaMethod,$qaBody){$qaArgs=[object[]]::new(1);$qaArgs[0]=$qaBody;return ,$qaMethod.Invoke($null,$qaArgs)}
try{
 $qaBoundarySource=Get-Content -LiteralPath (Join-Path $qaRoot 'src/SC4Evolution.Mod/Economy/ResidentialGrowthNativeBoundary.cs') -Raw
 foreach($qaSpec in @(@('ZoneBlock','SimulationStep',1),@('PrivateBuildingAI','SimulationStep',3),@('ResidentialBuildingAI','SimulationStep',3),@('BuildingManager','ReleaseBuilding',1),@('PrivateBuildingAI','StartUpgrading',2))){
  $qaNative=($qaModule.Types | Where-Object Name -eq $qaSpec[0]).Methods | Where-Object {$_.Name -eq $qaSpec[1] -and $_.Parameters.Count -eq $qaSpec[2]}
  $qaRawBody=[System.Reflection.Metadata.PEReaderExtensions]::GetMethodBody($qaPe,[int]$qaNative.RVA);$qaBytes=$qaRawBody.GetILBytes()
  $qaIlHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($qaBytes)).ToLowerInvariant()
  $qaMaterial='ILBodyV1;maxStack='+$qaRawBody.MaxStack+';initLocals='+[int]$qaRawBody.LocalVariablesInitialized+';IL='+[Convert]::ToHexString($qaBytes).ToLowerInvariant()+';locals='
  foreach($qaLocal in $qaNative.Body.Variables){$qaMaterial+=$qaLocal.Index.ToString()+':'+$qaLocal.VariableType.FullName.Replace('/','+')+':0;'};$qaMaterial+='EH='
  $qaBodyHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($qaMaterial))).ToLowerInvariant()
  Assert-Qa ($qaBoundarySource.Contains('"'+$qaIlHash+'"')) ('literal installed IL pin '+$qaSpec[0])
  Assert-Qa ($qaBoundarySource.Contains('"'+$qaBodyHash+'"')) ('literal locals/EH/body pin '+$qaSpec[0])
  Assert-Qa ($qaNative.Body.ExceptionHandlers.Count -eq 0) ('native EH empty '+$qaSpec[0])
  if($qaSpec[1] -ne 'SimulationStep' -or $qaSpec[0] -eq 'ResidentialBuildingAI'){continue}
  $qaBody=Convert-QaBody $qaNative;$qaType=if($qaSpec[0] -eq 'ZoneBlock'){$qaGate}else{$qaClass}
  $qaMethodName=if($qaSpec[0] -eq 'ZoneBlock'){'Transpile'}else{'TranspileRedevelopment'}
  $qaTranspiler=$qaType.GetMethod($qaMethodName,$qaFlags);$qaResult=Invoke-QaTranspiler $qaTranspiler (Copy-QaBody $qaBody)
  $qaAdded=if($qaSpec[0] -eq 'ZoneBlock'){3}else{6}
  Assert-Qa ($qaResult.Count -eq $qaBody.Count+$qaAdded) ('exact insertion count '+$qaSpec[0])
  Assert-Qa (@($qaResult | Where-Object {$_.operand -is [Reflection.MethodInfo] -and $_.operand.DeclaringType -eq [ColossalFramework.Math.Randomizer] -and $_.operand.Name -eq 'Int32'}).Count -eq @($qaBody | Where-Object {$_.operand -is [Reflection.MethodInfo] -and $_.operand.DeclaringType -eq [ColossalFramework.Math.Randomizer] -and $_.operand.Name -eq 'Int32'}).Count) ('no extra random draw '+$qaSpec[0])
  $qaPredicate=if($qaSpec[0] -eq 'ZoneBlock'){'AdmitCandidate'}else{'AdmitRedevelopment'}
  Assert-Qa (@($qaResult | Where-Object {$_.operand -is [Reflection.MethodInfo] -and $_.operand.Name -eq $qaPredicate}).Count -eq 1) ('one admission predicate '+$qaSpec[0])
  if($qaSpec[0] -eq 'PrivateBuildingAI'){
   $qaDraw=-1
   for($qaI=1;$qaI -lt $qaBody.Count;$qaI++){if($qaBody[$qaI].operand -is [Reflection.MethodInfo] -and $qaBody[$qaI].operand.Name -eq 'Int32' -and $qaBody[$qaI-1].operand -eq 100){$qaDraw=$qaI;break}}
   Assert-Qa (!$qaBody[$qaDraw+2].operand.Equals($qaBody[$qaDraw+7].operand)) 'provider-shaped distinct labels for convergent redevelopment branches'
   $qaShared=Copy-QaBody $qaBody;$qaShared[$qaDraw+7].operand=$qaShared[$qaDraw+2].operand
   $qaSharedResult=Invoke-QaTranspiler $qaTranspiler $qaShared
   Assert-Qa ($qaSharedResult.Count -eq $qaBody.Count+6) 'shared-label same-target representation also accepted'
   foreach($qaTargetMutation in @('different-target','missing-target','ambiguous-target','duplicate-label-definition')){
    $qaBad=Copy-QaBody $qaBody;$qaLabel=$qaBad[$qaDraw+7].operand
    if($qaTargetMutation -eq 'different-target' -or $qaTargetMutation -eq 'missing-target'){
     # A label from another generator may have an equal numeric ID. Allocate
     # enough labels to choose one absent from this entire instruction stream.
     $qaOtherGenerator=[Reflection.Emit.DynamicMethod]::new('FreshLabels',[void],[type[]]@()).GetILGenerator()
     do{$qaLabel=$qaOtherGenerator.DefineLabel();$qaPresent=@($qaBad | Where-Object {$_.labels.Contains($qaLabel)}).Count -ne 0}while($qaPresent)
     $qaBad[$qaDraw+7].operand=$qaLabel
     if($qaTargetMutation -eq 'different-target'){$qaBad[$qaDraw+8].labels.Add($qaLabel)}
    }elseif($qaTargetMutation -eq 'ambiguous-target'){$qaBad[$qaDraw+8].labels.Add($qaLabel)}
    else{($qaBad | Where-Object {$_.labels.Contains($qaLabel)})[0].labels.Add($qaLabel)}
    $qaRejected=$false;try{[void](Invoke-QaTranspiler $qaTranspiler $qaBad)}catch{$qaRejected=$true}
    Assert-Qa $qaRejected ('reject redevelopment '+$qaTargetMutation)
   }
  }
  foreach($qaMutation in @('missing-draw','duplicate-create','wrong-threshold','wrong-branch','exception-region')){
   $qaBad=Copy-QaBody $qaBody;$qaDraw=-1
   for($qaI=1;$qaI -lt $qaBad.Count;$qaI++){if($qaBad[$qaI].operand -is [Reflection.MethodInfo] -and $qaBad[$qaI].operand.Name -eq 'Int32' -and $qaBad[$qaI-1].operand -eq 100){$qaDraw=$qaI;break}}
   if($qaMutation -eq 'missing-draw'){$qaBad[$qaDraw].opcode=[Reflection.Emit.OpCodes]::Nop;$qaBad[$qaDraw].operand=$null}
   elseif($qaMutation -eq 'duplicate-create'){$qaBad.Add([HarmonyLib.CodeInstruction]::new([Reflection.Emit.OpCodes]::Callvirt,$qaGate.GetField('Create',$qaFlags).GetValue($null)))}
   elseif($qaMutation -eq 'wrong-threshold'){$qaBad[$qaDraw+1].operand=9}
   elseif($qaMutation -eq 'wrong-branch'){$qaBad[$qaDraw+2].opcode=[Reflection.Emit.OpCodes]::Brtrue}
   else{$qaBad[$qaDraw+2].blocks.Add([object]::new())}
   $qaRejected=$false;try{[void](Invoke-QaTranspiler $qaTranspiler $qaBad)}catch{$qaRejected=$true}
   Assert-Qa $qaRejected ('reject '+$qaSpec[0]+' '+$qaMutation)
  }
 }
 $qaInputs=@(foreach($qaRelative in $qaOwned){[ordered]@{path=$qaRelative;sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $qaRoot $qaRelative)).Hash}})
 [ordered]@{scope='ActualOwnedBoundarySourcesWithInertNativeBridgeStubsAndInstalledMetadataOnly';recordedUtc=[DateTime]::UtcNow.ToString('o');behaviorChecks=$qaBehavior;metadataChecks=$script:qaMetadata;net35BoundaryApiCompilePassed=$true;engineInvoked=$false;providerInvoked=$false;modBuilt=$false;sourceInputs=$qaInputs;harnessSha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $PSCommandPath).Hash;passed=$true}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $qaOutput 'result.json') -Encoding utf8
 'PASS behavior='+$qaBehavior+' metadata='+$script:qaMetadata+'; no engine/provider invocation; no Mod build'
}finally{$qaPe.Dispose();$qaStream.Dispose();$qaModule.Dispose()}
