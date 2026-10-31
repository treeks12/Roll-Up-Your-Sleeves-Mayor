param([string]$OutputDirectory = 'artifacts/incoming-context-check', [string]$AssemblyName = 'IncomingDemand.Compile.dll')
$ErrorActionPreference = 'Stop'
$incomingRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../..'))
. (Join-Path $incomingRoot 'scripts/Enter-Project.ps1')
$incomingPaths = Get-Content (Join-Path $incomingRoot 'config/local.paths.json') -Raw | ConvertFrom-Json
$incomingManaged = Join-Path $incomingPaths.CitiesSkylines 'Cities_Data/Managed'
foreach ($incomingName in @('UnityEngine.dll', 'ColossalManaged.dll', 'Assembly-CSharp-firstpass.dll', 'ICities.dll', 'Assembly-CSharp.dll')) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $incomingManaged $incomingName))
}
[void][Reflection.Assembly]::LoadFrom((Join-Path $incomingRoot 'tools/CitiesHarmony/provider/CitiesHarmony/CitiesHarmony.Harmony.dll'))
$incomingPath = Join-Path (Join-Path $incomingRoot $OutputDirectory) $AssemblyName
if (Test-Path (Join-Path (Split-Path $incomingPath) 'SC4Evolution.Simulation.dll')) { [void][Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $incomingPath) 'SC4Evolution.Simulation.dll')) }
$incomingAssembly = [Reflection.Assembly]::LoadFrom($incomingPath)
Add-Type -Path (Join-Path $incomingRoot 'tools/bin/.store/ilspycmd/9.1.0.7988/ilspycmd/9.1.0.7988/tools/net8.0/any/Mono.Cecil.dll')
$incomingFlags = [Reflection.BindingFlags]'NonPublic,Static'
$incomingPatches = $incomingAssembly.GetType('SC4Evolution.Mod.Economy.IncomingResidentDemandPatches', $true)
$incomingConstruction = $incomingPatches.GetField('Construction', $incomingFlags).GetValue($null)
$incomingCalculator = $incomingPatches.GetField('Incoming', $incomingFlags).GetValue($null)
$incomingCallback = $incomingPatches.GetField('SharedCallback', $incomingFlags).GetValue($null)
$incomingGuard = $incomingPatches.GetMethod('ValidateBody', $incomingFlags)
$incomingOpcodes = @{}
foreach ($incomingField in [Reflection.Emit.OpCodes].GetFields([Reflection.BindingFlags]'Public,Static')) {
    $incomingOpcode = $incomingField.GetValue($null); $incomingOpcodes[$incomingOpcode.Name] = $incomingOpcode
}
$incomingGame = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $incomingManaged 'Assembly-CSharp.dll'))
$incomingCompiled = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($incomingPath)
try {
    $incomingZone = $incomingGame.MainModule.GetType('ZoneManager')
    foreach ($incomingTarget in @($incomingConstruction, $incomingCalculator)) {
        if (-not $incomingTarget.IsPrivate -or $incomingTarget.ReturnType -ne [int] -or $incomingTarget.GetParameters().Count -ne 1 -or $incomingTarget.GetParameters()[0].ParameterType.FullName -ne 'District&') { throw 'Calculator target signature differs.' }
        $incomingMethod = $incomingZone.Methods | Where-Object Name -eq $incomingTarget.Name
        $incomingBody = [Collections.Generic.List[HarmonyLib.CodeInstruction]]::new()
        foreach ($incomingInstruction in $incomingMethod.Body.Instructions) {
            # Guard only inspects call operands. Every other instruction remains
            # an opaque original operand; no method body or native function runs.
            $incomingOperand = $incomingInstruction.Operand
            if ($incomingOperand -is [Mono.Cecil.MethodReference] -and $incomingOperand.DeclaringType.Name -eq 'DemandWrapper' -and $incomingOperand.Name -eq 'OnCalculateResidentialDemand') { $incomingOperand = $incomingCallback }
            $incomingOpcode = $incomingOpcodes[$incomingInstruction.OpCode.Name]
            $incomingBody.Add([HarmonyLib.CodeInstruction]::new($incomingOpcode, [object]$incomingOperand))
        }
        $incomingOriginalOpcodes = @($incomingBody | ForEach-Object { $_.opcode.Value })
        $incomingOriginalOperands = @($incomingBody | ForEach-Object { ,$_.operand })
        $incomingArgs = [object[]]::new(1); $incomingArgs[0] = $incomingBody
        $incomingResult = $incomingGuard.Invoke($null, $incomingArgs)
        if ($incomingResult.Count -ne $incomingBody.Count) { throw 'Guard rewrote native body.' }
        for ($incomingIndex = 0; $incomingIndex -lt $incomingBody.Count; $incomingIndex++) {
            if (-not [object]::ReferenceEquals($incomingResult[$incomingIndex], $incomingBody[$incomingIndex]) -or
                $incomingResult[$incomingIndex].opcode.Value -ne $incomingOriginalOpcodes[$incomingIndex] -or
                -not [object]::Equals($incomingResult[$incomingIndex].operand, $incomingOriginalOperands[$incomingIndex])) { throw 'Guard modified an original instruction.' }
        }
        $incomingBad = [Collections.Generic.List[HarmonyLib.CodeInstruction]]::new($incomingBody)
        $incomingBad.Add([HarmonyLib.CodeInstruction]::new([Reflection.Emit.OpCodes]::Callvirt, $incomingCallback))
        $incomingArgs[0] = $incomingBad; $incomingRejected = $false
        try { [void]$incomingGuard.Invoke($null, $incomingArgs) } catch { $incomingRejected = $true }
        if (-not $incomingRejected) { throw 'Duplicate native callback was accepted.' }
        $incomingArgs[0] = [Collections.Generic.List[HarmonyLib.CodeInstruction]]::new(); $incomingRejected = $false
        try { [void]$incomingGuard.Invoke($null, $incomingArgs) } catch { $incomingRejected = $true }
        if (-not $incomingRejected) { throw 'Missing native callback was accepted.' }
        Write-Output ('PASS exact private calculator ' + $incomingTarget.Name + ': actual installed call guard, unchanged instruction identities, missing/duplicate rejection.')
    }
    $incomingGetter = $incomingZone.Methods | Where-Object Name -eq 'GetIncomingResidentDemand'
    if (-not $incomingGetter.IsPublic -or $incomingGetter.Parameters.Count -ne 0 -or $incomingGetter.ReturnType.FullName -ne 'System.Int32' -or
        @($incomingGetter.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'CalculateIncomingResidentDemand' }).Count -ne 1) { throw 'Public incoming getter no longer reaches expected calculator.' }
    $incomingPatchType = $incomingCompiled.MainModule.GetType('SC4Evolution.Mod.Economy.IncomingResidentDemandPatches')
    $incomingInstall = $incomingPatchType.Methods | Where-Object Name -eq 'Install'
    if (@($incomingInstall.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'Patch' }).Count -ne 2 -or
        @($incomingInstall.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'UnpatchAll' }).Count -ne 1 -or $incomingInstall.Body.ExceptionHandlers.Count -eq 0) { throw 'Atomic two-target patch/rollback shape changed.' }
    $incomingFinalizer = $incomingPatchType.Methods | Where-Object Name -eq 'FinalizeCall'
    if (@($incomingFinalizer.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'Finish' }).Count -ne 1) { throw 'Finalizer is not using tested context cleanup.' }
    $incomingBridge = $incomingCompiled.MainModule.GetType('SC4Evolution.Mod.Economy.EconomicBridge')
    $incomingStart = $incomingBridge.Methods | Where-Object Name -eq 'Start'
    if ($null -ne $incomingStart) {
        $incomingInstallCall = $incomingStart.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.Name -eq 'IncomingDemandDependency' -and $_.Operand.Name -eq 'Install' }
        $incomingBindCall = $incomingStart.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.Name -eq 'EconomicDemandExtension' -and $_.Operand.Name -eq 'Bind' }
        if ($incomingInstallCall.Offset -ge $incomingBindCall.Offset) { throw 'Context is not installed before extension binding.' }
        $incomingStop = $incomingBridge.Methods | Where-Object Name -eq 'StopLocked'
        $incomingInvalidation = $incomingStop.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'Invalidate' }
        $incomingUnbind = $incomingStop.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'Unbind' }
        $incomingRemove = $incomingStop.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.Name -eq 'IncomingDemandDependency' -and $_.Operand.Name -eq 'Remove' }
        if ($incomingInvalidation.Offset -ge $incomingUnbind.Offset -or $incomingUnbind.Offset -ge $incomingRemove.Offset) { throw 'Stop invalidation/unbind/removal order changed.' }
        Write-Output 'PASS integrated compiled lifecycle: install before Bind, invalidate before Unbind before removal.'
    } else { Write-Output 'Integrated lifecycle check unavailable in isolated adapter assembly; run with full artifact separately.' }
    Write-Output 'PASS public getter chain, atomic patch/rollback and finalizer wiring. No native methods executed or patches installed.'
} finally { $incomingGame.Dispose(); $incomingCompiled.Dispose() }
