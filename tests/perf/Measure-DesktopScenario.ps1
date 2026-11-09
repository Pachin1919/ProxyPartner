# Called by Test-DesktopBaseline.ps1 after it verifies the candidate artifacts.
function Start-DesktopJob([string]$Name, [string]$Exe, [string[]]$AppArgs, [int]$Timeout, [int]$NodeCount = 100) {
    $pidFile = Join-Path $out "$Name.pid"
    $run = Start-Recorded $Name $pwsh @('-NoProfile','-NonInteractive','-File',
        (Join-Path $repo 'scripts/Measure-JobPeak.ps1'),'-Executable',$Exe,
        '-ArgumentsJson',(ConvertTo-Json -InputObject @($AppArgs) -Compress),'-Workload',"T03/${NodeCount}x3/$Name",
        '-ExpectedProcesses','1','-TimeoutSeconds',"$Timeout",'-StartedFile',$pidFile)
    $job = [pscustomobject]@{ run=$run; pidFile=$pidFile }
    $jobs.Add($job)
    $job
}
function Get-JobRoot($Job) {
    Wait-Value {
        if (Test-Path -LiteralPath $Job.pidFile) {
            $line = Get-Content -LiteralPath $Job.pidFile -Raw
            if ($line) { [int]$line }
        }
    } "$($Job.run.name) did not report a PID."
}
function Confirm-Job($Job, [int]$RootId) {
    $data = Receive-Recorded $Job.run 45 | ConvertFrom-Json
    Assert ($data.head_sha -ceq $head -and $data.status -eq 'MEASURED' -and $data.assigned_before_resume -and
        $data.total_processes -eq 1 -and $data.active_processes -eq 0 -and $data.root_exit_code -eq 0 -and
        $data.root_pid -eq $RootId -and !(Get-Process -Id $RootId -ErrorAction SilentlyContinue)) 'Incomplete Job lifecycle or cleanup.'
    $data
}
function Start-Sampling([string]$Phase, [int]$RootId, [int]$Seconds) {
    $csv = Join-Path $out "$Phase.csv"
    Start-Recorded "$Phase-sampling" $pwsh @('-NoProfile','-NonInteractive','-File',
        (Join-Path $repo 'scripts/Measure-ProcessTree.ps1'),'-RootPid',"$RootId",
        '-Seconds',"$Seconds",'-IntervalMs','250','-OutFile',$csv)
}
function Read-Budget([string]$Phase, [int]$Seconds) {
    $csv = Join-Path $out "$Phase.csv"
    $go = Join-Path (Split-Path -Parent $ToolchainRoot) 'orchestrator/go/bin/go.exe'
    $arguments = @('run','.','--csv',$csv,'--phase',$Phase,'--min-seconds',"$Seconds")
    $run = Start-Recorded "$Phase-budget" $go $arguments (Join-Path $repo 'tools/budget')
    Receive-Recorded $run | ConvertFrom-Json
}

if ($Check -eq '1000-node-stress') {
    . (Join-Path $PSScriptRoot 'Test-DesktopStress.ps1')
    return
}

$env:GOCACHE = Join-Path $out 'go-cache'
$env:GOPROXY = 'off'
$env:GOTOOLCHAIN = 'local'
$env:GOTELEMETRY = 'off'
$summary.host_before = Get-HostProcesses
$summary.listener_checks = @()
try {
    $comparison = Get-Artifact 'avalonia'
    $summary.avalonia_exe_sha256 = ($comparison.files | Where-Object name -eq 'ProxyPartner.exe').sha256
    $comparisonJob = Start-DesktopJob 'avalonia-cold-01' $comparison.exe @('--peak-probe') 40
    $comparisonId = Get-JobRoot $comparisonJob
    try {
        $comparisonWindow = Wait-Value {
            if ($comparisonJob.run.process.HasExited) { throw 'Avalonia exited before its window was observed.' }
            [DesktopWindows]::Find($comparisonId,$null,$true)
        } 'Avalonia window did not appear.'
        $summary.avalonia_window = Save-Window $comparisonId $comparisonWindow $comparison.exe 'avalonia-window'
    } catch {
        $summary.avalonia_window_error = $_.Exception.Message
    }
    $summary.avalonia = Confirm-Job $comparisonJob $comparisonId
    $summary.avalonia_decision = if (!$comparisonWindow) { 'WINDOW_UNVERIFIED' }
        elseif ($summary.avalonia.peak_job_commit_bytes -gt 40MB) { 'REJECTED_HARD_PEAK' }
        else { 'FULL_COMPARISON_REQUIRED' }
} catch {
    $summary.avalonia_error = $_.Exception.Message
    $summary.avalonia_decision = 'COMPARISON_FAILED'
}

$summary.win32_cold = @()
foreach ($iteration in 1..3) {
    $name = "win32-cold-0$iteration"
    $job = Start-DesktopJob $name $artifact.exe @() 1500
    $rootId = Get-JobRoot $job
    $panel = Wait-Value { Find-Panel $rootId } "No Win32 window in $name."
    $tray = Wait-Value { Find-Tray $rootId } "No Win32 tray HWND in $name."
    Assert ((Send-Number (Get-List $panel) 0x1004) -eq 100) 'Expected 100 rows after cold start.'
    if ($iteration -eq 3) {
        $cards = [DesktopWindows]::GetDlgItem($panel,106)
        Assert ([DesktopWindows]::IsWindowVisible($cards)) 'The Win32 window phase must start on the default card surface.'
        Assert-NoListeners $rootId
        $summary.listener_checks += [ordered]@{ phase='cold-window'; pid=$rootId; tcp_listen=0; udp=0 }
        Close-Panel $panel
        Start-Sleep -Seconds 5
        Assert-NoListeners $rootId
        $summary.listener_checks += [ordered]@{ phase='tray'; pid=$rootId; tcp_listen=0; udp=0 }
        $sampling = Start-Sampling 'tray' $rootId 600
        $null = Receive-Recorded $sampling 660
        $summary.tray = Read-Budget 'tray' 600
        $panel = Open-FromTray $rootId $tray
        $cards = [DesktopWindows]::GetDlgItem($panel,106)
        $search = [DesktopWindows]::GetDlgItem($panel,103)
        Assert-NoListeners $rootId
        $summary.listener_checks += [ordered]@{ phase='interaction'; pid=$rootId; tcp_listen=0; udp=0 }
        Start-Sleep -Seconds 3
        $sampling = Start-Sampling 'window' $rootId 300
        $turn = 0
        while (!$sampling.process.HasExited) {
            $null = Send-Number $cards 0x100 $(if ($turn % 2) { 0x24 } else { 0x23 })
            if ($turn % 4 -eq 0) {
                $null = Send-Number $panel 0x111 112
                $null = [DesktopWindows]::SendMessage($search,0xC,[IntPtr]::Zero,'香港')
            } else {
                $null = [DesktopWindows]::SendMessage($search,0xC,[IntPtr]::Zero,'')
            }
            $turn++
            Start-Sleep -Seconds 4
        }
        $null = Receive-Recorded $sampling 15
        $summary.interaction_turns = $turn
        $summary.window = Read-Budget 'window' 300
        $leak = @(for ($cycle = 1; $cycle -le 100; $cycle++) {
            Close-Panel $panel
            Assert ((Find-Panel $rootId) -eq 0) "Panel still open in cycle $cycle."
            if ($cycle -in 1,10,25,50,75,100) {
                $sample = Get-Resources $rootId
                $sample.cycle = $cycle
                [pscustomobject]$sample
            }
            $panel = Open-FromTray $rootId $tray
        })
        $leak | Export-Csv -NoTypeInformation -LiteralPath (Join-Path $out 'close-open.csv')
        $summary.close_reopen_cycles = 100
        $start = $leak | Where-Object cycle -eq 10
        $end = $leak | Where-Object cycle -eq 100
        Assert ($end.gdi -le $start.gdi -and $end.user -le $start.user -and $end.handles -le ($start.handles + 2)) 'Tray resources grew across 100 close/reopen cycles.'
    }
    Exit-Tray $tray
    $result = Confirm-Job $job $rootId
    $summary.win32_cold += $result
    Assert ($result.peak_job_commit_bytes -le 40MB) "Win32 hard peak exceeded in $name."
}
$summary.host_after = Get-HostProcesses
Assert ($summary.avalonia_decision -eq 'REJECTED_HARD_PEAK') "Avalonia comparison incomplete: $($summary.avalonia_decision)"
