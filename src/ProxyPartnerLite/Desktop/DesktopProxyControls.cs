using static ProxyPartnerLite.Desktop.Win32;

namespace ProxyPartnerLite.Desktop;

public sealed unsafe partial class DesktopApp
{
    private const int ProxyGroupId = 251, ProxyMemberId = 252, ApplyProxySelection = 253, TestExitIp = 254;
    private nint proxyWindow, proxyGroup, proxyMember, proxyApply, proxyStatus, proxyFont, exitButton;
    private DesktopProxyControl? proxyOptions;
    private DesktopProxyControl? pendingProxyControl;
    private string? proxyFeedback;
    private long proxyFeedbackUntil;

    private void SwitchToNode(int index)
    {
        if ((uint)index >= rows.Length || rows[index].Kind != NodeKind.Leaf ||
            source is not IDesktopProxyControlSource || pendingProxyControl is not null) return;
        if (source.GetSnapshot().ProxyControl is not { } options)
        {
            ShowProxyFeedback(L("未连接 Clash", "Clash is unavailable"));
            return;
        }
        string member = rows[index].Name;
        if (string.Equals(options.Mode, "direct", StringComparison.OrdinalIgnoreCase))
        {
            ShowProxyFeedback(L("Clash 当前为直连模式", "Clash is in Direct mode"));
            return;
        }
        if (string.Equals(options.Mode, "global", StringComparison.OrdinalIgnoreCase))
        {
            string? groupName = "GLOBAL";
            for (int depth = 0; depth < options.Groups.Count && groupName is not null; depth++)
            {
                var group = options.Groups.FirstOrDefault(item => item.Name == groupName);
                if (group is null) break;
                if (group.Members.Contains(member, StringComparer.Ordinal))
                {
                    StartProxySelection(options, group, member);
                    return;
                }
                groupName = group.Selected == groupName ? null : group.Selected;
            }
            ShowProxyFeedback(L("全局策略组中没有此节点", "Node is outside the Global selector"));
            return;
        }

        var groups = options.Groups.Where(group => group.Name != "GLOBAL" &&
            group.Members.Contains(member, StringComparer.Ordinal)).ToArray();
        if (groups.Length == 0)
        {
            ShowProxyFeedback(L("此节点不在可切换的策略组里", "No manual selector contains this node"));
            return;
        }
        if (groups.Length == 1)
        {
            StartProxySelection(options, groups[0], member);
            return;
        }
        nint menu = CreatePopupMenu();
        if (menu == 0) return;
        for (int i = 0; i < groups.Length; i++)
            AppendMenu(menu, groups[i].Selected == member ? 8u : 0u, (nuint)(i + 1), groups[i].Name.Replace("&", "&&"));
        Rect anchor;
        GetWindowRect(proxyButton, &anchor);
        uint command = TrackPopupMenu(menu, 0x102, anchor.Left, anchor.Bottom, 0, panel, 0);
        DestroyMenu(menu);
        if (command > 0 && command <= groups.Length)
            StartProxySelection(options, groups[command - 1], member);
    }

    private void ShowProxyFeedback(string message)
    {
        proxyFeedback = message;
        proxyFeedbackUntil = Environment.TickCount64 + 5000;
        UpdateStatus();
        if (proxyStatus != 0) SetText(proxyStatus, message);
    }

    private void StartProxySelection(DesktopProxyControl options, DesktopProxyGroup group, string member)
    {
        if (pendingProxyControl is not null || source is not IDesktopProxyControlSource control) return;
        pendingProxyControl = options;
        ShowProxyFeedback(L($"正在切换 · {member}", $"Switching · {member}"));
        control.RequestProxySelection(options, group.Name, member);
    }

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
        if (proxyOptions is not { } options) return;
        int groupIndex = (int)Send(proxyGroup, 0x147, 0, 0);
        int memberIndex = (int)Send(proxyMember, 0x147, 0, 0);
        if ((uint)groupIndex >= options.Groups.Count) return;
        var group = options.Groups[groupIndex];
        if ((uint)memberIndex >= group.Members.Count) return;
        StartProxySelection(options, group, group.Members[memberIndex]);
    }

    private void UpdateProxyControl(DesktopProxyControl? control)
    {
        if (pendingProxyControl is { } pending && (control is null || control.CorePid != pending.CorePid ||
            control.CoreStarted != pending.CoreStarted || control.LastSelection is { } result &&
            !ReferenceEquals(result, pending.LastSelection)))
        {
            pendingProxyControl = null;
            bool sameHost = control?.CorePid == pending.CorePid && control.CoreStarted == pending.CoreStarted;
            if (proxyWindow != 0) LoadProxyOptions(control, sameHost ? control?.LastSelection?.Group : null);
            ShowProxyFeedback(sameHost && control?.LastSelection is { Confirmed: true } applied
                ? L($"Clash 已选 · {applied.Member}", $"Clash selected · {applied.Member}")
                : L("未能确认切换，请核对 Clash 当前选点", "Switch not confirmed. Check the current selection in Clash."));
        }
        else if (proxyWindow != 0 && pendingProxyControl is null &&
            (control?.CorePid != proxyOptions?.CorePid || control?.CoreStarted != proxyOptions?.CoreStarted))
            LoadProxyOptions(control);
    }
}
