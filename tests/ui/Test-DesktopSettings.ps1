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
Assert ($detailZh -like '*节点 004*OPENAI*401 鉴权响应*GPT*403 受限*无法判定*' -and
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
Click-Control $settings 201
Click-Control $settings 202
Click-Control $settings 227
Assert (![DesktopWindows]::IsWindow($settings) -and (Test-Path -LiteralPath $settingsFile)) 'Valid settings did not save.'
$saved = Get-Content -Raw -LiteralPath $settingsFile | ConvertFrom-Json
Assert ($saved.English -and $saved.Light -and $saved.CustomTargets.Count -eq 1 -and
    $saved.CustomTargets[0].Url -eq 'https://example.invalid/updated' -and
    $saved.CustomTargets[0].ShortName -eq 'EX2' -and $saved.CustomTargets[0].Symbol -eq '◆') 'Incorrect saved target or display preferences.'
Assert ((Read-Control $ui.panel 0) -like '*Synthetic data*100 rows*') 'English panel status did not apply.'
$summary.light_en = Save-Window $ui.id $ui.panel $ui.exe 'light-en'
$summary.saved_sha256 = (Get-FileHash -LiteralPath $settingsFile).Hash

Exit-Tray $ui.tray
$null = Receive-Recorded $ui.run 15
$ui = Start-Panel $artifact 'settings-restart'
Assert ((Read-Control $ui.panel 0) -like '*Synthetic data*') 'English did not persist across exit.'
Select-SampleNode '节点 005'
$details = Open-Auxiliary 125
$detailEn = Read-Control $details 240
Assert ($detailEn -match 'Score\s+87\b' -and
    $detailEn -like '*EX2*Untested*https://example.invalid/updated*undetermined*') "Saved target, provenance or fixed score is absent: $detailEn"
$summary.details_en = Save-Window $ui.id $details $ui.exe 'details-en'
Close-Panel $details
$settings = Open-Auxiliary 124
Assert ((Read-Weights $settings) -eq '70,20,10,50,40,10' -and (Target-Count $settings) -eq 1) 'Weights or target did not persist.'
Assert ((Send-Number ([DesktopWindows]::GetDlgItem($settings,201)) 0xF0) -eq 1 -and
    (Send-Number ([DesktopWindows]::GetDlgItem($settings,202)) 0xF0) -eq 1) 'Theme or language toggle did not persist.'
$summary.saved_en = Save-Window $ui.id $settings $ui.exe 'settings-en'
Write-Control $settings 213 '51'
Click-Control $settings 227
Assert ([DesktopWindows]::IsWindow($settings) -and
    (Get-FileHash -LiteralPath $settingsFile).Hash -eq $summary.saved_sha256) 'Invalid target weights overwrote saved preferences.'
Write-Control $settings 213 '50'
Select-FirstTarget $settings
Assert ((Read-Control $settings 221) -eq 'https://example.invalid/updated' -and
    (Read-Control $settings 222) -eq 'EX2' -and (Read-Control $settings 223) -eq '◆') 'Edited target did not reload.'
Click-Control $settings 226
Assert ((Target-Count $settings) -eq 0) 'Remove did not update the draft.'
Click-Control $settings 227
Assert (![DesktopWindows]::IsWindow($settings)) 'Removal could not be saved.'

Exit-Tray $ui.tray
$null = Receive-Recorded $ui.run 15
$ui = Start-Panel $artifact 'settings-remove-restart'
$settings = Open-Auxiliary 124
Assert ((Target-Count $settings) -eq 0 -and (Read-Weights $settings) -eq '70,20,10,50,40,10') 'Removal did not persist.'
Close-Panel $settings
