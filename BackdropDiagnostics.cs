using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace WidgetNotes;

// A display-level regression check. Screen pixels are read only in this explicit
// isolated diagnostic; the application renderer never captures the desktop.
internal static class BackdropDiagnostics
{
    public static async Task Run(MainWindow main, FrameworkElement root, string folder)
    {
        var report = new List<string>();
        Window? behind = null;
        try
        {
            main.Expand(true); main.Width = 900; main.Height = 600; main.Left = 80; main.Top = 70;
            behind = new Window { Title = "WidgetNotes backdrop test surface", WindowStyle = WindowStyle.None,
                ShowInTaskbar = false, ShowActivated = false, Left = 50, Top = 40, Width = 960, Height = 660, Background = Brushes.Red };
            behind.Show(); main.Owner = behind; main.Topmost = true;
            await main.Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
            bool defaultPassed = false, measured = false;
            foreach (int mode in new[] { 0, 1, 2 })
            {
                bool enabled = NativeGlass.ApplyMode(main, true, mode);
                behind.Background = Brushes.Red; await Task.Delay(350); _ = DwmFlush();
                Point point = root.PointToScreen(new Point(root.ActualWidth - 110, root.ActualHeight / 2));
                if (GetAncestor(WindowFromPoint(new ScreenPoint { X = (int)point.X, Y = (int)point.Y }), 2) != new WindowInteropHelper(main).Handle)
                {
                    report.Add($"Mode={mode}; NOT MEASURED: diagnostic window is covered by another application");
                    continue;
                }
                uint redFrame = Pixel((int)point.X, (int)point.Y);
                SaveFrame(main, Path.Combine(folder, $"backdrop-mode{mode}-red.png"));
                behind.Background = Brushes.Blue; await Task.Delay(350); _ = DwmFlush();
                uint blueFrame = Pixel((int)point.X, (int)point.Y);
                SaveFrame(main, Path.Combine(folder, $"backdrop-mode{mode}-blue.png"));
                int difference = Math.Abs((int)(redFrame & 255) - (int)(blueFrame & 255)) + Math.Abs((int)((redFrame >> 16) & 255) - (int)((blueFrame >> 16) & 255));
                bool passed = enabled && redFrame != 0xffffffff && blueFrame != 0xffffffff && difference > 40;
                report.Add($"Mode={mode}; Enabled={enabled}; RedFrame={redFrame:X8}; BlueFrame={blueFrame:X8}; Difference={difference}; Live={passed}");
                if (mode == 2) { defaultPassed = passed; measured = true; }
            }
            NativeGlass.Apply(main, true);
            report.Add(defaultPassed ? "LIVE BACKDROP TEST PASSED" : measured ? "LIVE BACKDROP TEST FAILED" : "LIVE BACKDROP CHECK NOT MEASURED: diagnostic window was covered");
        }
        catch (Exception ex) { report.Add("FAIL " + ex); }
        finally { main.Owner = null; behind?.Close(); }
        await File.WriteAllLinesAsync(Path.Combine(folder, "backdrop-test.txt"), report);
    }

    static uint Pixel(int x, int y)
    {
        var dc = GetDC(IntPtr.Zero);
        try { return GetPixel(dc, x, y); } finally { _ = ReleaseDC(IntPtr.Zero, dc); }
    }

    static void SaveFrame(Window window, string path)
    {
        Point origin = window.PointToScreen(new Point(0, 0));
        Point end = window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight));
        using var bitmap = new System.Drawing.Bitmap((int)(end.X - origin.X), (int)(end.Y - origin.Y));
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.CopyFromScreen((int)origin.X, (int)origin.Y, 0, 0, bitmap.Size);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    [DllImport("dwmapi.dll")] static extern int DwmFlush();
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);
    [StructLayout(LayoutKind.Sequential)] struct ScreenPoint { public int X, Y; }
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(ScreenPoint point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
