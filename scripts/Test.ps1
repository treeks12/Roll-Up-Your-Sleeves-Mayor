param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Enter-Project.ps1')

$testsRoot = Join-Path $Sc4EvolutionRoot 'tests'
if (-not (Test-Path -LiteralPath $testsRoot -PathType Container)) {
    throw "Tests directory was not found: $testsRoot"
}

$consoleProjects = [System.Collections.Generic.List[string]]::new()
foreach ($project in (Get-ChildItem -LiteralPath $testsRoot -Filter '*.csproj' -File -Recurse | Sort-Object FullName)) {
    try {
        [xml]$projectXml = Get-Content -LiteralPath $project.FullName -Raw
    }
    catch {
        throw "Could not parse test project $($project.FullName): $($_.Exception.Message)"
    }

    $outputTypes = @($projectXml.SelectNodes("//*[local-name()='OutputType']") | ForEach-Object { $_.InnerText.Trim() })
    if ($outputTypes -contains 'Exe' -or $outputTypes -contains 'WinExe') {
        $consoleProjects.Add($project.FullName)
    }
}

if ($consoleProjects.Count -eq 0) {
    throw "No console-check project with OutputType Exe or WinExe was found under $testsRoot"
}

foreach ($projectPath in $consoleProjects) {
    Write-Output "Running console checks: $projectPath"
    $runArguments = @('run', '--project', $projectPath, '--configuration', 'Release', '--no-launch-profile')
    Push-Location $Sc4EvolutionRoot
    try {
        & dotnet @runArguments
        $runExitCode = $LASTEXITCODE
    }
    finally { Pop-Location }
    if ($runExitCode -ne 0) {
        throw "Console checks failed for $projectPath with exit code $runExitCode."
    }
}

Write-Output "All $($consoleProjects.Count) console-check project(s) passed."
