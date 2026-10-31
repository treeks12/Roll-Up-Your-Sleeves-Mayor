param([switch]$Enable, [switch]$SelfTest)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/Enter-Project.ps1')
$helper = Join-Path $PSScriptRoot 'bin/NativeHash.exe'
New-Item -ItemType Directory -Force (Split-Path $helper) | Out-Null
$reference = Join-Path $Sc4EvolutionCache 'nuget/packages/microsoft.netframework.referenceassemblies.net35/1.0.3/build/.NETFramework/v3.5/mscorlib.dll'
$sdkVersion = (dotnet --version).Trim()
$sdkEntry = @(dotnet --list-sdks | Where-Object { $_ -match ('^' + [regex]::Escape($sdkVersion) + ' \[') })
if ($sdkEntry.Count -ne 1) { throw "Cannot uniquely resolve selected SDK $sdkVersion" }
$sdk = $sdkEntry[0] -replace '^([^ ]+) \[(.+)\]$', '$2/$1'
& dotnet (Join-Path $sdk 'Roslyn/bincore/csc.dll') /nologo /noconfig /nostdlib /target:exe "/reference:$reference" "/out:$helper" (Join-Path $PSScriptRoot 'NativeHash.cs')
if ($LASTEXITCODE -ne 0) { throw 'Native hash helper compilation failed' }
$arguments = @()
if ($Enable) { $arguments += '--enable' }
if ($SelfTest) { $arguments += '--self-test' }
& dotnet run --project (Join-Path $PSScriptRoot 'CS1LocalPreferences.csproj') -- @arguments
if ($LASTEXITCODE -ne 0) { throw 'Preference tool failed' }

