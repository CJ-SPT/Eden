[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $DevelopmentRoot,
    [string] $Catalog = 'src/Eden.Server/data/catalog.sqlite',
    [string] $Resources,
    [string] $Converter,
    [ValidateRange(1, 16)][int] $Workers = 2,
    [ValidateSet('structures', 'all-static')][string] $Scope = 'structures',
    [switch] $Preflight,
    [switch] $Refresh
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$development = (Resolve-Path -LiteralPath $DevelopmentRoot).ProviderPath
$client = Join-Path $development 'tq'
if (-not (Test-Path -LiteralPath (Join-Path $client 'resfileindex.txt'))) {
    throw 'Supply the staged build-3662168 development root containing tq/resfileindex.txt.'
}
if (-not $Resources) { $Resources = Join-Path $development 'ResFiles' }
$Resources = (Resolve-Path -LiteralPath $Resources).ProviderPath
$revision = '30c0b48255bd3e8c845dd6de77aefded1eb7fd4e'
$toolRoot = Join-Path $repo ('artifacts/tools/Knit-' + $revision)

Push-Location -LiteralPath $repo
try {
    $Catalog = (Resolve-Path -LiteralPath $Catalog).ProviderPath
    if (-not $Converter) {
        $Converter = Join-Path $toolRoot 'Knit.Convert/bin/Release/net10.0/Knit.Convert.dll'
        if (-not $Preflight -and -not (Test-Path -LiteralPath $Converter)) {
            # Knit remains an external EUPL-1.2 offline tool in ignored artifacts, never a runtime dependency.
            if (-not (Test-Path -LiteralPath $toolRoot)) {
                & git clone --no-checkout https://github.com/neptuwunium/Knit.git $toolRoot
                if ($LASTEXITCODE -ne 0) { throw 'Failed to obtain the external mesh converter.' }
            }
            & git -C $toolRoot checkout --detach $revision
            if ($LASTEXITCODE -ne 0) { throw 'Failed to select the pinned converter revision.' }
            & git -C $toolRoot submodule update --init --recursive
            if ($LASTEXITCODE -ne 0) { throw 'Failed to obtain the pinned converter dependencies.' }
            # The upstream revision targets a newer SDK. Its reader also builds against Eden's pinned .NET 10 SDK.
            # Isolate its build from Eden's parent-directory policies and change only the target frameworks.
            [IO.File]::WriteAllText((Join-Path $toolRoot 'Directory.Build.props'), "<Project />`n")
            [IO.File]::WriteAllText((Join-Path $toolRoot 'Directory.Build.targets'), "<Project />`n")
            Get-ChildItem -LiteralPath $toolRoot -Recurse -File -Include '*.csproj', '*.props' | ForEach-Object {
                $content = [IO.File]::ReadAllText($_.FullName)
                $updated = $content.Replace('net10.0;net11.0', 'net10.0').Replace('net11.0', 'net10.0')
                if ($updated -ne $content) { [IO.File]::WriteAllText($_.FullName, $updated) }
            }
            & dotnet build (Join-Path $toolRoot 'Knit.Convert/Knit.Convert.csproj') -c Release --nologo
            if ($LASTEXITCODE -ne 0) { throw 'Failed to build the external mesh converter.' }
        }
    }
    $arguments = @('tools/Export-StaticCollisionGeometry.py', '--catalog', $Catalog, '--client', $client,
        '--resources', $Resources, '--converter', $Converter, '--workers', $Workers, '--scope', $Scope)
    if ($Preflight) { $arguments += '--preflight' }
    if ($Refresh) { $arguments += '--refresh' }
    & python @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Static collision export failed; the previous complete payload is preserved.' }
}
finally {
    Pop-Location
}
