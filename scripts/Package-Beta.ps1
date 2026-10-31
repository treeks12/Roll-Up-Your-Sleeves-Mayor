[CmdletBinding()]
param(
    [ValidateSet('Beta-0.2', 'Beta-0.3')]
    [string]$ReleaseName = 'Beta-0.2'
)

$ErrorActionPreference = 'Stop'

function Get-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [IO.Path]::GetFullPath($Path)
}

function Get-RelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )
    $rootFull = (Get-FullPath $Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $pathFull = Get-FullPath $Path
    if (-not $pathFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the package root: $pathFull"
    }
    return $pathFull.Substring($rootFull.Length).Replace([IO.Path]::DirectorySeparatorChar, '/')
}

function Get-FileEntry {
    param(
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [Parameter(Mandatory = $true)][string]$Path
    )
    $item = Get-Item -LiteralPath $Path -Force
    if (-not $item.PSIsContainer -and $item.LinkType) {
        throw "Package input must not be a link: $Path"
    }
    return [ordered]@{
        Path = Get-RelativePath -Root $PackageRoot -Path $Path
        Length = [int64]$item.Length
        Sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    }
}

function Copy-FileChecked {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )
    $parent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
    if (-not (Test-Path -LiteralPath $Destination -PathType Leaf)) {
        throw "Copy did not produce a file: $Destination"
    }
}

$projectRoot = Get-FullPath (Join-Path $PSScriptRoot '..')
$releasesRoot = Get-FullPath (Join-Path $projectRoot 'releases')
$releaseRoot = Get-FullPath (Join-Path $releasesRoot $ReleaseName)
$alphaRoot = Get-FullPath (Join-Path $releasesRoot 'Alpha-0.1')
$stagingRoot = Get-FullPath (Join-Path $releasesRoot ('.' + $ReleaseName + '.staging-' + [Guid]::NewGuid().ToString('N')))

$isShadowBeta = $ReleaseName -ceq 'Beta-0.3'
$releaseLabel = 'SC4Evolution ' + $ReleaseName
$optInNames = if ($isShadowBeta) { @('Employment', 'Neighborhoods', 'Decisions', 'ShadowTaxation', 'ShadowAccessFreight') } else { @('Employment', 'Neighborhoods', 'Decisions') }
$optInLine = 'Gameplay=' + ($optInNames -join ',')
if (Test-Path -LiteralPath $releaseRoot) {
    throw "Refusing to overwrite an existing release: $releaseRoot"
}
if (-not (Test-Path -LiteralPath $alphaRoot -PathType Container)) {
    throw "The preserved Alpha release is missing: $alphaRoot"
}
$alphaManifestPath = Join-Path $alphaRoot 'manifest.json'
if (-not (Test-Path -LiteralPath $alphaManifestPath -PathType Leaf)) {
    throw "The preserved Alpha manifest is missing: $alphaManifestPath"
}
$alphaManifestHash = (Get-FileHash -LiteralPath $alphaManifestPath -Algorithm SHA256).Hash

$expectedDefaultConfig = "Stage=capacity-admission`nLocalPolicy=SC4WaterRawCapacityV1`n"
$liveConfigPath = Join-Path $projectRoot 'config/economic-bridge.config'
if (-not (Test-Path -LiteralPath $liveConfigPath -PathType Leaf)) {
    throw "The live economic bridge configuration is missing: $liveConfigPath"
}
$liveConfig = [IO.File]::ReadAllText($liveConfigPath)
$liveConfigNormalized = $liveConfig.Replace("`r`n", "`n").Replace("`r", "`n")
if ($liveConfigNormalized.Trim() -cne $expectedDefaultConfig.Trim()) {
    throw 'The live configuration is not the Alpha baseline. Restore the baseline before packaging Beta.'
}

$requiredBuildFiles = @(
    'CitiesHarmony.API.dll',
    'project-root.path',
    'SC4Evolution.Core.dll',
    'SC4Evolution.Core.pdb',
    'SC4Evolution.dll',
    'SC4Evolution.pdb',
    'SC4Evolution.Simulation.dll',
    'SC4Evolution.Simulation.pdb'
)
$buildSources = [ordered]@{
    Installed = (Get-FullPath (Join-Path $projectRoot 'artifacts/mod'))
    Preview = (Get-FullPath (Join-Path $projectRoot 'artifacts/preview'))
}

$gitCommit = (& git -C $projectRoot rev-parse HEAD 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace([string]$gitCommit)) {
    throw 'Could not resolve the source commit for the package manifest.'
}
$gitCommit = ([string]$gitCommit).Trim()

try {
    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
    $packageFiles = New-Object 'System.Collections.Generic.List[object]'
    $buildEntries = [ordered]@{}

    foreach ($variant in $buildSources.Keys) {
        $sourceRoot = [string]$buildSources[$variant]
        if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
            throw "The $variant build directory is missing: $sourceRoot"
        }
        $markerPath = Join-Path $sourceRoot 'project-root.path'
        if ((Get-Content -LiteralPath $markerPath -Raw).Trim() -cne $projectRoot) {
            throw "The $variant build points to a different project directory. Rebuild it here first."
        }
        $sourceNames = @(Get-ChildItem -LiteralPath $sourceRoot -File | ForEach-Object Name)
        $unexpected = @($sourceNames | Where-Object { $_ -notin $requiredBuildFiles })
        if ($unexpected.Count -gt 0) {
            throw "The $variant build has unexpected files: $($unexpected -join ', ')"
        }
        foreach ($name in $requiredBuildFiles) {
            $sourcePath = Join-Path $sourceRoot $name
            if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
                throw "The $variant build is incomplete ($name missing). Run scripts/Build.ps1 -Destination $variant first."
            }
            $destinationPath = Join-Path $stagingRoot ('builds/' + $variant + '/mod/' + $name)
            Copy-FileChecked -Source $sourcePath -Destination $destinationPath
            $entry = Get-FileEntry -PackageRoot $stagingRoot -Path $destinationPath
            $packageFiles.Add($entry)
        }
        $buildEntries[$variant] = [ordered]@{
            Path = ('builds/' + $variant + '/mod')
            Files = @($requiredBuildFiles)
            MainAssemblySha256 = (Get-FileHash -LiteralPath (Join-Path $sourceRoot 'SC4Evolution.dll') -Algorithm SHA256).Hash
        }
    }

    $advisorSource = Get-FullPath (Join-Path $projectRoot 'analysis/generated/sc4-advisor-assets')
    if (-not (Test-Path -LiteralPath $advisorSource -PathType Container)) {
        throw "Advisor animation resources are missing: $advisorSource"
    }
    $advisorFiles = @(Get-ChildItem -LiteralPath $advisorSource -File | Sort-Object Name)
    if ($advisorFiles.Count -ne 952) {
        throw "Expected 952 advisor resource files (950 PNG + 2 CSV); found $($advisorFiles.Count)."
    }
    $advisorPngCount = @($advisorFiles | Where-Object Extension -ieq '.png').Count
    $advisorCsvCount = @($advisorFiles | Where-Object Extension -ieq '.csv').Count
    if ($advisorPngCount -ne 950 -or $advisorCsvCount -ne 2) {
        throw "Advisor resource shape is unexpected: PNG=$advisorPngCount CSV=$advisorCsvCount."
    }
    foreach ($file in $advisorFiles) {
        $destinationPath = Join-Path $stagingRoot ('advisor-assets/' + $file.Name)
        Copy-FileChecked -Source $file.FullName -Destination $destinationPath
        $packageFiles.Add((Get-FileEntry -PackageRoot $stagingRoot -Path $destinationPath))
    }

    $defaultConfigPath = Join-Path $stagingRoot 'config/economic-bridge.config'
    New-Item -ItemType Directory -Path (Split-Path -Parent $defaultConfigPath) -Force | Out-Null
    [IO.File]::WriteAllText($defaultConfigPath, $expectedDefaultConfig, [Text.UTF8Encoding]::new($false))
    $optInConfig = "Stage=capacity-admission`nLocalPolicy=SC4WaterRawCapacityV1`n$optInLine`n"
    $optInConfigPath = Join-Path $stagingRoot 'config/economic-bridge.opt-in-readonly.config'
    [IO.File]::WriteAllText($optInConfigPath, $optInConfig, [Text.UTF8Encoding]::new($false))
    if (($optInNames -contains 'Taxation') -or ($optInNames -contains 'AccessFreight') -or ($optInNames -contains 'CivicFunding')) {
        throw 'The opt-in configuration accidentally enables a restricted sprint.'
    }
    $packageFiles.Add((Get-FileEntry -PackageRoot $stagingRoot -Path $defaultConfigPath))
    $packageFiles.Add((Get-FileEntry -PackageRoot $stagingRoot -Path $optInConfigPath))

    $readme = @'
# __RELEASE__

Este diretório é um checkpoint local reproduzível do projeto em __SOURCE_COMMIT__.

## Conteúdo

- `builds/Installed/mod`: build Installed capturada de `artifacts/mod`.
- `builds/Preview/mod`: build Preview capturada de `artifacts/preview`.
- `advisor-assets`: 28 retratos estáticos e 922 frames ANIM derivados, com os dois manifests CSV.
- `config/economic-bridge.config`: preset padrão Alpha (`capacity-admission` + `SC4WaterRawCapacityV1`).
- `config/economic-bridge.opt-in-readonly.config`: exemplo separado com `__OPTIN__`.

O opt-in não é instalado automaticamente e não contém `Taxation`, `AccessFreight` ou `CivicFunding`.
O pacote não contém saves pessoais. A `releases/Alpha-0.1` existente não é modificada.

Para usar este checkpoint no projeto local, copie explicitamente uma das pastas `builds/*/mod` para o destino escolhido com o jogo fechado e confira `manifest.json` antes de iniciar o CS1. A configuração padrão do projeto não é alterada por este pacote.
'@
    $readme = $readme.Replace('__RELEASE__', $releaseLabel).Replace('__SOURCE_COMMIT__', $gitCommit).Replace('__OPTIN__', $optInLine)
    $readmePath = Join-Path $stagingRoot 'LEIA-ME.md'
    [IO.File]::WriteAllText($readmePath, $readme, [Text.UTF8Encoding]::new($false))
    $packageFiles.Add((Get-FileEntry -PackageRoot $stagingRoot -Path $readmePath))

    $forbiddenExtensions = @('.sc4', '.crp', '.cgs', '.ccs', '.save', '.sav')
    $forbidden = @(Get-ChildItem -LiteralPath $stagingRoot -Recurse -File | Where-Object { $_.Extension.ToLowerInvariant() -in $forbiddenExtensions })
    if ($forbidden.Count -gt 0) {
        throw "The package contains personal-save extensions: $($forbidden.FullName -join ', ')"
    }

    $manifest = @{
        Schema = 1
        Release = $releaseLabel
        CreatedUtc = [DateTime]::UtcNow.ToString('o')
        SourceCommit = $gitCommit
        LocalProjectRequired = $true
        DefaultPreset = 'capacity-admission / SC4WaterRawCapacityV1'
        DefaultConfig = 'config/economic-bridge.config'
        OptInConfig = 'config/economic-bridge.opt-in-readonly.config'
        OptInGameplay = $optInNames
        RestrictedGameplayEnabled = [ordered]@{
            Taxation = $false
            AccessFreight = $false
            CivicFunding = $false
        }
        AlphaRelease = [ordered]@{
            Path = 'releases/Alpha-0.1'
            Preserved = $true
            ManifestSha256 = $alphaManifestHash
        }
        Builds = $buildEntries
        AdvisorAssets = [ordered]@{
            Path = 'advisor-assets'
            PngCount = $advisorPngCount
            StaticPortraitCount = 28
            AnimationFrameCount = 922
            CsvManifestCount = $advisorCsvCount
        }
        PersonalSavesIncluded = $false
        Files = $packageFiles.ToArray()
        Documentation = 'LEIA-ME.md'
    }
    $manifestPath = Join-Path $stagingRoot 'manifest.json'
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))

    $manifestRead = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifestRead.Release -cne $releaseLabel -or
        $manifestRead.SourceCommit -cne $gitCommit -or
        $manifestRead.PersonalSavesIncluded -ne $false -or
        $manifestRead.AdvisorAssets.PngCount -ne 950 -or
        $manifestRead.AdvisorAssets.AnimationFrameCount -ne 922) {
        throw 'The generated Beta manifest failed its structural validation.'
    }
    if ($manifestRead.OptInGameplay -contains 'Taxation' -or $manifestRead.OptInGameplay -contains 'AccessFreight') {
        throw 'The generated Beta opt-in manifest includes a restricted sprint.'
    }

    Move-Item -LiteralPath $stagingRoot -Destination $releaseRoot
    Write-Output "Beta checkpoint created: $releaseRoot"
    Write-Output "Source commit: $gitCommit"
    Write-Output "Advisor assets: $advisorPngCount PNG + $advisorCsvCount CSV (28 static portraits, 922 frames)"
    Write-Output "Alpha manifest preserved: $alphaManifestHash"
}
catch {
    if (Test-Path -LiteralPath $stagingRoot) {
        $stagingFull = Get-FullPath $stagingRoot
        $releasesFull = (Get-FullPath $releasesRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if ($stagingFull.StartsWith($releasesFull, [StringComparison]::OrdinalIgnoreCase) -and $stagingFull -ne (Get-FullPath $releasesRoot)) {
            Remove-Item -LiteralPath $stagingFull -Recurse -Force
        }
    }
    throw
}
