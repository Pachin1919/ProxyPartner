#Requires -Version 7.3
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [string]$ArgumentsJson = '[]',
    [Parameter(Mandatory)][string]$Workload,
    [ValidateRange(1,86400)][int]$TimeoutSeconds = 600,
    [ValidateRange(0,2147483647)][int]$ExpectedProcesses = 0,
    [string]$StartedFile
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$report = [ordered]@{
    schema_version = 1
    status = 'FAIL'
    method = 'Windows Job Object PeakJobMemoryUsed'
    head_sha = $null
    os = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    workload = $Workload
    executable = $Executable
    arguments = @()
    cwd = $PWD.Path
    started_utc = [DateTime]::UtcNow.ToString('o')
    timeout_seconds = $TimeoutSeconds
    expected_processes = if ($ExpectedProcesses) { $ExpectedProcesses } else { $null }
}
$clock = [Diagnostics.Stopwatch]::StartNew()
$exitCode = 2
try {
    if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
        throw 'Windows x64 PowerShell is required.'
    }
    $report.head_sha = git -C $PSScriptRoot rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve measurement tool HEAD.' }
    $argv = ConvertFrom-Json -InputObject $ArgumentsJson -NoEnumerate
    if ($argv -isnot [array] -or @($argv | Where-Object { $_ -isnot [string] -or $_.Contains([char]0) }).Count) {
        throw 'ArgumentsJson must be an array of strings without NUL characters.'
    }
    $report.arguments = $argv
    $report.executable = [IO.Path]::GetFullPath($Executable, $report.cwd)
    if ($StartedFile) { $StartedFile = [IO.Path]::GetFullPath($StartedFile, $report.cwd) }

    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

public static class JobPeak
{
    const uint KillOnClose = 0x2000;
    const uint CreateSuspended = 0x4;
    const uint CreateNoWindow = 0x08000000;
    const int BasicAccounting = 1;
    const int ExtendedLimits = 9;

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint Priority, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Extended
    {
        public BasicLimits Basic;
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Accounting
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaultCount, TotalProcesses, ActiveProcesses, LimitTerminations;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Startup
    {
        public uint Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedData, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInfo
    {
        public IntPtr Process, Thread;
        public uint Pid, Tid;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern IntPtr CreateJobObjectW(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref Extended data, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out Extended data, int size, IntPtr unused);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out Accounting data, int size, IntPtr unused);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern bool CreateProcessW(string path, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string cwd,
        ref Startup startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateJobObject(IntPtr job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    public sealed class Result
    {
        public uint? root_pid, root_exit_code, total_processes, active_processes;
        public ulong? peak_bytes;
        public bool timed_out, assigned_before_resume;
        public string error;
    }

    static void Require(bool success, string operation)
    {
        if (success) return;
        int code = Marshal.GetLastWin32Error();
        throw new Win32Exception(code, operation + " failed (" + code + "): " + new Win32Exception(code).Message);
    }

    // Escape for the Windows CRT argv rules, including empty args and trailing backslashes.
    static string Quote(string arg) =>
        "\"" + Regex.Replace(arg, @"(\\*)(""|$)", m =>
            m.Groups[1].Value + m.Groups[1].Value + (m.Groups[2].Value == "\"" ? "\\\"" : "")) + "\"";

    static Accounting ReadAccounting(IntPtr job)
    {
        Require(QueryInformationJobObject(job, BasicAccounting, out Accounting stats,
            Marshal.SizeOf<Accounting>(), IntPtr.Zero), "QueryInformationJobObject(accounting)");
        return stats;
    }

    public static Result Measure(string path, string[] args, string cwd, int timeoutSeconds, int expected, string startedFile)
    {
        var result = new Result();
        IntPtr job = IntPtr.Zero;
        ProcessInfo process = default;
        try
        {
            job = CreateJobObjectW(IntPtr.Zero, null);
            Require(job != IntPtr.Zero, "CreateJobObjectW");
            var limits = new Extended();
            limits.Basic.Flags = KillOnClose;
            Require(SetInformationJobObject(job, ExtendedLimits, ref limits, Marshal.SizeOf<Extended>()),
                "SetInformationJobObject");
            var startup = new Startup { Size = (uint)Marshal.SizeOf<Startup>() };
            var command = new StringBuilder(Quote(path));
            foreach (string arg in args) command.Append(' ').Append(Quote(arg));
            Require(CreateProcessW(path, command, IntPtr.Zero, IntPtr.Zero, false, CreateSuspended | CreateNoWindow,
                IntPtr.Zero, cwd, ref startup, out process), "CreateProcessW");
            result.root_pid = process.Pid;
            Require(AssignProcessToJobObject(job, process.Process), "AssignProcessToJobObject");
            result.assigned_before_resume = true;
            Accounting initial = ReadAccounting(job);
            if (initial.TotalProcesses != 1 || initial.ActiveProcesses != 1)
                throw new InvalidOperationException("The suspended root must be the only initial Job member.");
            if (!string.IsNullOrEmpty(startedFile))
            {
                using var stream = new FileStream(startedFile, FileMode.CreateNew, FileAccess.Write);
                using var writer = new StreamWriter(stream);
                writer.WriteLine(process.Pid);
            }
            Require(ResumeThread(process.Thread) != uint.MaxValue, "ResumeThread");

            var watch = Stopwatch.StartNew();
            Accounting stats = ReadAccounting(job);
            while (stats.ActiveProcesses != 0 && watch.Elapsed.TotalSeconds < timeoutSeconds)
            {
                Thread.Sleep(15);
                stats = ReadAccounting(job);
            }
            if (stats.ActiveProcesses != 0)
            {
                result.timed_out = true;
                Require(TerminateJobObject(job, 124), "TerminateJobObject");
                watch.Restart();
                do
                {
                    Thread.Sleep(15);
                    stats = ReadAccounting(job);
                } while (stats.ActiveProcesses != 0 && watch.Elapsed.TotalSeconds < 5);
            }
            result.total_processes = stats.TotalProcesses;
            result.active_processes = stats.ActiveProcesses;
            Require(QueryInformationJobObject(job, ExtendedLimits, out Extended final,
                Marshal.SizeOf<Extended>(), IntPtr.Zero), "QueryInformationJobObject(limits)");
            result.peak_bytes = final.PeakJobMemoryUsed.ToUInt64();
            if (stats.ActiveProcesses != 0 || stats.TotalProcesses == 0 || result.peak_bytes == 0)
                throw new InvalidOperationException("Incomplete Job accounting.");
            Require(GetExitCodeProcess(process.Process, out uint exit), "GetExitCodeProcess");
            result.root_exit_code = exit;
            if (result.timed_out) result.error = "The Job exceeded its timeout.";
            else if (expected != 0 && stats.TotalProcesses != (uint)expected)
                result.error = "Job lifetime member count differs from the expected workload.";
            else if (exit != 0) result.error = "The root process exited nonzero.";
        }
        catch (Exception error) when (error is Win32Exception || error is IOException ||
                                      error is UnauthorizedAccessException || error is InvalidOperationException)
        {
            result.error = error.Message;
        }
        finally
        {
            if (process.Process != IntPtr.Zero && !result.assigned_before_resume &&
                !TerminateProcess(process.Process, 125))
                result.error += " Cleanup of the suspended process failed: " + Marshal.GetLastWin32Error();
            if (job != IntPtr.Zero) CloseHandle(job); // Kills any owned process still active on failure.
            if (process.Thread != IntPtr.Zero) CloseHandle(process.Thread);
            if (process.Process != IntPtr.Zero) CloseHandle(process.Process);
        }
        return result;
    }
}
'@

    $result = [JobPeak]::Measure($report.executable, [string[]]$argv, $report.cwd, $TimeoutSeconds, $ExpectedProcesses, $StartedFile)
    $report.root_pid = $result.root_pid
    $report.assigned_before_resume = $result.assigned_before_resume
    $report.root_exit_code = $result.root_exit_code
    $report.total_processes = $result.total_processes
    $report.active_processes = $result.active_processes
    $report.timed_out = $result.timed_out
    $report.peak_job_commit_bytes = $result.peak_bytes
    $report.peak_job_commit_mib = if ($null -ne $result.peak_bytes) { $result.peak_bytes / 1MB } else { $null }
    if ($result.error) { throw $result.error }
    $report.status = 'MEASURED'
    $exitCode = 0
} catch {
    $report.error = $_.Exception.Message
} finally {
    $report.ended_utc = [DateTime]::UtcNow.ToString('o')
    $report.elapsed_seconds = $clock.Elapsed.TotalSeconds
    $report.exit_code = $exitCode
}
$report | ConvertTo-Json -Depth 6
exit $exitCode
