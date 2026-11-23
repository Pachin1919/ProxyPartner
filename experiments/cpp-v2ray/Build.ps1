#Requires -Version 7.3
param([Parameter(Mandatory)][string]$OutputDirectory,[Parameter(Mandatory)][string]$ToolchainRoot,[Parameter(Mandatory)][string]$HeaderRoot)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $OutputDirectory){throw 'Use a new output directory.'}
$out=(New-Item -ItemType Directory -Path $OutputDirectory).FullName
$native=Join-Path $ToolchainRoot 'native';$ssl=Join-Path $HeaderRoot 'openssl-4.0.0';$sdk=Join-Path $HeaderRoot 'sdk-headers/c/Include/10.0.26100.0'
$env:PATH="$native/bin;$env:PATH"
$env:INCLUDE="$HeaderRoot/msvc-headers/Contents/VC/Tools/MSVC/14.44.35207/include;$sdk/ucrt;$sdk/shared;$sdk/um;$ssl/include"
$env:LIB="$native/lib/msvc;$native/lib/um;$native/lib/ucrt"
Push-Location $out
try {
 & "$native/bin/cl.exe" /nologo /std:c++20 /EHsc /O2 /MT /W4 /WX /utf-8 /D_CRT_SECURE_NO_WARNINGS /LD /Fe:ProxyPartner.V2RayProbe.dll "$PSScriptRoot/v2ray.cpp" /link /INCREMENTAL:NO ws2_32.lib "$ssl/libssl.lib" "$ssl/libcrypto.lib"
 if($LASTEXITCODE){exit $LASTEXITCODE}
 foreach($name in 'libcrypto-4-x64.dll','libssl-4-x64.dll'){Copy-Item "$ssl/$name" $out}
} finally {Pop-Location}
