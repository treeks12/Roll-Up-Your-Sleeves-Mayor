param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Enter-Project.ps1')

function Get-AcfValue {
    param([string]$Text, [string]$Key)
    $match = [regex]::Match($Text, '(?m)^\s*"' + [regex]::Escape($Key) + '"\s+"([^"]*)"\s*$')
    if ($match.Success) { return $match.Groups[1].Value }
    return $null
}

function Get-ManifestSummary {
    param([string]$GameRoot, [string]$ManifestName)

    if ([string]::IsNullOrWhiteSpace($GameRoot) -or -not (Test-Path -LiteralPath $GameRoot -PathType Container)) {
        return [ordered]@{ present = $false; buildId = $null; appId = $null; name = $null }
    }
    $steamAppsPath = Split-Path -Parent (Split-Path -Parent $GameRoot)
    $manifestPath = Join-Path $steamAppsPath $ManifestName
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        return [ordered]@{ present = $false; buildId = $null; appId = $null; name = $null }
    }

    $manifestText = Get-Content -LiteralPath $manifestPath -Raw
    return [ordered]@{
        present = $true
        appId = Get-AcfValue -Text $manifestText -Key 'appid'
        name = Get-AcfValue -Text $manifestText -Key 'name'
        buildId = Get-AcfValue -Text $manifestText -Key 'buildid'
        stateFlags = Get-AcfValue -Text $manifestText -Key 'StateFlags'
    }
}

$pathsFile = Join-Path $Sc4EvolutionRoot 'config/local.paths.json'
$configValid = $false
$citiesRoot = $null
$simCityRoot = $null
$configError = $null
if (Test-Path -LiteralPath $pathsFile -PathType Leaf) {
    try {
        $paths = Get-Content -LiteralPath $pathsFile -Raw | ConvertFrom-Json
        foreach ($name in @('CitiesSkylines', 'SimCity4')) {
            $property = $paths.PSObject.Properties[$name]
            if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
                throw "config/local.paths.json must contain a non-empty '$name' path."
            }
            if (-not [IO.Path]::IsPathRooted([string]$property.Value)) {
                throw "The configured '$name' path must be absolute."
            }
        }
        $citiesRoot = [IO.Path]::GetFullPath([string]$paths.CitiesSkylines)
        $simCityRoot = [IO.Path]::GetFullPath([string]$paths.SimCity4)
        $configValid = $true
    }
    catch { $configError = $_.Exception.Message }
}
else { $configError = 'config/local.paths.json is missing.' }

$citiesPathExists = $configValid -and (Test-Path -LiteralPath $citiesRoot -PathType Container)
$simCityPathExists = $configValid -and (Test-Path -LiteralPath $simCityRoot -PathType Container)
$managedPath = if ($citiesPathExists) { Join-Path $citiesRoot 'Cities_Data/Managed' } else { $null }
$requiredManagedDlls = @('ICities.dll', 'Assembly-CSharp.dll', 'ColossalManaged.dll', 'UnityEngine.dll')
$managedReferences = @(
    foreach ($name in $requiredManagedDlls) {
        $filePath = if ($managedPath) { Join-Path $managedPath $name } else { $null }
        [ordered]@{ name = $name; present = ($filePath -and (Test-Path -LiteralPath $filePath -PathType Leaf)) }
    }
)
$citiesExe = if ($citiesPathExists) { Join-Path $citiesRoot 'Cities.exe' } else { $null }
$simCityExe = if ($simCityPathExists) { Join-Path $simCityRoot 'Apps/SimCity 4.exe' } else { $null }

$dotnetAvailable = $false
$dotnetVersion = $null
$dotnetError = $null
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    Push-Location $Sc4EvolutionRoot
    try {
        $dotnetOutput = @(& dotnet '--version' 2>&1)
        $dotnetExitCode = $LASTEXITCODE
    }
    catch { $dotnetError = $_.Exception.Message; $dotnetExitCode = -1 }
    finally { Pop-Location }
    if ($dotnetExitCode -eq 0) {
        $dotnetAvailable = $true
        $dotnetVersion = (($dotnetOutput | ForEach-Object { [string]$_ }) -join ' ').Trim()
    }
    elseif (-not $dotnetError) { $dotnetError = ($dotnetOutput -join ' ').Trim() }
}
else { $dotnetError = 'dotnet was not found on PATH.' }

$ilspyPath = Join-Path $Sc4EvolutionRoot 'tools/bin/ilspycmd.exe'
$expectedIlspyVersion = '9.1.0.7988'
$ilspyAvailable = $false
$ilspyVersion = $null
$ilspyPinnedVersionMatch = $false
$ilspyError = $null
if (Test-Path -LiteralPath $ilspyPath -PathType Leaf) {
    try {
        $ilspyOutput = @(& $ilspyPath '--version' 2>&1)
        $ilspyExitCode = $LASTEXITCODE
        if ($ilspyExitCode -ne 0) { throw "ilspycmd exited with code ${ilspyExitCode}: $($ilspyOutput -join ' ')" }
        foreach ($line in $ilspyOutput) {
            if ([string]$line -match 'ilspycmd:\s*([0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?)') {
                $ilspyVersion = $Matches[1]
                break
            }
        }
        $ilspyAvailable = -not [string]::IsNullOrWhiteSpace($ilspyVersion)
        $ilspyPinnedVersionMatch = $ilspyAvailable -and ($ilspyVersion -eq $expectedIlspyVersion)
        if (-not $ilspyAvailable) { $ilspyError = 'ilspycmd did not report its version.' }
    }
    catch { $ilspyError = $_.Exception.Message }
}
else { $ilspyError = 'tools/bin/ilspycmd.exe is missing; run scripts/Bootstrap.ps1.' }

$citiesManifest = Get-ManifestSummary -GameRoot $citiesRoot -ManifestName 'appmanifest_255710.acf'
$simCityManifest = Get-ManifestSummary -GameRoot $simCityRoot -ManifestName 'appmanifest_24780.acf'
$citiesRunning = @(Get-Process -Name 'Cities' -ErrorAction SilentlyContinue).Count -gt 0

$requiredChecksPass = $configValid -and $citiesPathExists -and $simCityPathExists -and
    (Test-Path -LiteralPath $citiesExe -PathType Leaf) -and
    (Test-Path -LiteralPath $simCityExe -PathType Leaf) -and
    (@($managedReferences | Where-Object { -not $_.present }).Count -eq 0) -and
    $dotnetAvailable -and $ilspyAvailable -and $ilspyPinnedVersionMatch
$report = [ordered]@{
    generatedUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    projectRoot = $Sc4EvolutionRoot
    status = if ($requiredChecksPass) { 'ok' } else { 'needs-attention' }
    config = [ordered]@{ present = (Test-Path -LiteralPath $pathsFile -PathType Leaf); valid = $configValid; error = $configError }
    games = [ordered]@{
        citiesSkylines = [ordered]@{
            path = $citiesRoot
            directoryPresent = $citiesPathExists
            executablePresent = ($citiesExe -and (Test-Path -LiteralPath $citiesExe -PathType Leaf))
            managedReferences = $managedReferences
            steamManifest = $citiesManifest
        }
        simCity4 = [ordered]@{
            path = $simCityRoot
            directoryPresent = $simCityPathExists
            executablePresent = ($simCityExe -and (Test-Path -LiteralPath $simCityExe -PathType Leaf))
            steamManifest = $simCityManifest
        }
    }
    tools = [ordered]@{
        dotnet = [ordered]@{ available = $dotnetAvailable; version = $dotnetVersion; error = $dotnetError }
        ilspycmd = [ordered]@{ available = $ilspyAvailable; version = $ilspyVersion; expectedVersion = $expectedIlspyVersion; pinnedVersionMatch = $ilspyPinnedVersionMatch; error = $ilspyError }
    }
    citiesProcessRunning = $citiesRunning
    steamOwnerIdsIncluded = $false
}

$reportPath = Join-Path $Sc4EvolutionRoot 'analysis/generated/doctor.json'
New-Item -ItemType Directory -Path (Split-Path -Parent $reportPath) -Force | Out-Null
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8

Write-Output "Doctor status: $($report.status)"
Write-Output "Report: $reportPath"
Write-Output "Cities: directory=$citiesPathExists, managed references=$(@($managedReferences | Where-Object { $_.present }).Count)/$($managedReferences.Count), build ID=$($citiesManifest.buildId)"
Write-Output "SimCity 4: directory=$simCityPathExists, build ID=$($simCityManifest.buildId)"
Write-Output "Tools: dotnet=$dotnetVersion, ilspycmd=$ilspyVersion"
if (-not $requiredChecksPass) { exit 1 }
