#Requires -Version 7.3
<#
.SYNOPSIS
Runs five read-only HTTPS GET probes through an explicit local HTTP proxy.
.DESCRIPTION
Windows, PowerShell 7.3+, and Windows system curl.exe 8.7+ are required.
Accepts http://127.0.0.1:PORT or http://[::1]:PORT. Emits one JSON object.
Exit 0 means all five target records were emitted. Exit 2 means setup failed.
Proxy peer evidence applies to the local socket only. API model calls are untested.
.EXAMPLE
pwsh -NoProfile -File scripts/Test-TargetConnectivity.ps1 -Proxy http://127.0.0.1:7897
#>
[CmdletBinding()]
param([string]$Proxy)

function Invoke-TerminalCurl {
    param([string]$CurlPath, [string[]]$CurlArguments)

    # Preserve the empty argument to --noproxy and retain curl failures as data.
    $PSNativeCommandArgumentPassing = 'Standard'
    $PSNativeCommandUseErrorActionPreference = $false
    try {
        $output = & $CurlPath @CurlArguments 2>$null
        [pscustomobject]@{ output = @($output); exit_code = $LASTEXITCODE }
    } catch {
        [pscustomobject]@{ output = @(); exit_code = $null }
    }
}

function Invoke-TerminalTargetBatch {
    param([string]$Proxy)

    if ($Proxy -cnotmatch '\Ahttp://(127\.0\.0\.1|\[::1\]):([1-9][0-9]{0,4})/?\z' -or [int]$Matches[2] -gt 65535) {
        throw 'Use an explicit HTTP proxy at 127.0.0.1 or [::1] with a port from 1 to 65535.'
    }
    $proxyHost = $Matches[1].Trim('[', ']')
    $proxyPort = [int]$Matches[2]
    $proxyUrl = $Proxy.TrimEnd('/')
    if (-not $IsWindows) { throw 'Windows is required.' }
    $curl = Join-Path ([Environment]::GetFolderPath('System')) 'curl.exe'
    $versionCheck = Invoke-TerminalCurl $curl @('-q', '--version')
    $version = [string]($versionCheck.output | Select-Object -First 1)
    if ($versionCheck.exit_code -ne 0 -or $version -cnotmatch '\Acurl ([0-9]+\.[0-9]+\.[0-9]+)\s' -or
        [version]$Matches[1] -lt [version]'8.7.0') {
        throw 'Windows system curl.exe 8.7.0 or newer is required.'
    }

    $manifestPath = Join-Path $PSScriptRoot '../contracts/targets.terminal-smoke.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    if ($manifest.schema_version -ne 1 -or $manifest.targets.Count -ne 5) {
        throw 'Invalid terminal target manifest.'
    }
    foreach ($target in $manifest.targets) {
        $uri = $null
        if (-not [Uri]::TryCreate($target.url, [UriKind]::Absolute, [ref]$uri) -or
            $uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or
            $target.kind -cnotin @('api', 'web') -or
            ($target.id -ceq 'anthropic_api' -and $target.anthropic_version -cnotmatch '\A[0-9]{4}-[0-9]{2}-[0-9]{2}\z')) {
            throw 'Invalid HTTPS target in the terminal target manifest.'
        }
    }

    $connectTimeout = 5
    $transferTimeout = 12
    $bodyLimit = 65536
    $common = @(
        '-q', '--proxy', $proxyUrl, '--noproxy', '', '--proto', '=https', '--globoff',
        '--request', 'GET', '--no-location', '--max-redirs', '0', '--retry', '0', '--no-netrc',
        '--connect-timeout', "$connectTimeout", '--max-time', "$transferTimeout",
        '--max-filesize', "$bodyLimit", '--silent', '--output', 'NUL',
        '--header', 'Authorization:', '--header', 'Cookie:', '--proxy-header', 'Proxy-Authorization:',
        '--write-out', '%{http_code}|%{time_starttransfer}|%{time_total}|%{proxy_used}|%{remote_ip}|%{remote_port}|%{http_connect}'
    )
    $culture = [Globalization.CultureInfo]::InvariantCulture
    $results = foreach ($target in $manifest.targets) {
        $curlArgs = $common + @('--url', $target.url)
        if ($target.id -ceq 'anthropic_api') {
            $curlArgs += @('--header', "anthropic-version: $($target.anthropic_version)")
        }
        $started = [DateTime]::UtcNow
        $transfer = Invoke-TerminalCurl $curl $curlArgs
        $ended = [DateTime]::UtcNow
        $exitCode = $transfer.exit_code
        $httpStatus = $connectStatus = $firstByte = $total = $proxyUsed = $remoteIp = $remotePort = $null
        $metrics = ($transfer.output -join '') -cmatch (
            '\A([0-9]{3})\|([0-9]+\.[0-9]+)\|([0-9]+\.[0-9]+)\|([01])\|([0-9A-Fa-f:.]*)\|(-1|[0-9]{1,5})\|([0-9]{3})\z'
        )
        if ($metrics) {
            if ([int]$Matches[1]) { $httpStatus = [int]$Matches[1] }
            if ([int]$Matches[7]) { $connectStatus = [int]$Matches[7] }
            $proxyUsed = $Matches[4] -eq '1'
            if ($Matches[5]) { $remoteIp = $Matches[5] }
            if ([int]$Matches[6] -gt 0) { $remotePort = [int]$Matches[6] }
            $total = [math]::Round([double]::Parse($Matches[3], $culture) * 1000, 3)
            if ($httpStatus) { $firstByte = [math]::Round([double]::Parse($Matches[2], $culture) * 1000, 3) }
        }
        $peerConfirmed = $proxyUsed -eq $true -and $remoteIp -eq $proxyHost -and $remotePort -eq $proxyPort
        $transport = switch ($exitCode) {
            $null { 'curl_launch_error' }
            0 { 'completed' }
            5 { 'proxy_resolution_error' }
            7 { 'connection_error' }
            28 { 'timeout' }
            35 { 'tls_handshake_error' }
            56 { 'receive_error' }
            60 { 'tls_certificate_error' }
            63 { 'response_body_limit' }
            default { 'curl_error' }
        }
        $network = if (-not $metrics) { 'metrics_unavailable' }
            elseif ($connectStatus -and ($connectStatus -lt 200 -or $connectStatus -ge 300)) { 'proxy_tunnel_not_established' }
            elseif (-not $peerConfirmed) { 'proxy_peer_unconfirmed' }
            elseif (-not $connectStatus) { 'proxy_tunnel_not_established' }
            elseif ($httpStatus) { 'target_http_response' }
            else { 'no_target_http_response' }
        $httpResult = switch ($httpStatus) {
            $null { 'no_http_response'; break }
            401 { if ($target.kind -eq 'api') { 'api_authentication_required' } else { 'http_unauthorized' }; break }
            403 { if ($target.kind -eq 'web') { 'web_restricted' } else { 'api_forbidden' }; break }
            429 { 'rate_limited'; break }
            { $_ -ge 200 -and $_ -lt 300 } { 'http_success'; break }
            { $_ -ge 300 -and $_ -lt 400 } { 'redirect_not_followed'; break }
            { $_ -ge 500 -and $_ -lt 600 } { 'http_server_error'; break }
            default { 'http_other' }
        }
        [ordered]@{
            id = $target.id
            url = $target.url
            kind = $target.kind
            started_utc = $started.ToString('o')
            ended_utc = $ended.ToString('o')
            http_status = $httpStatus
            proxy_connect_status = $connectStatus
            first_byte_ms = $firstByte
            curl_total_ms = $total
            proxy_used = $proxyUsed
            remote_peer_ip = $remoteIp
            remote_peer_port = $remotePort
            loopback_proxy_peer_confirmed = $peerConfirmed
            curl_exit_code = $exitCode
            transport = $transport
            network_result = $network
            http_result = $httpResult
            model_api_verified = if ($target.kind -eq 'api') { $false } else { $null }
        }
    }
    [ordered]@{
        schema_version = 1
        batch_exit_code = 0
        method = 'GET'
        proxy = $proxyUrl
        curl_version = $version
        route_scope = 'local HTTP proxy peer only'
        redirects_followed = $false
        connect_timeout_seconds = $connectTimeout
        max_time_per_request_seconds = $transferTimeout
        response_body_limit_bytes = $bodyLimit
        first_byte_metric = 'curl time_starttransfer from request start, includes connection and TLS time'
        anthropic_version = ($manifest.targets | Where-Object id -CEQ 'anthropic_api').anthropic_version
        results = @($results)
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    $ErrorActionPreference = 'Stop'
    try {
        $batch = Invoke-TerminalTargetBatch -Proxy $Proxy
    } catch {
        $batch = [ordered]@{
            schema_version = 1
            batch_exit_code = 2
            error = if ($_.Exception.Message -in @(
                'Use an explicit HTTP proxy at 127.0.0.1 or [::1] with a port from 1 to 65535.',
                'Windows is required.', 'Windows system curl.exe 8.7.0 or newer is required.'
            )) { $_.Exception.Message } else { 'Terminal smoke setup failed. Check the target manifest and local runtime.' }
            results = @()
        }
    }
    $batch | ConvertTo-Json -Depth 6
    exit $batch.batch_exit_code
}
