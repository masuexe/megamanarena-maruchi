[CmdletBinding()]
param(
    [ValidateRange(1, 65535)]
    [int]$Port = 6520,
    [ValidateRange(2, 64)]
    [int]$MaxPlayers = 8,
    [ValidateSet('Arena', 'Teams', 'Squads', 'Boss', 'Elevator', 'Stage')]
    [string]$Mode = 'Arena',
    [switch]$Build
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$binary = Join-Path $repo 'local\publish\linux-x64\Mma.Server'

if ($Build -or -not (Test-Path -LiteralPath $binary -PathType Leaf)) {
    & (Join-Path $PSScriptRoot 'Publish-LinuxServer.ps1')
}

$fullPath = (Get-Item -LiteralPath $binary).FullName
if ($fullPath -notmatch '^([A-Za-z]):\\(.*)$') {
    throw "Cannot convert path '$fullPath' to WSL mount path."
}
$drive = $Matches[1].ToLowerInvariant()
$rest = $Matches[2] -replace '\\', '/'
$wslSource = "/mnt/$drive/$rest"

$stageScript = (@"
set -e
mkdir -p /tmp/mma-server
cp -f '$wslSource' /tmp/mma-server/Mma.Server
chmod +x /tmp/mma-server/Mma.Server
"@ -replace "`r", '')

$stageScript | & wsl -d Ubuntu -- bash -s
if ($LASTEXITCODE -ne 0) {
    throw "Failed to stage Mma.Server inside WSL2 (exit code $LASTEXITCODE)."
}

Write-Host "Starting Linux server in WSL2 (Ubuntu) on UDP $Port (Mode=$Mode, MaxPlayers=$MaxPlayers)..."
& wsl -d Ubuntu -- /tmp/mma-server/Mma.Server --port $Port --max-players $MaxPlayers --mode $Mode
exit $LASTEXITCODE
