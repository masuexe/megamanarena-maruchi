param(
    [string]$UndertaleModCli = ''
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $repo 'scripts\Resolve-MmaPaths.ps1')

$gameDir = Get-MmaGameDir
$stockData = Join-Path $gameDir 'data.win'
Assert-MmaStockDataWin -DataWinPath $stockData

$patcher = Get-UndertaleModCli -ExplicitPath $UndertaleModCli
$patchScript = Join-Path $PSScriptRoot 'Patch.csx'
$dedicatedData = Join-Path $repo 'local\build\DedicatedServer\data.server.win'
if (!(Test-Path -LiteralPath $dedicatedData -PathType Leaf)) {
    & (Join-Path $repo 'DedicatedServer\Build-DedicatedServer.ps1') -UndertaleModCli $patcher
}

$outDir = Join-Path $repo 'local\build\Arena8'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

function Invoke-Arena8Patch {
    param(
        [Parameter(Mandatory)][string]$InputData,
        [Parameter(Mandatory)][string]$OutputData
    )
    $temp = Join-Path $outDir ('build-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        @() | & $patcher load $InputData -s $patchScript -o $temp
        if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $temp)) {
            throw "Arena8 patch failed for $InputData"
        }
        Move-Item -LiteralPath $temp -Destination $OutputData -Force
        Write-Host "Built $OutputData"
    }
    finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp }
    }
}

Invoke-Arena8Patch -InputData $stockData -OutputData (Join-Path $outDir 'data.client8.win')
Invoke-Arena8Patch -InputData $dedicatedData -OutputData (Join-Path $outDir 'data.server8.win')
