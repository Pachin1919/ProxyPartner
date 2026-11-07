# External sampling aid only. NOT proof of lifetime peak: very short children can escape polling.
# Formal release additionally requires Job Object peak/ETW evidence, see spec/PERFORMANCE.md.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateRange(1,2147483647)][int]$RootPid,
    [ValidateRange(2,86400)][int]$Seconds=600,
    [ValidateRange(100,5000)][int]$IntervalMs=250,
    [string]$OutFile='runs\perf\samples.csv'
)
$ErrorActionPreference='Stop'
$root=Get-Process -Id $RootPid -ErrorAction Stop
$rootStart=$root.StartTime.ToUniversalTime().Ticks
$directory=Split-Path -Parent $OutFile
if ($directory -ne '') { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
if (Test-Path -LiteralPath $OutFile) { throw 'Output already exists. Use a new file; do not overwrite evidence.' }
$known=@{}; $cpuLast=@{}; $cpuTotal=0.0
$known[$RootPid]=$rootStart
$watch=[Diagnostics.Stopwatch]::StartNew(); $lastDiscovery=-2.0; $firstSampleElapsed=$null
$writer=[IO.StreamWriter]::new($OutFile,$false,[Text.UTF8Encoding]::new($false))
try {
    $writer.WriteLine('elapsed_seconds,private_bytes,working_set_bytes,cpu_total_seconds,process_count,root_alive')
    while ($true) {
        if (($watch.Elapsed.TotalSeconds-$lastDiscovery) -ge 1.0) {
            $all=@(Get-CimInstance Win32_Process -Property ProcessId,ParentProcessId)
            $added=$true
            while ($added) {
                $added=$false
                foreach ($entry in $all) {
                    $child=[int]$entry.ProcessId; $parent=[int]$entry.ParentProcessId
                    if ($known.ContainsKey($parent) -and $known.ContainsKey($child) -eq $false) {
                        $p=Get-Process -Id $child -ErrorAction SilentlyContinue
                        $parentProcess=Get-Process -Id $parent -ErrorAction SilentlyContinue
                        if ($null -ne $p -and $null -ne $parentProcess) {
                            if ($parentProcess.StartTime.ToUniversalTime().Ticks -eq $known[$parent]) {
                                $known[$child]=$p.StartTime.ToUniversalTime().Ticks; $added=$true
                            }
                        }
                    }
                }
            }
            $lastDiscovery=$watch.Elapsed.TotalSeconds
        }
        [long]$private=0; [long]$working=0; $count=0; $alive=0
        foreach ($processId in @($known.Keys)) {
            $p=Get-Process -Id $processId -ErrorAction SilentlyContinue
            if ($null -eq $p) { continue }
            try {
                $p.Refresh()
                if ($p.StartTime.ToUniversalTime().Ticks -ne $known[$processId]) { continue }
                if ($processId -eq $RootPid) { $alive=1 }
                $private+=$p.PrivateMemorySize64; $working+=$p.WorkingSet64; $count++
                $key="$processId-$($known[$processId])"
                $current=$p.TotalProcessorTime.TotalSeconds
                if ($cpuLast.ContainsKey($key)) { $cpuTotal += [Math]::Max(0.0,$current-$cpuLast[$key]) }
                $cpuLast[$key]=$current
            } catch { throw "Cannot sample process $processId reliably: $($_.Exception.Message)" }
        }
        $sampleElapsed=$watch.Elapsed.TotalSeconds
        if ($null -eq $firstSampleElapsed) { $firstSampleElapsed=$sampleElapsed }
        $row=[string]::Format([Globalization.CultureInfo]::InvariantCulture,'{0:F6},{1},{2},{3:F6},{4},{5}',$sampleElapsed,$private,$working,$cpuTotal,$count,$alive)
        $writer.WriteLine($row); $writer.Flush()
        if ($alive -eq 0) { throw 'Root exited; run is incomplete, not a passing memory result.' }
        # Include an endpoint sample so the measured span is at least Seconds.
        if (($sampleElapsed-$firstSampleElapsed) -ge $Seconds) { break }
        Start-Sleep -Milliseconds $IntervalMs
    }
} finally { $writer.Dispose(); $watch.Stop() }
Write-Host "Sampling saved: $OutFile. Short-lived child and lifetime-peak proof are still required."
