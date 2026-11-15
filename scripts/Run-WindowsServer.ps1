[CmdletBinding()]
param(
    [ValidateRange(1, 65534)]
    [int]$Port = 6520,
    [ValidateRange(2, 64)]
    [int]$MaxPlayers = 4,
    [ValidateSet('Arena', 'Teams', 'Squads', 'Boss', 'Elevator', 'Stage', 'Arena8')]
    [string]$Mode = 'Arena',
    [string]$Stage,
    [ValidateRange(0, 12)]
    [Nullable[int]]$Buffer = $null,
    [switch]$Build
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$binary = Join-Path $repo 'local\publish\win-x64\Mma.Server.exe'

if ($Build -or -not (Test-Path -LiteralPath $binary -PathType Leaf)) {
    & (Join-Path $PSScriptRoot 'Publish-Server.ps1') -Runtime 'win-x64'
}

$serverArgs = [System.Collections.Generic.List[string]]::new()
$serverArgs.Add('--port')
$serverArgs.Add([string]$Port)
$serverArgs.Add('--max-players')
$serverArgs.Add([string]$MaxPlayers)
$serverArgs.Add('--mode')
$serverArgs.Add($Mode)

if (-not [string]::IsNullOrWhiteSpace($Stage)) {
    $serverArgs.Add('--stage')
    $serverArgs.Add($Stage.Trim())
}

if ($null -ne $Buffer) {
    $serverArgs.Add('--buffer')
    $serverArgs.Add([string]$Buffer.Value)
}

Write-Host "Starting Windows headless server on UDP $Port/$($Port + 1) (Mode=$Mode, MaxPlayers=$MaxPlayers)..."
& $binary @serverArgs
exit $LASTEXITCODE
