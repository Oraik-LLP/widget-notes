using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace WidgetNotes;

// Adapted from the MIT licensed WPF-Liquid-Glass-Effect project. Captures only
// the window rectangle needed by the glass shader, rather than retaining a
// screenshot of every monitor.
internal static class ScreenCaptureHelper
{
    const int SrcCopy = 0x00CC0020;
    const int CaptureLayeredWindows = 0x40000000;
    const int SwHide = 0;
    const int SwShowNoActivate = 4;

    public static BitmapSource? CaptureRegion(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        IntPtr screenDc = IntPtr.Zero, memoryDc = IntPtr.Zero, bitmap = IntPtr.Zero, previousBitmap = IntPtr.Zero;
        try
        {
            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return null;
            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero) return null;
            bitmap = CreateCompatibleBitmap(screenDc, width, height);
            if (bitmap == IntPtr.Zero) return null;
            previousBitmap = SelectObject(memoryDc, bitmap);
            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, x, y, SrcCopy | CaptureLayeredWindows)) return null;

            var result = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(width, height));
            result.Freeze();
            return result;
        }
        finally
        {
            if (previousBitmap != IntPtr.Zero && memoryDc != IntPtr.Zero) _ = SelectObject(memoryDc, previousBitmap);
            if (bitmap != IntPtr.Zero) _ = DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) _ = DeleteDC(memoryDc);
            if (screenDc != IntPtr.Zero) _ = ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public static BitmapSource? CaptureWindowRegion(IntPtr window, int x, int y, int width, int height)
    {
        bool wasForeground = GetForegroundWindow() == window;
        try
        {
            _ = ShowWindow(window, SwHide);
            _ = DwmFlush();
            return CaptureRegion(x, y, width, height);
        }
        finally
        {
            _ = ShowWindow(window, SwShowNoActivate);
            if (wasForeground) _ = SetForegroundWindow(window);
        }
    }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("dwmapi.dll")] static extern int DwmFlush();
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dest, int xDest, int yDest, int width, int height, IntPtr source, int xSrc, int ySrc, int rop);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
}
