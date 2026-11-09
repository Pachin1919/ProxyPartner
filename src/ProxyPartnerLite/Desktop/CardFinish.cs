using System.Runtime.InteropServices;
using static ProxyPartnerLite.Desktop.Win32;

namespace ProxyPartnerLite.Desktop;

internal static unsafe partial class CardFinish
{
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInput
    {
        public uint Version;
        public nint DebugCallback;
        public int SuppressThread, SuppressExternalCodecs;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizeRect { public int X, Y, Width, Height; }

    internal static nuint Start()
    {
        var input = new StartupInput { Version = 1, SuppressExternalCodecs = 1 };
        return Startup(out nuint token, &input, 0) == 0 ? token : 0;
    }

    internal static nint Begin(nuint token, nint dc)
    {
        if (token == 0 || CreateGraphics(dc, out nint graphics) != 0) return 0;
        SmoothingMode(graphics, 4); // antialias
        return graphics;
    }

    internal static bool Draw(nint graphics, Rect bounds, int radius, uint color, uint? focus, int stroke)
    {
        if (graphics == 0) return false;
        int left = bounds.Left + 1, top = bounds.Top + 1, right = bounds.Right - 1, bottom = bounds.Bottom - 1;
        int diameter = Math.Min(radius * 2, Math.Min(right - left, bottom - top));
        if (diameter < 2 || CreatePath(0, out nint path) != 0) return false;
        AddArc(path, left, top, diameter, diameter, 180, 90);
        AddArc(path, right - diameter, top, diameter, diameter, 270, 90);
        AddArc(path, right - diameter, bottom - diameter, diameter, diameter, 0, 90);
        AddArc(path, left, bottom - diameter, diameter, diameter, 90, 90);
        ClosePath(path);

        var rect = new SizeRect { X = left, Y = top, Width = right - left, Height = bottom - top };
        bool painted = false;
        if (CreateGradient(&rect, Highlight(color, 6), Argb(color), 1, 0, out nint brush) == 0)
        {
            painted = FillPath(graphics, brush, path) == 0;
            DeleteBrush(brush);
        }
        if (CreatePen(focus is uint selection ? Argb(selection) : Highlight(color, 18),
                focus.HasValue ? stroke : 1, 2, out nint pen) == 0)
        {
            DrawPath(graphics, pen, path);
            DeletePen(pen);
        }
        DeletePath(path);
        return painted;
    }

    private static uint Argb(uint color) => 0xff000000 | ((color & 0xff) << 16) | (color & 0xff00) | (color >> 16);

    private static uint Highlight(uint color, uint percent)
    {
        uint Mix(uint value) => (value * (100 - percent) + 255 * percent) / 100;
        return 0xff000000 | (Mix(color & 255) << 16) |
            (Mix((color >> 8) & 255) << 8) | Mix(color >> 16);
    }

    [LibraryImport("gdiplus", EntryPoint = "GdiplusStartup")]
    private static partial int Startup(out nuint token, StartupInput* input, nint output);
    [LibraryImport("gdiplus", EntryPoint = "GdiplusShutdown")]
    internal static partial void EndSession(nuint token);
    [LibraryImport("gdiplus", EntryPoint = "GdipCreateFromHDC")]
    private static partial int CreateGraphics(nint dc, out nint graphics);
    [LibraryImport("gdiplus", EntryPoint = "GdipDeleteGraphics")]
    internal static partial int End(nint graphics);
    [LibraryImport("gdiplus", EntryPoint = "GdipSetSmoothingMode")]
    private static partial int SmoothingMode(nint graphics, int mode);
    [LibraryImport("gdiplus", EntryPoint = "GdipCreatePath")]
    private static partial int CreatePath(int fill, out nint path);
    [LibraryImport("gdiplus", EntryPoint = "GdipAddPathArcI")]
    private static partial int AddArc(nint path, int x, int y, int w, int h, float start, float sweep);
    [LibraryImport("gdiplus", EntryPoint = "GdipClosePathFigure")]
    private static partial int ClosePath(nint path);
    [LibraryImport("gdiplus", EntryPoint = "GdipDeletePath")]
    private static partial int DeletePath(nint path);
    [LibraryImport("gdiplus", EntryPoint = "GdipCreateLineBrushFromRectI")]
    private static partial int CreateGradient(SizeRect* rect, uint top, uint bottom, int direction, int wrap, out nint brush);
    [LibraryImport("gdiplus", EntryPoint = "GdipFillPath")]
    private static partial int FillPath(nint graphics, nint brush, nint path);
    [LibraryImport("gdiplus", EntryPoint = "GdipDeleteBrush")]
    private static partial int DeleteBrush(nint brush);
    [LibraryImport("gdiplus", EntryPoint = "GdipCreatePen1")]
    private static partial int CreatePen(uint color, float width, int unit, out nint pen);
    [LibraryImport("gdiplus", EntryPoint = "GdipDrawPath")]
    private static partial int DrawPath(nint graphics, nint pen, nint path);
    [LibraryImport("gdiplus", EntryPoint = "GdipDeletePen")]
    private static partial int DeletePen(nint pen);
}
