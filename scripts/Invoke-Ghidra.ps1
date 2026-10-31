[CmdletBinding()]
param(
    [switch]$Help,
    [switch]$JavaVersion,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$')]
    [string]$ProjectName,
    [string[]]$HeadlessArguments = @()
)

$ErrorActionPreference = 'Stop'

$environmentNames = @(
    'DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'NUGET_PLUGINS_CACHE_PATH',
    'DOTNET_BUNDLE_EXTRACT_BASE_DIR', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO',
    'DOTNET_SKIP_FIRST_TIME_EXPERIENCE', 'DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE',
    'TEMP', 'TMP', 'HOME', 'USERPROFILE', 'APPDATA', 'LOCALAPPDATA',
    'XDG_CACHE_HOME', 'XDG_CONFIG_HOME', 'JAVA_HOME', 'PATH', 'GHIDRA_JAVA_OPTIONS',
    'GHIDRA_HEADLESS_JAVA_OPTIONS', 'JAVA_TOOL_OPTIONS', 'JDK_JAVA_OPTIONS', '_JAVA_OPTIONS'
)
$environmentBefore = @{}
foreach ($name in $environmentNames) {
    $environmentBefore[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

. (Join-Path $PSScriptRoot 'Enter-Project.ps1')

try {
    if ($Help -and $JavaVersion) { throw 'Choose either -Help or -JavaVersion.' }
    if (-not $Help -and -not $JavaVersion -and [string]::IsNullOrWhiteSpace($ProjectName)) {
        throw 'Pass -Help, -JavaVersion, or -ProjectName plus -HeadlessArguments.'
    }

    $root = [IO.Path]::GetFullPath($Sc4EvolutionRoot)
    $ghidraRoot = Join-Path $root 'tools/ghidra/ghidra_12.1.4_PUBLIC'
    $jdkRoot = Join-Path $root 'tools/jdk/jdk-21.0.12.1+1'
    $headless = Join-Path $ghidraRoot 'support/analyzeHeadless.bat'
    $java = Join-Path $jdkRoot 'bin/java.exe'
    if (-not (Test-Path -LiteralPath $headless -PathType Leaf)) {
        throw "Ghidra 12.1.4 headless launcher is missing: $headless"
    }
    if (-not (Test-Path -LiteralPath $java -PathType Leaf)) {
        throw "Portable Temurin JDK is missing: $java"
    }

    $localState = Join-Path $root '.cache/ghidra'
    $localHome = Join-Path $localState 'home'
    $settingsPath = Join-Path $localState 'settings'
    $cachePath = Join-Path $localState 'cache'
    $tempPath = Join-Path $localState 'temp'
    $appDataPath = Join-Path $localState 'appdata'
    $localAppDataPath = Join-Path $localState 'localappdata'
    foreach ($path in @($localHome, $settingsPath, $cachePath, $tempPath, $appDataPath, $localAppDataPath)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }

    $env:JAVA_HOME = $jdkRoot
    $env:PATH = "$($jdkRoot)\bin;$($environmentBefore['PATH'])"
    $env:APPDATA = $appDataPath
    $env:LOCALAPPDATA = $localAppDataPath
    $env:XDG_CONFIG_HOME = $settingsPath
    $env:XDG_CACHE_HOME = $cachePath
    $env:TEMP = $tempPath
    $env:TMP = $tempPath
    foreach ($name in @('GHIDRA_HEADLESS_JAVA_OPTIONS', 'JAVA_TOOL_OPTIONS', 'JDK_JAVA_OPTIONS', '_JAVA_OPTIONS')) {
        Remove-Item -LiteralPath "Env:\$name" -ErrorAction SilentlyContinue
    }

    $env:GHIDRA_JAVA_OPTIONS = @(
        ('-Duser.home="{0}"' -f $localHome),
        ('-Dapplication.settingsdir="{0}"' -f $settingsPath),
        ('-Dapplication.cachedir="{0}"' -f $cachePath),
        ('-Dapplication.tempdir="{0}"' -f $tempPath),
        ('-Djava.io.tmpdir="{0}"' -f $tempPath)
    ) -join ' '

    if ($JavaVersion) {
        & $java '-version'
        $nativeExitCode = $LASTEXITCODE
        if ($nativeExitCode -ne 0) { throw "Portable Java exited with code ${nativeExitCode}." }
        return
    }

    if ($Help) {
        $helpOutput = @(& $headless '-help' 2>&1)
        $nativeExitCode = $LASTEXITCODE
        $helpOutput | ForEach-Object { Write-Output $_ }
        $helpText = ($helpOutput | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
        if ($helpText -notmatch 'Headless Analyzer Usage:\s*analyzeHeadless') {
            throw "Ghidra headless help did not print its expected usage text (exit code ${nativeExitCode})."
        }
        if ($nativeExitCode -gt 1) { throw "Ghidra headless help exited with code ${nativeExitCode}." }
        return
    }

    $projectsRoot = [IO.Path]::GetFullPath((Join-Path $root 'analysis/generated/ghidra/projects'))
    $rootPrefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $projectsRoot.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Ghidra project directory escaped the project root: $projectsRoot"
    }
    New-Item -ItemType Directory -Path $projectsRoot -Force | Out-Null

    $arguments = @($projectsRoot, $ProjectName) + $HeadlessArguments
    Push-Location $root
    try {
        & $headless @arguments
        $nativeExitCode = $LASTEXITCODE
    }
    finally { Pop-Location }
    if ($nativeExitCode -ne 0) { throw "Ghidra headless analysis exited with code ${nativeExitCode}." }
}
finally {
    foreach ($name in $environmentNames) {
        if ($null -eq $environmentBefore[$name]) {
            Remove-Item -LiteralPath "Env:\$name" -ErrorAction SilentlyContinue
        }
        else {
            [Environment]::SetEnvironmentVariable($name, $environmentBefore[$name], 'Process')
        }
    }
}
