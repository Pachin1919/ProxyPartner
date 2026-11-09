using static ProxyPartnerLite.Desktop.Win32;

namespace ProxyPartnerLite.Desktop;

public sealed unsafe partial class DesktopApp
{
    private const string CardsClass = "ProxyPartner.Cards";
    private static readonly uint Background = Color(0x14161B), Tools = Color(0x1A1D23),
        Control = Color(0x23262D), Border = Color(0x373C46), Text = Color(0xF2F4F7),
        Muted = Color(0xAFB6C2), Focus = Color(0x8BADFF), Active = Color(0x315AC9),
        ActiveText = Color(0xF7F9FF);
    private nint cards, toggle, sortButton, title, searchLabel, smallFont, boldFont, scoreFont, editBrush, toolsBrush;
    private bool tableMode;
    private string? selectedId;
    private string filterText = "";
    private bool showGroups;
    private readonly HashSet<string> tested = new(StringComparer.Ordinal);
    private int columns = 1, cardWidth, cardHeight, cardGap, scrollY, viewportHeight;

    private readonly record struct Tone(uint Background, uint Accent, uint Text, string Label);

    private static Tone GetTone(DesktopRow row) => row.Quality?.State switch
    {
        QualityState.Good => new(Color(0x17382E), Color(0x4CC99A), Color(0x81E9BC), "表现好"),
        QualityState.Poor => new(Color(0x45272C), Color(0xF27473), Color(0xFFACA7), "表现差"),
        QualityState.Restricted => new(Color(0x3C3021), Color(0xDDA34B), Color(0xF6CF83), "服务受限"),
        _ => new(Color(0x242831), Color(0x6C7483), Color(0xBEC6D3), "样本不足")
    };

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
                    if (localX >= cardWidth - Scale(48) && y % (cardHeight + cardGap) < Scale(43))
                        ToggleTest(index);
                }
                return 0;
            case WmKeyDown:
                int selected = IndexOfSelection();
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
        cardGap = Scale(12);
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
        scrollY = Math.Clamp(position, 0, Math.Max(0, extent - viewportHeight));
        var info = new Scroll
        {
            Size = (uint)sizeof(Scroll), Mask = 0xf, Max = Math.Max(0, extent - 1),
            Page = (uint)Math.Max(0, viewportHeight), Position = scrollY
        };
        SetScrollInfo(cards, 1, &info, 1);
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
            Label(dc, "没有匹配的节点", Scale(16), Scale(14), area.Right, Scale(30), Muted, font);
            return;
        }
        int pitch = cardHeight + cardGap;
        int firstRow = (scrollY + area.Top) / pitch;
        int lastRow = (scrollY + Math.Max(0, area.Bottom - 1)) / pitch;
        for (int index = firstRow * columns; index < rows.Length && index / columns <= lastRow; index++)
        {
            int x = index % columns * (cardWidth + cardGap);
            int y = index / columns * pitch - scrollY;
            var row = rows[index];
            var tone = GetTone(row);
            bool selected = row.Id == selectedId;
            int inset = Scale(2);
            if (selected)
                Fill(dc, Box(x, y, cardWidth, cardHeight), GetFocus() == cards ? Focus : Active);
            else inset = 0;
            Fill(dc, Box(x + inset, y + inset, cardWidth - 2 * inset, cardHeight - 2 * inset), tone.Background);
            Fill(dc, Box(x + inset, y + inset, Scale(3), cardHeight - 2 * inset), tone.Accent);
            int left = x + Scale(13), right = x + cardWidth - Scale(12);
            int at(int percent) => y + cardHeight * percent / 100;
            Label(dc, row.Kind == NodeKind.Group ? "策略组" : row.IsCurrent ? "当前节点" : "节点", left, at(5), right - Scale(37), Scale(18), Muted, smallFont);
            if (row.Kind == NodeKind.Leaf)
            {
                bool tick = tested.Contains(row.Id);
                Fill(dc, Box(right - Scale(19), y + Scale(11), Scale(19), Scale(19)), tick ? Active : Border);
                Label(dc, tick ? "✓" : "□", right - Scale(18), y + Scale(11), right, Scale(19), tick ? ActiveText : Muted, smallFont, 1);
            }
            Label(dc, row.Name, left, at(15), right, Scale(23), Text, boldFont);
            Label(dc, row.Quality?.Score is int score ? $"{score} 分" : "—", left, at(28), right, Scale(32), tone.Text, scoreFont);
            Label(dc, tone.Label, left, at(44), right, Scale(18), tone.Text, smallFont);
            Fill(dc, Box(left, at(55), Math.Max(0, right - left), 1), tone.Accent);
            Label(dc, $"原生延迟  {row.Native.DisplayText}", left, at(58), right, Scale(16), Text, smallFont);
            Label(dc, $"OpenAI  {row.OpenAi.DisplayText}", left, at(66), right, Scale(16), Text, smallFont);
            Label(dc, $"ChatGPT  {row.ChatGpt.DisplayText}", left, at(74), right, Scale(16), Text, smallFont);
            Label(dc, $"GitHub  {row.GitHub.DisplayText}", left, at(82), right, Scale(16), Text, smallFont);
            Label(dc, $"样本  {Timestamp(row.LastObservedUtc)} UTC", left, at(91), right, Scale(15), Muted, smallFont);
        }
    }

    private void DrawToolbarButton(DrawItem* item)
    {
        if (item->Id is not (Toggle or SortMenu or Groups)) return;
        var bounds = item->Bounds;
        bool activeButton = item->Id == Toggle || item->Id == Groups && showGroups;
        Fill(item->Dc, bounds, (item->State & 0x10) != 0 ? Focus : Border);
        var inside = Box(bounds.Left + 1, bounds.Top + 1, bounds.Right - bounds.Left - 2, bounds.Bottom - bounds.Top - 2);
        Fill(item->Dc, inside, activeButton || (item->State & 1) != 0 ? Active : Control);
        SetBkMode(item->Dc, 1);
        string label = item->Id switch
        {
            Toggle => tableMode ? "切换卡片" : "切换表格",
            Groups => showGroups ? "✓ 策略组" : "策略组",
            _ => descending ? "排序 ↓" : "排序 ↑"
        };
        Label(item->Dc, label, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom - bounds.Top,
            activeButton ? ActiveText : Text, font, 1);
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
        var tone = GetTone(rows[(int)item->Item]);
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
