[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $DevelopmentRoot,
    [string] $Archive
)

$ErrorActionPreference = 'Stop'
$client = (Resolve-Path -LiteralPath $DevelopmentRoot).ProviderPath
if (-not (Test-Path -LiteralPath (Join-Path $client '.eden-client-ready'))) {
    throw 'Stage build 3662168 with DevelopmentClient.ps1 before creating the catalog.'
}
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    & dotnet build src/Eden.Server
    if ($LASTEXITCODE -ne 0) { throw 'The catalog verifier failed to build.' }
    $arguments = @('tools/catalog/build.py', $client)
    if ($Archive) { $arguments += @('--archive', (Resolve-Path -LiteralPath $Archive).ProviderPath) }
    & python @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Catalog setup failed. See the source or verification error above.' }
}
finally {
    Pop-Location
}
