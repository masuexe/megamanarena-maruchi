param(
    [string]$UndertaleModCli = ''
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $repo 'scripts\Resolve-MmaPaths.ps1')

$gameDir = Get-MmaGameDir
$source = Join-Path $gameDir 'data.win'
Assert-MmaStockDataWin -DataWinPath $source

$patcher = Get-UndertaleModCli -ExplicitPath $UndertaleModCli
$outDir = Join-Path $repo 'local\build\DedicatedServer'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$output = Join-Path $outDir 'data.server.win'
$temp = Join-Path $outDir ('build-' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    @() | & $patcher load $source -s (Join-Path $PSScriptRoot 'Patch.csx') -o $temp
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $temp)) {
        throw 'The GameMaker patch did not produce a server file.'
    }
    Move-Item -LiteralPath $temp -Destination $output -Force
    Write-Host "Built $output"
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp }
}
