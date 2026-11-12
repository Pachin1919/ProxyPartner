#Requires -Version 7.3
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ToolchainRoot,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [ValidateSet('Win32','Avalonia')][string]$Variant = 'Win32',
    [string]$TargetIcons,
    [string]$OpenSslBin,
    [string]$ProbeCaBundle
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repo = Split-Path -Parent $PSScriptRoot
if ([bool]$OpenSslBin -ne [bool]$ProbeCaBundle -or ($OpenSslBin -and $Variant -ne 'Win32')) {
    throw 'Native probes require both OpenSslBin and ProbeCaBundle with the Win32 build.'
}
if ($OpenSslBin) {
    $OpenSslBin = (Resolve-Path -LiteralPath $OpenSslBin).Path
    $ProbeCaBundle = (Resolve-Path -LiteralPath $ProbeCaBundle).Path
}
$lock = Get-Content -Raw -LiteralPath (Join-Path $repo 'contracts/windows-toolchain.json') | ConvertFrom-Json
$sdk = Join-Path $ToolchainRoot "dotnet-$($lock.dotnet.sdk)-$($lock.rid)"
$dotnet = Join-Path $sdk 'dotnet.exe'
$native = Join-Path $ToolchainRoot 'native'
$linker = Join-Path $native 'bin/link.exe'
$feed = Join-Path $ToolchainRoot 'nuget-packages'
if ($Variant -eq 'Win32') {
    foreach ($package in $lock.dotnet.nuget_packages | Where-Object id -in 'quickproxynet','yamldotnet') {
        $file = Join-Path $feed "$($package.id).$($package.version).nupkg"
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $package.sha256) {
            throw "Probe package checksum mismatch: $($package.id)"
        }
    }
}
$out = Join-Path $EvidenceDirectory $Variant.ToLowerInvariant()
if (Test-Path -LiteralPath $out) { throw "Evidence already exists: $out" }
$null = New-Item -ItemType Directory -Path $out
$project = if ($Variant -eq 'Win32') { 'build/DesktopBaseline/DesktopBaseline.csproj' } else { 'tests/ui/AvaloniaBaseline/AvaloniaBaseline.csproj' }
$project = Join-Path $repo $project
$publish = Join-Path $out 'publish'
$env:DOTNET_ROOT = $sdk
$env:DOTNET_ROOT_X64 = $sdk
$cache = Join-Path $ToolchainRoot 'cache'
$env:DOTNET_CLI_HOME = Join-Path $cache 'dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:NUGET_PACKAGES = Join-Path $cache 'nuget-packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $cache 'nuget-http-cache'
$env:PATH = "$(Join-Path $native 'bin');$env:PATH"
$env:LIB = @('msvc', 'um', 'ucrt' | ForEach-Object { Join-Path $native "lib/$_" }) -join ';'

function Invoke-Recorded([string]$Stage, [string[]]$Arguments) {
    $start = [DateTime]::UtcNow.ToString('o')
    & $dotnet @Arguments *> (Join-Path $out "$Stage.log")
    $exitCode = $LASTEXITCODE
    [ordered]@{ exe=$dotnet; args=$Arguments; cwd=$PWD.Path; started_utc=$start
        dotnet_cli_home=$env:DOTNET_CLI_HOME; nuget_packages=$env:NUGET_PACKAGES
        nuget_http_cache=$env:NUGET_HTTP_CACHE_PATH
        ended_utc=[DateTime]::UtcNow.ToString('o'); exit_code=$exitCode } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $out "$Stage.command.json") -Encoding utf8NoBOM
    if ($exitCode -ne 0) {
        Get-Content -LiteralPath (Join-Path $out "$Stage.log") -Tail 24 | Write-Host
        throw "$Stage exited $exitCode."
    }
}

Set-Location -LiteralPath $out
@{ sdk = @{ version = $lock.dotnet.sdk; rollForward = 'disable' } } |
    ConvertTo-Json | Set-Content -LiteralPath 'global.json' -Encoding utf8NoBOM
$sdkVersion = & $dotnet --version
if ($LASTEXITCODE -ne 0 -or $sdkVersion -cne $lock.dotnet.sdk) { throw 'SDK version mismatch.' }
if ((Get-Item -LiteralPath $linker).VersionInfo.FileVersion -cne $lock.native.msvc_tools_version) {
    throw 'MSVC linker version mismatch.'
}
$nuget = Join-Path $out 'NuGet.Config'
$sources = '<add key="t00" value="' + [Security.SecurityElement]::Escape($feed) + '"/>'
if ($Variant -eq 'Avalonia') { $sources += '<add key="nuget" value="https://api.nuget.org/v3/index.json" protocolVersion="3"/>' }
"<configuration><packageSources><clear/>$sources</packageSources></configuration>" |
    Set-Content -LiteralPath $nuget -Encoding utf8NoBOM
$restoreArgs = @('restore',$project,'-r','win-x64','--disable-build-servers','--configfile',$nuget,'-p:NuGetAudit=false')
if ($Variant -eq 'Avalonia') { $restoreArgs += '--locked-mode' }
Invoke-Recorded restore $restoreArgs
Invoke-Recorded publish @('publish',$project,'-c','Release','-r','win-x64','--self-contained','--no-restore',
    '--disable-build-servers','-p:DebugType=none','-p:IlcUseEnvironmentalTools=true',"-p:CppLinker=$linker",'-o',$publish)
$exe = Join-Path $publish 'ProxyPartner.exe'
if ($Variant -eq 'Win32') {
    Copy-Item -LiteralPath (Join-Path $repo 'build/DesktopBaseline/QuickProxyNet.LICENSE.txt') -Destination $publish
    Copy-Item -LiteralPath (Join-Path $repo 'build/DesktopBaseline/YamlDotNet.LICENSE.txt') -Destination $publish
    Copy-Item -LiteralPath (Join-Path $repo 'build/DesktopBaseline/Gost.LICENSE.txt') -Destination $publish
}
if ($OpenSslBin) {
    $nativeOutput = Join-Path $out 'native-probe'
    pwsh -NoProfile -NonInteractive -File (Join-Path $repo 'experiments/cpp-probe/Build-Sweep.ps1') -Desktop `
        -ToolchainRoot $ToolchainRoot -OpenSslBin $OpenSslBin -OutputDirectory $nativeOutput
    if ($LASTEXITCODE -ne 0) { throw 'C++ probe build failed.' }
    foreach ($name in 'ProxyPartner.NativeProbe.dll','libcrypto-3-x64.dll','libssl-3-x64.dll') {
        Copy-Item -LiteralPath (Join-Path $nativeOutput $name) -Destination $publish
    }
    Copy-Item -LiteralPath $ProbeCaBundle -Destination (Join-Path $publish 'probe-ca-bundle.crt')
    Copy-Item -LiteralPath (Join-Path $repo 'build/DesktopBaseline/NativeProbe.NOTICE.txt') -Destination $publish
}
$data = [IO.File]::ReadAllBytes($exe)
$pe = [BitConverter]::ToInt32($data,0x3c)
if ([BitConverter]::ToUInt32($data,$pe) -ne 0x4550 -or [BitConverter]::ToUInt16($data,$pe+4) -ne 0x8664 -or
    [BitConverter]::ToUInt16($data,$pe+24) -ne 0x20b -or [BitConverter]::ToUInt16($data,$pe+92) -ne 2 -or
    [BitConverter]::ToUInt32($data,$pe+248) -ne 0) { throw 'Expected x64 native GUI PE without a CLR directory.' }
if ($Variant -eq 'Win32' -and $TargetIcons) {
    if (![IO.Path]::IsPathFullyQualified($TargetIcons) -or !(Test-Path -LiteralPath $TargetIcons -PathType Container)) {
        throw 'TargetIcons must point to an existing absolute directory of local preview resources.'
    }
    $destination = Join-Path $publish 'TargetIcons'
    $null = New-Item -ItemType Directory -Path $destination
    $sourcesFile = Join-Path $TargetIcons 'sources.json'
    $iconSources = if (Test-Path -LiteralPath $sourcesFile -PathType Leaf) {
        Get-Content -Raw -LiteralPath $sourcesFile | ConvertFrom-Json
    }
    foreach ($name in 'google.ico','youtube.ico','openai_api.ico','chatgpt_web.ico','anthropic_api.ico') {
        $path = Join-Path $TargetIcons $name
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            if ($iconSources) {
                $source = @($iconSources | Where-Object file -ceq $name)
                if ($source.Count -ne 1 -or (Get-FileHash -LiteralPath $path).Hash -ne $source[0].sha256) {
                    throw "Local preview icon hash differs from sources.json: $name"
                }
            }
            Copy-Item -LiteralPath $path -Destination (Join-Path $destination $name)
        }
    }
    if ($iconSources) { Copy-Item -LiteralPath $sourcesFile -Destination (Join-Path $destination 'sources.json') }
}
$head = git -C $repo rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve build HEAD.' }
[ordered]@{ head_sha=$head; worktree=$repo; sdk=$sdkVersion; runtime=$lock.dotnet.runtime
    linker_version=$lock.native.msvc_tools_version; exe=$exe; variant=$Variant; bytes=$data.Length
    files=@(Get-ChildItem -File -Recurse -LiteralPath $publish | ForEach-Object {
        [ordered]@{ name=[IO.Path]::GetRelativePath($publish,$_.FullName).Replace('\','/')
            sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() }
    }) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $out 'artifact.json') -Encoding utf8NoBOM
Write-Output $exe
