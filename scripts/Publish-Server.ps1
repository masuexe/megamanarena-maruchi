[CmdletBinding()]
param(
    [ValidateSet('all', 'linux-x64', 'win-x64')]
    [string]$Runtime = 'all',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\Mma.Server\Mma.Server.csproj'

if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "Server project not found at '$project'."
}

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Content,
        [Parameter(Mandatory = $true)]
        [string]$NewLine
    )

    $normalized = ($Content -replace "`r`n", "`n" -replace "`r", "`n") -replace "`n", $NewLine
    [System.IO.File]::WriteAllText($Path, $normalized, $utf8NoBom)
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

$windowsFirewallScript = @'
[CmdletBinding()]
param(
    [ValidateRange(1, 65534)]
    [int]$Port = 6520,
    [string]$RuleName = 'Mega Man Arena Headless Server'
)

$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Administrator privileges are required to modify Windows Firewall rules. Re-run PowerShell as Administrator and retry.'
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

$targets = if ($Runtime -eq 'all') { @('linux-x64', 'win-x64') } else { @($Runtime) }

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
    }
    elseif ($rid -eq 'win-x64') {
        $startCmdPath = Join-Path $outDir 'Start-MmaServer.cmd'
        Write-Utf8NoBom -Path $startCmdPath -Content $windowsStartCmd -NewLine "`r`n"

        $firewallPath = Join-Path $outDir 'Open-FirewallPorts.ps1'
        Write-Utf8NoBom -Path $firewallPath -Content $windowsFirewallScript -NewLine "`r`n"
    }

    Write-Host "Published $rid server bundle: $binary"
}
