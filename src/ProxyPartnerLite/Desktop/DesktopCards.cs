using System.Runtime.InteropServices;
using static ProxyPartnerLite.Desktop.Win32;

namespace ProxyPartnerLite.Desktop;

public sealed unsafe partial class DesktopApp
{
    private const string CardsClass = "ProxyPartner.Cards";
    private static readonly (string Id, string Name, string Short)[] Sites =
    [
        ("google", "Google", "GO"), ("youtube", "YouTube", "YT"), ("openai_api", "OpenAI API", "OPENAI"),
        ("chatgpt_web", "ChatGPT", "GPT"), ("anthropic_api", "Anthropic API", "ANT")
    ];
    // Only the API and GPT slots contain measured results. Reserve room for
    // three Chinese characters there while keeping the other marks aligned.
    private static readonly int[] SiteEdges = [0, 18, 36, 59, 82, 100];
    private uint Background => Color(preferences.Light ? 0xE6EBF1u : 0x222933u);
    private uint Tools => Color(preferences.Light ? 0xF1F4F8u : 0x29313Du);
    private uint Control => Color(preferences.Light ? 0xFFFFFFu : 0x323B47u);
    private uint Border => Color(preferences.Light ? 0xB7C3D0u : 0x495564u);
    private uint Text => Color(preferences.Light ? 0x202B3Bu : 0xF2F4F7u);
    private uint Muted => Color(preferences.Light ? 0x475A70u : 0xC3CEDBu);
    private uint Focus => Color(preferences.Light ? 0x285C9Eu : 0xA6C3F3u);
    private static readonly uint Active = Color(0x315AC9), ActiveText = Color(0xF7F9FF);
    private nint cards, toggle, sortButton, title, searchLabel, hint, smallFont, boldFont, scoreFont, siteStatusFont, editBrush, toolsBrush;
    private nint tooltip, tipBuffer;
    private int hoverIndex = -1, hoverSite = -1;
    private nuint finishSession;
    private nint[]? siteIcons;
    private bool tableMode;
    private string? selectedId;
    private string filterText = "";
    private bool showGroups;
    private readonly HashSet<string> tested = new(StringComparer.Ordinal);
    private int columns = 1, cardWidth, cardHeight, cardGap, scrollY, viewportHeight;

    private readonly record struct Tone(uint Background, uint Accent, uint Text, string Label);

    private Tone GetTone(DesktopRow row, bool table = false)
    {
        bool light = preferences.Light;
        var tone = row.Quality?.State switch
        {
            QualityState.Good => new Tone(Color(light ? 0xD7EDE1u : 0x17382Eu), Color(light ? 0x257C60u : 0x78B89Du),
                Color(light ? 0x225942u : 0xA5E8CEu), L("表现好", "Good")),
            QualityState.Poor => new Tone(Color(light ? 0xF2DDE5u : 0x45272Cu), Color(light ? 0xA34D6Au : 0xCC8A9Eu),
                Color(light ? 0x78334Cu : 0xFFB9BAu), L("表现差", "Poor")),
            QualityState.Restricted => new Tone(Color(light ? 0xF5E9CAu : 0x3C3021u), Color(light ? 0x96621Eu : 0xCBA769u),
                Color(light ? 0x734815u : 0xF6CF83u), L("服务受限", "Restricted")),
            _ => new Tone(Color(light ? 0xE6EDF4u : 0x242831u), Color(light ? 0x60768Bu : 0x99ADBFu),
                Color(light ? 0x475A70u : 0xBEC6D3u), L("样本不足", "Low samples"))
        };
        return !light && !table ? tone with { Background = Color(0x303741), Text = Muted } : tone;
    }

    private static uint Color(uint rgb) => ((rgb & 0xff) << 16) | (rgb & 0xff00) | (rgb >> 16);

    private nint HandleCards(uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case WmSize:
                LayoutCards();
                return 0;
            case 0x14: // WM_ERASEBKGND
                return 1;
            case 0x0f: // WM_PAINT
                Paint paint;
                nint dc = BeginPaint(cards, &paint);
                DrawCards(dc, paint.Bounds);
                EndPaint(cards, &paint);
                return 0;
            case 0x318: // WM_PRINTCLIENT
                Rect client;
                GetClientRect(cards, &client);
                DrawCards((nint)wParam, client);
                return 0;
            case 0x07: // WM_SETFOCUS
            case 0x08: // WM_KILLFOCUS
                InvalidateRect(cards, 0, 0);
                return 0;
            case 0x87: // WM_GETDLGCODE
                return 0x81; // arrow keys and character input, retaining Tab navigation
            case 0x115: // WM_VSCROLL
                var scroll = new Scroll { Size = (uint)sizeof(Scroll), Mask = 0x10 };
                GetScrollInfo(cards, 1, &scroll);
                int next = ((int)wParam & 0xffff) switch
                {
                    0 => scrollY - Scale(40), 1 => scrollY + Scale(40),
                    2 => scrollY - viewportHeight, 3 => scrollY + viewportHeight,
                    4 or 5 => scroll.Track, 6 => 0, 7 => int.MaxValue, _ => scrollY
                };
                ScrollCards(next);
                return 0;
            case 0x20a: // WM_MOUSEWHEEL
                ScrollCards(scrollY - (short)(wParam >> 16) * Scale(60) / 120);
                return 0;
            case 0x200: // WM_MOUSEMOVE
                UpdateHover((short)(lParam & 0xffff), (short)(lParam >> 16));
                if (tooltip != 0)
                {
                    var motion = new Message { Window = cards, Id = msg, WParam = wParam, LParam = lParam };
                    GetCursorPos(&motion.Position);
                    Send(tooltip, 0x407, 0, (nint)(&motion)); // TTM_RELAYEVENT
                }
                return 0;
            case 0x201: // WM_LBUTTONDOWN
                SetFocus(cards);
                int x = (short)(lParam & 0xffff), y = (short)(lParam >> 16) + scrollY;
                int col = x / (cardWidth + cardGap);
                int index = y / (cardHeight + cardGap) * columns + col;
                if (x >= 0 && col < columns && x % (cardWidth + cardGap) < cardWidth &&
                    y % (cardHeight + cardGap) < cardHeight && (uint)index < rows.Length)
                {
                    SelectCard(index);
                    int localX = x - col * (cardWidth + cardGap);
                    int localY = y % (cardHeight + cardGap);
                    var check = CardCheckBounds(0, 0);
                    if (localX >= check.Left && localX < check.Right &&
                        localY >= check.Top && localY < check.Bottom)
                        ToggleTest(index);
                }
                return 0;
            case WmKeyDown:
                int selected = IndexOfSelection();
                if (wParam == 0x0d)
                {
                    ShowDetails();
                    return 0;
                }
                if (wParam == 0x20)
                {
                    ToggleTest(selected);
                    return 0;
                }
                int step = Math.Max(1, viewportHeight / (cardHeight + cardGap)) * columns;
                int target = wParam switch
                {
                    0x25 => selected - 1, 0x27 => selected + 1,
                    0x26 => selected - columns, 0x28 => selected + columns,
                    0x21 => selected - step, 0x22 => selected + step,
                    0x24 => 0, 0x23 => rows.Length - 1, _ => int.MinValue
                };
                if (target == int.MinValue) break;
                if (rows.Length > 0) SelectCard(Math.Clamp(selected < 0 && wParam != 0x23 ? 0 : target, 0, rows.Length - 1));
                return 0;
        }
        return Default(cards, msg, wParam, lParam);
    }

    private void LayoutCards()
    {
        Rect rect;
        GetClientRect(cards, &rect);
        int first = scrollY / Math.Max(1, cardHeight + cardGap) * columns;
        ClearCardHover();
        cardGap = Scale(20);
        int width = Math.Max(1, rect.Right);
        columns = Math.Clamp((width + cardGap) / (Scale(224) + cardGap), 1, 6);
        cardWidth = Math.Max(1, (width - cardGap * (columns - 1)) / columns);
        cardHeight = Math.Max(1, (cardWidth * 96 + 50) / 100);
        viewportHeight = rect.Bottom;
        ScrollCards(first / columns * (cardHeight + cardGap));
    }

    private void ScrollCards(int position)
    {
        int count = (rows.Length + columns - 1) / columns;
        int extent = Math.Max(0, count * (cardHeight + cardGap) - cardGap);
        int next = Math.Clamp(position, 0, Math.Max(0, extent - viewportHeight));
        if (next != scrollY) ClearCardHover();
        scrollY = next;
        var info = new Scroll
        {
            Size = (uint)sizeof(Scroll), Mask = 0xf, Max = Math.Max(0, extent - 1),
            Page = (uint)Math.Max(0, viewportHeight), Position = scrollY
        };
        SetScrollInfo(cards, 1, &info, 1);
        if (tooltip != 0) Send(tooltip, 0x41c, 0, 0); // TTM_POP
        InvalidateRect(cards, 0, 0);
    }

    private int IndexOfSelection() => Array.FindIndex(rows, row => row.Id == selectedId);

    private void SelectCard(int index)
    {
        selectedId = rows[index].Id;
        int top = index / columns * (cardHeight + cardGap);
        ScrollCards(top < scrollY ? top :
            top + cardHeight > scrollY + viewportHeight ? top + cardHeight - viewportHeight : scrollY);
    }

    private void ToggleTest(int index)
    {
        if ((uint)index >= rows.Length || rows[index].Kind != NodeKind.Leaf) return;
        string id = rows[index].Id;
        if (!tested.Add(id)) tested.Remove(id);
        InvalidateRect(cards, 0, 0);
        InvalidateRect(list, 0, 0);
        UpdateStatus();
    }

    private void DrawCards(nint dc, Rect area)
    {
        Fill(dc, area, Background);
        SetBkMode(dc, 1);
        if (rows.Length == 0)
        {
            Label(dc, L("没有匹配的节点", "No matching nodes"), Scale(16), Scale(14), area.Right, Scale(30), Muted, font);
            return;
        }
        int pitch = cardHeight + cardGap;
        int firstRow = (scrollY + area.Top) / pitch;
        int lastRow = (scrollY + Math.Max(0, area.Bottom - 1)) / pitch;
        int end = Math.Min(rows.Length, (lastRow + 1) * columns);
        EnsureCardArtwork();
        nint finish = CardFinish.Begin(finishSession, dc);
        for (int index = firstRow * columns; index < end; index++)
        {
            int x = index % columns * (cardWidth + cardGap);
            int y = index / columns * pitch - scrollY;
            var tone = GetTone(rows[index]);
            var bounds = Box(x, y, cardWidth, cardHeight);
            uint? outline = rows[index].Id == selectedId ? GetFocus() == cards ? Focus : Active : null;
            if (preferences.Light)
                CardFinish.Draw(finish, Box(x, y + Scale(2), cardWidth, cardHeight), Scale(12), Color(0xCCD6E0), null, 1);
            if (!CardFinish.Draw(finish, bounds, Scale(12), tone.Background, outline, Scale(2), preferences.Light))
                Fill(dc, bounds, outline ?? tone.Background);
        }
        if (finish != 0) CardFinish.End(finish);
        SetBkMode(dc, 1);
        for (int index = firstRow * columns; index < end; index++)
        {
            int x = index % columns * (cardWidth + cardGap);
            int y = index / columns * pitch - scrollY;
            var row = rows[index];
            var tone = GetTone(row);
            int left = x + Scale(17), right = x + cardWidth - Scale(17);
            int at(int percent) => y + cardHeight * percent / 100;
            if (preferences.Light) Fill(dc, Box(left, at(8) + Scale(4), Scale(2), Scale(15)), tone.Accent);
            Label(dc, row.Name, left + Scale(preferences.Light ? 9 : 0), at(8), right - Scale(25), Scale(24), Text, boldFont);
            if (row.Kind == NodeKind.Leaf)
            {
                bool tick = tested.Contains(row.Id);
                var check = CardCheckBounds(x, y);
                Rounded(dc, check, Scale(5), tick ? Active : Border);
                Label(dc, tick ? "✓" : "□", check.Left + Scale(1), check.Top, check.Right,
                    check.Bottom - check.Top, tick ? ActiveText : Muted, smallFont, 1);
            }
            int midpoint = (left + right) / 2;
            uint scoreColor = preferences.Light ? Text : DarkScoreColor(row.Quality?.Score) ?? Text;
            Label(dc, ScoreText(row), left, at(23), midpoint, Scale(38), scoreColor, scoreFont);
            int badgeLeft = Math.Max(midpoint, right - Scale(102));
            Rounded(dc, Box(badgeLeft, at(28), right - badgeLeft, Scale(20)), Scale(8), Control);
            Rounded(dc, Box(badgeLeft + Scale(7), at(28) + Scale(8), Scale(5), Scale(5)), Scale(5), tone.Accent);
            Label(dc, tone.Label, badgeLeft + Scale(17), at(28), right - Scale(4), Scale(20), tone.Text, smallFont);
            if (row.IsCurrent || row.Kind == NodeKind.Group)
                Label(dc, row.IsCurrent ? L("宿主当前", "Host current") : L("策略组", "Policy group"), midpoint, at(38), right, Scale(16), Muted, smallFont, 2);
            Fill(dc, Box(left, at(48), Math.Max(0, right - left), 1), Border);
            DrawSites(dc, row, left, right, at(55), at(66), at(75));
            Fill(dc, Box(left, at(86), Math.Max(0, right - left), 1), Border);
            Label(dc, $"{L("原生", "Native")} {ShortResult(row.Native)}", left, at(89), midpoint, Scale(17), Muted, smallFont);
            Label(dc, Timestamp(row.LastObservedUtc), midpoint, at(89), right, Scale(17), Muted, smallFont, 2);
        }
    }

    private static uint? DarkScoreColor(int? score) => score switch
    {
        >= 0 and < 60 => Color(0xF29DA2),
        >= 60 and < 80 => Color(0xE6C477),
        >= 80 and <= 100 => Color(0x79C79D),
        _ => null
    };

    private Rect CardCheckBounds(int x, int y)
    {
        int size = Scale(19);
        return Box(x + cardWidth - Scale(17) - size, y + cardHeight * 8 / 100 + Scale(2), size, size);
    }

    private void ClearCardHover()
    {
        if (hoverIndex == -1) return;
        hoverIndex = hoverSite = -1;
        if (!tableMode) SetText(hint, HoverText());
    }

    private void EnsureCardArtwork()
    {
        if (siteIcons is not null) return;
        finishSession = CardFinish.Start();
        siteIcons = new nint[Sites.Length];
        for (int index = 0; index < siteIcons.Length; index++)
            if (index != 3) siteIcons[index] = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "TargetIcons", $"{Sites[index].Id}.ico"), 1, 0, 0, 0x10);
    }

    private void FreeCardArtwork()
    {
        if (tooltip != 0) DestroyWindow(tooltip);
        if (tipBuffer != 0) Marshal.FreeHGlobal(tipBuffer);
        tooltip = tipBuffer = 0;
        hoverIndex = hoverSite = -1;
        if (siteIcons is not null)
            foreach (nint mark in siteIcons)
                if (mark != 0) DestroyIcon(mark);
        siteIcons = null;
        if (finishSession != 0) CardFinish.EndSession(finishSession);
        finishSession = 0;
    }

    private static DesktopResult? SiteResult(DesktopRow row, int site) => site switch
    {
        2 => row.OpenAi, 3 => row.ChatGpt, _ => null
    };

    private string ShortResult(DesktopResult? result) => result?.State switch
    {
        ResultState.AuthenticationRequired => L("鉴权", "Auth"),
        ResultState.Restricted => L("受限", "Blocked"),
        ResultState.Failed => L("失败", "Failed"),
        ResultState.Stale => L("过期", "Stale"),
        ResultState.Incomparable => L("不可比", "N/A"),
        ResultState.Measured when result.ElapsedMs is int ms => $"{ms} ms",
        _ => L("待测", "N/T")
    };

    private void DrawSites(nint dc, DesktopRow row, int left, int right, int iconY, int labelY, int statusY)
    {
        int size = Scale(18);
        for (int index = 0; index < Sites.Length; index++)
        {
            int start = left + (right - left) * SiteEdges[index] / 100;
            int end = left + (right - left) * SiteEdges[index + 1] / 100;
            int iconX = (start + end - size) / 2;
            nint mark = siteIcons![index];
            if (mark != 0 || index == 3)
            {
                Rounded(dc, Box(iconX - Scale(2), iconY - Scale(2), size + Scale(4), size + Scale(4)),
                    Scale(6), Color(preferences.Light ? 0xF8FAFCu : 0xE0E9F4u));
                if (index == 3)
                {
                    uint bubble = Color(0x24566B);
                    Rounded(dc, Box(iconX + Scale(1), iconY + Scale(2), Scale(16), Scale(12)), Scale(5), bubble);
                    Fill(dc, Box(iconX + Scale(4), iconY + Scale(13), Scale(3), Scale(3)), bubble);
                    for (int dot = 0; dot < 3; dot++)
                        Fill(dc, Box(iconX + Scale(4 + dot * 4), iconY + Scale(7), Scale(2), Scale(2)), ActiveText);
                }
                else DrawIconEx(dc, iconX, iconY, mark, size, size, 0, 0, 3);
                Label(dc, Sites[index].Short, start, labelY, end, Scale(17), Muted, siteStatusFont, 1);
            }
            else
            {
                Fill(dc, Box(start + Scale(2), iconY, Math.Max(0, end - start - Scale(4)), Scale(23)), Control);
                Label(dc, Sites[index].Short, start, iconY, end, Scale(23), Text, siteStatusFont, 1);
            }
            var result = SiteResult(row, index);
            string state = result is { State: ResultState.Measured, ElapsedMs: int ms } ? $"{ms}ms" : ShortResult(result);
            Label(dc, state, start, statusY, end, Scale(18), result is null ? Muted : Text, siteStatusFont, 1);
        }
    }

    private void CreateCardTooltip()
    {
        tooltip = CreateWindow(8, "tooltips_class32", "", 0x80000003, UseDefault, UseDefault,
            UseDefault, UseDefault, panel, 0, module, 0);
        if (tooltip == 0) return;
        Send(tooltip, 0x2005, 1, 0); // CCM_SETUNICODEFORMAT
        var tool = new TooltipTool { Size = (uint)sizeof(TooltipTool), Flags = 1, Window = panel,
            Id = (nuint)cards, Text = -1 };
        Send(tooltip, 0x432, 0, (nint)(&tool)); // TTM_ADDTOOLW
        Send(tooltip, 0x418, 0, Scale(420)); // TTM_SETMAXTIPWIDTH
    }

    private void UpdateHover(int x, int y, bool dismiss = true)
    {
        if (cardWidth == 0 || x < 0 || y < 0) return;
        y += scrollY;
        int col = x / (cardWidth + cardGap), pitch = cardHeight + cardGap;
        int localX = x % (cardWidth + cardGap), localY = y % pitch;
        int index = y / pitch * columns + col, site = -1;
        if (col >= columns || localX >= cardWidth || localY >= cardHeight || index >= rows.Length) index = -1;
        else if (localY >= cardHeight * 52 / 100 && localY < cardHeight * 86 / 100)
        {
            int inset = Scale(17), available = cardWidth - inset * 2;
            if (available > 0 && localX >= inset && localX < cardWidth - inset)
            {
                for (int indexOfSite = 0; indexOfSite < Sites.Length; indexOfSite++)
                {
                    if (localX - inset >= available * SiteEdges[indexOfSite + 1] / 100) continue;
                    site = indexOfSite;
                    break;
                }
            }
        }
        if (hoverIndex == index && hoverSite == site) return;
        hoverIndex = index;
        hoverSite = site;
        SetText(hint, HoverText());
        if (dismiss && tooltip != 0) Send(tooltip, 0x41c, 0, 0);
    }

    private string HoverText()
    {
        if ((uint)hoverIndex >= rows.Length) return L("悬停查看站点状态", "Hover to see target details");
        var row = rows[hoverIndex];
        return hoverSite < 0 ? row.Name :
            $"{Sites[hoverSite].Name} · {SiteResult(row, hoverSite)?.Format(preferences.English) ?? L("待测", "Untested")}";
    }

    private void GetTooltipText(TooltipInfo* info)
    {
        Point point;
        GetCursorPos(&point);
        ScreenToClient(cards, &point);
        UpdateHover(point.X, point.Y, dismiss: false);
        if (tipBuffer != 0) Marshal.FreeHGlobal(tipBuffer);
        tipBuffer = Marshal.StringToHGlobalUni(HoverText());
        info->Text = tipBuffer;
    }

    private void DrawToolbarButton(DrawItem* item)
    {
        if (item->Id is not (Toggle or SortMenu or Groups or OpenSettings or OpenDetails)) return;
        bool activeButton = item->Id == Toggle || item->Id == Groups && showGroups;
        string label = item->Id switch
        {
            Toggle => ViewLabel,
            Groups => showGroups ? L("✓ 策略组", "✓ Groups") : L("策略组", "Groups"),
            OpenSettings => L("设置", "Settings"),
            OpenDetails => L("详情", "Details"),
            _ => descending ? L("排序 ↓", "Sort ↓") : L("排序 ↑", "Sort ↑")
        };
        DrawPanelButton(item, label, font, activeButton);
    }

    private void DrawPanelButton(DrawItem* item, string label, nint face, bool primary)
    {
        var bounds = item->Bounds;
        bool accent = primary || (item->State & 1) != 0;
        Fill(item->Dc, bounds, (item->State & 0x10) != 0 ? Focus : Border);
        var inside = Box(bounds.Left + 1, bounds.Top + 1, bounds.Right - bounds.Left - 2, bounds.Bottom - bounds.Top - 2);
        Fill(item->Dc, inside, accent ? Active : Control);
        SetBkMode(item->Dc, 1);
        Label(item->Dc, label, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom - bounds.Top,
            accent ? ActiveText : Text, face, 1);
    }

    private void ShowSortMenu()
    {
        nint menu = CreatePopupMenu();
        if (menu == 0) return;
        for (int index = 0; index < Columns.Length; index++)
            AppendMenu(menu, index == sortColumn ? 8u : 0, (nuint)(SortFirst + index), Columns[index]);
        Rect rect;
        GetWindowRect(sortButton, &rect);
        uint command = TrackPopupMenu(menu, 0x102, rect.Left, rect.Bottom, 0, panel, 0);
        DestroyMenu(menu);
        if (command >= SortFirst && command < SortFirst + Columns.Length)
            Sort((int)command - SortFirst);
    }

    private nint DrawTable(CustomDraw* item)
    {
        if (item->Stage == 1) return 0x20;
        if (item->Stage != 0x10001 || item->Item >= (nuint)rows.Length) return 0;
        var tone = GetTone(rows[(int)item->Item], table: true);
        item->BackgroundColor = tone.Background;
        item->TextColor = tone.Text;
        return 0;
    }

    private static Rect Box(int x, int y, int width, int height) =>
        new() { Left = x, Top = y, Right = x + width, Bottom = y + height };

    private static void Fill(nint dc, Rect rect, uint color)
    {
        SetDCBrushColor(dc, color);
        FillRect(dc, &rect, GetStockObject(18)); // DC_BRUSH
    }

    private static void Rounded(nint dc, Rect rect, int diameter, uint color)
    {
        if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) return;
        nint previousBrush = SelectObject(dc, GetStockObject(18));
        nint previousPen = SelectObject(dc, GetStockObject(8)); // NULL_PEN
        SetDCBrushColor(dc, color);
        RoundRect(dc, rect.Left, rect.Top, rect.Right, rect.Bottom, diameter, diameter);
        SelectObject(dc, previousPen);
        SelectObject(dc, previousBrush);
    }

    private static void Label(nint dc, string value, int x, int y, int right, int height, uint color, nint face, uint align = 0)
    {
        if (right <= x) return;
        SetTextColor(dc, color);
        var rect = Box(x, y, right - x, height);
        nint previous = face == 0 ? 0 : SelectObject(dc, face);
        fixed (char* text = value)
            DrawText(dc, text, value.Length, &rect, align | 0x4 | 0x20 | 0x800 | 0x8000);
        if (previous != 0) SelectObject(dc, previous);
    }
}
