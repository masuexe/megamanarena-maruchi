param([switch]$StopServer)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $repo 'scripts\Resolve-MmaPaths.ps1')

$runtime = Join-Path $repo 'local\runtime\DedicatedServer'
$exe = Join-Path $runtime 'MegaManArenaDedicated.exe'
$running = @(Get-Process -Name MegaManArenaDedicated -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $exe })

if ($StopServer) {
    if ($running.Count -eq 0) {
        Write-Host 'Server is not running.'
    } else {
        $running | Stop-Process
        Write-Host 'Server stopped.'
    }
    return
}
if ($running.Count -gt 0) {
    Write-Host 'Server is already running on UDP 6520. Stop it before changing the settings.'
    return
}

$config = Join-Path $PSScriptRoot 'server.json'
$mode = 'Arena'
$players = 2
if (Test-Path -LiteralPath $config -PathType Leaf) {
    $settings = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
    $modes = 'Arena', 'Teams', 'Squads', 'Boss', 'Elevator', 'Stage'
    if ($settings.Mode -isnot [string] -or $modes -notcontains $settings.Mode) {
        throw 'server.json: Mode must be Arena, Teams, Squads, Boss, Elevator, or Stage.'
    }
    if (($settings.Players -isnot [int] -and $settings.Players -isnot [long]) -or
        $settings.Players -notin 2,3,4) {
        throw 'server.json: Players must be 2, 3, or 4.'
    }
    $mode = $modes | Where-Object { $_ -eq $settings.Mode }
    $players = $settings.Players
}

$serverData = Join-Path $repo 'local\build\DedicatedServer\data.server.win'
if (!(Test-Path -LiteralPath $serverData -PathType Leaf)) {
    throw 'Missing local\build\DedicatedServer\data.server.win. Run .\DedicatedServer\Build-DedicatedServer.ps1 first.'
}

$socket = New-Object System.Net.Sockets.UdpClient
try {
    $socket.ExclusiveAddressUse = $true
    $socket.Client.Bind([System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 6520))
} catch {
    throw 'UDP 6520 is in use. Close any other Mega Man Arena host first.'
} finally {
    $socket.Dispose()
}

$gameDir = Get-MmaGameDir
Initialize-MmaRuntimeDir `
    -GameDir $gameDir `
    -RuntimeDir $runtime `
    -ExeSource (Join-Path $gameDir 'MegaManArena.exe') `
    -ExeDestName 'MegaManArenaDedicated.exe' `
    -DataWinSource $serverData

$log = Join-Path $runtime ('server-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.log')
$runner = Start-Process -FilePath $exe -WorkingDirectory $runtime -ArgumentList @(
    '-output', ('"' + $log + '"'), "-mma-mode=$mode", "-mma-players=$players"
) -WindowStyle Hidden -PassThru
$readyMessage = "MMA Dedicated: lobby ready (UDP 6520) $mode / $players players"
$started = $false
for ($i = 0; $i -lt 60; $i++) {
    if ((Test-Path -LiteralPath $log) -and
            (Select-String -LiteralPath $log -SimpleMatch $readyMessage -Quiet)) {
        $started = $true
        break
    }
    if ($runner.WaitForExit(250)) { break }
}
if (!$started) {
    if (!$runner.HasExited) { $runner | Stop-Process }
    throw "Server could not start. Check $log"
}

Write-Host "Mega Man Arena server is running: $mode / $players players (PID $($runner.Id))."
Write-Host 'Players: Online > Direct IP, UDP 6520.'
Write-Host "Log: $log"
Write-Host 'Use Stop Server.cmd to close the lobby.'
