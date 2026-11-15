[CmdletBinding()]
param(
    [switch]$SkipPatchBuild
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Resolve-MmaPaths.ps1')

$repo = Get-MmaRepoRoot
$gameDir = Get-MmaGameDir
$stockDataWin = Join-Path $gameDir 'data.win'
Assert-MmaStockDataWin -DataWinPath $stockDataWin
Write-Host "[1/3] Verified stock Mega Man Arena 5.0 data.win in $gameDir"

$utmtDir = Join-Path $repo 'local\tools\UTMT_CLI_v0.9.2.0'
$utmtZip = Join-Path $repo 'local\tools\UTMT_CLI_v0.9.2.0-Windows.zip'
$existingCli = $null
if (Test-Path -LiteralPath $utmtDir -PathType Container) {
    $existingCli = Get-ChildItem -LiteralPath $utmtDir -Recurse -Filter 'UndertaleModCli.exe' -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
}

if (-not $existingCli) {
    $url = 'https://github.com/UnderminersTeam/UndertaleModTool/releases/download/0.9.2.0/UTMT_CLI_v0.9.2.0-Windows.zip'
    New-Item -ItemType Directory -Path (Split-Path -Parent $utmtZip) -Force | Out-Null
    Write-Host "[2/3] Downloading UndertaleModCli v0.9.2.0 from GitHub..."
    Invoke-WebRequest -Uri $url -OutFile $utmtZip -UseBasicParsing
    New-Item -ItemType Directory -Path $utmtDir -Force | Out-Null
    Expand-Archive -LiteralPath $utmtZip -DestinationPath $utmtDir -Force
    $existingCli = Get-ChildItem -LiteralPath $utmtDir -Recurse -Filter 'UndertaleModCli.exe' -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $existingCli) {
        throw "Downloaded UTMT_CLI_v0.9.2.0-Windows.zip, but UndertaleModCli.exe was not found inside '$utmtDir'."
    }
}
$cliPath = $existingCli.FullName
Write-Host "[2/3] UndertaleModCli ready: $cliPath"

if (-not $SkipPatchBuild) {
    Write-Host "[3/3] Building DedicatedServer and Arena8 patched data.win artifacts..."
    & (Join-Path $repo 'DedicatedServer\Build-DedicatedServer.ps1') -UndertaleModCli $cliPath
    & (Join-Path $repo 'Arena8\Build-Arena8.ps1') -UndertaleModCli $cliPath
    Write-Host "[3/3] Patch toolchain verification complete."
}
