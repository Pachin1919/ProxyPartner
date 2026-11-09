#Requires -Version 7.3
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('environment', 'license-lock', 'budget-contract')][string]$Check,
    [Parameter(Mandatory)][string]$ToolchainRoot,
    [Parameter(Mandatory)][string]$EvidenceDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (-not [IO.Path]::IsPathFullyQualified($ToolchainRoot) -or
    -not [IO.Path]::IsPathFullyQualified($EvidenceDirectory)) { throw 'Use absolute directory paths.' }

$repo = Split-Path -Parent $PSScriptRoot
$toolchain = (Resolve-Path -LiteralPath $ToolchainRoot).Path
$run = Join-Path $EvidenceDirectory $Check
if (Test-Path -LiteralPath $run) { throw "Evidence already exists at $run. Use a new run directory." }
New-Item -ItemType Directory -Path $run -Force | Out-Null
$log = Join-Path $run 'run.log'
$result = [ordered]@{ check = $Check; head_sha = $null; status = 'FAIL'; exit_code = 1
    started_utc = [DateTime]::UtcNow.ToString('o') }

function Invoke-BaselineCommand([string]$File, [string[]]$Arguments) {
    "CWD=$PWD" | Add-Content -LiteralPath $log
    "COMMAND=$File $($Arguments -join ' ')" | Add-Content -LiteralPath $log
    $output = @(& $File @Arguments 2>&1)
    $code = $LASTEXITCODE
    $output | ForEach-Object { "$_" } | Add-Content -LiteralPath $log
    "EXIT_CODE=$code" | Add-Content -LiteralPath $log
    if ($code -ne 0) { throw "$File exited $code. See $log." }
    return $output
}

function Assert-LockedFile([string]$Path, [string]$Expected, [string]$Algorithm = 'SHA256') {
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash
    "$Algorithm $actual $Path" | Add-Content -LiteralPath $log
    if ($actual -ne $Expected) { throw "$Algorithm mismatch for $Path." }
}

$location = Get-Location
try {
    $result.head_sha = [string](Invoke-BaselineCommand git @('-C', $repo, 'rev-parse', 'HEAD'))
    Invoke-BaselineCommand git @('-C', $repo, 'status', '--short') | Out-Null
    $lock = Get-Content -LiteralPath (Join-Path $repo 'contracts/windows-toolchain.json') -Raw | ConvertFrom-Json
    if ($lock.schema_version -ne 1 -or $lock.rid -ne 'win-x64') { throw 'Unsupported toolchain lock.' }

    if ($Check -in @('environment', 'license-lock')) {
        $sdk = Join-Path $toolchain "dotnet-$($lock.dotnet.sdk)-$($lock.rid)"
        $dotnet = Join-Path $sdk 'dotnet.exe'
        $go = [IO.Path]::GetFullPath((Join-Path $toolchain $lock.go.relative_path))
        $native = Join-Path $toolchain 'native'
        $feed = Join-Path $toolchain 'nuget-packages'
        Assert-LockedFile (Join-Path $toolchain "dotnet-sdk-$($lock.dotnet.sdk)-$($lock.rid).zip") $lock.dotnet.archive_sha512 SHA512
        foreach ($file in $lock.tool_files) { Assert-LockedFile (Join-Path $toolchain $file.path) $file.sha256 }
        foreach ($package in $lock.dotnet.nuget_packages) {
            $dir = Join-Path $feed "$($package.id)/$($package.version)"
            Assert-LockedFile (Join-Path $dir "$($package.id).$($package.version).nupkg") $package.sha256
            if ($Check -eq 'license-lock') {
                $meta = ([xml](Get-Content -LiteralPath (Join-Path $dir "$($package.id).nuspec") -Raw)).package.metadata
                if ($meta.id -ne $package.id -or $meta.version -ne $package.version -or
                    $meta.license.'#text' -cne 'MIT' -or $meta.repository.commit -cne $lock.dotnet.runtime_source_commit) {
                    throw "Package license or provenance mismatch for $($package.id)."
                }
                "NuGet $($meta.id) $($meta.version) MIT $($meta.repository.commit)" | Add-Content -LiteralPath $log
                $notices = @(Get-ChildItem -LiteralPath $dir -File | Where-Object Name -Match '^(LICENSE|THIRD-PARTY-NOTICES)(\.TXT)?$')
                if (-not $notices.Count) { throw "Package license or notice file missing for $($package.id)." }
                foreach ($notice in $notices) {
                    "SHA256 $((Get-FileHash -LiteralPath $notice.FullName).Hash) $($notice.FullName)" |
                        Add-Content -LiteralPath $log
                }
            }
        }
    }

    switch ($Check) {
        'environment' {
            $os = [Runtime.InteropServices.RuntimeInformation]
            if (-not $IsWindows -or $os::OSArchitecture -ne 'X64' -or $os::ProcessArchitecture -ne 'X64') {
                throw 'Run on Windows x64 with x64 PowerShell.'
            }
            $result.os = $os::OSDescription
            $hostPath = Join-Path $toolchain $lock.host_evidence.path
            Assert-LockedFile $hostPath $lock.host_evidence.sha256
            $observed = Get-Content -LiteralPath $hostPath -Raw | ConvertFrom-Json
            if ($observed.tool_exit_code -ne 0 -or $observed.result -ne 'read_only_probe_pass' -or
                $observed.core_api_version -cne $lock.host_evidence.version -or
                $observed.matched_server_process -cne 'verge-mihomo' -or $observed.get_routes -cnotcontains '/version') {
                throw 'Archived host verification does not match the toolchain lock.'
            }
            $result.host = [ordered]@{
                method = 'archived_identity_checked_read_only_pipe'
                evidence_sha256 = $lock.host_evidence.sha256
                observed_at = $observed.observed_at_local
                head_sha = $observed.head_sha
                pipe = $observed.pipe
                core_version = $observed.core_api_version
                effective_node_config = 'unverified'
                config_source_boundary = 'active merged config path not established; pipe provides observed runtime metadata'
            }

            $env:DOTNET_CLI_HOME = Join-Path $run 'dotnet-home'
            $env:DOTNET_ROOT = $sdk
            $env:DOTNET_ROOT_X64 = $sdk
            $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
            $env:DOTNET_NOLOGO = '1'
            $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
            $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
            $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
            $env:NUGET_PACKAGES = Join-Path $run 'nuget-packages'
            $env:NUGET_HTTP_CACHE_PATH = Join-Path $run 'nuget-http-cache'
            $env:NUGET_PLUGINS_CACHE_PATH = Join-Path $run 'nuget-plugins-cache'
            $env:NUGET_SCRATCH = Join-Path $run 'nuget-scratch'
            $env:APPDATA = Join-Path $run 'roaming'
            $env:LOCALAPPDATA = Join-Path $run 'local-appdata'
            $env:TEMP = Join-Path $run 'temp'
            $env:TMP = $env:TEMP
            $env:LIB = @('msvc', 'um', 'ucrt' | ForEach-Object { Join-Path $native "lib/$_" }) -join ';'
            "LIB=$env:LIB" | Add-Content -LiteralPath $log
            $env:PATH = "$(Join-Path $native 'bin');$env:PATH"
            $env:GOCACHE = Join-Path $run 'go-cache'
            $env:GOPATH = Join-Path $run 'go-path'
            $env:GOPROXY = 'off'
            $env:GOTOOLCHAIN = 'local'
            $env:GOTELEMETRY = 'off'
            $env:CGO_ENABLED = '0'
            New-Item -ItemType Directory -Path $env:TEMP, $env:APPDATA, $env:LOCALAPPDATA -Force | Out-Null

            Set-Location -LiteralPath $run
            @{ sdk = @{ version = $lock.dotnet.sdk; rollForward = 'disable' } } |
                ConvertTo-Json | Set-Content -LiteralPath 'global.json' -Encoding utf8NoBOM
            if ([string](Invoke-BaselineCommand $dotnet @('--version')) -cne $lock.dotnet.sdk) { throw 'SDK version mismatch.' }
            $linker = Join-Path $native 'bin/link.exe'
            $result.linker_version = (Get-Item -LiteralPath $linker).VersionInfo.FileVersion
            if ($result.linker_version -cne $lock.native.msvc_tools_version) { throw 'MSVC linker version mismatch.' }
            '<configuration><packageSources><clear /></packageSources></configuration>' |
                Set-Content -LiteralPath 'NuGet.Config' -Encoding utf8NoBOM
            @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <RuntimeFrameworkVersion>$($lock.dotnet.runtime)</RuntimeFrameworkVersion>
    <RuntimeIdentifier>$($lock.rid)</RuntimeIdentifier>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <PublishAot>true</PublishAot>
  </PropertyGroup>
</Project>
"@ | Set-Content -LiteralPath 'Probe.csproj' -Encoding utf8NoBOM
            @'
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

internal static partial class Program
{
    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentProcessId();

    [LibraryImport("winsqlite3.dll", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int sqlite3_open(string file, out IntPtr db);

    [LibraryImport("winsqlite3.dll", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int sqlite3_exec(IntPtr db, string sql, IntPtr callback, IntPtr context, IntPtr error);

    [LibraryImport("winsqlite3.dll")]
    private static partial int sqlite3_changes(IntPtr db);

    [LibraryImport("winsqlite3.dll")]
    private static partial int sqlite3_close(IntPtr db);

    [LibraryImport("winsqlite3.dll")]
    private static partial IntPtr sqlite3_libversion();

    [LibraryImport("winsqlite3.dll")]
    private static partial IntPtr sqlite3_sourceid();

    private static int Main()
    {
        if (RuntimeFeature.IsDynamicCodeSupported || GetCurrentProcessId() != Environment.ProcessId) return 1;
        Console.WriteLine("win32_pid=PASS");
        Console.WriteLine($"sqlite_version={Marshal.PtrToStringUTF8(sqlite3_libversion())}");
        Console.WriteLine($"sqlite_source_id={Marshal.PtrToStringUTF8(sqlite3_sourceid())}");
        int opened = sqlite3_open(":memory:", out IntPtr db);
        if (opened != 0)
        {
            if (db != IntPtr.Zero) sqlite3_close(db);
            return 2;
        }
        int result = sqlite3_exec(db, "CREATE TABLE sample(value INTEGER NOT NULL) STRICT; INSERT INTO sample VALUES(7);",
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        int rows = sqlite3_changes(db);
        int closed = sqlite3_close(db);
        if (result != 0 || rows != 1 || closed != 0) return 3;
        Console.WriteLine("sqlite_strict_insert=PASS");
        return 0;
    }
}
'@ | Set-Content -LiteralPath 'Program.cs' -Encoding utf8NoBOM
            Invoke-BaselineCommand $dotnet @('restore', 'Probe.csproj', '--configfile', 'NuGet.Config',
                '--source', $feed, '--disable-build-servers') | Out-Null
            Invoke-BaselineCommand $dotnet @('publish', 'Probe.csproj', '-c', 'Release', '--no-restore',
                '--disable-build-servers', '-p:DebugType=none', '-p:IlcUseEnvironmentalTools=true',
                "-p:CppLinker=$linker") | Out-Null
            $exe = Join-Path $run "bin/Release/net10.0/$($lock.rid)/publish/Probe.exe"
            $stream = [IO.File]::OpenRead($exe)
            try {
                $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
                try {
                    if ($pe.PEHeaders.CoffHeader.Machine -ne 'Amd64' -or $null -ne $pe.PEHeaders.CorHeader) {
                        throw 'Publish output must be an x64 native PE.'
                    }
                } finally { $pe.Dispose() }
            } finally { $stream.Dispose() }
            $probe = @(Invoke-BaselineCommand $exe @())
            if ($probe -cnotcontains "sqlite_version=$($lock.winsqlite.version)" -or
                $probe -cnotcontains "sqlite_source_id=$($lock.winsqlite.source_id)" -or
                $probe -cnotcontains 'win32_pid=PASS' -or $probe -cnotcontains 'sqlite_strict_insert=PASS') {
                throw 'Native probe did not match the Win32/SQLite baseline.'
            }
            $result.native_exe_sha256 = (Get-FileHash -LiteralPath $exe).Hash
            $result.winsqlite_version = $lock.winsqlite.version
            $result.winsqlite_dll_sha256 = (Get-FileHash -LiteralPath (Join-Path ([Environment]::SystemDirectory) 'winsqlite3.dll')).Hash

            $goVersion = [string](Invoke-BaselineCommand $go @('version'))
            if ($goVersion -cne "go version $($lock.go.version) windows/amd64") { throw 'Go version mismatch.' }
            @'
package main

import "fmt"

func main() { fmt.Println("go_probe=PASS") }
'@ | Set-Content -LiteralPath 'go-probe.go' -Encoding ascii
            $goExe = Join-Path $run 'go-probe.exe'
            Invoke-BaselineCommand $go @('build', '-o', $goExe, 'go-probe.go') | Out-Null
            if ([string](Invoke-BaselineCommand $goExe @()) -cne 'go_probe=PASS') { throw 'Go probe output mismatch.' }
            $result.go_exe_sha256 = (Get-FileHash -LiteralPath $goExe).Hash
        }
        'license-lock' {
            $blob = [string](Invoke-BaselineCommand git @('-C', $repo, 'hash-object', '--path=LICENSE', (Join-Path $repo 'LICENSE')))
            if ($blob -cne $lock.project_license_blob) { throw 'Project MIT license differs from the lock.' }
            foreach ($file in $lock.provenance_files) { Assert-LockedFile (Join-Path $toolchain $file.path) $file.sha256 }
            $pkg = $lock.native.windows_sdk_package
            $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $toolchain "$($pkg.ToLowerInvariant()).$($lock.native.windows_sdk_version).nupkg"))
            try {
                $reader = [IO.StreamReader]::new($archive.GetEntry("$pkg.nuspec").Open())
                try { $meta = ([xml]$reader.ReadToEnd()).package.metadata }
                finally { $reader.Dispose() }
                if ($meta.id -cne $pkg -or $meta.version -cne $lock.native.windows_sdk_version -or
                    $meta.licenseUrl -cne $lock.native.windows_sdk_license_url) { throw 'Windows SDK license metadata mismatch.' }
            } finally { $archive.Dispose() }
            $architecture = Get-Content -LiteralPath (Join-Path $repo 'spec/ARCHITECTURE.md') -Raw
            if (-not $architecture.Contains('Mihomo 与 faceair') -or -not $architecture.Contains('GPL 参考代码不进入实现')) {
                throw 'Reference-only GPL boundary is missing.'
            }
            $result.scope = 'project MIT; Windows SDK and MSVC build-only; .NET pack MIT notices; Go BSD-3-Clause; OS Win32 and WinSQLite APIs'
            $result.windows_sdk_license_url = $lock.native.windows_sdk_license_url
            $result.msvc_license_source = $lock.native.msvc_license_source
            $result.sqlite_product_dependency = 'pending_T05'
        }
        'budget-contract' {
            $budget = Get-Content -LiteralPath (Join-Path $repo 'contracts/performance-budget.json') -Raw | ConvertFrom-Json
            $expected = [ordered]@{
                schema_version = 1
                unit = 'MiB'
                bytes_per_mib = 1048576
                all_product_processes = $true
                tray_p95_mib = 25
                window_p95_mib = 35
                hard_peak_private_mib = 40
                tray_mean_cpu_one_core_pct = 0.5
                soak_hours = 24
                soak_median_growth_mib = 2
                fixture_nodes = 100
                stress_nodes = 1000
                fixture_targets = 3
                sampling_max_gap_seconds = 2.5
            }
            foreach ($key in $expected.Keys) {
                if ((ConvertTo-Json -InputObject $budget.$key -Compress) -cne
                    (ConvertTo-Json -InputObject $expected[$key] -Compress)) { throw "Budget drift at $key." }
            }
            $performance = Get-Content -LiteralPath (Join-Path $repo 'spec/PERFORMANCE.md') -Raw
            foreach ($rule in @('三次冷启动', '7天fixture', '托盘10分钟', '窗口交互5分钟',
                '开关窗口100次', 'Job Object PeakJobMemoryUsed', '连续24h')) {
                if (-not $performance.Contains($rule)) { throw "Performance workload rule missing: $rule." }
            }
            $result.budget = $expected
            $result.measurement = 'not_run'
        }
    }
    $result.status = 'PASS'
    $result.exit_code = 0
} catch {
    $result.error = $_.Exception.Message
    $result.error | Add-Content -LiteralPath $log
} finally {
    Set-Location -LiteralPath $location
    $result.ended_utc = [DateTime]::UtcNow.ToString('o')
    $result.log_sha256 = (Get-FileHash -LiteralPath $log).Hash
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $run 'result.json') -Encoding utf8NoBOM
}
$result | ConvertTo-Json -Depth 8
exit $result.exit_code
