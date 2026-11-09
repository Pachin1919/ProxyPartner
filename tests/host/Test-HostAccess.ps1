#Requires -Version 7.3
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('pipe-handshake','fallback-auth','readonly-routes','no-secrets')][string]$Check,
    [Parameter(Mandatory)][string]$ToolchainRoot,
    [Parameter(Mandatory)][string]$EvidenceDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64' -or
    -not [IO.Path]::IsPathFullyQualified($ToolchainRoot) -or -not [IO.Path]::IsPathFullyQualified($EvidenceDirectory)) {
    throw 'Use x64 PowerShell on Windows and absolute toolchain and evidence paths.'
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$run = Join-Path $EvidenceDirectory $Check
if (Test-Path -LiteralPath $run) { throw "Use a new evidence directory. $run already exists." }
$tree = Join-Path $run 'work'
$test = Join-Path $tree 'tests/host'
$source = Join-Path $tree 'src/ProxyPartnerLite'
New-Item -ItemType Directory -Path $test,$source -Force | Out-Null
$log = Join-Path $run 'run.log'
$result = [ordered]@{ check = $Check; status = 'FAIL'; exit_code = 1; head_sha = $null
    started_utc = [DateTime]::UtcNow.ToString('o') }

function Invoke-Recorded([string]$File, [string[]]$Arguments) {
    @("CWD=$PWD", "COMMAND=$File $($Arguments -join ' ')") | Add-Content -LiteralPath $log
    $output = @(& $File @Arguments 2>&1)
    $code = $LASTEXITCODE
    $output | ForEach-Object { "$_" } | Add-Content -LiteralPath $log
    "EXIT_CODE=$code" | Add-Content -LiteralPath $log
    if ($code -ne 0) {
        if ($File -eq $exe -and $code -eq 2) {
            $result.status = 'BLOCKED'
            $result.exit_code = 2
        }
        throw "Command exited $code. See run.log."
    }
    return $output
}

$previous = Get-Location
try {
    $result.head_sha = [string](Invoke-Recorded git @('-C', $repo, 'rev-parse', 'HEAD'))
    Invoke-Recorded git @('-C', $repo, 'status', '--short') | Out-Null
    $lock = Get-Content -LiteralPath (Join-Path $repo 'contracts/windows-toolchain.json') -Raw | ConvertFrom-Json
    $toolchain = (Resolve-Path -LiteralPath $ToolchainRoot).Path
    $sdk = Join-Path $toolchain "dotnet-$($lock.dotnet.sdk)-$($lock.rid)"
    $dotnet = Join-Path $sdk 'dotnet.exe'
    $native = Join-Path $toolchain 'native'
    $linker = Join-Path $native 'bin/link.exe'
    $out = Join-Path $run 'publish'
    $exe = Join-Path $out 'ProxyPartnerLite.Host.Tests.exe'

    foreach ($dir in @('Discovery', 'Host')) {
        $target = New-Item -ItemType Directory -Path (Join-Path $source $dir)
        Get-ChildItem -LiteralPath (Join-Path $repo "src/ProxyPartnerLite/$dir") -File |
            Where-Object Extension -In @('.cs','.csproj') | Copy-Item -Destination $target.FullName
    }
    Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object Extension -In @('.cs','.csproj') |
        Copy-Item -Destination $test
    @{ sdk = @{ version = $lock.dotnet.sdk; rollForward = 'disable' } } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $tree 'global.json') -Encoding utf8NoBOM
    '<configuration><packageSources><clear /></packageSources></configuration>' |
        Set-Content -LiteralPath (Join-Path $test 'NuGet.Config') -Encoding utf8NoBOM

    $env:DOTNET_ROOT = $sdk
    $env:DOTNET_ROOT_X64 = $sdk
    $env:DOTNET_CLI_HOME = Join-Path $run 'dotnet-home'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    $env:APPDATA = Join-Path $run 'roaming'
    $env:LOCALAPPDATA = Join-Path $run 'local-appdata'
    $env:NUGET_PACKAGES = Join-Path $run 'nuget-packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $run 'nuget-http-cache'
    $env:NUGET_PLUGINS_CACHE_PATH = Join-Path $run 'nuget-plugins-cache'
    $env:NUGET_SCRATCH = Join-Path $run 'nuget-scratch'
    $env:TEMP = Join-Path $run 'temp'
    $env:TMP = $env:TEMP
    $env:LIB = (@('msvc', 'um', 'ucrt') | ForEach-Object { Join-Path $native "lib/$_" }) -join ';'
    $env:PATH = "$(Join-Path $native 'bin');$env:PATH"
    New-Item -ItemType Directory -Path $env:TEMP,$env:APPDATA,$env:LOCALAPPDATA -Force | Out-Null

    Set-Location -LiteralPath $test
    if ([string](Invoke-Recorded $dotnet @('--version')) -cne $lock.dotnet.sdk) { throw 'SDK version mismatch.' }
    $project = 'ProxyPartnerLite.Host.Tests.csproj'
    Invoke-Recorded $dotnet @('restore', $project, '--configfile', 'NuGet.Config', '--source',
        (Join-Path $toolchain 'nuget-packages'), '--disable-build-servers') | Out-Null
    Invoke-Recorded $dotnet @('publish', $project, '-c', 'Release', '--no-restore', '--disable-build-servers',
        '-p:DebugType=none', '-p:IlcUseEnvironmentalTools=true', "-p:CppLinker=$linker", '-o', $out) | Out-Null
    Set-Location -LiteralPath $run
    $result.exe_sha256 = (Get-FileHash -LiteralPath $exe).Hash
    $result.diagnostics = @(Invoke-Recorded $exe @($Check))
    $result.status = 'PASS'
    $result.exit_code = 0
} catch {
    $result.error = $_.Exception.Message
} finally {
    Set-Location -LiteralPath $previous
    $result.ended_utc = [DateTime]::UtcNow.ToString('o')
    $result.log_sha256 = (Get-FileHash -LiteralPath $log).Hash
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $run 'result.json') -Encoding utf8NoBOM
}
$result | ConvertTo-Json -Depth 6
exit $result.exit_code
