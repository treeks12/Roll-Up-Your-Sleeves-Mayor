$ErrorActionPreference = 'Stop'
$Sc4EvolutionRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Sc4EvolutionCache = Join-Path $Sc4EvolutionRoot '.cache'
$Sc4EvolutionDirectories = @{
    DOTNET_CLI_HOME = 'dotnet-home'
    NUGET_PACKAGES = 'nuget/packages'
    NUGET_HTTP_CACHE_PATH = 'nuget/http'
    NUGET_PLUGINS_CACHE_PATH = 'nuget/plugins'
    DOTNET_BUNDLE_EXTRACT_BASE_DIR = 'dotnet-bundles'
    TEMP = 'tmp'
    TMP = 'tmp'
}
foreach ($Sc4EvolutionEntry in $Sc4EvolutionDirectories.GetEnumerator()) {
    $Sc4EvolutionCachePath = Join-Path $Sc4EvolutionCache $Sc4EvolutionEntry.Value
    New-Item -ItemType Directory -Path $Sc4EvolutionCachePath -Force | Out-Null
    [Environment]::SetEnvironmentVariable($Sc4EvolutionEntry.Key, $Sc4EvolutionCachePath, 'Process')
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'

