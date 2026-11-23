#Requires -Version 7.3
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$ToolchainRoot = 'F:/PachinStudio/ProxyPartner/runs/toolchain',
    [string]$HeaderRoot = 'F:/PachinStudio/ProxyPartner/runs/coordination/standalone-kjx/backend/next/cpp-anytls',
    [string]$CliDirectory,
    [switch]$Tests
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ((Get-PSDrive F).Free -lt 32GB) { throw 'F drive is below the 32 GiB write gate.' }
if (![IO.Path]::IsPathFullyQualified($OutputDirectory)) { throw 'Use an absolute output directory.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new output directory to preserve earlier artifacts.' }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$native = Join-Path $ToolchainRoot 'native'
$sdk = Join-Path $HeaderRoot 'sdk-headers/c/Include/10.0.26100.0'
$includes = @((Join-Path $HeaderRoot 'msvc-headers/Contents/VC/Tools/MSVC/14.44.35207/include'),
    (Join-Path $sdk 'ucrt'),(Join-Path $sdk 'shared'),(Join-Path $sdk 'um'),
    (Join-Path $ToolchainRoot 'native-cli-deps/include'))
foreach ($include in $includes) { if (!(Test-Path -LiteralPath $include)) { throw "Missing local include directory $include" } }
$env:PATH = "$(Join-Path $native 'bin');$env:PATH"
$env:LIB = @('msvc','um','ucrt' | ForEach-Object { Join-Path $native "lib/$_" }) -join ';'
$out = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
$publish = (New-Item -ItemType Directory -Path (Join-Path $out 'publish')).FullName

# Compile the original application icon and manifest into a standard Win32 resource file.
$resource = Join-Path $out 'desktop.res'
$stream = [IO.File]::Create($resource)
$writer = [IO.BinaryWriter]::new($stream)
function Write-Resource([int]$Type,[int]$Id,[byte[]]$Data) {
    $writer.Write([uint32]$Data.Length); $writer.Write([uint32]32)
    $writer.Write([uint16]65535); $writer.Write([uint16]$Type)
    $writer.Write([uint16]65535); $writer.Write([uint16]$Id)
    $writer.Write([uint32]0); $writer.Write([uint16]0x1030); $writer.Write([uint16]0)
    $writer.Write([uint32]0); $writer.Write([uint32]0); $writer.Write($Data)
    while ($writer.BaseStream.Position % 4) { $writer.Write([byte]0) }
}
try {
    Write-Resource 0 0 @()
    $icon = [IO.File]::ReadAllBytes((Join-Path $repo 'build/DesktopBaseline/ProxyPartner.ico'))
    $count = [BitConverter]::ToUInt16($icon,4)
    $groupStream = [IO.MemoryStream]::new()
    $group = [IO.BinaryWriter]::new($groupStream)
    try {
        $group.Write([uint16]0); $group.Write([uint16]1); $group.Write([uint16]$count)
        for ($i=0; $i -lt $count; $i++) {
            $offset = 6 + $i*16
            $size = [BitConverter]::ToInt32($icon,$offset+8)
            $start = [BitConverter]::ToInt32($icon,$offset+12)
            Write-Resource 3 ($i+1) $icon[$start..($start+$size-1)]
            $group.Write($icon,$offset,12); $group.Write([uint16]($i+1))
        }
        Write-Resource 14 1 $groupStream.ToArray()
    } finally { $group.Dispose(); $groupStream.Dispose() }
    Write-Resource 24 1 ([IO.File]::ReadAllBytes((Join-Path $repo 'build/DesktopBaseline/app.manifest')))
} finally { $writer.Dispose(); $stream.Dispose() }

$exe = Join-Path $publish 'ProxyPartner.exe'
$source = Join-Path $PSScriptRoot 'desktop.cpp'
$arguments = @('/nologo','/std:c++17','/W4','/WX','/O2','/MT','/utf-8','/EHsc','/GS') +
    @($includes | ForEach-Object { "/I$_" }) + @("/Fe:$exe","/Fo:$out/desktop.obj",$source,'/link',
    '/INCREMENTAL:NO','/SUBSYSTEM:WINDOWS','/MANIFEST:NO',$resource,
    'user32.lib','comctl32.lib','gdi32.lib','shell32.lib','ole32.lib','uuid.lib','comdlg32.lib','dwmapi.lib','uxtheme.lib','winhttp.lib','ws2_32.lib')
$started = [DateTime]::UtcNow.ToString('o')
& (Join-Path $native 'bin/cl.exe') @arguments *> (Join-Path $out 'build.log')
$code = $LASTEXITCODE
[ordered]@{source_sha=(git -C $repo rev-parse HEAD); cwd=$repo; compiler=(Join-Path $native 'bin/cl.exe'); arguments=$arguments
    started_utc=$started; ended_utc=[DateTime]::UtcNow.ToString('o'); exit_code=$code } |
    ConvertTo-Json -Depth 4 | Set-Content (Join-Path $out 'build.command.json')
if ($code) { Get-Content (Join-Path $out 'build.log'); exit $code }
Copy-Item -LiteralPath (Join-Path $repo 'build/DesktopBaseline/TargetIcons') -Destination $publish -Recurse
Copy-Item -LiteralPath (Join-Path $ToolchainRoot 'native-cli-deps/nlohmann-json.LICENSE') -Destination (Join-Path $publish 'nlohmann-json.LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $publish
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Portable.README.txt') -Destination $publish
if ($CliDirectory) {
    $cli = (Resolve-Path -LiteralPath $CliDirectory).Path
    if (!(Test-Path -LiteralPath (Join-Path $cli 'ProxyPartner.Cli.exe'))) { throw 'The CLI directory must contain ProxyPartner.Cli.exe.' }
    Get-ChildItem -LiteralPath $cli -File | Where-Object {
        $_.Name -ne 'ProxyPartner.exe' -and ($_.Extension -in '.exe','.dll','.crt' -or $_.Name -match '(LICENSE|NOTICE)')
    } | Copy-Item -Destination $publish
}
if ($Tests) {
    $testExe = Join-Path $out 'NativeDesktop.Tests.exe'
    $testArguments = @('/nologo','/std:c++17','/W4','/WX','/O2','/MT','/utf-8','/EHsc','/GS') +
        @($includes | ForEach-Object { "/I$_" }) + @("/Fe:$testExe","/Fo:$out/tests.obj",(Join-Path $PSScriptRoot 'tests.cpp'),
        '/link','/INCREMENTAL:NO','/SUBSYSTEM:CONSOLE','user32.lib','comctl32.lib','gdi32.lib','shell32.lib','ole32.lib','uuid.lib','comdlg32.lib','dwmapi.lib','uxtheme.lib','winhttp.lib','ws2_32.lib')
    & (Join-Path $native 'bin/cl.exe') @testArguments *> (Join-Path $out 'tests-build.log')
    if ($LASTEXITCODE) { Get-Content (Join-Path $out 'tests-build.log'); exit $LASTEXITCODE }
    $testStarted = [DateTime]::UtcNow.ToString('o')
    $testRunArguments = @((Join-Path $out 'test-results.json'))
    if ($CliDirectory) { $testRunArguments += $publish }
    & $testExe @testRunArguments
    $testCode = $LASTEXITCODE
    [ordered]@{exe=$testExe; arguments=$testRunArguments;cwd=$repo;head_sha=(git -C $repo rev-parse HEAD)
        started_utc=$testStarted;ended_utc=[DateTime]::UtcNow.ToString('o');exit_code=$testCode} |
        ConvertTo-Json | Set-Content (Join-Path $out 'tests.command.json')
    if ($testCode) { exit $testCode }
}
[ordered]@{head_sha=(git -C $repo rev-parse HEAD); worktree=$repo; exe=$exe; cli_included=[bool]$CliDirectory
    files=@(Get-ChildItem -LiteralPath $publish -File -Recurse | ForEach-Object {
        [ordered]@{name=[IO.Path]::GetRelativePath($publish,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
    }) } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $out 'artifact.json')
$exe
