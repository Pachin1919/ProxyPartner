using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static ProxyPartnerLite.Desktop.Win32;

namespace ProxyPartnerLite.Desktop;

public sealed unsafe class DesktopApp
{
    private const string TrayClass = "ProxyPartner.Tray", PanelClass = "ProxyPartner.Panel";
    private const uint TrayMessage = 0x8001, SnapshotMessage = 0x8002;
    private const int Open = 101, Exit = 102, Search = 103, Groups = 104, SortFirst = 110;
    private static readonly string[] Columns = ["节点", "当前", "原生延迟", "OpenAI API", "ChatGPT", "GitHub", "最近样本 (UTC)"];
    private static readonly int[] ColumnWidths = [192, 54, 102, 126, 112, 102, 142];
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
        if (!Register(TrayClass) || !Register(PanelClass)) return 1;
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
                msg.WParam is >= 0x31 and <= 0x37)
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
                    panel = status = search = groups = list = 0;
                    if (font != 0) DeleteObject(font);
                    font = 0;
                    return 0;
                case WmDpiChanged:
                    ApplyDpi((int)wParam & 0xffff, (Rect*)lParam);
                    return 0;
                case WmCommand:
                    int id = (int)wParam & 0xffff;
                    if (id == Groups || id == Search && ((int)wParam >> 16) == 0x300) Refresh();
                    if (id >= SortFirst && id < SortFirst + Columns.Length) Sort(id - SortFirst);
                    return 0;
                case WmNotify:
                    var header = (NotifyHeader*)lParam;
                    if (header->Window != list) break;
                    if (header->Code == LvnGetDisplayInfo) FillCell(&((DisplayInfo*)lParam)->Item);
                    if (header->Code == LvnColumnClick) Sort(((ListNotify*)lParam)->SubItem);
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
        status = Child("STATIC", "", 0, 0);
        search = Child("EDIT", "", WsTabStop | 0x800000 | 0x80, Search);
        groups = Child("BUTTON", "显示策略组", WsTabStop | 3, Groups);
        list = Child("SysListView32", "", WsTabStop | 0x800000 | 0x1000 | 1 | 4 | 8, 105);
        if (status == 0 || search == 0 || groups == 0 || list == 0)
        {
            DestroyWindow(panel);
            return false;
        }
        Send(list, 0x2005, 1, 0); // CCM_SETUNICODEFORMAT
        Send(list, LvmSetExtendedStyle, 0, 0x10020); // double buffer and full-row selection
        fixed (char* cue = "筛选节点（Ctrl+1–7 排序）")
            Send(search, 0x1501, 1, (nint)cue);
        for (int i = 0; i < Columns.Length; i++)
        {
            fixed (char* title = Columns[i])
            {
                var column = new Column { Mask = 2 | 4 | 8, Text = title, Width = ColumnWidths[i], SubItem = i };
                Send(list, LvmInsertColumn, (nuint)i, (nint)(&column));
            }
        }
        int initialDpi = (int)GetDpiForWindow(panel);
        var placement = new Rect();
        var monitor = new MonitorInfo { Size = (uint)sizeof(MonitorInfo) };
        if (GetWindowRect(panel, &placement) != 0 && GetMonitorInfo(MonitorFromWindow(panel, 2), &monitor) != 0)
        {
            int width = Math.Min(900 * initialDpi / 96, monitor.Work.Right - monitor.Work.Left);
            int height = Math.Min(500 * initialDpi / 96, monitor.Work.Bottom - monitor.Work.Top);
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
        SetFocus(search);
        return true;
    }

    private nint Child(string cls, string label, uint style, int id) =>
        CreateWindow(0, cls, label, WsChild | WsVisible | style, 0, 0, 0, 0, panel, id, module, 0);

    private int Scale(int size) => (size * dpi + 48) / 96;

    private void ApplyDpi(int value, Rect* suggested)
    {
        if (value <= 0 || list == 0) return;
        dpi = value;
        nint nextFont = CreateFont(-Scale(16), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, "Segoe UI");
        if (nextFont != 0)
        {
            Send(status, WmSetFont, (nuint)nextFont, 1);
            Send(search, WmSetFont, (nuint)nextFont, 1);
            Send(groups, WmSetFont, (nuint)nextFont, 1);
            Send(list, WmSetFont, (nuint)nextFont, 1);
            if (font != 0) DeleteObject(font);
            font = nextFont;
        }
        for (int i = 0; i < Columns.Length; i++)
            Send(list, LvmSetColumnWidth, (nuint)i, Scale(ColumnWidths[i]));
        if (suggested != null)
            MoveWindow(panel, suggested->Left, suggested->Top, suggested->Right - suggested->Left,
                suggested->Bottom - suggested->Top, 1);
        Layout();
    }

    private void Layout()
    {
        if (list == 0) return;
        Rect size;
        GetClientRect(panel, &size);
        int pad = Scale(12), width = Math.Max(0, size.Right - pad * 2), top = Scale(70);
        MoveWindow(status, pad, pad, width, Scale(22), 1);
        MoveWindow(search, pad, Scale(38), Scale(250), Scale(24), 1);
        MoveWindow(groups, Scale(278), Scale(38), Scale(132), Scale(24), 1);
        MoveWindow(list, pad, top, width, Math.Max(0, size.Bottom - top - pad), 1);
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
        Refresh();
    }

    private void Refresh()
    {
        if (list == 0) return;
        var snapshot = source.GetSnapshot();
        char* text = stackalloc char[256];
        int length = GetText(search, text, 256);
        var filter = new ReadOnlySpan<char>(text, length).Trim();
        bool includeGroups = Send(groups, 0xf0, 0, 0) == 1;
        int selected = (int)Send(list, LvmGetNextItem, nuint.MaxValue, 2);
        string? selectedId = (uint)selected < rows.Length ? rows[selected].Id : null;
        var filtered = new List<DesktopRow>(snapshot.Rows.Count);
        foreach (var row in snapshot.Rows)
        {
            if ((includeGroups || row.Kind == NodeKind.Leaf) &&
                row.Name.AsSpan().Contains(filter, StringComparison.OrdinalIgnoreCase))
                filtered.Add(row);
        }
        filtered.Sort(CompareRows);
        rows = filtered.ToArray();
        var state = new Item { StateMask = 3 };
        Send(list, LvmSetItemState, nuint.MaxValue, (nint)(&state));
        Send(list, LvmSetItemCount, (nuint)rows.Length, 2);
        if (selectedId != null)
        {
            int index = Array.FindIndex(rows, row => row.Id == selectedId);
            if (index >= 0)
            {
                state.State = 3;
                Send(list, LvmSetItemState, (nuint)index, (nint)(&state));
            }
        }
        InvalidateRect(list, 0, 1);
        string collection = snapshot.Collection switch
        {
            CollectionState.Synthetic => "合成数据 · 桌面基线",
            CollectionState.Observing => "观察中",
            CollectionState.Idle => "无活跃连接",
            CollectionState.SleepGap => "休眠间隔",
            CollectionState.SourceStopped => "采集已停止",
            _ => "未接入"
        };
        SetText(status, $"{collection}  |  {snapshot.Host}  |  实际链路：{snapshot.VerifiedCurrentPath ?? "未知"}  |  {rows.Length} 行  |  更新：{Timestamp(snapshot.UpdatedUtc)} UTC");
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
        5 => row.GitHub.ElapsedMs, 6 => row.LastObservedUtc?.UtcTicks, _ => null
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
            6 => Timestamp(row.LastObservedUtc), _ => ""
        };
        int count = Math.Min(value.Length, item->TextCapacity - 1);
        value.AsSpan(0, count).CopyTo(new Span<char>(item->Text, count));
        item->Text[count] = '\0';
    }

    private static string Timestamp(DateTimeOffset? timestamp) =>
        timestamp?.UtcDateTime.ToString("MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "未知";
}
