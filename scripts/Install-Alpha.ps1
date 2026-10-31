[CmdletBinding()]
param([ValidateSet('Experimental', 'Observation')][string]$Mode = 'Experimental')

$ErrorActionPreference = 'Stop'
$alphaRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (Get-Process -Name Cities -ErrorAction SilentlyContinue) { throw 'Feche o CS1 antes de instalar a Alpha.' }
$alphaRelease = Join-Path $alphaRoot 'releases/Alpha-0.1'
$alphaManifest = Get-Content -LiteralPath (Join-Path $alphaRelease 'manifest.json') -Raw | ConvertFrom-Json
if ($alphaManifest.Release -cne 'SC4Evolution Alpha 0.1' -or $alphaManifest.SourceCommit -cne '9386a48' -or
    $alphaManifest.MainAssemblySha256 -cne '4FCE33DD01C4C5A29ED97DBA9806D94989E4386BE048E0347E18653BB6949A80' -or $alphaManifest.Files.Count -ne 8) {
    throw 'Manifesto da Alpha inválido.'
}
foreach ($alphaFile in $alphaManifest.Files) {
    if ([IO.Path]::GetFileName([string]$alphaFile.Name) -cne [string]$alphaFile.Name) { throw 'Nome inválido no manifesto.' }
    $alphaPackaged = Join-Path $alphaRelease ('mod/' + $alphaFile.Name)
    if ((Get-FileHash -LiteralPath $alphaPackaged -Algorithm SHA256).Hash -cne $alphaFile.Sha256) { throw ('Pacote alterado: ' + $alphaFile.Name) }
}
$alphaAssets = @($alphaManifest.LocalResources)
foreach ($alphaAsset in $alphaAssets) {
    $alphaAssetPath = [IO.Path]::GetFullPath((Join-Path $alphaRoot $alphaAsset.Path))
    if (-not $alphaAssetPath.StartsWith($alphaRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Get-FileHash -LiteralPath $alphaAssetPath -Algorithm SHA256).Hash -cne $alphaAsset.Sha256) {
        throw ('Recurso local necessário indisponível: ' + $alphaAsset.Path)
    }
}
# The installed target is this project's named build directory. Game files and saves are untouched.
$alphaDestination = Join-Path $alphaRoot 'artifacts/mod'
New-Item -ItemType Directory -Path $alphaDestination -Force | Out-Null
foreach ($alphaFile in $alphaManifest.Files) {
    $alphaTarget = Join-Path $alphaDestination $alphaFile.Name
    if (-not (Test-Path -LiteralPath $alphaTarget) -or
        (Get-FileHash -LiteralPath $alphaTarget -Algorithm SHA256).Hash -cne $alphaFile.Sha256) {
        Copy-Item -LiteralPath (Join-Path $alphaRelease ('mod/' + $alphaFile.Name)) -Destination $alphaTarget
    }
}
& (Join-Path $PSScriptRoot 'Install-LocalMod.ps1')
& (Join-Path $PSScriptRoot 'Set-AlphaMode.ps1') -Mode $Mode
Write-Output 'Alpha instalada. Na Steam, abra CS1 e habilite Harmony e SC4Evolution no Gerenciador de Conteúdo > Mods.'
