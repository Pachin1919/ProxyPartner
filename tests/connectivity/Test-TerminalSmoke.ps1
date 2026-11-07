#Requires -Version 7.3
$ErrorActionPreference = 'Stop'
$entry = Join-Path $PSScriptRoot '../../scripts/Test-TargetConnectivity.ps1'
. $entry

function Assert-Equal($Actual, $Expected, [string]$Label) {
    if ($Actual -cne $Expected) { throw "$Label expected [$Expected], got [$Actual]" }
}

$targetIds = 'google,youtube,openai_api,chatgpt_web,anthropic_api'
$targetUrls = @(
    'https://www.google.com/', 'https://www.youtube.com/', 'https://api.openai.com/v1/models',
    'https://chatgpt.com/', 'https://api.anthropic.com/v1/models'
)
$testProxy = 'http://127.0.0.1:7897'
$script:calls = [Collections.Generic.List[object]]::new()
$script:responses = [Collections.Generic.Queue[object]]::new()

function Invoke-TerminalCurl($CurlPath, [string[]]$CurlArguments) {
    if ($CurlArguments[1] -eq '--version') {
        return [pscustomobject]@{ output = @('curl 8.21.0 (Windows)'); exit_code = 0 }
    }
    $script:calls.Add($CurlArguments)
    if (-not $script:responses.Count) { throw 'Unexpected extra request.' }
    $case = $script:responses.Dequeue()
    [pscustomobject]@{ output = @($case[0]); exit_code = $case[1] }
}

foreach ($case in @(
    ,@('204|0.200|0.250|1|127.0.0.1|7897|200', 0)
    ,@('302|0.300|0.350|1|127.0.0.1|7897|200', 0)
    ,@('401|0.400|0.450|1|127.0.0.1|7897|200', 0)
    ,@('403|0.500|0.550|1|127.0.0.1|7897|200', 0)
    ,@('429|0.600|0.650|1|127.0.0.1|7897|200', 0)
)) { $script:responses.Enqueue($case) }

$batch = Invoke-TerminalTargetBatch -Proxy $testProxy
Assert-Equal ($batch.results.id -join ',') $targetIds 'target order'
Assert-Equal ($batch.results.http_result -join ',') 'http_success,redirect_not_followed,api_authentication_required,web_restricted,rate_limited' 'status classifications'
Assert-Equal $batch.results[2].model_api_verified $false '401 is an authentication response'
Assert-Equal $batch.results[2].first_byte_ms 400 'first byte in ms'
Assert-Equal $batch.results[2].curl_total_ms 450 'total in ms'
Assert-Equal $batch.results[2].proxy_connect_status 200 'CONNECT response'
Assert-Equal $batch.batch_exit_code 0 'complete batch'
Assert-Equal $script:calls.Count 5 'requests per batch'

for ($i = 0; $i -lt $script:calls.Count; $i++) {
    $call = $script:calls[$i]
    Assert-Equal ($call -join '|') (
        '-q|--proxy|' + $testProxy + '|--noproxy||--proto|=https|--globoff|--request|GET|--no-location|' +
        '--max-redirs|0|--retry|0|--no-netrc|--connect-timeout|5|--max-time|12|--max-filesize|65536|' +
        '--silent|--output|NUL|--header|Authorization:|--header|Cookie:|--proxy-header|Proxy-Authorization:|' +
        '--write-out|%{http_code}|%{time_starttransfer}|%{time_total}|%{proxy_used}|%{remote_ip}|%{remote_port}|%{http_connect}|' +
        '--url|' + $targetUrls[$i] + $(if ($i -eq 4) { '|--header|anthropic-version: 2026-10-21' })
    ) "curl arguments for $($batch.results[$i].id)"
}

foreach ($case in @(
    ,@('503|0.100|0.150|1|127.0.0.1|7897|200', 0)
    ,@('000|0.000|12.000|1|127.0.0.1|7897|200', 28)
    ,@('000|0.000729|0.000733|1||-1|000', 7)
    ,@('200|0.100|0.150|1|127.0.0.1|7897|200', 63)
    ,@('', $null)
)) { $script:responses.Enqueue($case) }

$batch = Invoke-TerminalTargetBatch -Proxy $testProxy
Assert-Equal ($batch.results.id -join ',') $targetIds 'failed requests preserve target order'
Assert-Equal ($batch.results.transport -join ',') 'completed,timeout,connection_error,response_body_limit,curl_launch_error' 'transport classifications'
Assert-Equal $batch.results[0].http_result 'http_server_error' '5xx classification'
Assert-Equal $batch.results[1].network_result 'no_target_http_response' 'timeout after CONNECT'
Assert-Equal $batch.results[1].http_status $null 'missing target status'
Assert-Equal $batch.results[1].first_byte_ms $null 'missing first byte'
Assert-Equal $batch.results[2].network_result 'proxy_peer_unconfirmed' 'connection failure'
Assert-Equal $batch.results[2].curl_total_ms 0.733 'connection failure still records elapsed time'
Assert-Equal $batch.results[2].remote_peer_port $null 'missing peer port'
Assert-Equal $batch.results[3].http_status 200 'body limit preserves HTTP status'
Assert-Equal $batch.results[4].network_result 'metrics_unavailable' 'missing curl metrics'
Assert-Equal $batch.batch_exit_code 0 'failed transfers still form a complete batch'
Write-Output 'PASS offline argv, statuses, metrics, failure rows and batch semantics'

$pwsh = (Get-Process -Id $PID).Path
foreach ($invalid in @('https://127.0.0.1:7897', 'http://192.0.2.1:7897',
                       'http://localhost:7897', 'http://127.0.0.1:0',
                       'http://127.0.0.1:65536', 'http://user:fixture-secret@127.0.0.1:7897',
                       'http://127.0.0.1:7897/path?key=fixture-secret')) {
    $output = & $pwsh -NoProfile -NonInteractive -File $entry -Proxy $invalid
    Assert-Equal $LASTEXITCODE 2 'invalid proxy exit'
    $rejected = $output -join "`n" | ConvertFrom-Json
    Assert-Equal $rejected.batch_exit_code 2 'invalid proxy JSON'
    Assert-Equal $rejected.results.Count 0 'invalid proxy results'
    Assert-Equal ([bool]($output -match 'fixture-secret')) $false 'credentials excluded from output'
}
Write-Output 'PASS invalid and credential-bearing proxy inputs produce JSON and exit 2'

# Use reserved .invalid names so even a proxy-bypass regression stays offline.
$fixtureRoot = Join-Path (Join-Path $PSScriptRoot '../../runs') ('terminal-fixture-' + [guid]::NewGuid())
$fixtureEntry = Join-Path $fixtureRoot 'scripts/Test-TargetConnectivity.ps1'
$fixtureManifest = Join-Path $fixtureRoot 'contracts/targets.terminal-smoke.json'
$fixtureTargets = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../../contracts/targets.terminal-smoke.json') -Raw |
    ConvertFrom-Json
foreach ($target in $fixtureTargets.targets) { $target.url = "https://$($target.id.Replace('_', '-')).invalid/" }
New-Item -ItemType Directory -Force (Split-Path $fixtureEntry), (Split-Path $fixtureManifest) | Out-Null
Copy-Item -LiteralPath $entry -Destination $fixtureEntry
$fixtureTargets | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $fixtureManifest -Encoding utf8NoBOM

# The fixture rejects each CONNECT and never opens an upstream socket.
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$job = $null
try {
    $job = Start-ThreadJob -ArgumentList $listener -ScriptBlock {
        param($Listener)
        $ErrorActionPreference = 'Stop'
        $crlf = [string][char]13 + [char]10
        $reply = [Text.Encoding]::ASCII.GetBytes(
            ('HTTP/1.1 407 Proxy Authentication Required' + $crlf +
             'Proxy-Authenticate: Basic realm="fixture"' + $crlf +
             'Content-Length: 0' + $crlf + 'Connection: close' + $crlf + $crlf)
        )
        for ($i = 0; $i -lt 5; $i++) {
            $client = $Listener.AcceptTcpClient()
            try {
                $client.ReceiveTimeout = 3000
                $stream = $client.GetStream()
                $reader = [IO.StreamReader]::new($stream)
                $request = $reader.ReadLine()
                $headers = while (($line = $reader.ReadLine())) { $line }
                $stream.Write($reply, 0, $reply.Length)
                [pscustomobject]@{
                    request = $request
                    headers = @($headers)
                    local_port = $client.Client.LocalEndPoint.Port
                }
            } finally {
                $client.Dispose()
            }
        }
    }
    $previousNoProxy = $env:NO_PROXY
    $env:NO_PROXY = '*'
    try {
        $output = & $pwsh -NoProfile -NonInteractive -File $fixtureEntry -Proxy "http://127.0.0.1:$port"
        Assert-Equal $LASTEXITCODE 0 'loopback batch exit'
    } finally {
        $env:NO_PROXY = $previousNoProxy
    }
    $batch = $output -join "`n" | ConvertFrom-Json
    if ($batch.results[0].proxy_connect_status -ne 407) {
        throw "Expected fixture CONNECT response. First result: $($batch.results[0] | ConvertTo-Json -Compress)"
    }
    Assert-Equal $batch.batch_exit_code 0 'loopback batch JSON'
    Assert-Equal ($batch.results.id -join ',') $targetIds 'actual curl output order'
    Assert-Equal ($batch.results.proxy_connect_status -join ',') '407,407,407,407,407' 'actual CONNECT statuses'
    Assert-Equal (@($batch.results | Where-Object { $null -ne $_.http_status }).Count) 0 'CONNECT 407 is separate from target HTTP'
    Assert-Equal (@($batch.results | Where-Object { $_.network_result -eq 'proxy_tunnel_not_established' }).Count) 5 'failed tunnels'
    $requests = @(Receive-Job -Job $job -Wait)
    Assert-Equal (@($requests | Where-Object { $_.local_port -eq $port }).Count) 5 'fixture accepted five loopback peers'
    Assert-Equal ($requests.request -join ',') (
        ($fixtureTargets.targets | ForEach-Object { "CONNECT $(([Uri]$_.url).Host):443 HTTP/1.1" }) -join ','
    ) 'CONNECT targets'
    Assert-Equal (@($requests.headers | Where-Object { $_ -match '^(Proxy-Authorization|Authorization|Cookie|X-Api-Key):' }).Count) 0 'no credential headers'
} finally {
    $listener.Stop()
    if ($job) { Remove-Job -Job $job -Force }
    Remove-Item -LiteralPath $fixtureEntry, $fixtureManifest
    Remove-Item -LiteralPath (Split-Path $fixtureEntry), (Split-Path $fixtureManifest)
    Remove-Item -LiteralPath $fixtureRoot
}
Write-Output 'PASS native curl loopback CONNECT fixture, proxy override, JSON and no credentials'
