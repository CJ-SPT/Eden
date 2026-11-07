[CmdletBinding()]
param(
    [int[]]$NpcCounts = @(0, 1, 10, 100, 250, 500, 1000, 2000),
    [ValidateRange(1, 256)]
    [int]$NpcPlayers = 1,
    [int[]]$PlayerCounts = @(1, 10, 25, 50),
    [ValidateSet('idle', 'combat')]
    [string[]]$Modes = @('idle', 'combat'),
    [ValidateRange(10, 120)]
    [int]$Frames = 30,
    [string]$Output = 'artifacts/npc-capacity',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repository
$names = @('EDEN_CAPACITY_NPCS', 'EDEN_CAPACITY_PLAYERS', 'EDEN_CAPACITY_MODE',
    'EDEN_CAPACITY_FRAMES', 'EDEN_CAPACITY_OUTPUT', 'EDEN_CAPACITY_REVISION', 'DOTNET_TieredCompilation')
$previous = @{}
foreach ($name in $names) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name)
}
try {
    $env:DOTNET_TieredCompilation = '0'
    $project = 'tests/Eden.Protocol.Tests/Eden.Protocol.Tests.csproj'
    $artifacts = 'artifacts/npc-capacity-build'
    if (-not $NoBuild) {
        dotnet build $project --configuration Release --artifacts-path $artifacts --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw 'Capacity benchmark build failed.' }
    }
    $env:EDEN_CAPACITY_FRAMES = $Frames.ToString()
    $env:EDEN_CAPACITY_OUTPUT = [IO.Path]::GetFullPath($Output, $repository)
    $env:EDEN_CAPACITY_REVISION = (git rev-parse HEAD).Trim()
    $cases = @()
    foreach ($mode in $Modes) {
        foreach ($count in $NpcCounts) {
            if ($mode -eq 'combat' -and $count -eq 0) { continue }
            $cases += [pscustomobject]@{ Players = $NpcPlayers; Npcs = $count; Mode = $mode }
        }
        foreach ($count in $PlayerCounts) {
            $cases += [pscustomobject]@{ Players = $count; Npcs = 1; Mode = $mode }
        }
    }
    foreach ($case in ($cases | Sort-Object Players, Npcs, Mode -Unique)) {
        $env:EDEN_CAPACITY_NPCS = $case.Npcs.ToString()
        $env:EDEN_CAPACITY_PLAYERS = $case.Players.ToString()
        $env:EDEN_CAPACITY_MODE = $case.Mode
        Write-Host ("Measuring {0} players, {1} NPCs, {2}." -f $case.Players, $case.Npcs, $case.Mode)
        $result = Join-Path $env:EDEN_CAPACITY_OUTPUT ('{0}p-{1}n-{2}.trx' -f $case.Players, $case.Npcs, $case.Mode)
        dotnet test $project --configuration Release --artifacts-path $artifacts --no-build `
            --filter 'FullyQualifiedName~NpcCapacityBenchmarkTests.MeasureNpcAndPlayerCapacityThroughNormalSessions' `
            --logger "trx;LogFileName=$result" -- xUnit.MaxParallelThreads=1
        if ($LASTEXITCODE -ne 0) { throw 'Capacity replay failed. Inspect the reported test failure before continuing.' }
        $json = Join-Path $env:EDEN_CAPACITY_OUTPUT ('{0}p-{1}n-{2}.json' -f $case.Players, $case.Npcs, $case.Mode)
        $report = Get-Content -LiteralPath $json -Raw | ConvertFrom-Json
        Write-Host ('Median {0:N2} ms, p95 {1:N2} ms, mean SQL {2:N2}, opens {3:N2}, fits {4:N2}.' -f `
            $report.frame.Median, $report.frame.P95, $report.meanCommands, $report.meanConnections, $report.meanFits)
    }
}
finally {
    foreach ($name in $names) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name])
    }
    Pop-Location
}
