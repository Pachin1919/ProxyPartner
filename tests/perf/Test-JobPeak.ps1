#Requires -Version 7.3
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('process-tree','short-child-peak','baseline-format')][string]$Check,
    [Parameter(Mandatory)][string]$Go,
    [Parameter(Mandatory)][string]$EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (-not $IsWindows -or -not [IO.Path]::IsPathFullyQualified($Go) -or
    -not [IO.Path]::IsPathFullyQualified($EvidenceDirectory)) { throw 'Use Windows and absolute paths.' }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$head = git -C $repo rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve HEAD.' }
$run = Join-Path $EvidenceDirectory $Check
if (Test-Path -LiteralPath $run) { throw "Evidence already exists: $run" }
$null = New-Item -ItemType Directory -Path $run
$pwsh = (Get-Process -Id $PID).Path
$measure = Join-Path $repo 'scripts/Measure-JobPeak.ps1'
$sampler = Join-Path $repo 'scripts/Measure-ProcessTree.ps1'
$fixture = Join-Path $run '进程 fixture.exe'
$env:GOCACHE = Join-Path $EvidenceDirectory 'go-cache'
$env:GOPROXY = 'off'
$env:GOTOOLCHAIN = 'local'
$env:GOTELEMETRY = 'off'
$env:CGO_ENABLED = '0'
$children = [Collections.Generic.List[object]]::new()
$clock = [Diagnostics.Stopwatch]::StartNew()
$summary = [ordered]@{ head_sha=$head; check=$Check; scope='fixture'; status='FAIL'; started_utc=[DateTime]::UtcNow.ToString('o') }

function Assert($Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Start-Recorded([string]$Name, [string]$Exe, [string[]]$Argv, [string]$WorkingDirectory = $repo) {
    $info = [Diagnostics.ProcessStartInfo]::new($Exe)
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8
    $info.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($argument in $Argv) { $info.ArgumentList.Add($argument) }
    $started = [DateTime]::UtcNow.ToString('o')
    $process = [Diagnostics.Process]::Start($info)
    $record = [pscustomobject]@{ name=$Name; process=$process; stdout=$process.StandardOutput.ReadToEndAsync()
        stderr=$process.StandardError.ReadToEndAsync(); command=@($Exe)+$Argv; cwd=$WorkingDirectory; start_utc=$started }
    $children.Add($record)
    $record
}

function Receive-Recorded($Record, [int]$Expected = 0, [int]$WaitMs = 40000) {
    if (-not $Record.process.WaitForExit($WaitMs)) { throw "Process did not finish: $($Record.name)" }
    $out = $Record.stdout.GetAwaiter().GetResult()
    $err = $Record.stderr.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $run "$($Record.name).stdout"), $out)
    [IO.File]::WriteAllText((Join-Path $run "$($Record.name).stderr"), $err)
    $code = $Record.process.ExitCode
    [ordered]@{ head_sha=$head; command=$Record.command; cwd=$Record.cwd; pid=$Record.process.Id
        start_utc=$Record.start_utc; end_utc=$Record.process.ExitTime.ToUniversalTime().ToString('o'); exit_code=$code } |
        ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8NoBOM (Join-Path $run "$($Record.name).command.json")
    Assert ($code -eq $Expected) "$($Record.name) exited $code, expected $Expected. See $run."
    $out
}

function Start-Measure([string]$Name, [string]$Exe, [string[]]$Argv, [int]$Members, [int]$Timeout = 15, [string]$Started = '') {
    $args = @('-NoProfile','-NonInteractive','-File',$measure,'-Executable',$Exe,
        '-ArgumentsJson',(ConvertTo-Json -InputObject @($Argv) -Compress),'-Workload',"fixture/$Name",
        '-ExpectedProcesses',"$Members",'-TimeoutSeconds',"$Timeout")
    if ($Started) { $args += @('-StartedFile',$Started) }
    Start-Recorded $Name $pwsh $args
}

function Wait-Value([scriptblock]$Read, $Record) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt 15) {
        $value = & $Read
        if ($value) { return $value }
        if ($Record.process.HasExited) {
            $null = Receive-Recorded $Record
            throw "Process finished before fixture was ready: $($Record.name)"
        }
        Start-Sleep -Milliseconds 15
    }
    throw "Timed out waiting for fixture data: $($Record.name)"
}

try {
    # GUI subsystem keeps console support processes out of the two-member fixture.
    $build = Start-Recorded 'build-fixture' $Go @('build','-trimpath','-ldflags=-H=windowsgui','-o',$fixture,(Join-Path $PSScriptRoot 'job-fixture.go'))
    $null = Receive-Recorded $build 0 90000
    $summary.fixture_sha256 = (Get-FileHash -LiteralPath $fixture).Hash.ToLowerInvariant()

    if ($Check -eq 'process-tree') {
        $job = Start-Measure 'root-and-child' $fixture @('root',$run,'','1048576','750','exit-first') 2
        $tree = Receive-Recorded $job | ConvertFrom-Json
        $allocated = (Get-Content -Raw (Join-Path $run 'allocated.txt')).Trim().Split(',')
        $rootEnd = [DateTimeOffset](Get-Content -Raw (Join-Path $run 'root-end.txt'))
        $childEnd = [DateTimeOffset](Get-Content -Raw (Join-Path $run 'child-end.txt'))
        Assert ($tree.status -eq 'MEASURED' -and $tree.assigned_before_resume -and $tree.total_processes -eq 2 -and
            $tree.active_processes -eq 0 -and $tree.root_exit_code -eq 0 -and [int]$allocated[0] -ne $tree.root_pid -and
            $childEnd -gt $rootEnd -and [DateTimeOffset]$tree.ended_utc -ge $childEnd) 'Job did not cover the child after root exit.'
        $summary.root_pid = $tree.root_pid
        $summary.child_pid = [int]$allocated[0]
        $summary.total_processes = $tree.total_processes
        $summary.peak_job_commit_bytes = $tree.peak_job_commit_bytes

        $argvTest = Start-Measure 'argv' $fixture @('argv',$run,'','a "quote"','trailing slash\') 1
        $null = Receive-Recorded $argvTest
        $fail = Start-Measure 'root-nonzero' $fixture @('fail',$run) 1
        $bad = Receive-Recorded $fail 2 | ConvertFrom-Json
        Assert ($bad.root_exit_code -eq 7 -and $bad.active_processes -eq 0 -and $bad.status -eq 'FAIL') 'Nonzero root exit was not propagated.'
        $missing = Start-Measure 'missing-executable' (Join-Path $run 'missing.exe') @() 1
        $bad = Receive-Recorded $missing 2 | ConvertFrom-Json
        Assert ($bad.error -match 'CreateProcessW failed' -and -not $bad.assigned_before_resume -and $null -eq $bad.peak_job_commit_bytes) 'Missing executable passed.'
        $mismatch = Start-Measure 'count-mismatch' $fixture @('root',$run,'','0','0','') 2
        $bad = Receive-Recorded $mismatch 2 | ConvertFrom-Json
        Assert ($bad.total_processes -eq 1 -and $bad.error -match 'member count') 'Wrong member count passed.'

        $timeoutDir = Join-Path $run 'timeout fixture'
        $null = New-Item -ItemType Directory $timeoutDir
        $timeout = Start-Measure 'timeout' $fixture @('root',$timeoutDir,'','1048576','30000','') 2 2
        $bad = Receive-Recorded $timeout 2 | ConvertFrom-Json
        Assert ($bad.timed_out -and $bad.total_processes -eq 2 -and $bad.active_processes -eq 0 -and $bad.root_exit_code -eq 124) 'Timeout did not drain the owned Job.'
        Assert (-not (Test-Path (Join-Path $timeoutDir 'child-end.txt'))) 'Timed-out child completed unexpectedly.'
    } else {
        $short = $Check -eq 'short-child-peak'
        $commitBytes = if ($short) { 64MB } else { 0 }
        $members = if ($short) { 2 } else { 1 }
        $interval = if ($short) { 1800 } else { 250 }
        $started = Join-Path $run 'root-pid.txt'
        $csv = Join-Path $run 'samples.csv'
        $job = Start-Measure $Check $fixture @('root',$run,'trigger.txt',"$commitBytes",'35','finish') $members 25 $started
        $rootId = Wait-Value { if (Test-Path -LiteralPath $started) { [int](Get-Content -Raw -LiteralPath $started) } } $job
        $sample = Start-Recorded 'sampling' $pwsh @('-NoProfile','-NonInteractive','-File',$sampler,
            '-RootPid',"$rootId",'-Seconds','2','-IntervalMs',"$interval",'-OutFile',$csv)
        $null = Wait-Value { if (Test-Path -LiteralPath $csv) { @(Import-Csv -LiteralPath $csv).Count -ge 1 } } $sample
        $summary.trigger_utc = [DateTime]::UtcNow.ToString('o')
        New-Item -ItemType File -Path (Join-Path $run 'trigger.txt') | Out-Null
        if ($short) {
            $null = Wait-Value { Test-Path -LiteralPath (Join-Path $run 'child-wait.txt') } $job
            $summary.csv_rows_at_child_exit = @(Import-Csv -LiteralPath $csv).Count
        }
        $null = Receive-Recorded $sample
        New-Item -ItemType File -Path (Join-Path $run 'finish.txt') | Out-Null
        $peak = Receive-Recorded $job | ConvertFrom-Json
        $samples = @(Import-Csv -LiteralPath $csv)
        Assert ($peak.status -eq 'MEASURED' -and $peak.total_processes -eq $members -and $peak.active_processes -eq 0 -and
            $peak.assigned_before_resume -and $peak.root_exit_code -eq 0 -and $peak.root_pid -eq $rootId -and
            $samples.Count -ge 2 -and @($samples | Where-Object { $_.root_alive -ne '1' }).Count -eq 0) 'Incomplete lifecycle or sampling.'
        $summary.peak_job_commit_bytes = $peak.peak_job_commit_bytes
        $summary.peak_job_commit_mib = $peak.peak_job_commit_mib
        $summary.sampled_peak_private_bytes = ($samples | ForEach-Object { [long]$_.private_bytes } | Measure-Object -Maximum).Maximum
        if ($short) {
            $allocated = (Get-Content -Raw (Join-Path $run 'allocated.txt')).Trim().Split(',')
            Assert ([long]$allocated[1] -eq $commitBytes -and [int]$allocated[0] -ne $rootId -and
                $summary.csv_rows_at_child_exit -eq 1 -and @($samples | Where-Object { $_.process_count -ne '1' }).Count -eq 0 -and
                $peak.peak_job_commit_bytes -ge $commitBytes -and $summary.sampled_peak_private_bytes -lt $commitBytes) 'Did not demonstrate a missed child and captured Job peak.'
            $summary.root_pid = $rootId
            $summary.child_pid = [int]$allocated[0]
            $summary.child_committed_bytes = $commitBytes
        } else {
            Assert ($peak.head_sha -eq $head -and $peak.schema_version -eq 1 -and $peak.workload -eq 'fixture/baseline-format' -and
                $peak.os -and $peak.cwd -eq $repo -and $peak.executable -eq $fixture -and $peak.arguments.Count -eq 6 -and
                $peak.elapsed_seconds -gt 0 -and [DateTimeOffset]$peak.ended_utc -gt [DateTimeOffset]$peak.started_utc -and
                $peak.exit_code -eq 0 -and $peak.peak_job_commit_mib -eq ($peak.peak_job_commit_bytes / 1MB)) 'Peak JSON contract mismatch.'
            $budgetDir = Join-Path $repo 'tools/budget'
            $test = Start-Recorded 'budget-tests' $Go @('test','./...') $budgetDir
            $null = Receive-Recorded $test 0 90000
            $budget = Start-Recorded 'budget-analysis' $Go @('run','.','--csv',$csv,'--phase','probe','--min-seconds','2') $budgetDir
            $analysis = Receive-Recorded $budget | ConvertFrom-Json
            Assert ($analysis.status -eq 'SAMPLED_PASS_REQUIRES_PEAK_PROOF' -and $analysis.requires_lifetime_peak_proof) 'Unexpected existing budget result.'
        }
    }
    $summary.status = 'PASS'
} catch {
    $summary.error = $_.Exception.Message
} finally {
    foreach ($child in $children) {
        if (-not $child.process.HasExited) {
            $child.process.Kill()
            $null = $child.process.WaitForExit(3000)
        }
        $child.process.Dispose()
    }
    $summary.ended_utc = [DateTime]::UtcNow.ToString('o')
    $summary.elapsed_seconds = $clock.Elapsed.TotalSeconds
    $summary.exit_code = if ($summary.status -eq 'PASS') { 0 } else { 1 }
    $output = $summary | ConvertTo-Json -Depth 8
    $output | Set-Content -Encoding utf8NoBOM (Join-Path $run 'result.json')
    $output
}
exit $summary.exit_code
