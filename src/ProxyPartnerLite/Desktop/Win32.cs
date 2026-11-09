using System.Runtime.InteropServices;

namespace ProxyPartnerLite.Desktop;

internal static unsafe partial class Win32
{
    internal const int WmClose = 0x10, WmDestroy = 2, WmSize = 5, WmNotify = 0x4e,
        WmCommand = 0x111, WmSetFont = 0x30, WmDpiChanged = 0x2e0, WmKeyDown = 0x100;
    internal const int LvmFirst = 0x1000, LvmGetNextItem = LvmFirst + 12,
        LvmSetItemCount = LvmFirst + 47, LvmSetItemState = LvmFirst + 43,
        LvmEnsureVisible = LvmFirst + 19, LvmInsertColumn = LvmFirst + 97,
        LvmSetColumnWidth = LvmFirst + 30, LvmSetExtendedStyle = LvmFirst + 54;
    internal const int LvnGetDisplayInfo = -177, LvnColumnClick = -108;
    internal const uint WsChild = 0x40000000, WsVisible = 0x10000000, WsTabStop = 0x10000,
        WsOverlappedWindow = 0xcf0000, WsClipChildren = 0x02000000;
    internal const int UseDefault = unchecked((int)0x80000000);

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowClass
    {
        public uint Size, Style;
        public delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> Proc;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background, Menu;
        public char* Name;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo { public uint Size; public Rect Bounds, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Position;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NotifyHeader { public nint Window; public nuint Id; public int Code; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Column
    {
        public uint Mask;
        public int Format, Width;
        public char* Text;
        public int TextCapacity, SubItem, Image, Order, MinWidth, DefaultWidth, IdealWidth;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Item
    {
        public uint Mask;
        public int Index, SubItem;
        public uint State, StateMask;
        public char* Text;
        public int TextCapacity, Image;
        public nint Param;
        public int Indent, GroupId;
        public uint Columns;
        public nint ColumnIndices, ColumnFormats;
        public int Group;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayInfo { public NotifyHeader Header; public Item Item; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ListNotify
    {
        public NotifyHeader Header;
        public int Item, SubItem;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct CustomDraw
    {
        public NotifyHeader Header;
        public uint Stage;
        public nint Dc;
        public Rect Bounds;
        public nuint Item;
        public uint State;
        public nint Param;
        public uint TextColor, BackgroundColor;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct DrawItem
    {
        public uint Type, Id, Item, Action, State;
        public nint Window, Dc;
        public Rect Bounds;
        public nuint Data;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Paint
    {
        public nint Dc;
        public int Erase;
        public Rect Bounds;
        public int Restore, Update;
        public fixed byte Reserved[32];
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Scroll
    {
        public uint Size, Mask;
        public int Min, Max;
        public uint Page;
        public int Position, Track;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct TooltipTool
    {
        public uint Size, Flags;
        public nint Window;
        public nuint Id;
        public Rect Bounds;
        public nint Instance, Text, Data, Reserved;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct TooltipInfo
    {
        public NotifyHeader Header;
        public nint Text;
        public fixed char Buffer[80];
        public nint Instance;
        public uint Flags;
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NotifyIcon
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, Callback;
        public nint Icon;
        public fixed char Tip[128];
        public uint State, StateMask;
        public fixed char Info[256];
        public uint Version;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }

    [LibraryImport("kernel32", EntryPoint = "GetModuleHandleW")]
    internal static partial nint GetModuleHandle(nint name);
    [LibraryImport("comctl32")]
    internal static partial int InitCommonControlsEx(uint* controls);
    [LibraryImport("user32", EntryPoint = "RegisterClassExW")]
    internal static partial ushort RegisterClass(WindowClass* value);
    [LibraryImport("user32", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWindow(uint ex, string cls, string text, uint style, int x, int y,
        int width, int height, nint parent, nint menu, nint instance, nint param);
    [LibraryImport("user32", EntryPoint = "DefWindowProcW")]
    internal static partial nint Default(nint window, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32", EntryPoint = "GetMessageW")]
    internal static partial int GetMessage(Message* msg, nint window, uint first, uint last);
    [LibraryImport("user32")]
    internal static partial int TranslateMessage(Message* msg);
    [LibraryImport("user32", EntryPoint = "DispatchMessageW")]
    internal static partial nint DispatchMessage(Message* msg);
    [LibraryImport("user32", EntryPoint = "IsDialogMessageW")]
    internal static partial int IsDialogMessage(nint dialog, Message* msg);
    [LibraryImport("user32")]
    internal static partial void PostQuitMessage(int code);
    [LibraryImport("user32")]
    internal static partial int DestroyWindow(nint window);
    [LibraryImport("user32")]
    internal static partial int ShowWindow(nint window, int command);
    [LibraryImport("user32")]
    internal static partial int UpdateWindow(nint window);
    [LibraryImport("user32")]
    internal static partial int SetForegroundWindow(nint window);
    [LibraryImport("user32")]
    internal static partial nint SetFocus(nint window);
    [LibraryImport("user32")]
    internal static partial short GetKeyState(int key);
    [LibraryImport("user32")]
    internal static partial int GetClientRect(nint window, Rect* rect);
    [LibraryImport("user32")]
    internal static partial int MoveWindow(nint window, int x, int y, int width, int height, int repaint);
    [LibraryImport("user32")]
    internal static partial uint GetDpiForWindow(nint window);
    [LibraryImport("user32")]
    internal static partial nint MonitorFromWindow(nint window, uint flags);
    [LibraryImport("user32", EntryPoint = "GetMonitorInfoW")]
    internal static partial int GetMonitorInfo(nint monitor, MonitorInfo* info);
    [LibraryImport("user32")]
    internal static partial int GetWindowRect(nint window, Rect* rect);
    [LibraryImport("user32")]
    internal static partial int InvalidateRect(nint window, nint rect, int erase);
    [LibraryImport("user32", EntryPoint = "PostMessageW")]
    internal static partial int Post(nint window, uint msg, nuint wParam, nint lParam);
    [LibraryImport("user32", EntryPoint = "SendMessageW")]
    internal static partial nint Send(nint window, uint msg, nuint wParam, nint lParam);
    [LibraryImport("user32", EntryPoint = "LoadIconW")]
    internal static partial nint LoadIcon(nint module, nint id);
    [LibraryImport("user32", EntryPoint = "LoadCursorW")]
    internal static partial nint LoadCursor(nint module, nint id);
    [LibraryImport("user32", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterWindowMessage(string name);
    [LibraryImport("user32", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SetText(nint window, string text);
    [LibraryImport("user32", EntryPoint = "GetWindowTextW")]
    internal static partial int GetText(nint window, char* text, int capacity);
    [LibraryImport("user32")]
    internal static partial nint CreatePopupMenu();
    [LibraryImport("user32", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int AppendMenu(nint menu, uint flags, nuint id, string label);
    [LibraryImport("user32")]
    internal static partial uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);
    [LibraryImport("user32")]
    internal static partial int GetCursorPos(Point* point);
    [LibraryImport("user32")]
    internal static partial int ScreenToClient(nint window, Point* point);
    [LibraryImport("user32", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint LoadImage(nint instance, string path, uint type, int width, int height, uint flags);
    [LibraryImport("user32")]
    internal static partial int DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);
    [LibraryImport("user32")]
    internal static partial int DestroyIcon(nint icon);
    [LibraryImport("user32")]
    internal static partial int DestroyMenu(nint menu);
    [LibraryImport("shell32", EntryPoint = "Shell_NotifyIconW")]
    internal static partial int ShellNotifyIcon(uint message, NotifyIcon* data);
    [LibraryImport("gdi32", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateFont(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outputPrecision,
        uint clipPrecision, uint quality, uint pitchAndFamily, string faceName);
    [LibraryImport("gdi32")]
    internal static partial int DeleteObject(nint handle);
    [LibraryImport("user32")]
    internal static partial nint BeginPaint(nint window, Paint* paint);
    [LibraryImport("user32")]
    internal static partial int EndPaint(nint window, Paint* paint);
    [LibraryImport("user32")]
    internal static partial int FillRect(nint dc, Rect* rect, nint brush);
    [LibraryImport("user32", EntryPoint = "DrawTextW")]
    internal static partial int DrawText(nint dc, char* text, int length, Rect* rect, uint flags);
    [LibraryImport("user32")]
    internal static partial nint GetFocus();
    [LibraryImport("user32")]
    internal static partial int GetScrollInfo(nint window, int bar, Scroll* info);
    [LibraryImport("user32")]
    internal static partial int SetScrollInfo(nint window, int bar, Scroll* info, int redraw);
    [LibraryImport("gdi32")]
    internal static partial nint CreateSolidBrush(uint color);
    [LibraryImport("gdi32")]
    internal static partial nint GetStockObject(int index);
    [LibraryImport("gdi32")]
    internal static partial nint SelectObject(nint dc, nint value);
    [LibraryImport("gdi32")]
    internal static partial uint SetDCBrushColor(nint dc, uint color);
    [LibraryImport("gdi32")]
    internal static partial uint SetTextColor(nint dc, uint color);
    [LibraryImport("gdi32")]
    internal static partial uint SetBkColor(nint dc, uint color);
    [LibraryImport("gdi32")]
    internal static partial int SetBkMode(nint dc, int mode);
    [LibraryImport("dwmapi")]
    internal static partial int DwmSetWindowAttribute(nint window, uint attribute, int* value, uint size);
}
