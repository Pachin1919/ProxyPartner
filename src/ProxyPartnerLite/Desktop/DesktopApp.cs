using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static ProxyPartnerLite.Desktop.Win32;

namespace ProxyPartnerLite.Desktop;

public sealed unsafe partial class DesktopApp
{
    private const string TrayClass = "ProxyPartner.Tray", PanelClass = "ProxyPartner.Panel";
    private const uint TrayMessage = 0x8001, SnapshotMessage = 0x8002;
    private const int Open = 101, Exit = 102, Search = 103, Groups = 104, Toggle = 107, SortMenu = 108, SortFirst = 110;
    private static readonly string[] Columns = ["节点", "当前", "原生延迟", "OpenAI API", "ChatGPT", "GitHub", "最近样本 (UTC)", "评分", "测试选择", "质量状态"];
    private static readonly int[] ColumnWidths = [192, 56, 102, 126, 112, 102, 142, 74, 95, 94];
    private static DesktopApp? active;

    private readonly IDesktopSnapshotSource source;
    private readonly nint module = GetModuleHandle(0);
    private nint tray, panel, status, search, groups, list, font, icon;
    private uint taskbarCreated;
    private int dpi = 96, sortColumn;
    private bool descending;
    private DesktopRow[] rows = [];

    private DesktopApp(IDesktopSnapshotSource source) => this.source = source;

    // Run on the caller's Windows UI thread. The caller owns the source lifecycle.
    public static int Run(IDesktopSnapshotSource source, bool startInTray = false)
    {
        if (active is not null) throw new InvalidOperationException("A desktop loop is already running.");
        var app = new DesktopApp(source);
        active = app;
        try
        {
            return app.RunLoop(startInTray);
        }
        finally
        {
            if (app.panel != 0) DestroyWindow(app.panel);
            if (app.tray != 0) DestroyWindow(app.tray);
            active = null;
        }
    }

    private int RunLoop(bool startInTray)
    {
        uint* common = stackalloc uint[] { 8, 1 };
        if (InitCommonControlsEx(common) == 0) return 1;
        icon = LoadIcon(0, 32512);
        if (!Register(TrayClass) || !Register(PanelClass) || !Register(CardsClass)) return 1;
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        tray = CreateWindow(0, TrayClass, "ProxyPartner · 托盘", 0, 0, 0, 0, 0, 0, 0, module, 0);
        if (tray == 0 || !UpdateTray(0)) return 1;
        UpdateTray(4);
        if (!startInTray && !ShowPanel()) return 1;

        Message msg;
        int result;
        while ((result = GetMessage(&msg, 0, 0, 0)) > 0)
        {
            if (panel != 0 && msg.Id == WmKeyDown && GetKeyState(0x11) < 0 &&
                msg.WParam is >= 0x31 and <= 0x39)
            {
                Send(panel, WmCommand, SortFirst + msg.WParam - 0x31, 0);
                continue;
            }
            if (panel != 0 && IsDialogMessage(panel, &msg) != 0) continue;
            TranslateMessage(&msg);
            DispatchMessage(&msg);
        }
        return result < 0 ? 1 : (int)msg.WParam;
    }

    private bool Register(string name)
    {
        fixed (char* className = name)
        {
            var cls = new WindowClass
            {
                Size = (uint)sizeof(WindowClass), Proc = &WindowProc, Instance = module,
                Icon = icon, Cursor = LoadCursor(0, 32512), Background = 16, Name = className
            };
            return RegisterClass(&cls) != 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint window, uint msg, nuint wParam, nint lParam) =>
        active?.Handle(window, msg, wParam, lParam) ?? Default(window, msg, wParam, lParam);

    private nint Handle(nint window, uint msg, nuint wParam, nint lParam)
    {
        if (window == cards) return HandleCards(msg, wParam, lParam);
        if (window == tray)
        {
            if (msg == taskbarCreated && taskbarCreated != 0)
            {
                if (UpdateTray(0)) UpdateTray(4);
                return 0;
            }
            if (msg == TrayMessage)
            {
                switch ((int)lParam & 0xffff)
                {
                    case 0x400:
                    case 0x401:
                        ShowPanel();
                        break;
                    case 0x7b:
                        ShowTrayMenu();
                        break;
                }
                return 0;
            }
            if (msg == SnapshotMessage)
            {
                if (panel != 0) Refresh();
                return 0;
            }
            if (msg == WmCommand)
            {
                switch ((int)wParam & 0xffff)
                {
                    case Open: ShowPanel(); return 0;
                    case Exit: DestroyWindow(tray); return 0;
                }
            }
            if (msg == WmDestroy)
            {
                if (panel != 0) DestroyWindow(panel);
                UpdateTray(2);
                tray = 0;
                PostQuitMessage(0);
                return 0;
            }
        }
        if (window == panel)
        {
            switch (msg)
            {
                case WmSize: Layout(); return 0;
                case WmClose: DestroyWindow(panel); return 0;
                case WmDestroy:
                    source.SnapshotChanged -= OnSnapshotChanged;
                    rows = [];
                    panel = status = search = groups = list = cards = toggle = title = searchLabel = sortButton = 0;
                    FreeFonts();
                    if (editBrush != 0) DeleteObject(editBrush);
                    if (toolsBrush != 0) DeleteObject(toolsBrush);
                    editBrush = toolsBrush = 0;
                    return 0;
                case 0x14: // WM_ERASEBKGND
                    Rect background;
                    GetClientRect(panel, &background);
                    Fill((nint)wParam, background, Background);
                    Fill((nint)wParam, Box(0, 0, background.Right, ToolbarHeight(background.Right) - Scale(8)), Tools);
                    return 1;
                case 0x133: // WM_CTLCOLOREDIT
                case 0x138: // WM_CTLCOLORSTATIC
                    SetTextColor((nint)wParam, lParam == status ? Muted : Text);
                    SetBkMode((nint)wParam, 2);
                    SetBkColor((nint)wParam, msg == 0x133 ? Control : Tools);
                    return msg == 0x133 ? editBrush : toolsBrush;
                case 0x2b: // WM_DRAWITEM
                    DrawToolbarButton((DrawItem*)lParam);
                    return 1;
                case 0x20a: // WM_MOUSEWHEEL when focus is in the toolbar
                    if (!tableMode) return HandleCards(msg, wParam, lParam);
                    break;
                case WmDpiChanged:
                    ApplyDpi((int)wParam & 0xffff, (Rect*)lParam);
                    return 0;
                case WmCommand:
                    int id = (int)wParam & 0xffff;
                    if (id == Groups)
                    {
                        showGroups = !showGroups;
                        InvalidateRect(groups, 0, 0);
                        Refresh();
                    }
                    if (id == Search && ((int)wParam >> 16) == 0x300)
                    {
                        char* input = stackalloc char[256];
                        int length = GetText(search, input, 256);
                        filterText = new string(input, 0, length);
                        Refresh();
                    }
                    if (id == Toggle) SwitchView();
                    if (id == SortMenu) ShowSortMenu();
                    if (id >= SortFirst && id < SortFirst + Columns.Length) Sort(id - SortFirst);
                    return 0;
                case WmNotify:
                    var header = (NotifyHeader*)lParam;
                    if (header->Window != list) break;
                    if (header->Code == LvnGetDisplayInfo) FillCell(&((DisplayInfo*)lParam)->Item);
                    if (header->Code == LvnColumnClick) Sort(((ListNotify*)lParam)->SubItem);
                    if (header->Code == -12) return DrawTable((CustomDraw*)lParam);
                    if (header->Code == -2 && ((ListNotify*)lParam)->SubItem == 8)
                        ToggleTest(((ListNotify*)lParam)->Item);
                    if (header->Code == -155 && *(ushort*)((byte*)lParam + sizeof(NotifyHeader)) == 0x20)
                        ToggleTest(TableSelection());
                    return 0;
            }
        }
        return Default(window, msg, wParam, lParam);
    }

    private bool UpdateTray(uint message)
    {
        var data = new NotifyIcon
        {
            Size = (uint)sizeof(NotifyIcon), Window = tray, Id = 1, Flags = 1 | 2 | 4 | 0x80,
            Callback = TrayMessage, Icon = icon, Version = 4
        };
        "ProxyPartner · 节点质量面板".AsSpan().CopyTo(new Span<char>(data.Tip, 128));
        return ShellNotifyIcon(message, &data) != 0;
    }

    private void ShowTrayMenu()
    {
        nint menu = CreatePopupMenu();
        if (menu == 0) return;
        AppendMenu(menu, 0, Open, "打开");
        AppendMenu(menu, 0, Exit, "退出");
        Point point;
        GetCursorPos(&point);
        SetForegroundWindow(tray);
        uint command = TrackPopupMenu(menu, 0x100 | 2, point.X, point.Y, 0, tray, 0);
        DestroyMenu(menu);
        Post(tray, 0, 0, 0);
        if (command != 0) Post(tray, WmCommand, command, 0);
    }

    private bool ShowPanel()
    {
        if (panel != 0)
        {
            ShowWindow(panel, 9);
            SetForegroundWindow(panel);
            return true;
        }
        panel = CreateWindow(0, PanelClass, "ProxyPartner · 节点质量面板", WsOverlappedWindow | WsClipChildren,
            UseDefault, UseDefault, 1010, 560, 0, 0, module, 0);
        if (panel == 0) return false;
        int dark = 1;
        DwmSetWindowAttribute(panel, 20, &dark, sizeof(int));
        editBrush = CreateSolidBrush(Control);
        toolsBrush = CreateSolidBrush(Tools);
        title = Child("STATIC", "ProxyPartner  /  节点质量", 0, 109);
        status = Child("STATIC", "", 0, 0);
        searchLabel = Child("STATIC", "搜索", 0x200, 120);
        search = Child("EDIT", filterText, WsTabStop | 0x800000 | 0x80, Search);
        toggle = Child("BUTTON", tableMode ? "切换卡片" : "切换表格", WsTabStop | 11, Toggle);
        sortButton = Child("BUTTON", "排序", WsTabStop | 11, SortMenu);
        groups = Child("BUTTON", "策略组", WsTabStop | 11, Groups);
        list = Child("SysListView32", "", WsTabStop | 0x1000 | 1 | 4 | 8, 105);
        cards = Child(CardsClass, "节点卡片", WsTabStop | 0x200000, 106);
        if (title == 0 || status == 0 || search == 0 || searchLabel == 0 || groups == 0 || list == 0 ||
            cards == 0 || toggle == 0 || sortButton == 0 || editBrush == 0 || toolsBrush == 0)
        {
            DestroyWindow(panel);
            return false;
        }
        Send(list, 0x2005, 1, 0); // CCM_SETUNICODEFORMAT
        Send(list, LvmSetExtendedStyle, 0, 0x10020); // double buffer and full-row selection
        Send(list, LvmFirst + 1, 0, (nint)Background);
        Send(list, LvmFirst + 36, 0, (nint)Text);
        Send(list, LvmFirst + 38, 0, (nint)Background);
        for (int i = 0; i < Columns.Length; i++)
        {
            fixed (char* title = Columns[i])
            {
                var column = new Column { Mask = 2 | 4 | 8, Text = title, Width = ColumnWidths[i], SubItem = i };
                Send(list, LvmInsertColumn, (nuint)i, (nint)(&column));
            }
        }
        int* order = stackalloc int[] { 0, 7, 9, 1, 2, 3, 4, 5, 6, 8 };
        Send(list, LvmFirst + 58, (nuint)Columns.Length, (nint)order);
        ShowWindow(list, tableMode ? 5 : 0);
        ShowWindow(cards, tableMode ? 0 : 5);
        int initialDpi = (int)GetDpiForWindow(panel);
        var placement = new Rect();
        var monitor = new MonitorInfo { Size = (uint)sizeof(MonitorInfo) };
        if (GetWindowRect(panel, &placement) != 0 && GetMonitorInfo(MonitorFromWindow(panel, 2), &monitor) != 0)
        {
            int width = Math.Min(1260 * initialDpi / 96, monitor.Work.Right - monitor.Work.Left);
            int height = Math.Min(700 * initialDpi / 96, monitor.Work.Bottom - monitor.Work.Top);
            placement.Left = Math.Clamp(placement.Left, monitor.Work.Left, monitor.Work.Right - width);
            placement.Top = Math.Clamp(placement.Top, monitor.Work.Top, monitor.Work.Bottom - height);
            placement.Right = placement.Left + width;
            placement.Bottom = placement.Top + height;
            ApplyDpi(initialDpi, &placement);
        }
        else ApplyDpi(initialDpi, null);
        source.SnapshotChanged += OnSnapshotChanged;
        Refresh();
        ShowWindow(panel, 5);
        UpdateWindow(panel);
        SetFocus(tableMode ? list : cards);
        return true;
    }

    private nint Child(string cls, string label, uint style, int id) =>
        CreateWindow(0, cls, label, WsChild | WsVisible | style, 0, 0, 0, 0, panel, id, module, 0);

    private int Scale(int size) => (size * dpi + 48) / 96;

    private void ApplyDpi(int value, Rect* suggested)
    {
        if (value <= 0 || list == 0) return;
        dpi = value;
        nint nextFont = Face(15, 400);
        if (nextFont != 0)
        {
            nint nextBold = Face(17, 650);
            nint nextSmall = Face(14, 400);
            nint nextScore = Face(29, 650);
            Send(title, WmSetFont, (nuint)nextBold, 1);
            Send(status, WmSetFont, (nuint)nextFont, 1);
            Send(searchLabel, WmSetFont, (nuint)nextFont, 1);
            Send(search, WmSetFont, (nuint)nextFont, 1);
            Send(groups, WmSetFont, (nuint)nextFont, 1);
            Send(list, WmSetFont, (nuint)nextFont, 1);
            FreeFonts();
            font = nextFont;
            boldFont = nextBold;
            smallFont = nextSmall;
            scoreFont = nextScore;
        }
        for (int i = 0; i < Columns.Length; i++)
            Send(list, LvmSetColumnWidth, (nuint)i, Scale(ColumnWidths[i]));
        if (suggested != null)
            MoveWindow(panel, suggested->Left, suggested->Top, suggested->Right - suggested->Left,
                suggested->Bottom - suggested->Top, 1);
        Layout();
    }

    private nint Face(int size, int weight) =>
        CreateFont(-Scale(size), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 0, 0, "Segoe UI");

    private void FreeFonts()
    {
        if (font != 0) DeleteObject(font);
        if (boldFont != 0) DeleteObject(boldFont);
        if (smallFont != 0) DeleteObject(smallFont);
        if (scoreFont != 0) DeleteObject(scoreFont);
        font = boldFont = smallFont = scoreFont = 0;
    }

    private int ToolbarHeight(int width) => Scale(width >= Scale(460) ? 112 : 147);

    private void Layout()
    {
        if (list == 0) return;
        Rect size;
        GetClientRect(panel, &size);
        int pad = Scale(14), width = Math.Max(0, size.Right - pad * 2), top = ToolbarHeight(size.Right);
        bool compact = size.Right < Scale(460);
        int searchWidth = compact ? width : Math.Min(Scale(288), width - Scale(290));
        int controlX = compact ? pad : pad + searchWidth + Scale(10), controlY = Scale(compact ? 103 : 68);
        MoveWindow(title, pad, Scale(11), width, Scale(24), 1);
        MoveWindow(status, pad, Scale(40), width, Scale(20), 1);
        MoveWindow(searchLabel, pad, Scale(68), Scale(38), Scale(28), 1);
        MoveWindow(search, pad + Scale(42), Scale(68), Math.Max(0, searchWidth - Scale(42)), Scale(28), 1);
        MoveWindow(toggle, controlX, controlY, Scale(90), Scale(28), 1);
        MoveWindow(sortButton, controlX + Scale(98), controlY, Scale(76), Scale(28), 1);
        MoveWindow(groups, controlX + Scale(182), controlY, Scale(94), Scale(28), 1);
        MoveWindow(list, pad, top, width, Math.Max(0, size.Bottom - top - pad), 1);
        if (cards != 0) MoveWindow(cards, pad, top, width, Math.Max(0, size.Bottom - top - pad), 1);
        InvalidateRect(panel, 0, 1);
    }

    private void OnSnapshotChanged()
    {
        nint target = tray;
        if (target != 0) Post(target, SnapshotMessage, 0, 0);
    }

    private void Sort(int column)
    {
        if ((uint)column >= Columns.Length) return;
        descending = column == sortColumn && !descending;
        sortColumn = column;
        InvalidateRect(sortButton, 0, 0);
        Refresh();
    }

    private void Refresh()
    {
        if (list == 0) return;
        var snapshot = source.GetSnapshot();
        var filter = filterText.AsSpan().Trim();
        CaptureSelection();
        var filtered = new List<DesktopRow>(snapshot.Rows.Count);
        foreach (var row in snapshot.Rows)
        {
            if ((showGroups || row.Kind == NodeKind.Leaf) &&
                row.Name.AsSpan().Contains(filter, StringComparison.OrdinalIgnoreCase))
                filtered.Add(row);
        }
        filtered.Sort(CompareRows);
        rows = filtered.ToArray();
        SyncTable();
        ScrollCards(0);
        if (!tableMode && IndexOfSelection() is int selected && selected >= 0) SelectCard(selected);
        UpdateStatus(snapshot);
    }

    private int TableSelection() => (int)Send(list, LvmGetNextItem, nuint.MaxValue, 2);

    private void CaptureSelection()
    {
        if (!tableMode) return;
        int index = TableSelection();
        if ((uint)index < rows.Length) selectedId = rows[index].Id;
    }

    private void SyncTable()
    {
        var state = new Item { StateMask = 3 };
        Send(list, LvmSetItemState, nuint.MaxValue, (nint)(&state));
        Send(list, LvmSetItemCount, (nuint)rows.Length, 2);
        if (selectedId != null)
        {
            int index = IndexOfSelection();
            if (index >= 0)
            {
                state.State = 3;
                Send(list, LvmSetItemState, (nuint)index, (nint)(&state));
                if (tableMode) Send(list, LvmEnsureVisible, (nuint)index, 0);
            }
        }
        InvalidateRect(list, 0, 1);
    }

    private void UpdateStatus(DesktopSnapshot? snapshot = null)
    {
        snapshot ??= source.GetSnapshot();
        string collection = snapshot.Collection switch
        {
            CollectionState.Synthetic => "合成数据 · 桌面基线",
            CollectionState.Observing => "观察中",
            CollectionState.Idle => "无活跃连接",
            CollectionState.SleepGap => "休眠间隔",
            CollectionState.SourceStopped => "采集已停止",
            _ => "未接入"
        };
        SetText(status, $"{collection}  |  {snapshot.Host}  |  实际链路：{snapshot.VerifiedCurrentPath ?? "未知"}  |  {rows.Length} 行  |  测试勾选 {tested.Count}  |  更新：{Timestamp(snapshot.UpdatedUtc)} UTC");
    }

    private void SwitchView()
    {
        CaptureSelection();
        tableMode = !tableMode;
        ShowWindow(list, tableMode ? 5 : 0);
        ShowWindow(cards, tableMode ? 0 : 5);
        SetText(toggle, tableMode ? "切换卡片" : "切换表格");
        if (tableMode) SyncTable();
        else if (IndexOfSelection() is int index && index >= 0) SelectCard(index);
        SetFocus(tableMode ? list : cards);
    }

    private int CompareRows(DesktopRow a, DesktopRow b)
    {
        long? av = Number(a), bv = Number(b);
        if (sortColumn >= 2 && av.HasValue != bv.HasValue) return av.HasValue ? -1 : 1;
        int order = sortColumn switch
        {
            0 => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name),
            1 => b.IsCurrent.CompareTo(a.IsCurrent),
            _ => Nullable.Compare(av, bv)
        };
        if (order == 0) return StringComparer.Ordinal.Compare(a.Id, b.Id);
        return descending ? -order : order;
    }

    private long? Number(DesktopRow row) => sortColumn switch
    {
        2 => row.Native.ElapsedMs, 3 => row.OpenAi.ElapsedMs, 4 => row.ChatGpt.ElapsedMs,
        5 => row.GitHub.ElapsedMs, 6 => row.LastObservedUtc?.UtcTicks, 7 => row.Quality?.Score,
        8 => tested.Contains(row.Id) ? 1 : 0, 9 => (long)(row.Quality?.State ?? QualityState.Insufficient), _ => null
    };

    private void FillCell(Item* item)
    {
        if ((item->Mask & 1) == 0 || item->Text == null || item->TextCapacity < 1 ||
            (uint)item->Index >= rows.Length) return;
        DesktopRow row = rows[item->Index];
        string value = item->SubItem switch
        {
            0 => row.Kind == NodeKind.Group ? $"[组] {row.Name}" : row.Name,
            1 => row.IsCurrent ? "当前" : "—",
            2 => row.Native.DisplayText, 3 => row.OpenAi.DisplayText,
            4 => row.ChatGpt.DisplayText, 5 => row.GitHub.DisplayText,
            6 => Timestamp(row.LastObservedUtc), 7 => row.Quality?.Score is int score ? $"{score} 分" : "—",
            8 => row.Kind == NodeKind.Group ? "—" : tested.Contains(row.Id) ? "已勾选" : "未勾选",
            9 => GetTone(row).Label, _ => ""
        };
        int count = Math.Min(value.Length, item->TextCapacity - 1);
        value.AsSpan(0, count).CopyTo(new Span<char>(item->Text, count));
        item->Text[count] = '\0';
    }

    private static string Timestamp(DateTimeOffset? timestamp) =>
        timestamp?.UtcDateTime.ToString("MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "未知";
}
