param([string]$OutputDirectory = 'artifacts/water-policy-check')
$ErrorActionPreference = 'Stop'
# Reuse the actual native-IL guards and previous reconciliation/restoration regressions.
. (Join-Path $PSScriptRoot 'Check-ResidentialClass.ps1') -OutputDirectory $OutputDirectory
$waterInstance = [Reflection.BindingFlags]'NonPublic,Instance'
$waterStatic = [Reflection.BindingFlags]'NonPublic,Static'
$waterProfileStream = [IO.File]::OpenRead((Join-Path $taskRoot 'analysis/generated/sc4-residential-profile/water-prerequisites.s4ew'))
try { $waterProfile = [SC4Evolution.Simulation.ResidentialWaterProfileCodec]::Read($waterProfileStream) } finally { $waterProfileStream.Dispose() }
$waterNativePolicy = [SC4Evolution.Simulation.ResidentialSelectionPolicyId]::NativeSafetyOnlyV1
$waterPolicy = [SC4Evolution.Simulation.ResidentialSelectionPolicyId]::SC4WaterPrerequisiteV1
$waterNone = [SC4Evolution.Simulation.ResidentialSelectionPolicyId]::Unknown
$waterOld = [SC4Evolution.Simulation.ResidentialClassLedger]::new([Guid]::NewGuid(), [uint32]1, [SC4Evolution.Simulation.ResidentialSelectionPolicy]::PolicyHash($waterNativePolicy))
[void]$waterOld.CommitCreated($taskBefore, [SC4Evolution.Simulation.ResidentialClass]::High)
$waterPrepare = $taskRuntimeBridge.GetMethod('PreparePolicyLedger', $waterStatic)
$waterMatches = $taskRuntimeBridge.GetMethod('SelectionPolicyMatches', $waterStatic)
$waterMigrated = $waterPrepare.Invoke($null, @($waterOld, $true, $false, $waterPolicy, $waterProfile))
if ([SC4Evolution.Simulation.ResidentialSelectionPolicy]::Identify($waterMigrated) -ne $waterPolicy -or $waterMigrated.NextGeneration -ne $waterOld.NextGeneration -or $waterMigrated.LedgerId -ne $waterOld.LedgerId) { throw 'Runtime migration lost ledger identity.' }
foreach ($waterCase in @(
    @($waterOld, $false, $false, $waterPolicy, $waterProfile), # Shadow never migrates.
    @($waterOld, $true, $true, $waterPolicy, $waterProfile), # Provider/restoration fallback never migrates.
    @($waterOld, $true, $false, $waterPolicy, $null), # No guessed profile.
    @($waterOld, $true, $false, $waterNativePolicy, $waterProfile),
    @($waterMigrated, $true, $false, $waterNativePolicy, $null), # No implicit downgrade.
    @($waterMigrated, $false, $false, $waterNone, $null), # Shadow v2 needs no profile.
    @($waterMigrated, $true, $false, $waterPolicy, $waterProfile))) {
    if (-not [object]::ReferenceEquals($waterCase[0], $waterPrepare.Invoke($null, $waterCase))) { throw 'Runtime policy boundary mutated a retained ledger.' }
}
if (-not $waterMatches.Invoke($null, @($waterMigrated, $false, $waterPolicy, $waterProfile))) { throw 'Pinned opt-in policy did not match.' }
foreach ($waterCase in @(
    @($waterMigrated, $false, $waterNativePolicy, $null),
    @($waterMigrated, $false, $waterNone, $null),
    @($waterMigrated, $false, $waterPolicy, $null),
    @($waterMigrated, $true, $waterPolicy, $waterProfile),
    @($waterOld, $false, $waterPolicy, $waterProfile))) {
    if ($waterMatches.Invoke($null, $waterCase)) { throw 'Runtime policy mismatch armed selection.' }
}
Write-Output 'PASS compiled runtime policy helpers: explicit pinned opt-in only; Shadow, missing profile, restoration fallback and reverse policy preserve the original ledger without arming effects.'

$waterCtorArgs = [object[]]::new(4); $waterCtorArgs[2] = $false
$waterShadow = $taskCtor.Invoke($waterCtorArgs)
$waterConfigure = $taskRuntimeBridge.GetMethod('ConfigureSelectionPolicy', $waterInstance)
[void]$waterConfigure.Invoke($waterShadow, @($waterNone, $null))
# Inject a validated bound ledger solely to test serialization/publication off the game thread.
# Live fingerprint binding and buffer restoration still require native QA.
$waterReplaceLedger = $taskRuntimeBridge.GetMethod('ReplaceLedgerLocked', $waterInstance)
[void]$waterReplaceLedger.Invoke($waterShadow, @($waterMigrated))
$taskRuntimeBridge.GetField('_bound', $waterInstance).SetValue($waterShadow, $true)
[void]$taskRuntimeBridge.GetMethod('PublishLocked', $waterInstance).Invoke($waterShadow, @())
$waterStatus = $taskRuntimeBridge.GetProperty('Published', $waterInstance).GetValue($waterShadow)
if ($waterStatus.ConfiguredPolicy -ne 'None' -or $waterStatus.RetainedPolicy -ne 'SC4WaterPrerequisiteV1' -or $waterStatus.EffectivePolicy -ne 'None' -or $waterStatus.Policy -ne 'None') { throw 'Shadow published misleading policies.' }
if ($waterStatus.ImportedCount -ne 0 -or $waterStatus.SelectedCount -ne 1) { throw 'Bound ledger origin counts were not cached.' }
$waterSaved = $taskSaveMethod.Invoke($waterShadow, @())
$waterSavedStream = [IO.MemoryStream]::new([byte[]]$waterSaved, $false)
try { $waterReloaded = [SC4Evolution.Simulation.ResidentialClassLedgerCodec]::Read($waterSavedStream) } finally { $waterSavedStream.Dispose() }
if ($waterReloaded.LedgerId -ne $waterMigrated.LedgerId -or $waterReloaded.GetRecords()[0].OccupantClass -ne [SC4Evolution.Simulation.ResidentialClass]::High -or [SC4Evolution.Simulation.ResidentialSelectionPolicy]::Identify($waterReloaded) -ne $waterPolicy) { throw 'Shadow without profile changed retained water ledger.' }
$waterMirrorRecord = $waterReloaded.GetRecords()[0]
$taskMirrorArgs[0] = $false; $taskMirrorArgs[1] = [Enum]::ToObject($taskWealthType, 2); $taskMirrorArgs[2] = $waterMirrorRecord.OccupantClass
if (-not $taskMirrorMethod.Invoke($null, $taskMirrorArgs) -or [int]$taskMirrorArgs[4] -ne 0) { throw 'Water policy retained High cannot restore native Low.' }
Write-Output 'PASS compiled bound-ledger serialization/publication retains water v2 in Shadow without a profile; the same known-mirror restoration helper restores High to native Low.'

$waterLoadArgs = [object[]]::new(1); $waterLoadArgs[0] = [string](Join-Path $taskRoot 'artifacts/water-policy-check/no-profile-fixture')
$waterInvalidObserver = $taskMod.GetType('SC4Evolution.Mod.Economy.ResidentialWaterObserver').GetMethod('Load', $waterStatic).Invoke($null, $waterLoadArgs)
$waterCtorArgs[1] = [byte[]]$waterSaved; $waterCtorArgs[2] = $true
$waterInvalidAdapter = $taskCtor.Invoke($waterCtorArgs)
[void]$waterConfigure.Invoke($waterInvalidAdapter, @($waterPolicy, $waterInvalidObserver))
if ($taskRuntimeBridge.GetField('_bound', $waterInstance).GetValue($waterInvalidAdapter) -or
    $null -eq $taskRuntimeBridge.GetField('_pending', $waterInstance).GetValue($waterInvalidAdapter) -or
    $null -ne $taskRuntimeBridge.GetField('_loadFailure', $waterInstance).GetValue($waterInvalidAdapter) -or
    -not $taskRuntimeBridge.GetField('_suppressSave', $waterInstance).GetValue($waterInvalidAdapter) -or
    $null -ne $taskSaveMethod.Invoke($waterInvalidAdapter, @())) { throw 'Missing profile rejected valid payload or allowed overwriting its key.' }
Write-Output 'PASS missing profile preserves pending valid data for restoration, suppresses saving, and never fabricates a decoder error.'

$waterCompiled = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $taskOutput 'SC4Evolution.dll'))
try {
    $waterBridgeType = $waterCompiled.MainModule.GetType('SC4Evolution.Mod.Economy.ResidentialClassBridge')
    foreach ($waterMethod in @($waterBridgeType.Methods | Where-Object Name -in @('PublishLocked', 'ChooseAt'))) {
        if (@($waterMethod.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -in @('GetRecords', 'RecountOriginsLocked', 'ReplaceLedgerLocked') }).Count -ne 0) { throw ($waterMethod.Name + ' enumerates/recounts the ledger on the decision path.') }
    }
    $waterPublisher = $waterBridgeType.Methods | Where-Object Name -eq 'PublishLocked'
    foreach ($waterCachedField in @('_importedCount', '_selectedCount')) {
        if (@($waterPublisher.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq $waterCachedField }).Count -ne 1) { throw 'Publication does not use cached origin counts.' }
    }
    $waterPatchType = $waterCompiled.MainModule.GetType('SC4Evolution.Mod.Economy.ResidentialClassPatches')
    $waterGate = $waterCompiled.MainModule.GetType('SC4Evolution.Mod.Economy.NewLotGrowthGate').Methods | Where-Object Name -eq 'CreateEligibleBuilding'
    $waterSelect = $waterPatchType.Methods | Where-Object Name -eq 'SelectRedevelopment'
    foreach ($waterMethod in @($waterGate, $waterSelect)) {
        $waterChoices = @($waterMethod.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'ChooseAt' })
        if ($waterChoices.Count -ne 1) { throw ($waterMethod.Name + ' lacks one position-aware choice.') }
        if (@($waterMethod.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'get_Kind' }).Count -lt 2) { throw 'NativeFallback/ControlledReject/Selected are not separately checked.' }
    }
    if (@($waterSelect.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.DeclaringType.Name -eq 'Building' -and $_.Operand.Name -eq 'm_position' }).Count -ne 1) { throw 'Redevelopment does not sample the original candidate position once.' }
    foreach ($waterMethod in @(
        ($waterPatchType.Methods | Where-Object Name -eq 'CommitRedevelopment'),
        ($waterBridgeType.Methods | Where-Object Name -eq 'Commit'),
        ($waterBridgeType.Methods | Where-Object Name -eq 'AfterUpgrade'))) {
        if (@($waterMethod.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -in @('ChooseAt', 'Observe', 'CheckWater') }).Count -ne 0) { throw ($waterMethod.Name + ' contains a late local-policy decision.') }
    }
    $waterObserverType = $waterCompiled.MainModule.GetType('SC4Evolution.Mod.Economy.ResidentialWaterObserver')
    $waterObserve = $waterObserverType.Methods | Where-Object Name -eq 'Observe'
    $waterChecks = @($waterObserve.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'CheckWater' })
    $waterDomains = @($waterObserve.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'IsSupportedNativeQueryPosition' })
    if ($waterChecks.Count -ne 1 -or $waterDomains.Count -ne 1 -or $waterDomains[0].Offset -ge $waterChecks[0].Offset -or $waterObserve.Body.ExceptionHandlers.Count -eq 0) { throw 'Read-only observation guard/query shape changed.' }
    foreach ($waterMethod in @($waterObserverType.Methods) + @($waterBridgeType.Methods | Where-Object Name -eq 'ChooseAt')) {
        if (-not $waterMethod.HasBody) { continue }
        if (@($waterMethod.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and ($_.Operand.DeclaringType.Name -eq 'Randomizer' -or $_.Operand.Name -like '*FetchWater*') }).Count -ne 0) { throw 'Local policy consumes RNG/water.' }
    }
    Write-Output 'PASS compiled integration: one candidate-position choice per native site, tri-state branches, one guarded CheckWater read, no late commit/upgrade query or RNG/water fetch.'
} finally { $waterCompiled.Dispose() }

# Exercise the same mutation helpers used by live binding/commit/release without
# invoking native identity checks or buffers. Counts must follow actual changes,
# including failure after an old record has already been removed.
$waterCountAdapter = $taskCtor.Invoke($waterCtorArgs)
$waterPublishMethod = $taskRuntimeBridge.GetMethod('PublishLocked', $waterInstance)
$waterCommitRecord = $taskRuntimeBridge.GetMethod('CommitRecordLocked', $waterInstance)
$waterRemoveRecord = $taskRuntimeBridge.GetMethod('RemoveRecordLocked', $waterInstance)
function Assert-OriginCounts($waterAdapter, [int]$waterImported, [int]$waterSelected) {
    [void]$waterPublishMethod.Invoke($waterAdapter, @())
    $waterActual = $taskRuntimeBridge.GetProperty('Published', $waterInstance).GetValue($waterAdapter)
    if ($waterActual.ImportedCount -ne $waterImported -or $waterActual.SelectedCount -ne $waterSelected) { throw ('Wrong cached origin counts: ' + $waterActual.ImportedCount + '/' + $waterActual.SelectedCount + ' expected ' + $waterImported + '/' + $waterSelected) }
}
$waterMixed = [SC4Evolution.Simulation.ResidentialClassLedger]::new([Guid]::NewGuid(), [uint32]1, [SC4Evolution.Simulation.ResidentialSelectionPolicy]::PolicyHash($waterNativePolicy))
[void]$waterMixed.ImportExisting($taskBefore, [SC4Evolution.Simulation.ResidentialClass]::Low)
$waterSecond = [SC4Evolution.Simulation.ResidentialLotIdentity]::new([ushort]2, [uint32]0, 0, 0, 'Second', 1, 1, [byte]2, [byte]3)
[void]$waterMixed.CommitCreated($waterSecond, [SC4Evolution.Simulation.ResidentialClass]::High)
[void]$waterReplaceLedger.Invoke($waterCountAdapter, @($waterMixed)); Assert-OriginCounts $waterCountAdapter 1 1
[void]$waterCommitRecord.Invoke($waterCountAdapter, @($taskBefore, [SC4Evolution.Simulation.ResidentialClass]::High)); Assert-OriginCounts $waterCountAdapter 0 2
# Successful upgrade refresh preserves origin counts.
if (-not $waterMixed.RefreshUpgradeIdentity($taskBefore, $taskAfter)) { throw 'Count fixture upgrade failed.' }
Assert-OriginCounts $waterCountAdapter 0 2
[void]$waterRemoveRecord.Invoke($waterCountAdapter, @([ushort]1)); Assert-OriginCounts $waterCountAdapter 0 1
[void]$waterRemoveRecord.Invoke($waterCountAdapter, @([ushort]1)); Assert-OriginCounts $waterCountAdapter 0 1
$waterMigratedMixed = [SC4Evolution.Simulation.ResidentialSelectionPolicy]::MigrateKnownNativeSafetyV1ToWaterV1($waterMixed, $waterProfile)
[void]$waterReplaceLedger.Invoke($waterCountAdapter, @($waterMigratedMixed)); Assert-OriginCounts $waterCountAdapter 0 1
$waterCloneCtor = [SC4Evolution.Simulation.ResidentialClassLedger].GetConstructor($waterInstance, $null,
    [type[]]@([Guid], [uint32], [byte[]], [uint64], [SC4Evolution.Simulation.ResidentialLotRecord[]]), $null)
$waterExhausted = $waterCloneCtor.Invoke(@($waterMigratedMixed.LedgerId, [uint32]2, $waterMigratedMixed.PolicyHash, [uint64]::MaxValue, $waterMigratedMixed.GetRecords()))
[void]$waterReplaceLedger.Invoke($waterCountAdapter, @($waterExhausted)); Assert-OriginCounts $waterCountAdapter 0 1
$waterExpectedFailure = $false
try { [void]$waterCommitRecord.Invoke($waterCountAdapter, @($waterSecond, [SC4Evolution.Simulation.ResidentialClass]::Low)) }
catch { if ($_.Exception.InnerException -is [InvalidOperationException]) { $waterExpectedFailure = $true } else { throw } }
if (-not $waterExpectedFailure) { throw 'Exhausted generation unexpectedly committed.' }
Assert-OriginCounts $waterCountAdapter 0 0
[void]$waterReplaceLedger.Invoke($waterCountAdapter, @($null)); Assert-OriginCounts $waterCountAdapter 0 0
Write-Output 'PASS cached counts follow bind/import, replacement commit, upgrade identity, release/no-op release, migration, clearing and generation-exhaustion removal.'

$waterEmpty = [SC4Evolution.Simulation.ResidentialClassLedger]::new([Guid]::NewGuid(), [uint32]1, [SC4Evolution.Simulation.ResidentialSelectionPolicy]::PolicyHash($waterNativePolicy))
$waterFull = [SC4Evolution.Simulation.ResidentialClassLedger]::new([Guid]::NewGuid(), [uint32]1, [SC4Evolution.Simulation.ResidentialSelectionPolicy]::PolicyHash($waterNativePolicy))
for ($waterId = 1; $waterId -le 65535; $waterId++) {
    $waterIdentity = [SC4Evolution.Simulation.ResidentialLotIdentity]::new([ushort]$waterId, [uint32]0, 0, 0, 'CountCostFixture', 1, 1, [byte]2, [byte]3)
    if (($waterId % 2) -eq 0) { [void]$waterFull.ImportExisting($waterIdentity, [SC4Evolution.Simulation.ResidentialClass]::Low) }
    else { [void]$waterFull.CommitCreated($waterIdentity, [SC4Evolution.Simulation.ResidentialClass]::High) }
}
$waterPublishAction = $waterPublishMethod.CreateDelegate([Action], $waterCountAdapter)
function Measure-Publications($waterLedger) {
    [void]$waterReplaceLedger.Invoke($waterCountAdapter, @($waterLedger))
    for ($waterWarmup = 0; $waterWarmup -lt 100; $waterWarmup++) { $waterPublishAction.Invoke() }
    $waterClock = [Diagnostics.Stopwatch]::new()
    $waterBytesBefore = [GC]::GetAllocatedBytesForCurrentThread(); $waterClock.Start()
    for ($waterPublication = 0; $waterPublication -lt 1000; $waterPublication++) { $waterPublishAction.Invoke() }
    $waterClock.Stop(); $waterAllocated = [GC]::GetAllocatedBytesForCurrentThread() - $waterBytesBefore
    return [pscustomobject]@{ Bytes = $waterAllocated; Milliseconds = $waterClock.Elapsed.TotalMilliseconds }
}
$waterEmptyCost = Measure-Publications $waterEmpty
$waterFullCost = Measure-Publications $waterFull
Assert-OriginCounts $waterCountAdapter 32767 32768
if ($waterFullCost.Bytes -gt $waterEmptyCost.Bytes + 16384) { throw 'Publication allocation grows with ledger size.' }
Write-Output ('PASS O(1) compiled publication: 1000 calls empty=' + $waterEmptyCost.Bytes + 'B/' + [Math]::Round($waterEmptyCost.Milliseconds, 2) + 'ms, 65535 records=' + $waterFullCost.Bytes + 'B/' + [Math]::Round($waterFullCost.Milliseconds, 2) + 'ms; IL confirms no ledger enumeration on publication/choice paths.')
Write-Output 'No native simulation calls, patch installation, active outputs or configuration changes were performed.'
