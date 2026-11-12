#Requires -Version 7.3
[CmdletBinding()]
param(
    [string]$ToolchainRoot = (Join-Path $PSScriptRoot '../runs/toolchain'),
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$repo = Split-Path -Parent $PSScriptRoot
$sha = git -C $repo rev-parse HEAD
if (git -C $repo status --porcelain) { throw 'Package from a clean checkout.' }
$ToolchainRoot = (Resolve-Path -LiteralPath $ToolchainRoot).Path
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $repo "runs/release/$($sha.Substring(0,12))-$(Get-Date -AsUTC -Format 'yyyyMMdd-HHmmss-fff')"
}
$output = (New-Item -ItemType Directory -Path $OutputDirectory).FullName

$gitRoot = Split-Path -Parent (Split-Path -Parent (git --exec-path))
$sslBin = Join-Path $gitRoot 'bin'
$caBundle = Join-Path $gitRoot 'etc/ssl/certs/ca-bundle.crt'
& pwsh -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot 'Publish-DesktopBaseline.ps1') `
    -ToolchainRoot $ToolchainRoot -EvidenceDirectory $output -OpenSslBin $sslBin -ProbeCaBundle $caBundle

$build = Get-Content -Raw -LiteralPath (Join-Path $output 'win32/artifact.json') | ConvertFrom-Json
if ($build.head_sha -cne $sha) { throw 'The source revision changed during the build.' }
$publish = Split-Path -Parent $build.exe
$files = [ordered]@{}
foreach ($name in 'ProxyPartner.exe','ProxyPartner.NativeProbe.dll','libcrypto-3-x64.dll','libssl-3-x64.dll',
    'probe-ca-bundle.crt','NativeProbe.NOTICE.txt','OpenSSL.LICENSE.txt','Gost.LICENSE.txt',
    'QuickProxyNet.LICENSE.txt','YamlDotNet.LICENSE.txt') {
    $path = Join-Path $publish $name
    $expected = $build.files | Where-Object name -ceq $name
    if (!$expected -or (Get-FileHash -LiteralPath $path).Hash -ne $expected.sha256) {
        throw "Missing or mismatched build file: $name"
    }
    $files[$name] = $path
}
$runtime = Join-Path $ToolchainRoot "cache/nuget-packages/microsoft.netcore.app.runtime.nativeaot.win-x64/$($build.runtime)"
$files['LICENSE.txt'] = Join-Path $repo 'LICENSE'
$files['dotnet.LICENSE.txt'] = Join-Path $runtime 'LICENSE.TXT'
$files['dotnet.ThirdPartyNotices.txt'] = Join-Path $runtime 'THIRD-PARTY-NOTICES.TXT'
$files['README.txt'] = Join-Path $PSScriptRoot 'Portable.README.txt'
$info = Join-Path $output 'build-info.json'
[ordered]@{
    commit = $sha
    sdk = $build.sdk
    runtime = $build.runtime
    msvc = $build.linker_version
    git_for_windows = (git --version)
    openssl = (Get-Item -LiteralPath $files['libssl-3-x64.dll']).VersionInfo.ProductVersion
    files = @($files.GetEnumerator() | ForEach-Object {
        [ordered]@{ name = $_.Key; sha256 = (Get-FileHash -LiteralPath $_.Value).Hash.ToLowerInvariant() }
    })
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $info -Encoding utf8NoBOM
$files['build-info.json'] = $info

$zip = Join-Path $output 'ProxyPartner-windows-x64.zip'
$archive = [IO.Compression.ZipFile]::Open("$zip.partial", 'Create')
try {
    foreach ($file in $files.GetEnumerator()) {
        $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.Value, $file.Key, 'Optimal')
    }
} finally {
    $archive.Dispose()
}
Move-Item -LiteralPath "$zip.partial" -Destination $zip
"$((Get-FileHash -LiteralPath $zip).Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($zip))" |
    Set-Content -LiteralPath "$zip.sha256" -Encoding ascii
$zip
