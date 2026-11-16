[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [string]$OutputPath = ''
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

. (Join-Path $PSScriptRoot 'Resolve-MmaPaths.ps1')

$repo = Get-MmaRepoRoot
$gameDir = Get-MmaGameDir
Assert-MmaStockDataWin -DataWinPath (Join-Path $gameDir 'data.win')

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repo 'local\dist\MegaManArena-Maruchi-Client-Windows.zip'
}

if (-not $SkipBuild) {
    & (Join-Path $repo 'Arena8\Build-Arena8.ps1')
}

$clientWin = Join-Path $repo 'local\build\Arena8\data.client8.win'
if (-not (Test-Path -LiteralPath $clientWin -PathType Leaf)) {
    throw "Missing '$clientWin'. Re-run without -SkipBuild or run .\Arena8\Build-Arena8.ps1 first."
}

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Add-ZipFileEntry {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory = $true)]
        [string]$EntryName,
        [Parameter(Mandatory = $true)]
        [string]$SourcePath
    )

    $entry = $Archive.CreateEntry(($EntryName -replace '\\', '/'), [System.IO.Compression.CompressionLevel]::Optimal)
    $dst = $entry.Open()
    try {
        $src = [System.IO.File]::OpenRead($SourcePath)
        try {
            $src.CopyTo($dst)
        }
        finally {
            $src.Dispose()
        }
    }
    finally {
        $dst.Dispose()
    }
}

function Add-ZipTextEntry {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory = $true)]
        [string]$EntryName,
        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    $normalized = ($Text -replace "`r`n", "`n" -replace "`r", "`n") -replace "`n", "`r`n"
    $bytes = $utf8NoBom.GetBytes($normalized)
    $entry = $Archive.CreateEntry(($EntryName -replace '\\', '/'), [System.IO.Compression.CompressionLevel]::Optimal)
    $dst = $entry.Open()
    try {
        $dst.Write($bytes, 0, $bytes.Length)
    }
    finally {
        $dst.Dispose()
    }
}

$rootArena8Cmd = @'
@echo off
setlocal
set "MMA_GAME_DIR="
call "%~dp0Arena8\Start Arena8 Client.cmd"
'@ + "`n"

$rootOriginalCmd = @'
@echo off
setlocal
pushd "%~dp0local\game" || exit /b 1
start "" "MegaManArena.exe"
popd
'@ + "`n"

$usageTxt = @'
Mega Man Arena 5.0 · Maruchi 全模式八人版客户端（Windows）
=========================================================

[简体中文说明]
1. 完整解压整个 ZIP，保留目录结构。
2. 双击 Start Arena8 Client.cmd，在 Online -> Direct IP 输入服务器 IPv4。八人服默认使用 UDP 6528。
3. 全模式八人版支持自由对战（Arena8）、分队（Teams）、小队（Squads）、打 Boss（Boss）、电梯闯关（Elevator）和关卡闯关（Stage）。
   - 具体模式由服务端配置决定。分队对战按角色队色区分，小队模式需先选齐小队成员再按 Ready。
4. 房间上限为 8 人，只要当前在线人数 >= 2 且全员按 Ready 即可直接开局。
5. 抗卡顿机制（Deadline Timeline）：服务端按固定节奏确认每帧输入；单个玩家短时丢包时，缺失输入临时按松开按键处理而不冻结全房，网络恢复后客户端会补收帧并自动追帧同步。
6. 加入原版 2-4 人房时，请双击 Start Original Client.cmd（使用 UDP 6520）。
7. 八人版可在 Options -> Screen Size 选择 FIT 自动适配桌面工作区；日志保存在 local/runtime/Arena8-client。

[English Quick Start]
1. Extract the entire ZIP into a dedicated folder, keeping the directory layout intact.
2. Double-click Start Arena8 Client.cmd, open Online -> Direct IP, and enter the host IPv4 (UDP 6528).
   Supports 2-8 players across Arena8, Teams, Squads, Boss, Elevator, and Stage modes (with anti-stall deadline sync & FIT scaling).
3. Double-click Start Original Client.cmd to join stock 2-4 player servers (UDP 6520).

注意：包内含原版 5.0 游戏运行资产与 Arena8 补丁，仅限私人本地联机使用，请勿上传公开仓库。
'@ + "`n"

$outDir = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$tempPath = "$OutputPath.tmp"
if (Test-Path -LiteralPath $tempPath -PathType Leaf) {
    Remove-Item -LiteralPath $tempPath -Force
}

try {
    $fs = [System.IO.File]::Open($tempPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    try {
        $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            Add-ZipTextEntry -Archive $zip -EntryName 'Start Arena8 Client.cmd' -Text $rootArena8Cmd
            Add-ZipTextEntry -Archive $zip -EntryName 'Start Original Client.cmd' -Text $rootOriginalCmd
            Add-ZipTextEntry -Archive $zip -EntryName '使用说明.txt' -Text $usageTxt

            Add-ZipFileEntry -Archive $zip -EntryName 'scripts/Resolve-MmaPaths.ps1' -SourcePath (Join-Path $repo 'scripts\Resolve-MmaPaths.ps1')
            Add-ZipFileEntry -Archive $zip -EntryName 'Arena8/Start-Arena8.ps1' -SourcePath (Join-Path $repo 'Arena8\Start-Arena8.ps1')
            Add-ZipFileEntry -Archive $zip -EntryName 'Arena8/Start Arena8 Client.cmd' -SourcePath (Join-Path $repo 'Arena8\Start Arena8 Client.cmd')
            Add-ZipFileEntry -Archive $zip -EntryName 'local/build/Arena8/data.client8.win' -SourcePath $clientWin

            $gameFiles = Get-ChildItem -LiteralPath $gameDir -Recurse -File | Sort-Object -Property FullName
            foreach ($file in $gameFiles) {
                $relPath = [System.IO.Path]::GetRelativePath($gameDir, $file.FullName)
                if ($relPath -match '^(logs|runtime)(\\|/)|\.log$|\.tmp$') {
                    continue
                }
                Add-ZipFileEntry -Archive $zip -EntryName ("local/game/$relPath") -SourcePath $file.FullName
            }
        }
        finally {
            $zip.Dispose()
        }
    }
    finally {
        $fs.Dispose()
    }

    if (Test-Path -LiteralPath $OutputPath -PathType Leaf) {
        Remove-Item -LiteralPath $OutputPath -Force
    }
    Move-Item -LiteralPath $tempPath -Destination $OutputPath -Force
}
finally {
    if (Test-Path -LiteralPath $tempPath -PathType Leaf) {
        Remove-Item -LiteralPath $tempPath -Force
    }
}

Write-Host "Packaged Windows client bundle: $OutputPath"
Write-Host 'Note: This archive contains local stock Mega Man Arena 5.0 runtime assets for private friend distribution only; keep it out of public Git.'
