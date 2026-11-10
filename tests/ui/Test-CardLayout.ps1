# Called within the real Windows 100-node-ui run before the table checks.
$cards = [DesktopWindows]::GetDlgItem($ui.panel,106)
$view = [DesktopWindows]::GetDlgItem($ui.panel,107)
$search = [DesktopWindows]::GetDlgItem($ui.panel,103)
Assert ([DesktopWindows]::IsWindowVisible($cards) -and ![DesktopWindows]::IsWindowVisible($list)) 'Cards must be the default view.'

$bounds = [DesktopWindows+Rect]::new()
$client = [DesktopWindows+Rect]::new()
$screen = $null
foreach ($display in [DesktopWindows]::Displays()) {
    $null = [DesktopWindows]::MoveWindow($ui.panel,$display.Left+10,$display.Top+10,780,580,$true)
    if ([DesktopWindows]::GetDpiForWindow($ui.panel) -eq 96) { $screen = $display; break }
}
Assert ($null -ne $screen) 'No physical 100% monitor is available for the 1536/1280 width checks.'
$null = [DesktopWindows]::GetWindowRect($ui.panel,[ref]$bounds)
$null = [DesktopWindows]::GetClientRect($ui.panel,[ref]$client)
$frame = ($bounds.Right - $bounds.Left) - $client.Right
$checks = @(foreach ($sample in @(@{width=1536;columns=6},@{width=1280;columns=5},@{width=800;columns=3})) {
    Assert ([DesktopWindows]::MoveWindow($ui.panel,$screen.Left+10,$screen.Top+10,($sample.width+$frame),720,$true)) 'Window resize failed.'
    Assert ([DesktopWindows]::GetClientRect($ui.panel,[ref]$client) -and $client.Right -eq $sample.width) 'Actual panel client width differs.'
    $cardRect = [DesktopWindows+Rect]::new()
    $null = [DesktopWindows]::GetClientRect($cards,[ref]$cardRect)
    $expectedWidth = [int][Math]::Floor(($cardRect.Right - ($sample.columns-1)*20)/$sample.columns)
    $expectedHeight = [int][Math]::Floor(($expectedWidth*96+50)/100)
    $scroll = [DesktopWindows]::ReadScroll($cards)
    $expectedExtent = [int][Math]::Ceiling(100/$sample.columns)*($expectedHeight+20)-20
    Assert ($scroll.Max+1 -eq $expectedExtent -and $scroll.Page -eq $cardRect.Bottom) "Card layout/scroll range differs at $($sample.width)."
    [ordered]@{ client_width=$client.Right; viewport_width=$cardRect.Right; columns=$sample.columns
        card_width=$expectedWidth; card_height=$expectedHeight; ratio=[Math]::Round($expectedHeight/$expectedWidth,4)
        observed_scroll_max=$scroll.Max; expected_scroll_max=$expectedExtent-1
        window=(Save-Window $ui.id $ui.panel $ui.exe "cards-$($sample.width)") }
})
$summary.card_layout = $checks
$null = [DesktopWindows]::MoveWindow($ui.panel,$screen.Left+10,$screen.Top+10,(494+$frame),680,$true)
$narrow = [DesktopWindows+Rect]::new()
Assert ([DesktopWindows]::GetClientRect($cards,[ref]$narrow) -and $narrow.Right -lt 468) 'Expected a one-column viewport.'
$narrowHeight = [int][Math]::Floor(($narrow.Right*96+50)/100)
$checkX = $narrow.Right - 22
$checkY = [int][Math]::Floor($narrowHeight*0.08) + 17
$point = $checkX -bor ($checkY -shl 16)
$status = [DesktopWindows]::GetDlgItem($ui.panel,0)
$null = Send-Number $cards 0x201 1 $point
Assert (([DesktopWindows]::Text($status)) -like '*测试勾选 1*') 'The lower part of the checkbox did not accept a click.'
$null = Send-Number $cards 0x201 1 $point
Assert (([DesktopWindows]::Text($status)) -like '*测试勾选 0*') 'A second checkbox click did not clear the mark.'
$summary.narrow_checkbox = [ordered]@{ viewport_width=$narrow.Right; card_height=$narrowHeight; x=$checkX; y=$checkY }
$null = [DesktopWindows]::MoveWindow($ui.panel,$screen.Left+10,$screen.Top+10,(800+$frame),720,$true)
$hint = [DesktopWindows]::GetDlgItem($ui.panel,121)
$cardSize = $checks[-1]
$summary.site_hover_text = @(foreach ($site in @(@{slot=2;text='OpenAI API · 未测'},@{slot=3;text='ChatGPT · 105 ms'})) {
    $x = 17 + [int](($cardSize.card_width-34)*($site.slot+0.5)/5)
    $y = [int]($cardSize.card_height*0.63)
    $null = Send-Number $cards 0x200 0 ($x -bor ($y -shl 16))
    $actual = [DesktopWindows]::Text($hint)
    Assert ($actual -ceq $site.text) "Unexpected site hover text: $actual"
    $actual
})
$summary.site_hover_window = Save-Window $ui.id $ui.panel $ui.exe 'cards-hover'
$null = Send-Number $cards 0x100 0x23
$last = [DesktopWindows]::ReadScroll($cards)
Assert ($last.Position -eq $last.Max+1-$last.Page -and $last.Position -gt 0) 'End did not scroll to the final card.'
Assert (([DesktopWindows]::Text($hint)) -ceq '悬停查看站点状态') 'End left a hover hint from the previous card visible.'
$summary.card_end = Save-Window $ui.id $ui.panel $ui.exe 'cards-end'
$null = Send-Number $cards 0x100 0x20
Assert (([DesktopWindows]::Text([DesktopWindows]::GetDlgItem($ui.panel,0))) -like '*测试勾选 1*') 'Card selection did not produce a test check.'
$null = [DesktopWindows]::SendMessage($search,0xC,[IntPtr]::Zero,'节点 099')
$null = Send-Number $view 0xF5
Assert ([DesktopWindows]::IsWindowVisible($list) -and ![DesktopWindows]::IsWindowVisible($cards) -and
    (Send-Number $list 0x1004) -eq 1 -and (Send-Number $list 0x100C -1 2) -eq 0) 'Search and selected card did not carry into the table.'
$header = [IntPtr](Send-Number $list 0x101f)
Assert ((Send-Number $header 0x1200) -eq 10) 'Expected separate score, quality and test-selection columns.'
$null = [DesktopWindows]::MoveWindow($ui.panel,$screen.Left+10,$screen.Top+10,(1280+$frame),720,$true)
$summary.table_selection = Save-Window $ui.id $ui.panel $ui.exe 'table-selection'
$null = Send-Number $view 0xF5
Assert ([DesktopWindows]::IsWindowVisible($cards) -and
    ([DesktopWindows]::Text($search)) -eq '节点 099') 'Search text did not carry back to cards.'
$summary.cards_selection = Save-Window $ui.id $ui.panel $ui.exe 'cards-selection'
$null = Send-Number $view 0xF5
$null = [DesktopWindows]::SendMessage($search,0xC,[IntPtr]::Zero,'')
$summary.table_colors = Save-Window $ui.id $ui.panel $ui.exe 'table-colors'
