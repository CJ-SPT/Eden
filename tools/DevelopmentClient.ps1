[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $DevelopmentRoot,
    [string] $PrepareFrom,
    [switch] $Login,
    [ValidateSet('Pilot', 'Wing')][string] $Pilot = 'Pilot',
    [switch] $Hidden
)

$ErrorActionPreference = 'Stop'
$root = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($DevelopmentRoot).TrimEnd('\')
$marker = Join-Path $root '.eden-build-3662168'
$ready = Join-Path $root '.eden-client-ready'
$hashes = @{
    'start.ini'        = '4F8216EF3873B779ACDCF414D6A061C61545FB2A489BB3288EF0CFD95B9965F0'
    'code.ccp'         = '37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368'
    'bin64\exefile.exe' = '4C33AD1B4D95C80D2D314849FF6B577A27B6D2420D3CA52641EC3A354EA3D787'
    'bin64\blue.dll'    = 'BFC3C167D25A5BD2CFAF0EA8D60E25028E9F034380885746BCCCFB73645FC94A'
}

function Assert-ClientBuild([string] $client) {
    foreach ($file in $hashes.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $client $file)).Hash -ne $hashes[$file]) {
            throw "Wrong client build or modified file: $file"
        }
    }
}

if ($PrepareFrom) {
    $source = (Resolve-Path -LiteralPath $PrepareFrom).ProviderPath.TrimEnd('\')
    if (($root + '\').StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase) -or
        ($source + '\').StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Use separate, non-overlapping client and development folders.'
    }

    Assert-ClientBuild (Join-Path $source 'tq')
    if (!(Test-Path -LiteralPath (Join-Path $source 'ResFiles') -PathType Container)) {
        throw 'The source must contain the shared ResFiles cache.'
    }
    if (!(Test-Path -LiteralPath $marker -PathType Leaf)) {
        if ((Test-Path -LiteralPath $root) -and (Get-ChildItem -LiteralPath $root -Force | Select-Object -First 1)) {
            throw 'Choose an empty development folder for initial preparation.'
        }
        [IO.Directory]::CreateDirectory($root) | Out-Null
        [IO.File]::WriteAllText($marker, '3662168')
    }
    if (Test-Path -LiteralPath $ready) { Remove-Item -LiteralPath $ready }
    foreach ($name in 'tq', 'ResFiles') {
        & robocopy (Join-Path $source $name) (Join-Path $root $name) /E /XJ /XF '*.tmp' /R:1 /W:1 /MT:3 /NFL /NDL /NP
        if ($LASTEXITCODE -gt 7) { throw "Failed to stage $name (robocopy $LASTEXITCODE)." }
    }
    Assert-ClientBuild (Join-Path $root 'tq')
    [IO.File]::WriteAllText($ready, '3662168')
    return
}

if (!(Test-Path -LiteralPath $ready -PathType Leaf)) { throw 'Complete development copy preparation first.' }
$clientRoot = Join-Path $root 'tq'
Assert-ClientBuild $clientRoot
$cache = Join-Path $root 'ResFiles'
if (!(Test-Path -LiteralPath $cache -PathType Container)) { throw 'Missing staged resource cache.' }
$bin = Join-Path $clientRoot 'bin64'
$launchArgs = @('/noconsole', '/server:127.0.0.1', ('"/remotefilecachefolder=' + $cache + '"'), ('/settingsprofile=' + $(if ($Pilot -eq 'Wing') { 'EdenWing' } else { 'Eden' })), '/triPlatform=dx11')
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $bin 'exefile.exe'))
$start.WorkingDirectory = $bin
$start.UseShellExecute = $false
$start.WindowStyle = if ($Hidden) { 'Hidden' } else { 'Normal' }
if ($Login) {
    $project = Join-Path $PSScriptRoot '../src/Eden.Server'
    $ticket = & dotnet run --no-build --project $project -- --prepare-login $root $Pilot --development-login
    if ($LASTEXITCODE -or $ticket -notmatch '^eden2\.100000[12]\.[0-9]+\.[A-F0-9]+\.[A-F0-9]+$') {
        throw 'Could not provision the development login. Build the solution first.'
    }
    $launchArgs += "/ssoToken=$ticket"
    $start.Environment['EVE_PUBLIC_GATEWAY_ADDRESS'] = '127.0.0.1:26001'
}
$start.Arguments = $launchArgs -join ' '
[Diagnostics.Process]::Start($start)
