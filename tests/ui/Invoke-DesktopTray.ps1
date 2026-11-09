# Real shell activation used by the interactive DPI/tray check.
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$automationRoot = [Windows.Automation.AutomationElement]::RootElement
$nameProperty = [Windows.Automation.AutomationElement]::NameProperty
$processProperty = [Windows.Automation.AutomationElement]::ProcessIdProperty
$trayName = 'ProxyPartner · 节点质量面板'
$descendants = [Windows.Automation.TreeScope]::Descendants
$children = [Windows.Automation.TreeScope]::Children
$explorerIds = @(Get-Process -Name explorer | Where-Object SessionId -eq $summary.session | Select-Object -ExpandProperty Id)

function Find-AutomationItem([int[]]$ProcessIds, [string[]]$Names) {
    foreach ($id in $ProcessIds) {
        $process = [Windows.Automation.PropertyCondition]::new($processProperty,$id)
        foreach ($window in $automationRoot.FindAll($children,$process)) {
            foreach ($name in $Names) {
                $match = [Windows.Automation.PropertyCondition]::new($nameProperty,$name)
                $element = $window.FindFirst($descendants,$match)
                if ($null -ne $element -and !$element.Current.IsOffscreen) { return $element }
            }
        }
    }
}
function Invoke-AutomationItem($Element) {
    $pattern = $null
    if ($Element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern,[ref]$pattern)) {
        $pattern.Invoke()
        return 'UI Automation Invoke'
    }
    $point = $Element.GetClickablePoint()
    [DesktopWindows]::Click([int]$point.X,[int]$point.Y)
    'Pointer click on identified element'
}
function Find-ShellIcon {
    $icon = Find-AutomationItem $explorerIds @($trayName)
    if ($icon) { return $icon }
    $overflow = Find-AutomationItem $explorerIds @('显示隐藏的图标','Show hidden icons')
    if (!$overflow) { throw 'Explorer did not expose its overflow control.' }
    $null = Invoke-AutomationItem $overflow
    Wait-Value { Find-AutomationItem $explorerIds @($trayName) } 'Explorer overflow did not expose the ProxyPartner icon.'
}

$icon = Find-ShellIcon
$summary.shell_open_action = Invoke-AutomationItem $icon
$reopened = Wait-Value { Find-Panel $ui.id } 'Actual shell activation did not reopen the window.'
Assert ((Send-Number (Get-List $reopened) 0x1004) -eq 100) 'Reopened panel lost its rows.'
$summary.tray_reopen = Save-Window $ui.id $reopened $ui.exe 'tray-reopen'
Exit-Tray $ui.tray
$summary.shell_exit_action = 'Exit menu command dispatched to the product tray HWND'
