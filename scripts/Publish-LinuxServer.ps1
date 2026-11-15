$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\Mma.Server\Mma.Server.csproj'
$outDir = Join-Path $repo 'local\publish\linux-x64'

if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "Server project not found at '$project'."
}

New-Item -ItemType Directory -Path $outDir -Force | Out-Null

& dotnet publish $project `
    -c Release `
    -r linux-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -o $outDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$binary = Join-Path $outDir 'Mma.Server'
if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) {
    throw "Published binary not found at '$binary'."
}

Write-Host "Published Linux server binary: $binary"
