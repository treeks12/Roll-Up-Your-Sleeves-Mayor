[CmdletBinding()]
param([ValidateSet('Experimental', 'Observation', 'Status')][string]$Mode = 'Status')

$ErrorActionPreference = 'Stop'
$alphaRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$alphaRelease = Join-Path $alphaRoot 'releases/Alpha-0.1'
$alphaManifest = Get-Content -LiteralPath (Join-Path $alphaRelease 'manifest.json') -Raw | ConvertFrom-Json
if ($alphaManifest.Release -cne 'SC4Evolution Alpha 0.1' -or $alphaManifest.SourceCommit -cne '9386a48' -or
    $alphaManifest.MainAssemblySha256 -cne '4FCE33DD01C4C5A29ED97DBA9806D94989E4386BE048E0347E18653BB6949A80') {
    throw 'Manifesto da Alpha diferente da entrega validada.'
}
foreach ($alphaFile in $alphaManifest.Files) {
    if ([IO.Path]::GetFileName([string]$alphaFile.Name) -cne [string]$alphaFile.Name) { throw 'Nome inválido no manifesto.' }
    $alphaInstalled = Join-Path $alphaRoot ('artifacts/mod/' + $alphaFile.Name)
    if (-not (Test-Path -LiteralPath $alphaInstalled -PathType Leaf) -or
        (Get-FileHash -LiteralPath $alphaInstalled -Algorithm SHA256).Hash -cne $alphaFile.Sha256) {
        throw ('A instalação difere da Alpha: ' + $alphaFile.Name + '. Use Instalar Alpha.cmd com o jogo fechado.')
    }
}
$alphaConfig = Join-Path $alphaRoot 'config/economic-bridge.config'
$alphaExperimental = "Stage=capacity-admission`nLocalPolicy=SC4WaterRawCapacityV1`n"
$alphaObservation = "Stage=shadow`n"
$alphaExisting = if (Test-Path -LiteralPath $alphaConfig) { [IO.File]::ReadAllText($alphaConfig) } else { $null }
$alphaCurrentMode = if ($null -eq $alphaExisting -or $alphaExisting -ceq $alphaObservation) { 'Observation' }
    elseif ($alphaExisting -ceq $alphaExperimental) { 'Experimental' } else { 'Custom' }
if ($Mode -eq 'Status') {
    Write-Output ('SC4Evolution Alpha 0.1: ' + $alphaCurrentMode)
    Write-Output 'Binários conferidos com a versão validada. Civic/animações não fazem parte desta Alpha.'
    return
}
if (Get-Process -Name Cities -ErrorAction SilentlyContinue) { throw 'Feche o CS1 antes de mudar o modo da Alpha.' }
if ($alphaCurrentMode -eq 'Custom') { throw 'Existe uma configuração personalizada; ela foi preservada.' }
if (Test-Path -LiteralPath (Join-Path $alphaRoot 'config/runtime-test.request')) { throw 'Existe uma solicitação de teste pendente; instalação de jogador recusada.' }
if ($Mode -eq 'Experimental') {
    # Reuse the user's existing official Workshop dependency; never create a duplicate.
    $alphaPaths = Get-Content -LiteralPath (Join-Path $alphaRoot 'config/local.paths.json') -Raw | ConvertFrom-Json
    $alphaSteamapps = [IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($alphaPaths.CitiesSkylines)))
    $alphaHarmony = Join-Path $alphaSteamapps 'workshop/content/255710/2040656402'
    $alphaHarmonyManifestPath = Join-Path $alphaRoot 'tools/CitiesHarmony/manifest.json'
    if ((Get-FileHash -LiteralPath $alphaHarmonyManifestPath -Algorithm SHA256).Hash -cne 'D520A5B32AB16E6675CFDF9D5067D21328AB39EEC71BAB845F5DC3984F928F7B') {
        throw 'Manifesto da dependência Harmony alterado.'
    }
    $alphaHarmonyManifest = Get-Content -LiteralPath $alphaHarmonyManifestPath -Raw | ConvertFrom-Json
    foreach ($alphaProviderFile in $alphaHarmonyManifest.providerFiles) {
        $alphaProviderPath = Join-Path $alphaHarmony $alphaProviderFile.path
        if (-not (Test-Path -LiteralPath $alphaProviderPath -PathType Leaf) -or
            (Get-FileHash -LiteralPath $alphaProviderPath -Algorithm SHA256).Hash -ine $alphaProviderFile.sha256) {
            throw 'A dependência Harmony do Workshop precisa corresponder à versão validada. O modo atual foi preservado.'
        }
    }
}
$alphaDesired = if ($Mode -eq 'Experimental') { $alphaExperimental } else { $alphaObservation }
$alphaTemporary = Join-Path (Split-Path -Parent $alphaConfig) ('alpha-mode-' + [Guid]::NewGuid().ToString('N') + '.tmp')
$alphaBytes = [Text.Encoding]::ASCII.GetBytes($alphaDesired)
$alphaStream = [IO.File]::Open($alphaTemporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $alphaStream.Write($alphaBytes, 0, $alphaBytes.Length) } finally { $alphaStream.Dispose() }
if (Test-Path -LiteralPath $alphaConfig) { [IO.File]::Replace($alphaTemporary, $alphaConfig, [NullString]::Value) }
else { [IO.File]::Move($alphaTemporary, $alphaConfig) }
Write-Output ('Alpha 0.1 configurada: ' + $Mode + '. A mudança vale ao carregar a próxima cidade.')
