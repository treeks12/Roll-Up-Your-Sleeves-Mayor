[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Enter-Project.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = [IO.Path]::GetFullPath($Sc4EvolutionRoot)
$rootItem = Get-Item -LiteralPath $root -Force
if (-not $rootItem.PSIsContainer -or ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'The project root must be a real directory.'
}
$rootPrefix = $root.TrimEnd([char[]]@('\', '/')) + [IO.Path]::DirectorySeparatorChar
$script:HarmonyPrepareScratch = $null

$expectedManifest = [ordered]@{
    schema = 1
    providerRelease = '2.2.2-0'
    providerUrl = 'https://github.com/boformer/CitiesHarmony/releases/download/v2.2.2-0/CitiesHarmony.zip'
    providerArchiveSha256 = 'c1b57e4a4fe9afb2fc5e124e9b710a6e3595afdad22a101a019334b3a5a15548'
    apiPackageVersion = '2.2.0'
    apiUrl = 'https://api.nuget.org/v3-flatcontainer/citiesharmony.api/2.2.0/citiesharmony.api.2.2.0.nupkg'
    apiArchiveSha256 = '883fa5d1b6ffc9a50276a9b5d32b4adb5dabf36ebc0a40380a209855be425dbb'
    apiDllSha256 = 'dd25afca06ede82c433c9a5fefbf30426bb713d646ab6a452bbb65aa11a0fd3b'
    providerFiles = @(
        [ordered]@{ path = '0Harmony_2.0.0.8_impostor.dll'; size = 909824; sha256 = '1d62da74160ee9f4ae54fbc75ff8aa9b6ba150eb172c7b9d7d83375a8de574ac' }
        [ordered]@{ path = '0Harmony_2.0.0.9_impostor.dll'; size = 909824; sha256 = '4ebd280780d682e73188ca4d9260a08e891e32bd2342f2aab329c160b5e6ee0f' }
        [ordered]@{ path = '0Harmony_2.0.1.0_impostor.dll'; size = 909824; sha256 = '0f0848dfa84eb51fbcebaf8523b67d7f317a3ed5037267944cb7aa84a75547c1' }
        [ordered]@{ path = 'CitiesHarmony.dll'; size = 28672; sha256 = '6c9b1b24d024dac943a0b71f415516bff5b15dcf872bb218cd6de669ff894fe1' }
        [ordered]@{ path = 'CitiesHarmony.Harmony.dll'; size = 910336; sha256 = '4d46c0d319136ba3bb44fa52e48ecd0c65acb32ad788db146fd97d0c5eae1fed' }
        [ordered]@{ path = 'CitiesHarmony.Harmony.xml'; size = 233543; sha256 = '50d2c69d4432bc028c13ced6393086ba10ddcb9c4dae060aaceb3e998182a138' }
    )
}

$dependencyRoot = Join-Path $root 'tools/CitiesHarmony'
$manifestPath = Join-Path $dependencyRoot 'manifest.json'
$downloadsRoot = Join-Path $dependencyRoot 'downloads'
$providerArchivePath = Join-Path $downloadsRoot 'CitiesHarmony-2.2.2-0.zip'
$apiArchivePath = Join-Path $downloadsRoot 'CitiesHarmony.API.2.2.0.nupkg'
$providerRoot = Join-Path $dependencyRoot 'provider/CitiesHarmony'
$apiDllPath = Join-Path $dependencyRoot 'api/2.2.0/lib/net35/CitiesHarmony.API.dll'
$licensePath = Join-Path $dependencyRoot 'licenses/CitiesHarmony-LICENSE.txt'
$licenseUrl = 'https://raw.githubusercontent.com/boformer/CitiesHarmony/v2.2.2-0/LICENSE'
$licenseSha256 = '93d48eb29e43a507418f2d7931d44593e867f0b1af59b08676bc71c29f30bac0'

function Get-ContainedPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.Equals($root, [StringComparison]::OrdinalIgnoreCase) -and
        -not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "A preparation path escaped the project root: $fullPath"
    }
    return $fullPath
}

function Ensure-ProjectDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = Get-ContainedPath -Path $Path
    if ($fullPath.Equals($root, [StringComparison]::OrdinalIgnoreCase)) { return $fullPath }
    $relativePath = $fullPath.Substring($rootPrefix.Length)
    $segments = @($relativePath -split '[\\/]')
    $currentPath = $root
    foreach ($segment in $segments) {
        if ([string]::IsNullOrWhiteSpace($segment) -or $segment -eq '.' -or $segment -eq '..') {
            throw "Invalid project directory segment in: $fullPath"
        }
        $currentPath = Join-Path $currentPath $segment
        if (Test-Path -LiteralPath $currentPath) {
            $item = Get-Item -LiteralPath $currentPath -Force
            if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Expected a real project directory, found a file or link: $currentPath"
            }
        }
        else {
            New-Item -ItemType Directory -Path $currentPath | Out-Null
        }
    }
    return $fullPath
}

function Assert-FileHash {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedHash
    )

    $fullPath = Get-ContainedPath -Path $Path
    if (-not (Test-Path -LiteralPath $fullPath)) { return $false }
    $item = Get-Item -LiteralPath $fullPath -Force
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Expected a regular file, found a directory or link: $fullPath"
    }
    $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
    if ($actualHash -ine $ExpectedHash) {
        throw "SHA-256 mismatch; refusing to overwrite or reuse $fullPath. Expected $ExpectedHash, got $($actualHash.ToLowerInvariant())."
    }
    return $true
}

function Get-RunScratchDirectory {
    if ([string]::IsNullOrWhiteSpace($script:HarmonyPrepareScratch)) {
        $basePath = Ensure-ProjectDirectory -Path (Join-Path $root '.cache/harmony-prepare')
        $scratchPath = Join-Path $basePath ([Guid]::NewGuid().ToString('N'))
        if (Test-Path -LiteralPath $scratchPath) { throw "Scratch path unexpectedly exists: $scratchPath" }
        New-Item -ItemType Directory -Path $scratchPath | Out-Null
        $script:HarmonyPrepareScratch = $scratchPath
    }
    return $script:HarmonyPrepareScratch
}

function Ensure-VerifiedDownload {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$ExpectedHash
    )

    $destinationPath = Get-ContainedPath -Path $Destination
    if (Assert-FileHash -Path $destinationPath -ExpectedHash $ExpectedHash) { return $destinationPath }

    $destinationParent = Ensure-ProjectDirectory -Path (Split-Path -Parent $destinationPath)
    $scratchPath = Get-RunScratchDirectory
    $partialPath = Join-Path $scratchPath ([Guid]::NewGuid().ToString('N') + '.part')
    if (Test-Path -LiteralPath $partialPath) { throw "Download scratch path unexpectedly exists: $partialPath" }

    Invoke-WebRequest -Uri $Uri -OutFile $partialPath -UseBasicParsing
    if (-not (Assert-FileHash -Path $partialPath -ExpectedHash $ExpectedHash)) {
        throw "Downloaded artifact was not created: $Uri"
    }
    if (Test-Path -LiteralPath $destinationPath) {
        throw "Destination appeared during download; refusing to overwrite it: $destinationPath"
    }
    [IO.File]::Move($partialPath, $destinationPath)
    return $destinationPath
}

function Assert-ManifestMatches {
    param([Parameter(Mandatory = $true)]$ActualManifest)

    $expectedNames = @($expectedManifest.Keys)
    $actualNames = @($ActualManifest.PSObject.Properties | ForEach-Object { [string]$_.Name })
    if ($actualNames.Count -ne $expectedNames.Count) { throw 'The Harmony manifest has unexpected or missing fields.' }
    foreach ($name in $expectedNames) {
        if ($actualNames -cnotcontains [string]$name) { throw "The Harmony manifest is missing or renames field '$name'." }
        if ($name -ne 'providerFiles' -and [string]$ActualManifest.$name -cne [string]$expectedManifest[$name]) {
            throw "The Harmony manifest field '$name' differs from the pinned preparation data."
        }
    }

    $actualFiles = @($ActualManifest.providerFiles)
    $expectedFiles = @($expectedManifest.providerFiles)
    if ($actualFiles.Count -ne $expectedFiles.Count) { throw 'The Harmony manifest provider file list differs from the pinned contents.' }
    foreach ($expectedFile in $expectedFiles) {
        $matches = @($actualFiles | Where-Object { [string]$_.path -ceq [string]$expectedFile.path })
        if ($matches.Count -ne 1) { throw "The Harmony manifest file list is missing or duplicates '$($expectedFile.path)'." }
        $actualFile = $matches[0]
        $actualFileNames = @($actualFile.PSObject.Properties | ForEach-Object { [string]$_.Name })
        if ($actualFileNames.Count -ne 3 -or
            $actualFileNames -cnotcontains 'path' -or
            $actualFileNames -cnotcontains 'size' -or
            $actualFileNames -cnotcontains 'sha256') {
            throw "The Harmony manifest record for '$($expectedFile.path)' has unexpected fields."
        }
        if ([string]$actualFile.size -cne [string]$expectedFile.size -or
            [string]$actualFile.sha256 -cne [string]$expectedFile.sha256) {
            throw "The Harmony manifest record for '$($expectedFile.path)' differs from the pinned value."
        }
    }
}

function Get-SafeArchiveEntryMap {
    param(
        [Parameter(Mandatory = $true)][System.IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory = $true)][string]$ArchiveName
    )

    $entryMap = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Archive.Entries) {
        $entryName = [string]$entry.FullName
        if ([string]::IsNullOrWhiteSpace($entryName) -or $entryName.Contains('\') -or
            $entryName.StartsWith('/') -or $entryName -match '^[A-Za-z]:' -or
            $entryName.EndsWith('/')) {
            throw "Unsafe or unsupported archive path in $($ArchiveName): '$entryName'"
        }
        foreach ($segment in $entryName.Split('/')) {
            if ([string]::IsNullOrWhiteSpace($segment) -or $segment -eq '.' -or $segment -eq '..') {
                throw "Unsafe archive path in $($ArchiveName): '$entryName'"
            }
        }
        if ($entryMap.ContainsKey($entryName)) { throw "Duplicate archive path in $($ArchiveName): '$entryName'" }
        $entryMap.Add($entryName, $entry)
    }
    return ,$entryMap
}

function Assert-ProviderArchiveLayout {
    param(
        [Parameter(Mandatory = $true)][System.IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory = $true)][string]$ArchiveName
    )

    $entryMap = Get-SafeArchiveEntryMap -Archive $Archive -ArchiveName $ArchiveName
    $expectedFiles = @($expectedManifest.providerFiles)
    if ($entryMap.Count -ne $expectedFiles.Count) { throw 'The provider ZIP entry count differs from the pinned six-file release.' }
    foreach ($record in $expectedFiles) {
        if ([IO.Path]::GetFileName([string]$record.path) -cne [string]$record.path) {
            throw "Unsafe filename in pinned Harmony manifest: $($record.path)"
        }
        $entryName = 'CitiesHarmony/' + [string]$record.path
        if (-not $entryMap.ContainsKey($entryName)) { throw "The provider ZIP is missing the expected entry: $entryName" }
        if ($entryMap[$entryName].Length -ne [long]$record.size) { throw "Provider archive entry size mismatch: $entryName" }
    }
    return ,$entryMap
}

function Assert-ApiPackageLayout {
    param([Parameter(Mandatory = $true)][System.IO.Compression.ZipArchive]$Archive)

    $entryMap = Get-SafeArchiveEntryMap -Archive $Archive -ArchiveName 'CitiesHarmony.API.2.2.0.nupkg'
    if (-not $entryMap.ContainsKey('lib/net35/CitiesHarmony.API.dll')) {
        throw 'The API package is missing lib/net35/CitiesHarmony.API.dll.'
    }
    return ,$entryMap
}

function Copy-ZipEntryToFile {
    param(
        [Parameter(Mandatory = $true)]$Entry,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $destinationPath = Get-ContainedPath -Path $Destination
    $parentPath = Ensure-ProjectDirectory -Path (Split-Path -Parent $destinationPath)
    if (Test-Path -LiteralPath $destinationPath) { throw "Refusing to overwrite an extracted file: $destinationPath" }
    $inputStream = $Entry.Open()
    try {
        $outputStream = [IO.File]::Open($destinationPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $inputStream.CopyTo($outputStream) }
        finally { $outputStream.Dispose() }
    }
    finally { $inputStream.Dispose() }
}

function Test-ProviderDirectory {
    if (-not (Test-Path -LiteralPath $providerRoot)) { return $false }
    $fullProviderRoot = Get-ContainedPath -Path $providerRoot
    $providerItem = Get-Item -LiteralPath $fullProviderRoot -Force
    if (-not $providerItem.PSIsContainer -or ($providerItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "The provider target must be a real directory: $fullProviderRoot"
    }
    $actualFiles = @(Get-ChildItem -LiteralPath $fullProviderRoot -Force)
    $expectedFiles = @($expectedManifest.providerFiles)
    if ($actualFiles.Count -ne $expectedFiles.Count) {
        throw "The existing provider directory has unexpected contents; refusing to overwrite it: $fullProviderRoot"
    }
    foreach ($expectedFile in $expectedFiles) {
        if ([IO.Path]::GetFileName([string]$expectedFile.path) -cne [string]$expectedFile.path) {
            throw "Unsafe filename in pinned Harmony manifest: $($expectedFile.path)"
        }
        $filePath = Join-Path $fullProviderRoot ([string]$expectedFile.path)
        if (-not (Assert-FileHash -Path $filePath -ExpectedHash ([string]$expectedFile.sha256))) {
            throw "The existing provider file is missing: $filePath"
        }
        $item = Get-Item -LiteralPath $filePath -Force
        if ($item.Length -ne [long]$expectedFile.size) { throw "Provider file size mismatch: $filePath" }
    }
    foreach ($actualFile in $actualFiles) {
        if ($actualFile.PSIsContainer -or
            @($expectedFiles | Where-Object { [string]$_.path -ceq [string]$actualFile.Name }).Count -ne 1) {
            throw "The existing provider directory contains an unexpected entry: $($actualFile.FullName)"
        }
    }
    return $true
}

function Expand-ProviderArchive {
    $scratchPath = Get-RunScratchDirectory
    $stageRoot = Join-Path $scratchPath 'provider'
    if (Test-Path -LiteralPath $stageRoot) { throw "Provider staging path unexpectedly exists: $stageRoot" }
    Ensure-ProjectDirectory -Path $stageRoot | Out-Null

    $archive = [IO.Compression.ZipFile]::OpenRead($providerArchivePath)
    try {
        $entryMap = Assert-ProviderArchiveLayout -Archive $archive -ArchiveName 'CitiesHarmony-2.2.2-0.zip'
        $expectedFiles = @($expectedManifest.providerFiles)
        foreach ($record in $expectedFiles) {
            $entryName = 'CitiesHarmony/' + [string]$record.path
            $entry = $entryMap[$entryName]
            $destination = Join-Path $stageRoot ([string]$record.path)
            Copy-ZipEntryToFile -Entry $entry -Destination $destination
            if (-not (Assert-FileHash -Path $destination -ExpectedHash ([string]$record.sha256))) {
                throw "Provider archive entry was not extracted: $entryName"
            }
        }
    }
    finally { $archive.Dispose() }

    $stagedFiles = @(Get-ChildItem -LiteralPath $stageRoot -Force)
    if ($stagedFiles.Count -ne @($expectedManifest.providerFiles).Count) {
        throw "The staged provider directory does not contain exactly six files: $stageRoot"
    }
    $providerParent = Ensure-ProjectDirectory -Path (Split-Path -Parent $providerRoot)
    if (Test-Path -LiteralPath $providerRoot) { throw "Provider target appeared during extraction; refusing to overwrite it: $providerRoot" }
    [IO.Directory]::Move($stageRoot, $providerRoot)
}

function Expand-ApiPackage {
    $scratchPath = Get-RunScratchDirectory
    $stageRoot = Join-Path $scratchPath 'api'
    if (Test-Path -LiteralPath $stageRoot) { throw "API staging path unexpectedly exists: $stageRoot" }
    Ensure-ProjectDirectory -Path $stageRoot | Out-Null

    $archive = [IO.Compression.ZipFile]::OpenRead($apiArchivePath)
    try {
        $entryMap = Assert-ApiPackageLayout -Archive $archive
        $dllEntryName = 'lib/net35/CitiesHarmony.API.dll'
        $entry = $entryMap[$dllEntryName]
        $stagedDll = Join-Path $stageRoot 'CitiesHarmony.API.dll'
        Copy-ZipEntryToFile -Entry $entry -Destination $stagedDll
        if (-not (Assert-FileHash -Path $stagedDll -ExpectedHash ([string]$expectedManifest.apiDllSha256))) {
            throw 'The extracted API DLL was not created.'
        }
    }
    finally { $archive.Dispose() }

    $apiParent = Ensure-ProjectDirectory -Path (Split-Path -Parent $apiDllPath)
    if (Test-Path -LiteralPath $apiDllPath) { throw "API DLL appeared during extraction; refusing to overwrite it: $apiDllPath" }
    [IO.File]::Move($stagedDll, $apiDllPath)
}

function Write-NewManifest {
    param([Parameter(Mandatory = $true)][string]$Path)

    $manifestFullPath = Get-ContainedPath -Path $Path
    if (Test-Path -LiteralPath $manifestFullPath) { throw "Manifest appeared during preparation; refusing to overwrite it: $manifestFullPath" }
    $json = ConvertTo-Json -InputObject $expectedManifest -Depth 8
    $encoding = [Text.UTF8Encoding]::new($false)
    $stream = [IO.File]::Open($manifestFullPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $writer = [IO.StreamWriter]::new($stream, $encoding)
        try { $writer.WriteLine($json) }
        finally { $writer.Dispose() }
    }
    finally {
        if ($null -ne $stream) { $stream.Dispose() }
    }
}

foreach ($directory in @(
    $dependencyRoot,
    $downloadsRoot,
    (Split-Path -Parent $providerRoot),
    (Split-Path -Parent $apiDllPath),
    (Split-Path -Parent $licensePath)
)) {
    Ensure-ProjectDirectory -Path $directory | Out-Null
}

$manifestExists = $false
if (Test-Path -LiteralPath $manifestPath) {
    $manifestItem = Get-Item -LiteralPath $manifestPath -Force
    if ($manifestItem.PSIsContainer -or ($manifestItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "The Harmony manifest must be a regular file: $manifestPath"
    }
    try { $currentManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json }
    catch { throw "Could not parse tools/CitiesHarmony/manifest.json: $($_.Exception.Message)" }
    Assert-ManifestMatches -ActualManifest $currentManifest
    $manifestExists = $true
}

$providerAlreadyPrepared = Test-ProviderDirectory
$apiAlreadyPrepared = Assert-FileHash -Path $apiDllPath -ExpectedHash ([string]$expectedManifest.apiDllSha256)
$licenseAlreadyPresent = Assert-FileHash -Path $licensePath -ExpectedHash $licenseSha256
$providerArchiveAlreadyPresent = Assert-FileHash -Path $providerArchivePath -ExpectedHash ([string]$expectedManifest.providerArchiveSha256)
$apiArchiveAlreadyPresent = Assert-FileHash -Path $apiArchivePath -ExpectedHash ([string]$expectedManifest.apiArchiveSha256)

if (-not $providerArchiveAlreadyPresent) {
    Ensure-VerifiedDownload -Uri ([string]$expectedManifest.providerUrl) -Destination $providerArchivePath -ExpectedHash ([string]$expectedManifest.providerArchiveSha256) | Out-Null
}
if (-not $apiArchiveAlreadyPresent) {
    Ensure-VerifiedDownload -Uri ([string]$expectedManifest.apiUrl) -Destination $apiArchivePath -ExpectedHash ([string]$expectedManifest.apiArchiveSha256) | Out-Null
}
if (-not $licenseAlreadyPresent) {
    Ensure-VerifiedDownload -Uri $licenseUrl -Destination $licensePath -ExpectedHash $licenseSha256 | Out-Null
}

$providerArchive = [IO.Compression.ZipFile]::OpenRead($providerArchivePath)
try { Assert-ProviderArchiveLayout -Archive $providerArchive -ArchiveName 'CitiesHarmony-2.2.2-0.zip' | Out-Null }
finally { $providerArchive.Dispose() }
$apiArchive = [IO.Compression.ZipFile]::OpenRead($apiArchivePath)
try { Assert-ApiPackageLayout -Archive $apiArchive | Out-Null }
finally { $apiArchive.Dispose() }

if (-not $providerAlreadyPrepared) { Expand-ProviderArchive }
if (-not $apiAlreadyPrepared) { Expand-ApiPackage }
if (-not $manifestExists) {
    Ensure-ProjectDirectory -Path (Split-Path -Parent $manifestPath) | Out-Null
    Write-NewManifest -Path $manifestPath
}

Assert-ManifestMatches -ActualManifest (Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json)
if (-not (Test-ProviderDirectory)) { throw 'Harmony provider preparation did not produce the pinned provider files.' }
if (-not (Assert-FileHash -Path $apiDllPath -ExpectedHash ([string]$expectedManifest.apiDllSha256))) {
    throw 'Harmony API preparation did not produce the pinned API DLL.'
}
if (-not (Assert-FileHash -Path $licensePath -ExpectedHash $licenseSha256)) {
    throw 'Harmony license preparation did not produce the pinned license.'
}

Write-Output 'CitiesHarmony 2.2.2-0 / API 2.2.0 prepared and verified under tools/CitiesHarmony.'
Write-Output 'All prepared files remain under tools/CitiesHarmony; no game-side provider or subscription was changed.'
