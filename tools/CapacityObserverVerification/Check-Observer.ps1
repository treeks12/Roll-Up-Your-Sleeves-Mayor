param([string]$AssemblyPath)
$ErrorActionPreference = 'Stop'
$observerRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if ([string]::IsNullOrEmpty($AssemblyPath)) { $AssemblyPath = Join-Path $observerRoot '.cache/capacity-observer-verification/repo/out/CapacityObserver.exe' }
Add-Type -Path (Join-Path $observerRoot 'tools/bin/.store/ilspycmd/9.1.0.7988/ilspycmd/9.1.0.7988/tools/net8.0/any/Mono.Cecil.dll')
$observerAssembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly([IO.Path]::GetFullPath($AssemblyPath))
$observerPaths = Get-Content (Join-Path $observerRoot 'config/local.paths.json') -Raw | ConvertFrom-Json
$observerNative = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $observerPaths.CitiesSkylines 'Cities_Data/Managed/Assembly-CSharp.dll'))
try {
    $observerType = $observerAssembly.MainModule.GetType('SC4Evolution.Mod.Economy.ResidentialCapacityObserver')
    $observerEntry = $observerType.Methods | Where-Object Name -eq Observe
    $observerNativeHome = $observerNative.MainModule.GetType('ResidentialBuildingAI').Methods | Where-Object Name -eq CalculateHomeCount
    if (-not $observerNativeHome.IsPublic -or $observerNativeHome.ReturnType.FullName -ne 'System.Int32' -or
        ($observerNativeHome.Parameters.ParameterType.FullName -join ',') -ne 'ItemClass/Level,ColossalFramework.Math.Randomizer,System.Int32,System.Int32') { throw 'Native HOME API signature changed.' }
    # C# emits the base virtual slot token; the exact-AI guard in Observe
    # selects the verified ResidentialBuildingAI override at runtime.
    $observerHomeCalls = @($observerEntry.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.Name -in @('PrivateBuildingAI','ResidentialBuildingAI') -and $_.Operand.Name -eq 'CalculateHomeCount' })
    if ($observerHomeCalls.Count -ne 1 -or $observerHomeCalls[0].OpCode.Name -ne 'callvirt' -or $observerNativeHome.IsNewSlot) { throw 'Expected exactly one supported virtual HOME calculation.' }
    $observerBindings = @($observerEntry.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'CoherentPrefabBinding' })
    if ($observerBindings.Count -ne 2 -or $observerBindings[0].Offset -ge $observerHomeCalls[0].Offset -or $observerBindings[1].Offset -le $observerHomeCalls[0].Offset) { throw 'AI/prefab binding guard does not bracket native calculation/observation.' }
    $observerBindingGuard = $observerType.Methods | Where-Object Name -eq CoherentPrefabBinding
    foreach ($observerBackpointer in @('m_info','m_buildingAI')) {
        if (@($observerBindingGuard.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq $observerBackpointer }).Count -ne 1) { throw 'Bidirectional AI/prefab field guard is missing.' }
    }
    $observerSeedCalls = @($observerEntry.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.Name -eq 'Randomizer' -and $_.Operand.Name -eq '.ctor' })
    if ($observerSeedCalls.Count -ne 1 -or $observerSeedCalls[0].Previous.OpCode.Name -ne 'ldarg.0') { throw 'Local Randomizer is not seeded directly from building ID.' }
    foreach ($observerMethod in $observerType.Methods) {
        if (-not $observerMethod.HasBody) { continue }
        foreach ($observerInstruction in $observerMethod.Body.Instructions) {
            $observerOperand = $observerInstruction.Operand
            if ($observerOperand -is [Mono.Cecil.FieldReference] -and $observerOperand.Name -eq 'm_randomizer') { throw 'Observer references global native RNG.' }
            if ($observerInstruction.OpCode.Name -in @('stfld','stsfld') -and $observerOperand -is [Mono.Cecil.FieldReference] -and
                $observerOperand.DeclaringType.Scope.Name -in @('Assembly-CSharp','ColossalManaged','UnityEngine')) { throw 'Observer writes native field.' }
            if ($observerOperand -is [Mono.Cecil.MethodReference] -and $observerOperand.Name -match 'GetHomeBehaviour|GetCitizenWorkBehaviour|CreateBuilding|SetHome|SetWealthLevel|CreateCitizen|EnsureCitizenUnits|CreateUnits') { throw 'Observer calls a mutating/native behavior path.' }
        }
    }
    $observerScan = $observerType.Methods | Where-Object Name -eq InspectChain
    if (@($observerScan.Body.Instructions | Where-Object { $_.OpCode.Name -match '^stelem|^stobj' }).Count -ne 0) { throw 'Chain inspector writes native struct arrays.' }
    $observerThreadReads = @($observerEntry.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq 'm_simulationThread' })
    $observerFirstBufferRead = $observerEntry.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq 'm_buffer' } | Select-Object -First 1
    if ($observerThreadReads.Count -lt 2 -or $observerThreadReads[0].Offset -ge $observerFirstBufferRead.Offset) { throw 'Simulation thread gate is not before native buffer reads.' }
    Write-Output 'PASS exact public HOME API + one building-ID local Randomizer, bidirectional AI/prefab guards bracket observation, no global RNG/native field writes or mutating behavior calls, chain arrays read-only, thread gate precedes buffer access.'
    Write-Output 'Compile-only identity adapter is explicit; these checks do not certify a live city, global Harmony compatibility or off-thread safety of arbitrary third-party patches.'
} finally { $observerAssembly.Dispose(); $observerNative.Dispose() }
