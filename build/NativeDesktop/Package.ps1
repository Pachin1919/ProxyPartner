#Requires -Version 7.3
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CliDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$ToolchainRoot = 'F:/PachinStudio/ProxyPartner/runs/toolchain',
    [string]$HeaderRoot = 'F:/PachinStudio/ProxyPartner/runs/coordination/standalone-kjx/backend/next/cpp-anytls'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (git -C $repo status --porcelain) { throw 'Package from a clean candidate worktree.' }
& (Join-Path $PSScriptRoot 'Build.ps1') -OutputDirectory $OutputDirectory -CliDirectory $CliDirectory `
    -ToolchainRoot $ToolchainRoot -HeaderRoot $HeaderRoot -Tests
if ($LASTEXITCODE) { exit $LASTEXITCODE }
$artifact = Get-Content (Join-Path $OutputDirectory 'artifact.json') -Raw | ConvertFrom-Json
$publish = Split-Path -Parent $artifact.exe
$zip = Join-Path $OutputDirectory 'ProxyPartner-native-windows-x64.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($publish,$zip)
[ordered]@{head_sha=$artifact.head_sha;zip=$zip;sha256=(Get-FileHash -LiteralPath $zip).Hash.ToLowerInvariant()
    desktop_sha256=(Get-FileHash -LiteralPath $artifact.exe).Hash.ToLowerInvariant()
    cli_sha256=(Get-FileHash -LiteralPath (Join-Path $publish 'ProxyPartner.Cli.exe')).Hash.ToLowerInvariant()} |
    ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'package.json')
$zip
