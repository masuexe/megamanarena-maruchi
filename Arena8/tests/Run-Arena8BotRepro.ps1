param(
    [string]$ServerIp = '127.0.0.1',
    [int]$Port = 6528,
    [ValidateSet('Arena8','Teams','Squads','Boss','Elevator','Stage')]
    [string]$Mode = 'Arena8',
    [int]$Bots = 7,
    [int]$LagBots = 0,
    [int]$DelayMs = 0,
    [double]$DropRate = 0.0,
    [double]$StallEverySec = 0.0,
    [int]$StallDurationMs = 400,
    [switch]$ActiveInputs,
    [switch]$NoServer,
    [switch]$NoClient
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $repo 'scripts\Resolve-MmaPaths.ps1')
$qaDir = Join-Path $repo 'local\qa'
New-Item -ItemType Directory -Path $qaDir -Force | Out-Null

$botProj = Join-Path $PSScriptRoot 'Arena8BotRepro\Arena8BotRepro.csproj'
$botExe = Join-Path $PSScriptRoot 'Arena8BotRepro\bin\Release\net10.0\Arena8BotRepro.exe'
if (!(Test-Path -LiteralPath $botExe -PathType Leaf)) {
    Write-Host 'Building Arena8BotRepro...'
    & dotnet build $botProj -c Release -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Failed to build Arena8BotRepro.' }
}

$srvProc = $null
$botProc = $null
$clientProc = $null

try {
    if (-not $NoServer) {
        $srvExe = Join-Path $repo 'local\publish\win-x64\Mma.Server.exe'
        if (!(Test-Path -LiteralPath $srvExe -PathType Leaf)) {
            throw "Missing $srvExe. Run .\scripts\Publish-WindowsServer.ps1 first."
        }
        $srvLog = Join-Path $qaDir 'bot-repro-srv.log'
        Write-Host "Starting Mma.Server on UDP $Port ($Mode / 8 players)..."
        $srvProc = Start-Process -FilePath $srvExe `
            -ArgumentList @('--port', $Port, '--max-players', '8', '--mode', $Mode) `
            -WorkingDirectory (Split-Path $srvExe) `
            -RedirectStandardOutput $srvLog `
            -WindowStyle Hidden `
            -PassThru
        Start-Sleep -Milliseconds 600
        if ($srvProc.HasExited) {
            throw "Mma.Server exited early ($($srvProc.ExitCode)). Check $srvLog"
        }
    }

    $botArgs = @(
        '--host', $ServerIp,
        '--port', $Port,
        '--bots', $Bots,
        '--lag-bots', $LagBots,
        '--delay-ms', $DelayMs,
        '--drop-rate', $DropRate,
        '--stall-every-sec', $StallEverySec,
        '--stall-duration-ms', $StallDurationMs
    )
    if ($ActiveInputs) { $botArgs += '--active-inputs' }

    Write-Host "Starting $Bots virtual bot(s)..."
    $botProc = Start-Process -FilePath $botExe `
        -ArgumentList $botArgs `
        -NoNewWindow `
        -PassThru
    Start-Sleep -Seconds 2

    if (-not $NoClient) {
        $runtime = Join-Path $repo 'local\runtime\Arena8-client'
        $clientExe = Join-Path $runtime 'MegaManArena8client.exe'
        if (!(Test-Path -LiteralPath $clientExe -PathType Leaf)) {
            $data = Join-Path $repo 'local\build\Arena8\data.client8.win'
            if (!(Test-Path -LiteralPath $data -PathType Leaf)) {
                throw "Missing $data. Run .\Arena8\Build-Arena8.ps1 first."
            }
            $gameDir = Get-MmaGameDir
            Initialize-MmaRuntimeDir `
                -GameDir $gameDir `
                -RuntimeDir $runtime `
                -ExeSource (Join-Path $gameDir 'MegaManArena.exe') `
                -ExeDestName 'MegaManArena8client.exe' `
                -DataWinSource $data
        }

        $clientAppData = Join-Path $qaDir 'appdata-botrepro-client'
        $clientLog = Join-Path $qaDir 'bot-repro-client.log'
        New-Item -ItemType Directory -Path $clientAppData -Force | Out-Null

        Write-Host "Launching 1 real Arena8 client (connect via Online -> Direct IP -> $ServerIp)..."
        $psi = [System.Diagnostics.ProcessStartInfo]::new($clientExe, "-output `"$clientLog`"")
        $psi.WorkingDirectory = $runtime
        $psi.UseShellExecute = $false
        $psi.EnvironmentVariables['LOCALAPPDATA'] = $clientAppData
        $clientProc = [System.Diagnostics.Process]::Start($psi)

        Write-Host 'Close the game window or press Ctrl+C to stop the reproduction session.'
        $clientProc.WaitForExit()
    } else {
        Write-Host 'Running headless bots only. Press Ctrl+C to stop.'
        $botProc.WaitForExit()
    }
}
finally {
    foreach ($p in @($clientProc, $botProc, $srvProc)) {
        try {
            if ($p -and !$p.HasExited) { $p.Kill() }
        } catch {}
    }
}
