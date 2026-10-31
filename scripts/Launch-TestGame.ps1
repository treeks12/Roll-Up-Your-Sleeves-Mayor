param([switch]$NoWorkshop)

$ErrorActionPreference = 'Stop'
# Validate filesystem ancestors and any local provider BEFORE Enter-Project creates cache paths.
& (Join-Path $PSScriptRoot 'Harmony-TestLease.ps1') -Mode AssertLaunch -NoWorkshop:$NoWorkshop
. (Join-Path $PSScriptRoot 'Enter-Project.ps1')

if (@(Get-Process -Name 'Cities' -ErrorAction SilentlyContinue).Count -ne 0) {
    throw 'Cities: Skylines is already running. This launcher will not interrupt an existing session.'
}
if (@(Get-Process -Name 'steam' -ErrorAction SilentlyContinue).Count -eq 0) {
    throw 'Start the signed-in Steam client before launching this installed game.'
}
$paths = Get-Content -LiteralPath (Join-Path $Sc4EvolutionRoot 'config/local.paths.json') -Raw | ConvertFrom-Json
$gameRoot = [IO.Path]::GetFullPath([string]$paths.CitiesSkylines)
$executable = Join-Path $gameRoot 'Cities.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Configured Cities.exe was not found.' }
$launcher = Get-Content -LiteralPath (Join-Path $gameRoot 'launcher-settings.json') -Raw | ConvertFrom-Json
if ($launcher.distPlatform -ne 'steam' -or $launcher.exePath -ne 'Cities.exe' -or $launcher.version -ne '1.21.1-f9') {
    throw 'The configured game is not the Steam build verified for this project.'
}

# Steamworks documents steam_appid.txt in the process working directory for
# development launches. It still requires the signed-in client and game license.
# Keep this hint in the project cache; never place it in the Steam installation.
$workingDirectory = Join-Path $Sc4EvolutionRoot '.cache/cs1-run'
& (Join-Path $PSScriptRoot 'Harmony-TestLease.ps1') -Mode AssertLaunch -NoWorkshop:$NoWorkshop
New-Item -ItemType Directory -Path $workingDirectory -Force | Out-Null
$appIdPath = Join-Path $workingDirectory 'steam_appid.txt'
if (Test-Path -LiteralPath $appIdPath) {
    if (-not (Test-Path -LiteralPath $appIdPath -PathType Leaf) -or
        [IO.File]::ReadAllText($appIdPath).Trim() -ne '255710') {
        throw 'An unexpected Steam App ID hint already exists in the project launch cache.'
    }
}
else {
    & (Join-Path $PSScriptRoot 'Harmony-TestLease.ps1') -Mode AssertLaunch -NoWorkshop:$NoWorkshop
    $hintStream = [IO.FileStream]::new($appIdPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $hintStream.Write([Text.Encoding]::ASCII.GetBytes('255710')); $hintStream.Flush($true) } finally { $hintStream.Dispose() }
}

# Windowed mode was requested for testing. These switches are present in the
# installed Starter parser; do not resume the newest user save automatically.
$gameArguments = @('-windowed', '-screen-fullscreen', '0', '-screen-width', '1600', '-screen-height', '900')
if ($NoWorkshop) { $gameArguments += '-noWorkshop' }
& (Join-Path $PSScriptRoot 'Harmony-TestLease.ps1') -Mode AssertLaunch -NoWorkshop:$NoWorkshop
$gameProcess = Start-Process -FilePath $executable -WorkingDirectory $workingDirectory -ArgumentList $gameArguments -WindowStyle Normal -PassThru
$launchRecord = [ordered]@{
    ProcessId = $gameProcess.Id
    StartedUtc = $gameProcess.StartTime.ToUniversalTime().ToString('o')
    Executable = $executable
    WorkingDirectory = $workingDirectory
    Arguments = $gameArguments
}
& (Join-Path $PSScriptRoot 'Harmony-TestLease.ps1') -Mode AssertLaunch -NoWorkshop:$NoWorkshop
$launchPath = Join-Path $workingDirectory 'launch.json'
$launchTemp = Join-Path $workingDirectory ('launch.'+[guid]::NewGuid().ToString('N')+'.tmp')
try {
    $recordStream=[IO.FileStream]::new($launchTemp,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try{$recordStream.Write([Text.UTF8Encoding]::new($false).GetBytes(($launchRecord | ConvertTo-Json)));$recordStream.Flush($true)}finally{$recordStream.Dispose()}
    & (Join-Path $PSScriptRoot 'Harmony-TestLease.ps1') -Mode AssertLaunch -NoWorkshop:$NoWorkshop
    if([IO.File]::Exists($launchPath)){[IO.File]::Replace($launchTemp,$launchPath,[System.Management.Automation.Language.NullString]::Value)}else{[IO.File]::Move($launchTemp,$launchPath)}
} finally {
    & (Join-Path $PSScriptRoot 'Harmony-TestLease.ps1') -Mode AssertLaunch -NoWorkshop:$NoWorkshop
    if(Test-Path -LiteralPath $launchTemp){Remove-Item -LiteralPath $launchTemp -Force}
}
Write-Output ('Started installed CS1 for a windowed test. Process ID: ' + $gameProcess.Id)
Write-Output ('Steam development App ID hint: ' + $appIdPath)
