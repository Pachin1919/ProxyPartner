# Dot-sourced by Test-DesktopBaseline.ps1 with a fresh isolated settings path.
function Click-Control([IntPtr]$Window, [int]$Id) {
    $control = [DesktopWindows]::GetDlgItem($Window,$Id)
    Assert ($control -ne 0) "Missing button $Id."
    $null = Send-Number $control 0xF5
}
function Open-Auxiliary([int]$ButtonId) {
    Click-Control $ui.panel $ButtonId
    Wait-Value { [DesktopWindows]::Find($ui.id,'ProxyPartner.Auxiliary',$true) } 'Auxiliary window did not appear.'
}
function Read-Control([IntPtr]$Window, [int]$Id) {
    [DesktopWindows]::Text([DesktopWindows]::GetDlgItem($Window,$Id))
}
function Write-Control([IntPtr]$Window, [int]$Id, [string]$Value) {
    $control = [DesktopWindows]::GetDlgItem($Window,$Id)
    Assert ($control -ne 0) "Missing edit $Id."
    $null = [DesktopWindows]::SendMessage($control,0xC,[IntPtr]::Zero,$Value)
}
function Read-Weights([IntPtr]$Settings) {
    (210..215 | ForEach-Object { Read-Control $Settings $_ }) -join ','
}
function Target-Count([IntPtr]$Settings) {
    Send-Number ([DesktopWindows]::GetDlgItem($Settings,220)) 0x18B
}
function Limit-SettingsHeight([IntPtr]$Settings, [int]$Height) {
    $bounds = [DesktopWindows+Rect]::new()
    Assert ([DesktopWindows]::GetWindowRect($Settings,[ref]$bounds)) 'Settings bounds unavailable.'
    if ($bounds.Bottom-$bounds.Top -gt $Height) {
        Assert ([DesktopWindows]::MoveWindow($Settings,$bounds.Left,$bounds.Top,
            ($bounds.Right-$bounds.Left),$Height,$true)) 'Could not constrain the test window.'
    }
}
function Select-FirstTarget([IntPtr]$Settings) {
    $targets = [DesktopWindows]::GetDlgItem($Settings,220)
    Assert ((Send-Number $targets 0x186 0) -eq 0) 'Target could not be selected.'
    $null = Send-Number $Settings 0x111 (220 + 0x10000) $targets.ToInt64()
}
function Select-SampleNode([string]$Name) {
    Write-Control $ui.panel 103 $Name
    $null = Send-Number ([DesktopWindows]::GetDlgItem($ui.panel,106)) 0x100 0x24
}

$settingsFile = $env:PROXYPARTNER_DESKTOP_SETTINGS
$summary.dark_zh = Save-Window $ui.id $ui.panel $ui.exe 'dark-zh'
Select-SampleNode '节点 004'
$details = Open-Auxiliary 125
$detailZh = Read-Control $details 240
Assert ($detailZh -like '*节点 004*OPENAI*401 需 API 密钥*GPT*403 受限*无法判定*' -and
    $detailZh -like '*合成*') "Missing evidence in Chinese node details: $detailZh"
$summary.details_zh = Save-Window $ui.id $details $ui.exe 'details-zh'
Close-Panel $details
Select-SampleNode '节点 005'
$details = Open-Auxiliary 125
Assert ((Read-Control $details 240) -match '评分\s+87\b') 'Expected the fixed score for node 005.'
Close-Panel $details
Write-Control $ui.panel 103 ''

$settings = Open-Auxiliary 124
Assert ((Read-Weights $settings) -eq '50,35,15,60,30,10') 'Default weights differ.'
$summary.defaults_zh = Save-Window $ui.id $settings $ui.exe 'settings-zh'
$summary.example_before = Read-Control $settings 233
Write-Control $settings 210 '51'
Click-Control $settings 227
Assert ([DesktopWindows]::IsWindow($settings) -and (Read-Control $settings 232) -like '*100%*' -and
    !(Test-Path -LiteralPath $settingsFile)) 'Invalid components wrote preferences.'
for ($i = 0; $i -lt 6; $i++) {
    Write-Control $settings (210 + $i) (@('70','20','10','50','40','10')[$i])
}
$summary.example_after = Read-Control $settings 233
Assert ($summary.example_after -ne $summary.example_before -and
    $summary.example_after -match 'OPENAI 86[.,]0 / GPT 72[.,]5 / GitHub 98[.,]0 → 81[.,]8') 'Synthetic calculation did not update.'
Close-Panel $settings

# Use the actual smallest work area. Scroll first and prove visibility before interacting.
$displays = @([DesktopWindows]::Displays() | Sort-Object { ($_.Right - $_.Left) * ($_.Bottom - $_.Top) })
$small = $displays[0]
Assert ([DesktopWindows]::MoveWindow($ui.panel,$small.Left+8,$small.Top+8,($small.Right-$small.Left-16),
    ($small.Bottom-$small.Top-16),$true)) 'Could not move the panel to the smaller display.'
$settings = Open-Auxiliary 124
Assert ([DesktopWindows]::GetDpiForWindow($ui.panel) -eq 96 -and
    [DesktopWindows]::GetDpiForWindow($settings) -eq 96) 'Small physical display should be 96 DPI.'
$summary.physical_small = @{ work_width=$small.Right-$small.Left; work_height=$small.Bottom-$small.Top
    settings=Save-Window $ui.id $settings $ui.exe 'settings-physical-96' }
# The display resolution can change. Replay the prior 487 px window cap when its work area is taller.
Limit-SettingsHeight $settings 487
$null = Send-Number $settings 0x115 6
$summary.small_zh_top = Save-Window $ui.id $settings $ui.exe 'settings-small-top'
Assert (![DesktopWindows]::ControlInClient($settings,227)) "Small display must exercise scrolling. Work area $($small.Right-$small.Left)x$($small.Bottom-$small.Top), Settings $($summary.small_zh_top.width)x$($summary.small_zh_top.height)."
for ($i = 0; $i -lt 6; $i++) {
    Write-Control $settings (210 + $i) (@('70','20','10','50','40','10')[$i])
}
$null = Send-Number $settings 0x115 7
foreach ($id in 220,221,222,223,224,225,226,227) {
    Assert ([DesktopWindows]::ControlInClient($settings,$id)) "Settings control $id is clipped after scrolling."
}
$summary.small_zh_scrolled = Save-Window $ui.id $settings $ui.exe 'settings-small-scrolled'

Write-Control $settings 222 'EX'
Write-Control $settings 223 '+'
foreach ($invalid in 'file:///C:/sample','https://user:pass@example.invalid/test') {
    Write-Control $settings 221 $invalid
    Click-Control $settings 224
    Assert ((Target-Count $settings) -eq 0 -and (Read-Control $settings 232) -like '*HTTP(S)*') 'Invalid URL was accepted.'
}
Write-Control $settings 221 'https://example.invalid/first'
Click-Control $settings 224
Assert ((Target-Count $settings) -eq 1) 'Valid target was not added to the draft.'
Select-FirstTarget $settings
Write-Control $settings 221 'https://example.invalid/updated'
Write-Control $settings 222 'EX2'
Write-Control $settings 223 '◆'
Click-Control $settings 225
Assert ((Target-Count $settings) -eq 1 -and !(Test-Path -LiteralPath $settingsFile)) 'Draft update was not isolated.'
$null = Send-Number $settings 0x115 6
Click-Control $settings 201
Click-Control $settings 202
$null = Send-Number $settings 0x115 7
Assert ([DesktopWindows]::ControlInClient($settings,227)) 'Save is clipped on the smaller display.'
Click-Control $settings 227
Assert (![DesktopWindows]::IsWindow($settings) -and (Test-Path -LiteralPath $settingsFile)) 'Valid settings did not save.'
$saved = Get-Content -Raw -LiteralPath $settingsFile | ConvertFrom-Json
Assert ($saved.English -and $saved.Light -and $saved.CustomTargets.Count -eq 1 -and
    $saved.CustomTargets[0].Url -eq 'https://example.invalid/updated' -and
    $saved.CustomTargets[0].ShortName -eq 'EX2' -and $saved.CustomTargets[0].Symbol -eq '◆') 'Incorrect saved target or display preferences.'
Assert ((Read-Control $ui.panel 0) -like '*Synthetic data*100 rows*') 'English panel status did not apply.'
$summary.saved_sha256 = (Get-FileHash -LiteralPath $settingsFile).Hash

Exit-Tray $ui.tray
$null = Receive-Recorded $ui.run 15
$ui = Start-Panel $artifact 'settings-restart'
Assert ((Read-Control $ui.panel 0) -like '*Synthetic data*') 'English did not persist across exit.'
$summary.light_en = Save-Window $ui.id $ui.panel $ui.exe 'light-en'
Select-SampleNode '节点 005'
$details = Open-Auxiliary 125
$detailEn = Read-Control $details 240
Assert ($detailEn -match 'Score\s+87\b' -and
    $detailEn -like '*EX2*Untested*https://example.invalid/updated*undetermined*') "Saved target, provenance or fixed score is absent: $detailEn"
$summary.details_en = Save-Window $ui.id $details $ui.exe 'details-en'
Close-Panel $details
Write-Control $ui.panel 103 '节点 004'
Click-Control $ui.panel 107
Assert ([DesktopWindows]::IsWindowVisible((Get-List $ui.panel))) 'Could not open the localized table.'
$summary.table_en = Save-Window $ui.id $ui.panel $ui.exe 'table-en'
Click-Control $ui.panel 107
$settings = Open-Auxiliary 124
Assert ((Read-Weights $settings) -eq '70,20,10,50,40,10' -and (Target-Count $settings) -eq 1) 'Weights or target did not persist.'
Assert ((Send-Number ([DesktopWindows]::GetDlgItem($settings,201)) 0xF0) -eq 1 -and
    (Send-Number ([DesktopWindows]::GetDlgItem($settings,202)) 0xF0) -eq 1) 'Theme or language toggle did not persist.'
$summary.saved_en = Save-Window $ui.id $settings $ui.exe 'settings-en'
Limit-SettingsHeight $settings 730
$null = Send-Number $settings 0x115 6
Assert (![DesktopWindows]::ControlInClient($settings,227)) 'High-DPI test must exercise scrolling.'
$null = Send-Number $settings 0x115 7
Assert ([DesktopWindows]::GetDpiForWindow($settings) -eq 144 -and
    [DesktopWindows]::ControlInClient($settings,227) -and [DesktopWindows]::ControlInClient($settings,221)) 'High-DPI Settings controls are clipped.'
$summary.large_en_scrolled = Save-Window $ui.id $settings $ui.exe 'settings-144-scrolled'
$null = Send-Number $settings 0x115 6
Write-Control $settings 213 '51'
$null = Send-Number $settings 0x115 7
Click-Control $settings 227
Assert ([DesktopWindows]::IsWindow($settings) -and
    (Get-FileHash -LiteralPath $settingsFile).Hash -eq $summary.saved_sha256) 'Invalid target weights overwrote saved preferences.'
Write-Control $settings 213 '50'
Select-FirstTarget $settings
Assert ((Read-Control $settings 221) -eq 'https://example.invalid/updated' -and
    (Read-Control $settings 222) -eq 'EX2' -and (Read-Control $settings 223) -eq '◆') 'Edited target did not reload.'
Click-Control $settings 226
Assert ((Target-Count $settings) -eq 0) 'Remove did not update the draft.'
$null = Send-Number $settings 0x115 6
Click-Control $settings 201
$null = Send-Number $settings 0x115 7
Click-Control $settings 227
Assert (![DesktopWindows]::IsWindow($settings)) 'Removal could not be saved.'

Exit-Tray $ui.tray
$null = Receive-Recorded $ui.run 15
$ui = Start-Panel $artifact 'settings-remove-restart'
Assert ((Read-Control $ui.panel 0) -like '*合成数据*') 'Switching back to Chinese did not persist.'
$settings = Open-Auxiliary 124
Assert ((Target-Count $settings) -eq 0 -and (Read-Weights $settings) -eq '70,20,10,50,40,10') 'Removal did not persist.'
Close-Panel $settings
