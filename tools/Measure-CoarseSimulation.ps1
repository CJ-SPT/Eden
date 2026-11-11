[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Source,
    [string]$Project = 'tests/Eden.Protocol.Tests/Eden.Protocol.Tests.csproj',
    [string]$BuildArtifacts = 'artifacts/feature-tests',
    [string]$Output = 'artifacts/coarse-simulation/current',
    [int]$Seconds = 600,
    [int[]]$Observers = @(1, 2, 4),
    [int[]]$Scales = @(1, 5, 10),
    [switch]$Baseline,
    [switch]$IncludeStress
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repositoryRoot
$names = @('EDEN_BOT_REPLAY_SOURCE', 'EDEN_BOT_REPLAY_CASE', 'EDEN_BOT_REPLAY_SYSTEMS',
    'EDEN_BOT_REPLAY_OBSERVERS', 'EDEN_BOT_REPLAY_PREPARATION', 'EDEN_BOT_REPLAY_SECONDS',
    'EDEN_BOT_REPLAY_BASELINE', 'EDEN_PERFORMANCE_OUTPUT')
$previous = @{}
foreach ($name in $names) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
try {
    $env:EDEN_BOT_REPLAY_SOURCE = (Resolve-Path -LiteralPath $Source).Path
    $env:EDEN_BOT_REPLAY_SECONDS = $Seconds.ToString()
    $env:EDEN_BOT_REPLAY_PREPARATION = '1'
    $env:EDEN_BOT_REPLAY_BASELINE = if ($Baseline) { '1' } else { '0' }
    New-Item -ItemType Directory -Force -Path $Output | Out-Null
    $env:EDEN_PERFORMANCE_OUTPUT = (Resolve-Path -LiteralPath $Output).Path
    $cases = @(
        foreach ($observerCount in $Observers) {
            foreach ($scale in $Scales) {
                [pscustomobject]@{ Observers = $observerCount; Systems = 45 * $scale; Backlog = $false }
            }
        }
    )
    if ($IncludeStress) {
        $cases += [pscustomobject]@{ Observers = 4; Systems = 450; Backlog = $true }
        $cases += [pscustomobject]@{ Observers = 4; Systems = 900; Backlog = $true }
    }
    foreach ($case in $cases) {
        $env:EDEN_BOT_REPLAY_OBSERVERS = $case.Observers.ToString()
        $env:EDEN_BOT_REPLAY_SYSTEMS = $case.Systems.ToString()
        $env:EDEN_BOT_REPLAY_CASE = '450-' + $case.Backlog
        $report = '{0}-{1}-{2}.trx' -f $case.Observers, $case.Systems, $case.Backlog
        Write-Host ('Measuring {0} observers, {1} remote communities, backlog={2}.' -f `
            $case.Observers, $case.Systems, $case.Backlog)
        & dotnet test $Project --no-build --artifacts-path $BuildArtifacts `
            --filter 'FullyQualifiedName~ReplaySustainedBotThroughput' `
            --logger "trx;LogFileName=$report" --results-directory $Output
        if ($LASTEXITCODE -ne 0) {
            throw "Replay failed: $report. Inspect its measurements before continuing."
        }
    }
}
finally {
    foreach ($name in $names) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
    }
    Pop-Location
}
