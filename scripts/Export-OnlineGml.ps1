param(
    [string]$UndertaleModCli = ''
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Resolve-MmaPaths.ps1')

$repo = Get-MmaRepoRoot
$gameDir = Get-MmaGameDir
$source = Join-Path $gameDir 'data.win'
Assert-MmaStockDataWin -DataWinPath $source

$patcher = Get-UndertaleModCli -ExplicitPath $UndertaleModCli
$outDir = Join-Path $repo 'local\decompiled\online'
$scriptPath = Join-Path $PSScriptRoot 'ExportOnlineGml.csx'

$previousExportDir = $env:MMA_GML_EXPORT_DIR
try {
    $env:MMA_GML_EXPORT_DIR = $outDir
    @() | & $patcher load $source -s $scriptPath
    if ($LASTEXITCODE -ne 0) {
        throw "ExportOnlineGml.csx failed with exit code $LASTEXITCODE."
    }
}
finally {
    if ($null -eq $previousExportDir) {
        Remove-Item Env:MMA_GML_EXPORT_DIR -ErrorAction SilentlyContinue
    } else {
        $env:MMA_GML_EXPORT_DIR = $previousExportDir
    }
}
