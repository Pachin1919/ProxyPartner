#Requires -Version 7.3
param(
    [Parameter(Mandatory)][string]$ToolchainRoot,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$native = Join-Path (Resolve-Path -LiteralPath $ToolchainRoot) 'native'
$env:PATH = "$(Join-Path $native 'bin');$env:PATH"
$env:LIB = @('msvc','um','ucrt' | ForEach-Object { Join-Path $native "lib/$_" }) -join ';'
$out = (New-Item -ItemType Directory -Force -Path $OutputDirectory).FullName
& (Join-Path $native 'bin/cl.exe') /nologo /std:c++17 /W4 /WX /O2 /MT /utf-8 /GS "/Fe:$out/CppProbe.exe" "/Fo:$out/CppProbe.obj" (Join-Path $PSScriptRoot 'main.cpp') /link /INCREMENTAL:NO /SUBSYSTEM:CONSOLE winhttp.lib
exit $LASTEXITCODE
