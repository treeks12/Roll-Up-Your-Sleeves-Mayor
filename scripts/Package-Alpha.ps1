$ErrorActionPreference = 'Stop'
$alphaRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$alphaCheckpoint = Join-Path $alphaRoot 'artifacts/checkpoints/0.5.1-populated-capacity-save-verified'
$alphaProof = Get-Content -LiteralPath (Join-Path $alphaCheckpoint 'checkpoint.json') -Raw | ConvertFrom-Json
if ($alphaProof.Checkpoint -cne '0.5.1-populated-capacity-save-verified' -or $alphaProof.Files.Count -ne 8) { throw 'Checkpoint Alpha inválido.' }
$alphaReport = Join-Path $alphaRoot $alphaProof.RuntimeReport
if ((Get-FileHash -LiteralPath $alphaReport -Algorithm SHA256).Hash -cne $alphaProof.RuntimeReportSha256) { throw 'Evidência nativa alterada.' }
foreach ($alphaFile in $alphaProof.Files) {
    if ([IO.Path]::GetFileName([string]$alphaFile.Name) -cne [string]$alphaFile.Name -or
        (Get-FileHash -LiteralPath (Join-Path $alphaCheckpoint $alphaFile.Name) -Algorithm SHA256).Hash -cne $alphaFile.Sha256) { throw 'Arquivo do checkpoint alterado.' }
}
if (($alphaProof.Files | Where-Object Name -eq 'SC4Evolution.dll').Sha256 -cne '4FCE33DD01C4C5A29ED97DBA9806D94989E4386BE048E0347E18653BB6949A80') { throw 'Binário principal incorreto.' }
$alphaResources = @()
foreach ($alphaAdvisor in @('Finance', 'UrbanPlanning', 'EducationHealth', 'Utilities', 'PublicSafety', 'Environment', 'Transport')) {
    foreach ($alphaMood in @('neutral', 'happy', 'attention', 'alarmed')) {
        $alphaResource = 'analysis/generated/sc4-advisor-assets/' + $alphaAdvisor + '-' + $alphaMood + '.png'
        $alphaResources += [ordered]@{Path = $alphaResource; Sha256 = (Get-FileHash -LiteralPath (Join-Path $alphaRoot $alphaResource) -Algorithm SHA256).Hash}
    }
}
foreach ($alphaResource in @('analysis/generated/sc4-demand-profile/demand.sc4e', 'analysis/generated/sc4-residential-profile/water-prerequisites.s4ew')) {
    $alphaResources += [ordered]@{Path = $alphaResource; Sha256 = (Get-FileHash -LiteralPath (Join-Path $alphaRoot $alphaResource) -Algorithm SHA256).Hash}
}
$alphaRelease = Join-Path $alphaRoot 'releases/Alpha-0.1'
if (Test-Path -LiteralPath $alphaRelease) { throw 'O pacote Alpha já existe; ele não será sobrescrito.' }
New-Item -ItemType Directory -Path (Join-Path $alphaRelease 'mod') -Force | Out-Null
foreach ($alphaFile in $alphaProof.Files) { Copy-Item -LiteralPath (Join-Path $alphaCheckpoint $alphaFile.Name) -Destination (Join-Path $alphaRelease ('mod/' + $alphaFile.Name)) }
Copy-Item -LiteralPath (Join-Path $alphaRoot 'docs/ALPHA-0.1.md') -Destination (Join-Path $alphaRelease 'LEIA-ME.md')
$alphaManifest = [ordered]@{
    Schema = 1; Release = 'SC4Evolution Alpha 0.1'; CreatedUtc = [DateTime]::UtcNow.ToString('o')
    SourceCommit = '9386a48'; CompiledVersion = '0.5.1'; LocalProjectRequired = $true
    MainAssemblySha256 = '4FCE33DD01C4C5A29ED97DBA9806D94989E4386BE048E0347E18653BB6949A80'
    Files = @($alphaProof.Files); LocalResources = $alphaResources
    NativeEvidence = [ordered]@{Report = $alphaProof.RuntimeReport; Sha256 = $alphaProof.RuntimeReportSha256; Scope = $alphaProof.Scope; LoadedBytesHashClaimed = $false; ObservedModuleVersionId = $alphaProof.ObservedModuleVersionId}
    ExperimentalPreset = 'capacity-admission / SC4WaterRawCapacityV1'; AlternativePreset = 'shadow'
    CivicFundingIncluded = $false; AdvisorAnimationsIncluded = $false
    Documentation = 'docs/ALPHA-0.1.md'; FutureWork = 'docs/GOAL.md'
}
[IO.File]::WriteAllText((Join-Path $alphaRelease 'manifest.json'), ($alphaManifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
foreach ($alphaShortcut in @(
    @('Instalar Alpha.cmd', 'Install-Alpha.ps1', 'Experimental'),
    @('Ativar gameplay Alpha.cmd', 'Set-AlphaMode.ps1', 'Experimental'),
    @('Usar economia original.cmd', 'Set-AlphaMode.ps1', 'Observation'),
    @('Verificar Alpha.cmd', 'Set-AlphaMode.ps1', 'Status')
)) {
    $alphaBatch = '@echo off' + "`r`n" + 'pwsh.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\..\scripts\' + $alphaShortcut[1] + '" -Mode ' + $alphaShortcut[2] + "`r`n" + 'pause' + "`r`n"
    [IO.File]::WriteAllText((Join-Path $alphaRelease $alphaShortcut[0]), $alphaBatch, [Text.Encoding]::ASCII)
}
Write-Output ('Alpha criada em ' + $alphaRelease + '; oito arquivos do checkpoint, 30 recursos locais conferidos; nenhuma arte do SC4 copiada para o pacote.')
