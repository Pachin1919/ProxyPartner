#Requires -Version 7.3
param(
    [Parameter(Mandatory)][string]$ProfilePointer,
    [Parameter(Mandatory)][string]$HostAssemblyDirectory,
    [Parameter(Mandatory)][string]$CppExecutable,
    [Parameter(Mandatory)][string]$CSharpExecutable,
    [Parameter(Mandatory)][string]$CaBundle,
    [Parameter(Mandatory)][string]$EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$profile = (Get-Content -LiteralPath $ProfilePointer -Raw).Trim()
$activeConfig = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'io.github.clash-verge-rev.clash-verge-rev/clash-verge.yaml'
if ($profile -ne $activeConfig) { throw 'Expected the authorized active Clash configuration.' }
[void][Reflection.Assembly]::LoadFrom((Join-Path $HostAssemblyDirectory 'ProxyPartnerLite.Host.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $HostAssemblyDirectory 'YamlDotNet.dll'))
$client = [ProxyPartnerLite.Host.ReadOnlyHostClient]::new()
$options = [ProxyPartnerLite.Host.HostReadOptions]::new($false,$true)
$before = $client.ReadAsync($options).GetAwaiter().GetResult()
if ($before.Status.ToString() -ne 'ConnectedPipe' -or !$before.VerifiedPeerProcessId) { throw 'Mihomo pipe identity was not verified.' }
$beforeHash = (Get-FileHash -LiteralPath $profile).Hash
$roster = @($before.Facts.Nodes | Sort-Object Name)
$yaml = [YamlDotNet.RepresentationModel.YamlStream]::new()
$reader = [IO.File]::OpenText($profile)
try { $yaml.Load($reader) } finally { $reader.Dispose() }
function Field($node, [string]$name) {
    return ,$node.Children[[YamlDotNet.RepresentationModel.YamlScalarNode]::new($name)]
}
$profiles = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
foreach ($node in (Field $yaml.Documents[0].RootNode 'proxies').Children) {
    $name = (Field $node 'name').Value
    if ($name -cin $roster.Name) { $profiles.Add($name,$node) }
}
$payload = [Text.StringBuilder]::new()
foreach ($node in $roster) {
    $entry = $profiles[$node.Name]
    $obfs = Field $entry 'plugin-opts'
    if ($node.Type -cne 'Shadowsocks' -or (Field $entry 'cipher').Value -cne 'chacha20-ietf-poly1305' -or
        (Field $entry 'plugin').Value -cne 'obfs' -or (Field $obfs 'mode').Value -cne 'tls') {
        throw 'This benchmark requires Shadowsocks AEAD with TLS obfuscation for every node.'
    }
    $hostLabel = (Field $obfs 'host').Value ?? 'bing.com'
    $values = @((Field $entry 'server').Value,(Field $entry 'port').Value,(Field $entry 'password').Value,$hostLabel)
    if (@($values | Where-Object { [string]::IsNullOrEmpty($_) -or $_ -match '[\t\r\n\x00]' }).Count -or
        $hostLabel -notmatch '^[!-~]{1,253}$') { throw 'A profile cannot be represented in the benchmark input.' }
    [void]$payload.Append($values -join [char]9).Append([char]10)
}
$bytes = [Text.Encoding]::UTF8.GetBytes($payload.ToString())
$null = $payload.Clear()
$directory = (New-Item -ItemType Directory -Path $EvidenceDirectory).FullName

try {
    foreach ($exe in (Resolve-Path -LiteralPath $CppExecutable).Path,(Resolve-Path -LiteralPath $CSharpExecutable).Path) {
        $name = [IO.Path]::GetFileNameWithoutExtension($exe)
        $output = (New-Item -ItemType Directory -Path (Join-Path $directory $name)).FullName
        $resultFile = Join-Path $output 'sites.tsv'
        $pidFile = Join-Path $output 'root.pid'
        $pipeName = 'proxypartner-ab-' + [Guid]::NewGuid().ToString('N')
        $pipe = [IO.Pipes.NamedPipeServerStream]::new($pipeName,[IO.Pipes.PipeDirection]::Out,1,
            [IO.Pipes.PipeTransmissionMode]::Byte,[IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly)
        $argsJson = ConvertTo-Json -Compress -InputObject @("\\.\pipe\$pipeName",$resultFile,$CaBundle)
        $info = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.WorkingDirectory = Split-Path -Parent $exe
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $script = Join-Path $repo 'scripts/Measure-JobPeak.ps1'
        $args = '-NoProfile','-NonInteractive','-File',$script,'-Executable',$exe,'-ArgumentsJson',$argsJson,
            '-Workload',"$name 3 x $($roster.Count) nodes x 5 HTTPS, 8 concurrent nodes",'-TimeoutSeconds','330','-StartedFile',$pidFile
        foreach ($arg in $args) { $info.ArgumentList.Add($arg) }
        $job = [Diagnostics.Process]::Start($info)
        $stdout = $job.StandardOutput.ReadToEndAsync()
        $stderr = $job.StandardError.ReadToEndAsync()
        $clock = [Diagnostics.Stopwatch]::StartNew()
        try {
            $limit = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(15))
            try { $pipe.WaitForConnectionAsync($limit.Token).GetAwaiter().GetResult() }
            finally { $limit.Dispose() }
            $pipe.Write($bytes,0,$bytes.Length)
        } finally { $pipe.Dispose() }
        $previous = ''
        $markers = [Collections.Generic.List[object]]::new()
        $samples = [Collections.Generic.List[object]]::new()
        $productId = [int](Get-Content -LiteralPath $pidFile -Raw)
        while (!$job.HasExited) {
            $product = Get-Process -Id $productId -ErrorAction SilentlyContinue
            if ($product -and (Test-Path -LiteralPath $resultFile)) {
                $marker = Get-Content -LiteralPath $resultFile | Where-Object { $_ -match '^(sweep|idle)\t' } | Select-Object -Last 1
                if ($marker) {
                    $fields = $marker -split '\t'
                    $elapsed = $clock.Elapsed.TotalSeconds
                    if ($marker -ne $previous) {
                        $markers.Add([pscustomobject]@{stage=$fields[0];round=[int]$fields[1];seconds=$elapsed})
                        $previous = $marker
                    }
                    $samples.Add([pscustomobject]@{stage=$fields[0];round=[int]$fields[1];seconds=$elapsed;bytes=$product.PrivateMemorySize64})
                }
            }
            Start-Sleep -Milliseconds 150
        }
        $stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $output 'job.json')
        $stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $output 'job.stderr')
        $samples | Export-Csv -LiteralPath (Join-Path $output 'samples.csv') -NoTypeInformation
        $markers | Export-Csv -LiteralPath (Join-Path $output 'markers.csv') -NoTypeInformation
        if ($job.ExitCode -ne 0) { throw "$name did not complete. See job.json." }
        $lines = Get-Content -LiteralPath $resultFile | Where-Object { $_ -notmatch '^(sweep|idle)\t' }
        $unique = [Collections.Generic.HashSet[string]]::new()
        $rows = foreach ($line in $lines) {
            $f = $line -split '\t'
            if ($f.Count -ne 6 -or $f[0] -cnotin 'GO','YT','GPT','OPENAI','ANT' -or [int]$f[1] -notin 1,2,3 -or
                [int]$f[2] -lt 0 -or [int]$f[2] -ge $roster.Count -or !$unique.Add(($f[0..2] -join '/'))) {
                throw "$name emitted an invalid or duplicate node result."
            }
            [pscustomobject]@{round=[int]$f[1];status=[int]$f[3];timeout=[int]$f[5]}
        }
        if ($rows.Count -ne 15 * $roster.Count -or $markers.Count -ne 6) { throw "$name did not cover the whole workload." }
        $stats = Get-Content -LiteralPath (Join-Path $output 'job.json') -Raw | ConvertFrom-Json
        $rounds = foreach ($round in 1..3) {
            $selected = @($rows | Where-Object round -eq $round)
            $window = @($markers | Where-Object round -eq $round)
            $memory = @($samples | Where-Object round -eq $round)
            [ordered]@{round=$round;seconds=[Math]::Round($window[1].seconds - $window[0].seconds,2);
                results=$selected.Count;http=@($selected | Where-Object status -gt 0).Count;
                timeout=@($selected | Where-Object timeout -eq 1).Count;
                sweep_mib=[Math]::Round((($memory | Where-Object stage -eq sweep).bytes | Measure-Object -Average).Average/1MB,2);
                idle_mib=[Math]::Round((($memory | Where-Object stage -eq idle).bytes | Measure-Object -Average).Average/1MB,2)}
        }
        [ordered]@{exe=$name;sha256=(Get-FileHash -LiteralPath $exe).Hash;nodes=$roster.Count;peak_mib=$stats.peak_job_commit_mib;
            job_members=$stats.total_processes;rounds=@($rounds)} |
            ConvertTo-Json -Depth 6 | Tee-Object -FilePath (Join-Path $output 'summary.json')
    }
} finally { [Security.Cryptography.CryptographicOperations]::ZeroMemory($bytes) }
$after = $client.ReadAsync($options).GetAwaiter().GetResult()
$groupChanges = @(Compare-Object @($before.Facts.Groups | ForEach-Object {"$($_.Name)=$($_.Selected)"}) @($after.Facts.Groups | ForEach-Object {"$($_.Name)=$($_.Selected)"})).Count
[ordered]@{same_host=$after.VerifiedPeerProcessId -eq $before.VerifiedPeerProcessId;mode_unchanged=$after.Facts.Mode -eq $before.Facts.Mode;
    config_unchanged=(Get-FileHash -LiteralPath $profile).Hash -eq $beforeHash;group_changes=$groupChanges} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'host-check.json')
