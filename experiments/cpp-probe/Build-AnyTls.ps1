#Requires -Version 7.3
param(
    [Parameter(Mandatory)][string]$ToolchainRoot,
    [Parameter(Mandatory)][string]$OpenSslBin,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$Ech
)
$ErrorActionPreference = 'Stop'
$native = Join-Path (Resolve-Path -LiteralPath $ToolchainRoot) 'native'
$ssl = (Resolve-Path -LiteralPath $OpenSslBin).Path
$env:PATH = "$(Join-Path $native 'bin');$env:PATH"
$env:LIB = @('msvc','um','ucrt' | ForEach-Object { Join-Path $native "lib/$_" }) -join ';'
$out = (New-Item -ItemType Directory -Force -Path $OutputDirectory).FullName
$major = if ($Ech) { '4' } else { '3' }
$crypto = "libcrypto-$major-x64"
$sslName = "libssl-$major-x64"
$imports = [ordered]@{
    $crypto = @('EVP_CIPHER_CTX_new','EVP_CIPHER_CTX_free','EVP_CIPHER_CTX_reset','EVP_chacha20_poly1305',
        'SHA256','EVP_sha1','EVP_md5','EVP_BytesToKey','HMAC','EVP_CipherInit_ex','EVP_CIPHER_CTX_ctrl',
        'EVP_CipherUpdate','EVP_CipherFinal_ex','RAND_bytes','OPENSSL_cleanse','BIO_meth_new','BIO_meth_free',
        'BIO_meth_set_read','BIO_meth_set_write','BIO_meth_set_ctrl','BIO_new','BIO_free','BIO_set_data','BIO_get_data','BIO_set_init')
    $sslName = @('TLS_client_method','SSL_CTX_new','SSL_CTX_free','SSL_CTX_load_verify_locations','SSL_CTX_set_verify',
        'SSL_new','SSL_free','SSL_set_bio','SSL_set1_host','SSL_ctrl','SSL_connect','SSL_write','SSL_read')
}
if ($Ech) {
    $imports[$crypto] += 'CRYPTO_free'
    $imports[$sslName] += @('SSL_set1_ech_config_list','SSL_ech_get1_status')
}
foreach($library in $imports.Keys){
    $def = Join-Path $out "$library.def"
    @("LIBRARY $library.dll","EXPORTS") + $imports[$library] | Set-Content -LiteralPath $def -Encoding ascii
    & (Join-Path $native 'bin/lib.exe') /nologo /machine:x64 "/def:$def" "/out:$out/$library.lib"
    if($LASTEXITCODE){exit $LASTEXITCODE}
    Copy-Item -LiteralPath (Join-Path $ssl "$library.dll") -Destination $out
}
Push-Location -LiteralPath $out
try {
    $sources = @('anytls.cpp' | ForEach-Object { Join-Path $PSScriptRoot $_ })
    $flags = @('/LD','/Fe:ProxyPartner.NativeProbe.dll')
    if ($Ech) { $flags += '/DANYTLS_ECH' }
    & (Join-Path $native 'bin/cl.exe') /nologo /std:c++17 /W4 /WX /O2 /MT /utf-8 /GS @flags @sources /link /INCREMENTAL:NO ws2_32.lib "$crypto.lib" "$sslName.lib"
    $code = $LASTEXITCODE
}
finally { Pop-Location }
exit $code
