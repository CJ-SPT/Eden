[CmdletBinding()]
param(
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

Push-Location -LiteralPath $repositoryRoot
try {
    $styleArguments = @('format', 'style', 'Eden.slnx', '--no-restore', '--severity', 'warn')
    if ($Check) {
        $styleArguments += '--verify-no-changes'
    }
    & dotnet @styleArguments
    if ($LASTEXITCODE -ne 0) {
        throw 'The .NET style check failed.'
    }

    $formatterCommand = if ($Check) { 'check' } else { 'format' }
    $formatterArguments = @($formatterCommand, '.')
    if (-not $Check) {
        $formatterArguments += '--no-cache'
    }
    & dotnet csharpier @formatterArguments
    if ($LASTEXITCODE -ne 0) {
        throw 'CSharpier failed. Run .\tools\Format.ps1 to apply formatting.'
    }

    & python tools/check_formatting.py
    if ($LASTEXITCODE -ne 0) {
        throw 'The line-width check failed. Wrap the reported lines or document unavoidable exceptions.'
    }
}
finally {
    Pop-Location
}
