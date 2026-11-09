#Requires -Version 7.3
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('aot-build','100-node-ui','dpi-tray','memory-gate')][string]$Check,
    [Parameter(Mandatory)][string]$ToolchainRoot,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [string]$TargetIcons
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (!$IsWindows -or ![IO.Path]::IsPathFullyQualified($EvidenceDirectory) -or
    ![IO.Path]::IsPathFullyQualified($ToolchainRoot)) { throw 'Windows and absolute evidence/toolchain paths are required.' }
$head = git -C $repo rev-parse HEAD
if ($LASTEXITCODE -or (git -C $repo status --porcelain)) { throw 'Run from a clean candidate worktree.' }
$out = Join-Path $EvidenceDirectory $Check
if (Test-Path -LiteralPath $out) { throw "Evidence already exists: $out" }
$null = New-Item -ItemType Directory -Path $out
$pwsh = (Get-Process -Id $PID).Path
$summary = [ordered]@{ status='FAIL'; check=$Check; head_sha=$head; cwd=$repo
    started_utc=[DateTime]::UtcNow.ToString('o'); os=[Runtime.InteropServices.RuntimeInformation]::OSDescription
    cpu=$env:PROCESSOR_IDENTIFIER; session=(Get-Process -Id $PID).SessionId
    command=@($pwsh,'-NoProfile','-NonInteractive','-File',$PSCommandPath,
        '-Check',$Check,'-ToolchainRoot',$ToolchainRoot,'-EvidenceDirectory',$EvidenceDirectory) }
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
$jobs = [Collections.Generic.List[object]]::new()
if ($TargetIcons) { $summary.command += @('-TargetIcons',$TargetIcons) }

function Assert($Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}
function Write-Json($Value, [string]$File) {
    ConvertTo-Json -InputObject $Value -Depth 9 | Set-Content -LiteralPath $File -Encoding utf8NoBOM
}
function Start-Recorded([string]$Name, [string]$Exe, [string[]]$Arguments, [string]$Cwd = $repo) {
    $start = [DateTime]::UtcNow.ToString('o')
    $info = [Diagnostics.ProcessStartInfo]::new($Exe)
    $info.WorkingDirectory = $Cwd
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    $owned.Add($process)
    [pscustomobject]@{ name=$Name; exe=$Exe; args=$Arguments; cwd=$Cwd; start=$start; process=$process
        stdout=$process.StandardOutput.ReadToEndAsync(); stderr=$process.StandardError.ReadToEndAsync() }
}
function Receive-Recorded($Run, [int]$TimeoutSeconds = 90) {
    Assert ($Run.process.WaitForExit($TimeoutSeconds * 1000)) "$($Run.name) timed out."
    $stdout = $Run.stdout.GetAwaiter().GetResult()
    $stderr = $Run.stderr.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $out "$($Run.name).stdout"), $stdout)
    [IO.File]::WriteAllText((Join-Path $out "$($Run.name).stderr"), $stderr)
    $code = $Run.process.ExitCode
    $record = [ordered]@{ head_sha=$head; exe=$Run.exe; arguments=$Run.args; cwd=$Run.cwd
        pid=$Run.process.Id; started_utc=$Run.start; ended_utc=$Run.process.ExitTime.ToUniversalTime().ToString('o'); exit_code=$code }
    Write-Json $record (Join-Path $out "$($Run.name).command.json")
    Assert ($code -eq 0) "$($Run.name) exited $code. See $out."
    $stdout
}
function Wait-Value([scriptblock]$Action, [string]$Failure) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $value = & $Action
        if ($value -and ($value -isnot [IntPtr] -or $value -ne [IntPtr]::Zero)) { return $value }
        Start-Sleep -Milliseconds 40
    } while ($timer.Elapsed.TotalSeconds -lt 12)
    throw $Failure
}
function Get-Artifact([string]$Variant) {
    $root = Join-Path $EvidenceDirectory "aot-build/$Variant"
    $artifact = Get-Content -Raw -LiteralPath (Join-Path $root 'artifact.json') | ConvertFrom-Json
    Assert ($artifact.head_sha -ceq $head -and $artifact.worktree -eq $repo) 'Artifact was not built at this SHA/worktree.'
    foreach ($file in $artifact.files) {
        $path = Join-Path $root "publish/$($file.name)"
        Assert ((Get-FileHash -LiteralPath $path).Hash -eq $file.sha256) "Artifact hash mismatch: $path"
    }
    Assert ($artifact.exe -eq (Join-Path $root 'publish/ProxyPartner.exe')) 'Unexpected artifact path.'
    $artifact
}
function Find-Panel([int]$Id) {
    [DesktopWindows]::Find($Id,'ProxyPartner.Panel',$true)
}
function Find-Tray([int]$Id) {
    [DesktopWindows]::Find($Id,'ProxyPartner.Tray',$false)
}
function Start-Panel($Artifact, [string]$Name = 'launch') {
    $run = Start-Recorded $Name $Artifact.exe @()
    $panel = Wait-Value { Find-Panel $run.process.Id } 'Product window did not appear.'
    $tray = Wait-Value { Find-Tray $run.process.Id } 'Product tray HWND did not appear.'
    [pscustomobject]@{ run=$run; panel=$panel; tray=$tray; id=$run.process.Id; exe=$Artifact.exe }
}
function Save-Window([int]$Id, [IntPtr]$Window, [string]$Exe, [string]$Name) {
    $process = Get-Process -Id $Id
    Assert ($process.Path -eq $Exe -and [DesktopWindows]::BelongsTo($Id,$Window)) 'Capture target identity mismatch.'
    $bounds = [DesktopWindows+Rect]::new()
    Assert ([DesktopWindows]::GetWindowRect($Window,[ref]$bounds)) 'Window bounds unavailable.'
    $image = [Drawing.Bitmap]::new(($bounds.Right-$bounds.Left),($bounds.Bottom-$bounds.Top))
    try {
        $graphics = [Drawing.Graphics]::FromImage($image)
        try {
            $dc = $graphics.GetHdc()
            try { Assert ([DesktopWindows]::PrintWindow($Window,$dc,2)) 'PrintWindow failed.' }
            finally { $graphics.ReleaseHdc($dc) }
        } finally { $graphics.Dispose() }
        $image.Save((Join-Path $out "$Name.png"),[Drawing.Imaging.ImageFormat]::Png)
    } finally { $image.Dispose() }
    [ordered]@{ pid=$Id; hwnd=$Window.ToInt64(); exe=$process.Path; method='PrintWindow'
        dpi=[DesktopWindows]::GetDpiForWindow($Window); width=$bounds.Right-$bounds.Left
        height=$bounds.Bottom-$bounds.Top; utc=[DateTime]::UtcNow.ToString('o') }
}
function Close-Panel([IntPtr]$Window) {
    $null = [DesktopWindows]::SendMessage($Window,0x10,[IntPtr]::Zero,[IntPtr]::Zero)
    Assert (![DesktopWindows]::IsWindow($Window)) 'Closing did not destroy the panel.'
}
function Open-FromTray([int]$Id, [IntPtr]$Tray) {
    # Explorer sends NIN_SELECT through this callback after tray activation.
    $null = [DesktopWindows]::SendMessage($Tray,0x8001,[IntPtr]::Zero,[IntPtr]0x10400)
    Wait-Value { Find-Panel $Id } 'Tray activation did not reopen the panel.'
}
function Exit-Tray([IntPtr]$Tray) {
    Assert ([DesktopWindows]::PostMessage($Tray,0x111,[IntPtr]102,[IntPtr]::Zero)) 'Cannot dispatch tray exit.'
}
function Get-List([IntPtr]$Panel) {
    $list = [DesktopWindows]::GetDlgItem($Panel,105)
    Assert ($list -ne 0) 'ListView not found.'
    $list
}
function Send-Number([IntPtr]$Window, [uint32]$Message, [long]$W = 0, [long]$L = 0) {
    [DesktopWindows]::SendMessage($Window,$Message,[IntPtr]$W,[IntPtr]$L).ToInt64()
}
function Get-Resources([int]$Id) {
    $p = Get-Process -Id $Id
    [ordered]@{ utc=[DateTime]::UtcNow.ToString('o'); private_bytes=$p.PrivateMemorySize64
        working_set_bytes=$p.WorkingSet64; handles=$p.HandleCount; threads=$p.Threads.Count
        gdi=[DesktopWindows]::GetGuiResources($p.Handle,0); user=[DesktopWindows]::GetGuiResources($p.Handle,1) }
}
function Get-HostProcesses {
    @(Get-Process -Name 'clash-verge','verge-mihomo','mihomo' -ErrorAction SilentlyContinue |
        Select-Object Id,ProcessName,PrivateMemorySize64,WorkingSet64)
}
function Assert-NoListeners([int]$Id) {
    $tcp = @(Get-NetTCPConnection -State Listen | Where-Object OwningProcess -eq $Id)
    $udp = @(Get-NetUDPEndpoint | Where-Object OwningProcess -eq $Id)
    Assert (!$tcp.Count -and !$udp.Count) "Product PID $Id owns a TCP listener or UDP endpoint."
}

try {
    Add-Type -Path (Join-Path $PSScriptRoot 'DesktopWindows.cs')
    Add-Type -AssemblyName System.Drawing.Common
    $null = [DesktopWindows]::SetThreadDpiAwarenessContext([IntPtr](-4))
    if ($Check -eq 'aot-build') {
        foreach ($variant in 'Win32','Avalonia') {
            $arguments = @('-NoProfile','-NonInteractive','-File',
                (Join-Path $repo 'build/Publish-DesktopBaseline.ps1'),'-ToolchainRoot',$ToolchainRoot,
                '-EvidenceDirectory',$out,'-Variant',$variant)
            if ($variant -eq 'Win32' -and $TargetIcons) { $arguments += @('-TargetIcons',$TargetIcons) }
            $run = Start-Recorded "publish-$variant" $pwsh $arguments
            $null = Receive-Recorded $run 240
        }
    }
    $artifact = Get-Artifact 'win32'
    $summary.exe = $artifact.exe
    $summary.exe_sha256 = ($artifact.files | Where-Object name -eq 'ProxyPartner.exe').sha256
    if ($Check -ne 'memory-gate') {
        $ui = Start-Panel $artifact
        $list = Get-List $ui.panel
        if ($Check -eq 'aot-build') {
            $summary.launch = Save-Window $ui.id $ui.panel $ui.exe 'launch'
        } elseif ($Check -eq '100-node-ui') {
            . (Join-Path $PSScriptRoot 'Test-CardLayout.ps1')
            Assert ((Send-Number $list 0x1004) -eq 100) 'Expected exactly 100 leaves.'
            $status = [DesktopWindows]::Text([DesktopWindows]::GetDlgItem($ui.panel,0))
            Assert ($status -like '*合成数据*实际链路：未知*100 行*') "Unexpected status: $status"
            $summary.initial_status = $status
            $summary.initial = Save-Window $ui.id $ui.panel $ui.exe '100-leaves'
            $search = [DesktopWindows]::GetDlgItem($ui.panel,103)
            $null = [DesktopWindows]::SendMessage($search,0xC,[IntPtr]::Zero,'同名节点')
            Assert ((Send-Number $list 0x1004) -eq 2) 'Two same-name leaf IDs should survive.'
            $summary.same_name = Save-Window $ui.id $ui.panel $ui.exe 'same-name'
            $null = [DesktopWindows]::SendMessage($search,0xC,[IntPtr]::Zero,'节点 001')
            Assert ((Send-Number $list 0x1004) -eq 1) 'Chinese filter should show one row.'
            $summary.zero_distinction = Save-Window $ui.id $ui.panel $ui.exe 'zero-and-unknown'
            $null = [DesktopWindows]::SendMessage($search,0xC,[IntPtr]::Zero,'')
            $null = Send-Number ([DesktopWindows]::GetDlgItem($ui.panel,104)) 0xF5
            Assert ((Send-Number $list 0x1004) -eq 102) 'Group toggle should add two groups.'
            $null = Send-Number ([DesktopWindows]::GetDlgItem($ui.panel,104)) 0xF5
            Assert ((Send-Number $list 0x1004) -eq 100) 'Group toggle should restore leaves.'
            $null = Send-Number $ui.panel 0x111 112
            $summary.sorted_ascending = Save-Window $ui.id $ui.panel $ui.exe 'native-ascending'
            $null = Send-Number $ui.panel 0x111 112
            $summary.sorted_descending = Save-Window $ui.id $ui.panel $ui.exe 'native-descending'
            $null = Send-Number $list 0x100 0x23
            $summary.top_index = Send-Number $list 0x1027
            $summary.selected_index = Send-Number $list 0x100C -1 2
            Assert ($summary.top_index -gt 0 -and $summary.selected_index -eq 99) 'ListView End key did not select and scroll to final row.'
            $summary.last_page = Save-Window $ui.id $ui.panel $ui.exe 'last-page'
            $bareDirectory = Join-Path $out 'no-icons'
            $null = New-Item -ItemType Directory -Path $bareDirectory
            $bareExe = Join-Path $bareDirectory 'ProxyPartner.exe'
            Copy-Item -LiteralPath $artifact.exe -Destination $bareExe
            Assert ((Get-FileHash -LiteralPath $bareExe).Hash -eq $summary.exe_sha256) 'Fallback binary hash differs.'
            $bare = Start-Panel @{exe=$bareExe} 'fallback-launch'
            $summary.icon_fallback = Save-Window $bare.id $bare.panel $bare.exe 'cards-without-icons'
            Exit-Tray $bare.tray
            $null = Receive-Recorded $bare.run 15
        } else {
            $physical = [ordered]@{}
            foreach ($display in [DesktopWindows]::Displays()) {
                $width = [Math]::Min(1340,$display.Right-$display.Left-30)
                $height = [Math]::Min(730,$display.Bottom-$display.Top-30)
                Assert ([DesktopWindows]::MoveWindow($ui.panel,$display.Left+10,$display.Top+10,$width,$height,$true)) 'MoveWindow failed.'
                $dpi = [DesktopWindows]::GetDpiForWindow($ui.panel)
                if (!$physical.Contains("$dpi")) { $physical["$dpi"] = Save-Window $ui.id $ui.panel $ui.exe "physical-$dpi" }
            }
            Assert ($physical.Contains('96') -and $physical.Contains('144')) 'Expected physical 100% and 150% displays.'
            $summary.physical = $physical
            $message = [ordered]@{}
            foreach ($dpi in 120,192,([int][DesktopWindows]::GetDpiForWindow($ui.panel))) {
                [DesktopWindows]::DpiMessage($ui.id,$ui.panel,$dpi)
                $firstColumn = Send-Number $list 0x101D
                Assert ($firstColumn -eq [int](192 * $dpi / 96)) 'WM_DPICHANGED did not resize the first column.'
                $message["$dpi"] = $firstColumn
            }
            $summary.native_message_column_widths = $message
            Assert ([DesktopWindows]::IsWindowVisible([DesktopWindows]::GetDlgItem($ui.panel,106))) 'The card surface disappeared after DPI changes.'
            Close-Panel $ui.panel
            Assert ((Find-Panel $ui.id) -eq 0 -and (Find-Tray $ui.id) -eq $ui.tray -and ![DesktopWindows]::IsWindow($list)) 'Panel/list disposal did not leave only the tray.'
            . (Join-Path $PSScriptRoot 'Invoke-DesktopTray.ps1')
        }
        if ($Check -ne 'dpi-tray') { Exit-Tray $ui.tray }
        $null = Receive-Recorded $ui.run 15
        Assert (![DesktopWindows]::IsWindow($ui.tray)) 'Tray HWND remained after exit.'
    } else {
        . (Join-Path $repo 'tests/perf/Measure-DesktopScenario.ps1')
    }
    $summary.status = 'PASS'
} catch {
    $summary.error = $_.Exception.Message
    $summary.error_at = $_.ScriptStackTrace
} finally {
    foreach ($ownedJob in $jobs) {
        if (!$ownedJob.run.process.HasExited -and (Test-Path -LiteralPath $ownedJob.pidFile)) {
            $jobId = [int](Get-Content -Raw -LiteralPath $ownedJob.pidFile)
            $tray = Find-Tray $jobId
            if ($tray -ne [IntPtr]::Zero) { Exit-Tray $tray }
            $null = $ownedJob.run.process.WaitForExit(10000)
        }
    }
    foreach ($process in $owned) {
        if (!$process.HasExited) {
            $process.Kill()
            $null = $process.WaitForExit(3000)
        }
        $process.Dispose()
    }
    $summary.ended_utc = [DateTime]::UtcNow.ToString('o')
    $summary.exit_code = if ($summary.status -eq 'PASS') { 0 } else { 1 }
    $summary.artifacts = @(Get-ChildItem -File -LiteralPath $out | ForEach-Object {
        [ordered]@{ path=$_.Name; sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() }
    })
    Write-Json $summary (Join-Path $out 'result.json')
    ConvertTo-Json -InputObject $summary -Depth 9
}
exit $summary.exit_code
