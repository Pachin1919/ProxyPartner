#Requires -Version 7.3
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$ToolchainRoot,
    [Parameter(Mandatory)][string]$HeaderRoot,
    [Parameter(Mandatory)][string]$AnyTlsBin,
    [Parameter(Mandatory)][string]$MieruBin,
    [Parameter(Mandatory)][string]$CaBundle,
    [string]$ShadowsocksBin,
    [string]$V2RayBin
)

$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new output directory.' }
$native = Join-Path $ToolchainRoot 'native'
$deps = Join-Path $ToolchainRoot 'native-cli-deps'
$openssl = Join-Path $HeaderRoot 'openssl-4.0.0'
$sdk = Join-Path $HeaderRoot 'sdk-headers/c/Include/10.0.26100.0'
$msvc = Join-Path $HeaderRoot 'msvc-headers/Contents/VC/Tools/MSVC/14.44.35207/include'
$env:PATH = "$(Join-Path $native 'bin');$env:PATH"
$env:INCLUDE = @($msvc,"$sdk/ucrt","$sdk/shared","$sdk/um","$openssl/include","$deps/libyaml-0.2.5/include","$deps/include") -join ';'
$env:LIB = @("$native/lib/msvc","$native/lib/um","$native/lib/ucrt") -join ';'
$output = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
$publish = (New-Item -ItemType Directory -Path (Join-Path $output 'publish')).FullName
Push-Location -LiteralPath $output
try {
    $cl = Join-Path $native 'bin/cl.exe'
    & $cl /nologo /std:c++20 /EHsc /O2 /MT /utf-8 /W4 /DYAML_DECLARE_STATIC /D_CRT_SECURE_NO_WARNINGS `
        "/Fe:$publish/ProxyPartner.Cli.exe" (Join-Path $PSScriptRoot 'main.cpp') (Join-Path $PSScriptRoot 'age.cpp') (Join-Path $PSScriptRoot 'host.cpp') `
        (Join-Path $PSScriptRoot 'current_path.cpp') `
        /link /INCREMENTAL:NO /STACK:1048576,16384 ws2_32.lib dnsapi.lib winhttp.lib advapi32.lib iphlpapi.lib `
        (Join-Path $openssl 'libssl.lib') (Join-Path $openssl 'libcrypto.lib') (Join-Path $deps 'x64/yaml.lib')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally { Pop-Location }
foreach ($name in 'libcrypto-4-x64.dll','libssl-4-x64.dll') {
    $a = Join-Path $AnyTlsBin $name
    $b = Join-Path $MieruBin $name
    if ((Get-FileHash -LiteralPath $a).Hash -ne (Get-FileHash -LiteralPath $b).Hash) {
        throw "The native adapters use different OpenSSL binaries: $name"
    }
    Copy-Item -LiteralPath $a -Destination $publish
}
Copy-Item -LiteralPath (Join-Path $AnyTlsBin 'ProxyPartner.NativeProbe.dll') -Destination "$publish/ProxyPartner.AnyTlsProbe.dll"
Copy-Item -LiteralPath (Join-Path $MieruBin 'ProxyPartner.NativeProbe.dll') -Destination "$publish/ProxyPartner.MieruProbe.dll"
Copy-Item -LiteralPath $CaBundle -Destination "$publish/probe-ca-bundle.crt"
Copy-Item -LiteralPath (Join-Path $AnyTlsBin 'Mihomo-LICENSE') -Destination "$publish/AnyTls.LICENSE.txt"
Copy-Item -LiteralPath (Join-Path $AnyTlsBin 'OpenSSL-LICENSE.txt') -Destination "$publish/OpenSSL4.LICENSE.txt"
Copy-Item -LiteralPath (Join-Path $MieruBin 'Mieru-LICENSE') -Destination "$publish/Mieru.LICENSE.txt"
Copy-Item -LiteralPath (Join-Path $MieruBin 'Monocypher-LICENCE.md') -Destination "$publish/Monocypher.LICENSE.md"
Copy-Item -LiteralPath "$deps/libyaml-0.2.5/License" -Destination "$publish/LibYAML.LICENSE.txt"
Copy-Item -LiteralPath "$deps/nlohmann-json.LICENSE" -Destination "$publish/JSON.LICENSE.txt"
if ($ShadowsocksBin) {
    foreach ($name in 'ProxyPartner.NativeProbe.dll','libcrypto-3-x64.dll','libssl-3-x64.dll','NativeProbe.NOTICE.txt','OpenSSL.LICENSE.txt','Gost.LICENSE.txt') {
        Copy-Item -LiteralPath (Join-Path $ShadowsocksBin $name) -Destination $publish
    }
}
if ($V2RayBin) {
    foreach ($name in 'libcrypto-4-x64.dll','libssl-4-x64.dll') {
        if ((Get-FileHash -LiteralPath (Join-Path $V2RayBin $name)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $publish $name)).Hash) {
            throw "The legacy adapter requires the same OpenSSL binary: $name"
        }
    }
    Copy-Item -LiteralPath (Join-Path $V2RayBin 'ProxyPartner.V2RayProbe.dll') -Destination $publish
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../experiments/cpp-v2ray/QuickProxyNet.LICENSE.txt') -Destination $publish
}
$publish
