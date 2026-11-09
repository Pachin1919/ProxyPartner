# budget — sampled performance gate
Development-only Go standard-library tool. Not bundled with the product.

From this directory:
`go test ./...`
`go run . --csv ../../runs/perf/samples.csv --phase tray --min-seconds 600`

Consumes Measure-ProcessTree.ps1 CSV; validates numeric/time/process coverage and checks sampled total product-process PrivateBytes. Phases: tray25MiB p95; window35; probe40; all sampled peaks≤40. Tray CPU uses one-core-equivalent percentage. Exit2 indicates failure.

A zero exit is explicitly **SAMPLED_PASS_REQUIRES_PEAK_PROOF**. Keep the sample report, the lifetime peak evidence and the 24h trend evidence as separate artifacts.

## Windows lifetime peak

Use Windows x64 and PowerShell 7.3+ from the repository root. The following command starts the executable in the caller's working directory and emits one JSON report.

```powershell
pwsh -NoProfile -NonInteractive -File scripts/Measure-JobPeak.ps1 -Executable 'C:/path/My App.exe' -ArgumentsJson '["--scenario","window"]' -Workload '100 nodes, 3 targets, 7 days' -TimeoutSeconds 600 -ExpectedProcesses 2
```

The launcher creates a private Job with kill-on-close and no breakaway flags. It starts the root suspended, assigns it before resume and waits for zero active members, including after root exit. The report includes the root exit code, lifetime member count, timeout and `PeakJobMemoryUsed` in bytes and MiB. Exit 0 means a complete measurement, and exit 2 means launch, accounting, timeout or workload failure. Supply `-ExpectedProcesses` when the workload has a known member count. The launcher requires an executable it can start in its Job. It cannot measure an already-running instance or processes started indirectly by an external service.

Use a new `-StartedFile` to obtain the suspended root PID before resume when running `scripts/Measure-ProcessTree.ps1` alongside the Job. Keep the target alive through the sampling window. Compare the completed Job peak with the existing 40 MiB gate separately from the sampling report. Fixture results exercise the tools and do not carry a product performance verdict.

```powershell
pwsh -NoProfile -NonInteractive -File tests/perf/Test-JobPeak.ps1 -Check short-child-peak -Go 'C:/path/go.exe' -EvidenceDirectory 'C:/path/new-evidence'
```

Run the same entry for `process-tree` and `baseline-format`. The checks write per-command output, timing, exit codes and fixture measurements into distinct subdirectories. The short-child check uses a deliberate sampling gap within the existing 2.5-second contract to demonstrate why the peak and the samples are separate.

The development launcher uses the public Win32 API through P/Invoke. It adds no runtime package dependency and copies no upstream code. [Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects) · [Peak field](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_extended_limit_information)
