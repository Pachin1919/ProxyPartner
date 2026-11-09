# Run the 1000-node workload separately from the fixed 100-node phases.
$summary.workload = [ordered]@{ nodes=1000; targets=3; history_days=7 }
$summary.host_before = Get-HostProcesses
$job = Start-DesktopJob 'win32-1000' $artifact.exe @('--stress-1000') 90 1000
$rootId = Get-JobRoot $job
$panel = Wait-Value { Find-Panel $rootId } 'No Win32 window in the stress workload.'
$tray = Wait-Value { Find-Tray $rootId } 'No Win32 tray HWND in the stress workload.'
$list = Get-List $panel
$cards = [DesktopWindows]::GetDlgItem($panel,106)
$view = [DesktopWindows]::GetDlgItem($panel,107)
$search = [DesktopWindows]::GetDlgItem($panel,103)
$groups = [DesktopWindows]::GetDlgItem($panel,104)
$status = [DesktopWindows]::GetDlgItem($panel,0)
Assert ([DesktopWindows]::IsWindowVisible($cards) -and (Send-Number $list 0x1004) -eq 1000) 'Stress workload did not open with 1000 leaves.'
Assert-NoListeners $rootId
$summary.listener_check = [ordered]@{ pid=$rootId; tcp_listen=0; udp=0 }

$null = Send-Number $cards 0x100 0x23
$scroll = [DesktopWindows]::ReadScroll($cards)
Assert ($scroll.Position -gt 0 -and $scroll.Position -eq $scroll.Max + 1 - $scroll.Page) 'Card End did not reach the stress workload tail.'
$summary.card_scroll_end = $scroll
$summary.cards_end = Save-Window $rootId $panel $artifact.exe 'stress-cards-end'
$null = Send-Number $cards 0x100 0x20
Assert (([DesktopWindows]::Text($status)) -like '*测试勾选 1*') 'Stress card check did not persist.'
$null = [DesktopWindows]::SendMessage($search,0xC,[IntPtr]::Zero,'节点 999')
$null = Send-Number $view 0xF5
Assert ([DesktopWindows]::IsWindowVisible($list) -and (Send-Number $list 0x1004) -eq 1 -and
    (Send-Number $list 0x100C -1 2) -eq 0 -and ([DesktopWindows]::Text($status)) -like '*测试勾选 1*') 'Stress card selection or filter did not transfer to the table.'
$summary.filtered_table = Save-Window $rootId $panel $artifact.exe 'stress-filtered-table'
$null = [DesktopWindows]::SendMessage($search,0xC,[IntPtr]::Zero,'')
$null = Send-Number $groups 0xF5
Assert ((Send-Number $list 0x1004) -eq 1002) 'Stress group toggle lost rows.'
$null = Send-Number $groups 0xF5
Assert ((Send-Number $list 0x1004) -eq 1000) 'Stress leaf count was not restored.'
$null = Send-Number $panel 0x111 112
$null = Send-Number $list 0x100 0x23
$summary.table_selected_end = Send-Number $list 0x100C -1 2
Assert ($summary.table_selected_end -eq 999 -and (Send-Number $list 0x1027) -gt 0) 'The sorted stress table did not reach its final row.'
$summary.table_end = Save-Window $rootId $panel $artifact.exe 'stress-table-end'
Assert-NoListeners $rootId

Exit-Tray $tray
$summary.job = Confirm-Job $job $rootId
Assert ($summary.job.peak_job_commit_bytes -le 40MB) 'Stress process-tree peak exceeds 40 MiB.'
$summary.host_after = Get-HostProcesses
