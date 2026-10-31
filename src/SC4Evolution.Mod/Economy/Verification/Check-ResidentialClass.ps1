param([string]$OutputDirectory = 'artifacts/lot-class-check')
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../..'))
. (Join-Path $taskRoot 'scripts/Enter-Project.ps1')
$taskPaths = Get-Content -LiteralPath (Join-Path $taskRoot 'config/local.paths.json') -Raw | ConvertFrom-Json
$taskManaged = Join-Path $taskPaths.CitiesSkylines 'Cities_Data/Managed'
$taskOutput = Join-Path $taskRoot $OutputDirectory
foreach ($taskName in @('UnityEngine.dll', 'ColossalManaged.dll', 'Assembly-CSharp-firstpass.dll', 'ICities.dll', 'Assembly-CSharp.dll')) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $taskManaged $taskName))
}
[void][Reflection.Assembly]::LoadFrom((Join-Path $taskRoot 'tools/CitiesHarmony/provider/CitiesHarmony/CitiesHarmony.Harmony.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $taskOutput 'SC4Evolution.Simulation.dll'))
$taskMod = [Reflection.Assembly]::LoadFrom((Join-Path $taskOutput 'SC4Evolution.dll'))
$taskGame = [Reflection.Assembly]::LoadFrom((Join-Path $taskManaged 'Assembly-CSharp.dll'))
Add-Type -Path (Join-Path $taskRoot 'tools/bin/.store/ilspycmd/9.1.0.7988/ilspycmd/9.1.0.7988/tools/net8.0/any/Mono.Cecil.dll')
$taskFlags = [Reflection.BindingFlags]'NonPublic,Static'
$taskPatches = $taskMod.GetType('SC4Evolution.Mod.Economy.ResidentialClassPatches', $true)
$taskSelect = $taskPatches.GetField('Select', $taskFlags).GetValue($null)
$taskCreate = $taskPatches.GetField('Create', $taskFlags).GetValue($null)
$taskTranspile = $taskPatches.GetMethod('TranspileRedevelopment', $taskFlags)
$taskCecil = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $taskManaged 'Assembly-CSharp.dll'))
try {
    $taskMethod = $taskCecil.MainModule.GetType('PrivateBuildingAI').Methods | Where-Object { $_.Name -eq 'SimulationStep' -and $_.Parameters.Count -eq 3 }
    if (@($taskMethod).Count -ne 1 -or $taskMethod.Body.ExceptionHandlers.Count -ne 0) { throw 'Unexpected source method shape.' }
    $taskOpcodeMap = @{}
    foreach ($taskField in [Reflection.Emit.OpCodes].GetFields([Reflection.BindingFlags]'Public,Static')) {
        $taskOpcode = $taskField.GetValue($null)
        $taskOpcodeMap[$taskOpcode.Name] = $taskOpcode
    }
    $taskDynamic = [Reflection.Emit.DynamicMethod]::new('MetadataValidationOnly', [void], [type[]]@())
    $taskGenerator = $taskDynamic.GetILGenerator()
    $taskLabels = @{}
    foreach ($taskInstruction in $taskMethod.Body.Instructions) { $taskLabels[$taskInstruction.Offset] = $taskGenerator.DefineLabel() }
    $taskBody = [Collections.Generic.List[HarmonyLib.CodeInstruction]]::new()
    foreach ($taskInstruction in $taskMethod.Body.Instructions) {
        $taskOperand = $taskInstruction.Operand
        if ($taskOperand -is [Mono.Cecil.MethodReference] -and $taskOperand.DeclaringType.FullName -eq 'BuildingManager') {
            if ($taskOperand.Name -eq 'GetRandomBuildingInfo') { $taskOperand = $taskSelect }
            elseif ($taskOperand.Name -eq 'CreateBuilding') { $taskOperand = $taskCreate }
        } elseif ($taskOperand -is [Mono.Cecil.FieldReference] -and $taskOperand.DeclaringType.FullName -eq 'Building' -and $taskOperand.Name -eq 'm_flags') {
            $taskOperand = $taskGame.GetType('Building').GetField('m_flags')
        } elseif ($taskOperand -is [Mono.Cecil.Cil.Instruction]) { $taskOperand = $taskLabels[$taskOperand.Offset] }
        elseif ($taskOperand -is [Mono.Cecil.Cil.VariableDefinition]) { $taskOperand = $taskOperand.Index }
        $taskConverted = [HarmonyLib.CodeInstruction]::new([Reflection.Emit.OpCode]$taskOpcodeMap[$taskInstruction.OpCode.Name], [object]$taskOperand)
        $taskConverted.labels.Add([Reflection.Emit.Label]$taskLabels[$taskInstruction.Offset])
        $taskBody.Add($taskConverted)
    }
    function Invoke-Transpiler($taskInput) {
        $taskArguments = [object[]]::new(1); $taskArguments[0] = $taskInput
        return ,$taskTranspile.Invoke($null, $taskArguments)
    }
    function Copy-Body {
        $taskCopy = [Collections.Generic.List[HarmonyLib.CodeInstruction]]::new()
        foreach ($taskInstruction in $taskBody) { $taskCopy.Add([HarmonyLib.CodeInstruction]::new($taskInstruction)) }
        return ,$taskCopy
    }
    $taskValid = Invoke-Transpiler (Copy-Body)
    if ($taskValid.Count -ne $taskBody.Count + 2) { throw 'Expected exactly two argument loads appended at selection.' }
    $taskSelectCount = @($taskValid | Where-Object { $_.operand.Name -eq 'SelectRedevelopment' }).Count
    $taskCommitCount = @($taskValid | Where-Object { $_.operand.Name -eq 'CommitRedevelopment' }).Count
    if ($taskSelectCount -ne 1 -or $taskCommitCount -ne 1) { throw 'Wrapper counts differ.' }
    Write-Output ('PASS actual installed IL: ' + $taskBody.Count + ' instructions -> ' + $taskValid.Count + '; one selection and one commit wrapper.')
    foreach ($taskMutation in @('missing-create', 'duplicate-create', 'wrong-demolishing', 'wrong-null-branch')) {
        $taskBad = Copy-Body
        if ($taskMutation -eq 'missing-create') {
            for ($taskIndex = $taskBad.Count - 1; $taskIndex -ge 0; $taskIndex--) { if ([object]::Equals($taskBad[$taskIndex].operand, $taskCreate)) { $taskBad.RemoveAt($taskIndex) } }
        } elseif ($taskMutation -eq 'duplicate-create') { $taskBad.Add([HarmonyLib.CodeInstruction]::new([Reflection.Emit.OpCodes]::Callvirt, $taskCreate)) }
        else {
            for ($taskIndex = 0; $taskIndex -lt $taskBad.Count; $taskIndex++) {
                if ([object]::Equals($taskBad[$taskIndex].operand, $taskSelect)) {
                    if ($taskMutation -eq 'wrong-demolishing') { $taskBad[$taskIndex + 7].operand = 0 }
                    else { $taskBad[$taskIndex + 3].opcode = [Reflection.Emit.OpCodes]::Brtrue }
                    break
                }
            }
        }
        $taskRejected = $false
        try { [void](Invoke-Transpiler $taskBad) } catch { if ($_.Exception.InnerException -is [InvalidOperationException]) { $taskRejected = $true } else { throw } }
        if (-not $taskRejected) { throw ('Failed to reject ' + $taskMutation) }
        Write-Output ('PASS fail-closed metadata guard: ' + $taskMutation)
    }
} finally { $taskCecil.Dispose() }

$taskChooser = $taskMod.GetType('SC4Evolution.Mod.Economy.ResidentialClassBridge', $true).GetMethod('ChooseClass', $taskFlags)
function Assert-Choice([int[]]$taskNormalized, [float[]]$taskActive, [int]$taskExpected) {
    $taskStates = [SC4Evolution.Simulation.DemandNodeState[]]::new(3)
    # Reverse input order deliberately: positive ties still choose low developer key.
    for ($taskIndex = 0; $taskIndex -lt 3; $taskIndex++) {
        $taskState = [SC4Evolution.Simulation.DemandNodeState]::new()
        $taskState.Key = 0x21030 - $taskIndex * 0x10
        $taskState.NormalizedDemand = $taskNormalized[2 - $taskIndex]
        $taskState.ActiveDemand = $taskActive[2 - $taskIndex]
        $taskStates[$taskIndex] = $taskState
    }
    $taskArgs = [object[]]::new(1); $taskArgs[0] = $taskStates
    $taskChoice = [int]$taskChooser.Invoke($null, $taskArgs)
    if ($taskChoice -ne $taskExpected) { throw ('Wrong class choice: ' + $taskChoice + ' expected ' + $taskExpected) }
}
Assert-Choice @(100, 20, 10) @(1, 1, 1) 1
Assert-Choice @(10, 20, 100) @(1, 1, 1) 3
Assert-Choice @(50, 50, 50) @(1, 1, 1) 1
Assert-Choice @(0, -1, -20) @(1, 1, 1) 0
Assert-Choice @(5, 100, 1000) @(1, 0, -1) 1
Write-Output 'PASS class selection: low/high on the same level-independent input shape, key-order tie, nonpositive normalized and inactive rejection.'

$taskCurrent = $taskPatches.GetField('_current', $taskFlags)
$taskBegin = $taskPatches.GetMethod('BeginAttempt', $taskFlags)
$taskEnd = $taskPatches.GetMethod('EndAttempt', $taskFlags)
$taskOuterArgs = [object[]]::new(1)
[void]$taskBegin.Invoke($null, $taskOuterArgs)
$taskOuter = $taskCurrent.GetValue($null)
$taskInnerArgs = [object[]]::new(1)
[void]$taskBegin.Invoke($null, $taskInnerArgs)
$taskInner = $taskCurrent.GetValue($null)
if ([object]::ReferenceEquals($taskOuter, $taskInner) -or -not [object]::ReferenceEquals($taskInnerArgs[0], $taskOuter)) { throw 'Reentrant scopes were shared.' }
$taskFailure = [InvalidOperationException]::new('scripted original selection/creation failure')
$taskEndArgs = [object[]]::new(2); $taskEndArgs[0] = $taskFailure; $taskEndArgs[1] = $taskInnerArgs[0]
$taskReturned = $taskEnd.Invoke($null, $taskEndArgs)
if (-not [object]::ReferenceEquals($taskReturned, $taskFailure) -or -not [object]::ReferenceEquals($taskCurrent.GetValue($null), $taskOuter)) { throw 'Finalizer did not preserve exception and restore prior scope.' }
$taskEndArgs[0] = $null; $taskEndArgs[1] = $taskOuterArgs[0]
[void]$taskEnd.Invoke($null, $taskEndArgs)
if ($null -ne $taskCurrent.GetValue($null)) { throw 'Outer scope leaked.' }
$taskNextArgs = [object[]]::new(1); [void]$taskBegin.Invoke($null, $taskNextArgs)
$taskNext = $taskCurrent.GetValue($null)
if ($taskNext.GetType().GetField('Pending', [Reflection.BindingFlags]'NonPublic,Instance').GetValue($taskNext)) { throw 'Unrelated subsequent attempt inherited a pending choice.' }
$taskEndArgs[1] = $taskNextArgs[0]; [void]$taskEnd.Invoke($null, $taskEndArgs)
Write-Output 'PASS nested/reentrant attempt finalizers restore prior context, preserve original exceptions, and leave later attempts fresh.'

$taskCompiled = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $taskOutput 'SC4Evolution.dll'))
try {
    $taskCommit = $taskCompiled.MainModule.GetType('SC4Evolution.Mod.Economy.ResidentialClassPatches').Methods | Where-Object Name -eq 'CommitRedevelopment'
    $taskNativeCalls = @($taskCommit.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.Name -eq 'BuildingManager' -and $_.Operand.Name -eq 'CreateBuilding' })
    if ($taskNativeCalls.Count -ne 1 -or @($taskCommit.Body.ExceptionHandlers | Where-Object HandlerType -eq 'Finally').Count -ne 1) { throw 'Commit-only native call/finally cleanup shape changed.' }
    foreach ($taskMirror in @('HomeChanged', 'HomeUnitChanged')) {
        $taskMirrorMethod = $taskPatches.GetMethod($taskMirror, $taskFlags)
        if (-not $taskMirrorMethod.GetParameters()[0].ParameterType.IsByRef) { throw 'Struct mirror is not ref __instance.' }
    }
    $taskBridgeType = $taskCompiled.MainModule.GetType('SC4Evolution.Mod.Economy.ResidentialClassBridge')
    foreach ($taskEvent in @('Commit', 'AfterUpgrade')) {
        $taskEventMethod = $taskBridgeType.Methods | Where-Object Name -eq $taskEvent
        if (@($taskEventMethod.Body.Instructions | Where-Object { $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Stfld -and $_.Operand.Name -eq '_cursor' }).Count -ne 0) { throw ($taskEvent + ' restarts the full sweep.') }
        if (@($taskEventMethod.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'QueueHomeLocked' }).Count -ne 1) { throw ($taskEvent + ' does not queue affected-home reconciliation.') }
    }
    Write-Output 'PASS compiled commit-only wrapper has one native creation call and finally cleanup; both native struct mirrors use ref parameters.'
    Write-Output 'PASS compiled Commit and AfterUpgrade queue affected homes without resetting the full reconciliation cursor.'
} finally { $taskCompiled.Dispose() }

$taskRuntimeBridge = $taskMod.GetType('SC4Evolution.Mod.Economy.ResidentialClassBridge')
$taskCtor = $taskRuntimeBridge.GetConstructors([Reflection.BindingFlags]'NonPublic,Instance')[0]
$taskCtorArgs = [object[]]::new(4); $taskCtorArgs[0] = $null; $taskCtorArgs[1] = $null; $taskCtorArgs[2] = $false; $taskCtorArgs[3] = $null
$taskAdapter = $taskCtor.Invoke($taskCtorArgs)
$taskCursor = $taskRuntimeBridge.GetField('_cursor', [Reflection.BindingFlags]'NonPublic,Instance')
$taskCursor.SetValue($taskAdapter, 123456)
$taskQueue = $taskRuntimeBridge.GetMethod('QueueHomeLocked', [Reflection.BindingFlags]'NonPublic,Instance')
$taskQueueArgs = [object[]]::new(1)
for ($taskEvent = 1; $taskEvent -le 10000; $taskEvent++) {
    $taskQueueArgs[0] = [ushort](1 + ($taskEvent % 100))
    [void]$taskQueue.Invoke($taskAdapter, $taskQueueArgs)
    if ($taskCursor.GetValue($taskAdapter) -ne 123456) { throw 'Continuous affected-home notifications reset reconciliation progress.' }
}
$taskQueueField = $taskRuntimeBridge.GetField('_dirtyHomes', [Reflection.BindingFlags]'NonPublic,Instance').GetValue($taskAdapter)
if ($taskQueueField.Count -ne 100) { throw 'Affected-home work was not deduplicated.' }
Write-Output 'PASS 10,000 compiled affected-home notifications retain sweep position and deduplicate to 100 homes.'

$taskLedger = [SC4Evolution.Simulation.ResidentialClassLedger]::new([Guid]::NewGuid(), [uint32]1, [byte[]]::new(32))
$taskBefore = [SC4Evolution.Simulation.ResidentialLotIdentity]::new([ushort]1, [uint32]0, 0, 0, 'SameLevel1Prefab', 1, 1, [byte]2, [byte]3)
$taskSelected = $taskLedger.CommitCreated($taskBefore, [SC4Evolution.Simulation.ResidentialClass]::High)
$taskAfter = [SC4Evolution.Simulation.ResidentialLotIdentity]::new([ushort]1, [uint32]0, 0, 0, 'NativeUpgradedPrefab', 1, 2, [byte]3, [byte]2)
if (-not $taskLedger.RefreshUpgradeIdentity($taskBefore, $taskAfter)) { throw 'Shadow metadata upgrade did not refresh.' }
$taskSaved = [IO.MemoryStream]::new()
[SC4Evolution.Simulation.ResidentialClassLedgerCodec]::Write($taskSaved, $taskLedger)
$taskSaved.Position = 0
$taskReloaded = [SC4Evolution.Simulation.ResidentialClassLedgerCodec]::Read($taskSaved)
$taskRecord = $null
if (-not $taskReloaded.TryGet($taskAfter, [ref]$taskRecord) -or $taskRecord.Generation -ne $taskSelected.Generation -or $taskRecord.OccupantClass -ne [SC4Evolution.Simulation.ResidentialClass]::High -or $taskRecord.Origin -ne [SC4Evolution.Simulation.ClassOrigin]::Selected) { throw 'Upgrade/save/reload lost selected class or generation.' }
$taskSaved.Dispose()
Write-Output 'PASS selected High class, generation, Selected origin and zero native build index survive metadata-only upgrade and save/reload.'

$taskValidPayload = [IO.MemoryStream]::new()
[SC4Evolution.Simulation.ResidentialClassLedgerCodec]::Write($taskValidPayload, $taskReloaded)
$taskPayload = $taskValidPayload.ToArray(); $taskValidPayload.Dispose()
$taskCtorArgs[1] = $taskPayload; $taskCtorArgs[2] = $true
$taskRestoreAdapter = $taskCtor.Invoke($taskCtorArgs)
$taskInstanceFlags = [Reflection.BindingFlags]'NonPublic,Instance'
$taskRestoreMethod = $taskRuntimeBridge.GetMethod('RequireRestorationOnly', $taskInstanceFlags)
[void]$taskRestoreMethod.Invoke($taskRestoreAdapter, @('scripted provider unavailable'))
if ($taskRuntimeBridge.GetField('_bound', $taskInstanceFlags).GetValue($taskRestoreAdapter) -or
    $null -ne $taskRuntimeBridge.GetField('_loadFailure', $taskInstanceFlags).GetValue($taskRestoreAdapter) -or
    $null -eq $taskRuntimeBridge.GetField('_pending', $taskInstanceFlags).GetValue($taskRestoreAdapter) -or
    $taskRuntimeBridge.GetField('_requested', $taskInstanceFlags).GetValue($taskRestoreAdapter) -or
    $taskRuntimeBridge.GetField('_effects', $taskInstanceFlags).GetValue($taskRestoreAdapter)) { throw 'Valid provider-unavailable payload was rejected or effects remained armed.' }
# A bound valid ledger still cannot overwrite the old key without its identity hook.
$taskRuntimeBridge.GetField('_ledger', $taskInstanceFlags).SetValue($taskRestoreAdapter, $taskReloaded)
$taskSaveMethod = $taskRuntimeBridge.GetMethod('Save', $taskInstanceFlags)
if ($null -ne $taskSaveMethod.Invoke($taskRestoreAdapter, @())) { throw 'Restoration-only state overwrote saved metadata.' }
$taskMirrorMethod = $taskRuntimeBridge.GetMethod('TryMirrorValue', $taskFlags)
$taskWealthType = $taskGame.GetType('Citizen+Wealth')
$taskLevelType = $taskMirrorMethod.GetParameters()[3].ParameterType
$taskMirrorArgs = [object[]]::new(5)
$taskMirrorArgs[0] = $false; $taskMirrorArgs[1] = [Enum]::ToObject($taskWealthType, 2); $taskMirrorArgs[2] = [SC4Evolution.Simulation.ResidentialClass]::High
$taskMirrorArgs[3] = [Enum]::ToObject($taskLevelType, 0); $taskMirrorArgs[4] = [Enum]::ToObject($taskWealthType, 2)
if (-not $taskMirrorMethod.Invoke($null, $taskMirrorArgs) -or [int]$taskMirrorArgs[4] -ne 0) { throw 'Known SelectedHigh mirror on native level1 did not restore to native Low.' }
$taskMirrorArgs[1] = [Enum]::ToObject($taskWealthType, 1)
if ($taskMirrorMethod.Invoke($null, $taskMirrorArgs) -or [int]$taskMirrorArgs[4] -ne 1) { throw 'Restoration overwrote an unrelated changed native field.' }
Write-Output 'PASS valid provider-unavailable data remains bindable restoration-only, effects disabled, save suppressed; known High mirror restores native level1 Low while unrelated native changes remain untouched.'
Write-Output 'No native simulation methods were executed and no patches were installed.'
