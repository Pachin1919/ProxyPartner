using System.Globalization;
using System.Text;
using static ProxyPartnerLite.Desktop.Win32;

namespace ProxyPartnerLite.Desktop;

public sealed unsafe partial class DesktopApp
{
    private const string AuxClass = "ProxyPartner.Auxiliary";
    private const int OpenSettings = 124, OpenDetails = 125, EnglishId = 201, LightId = 202,
        WeightFirst = 210, TargetListId = 220, UrlId = 221, ShortId = 222, SymbolId = 223,
        AddTarget = 224, EditTarget = 225, RemoveTarget = 226, SaveSettings = 227, CancelSettings = 228;
    private nint settingsButton, detailButton, settingsWindow, detailWindow, detailText;
    private nint settingsFont, detailFont, englishBox, lightBox, targetsList, urlInput, shortInput, symbolInput, example, settingsStatus;
    private readonly nint[] weightInputs = new nint[6];
    private readonly List<(nint Handle, int X, int Y, int W, int H)> settingsControls = [];
    private readonly List<CustomTarget> draftTargets = [];

    private void ShowSettings()
    {
        if (settingsWindow != 0)
        {
            SetForegroundWindow(settingsWindow);
            return;
        }
        settingsWindow = CreateAux(L("ProxyPartner · 设置", "ProxyPartner · Settings"), 740, 634);
        if (settingsWindow == 0) return;
        draftTargets.AddRange(preferences.CustomTargets);
        const uint check = WsTabStop | 3, edit = WsTabStop | 0x800000 | 0x80;
        const uint button = WsTabStop;
        nint Field(string cls, string caption, uint style, int id, int x, int y, int width, int height)
        {
            nint handle = AuxChild(settingsWindow, cls, caption, style, id);
            settingsControls.Add((handle, x, y, width, height));
            return handle;
        }
        Field("STATIC", L("显示与测试偏好", "Display and testing preferences"), 0, 0, 20, 18, 670, 21);
        englishBox = Field("BUTTON", L("English 界面", "English interface"), check, EnglishId, 20, 45, 165, 24);
        lightBox = Field("BUTTON", L("浅色主题", "Light theme"), check, LightId, 205, 45, 165, 24);
        Send(englishBox, 0xf1, preferences.English ? 1u : 0u, 0);
        Send(lightBox, 0xf1, preferences.Light ? 1u : 0u, 0);
        Field("STATIC", L("评分分项（%）", "Score components (%)"), 0, 0, 20, 88, 680, 20);
        Field("STATIC", L("AI 工作目标（%）", "AI work targets (%)"), 0, 0, 20, 151, 680, 20);
        string[] labels = [L("成功率", "Success rate"), L("延迟", "Latency"), L("稳定性", "Stability"), "OPENAI", "GPT", "GitHub"];
        int[] values = [preferences.Components.First, preferences.Components.Second, preferences.Components.Third,
            preferences.Targets.First, preferences.Targets.Second, preferences.Targets.Third];
        for (int index = 0; index < weightInputs.Length; index++)
        {
            int x = 20 + index % 3 * 225, y = 111 + index / 3 * 63;
            Field("STATIC", labels[index], 0, 0, x, y + 2, 116, 22);
            weightInputs[index] = Field("EDIT", values[index].ToString(CultureInfo.InvariantCulture), edit,
                WeightFirst + index, x + 119, y, 58, 24);
            Send(weightInputs[index], 0xc5, 3, 0); // EM_SETLIMITTEXT
        }
        example = Field("STATIC", "", 0, 233, 20, 208, 685, 52);
        Field("STATIC", L("自定义目标 · 仅登记，待测", "Custom targets · registration only, untested"), 0, 0, 20, 275, 690, 20);
        targetsList = Field("LISTBOX", "", WsTabStop | 0x800000 | 0x300000 | 1, TargetListId, 20, 300, 684, 98);
        Send(targetsList, 0x194, (nuint)Scale(1400), 0); // horizontal extent available when the list is wide
        Field("STATIC", L("HTTP(S) 地址", "HTTP(S) URL"), 0, 0, 20, 405, 460, 20);
        Field("STATIC", L("简写", "Short name"), 0, 0, 505, 405, 100, 20);
        Field("STATIC", L("符号", "Symbol"), 0, 0, 620, 405, 82, 20);
        urlInput = Field("EDIT", "", edit, UrlId, 20, 426, 471, 25);
        shortInput = Field("EDIT", "", edit, ShortId, 505, 426, 101, 25);
        symbolInput = Field("EDIT", "", edit, SymbolId, 620, 426, 84, 25);
        Send(urlInput, 0xc5, 2048, 0);
        Send(shortInput, 0xc5, 12, 0);
        Send(symbolInput, 0xc5, 8, 0);
        Field("BUTTON", L("添加", "Add"), button, AddTarget, 20, 463, 84, 28);
        Field("BUTTON", L("修改所选", "Update"), button, EditTarget, 114, 463, 96, 28);
        Field("BUTTON", L("移除所选", "Remove"), button, RemoveTarget, 220, 463, 96, 28);
        settingsStatus = Field("STATIC", L("添加或修改后按保存。两组权重须各合计 100%。",
            "Add or update the target, then Save. Each weight group must total 100%."),
            0, 232, 20, 501, 684, 35);
        Field("BUTTON", L("保存", "Save"), button, SaveSettings, 516, 548, 88, 30);
        Field("BUTTON", L("取消", "Cancel"), button, CancelSettings, 616, 548, 88, 30);
        RefreshTargetList(-1);
        UpdateExample();
        SizeAux(settingsWindow, null);
        ShowWindow(settingsWindow, 5);
        SetFocus(englishBox);
    }

    private void ShowDetails()
    {
        if (detailWindow == 0)
        {
            detailWindow = CreateAux(L("ProxyPartner · 节点详情", "ProxyPartner · Node details"), 745, 620);
            if (detailWindow == 0) return;
            detailText = AuxChild(detailWindow, "EDIT", "", WsTabStop | 0x800000 | 0x200000 | 4 | 0x40 | 0x800, 240);
            SizeAux(detailWindow, null);
        }
        RefreshDetails();
        ShowWindow(detailWindow, 5);
        SetForegroundWindow(detailWindow);
        SetFocus(detailText);
    }

    private nint CreateAux(string caption, int width, int height)
    {
        Rect origin;
        GetWindowRect(panel, &origin);
        var monitor = new MonitorInfo { Size = (uint)sizeof(MonitorInfo) };
        int x = origin.Left + Scale(30), y = origin.Top + Scale(30), w = Scale(width), h = Scale(height);
        if (GetMonitorInfo(MonitorFromWindow(panel, 2), &monitor) != 0)
        {
            w = Math.Min(w, monitor.Work.Right - monitor.Work.Left);
            h = Math.Min(h, monitor.Work.Bottom - monitor.Work.Top);
            x = Math.Clamp(x, monitor.Work.Left, monitor.Work.Right - w);
            y = Math.Clamp(y, monitor.Work.Top, monitor.Work.Bottom - h);
        }
        nint window = CreateWindow(0, AuxClass, caption, 0x00c80000 | WsClipChildren, x, y, w, h, panel, 0, module, 0);
        int dark = preferences.Light ? 0 : 1;
        if (window != 0) DwmSetWindowAttribute(window, 20, &dark, sizeof(int));
        return window;
    }

    private nint AuxChild(nint parent, string cls, string value, uint style, int id) =>
        CreateWindow(0, cls, value, WsChild | WsVisible | style, 0, 0, 0, 0, parent, id, module, 0);

    private void SizeAux(nint window, Rect* suggested)
    {
        if (suggested != null)
            MoveWindow(window, suggested->Left, suggested->Top, suggested->Right - suggested->Left,
                suggested->Bottom - suggested->Top, 1);
        int scale = (int)GetDpiForWindow(window);
        int Px(int value) => (value * scale + 48) / 96;
        nint face = CreateFont(-Px(15), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, "Segoe UI");
        if (window == settingsWindow)
        {
            foreach (var item in settingsControls)
            {
                Send(item.Handle, WmSetFont, (nuint)face, 1);
                MoveWindow(item.Handle, Px(item.X), Px(item.Y), Px(item.W), Px(item.H), 1);
            }
            if (settingsFont != 0) DeleteObject(settingsFont);
            settingsFont = face;
        }
        else
        {
            Rect rect;
            GetClientRect(window, &rect);
            Send(detailText, WmSetFont, (nuint)face, 1);
            MoveWindow(detailText, Px(14), Px(14), Math.Max(0, rect.Right - Px(28)), Math.Max(0, rect.Bottom - Px(28)), 1);
            if (detailFont != 0) DeleteObject(detailFont);
            detailFont = face;
        }
    }

    private nint HandleAux(nint window, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case WmClose: DestroyWindow(window); return 0;
            case WmDestroy:
                if (window == settingsWindow)
                {
                    settingsWindow = 0;
                    if (settingsFont != 0) DeleteObject(settingsFont);
                    settingsFont = example = 0;
                    settingsControls.Clear();
                    draftTargets.Clear();
                }
                else
                {
                    detailWindow = detailText = 0;
                    if (detailFont != 0) DeleteObject(detailFont);
                    detailFont = 0;
                }
                return 0;
            case WmDpiChanged: SizeAux(window, (Rect*)lParam); return 0;
            case 0x14: // WM_ERASEBKGND
                Rect rect;
                GetClientRect(window, &rect);
                Fill((nint)wParam, rect, Tools);
                return 1;
            case 0x133: // WM_CTLCOLOREDIT
            case 0x134: // WM_CTLCOLORLISTBOX
            case 0x138: // WM_CTLCOLORSTATIC (includes readonly detail edit)
                bool field = message != 0x138 || lParam == detailText;
                SetTextColor((nint)wParam, Text);
                SetBkMode((nint)wParam, 2);
                SetBkColor((nint)wParam, field ? Control : Tools);
                return field ? editBrush : toolsBrush;
            case WmCommand when window == settingsWindow:
                int id = (int)wParam & 0xffff, notification = (int)wParam >> 16;
                if (id >= WeightFirst && id < WeightFirst + 6 && notification == 0x300 && example != 0) UpdateExample();
                if (id == TargetListId && notification == 1) SelectTarget();
                if (id is AddTarget or EditTarget or RemoveTarget) ChangeTarget(id);
                if (id == SaveSettings) CommitSettings();
                if (id == CancelSettings || id == 2) DestroyWindow(window);
                return 0;
            case WmCommand when (int)wParam == 2:
                DestroyWindow(window);
                return 0;
        }
        return Default(window, message, wParam, lParam);
    }

    private static string ReadField(nint handle, int capacity)
    {
        char* text = stackalloc char[capacity + 1];
        int length = GetText(handle, text, capacity + 1);
        return new string(text, 0, length).Trim();
    }

    private WeightSet ReadWeights(int start)
    {
        int Read(int i) => int.TryParse(ReadField(weightInputs[start + i], 3), NumberStyles.None,
            CultureInfo.InvariantCulture, out int value) ? value : -1;
        return new(Read(0), Read(1), Read(2));
    }

    private void UpdateExample()
    {
        var parts = ReadWeights(0);
        var sites = ReadWeights(3);
        if (!parts.IsValid || !sites.IsValid)
        {
            SetText(example, L("每项输入 0–100 的整数，两组各合计 100% 才能保存。",
                "Use whole numbers from 0 to 100. Each group must total 100% before saving."));
            return;
        }
        double openAi = parts.Apply(90, 75, 80), gpt = parts.Apply(70, 90, 55), gitHub = parts.Apply(100, 95, 90);
        double total = sites.Apply(openAi, gpt, gitHub);
        SetText(example, L(
            $"合成算例 · OPENAI {openAi:F1} / GPT {gpt:F1} / GitHub {gitHub:F1} → {total:F1}\r\n先加权成功率、延迟和稳定性，再加权目标。节点样例分数保持原值。",
            $"Synthetic example · OPENAI {openAi:F1} / GPT {gpt:F1} / GitHub {gitHub:F1} → {total:F1}\r\nWeight success, latency and stability per target, then weight the targets. Sample node scores stay unchanged."));
    }

    private int TargetIndex() => (int)Send(targetsList, 0x188, 0, 0);

    private void RefreshTargetList(int index)
    {
        Send(targetsList, 0x184, 0, 0);
        foreach (var target in draftTargets)
        {
            string value = $"{target.Symbol}  {target.ShortName}  ·  {L("待测", "Untested")}  ·  {target.Url}";
            fixed (char* text = value) Send(targetsList, 0x180, 0, (nint)text);
        }
        Send(targetsList, 0x186, unchecked((nuint)index), 0);
        SelectTarget();
    }

    private void SelectTarget()
    {
        int index = TargetIndex();
        CustomTarget? target = (uint)index < draftTargets.Count ? draftTargets[index] : null;
        SetText(urlInput, target?.Url ?? "");
        SetText(shortInput, target?.ShortName ?? "");
        SetText(symbolInput, target?.Symbol ?? "");
    }

    private void ChangeTarget(int action)
    {
        int index = TargetIndex();
        if (action != AddTarget && (uint)index >= draftTargets.Count)
        {
            SetText(settingsStatus, L("请先选择一个目标。", "Select a target first."));
            return;
        }
        if (action == RemoveTarget) draftTargets.RemoveAt(index);
        else
        {
            var target = new CustomTarget(ReadField(urlInput, 2048), ReadField(shortInput, 12), ReadField(symbolInput, 8));
            if (!CustomTarget.IsValid(target))
            {
                SetText(settingsStatus, L("需要无凭证的绝对 HTTP(S) URL、简写和符号。",
                    "Enter an absolute HTTP(S) URL without credentials, a short name and a symbol."));
                return;
            }
            if (action == AddTarget)
            {
                if (draftTargets.Count == DesktopPreferences.MaxTargets)
                {
                    SetText(settingsStatus, L("最多登记 16 个目标。", "Up to 16 custom targets are allowed."));
                    return;
                }
                index = draftTargets.Count;
                draftTargets.Add(target);
            }
            else draftTargets[index] = target;
        }
        RefreshTargetList(Math.Min(index, draftTargets.Count - 1));
        SetText(settingsStatus, L("草稿已更新，按保存后生效。", "Draft updated. Save to apply."));
    }

    private void CommitSettings()
    {
        var parts = ReadWeights(0);
        var sites = ReadWeights(3);
        if (!parts.IsValid || !sites.IsValid)
        {
            SetText(settingsStatus, L("两组权重须各合计 100%。现有配置未改动。",
                "Each weight group must total 100%. Saved preferences were kept."));
            return;
        }
        var updated = new DesktopPreferences(Send(englishBox, 0xf0, 0, 0) == 1, Send(lightBox, 0xf0, 0, 0) == 1,
            parts, sites, draftTargets.ToArray());
        try { updated.Save(settingsPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetText(settingsStatus, L("无法保存本机配置。", "Could not save the local preferences."));
            return;
        }
        preferences = updated;
        DestroyWindow(settingsWindow);
        ApplyPreferences();
    }

    private void ApplyPreferences()
    {
        nint nextEdit = CreateSolidBrush(Control), nextTools = CreateSolidBrush(Tools);
        if (nextEdit != 0) { DeleteObject(editBrush); editBrush = nextEdit; }
        if (nextTools != 0) { DeleteObject(toolsBrush); toolsBrush = nextTools; }
        int dark = preferences.Light ? 0 : 1;
        DwmSetWindowAttribute(panel, 20, &dark, sizeof(int));
        SetText(panel, PanelTitle);
        SetText(tray, L("ProxyPartner · 托盘", "ProxyPartner · Tray"));
        UpdateTray(1);
        SetText(title, L("ProxyPartner  /  节点质量", "ProxyPartner  /  Node quality"));
        SetText(searchLabel, L("搜索", "Search"));
        SetText(toggle, ViewLabel);
        SetText(sortButton, L("排序", "Sort"));
        SetText(groups, L("策略组", "Groups"));
        SetText(settingsButton, L("设置", "Settings"));
        SetText(detailButton, L("详情", "Details"));
        SetText(cards, L("节点卡片", "Node cards"));
        SetText(hint, tableMode ? TableHint : HoverText());
        Send(list, LvmFirst + 1, 0, (nint)Background);
        Send(list, LvmFirst + 36, 0, (nint)Text);
        Send(list, LvmFirst + 38, 0, (nint)Background);
        for (int index = 0; index < Columns.Length; index++)
        {
            fixed (char* label = Columns[index])
            {
                var column = new Column { Mask = 4, Text = label };
                Send(list, LvmFirst + 96, (nuint)index, (nint)(&column));
            }
        }
        if (detailWindow != 0)
        {
            DwmSetWindowAttribute(detailWindow, 20, &dark, sizeof(int));
            SetText(detailWindow, L("ProxyPartner · 节点详情", "ProxyPartner · Node details"));
            RefreshDetails();
            InvalidateRect(detailWindow, 0, 1);
        }
        UpdateStatus();
        Layout();
        InvalidateRect(cards, 0, 0);
        InvalidateRect(list, 0, 1);
    }

    private void RefreshDetails()
    {
        CaptureSelection();
        int index = IndexOfSelection();
        if (index < 0)
        {
            SetText(detailText, L("请先在卡片或表格中选择节点。", "Select a node in Cards or Table first."));
            return;
        }
        var row = rows[index];
        bool synthetic = source.GetSnapshot().Collection == CollectionState.Synthetic;
        string SourceLabel(string value) => value switch
        {
            "合成" => L("合成", "Synthetic"),
            "原生历史 · 合成" => L("原生历史 · 合成", "Native history · synthetic"),
            "HTTPS · 合成" => L("HTTPS · 合成", "HTTPS · synthetic"),
            _ => value
        };
        var report = new StringBuilder();
        void Line(string value = "") => report.Append(value).Append("\r\n");
        void Result(string name, DesktopResult result)
        {
            string value = result.Format(preferences.English);
            if (result.ElapsedMs is int ms && result.State != ResultState.Measured) value += $" · {ms} ms";
            Line($"{name} · {value} · {Timestamp(result.ObservedUtc)} UTC · {SourceLabel(result.Source)}");
        }
        Line(row.Name);
        Line($"{L("评分", "Score")} {ScoreText(row)}  |  {L("来源", "Source")}: {SourceLabel(row.Quality?.Source ?? "—")}");
        if (synthetic) Line(L("以下为合成样例。编辑权重只更新设置内的算例。", "Synthetic sample. Changing weights updates the Settings example only."));
        Line();
        Result(L("原生延迟", "Native latency"), row.Native);
        Result("OPENAI (OpenAI API)", row.OpenAi);
        Result("GPT (ChatGPT)", row.ChatGpt);
        Result("GitHub", row.GitHub);
        foreach (var target in preferences.CustomTargets) Line($"{target.Symbol} {target.ShortName} · {L("待测", "Untested")} · {target.Url}");
        Line();
        Line(L("401：已到达鉴权。403：服务可能受限。两者均与超时不同。",
            "401 reached authentication. 403 may indicate a restricted service. Both differ from a timeout."));
        Line(L("高延迟：看可比成功样本的均值。间歇失败：看跨轮次失败率及异常次数。",
            "High latency: compare mean successful latency. Intermittent failure: inspect failure rate and incident count across rounds."));
        Line(L("无法连接：需要可比的连接失败证据。样本不足：覆盖期或有效尝试不足。",
            "Unable to connect requires comparable connection-failure evidence. Low samples means insufficient coverage or eligible attempts."));
        Line(L("有效尝试、成功均值、失败率、异常次数、覆盖期：无法判定。",
            "Eligible attempts, mean success latency, failure rate, incidents and coverage: undetermined."));
        Line(L("按节点、目标、方法与环境分组后才能判断。单条失败不会推断掉线频次。",
            "Group by node, target, method and environment before drawing conclusions. A single failure does not establish disconnect frequency."));
        SetText(detailText, report.ToString());
    }
}
