Set-StrictMode -Version Latest

function Get-MmaRepoRoot {
    return Split-Path -Parent $PSScriptRoot
}

function Get-MmaGameDir {
    $repo = Get-MmaRepoRoot
    $gameDir = if (-not [string]::IsNullOrWhiteSpace($env:MMA_GAME_DIR)) {
        $env:MMA_GAME_DIR
    } else {
        Join-Path $repo 'local\game'
    }

    $dataWin = Join-Path $gameDir 'data.win'
    $zipPath = Join-Path $repo 'local\MegaManArena_v5.0.0.zip'
    if ([string]::IsNullOrWhiteSpace($env:MMA_GAME_DIR) -and
        -not (Test-Path -LiteralPath $dataWin -PathType Leaf) -and
        (Test-Path -LiteralPath $zipPath -PathType Leaf)) {
        Write-Host "Extracting MegaManArena_v5.0.0.zip to $gameDir..."
        New-Item -ItemType Directory -Path $gameDir -Force | Out-Null
        Expand-Archive -LiteralPath $zipPath -DestinationPath $gameDir -Force
    }

    $exePath = Join-Path $gameDir 'MegaManArena.exe'
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $dataWin -PathType Leaf)) {
        throw "Original Mega Man Arena 5.0 files were not found in '$gameDir'. Place MegaManArena_v5.0.0.zip in local\ or set `$env:MMA_GAME_DIR."
    }

    return (Get-Item -LiteralPath $gameDir).FullName
}

function Assert-MmaStockDataWin {
    param(
        [Parameter(Mandatory)]
        [string]$DataWinPath
    )

    if (-not (Test-Path -LiteralPath $DataWinPath -PathType Leaf)) {
        throw "Missing data.win at '$DataWinPath'."
    }

    $expected = 'D238464E652DB8C1D722C38BF1F79228275523C7B75748D555708C09671BD868'
    $actual = (Get-FileHash -LiteralPath $DataWinPath -Algorithm SHA256).Hash
    if ($actual -ne $expected) {
        throw "SHA-256 mismatch for '$DataWinPath'. Expected $expected (Mega Man Arena 5.0), got $actual."
    }
}

function Get-UndertaleModCli {
    param(
        [string]$ExplicitPath = ''
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (-not (Test-Path -LiteralPath $ExplicitPath -PathType Leaf)) {
            throw "UndertaleModCli.exe not found at '$ExplicitPath'."
        }
        return (Get-Item -LiteralPath $ExplicitPath).FullName
    }

    if (-not [string]::IsNullOrWhiteSpace($env:UTMT_CLI) -and
        (Test-Path -LiteralPath $env:UTMT_CLI -PathType Leaf)) {
        return (Get-Item -LiteralPath $env:UTMT_CLI).FullName
    }

    $repo = Get-MmaRepoRoot
    $toolsDir = Join-Path $repo 'local\tools\UTMT_CLI_v0.9.2.0'
    if (Test-Path -LiteralPath $toolsDir -PathType Container) {
        $found = Get-ChildItem -LiteralPath $toolsDir -Recurse -Filter 'UndertaleModCli.exe' -File -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($found) {
            return $found.FullName
        }
    }

    $cmd = Get-Command 'UndertaleModCli.exe' -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    throw "UndertaleModCli.exe not found. Run .\scripts\Setup-DevEnv.ps1 or pass -UndertaleModCli <path>."
}

function Initialize-MmaRuntimeDir {
    param(
        [Parameter(Mandatory)]
        [string]$GameDir,
        [Parameter(Mandatory)]
        [string]$RuntimeDir,
        [Parameter(Mandatory)]
        [string]$ExeSource,
        [Parameter(Mandatory)]
        [string]$ExeDestName,
        [Parameter(Mandatory)]
        [string]$DataWinSource
    )

    New-Item -ItemType Directory -Path $RuntimeDir -Force | Out-Null

    foreach ($name in 'audiogroupSFX.dat', 'gme.dll', 'libxprocess_x64.dll', 'options.ini', 'rousrDissonance.dll') {
        $src = Join-Path $GameDir $name
        $dest = Join-Path $RuntimeDir $name
        if ((Test-Path -LiteralPath $src -PathType Leaf) -and -not (Test-Path -LiteralPath $dest)) {
            Copy-Item -LiteralPath $src -Destination $dest -Force
        }
    }

    foreach ($dirName in 'Music', 'SFX') {
        $srcDir = Join-Path $GameDir $dirName
        $destDir = Join-Path $RuntimeDir $dirName
        if ((Test-Path -LiteralPath $srcDir -PathType Container) -and -not (Test-Path -LiteralPath $destDir)) {
            New-Item -ItemType Junction -Path $destDir -Target $srcDir | Out-Null
        }
    }

    Copy-Item -LiteralPath $ExeSource -Destination (Join-Path $RuntimeDir $ExeDestName) -Force
    Copy-Item -LiteralPath $DataWinSource -Destination (Join-Path $RuntimeDir 'data.win') -Force
}
