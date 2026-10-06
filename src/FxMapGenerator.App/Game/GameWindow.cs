using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using FxMapGenerator.Core.Capture;

namespace FxMapGenerator.App.Game;

/// <summary>
/// The FiveM game window (of the process <c>FiveM_…GTAProcess</c>): the size of its drawing area, and that area taken
/// with PrintWindow, which works while other windows cover it and does not take the focus.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class GameWindow : IGameWindow
{
    public static readonly GameWindow Instance = new();

    const uint PwClientOnly = 1, PwRenderFullContent = 2;

    [GeneratedRegex(@"^FiveM_.*GTAProcess$", RegexOptions.IgnoreCase)]
    private static partial Regex GameProcess();

    /// <summary>The main window of the game process; zero when the game is not running.</summary>
    public static IntPtr Find()
    {
        foreach (var p in Process.GetProcesses())
            using (p)
            {
                try
                {
                    if (GameProcess().IsMatch(p.ProcessName) && p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
                }
                catch (InvalidOperationException) { } // the process ended meanwhile
            }
        return IntPtr.Zero;
    }

    public (int Width, int Height)? ClientSize()
    {
        var hwnd = Find();
        return hwnd == IntPtr.Zero ? null : ClientSizeOf(hwnd);
    }

    public Frame? Capture()
    {
        var hwnd = Find();
        return hwnd == IntPtr.Zero ? null : CaptureWindow(hwnd);
    }

    public static (int Width, int Height)? ClientSizeOf(IntPtr hwnd) => GetClientRect(hwnd, out var r) ? (r.Right - r.Left, r.Bottom - r.Top) : null;

    /// <summary>The drawing area of a window as RGBA; null when it cannot be taken. The full content is asked for, as DirectX windows need.</summary>
    public static Frame? CaptureWindow(IntPtr hwnd)
    {
        if (ClientSizeOf(hwnd) is not { } size || size.Width <= 0 || size.Height <= 0) return null;
        var (w, h) = size;
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var header = new BitmapInfoHeader { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = w, Height = -h, Planes = 1, BitCount = 32 }; // top-down BGRA
        var bitmap = CreateDIBSection(dc, ref header, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            if (bitmap == IntPtr.Zero) return null;
            var old = SelectObject(dc, bitmap);
            bool ok = PrintWindow(hwnd, dc, PwClientOnly | PwRenderFullContent);
            GdiFlush();
            SelectObject(dc, old);
            if (!ok) return null;
            var rgba = new byte[w * h * 4];
            Marshal.Copy(bits, rgba, 0, rgba.Length);
            for (int i = 0; i < rgba.Length; i += 4)
            {
                (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
                rgba[i + 3] = 255;
            }
            return new Frame(w, h, rgba);
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
}
