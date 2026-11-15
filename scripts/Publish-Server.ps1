[CmdletBinding()]
param(
    [ValidateSet('all', 'linux-x64', 'win-x64')]
    [string]$Runtime = 'all',
    [string]$Configuration = 'Release',
    [switch]$SkipArchive
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\Mma.Server\Mma.Server.csproj'

if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "Server project not found at '$project'."
}

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Normalize-LineEndings {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Content,
        [Parameter(Mandatory = $true)]
        [string]$NewLine
    )

    return ($Content -replace "`r`n", "`n" -replace "`r", "`n") -replace "`n", $NewLine
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Content,
        [Parameter(Mandatory = $true)]
        [string]$NewLine
    )

    [System.IO.File]::WriteAllText($Path, (Normalize-LineEndings -Content $Content -NewLine $NewLine), $utf8NoBom)
}

function New-WindowsArchive {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ArchivePath,
        [Parameter(Mandatory = $true)]
        [string]$RootFolderName,
        [Parameter(Mandatory = $true)]
        [hashtable[]]$Entries
    )

    $tempPath = "$ArchivePath.tmp"
    if (Test-Path -LiteralPath $tempPath -PathType Leaf) {
        Remove-Item -LiteralPath $tempPath -Force
    }

    try {
        $fileStream = [System.IO.File]::Open($tempPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try {
            $zip = New-Object System.IO.Compression.ZipArchive($fileStream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
            try {
                foreach ($item in $Entries) {
                    $entryName = "$RootFolderName/$($item.Name)"
                    $entry = $zip.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
                    $entryStream = $entry.Open()
                    try {
                        if ($item.ContainsKey('SourcePath')) {
                            $srcStream = [System.IO.File]::OpenRead($item.SourcePath)
                            try {
                                $srcStream.CopyTo($entryStream)
                            }
                            finally {
                                $srcStream.Dispose()
                            }
                        }
                        else {
                            $bytes = $item.Bytes
                            $entryStream.Write($bytes, 0, $bytes.Length)
                        }
                    }
                    finally {
                        $entryStream.Dispose()
                    }
                }
            }
            finally {
                $zip.Dispose()
            }
        }
        finally {
            $fileStream.Dispose()
        }

        if (Test-Path -LiteralPath $ArchivePath -PathType Leaf) {
            Remove-Item -LiteralPath $ArchivePath -Force
        }
        Move-Item -LiteralPath $tempPath -Destination $ArchivePath -Force
    }
    finally {
        if (Test-Path -LiteralPath $tempPath -PathType Leaf) {
            Remove-Item -LiteralPath $tempPath -Force
        }
    }
}

function New-LinuxTarGzArchive {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ArchivePath,
        [Parameter(Mandatory = $true)]
        [string]$RootFolderName,
        [Parameter(Mandatory = $true)]
        [hashtable[]]$Entries
    )

    $tempPath = "$ArchivePath.tmp"
    if (Test-Path -LiteralPath $tempPath -PathType Leaf) {
        Remove-Item -LiteralPath $tempPath -Force
    }

    $hasManagedTar = $null -ne ('System.Formats.Tar.TarWriter' -as [type])
    if (-not $hasManagedTar) {
        $tarExe = Join-Path $env:SystemRoot 'System32\tar.exe'
        if (-not (Test-Path -LiteralPath $tarExe -PathType Leaf)) {
            throw 'Creating .tar.gz archives requires PowerShell 7+ (.NET System.Formats.Tar) or Windows tar.exe.'
        }

        $stagingParent = Join-Path ([System.IO.Path]::GetTempPath()) ("mma-stage-" + [System.Guid]::NewGuid().ToString('N'))
        $stagingRoot = Join-Path $stagingParent $RootFolderName
        New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
        try {
            foreach ($item in $Entries) {
                $dest = Join-Path $stagingRoot $item.Name
                if ($item.ContainsKey('SourcePath')) {
                    Copy-Item -LiteralPath $item.SourcePath -Destination $dest -Force
                }
                else {
                    [System.IO.File]::WriteAllBytes($dest, $item.Bytes)
                }
            }

            & $tarExe -C $stagingParent -czf $ArchivePath $RootFolderName
            if ($LASTEXITCODE -ne 0) {
                throw "tar.exe failed with exit code $LASTEXITCODE."
            }
            Write-Warning 'Created .tar.gz with Windows tar.exe; Linux recipients should run chmod +x Mma.Server start-server.sh if needed.'
            return
        }
        finally {
            if (Test-Path -LiteralPath $stagingParent -PathType Container) {
                Remove-Item -LiteralPath $stagingParent -Recurse -Force
            }
        }
    }

    $nowUtc = [DateTimeOffset]::UtcNow
    try {
        $fileStream = [System.IO.File]::Open($tempPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try {
            $gzipStream = New-Object System.IO.Compression.GZipStream($fileStream, [System.IO.Compression.CompressionLevel]::Optimal, $false)
            try {
                $tarWriter = New-Object System.Formats.Tar.TarWriter($gzipStream, [System.Formats.Tar.TarEntryFormat]::Pax, $false)
                try {
                    $dirEntry = New-Object System.Formats.Tar.PaxTarEntry([System.Formats.Tar.TarEntryType]::Directory, "$RootFolderName/")
                    $dirEntry.Mode = [System.IO.UnixFileMode]493
                    $dirEntry.ModificationTime = $nowUtc
                    $tarWriter.WriteEntry($dirEntry)

                    foreach ($item in $Entries) {
                        $entry = New-Object System.Formats.Tar.PaxTarEntry([System.Formats.Tar.TarEntryType]::RegularFile, "$RootFolderName/$($item.Name)")
                        $entry.Mode = [System.IO.UnixFileMode]$item.UnixMode
                        $entry.ModificationTime = $nowUtc

                        if ($item.ContainsKey('SourcePath')) {
                            $srcStream = [System.IO.File]::OpenRead($item.SourcePath)
                            try {
                                $entry.DataStream = $srcStream
                                $tarWriter.WriteEntry($entry)
                            }
                            finally {
                                $srcStream.Dispose()
                            }
                        }
                        else {
                            $memStream = [System.IO.MemoryStream]::new([byte[]]$item.Bytes)
                            try {
                                $entry.DataStream = $memStream
                                $tarWriter.WriteEntry($entry)
                            }
                            finally {
                                $memStream.Dispose()
                            }
                        }
                    }
                }
                finally {
                    $tarWriter.Dispose()
                }
            }
            finally {
                $gzipStream.Dispose()
            }
        }
        finally {
            $fileStream.Dispose()
        }

        if (Test-Path -LiteralPath $ArchivePath -PathType Leaf) {
            Remove-Item -LiteralPath $ArchivePath -Force
        }
        Move-Item -LiteralPath $tempPath -Destination $ArchivePath -Force
    }
    finally {
        if (Test-Path -LiteralPath $tempPath -PathType Leaf) {
            Remove-Item -LiteralPath $tempPath -Force
        }
    }
}

$exampleConfig = @'
{
  "Bind": "0.0.0.0",
  "Port": 6520,
  "MaxPlayers": 4,
  "Mode": "Arena",
  "Buffer": 3
}
'@ + "`n"

$linuxStartScript = @'
#!/usr/bin/env bash
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ ! -x "$DIR/Mma.Server" ]; then
  chmod +x "$DIR/Mma.Server"
fi
exec "$DIR/Mma.Server" "$@"
'@ + "`n"

$windowsStartCmd = @'
@echo off
setlocal
"%~dp0Mma.Server.exe" %*
set "EXIT_CODE=%ERRORLEVEL%"
if not "%EXIT_CODE%"=="0" (
    echo.
    echo Mma.Server.exe exited with code %EXIT_CODE%.
    pause
)
exit /b %EXIT_CODE%
'@ + "`n"

$windowsFirewallCmd = @'
@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Open-FirewallPorts.ps1" %*
set "EXIT_CODE=%ERRORLEVEL%"
echo.
pause
exit /b %EXIT_CODE%
'@ + "`n"

$windowsFirewallScript = @'
[CmdletBinding()]
param(
    [ValidateRange(0, 65534)]
    [int]$Port = 0,
    [string]$RuleName = 'Mega Man Arena Headless Server'
)

$ErrorActionPreference = 'Stop'

if (-not $PSBoundParameters.ContainsKey('Port') -or $Port -eq 0) {
    $Port = 6520
    $configFile = Join-Path $PSScriptRoot 'server.json'
    if (Test-Path -LiteralPath $configFile -PathType Leaf) {
        try {
            $rawJson = Get-Content -LiteralPath $configFile -Raw -Encoding UTF8
            $cleanJson = ($rawJson -replace '(?m)^\s*//.*$', '') -replace ',\s*([}\]])', '$1'
            $parsed = $cleanJson | ConvertFrom-Json
            if ($null -ne $parsed -and $null -ne $parsed.Port) {
                $candidate = [int]$parsed.Port
                if ($candidate -ge 1 -and $candidate -le 65534) {
                    $Port = $candidate
                }
            }
        }
        catch {
            Write-Warning "Could not read Port from '$configFile'; defaulting to UDP $Port."
        }
    }
}

if ($Port -lt 1 -or $Port -gt 65534) {
    throw "Port must be between 1 and 65534 (received $Port) so both Port and Port + 1 are valid."
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Administrator privileges are required to modify Windows Firewall rules. Right-click Open-FirewallPorts.cmd and choose "Run as administrator", or run PowerShell as Administrator.'
}

$ports = @([string]$Port, [string]($Port + 1))
$existing = Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue
if ($existing) {
    $existing | Remove-NetFirewallRule
}

New-NetFirewallRule `
    -DisplayName $RuleName `
    -Direction Inbound `
    -Protocol UDP `
    -LocalPort $ports `
    -Action Allow | Out-Null

Write-Host "Configured Windows Firewall inbound UDP rule '$RuleName' for ports $($ports -join ', ')."
'@ + "`n"

$windowsReadmeTxt = @'
Mega Man Arena 5.0 - Windows / Windows Server Headless Dedicated Server
=======================================================================

[简体中文快速开服]
1. 无需提前安装 .NET、显卡驱动、DirectX 或原版游戏文件；断开远程桌面(RDP)后也可稳定后台运行。
2. 右键 Open-FirewallPorts.cmd ->“以管理员身份运行”，自动放行 Windows 防火墙入站 UDP 双端口（默认 6520 与 6521）。
   - 如果使用阿里云、腾讯云、AWS 等云服务器，请同步在控制台安全组放行对应入站 UDP 端口。
3. 按需修改同目录的 server.json：
   - 原版 5.0 客户端（2~4 人）：保持 Port = 6520，Mode 可选 Arena / Teams / Squads / Boss / Elevator / Stage。
   - Arena8 八人客户端（5~8 人）：设 Port = 6528，Mode = "Arena8"（或 MaxPlayers = 8），并重新运行一次 Open-FirewallPorts.cmd 放行 6528 与 6529。
4. 双击 Start-MmaServer.cmd（或在命令行运行 Mma.Server.exe --help 查看全部参数）即可开服。
5. 玩家在游戏里打开 Online -> Direct IP，输入服务器公网/局域网 IPv4 地址及端口号即可加入。

[English Quick Start]
1. Standalone single-file executable: no .NET runtime, GPU, DirectX, interactive desktop session, or game assets required.
2. Right-click Open-FirewallPorts.cmd -> "Run as administrator" to allow inbound UDP Port and Port + 1 in Windows Firewall (defaults: 6520 and 6521).
   - On cloud VMs (AWS, Azure, Aliyun, Tencent Cloud, etc.), also allow inbound UDP for both Port and Port + 1 in the cloud security group.
3. Edit server.json as needed:
   - Stock 5.0 clients (2-4 players): Port = 6520; Mode in {Arena, Teams, Squads, Boss, Elevator, Stage}.
   - Arena8 clients (5-8+ players): Port = 6528; Mode = "Arena8" (or MaxPlayers = 8), then re-run Open-FirewallPorts.cmd to allow UDP 6528 and 6529.
4. Double-click Start-MmaServer.cmd (or run Mma.Server.exe --help for CLI/env options).
'@ + "`n"

$linuxReadmeTxt = @'
Mega Man Arena 5.0 - Linux Headless Dedicated Server (linux-x64)
================================================================

[简体中文快速开服]
1. 本程序为独立单文件可执行程序，无需安装 .NET、Wine、Xvfb、显卡驱动或原版游戏文件。
2. 如解压后缺少执行权限，先执行：
     chmod +x Mma.Server start-server.sh
3. 在系统防火墙与云服务器安全组同时放行入站 UDP 双端口（Port 与 Port + 1）：
   - 原版 5.0 客户端（2~4 人）：默认 UDP 6520 与 6521（例如 sudo ufw allow 6520:6521/udp）。
   - Arena8 八人客户端（5~8 人）：默认 UDP 6528 与 6529（需同步修改 server.json）。
4. 按需编辑 server.json，然后运行：
     ./start-server.sh
   或直接在 tmux / systemd 中运行 ./Mma.Server（支持 SIGINT / SIGTERM 优雅退出，--help 查看全部参数）。
5. 玩家在游戏里打开 Online -> Direct IP，输入服务器 IPv4 与端口即可加入。

[English Quick Start]
1. Standalone single-file executable: no .NET runtime, Wine, Xvfb, GPU, or original game files required.
2. Ensure execute permissions if needed:
     chmod +x Mma.Server start-server.sh
3. Allow inbound UDP for both Port and Port + 1 in your OS firewall and cloud security group:
   - Stock 5.0 clients (2-4 players): UDP 6520 and 6521 (e.g. sudo ufw allow 6520:6521/udp).
   - Arena8 clients (5-8+ players): UDP 6528 and 6529 (set Port/MaxPlayers/Mode in server.json).
4. Edit server.json as needed and start with:
     ./start-server.sh
   Supports SIGINT/SIGTERM for systemd or tmux; pass --help for full CLI/env configuration details.
'@ + "`n"

$targets = if ($Runtime -eq 'all') { @('linux-x64', 'win-x64') } else { @($Runtime) }
$distDir = Join-Path $repo 'local\dist'
$defaultConfigBytes = $utf8NoBom.GetBytes((Normalize-LineEndings -Content $exampleConfig -NewLine "`n"))
$defaultConfigBytesCrlf = $utf8NoBom.GetBytes((Normalize-LineEndings -Content $exampleConfig -NewLine "`r`n"))

foreach ($rid in $targets) {
    $outDir = Join-Path $repo "local\publish\$rid"
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null

    Write-Host "Publishing Mma.Server for '$rid' ($Configuration)..."
    & dotnet publish $project `
        -c $Configuration `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:DebugType=embedded `
        --disable-build-servers `
        -o $outDir

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish for '$rid' failed with exit code $LASTEXITCODE."
    }

    $binaryName = if ($rid -eq 'win-x64') { 'Mma.Server.exe' } else { 'Mma.Server' }
    $binary = Join-Path $outDir $binaryName
    if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) {
        throw "Published binary not found at '$binary'."
    }

    $exampleConfigPath = Join-Path $outDir 'server.example.json'
    Write-Utf8NoBom -Path $exampleConfigPath -Content $exampleConfig -NewLine "`n"

    $configPath = Join-Path $outDir 'server.json'
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        Write-Utf8NoBom -Path $configPath -Content $exampleConfig -NewLine "`n"
    }

    if ($rid -eq 'linux-x64') {
        $startShPath = Join-Path $outDir 'start-server.sh'
        Write-Utf8NoBom -Path $startShPath -Content $linuxStartScript -NewLine "`n"

        $readmePath = Join-Path $outDir 'README.txt'
        Write-Utf8NoBom -Path $readmePath -Content $linuxReadmeTxt -NewLine "`n"

        if (-not $SkipArchive) {
            New-Item -ItemType Directory -Path $distDir -Force | Out-Null
            $archivePath = Join-Path $distDir 'megamanarena-server-linux-x64.tar.gz'
            New-LinuxTarGzArchive -ArchivePath $archivePath -RootFolderName 'megamanarena-server-linux-x64' -Entries @(
                @{ Name = 'Mma.Server'; SourcePath = $binary; UnixMode = 493 },
                @{ Name = 'start-server.sh'; SourcePath = $startShPath; UnixMode = 493 },
                @{ Name = 'server.json'; Bytes = $defaultConfigBytes; UnixMode = 420 },
                @{ Name = 'server.example.json'; Bytes = $defaultConfigBytes; UnixMode = 420 },
                @{ Name = 'README.txt'; SourcePath = $readmePath; UnixMode = 420 }
            )
            Write-Host "Packaged linux-x64 archive: $archivePath"
        }
    }
    elseif ($rid -eq 'win-x64') {
        $startCmdPath = Join-Path $outDir 'Start-MmaServer.cmd'
        Write-Utf8NoBom -Path $startCmdPath -Content $windowsStartCmd -NewLine "`r`n"

        $firewallPath = Join-Path $outDir 'Open-FirewallPorts.ps1'
        Write-Utf8NoBom -Path $firewallPath -Content $windowsFirewallScript -NewLine "`r`n"

        $firewallCmdPath = Join-Path $outDir 'Open-FirewallPorts.cmd'
        Write-Utf8NoBom -Path $firewallCmdPath -Content $windowsFirewallCmd -NewLine "`r`n"

        $readmePath = Join-Path $outDir 'README.txt'
        Write-Utf8NoBom -Path $readmePath -Content $windowsReadmeTxt -NewLine "`r`n"

        if (-not $SkipArchive) {
            New-Item -ItemType Directory -Path $distDir -Force | Out-Null
            $archivePath = Join-Path $distDir 'megamanarena-server-win-x64.zip'
            New-WindowsArchive -ArchivePath $archivePath -RootFolderName 'megamanarena-server-win-x64' -Entries @(
                @{ Name = 'Mma.Server.exe'; SourcePath = $binary },
                @{ Name = 'server.json'; Bytes = $defaultConfigBytesCrlf },
                @{ Name = 'server.example.json'; Bytes = $defaultConfigBytesCrlf },
                @{ Name = 'Start-MmaServer.cmd'; SourcePath = $startCmdPath },
                @{ Name = 'Open-FirewallPorts.ps1'; SourcePath = $firewallPath },
                @{ Name = 'Open-FirewallPorts.cmd'; SourcePath = $firewallCmdPath },
                @{ Name = 'README.txt'; SourcePath = $readmePath }
            )
            Write-Host "Packaged win-x64 archive: $archivePath"
        }
    }

    Write-Host "Published $rid server bundle: $binary"
}

if (-not $SkipArchive -and (Test-Path -LiteralPath $distDir -PathType Container)) {
    $archives = Get-ChildItem -LiteralPath $distDir -File |
        Where-Object { $_.Name -like '*.zip' -or $_.Name -like '*.tar.gz' } |
        Sort-Object -Property Name

    if ($archives.Count -gt 0) {
        $lines = foreach ($archive in $archives) {
            $hash = (Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $($archive.Name)"
        }
        $sumsPath = Join-Path $distDir 'SHA256SUMS.txt'
        Write-Utf8NoBom -Path $sumsPath -Content (($lines -join "`n") + "`n") -NewLine "`n"
        Write-Host "Wrote checksums: $sumsPath"
    }
}