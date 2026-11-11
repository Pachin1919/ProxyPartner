using static ProxyPartnerLite.Desktop.Win32;

namespace ProxyPartnerLite.Desktop;

public sealed unsafe partial class DesktopApp
{
    private const int ProxyGroupId = 251, ProxyMemberId = 252, ApplyProxySelection = 253, TestExitIp = 254;
    private nint proxyWindow, proxyGroup, proxyMember, proxyApply, proxyStatus, proxyFont, exitButton;
    private DesktopProxyControl? proxyOptions;
    private bool proxyPending;

    private void ShowProxyControl()
    {
        if (proxyWindow != 0)
        {
            SetForegroundWindow(proxyWindow);
            return;
        }
        proxyWindow = CreateAux(L("ProxyPartner · 切换 Clash 节点", "ProxyPartner · Switch Clash node"), 525, 251);
        if (proxyWindow == 0) return;
        const uint dropDown = WsTabStop | 0x200000 | 3; // CBS_DROPDOWNLIST
        proxyGroup = AuxChild(proxyWindow, "COMBOBOX", "", dropDown, ProxyGroupId);
        proxyMember = AuxChild(proxyWindow, "COMBOBOX", "", dropDown, ProxyMemberId);
        proxyApply = AuxChild(proxyWindow, "BUTTON", L("应用到 Clash", "Apply to Clash"), WsTabStop, ApplyProxySelection);
        proxyStatus = AuxChild(proxyWindow, "STATIC", "", 0, 255);
        SizeAux(proxyWindow, null);
        LoadProxyOptions(source.GetSnapshot().ProxyControl);
        ShowWindow(proxyWindow, 5);
        SetFocus(proxyGroup);
    }

    private void LoadProxyOptions(DesktopProxyControl? options, string? selectedGroup = null)
    {
        proxyOptions = options;
        Send(proxyGroup, 0x14b, 0, 0);
        if (options is not null)
        {
            foreach (var group in options.Groups) AddCombo(proxyGroup, group.Name);
            int index = 0;
            while (index < options.Groups.Count && options.Groups[index].Name != selectedGroup) index++;
            if (options.Groups.Count != 0)
                Send(proxyGroup, 0x14e, (nuint)(index < options.Groups.Count ? index : 0), 0);
        }
        LoadProxyMembers();
    }

    private void LoadProxyMembers()
    {
        Send(proxyMember, 0x14b, 0, 0);
        int index = (int)Send(proxyGroup, 0x147, 0, 0);
        if (proxyOptions is not { } options || (uint)index >= options.Groups.Count)
        {
            SetText(proxyStatus, L("未连接可手动切换的 Clash 策略组", "No manual Clash selector is available"));
            return;
        }
        var group = options.Groups[index];
        for (int i = 0; i < group.Members.Count; i++)
        {
            AddCombo(proxyMember, group.Members[i]);
            if (group.Members[i] == group.Selected) Send(proxyMember, 0x14e, (nuint)i, 0);
        }
        SetText(proxyStatus, L($"已选节点 · {group.Selected ?? "—"}", $"Selected node · {group.Selected ?? "—"}"));
    }

    private static void AddCombo(nint combo, string value)
    {
        fixed (char* text = value) Send(combo, 0x143, 0, (nint)text);
    }

    private void ApplyProxyMember()
    {
        if (proxyPending || proxyOptions is not { } options || source is not IDesktopProxyControlSource control) return;
        int groupIndex = (int)Send(proxyGroup, 0x147, 0, 0);
        int memberIndex = (int)Send(proxyMember, 0x147, 0, 0);
        if ((uint)groupIndex >= options.Groups.Count) return;
        var group = options.Groups[groupIndex];
        if ((uint)memberIndex >= group.Members.Count) return;
        proxyPending = true;
        SetText(proxyStatus, L("正在应用", "Applying selection"));
        control.RequestProxySelection(options, group.Name, group.Members[memberIndex]);
    }

    private void UpdateProxyControl(DesktopProxyControl? control)
    {
        if (proxyWindow == 0) return;
        if (proxyPending && (control is null || control.LastSelection is { } result &&
            !ReferenceEquals(result, proxyOptions?.LastSelection)))
        {
            proxyPending = false;
            LoadProxyOptions(control, control?.LastSelection?.Group);
            SetText(proxyStatus, control?.LastSelection is { Confirmed: true } applied
                ? L($"Clash 已选 · {applied.Member}", $"Clash selected · {applied.Member}")
                : L("未能确认切换，请核对 Clash 当前选点", "Switch not confirmed. Check the current selection in Clash."));
        }
        else if (!proxyPending && (control?.CorePid != proxyOptions?.CorePid || control?.CoreStarted != proxyOptions?.CoreStarted))
            LoadProxyOptions(control);
    }
}
