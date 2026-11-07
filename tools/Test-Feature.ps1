[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$Filter
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Filter)) {
    throw 'Supply a test filter selecting the affected methods or classes.'
}
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repositoryRoot
try {
    $project = 'tests/Eden.Protocol.Tests/Eden.Protocol.Tests.csproj'
    $output = 'artifacts/feature-tests'
    $results = Join-Path $output ('results/' + [Guid]::NewGuid().ToString('N'))
    Write-Host "Filter: $Filter"
    $buildTimer = [Diagnostics.Stopwatch]::StartNew()
    & dotnet build $project --artifacts-path $output --verbosity quiet
    if ($LASTEXITCODE -ne 0) {
        throw 'Feature build failed.'
    }
    $buildTimer.Stop()
    $testTimer = [Diagnostics.Stopwatch]::StartNew()
    & dotnet test $project --artifacts-path $output --no-build --filter $Filter `
        --logger 'trx;LogFileName=tests.trx' --results-directory $results
    $testExitCode = $LASTEXITCODE
    $testTimer.Stop()
    $report = Join-Path $results 'tests.trx'
    if (-not (Test-Path -LiteralPath $report)) {
        throw 'The test runner produced no result report.'
    }
    [xml]$trx = Get-Content -LiteralPath $report -Raw
    $count = [int]$trx.TestRun.ResultSummary.Counters.total
    Write-Host ('Matched {0} tests. Build {1:N2}s. Tests {2:N2}s.' -f `
        $count, $buildTimer.Elapsed.TotalSeconds, $testTimer.Elapsed.TotalSeconds)
    if ($count -eq 0) {
        throw "No tests matched filter '$Filter'."
    }
    if ($testExitCode -ne 0) {
        throw "Feature tests failed. See $report."
    }
}
finally {
    Pop-Location
}
