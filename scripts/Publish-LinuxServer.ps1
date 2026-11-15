[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'Publish-Server.ps1') -Runtime 'linux-x64' -Configuration $Configuration
