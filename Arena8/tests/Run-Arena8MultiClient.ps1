param(
    [int]$Clients = 8,
    [int]$Port = 6528,
    [ValidateSet('Arena8','Teams','Squads','Boss','Elevator','Stage')]
    [string]$Mode = 'Arena8',
    [int]$TimeoutSec = 120,
    [switch]$Rebuild,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $repo 'scripts\Resolve-MmaPaths.ps1')
$qaDir = Join-Path $repo 'local\qa'
New-Item -ItemType Directory -Path $qaDir -Force | Out-Null

$baseClientWin = Join-Path $repo 'local\build\Arena8\data.client8.win'
if (!(Test-Path -LiteralPath $baseClientWin -PathType Leaf)) {
    throw "Missing $baseClientWin. Run .\Arena8\Build-Arena8.ps1 first."
}

$qaRuntime = Join-Path $qaDir 'arena8-qa'
$qaWin = Join-Path $qaRuntime 'data.client8.qa.win'
$testPatch = Join-Path $repo 'Arena8\TestClient.csx'
$needsBuild = $Rebuild -or
    !(Test-Path -LiteralPath $qaWin -PathType Leaf) -or
    ((Get-Item $testPatch).LastWriteTimeUtc -gt (Get-Item $qaWin).LastWriteTimeUtc) -or
    ((Get-Item $baseClientWin).LastWriteTimeUtc -gt (Get-Item $qaWin).LastWriteTimeUtc)

if ($needsBuild) {
    New-Item -ItemType Directory -Path $qaRuntime -Force | Out-Null
    $patcher = Get-UndertaleModCli
    $temp = Join-Path $qaRuntime ('qa-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $qaPatch = Join-Path $qaRuntime 'QaMultiClient.csx'
    $testContent = Get-Content -LiteralPath $testPatch -Raw
    $portOverride = @'
for (var qaPortArg = 1; qaPortArg <= parameter_count(); qaPortArg++)
{
    var qaFlag = parameter_string(qaPortArg);
    if (string_pos(""-mma-client-port="", qaFlag) == 1)
        global.defaultClientPort = real(string_delete(qaFlag, 1, 17));
}
var popup = instance_create(0, 0, objDirectIPPopup);
'@
    $testContent = $testContent.Replace('var popup = instance_create(0, 0, objDirectIPPopup);', $portOverride)
    Set-Content -LiteralPath $qaPatch -Value $testContent -Encoding UTF8
    Write-Host 'Patching data.client8.win with Arena8\TestClient.csx + deterministic client port...'
    try {
        @() | & $patcher load $baseClientWin -s $qaPatch -o $temp
        if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $temp)) {
            throw 'UndertaleModCli failed while applying Arena8\TestClient.csx.'
        }
        Move-Item -LiteralPath $temp -Destination $qaWin -Force
    }
    finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp }
    }
}

$gameDir = Get-MmaGameDir
Initialize-MmaRuntimeDir `
    -GameDir $gameDir `
    -RuntimeDir $qaRuntime `
    -ExeSource (Join-Path $gameDir 'MegaManArena.exe') `
    -ExeDestName 'MegaManArena8QA.exe' `
    -DataWinSource $qaWin

$qaExe = Join-Path $qaRuntime 'MegaManArena8QA.exe'
$srvExe = Join-Path $repo 'local\publish\win-x64\Mma.Server.exe'
if (!(Test-Path -LiteralPath $srvExe -PathType Leaf)) {
    throw "Missing $srvExe. Run .\scripts\Publish-WindowsServer.ps1 first."
}

function Read-SharedText([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    $fs = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try {
        $sr = [IO.StreamReader]::new($fs)
        try { return $sr.ReadToEnd() } finally { $sr.Dispose() }
    } finally {
        $fs.Dispose()
    }
}

$srvLog = Join-Path $qaDir 'qa8-srv.log'
Remove-Item -Force $srvLog -ErrorAction SilentlyContinue

$srvProc = $null
$clientProcs = @()

try {
    Write-Host "Starting Mma.Server on UDP $Port ($Mode / $Clients players)..."
    $srvProc = Start-Process -FilePath $srvExe `
        -ArgumentList @('--port', $Port, '--max-players', $Clients, '--mode', $Mode) `
        -WorkingDirectory (Split-Path $srvExe) `
        -RedirectStandardOutput $srvLog `
        -WindowStyle Hidden `
        -PassThru
    Start-Sleep -Milliseconds 600
    if ($srvProc.HasExited) {
        throw "Mma.Server exited early ($($srvProc.ExitCode)). Check $srvLog"
    }

    for ($i = 1; $i -le $Clients; $i++) {
        $appData = Join-Path $qaDir "appdata-qa-p$i"
        $log = Join-Path $qaDir "qa8-p$i.log"
        Remove-Item -Force $log -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Path $appData -Force | Out-Null

        $clientPort = $Port + 100 + ($i * 2)
        $psi = [System.Diagnostics.ProcessStartInfo]::new($qaExe, "-output `"$log`" -mma-client-port=$clientPort")
        $psi.WorkingDirectory = $qaRuntime
        $psi.UseShellExecute = $false
        $psi.EnvironmentVariables['LOCALAPPDATA'] = $appData
        $clientProcs += [System.Diagnostics.Process]::Start($psi)
        Write-Host "Launched QA Client #$i (PID $($clientProcs[-1].Id))"
        Start-Sleep -Milliseconds 350
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $allAdvanced = $false
    while ($KeepRunning -or $sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        Start-Sleep -Seconds 2
        $lobbies = 0
        $advanced = 0
        for ($i = 1; $i -le $Clients; $i++) {
            $txt = Read-SharedText (Join-Path $qaDir "qa8-p$i.log")
            if ($txt -match 'Arena8 QA lobby ') { $lobbies++ }
            if ($txt -match 'Arena8 QA round ') { $advanced++ }
        }
        Write-Host ("[t={0:F1}s] Lobby={1}/{2}, AdvancedFrame265={3}/{2}" -f $sw.Elapsed.TotalSeconds, $lobbies, $Clients, $advanced)

        if (-not $KeepRunning -and $advanced -eq $Clients) {
            $allAdvanced = $true
            break
        }

        $exited = @($clientProcs | Where-Object { $_.HasExited })
        if ($exited.Count -gt 0) {
            Write-Warning "$($exited.Count) client(s) exited."
            if (-not $KeepRunning) { break }
        }
    }

    if (-not $KeepRunning) {
        Write-Host "Done in $([Math]::Round($sw.Elapsed.TotalSeconds, 1))s. AllAdvanced=$allAdvanced. Logs: local\qa\qa8-p*.log"
    }
}
finally {
    foreach ($p in $clientProcs) {
        try { if ($p -and !$p.HasExited) { $p.Kill() } } catch {}
    }
    try { if ($srvProc -and !$srvProc.HasExited) { $srvProc.Kill() } } catch {}
}
