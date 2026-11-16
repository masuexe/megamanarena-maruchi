param(
    [Parameter(Mandatory)]
    [ValidateSet('Server','Client','Stop')]
    [string]$Role,
    [ValidateSet('Arena8','Teams','Squads','Boss','Elevator','Stage')]
    [string]$Mode = 'Arena8'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $repo 'scripts\Resolve-MmaPaths.ps1')

$isClient = $Role -eq 'Client'
$kind = if ($isClient) { 'client' } else { 'server' }
$runtime = Join-Path $repo "local\runtime\Arena8-$kind"
$exe = Join-Path $runtime "MegaManArena8$kind.exe"
$running = @(Get-Process -Name "MegaManArena8$kind" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $exe })

if ($Role -eq 'Stop') {
    if ($running.Count -gt 0) { $running | Stop-Process }
    Write-Host 'Arena 8 server stopped.'
    return
}
if ($running.Count -gt 0) {
    Write-Host "Arena 8 $kind is already running."
    return
}

$data = Join-Path $repo "local\build\Arena8\data.${kind}8.win"
if (!(Test-Path -LiteralPath $data -PathType Leaf)) {
    throw "Missing $data. Run .\Arena8\Build-Arena8.ps1 first."
}
if (!$isClient) {
    $probe = New-Object System.Net.Sockets.UdpClient
    try {
        $probe.ExclusiveAddressUse = $true
        $probe.Client.Bind([System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 6528))
    } finally {
        $probe.Dispose()
    }
}

$gameDir = Get-MmaGameDir
Initialize-MmaRuntimeDir `
    -GameDir $gameDir `
    -RuntimeDir $runtime `
    -ExeSource (Join-Path $gameDir 'MegaManArena.exe') `
    -ExeDestName "MegaManArena8$kind.exe" `
    -DataWinSource $data

$log = Join-Path $runtime ('arena8-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.log')
$arguments = @('-output', ('"' + $log + '"'))
if ($isClient) {
    Add-Type -AssemblyName System.Windows.Forms
    $screen = [Windows.Forms.Screen]::PrimaryScreen
    if ($screen) {
        $area = $screen.WorkingArea
        $border = [Windows.Forms.SystemInformation]::FrameBorderSize
        $paddingWidth = [Math]::Max(16, 2 * $border.Width + 8)
        $paddingHeight = [Math]::Max(48, 2 * $border.Height + [Windows.Forms.SystemInformation]::CaptionHeight + 8)
        $arguments += "-arena8-work-area=$($area.X),$($area.Y),$($area.Width),$($area.Height),$paddingWidth,$paddingHeight"
    }
    Start-Process -FilePath $exe -WorkingDirectory $runtime -ArgumentList $arguments
    return
}

$Mode = 'Arena8','Teams','Squads','Boss','Elevator','Stage' | Where-Object { $_ -eq $Mode }
$arguments += @("-mma-mode=$Mode", '-mma-players=8')
$runner = Start-Process -FilePath $exe -WorkingDirectory $runtime -ArgumentList $arguments -WindowStyle Hidden -PassThru
for ($i = 0; $i -lt 80; $i++) {
    if ((Test-Path -LiteralPath $log) -and
        (Select-String -LiteralPath $log -SimpleMatch "MMA Dedicated: lobby ready (UDP 6528) $Mode / 8 players" -Quiet)) {
        Write-Host "Arena 8 is running on UDP 6528: $Mode / 8 players (PID $($runner.Id))."
        Write-Host "Log: $log"
        return
    }
    if ($runner.WaitForExit(250)) { break }
}
if (!$runner.HasExited) { Stop-Process -InputObject $runner }
throw "Arena 8 could not start. Check $log"
