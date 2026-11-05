using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;

namespace WidgetNotes;

public static class Desktop
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static void Startup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue("WidgetNotes", $"\"{Environment.ProcessPath}\" --startup"); else key.DeleteValue("WidgetNotes", false);
    }
    public static bool HasStartup() { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue("WidgetNotes") != null; }
    public static bool Dark => (int?)Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0;
    public static string Accent
    {
        get { uint v = unchecked((uint)(int)(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "ColorizationColor", unchecked((int)0xff0078d4)) ?? unchecked((int)0xff0078d4))); return $"#{(v >> 16) & 255:X2}{(v >> 8) & 255:X2}{v & 255:X2}"; }
    }
    public static void Shell(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    public static (uint Mods, uint Key) ParseShortcut(string text)
    {
        uint mods = 0x4000; Key key = Key.None;
        foreach (string s in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (s.ToUpperInvariant())
            {
                case "CTRL": mods |= 2; break; case "ALT": mods |= 1; break; case "SHIFT": mods |= 4; break; case "WIN": mods |= 8; break;
                default: if (!Enum.TryParse(s, true, out key)) throw new ArgumentException("Use a shortcut such as Ctrl+Alt+N or Ctrl+Shift+F8."); break;
            }
        }
        if ((mods & 15) == 0 || key == Key.None || key is Key.LeftCtrl or Key.LeftAlt or Key.LeftShift or Key.LWin) throw new ArgumentException("Choose a letter or function key with Ctrl, Alt, Shift, or Win.");
        return (mods, (uint)KeyInterop.VirtualKeyFromKey(key));
    }
}

public sealed class App : System.Windows.Application
{
    Mutex? mutex;
    EventWaitHandle? wake;
    System.Windows.Forms.NotifyIcon? tray;
    public static readonly Stopwatch LaunchTime = Stopwatch.StartNew();
    public static string[] Arguments = [];
    public static string SettingsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WidgetNotes", "settings.json");
    public static string? TestRoot => Arguments.FirstOrDefault(a => a.StartsWith("--test-root="))?[12..];
    public static string ActualSettingsFile => TestRoot is {} r ? Path.Combine(r, "settings.json") : SettingsFile;
    [STAThread]
    public static int Main(string[] args)
    {
        Arguments = args;
        if (args.Contains("--self-test")) return SelfTest.Run(args);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Run(); return 0;
    }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ev) =>
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(ActualSettingsFile)!); File.AppendAllText(Path.ChangeExtension(ActualSettingsFile, ".errors.log"), DateTimeOffset.Now.ToString("O") + "\n" + ev.Exception + "\n\n"); } catch { }
            MessageBox.Show(ev.Exception.Message, "WidgetNotes — unexpected error"); ev.Handled = true;
        };
        string suffix = TestRoot == null ? Environment.UserName : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(TestRoot))))[..16];
        string instance = typeof(App).Assembly.GetName().Version?.ToString() ?? "1.0.0.0";
        mutex = new Mutex(true, "Local\\WidgetNotes-" + instance + "-" + suffix, out bool first);
        wake = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\WidgetNotesWake-" + instance + "-" + suffix);
        if (!first)
        {
            // File arguments are handed to the owning process through a per-user inbox.
            string? archive = e.Args.FirstOrDefault(a => a.EndsWith(".wnote", StringComparison.OrdinalIgnoreCase));
            if (archive != null) { Directory.CreateDirectory(Path.GetDirectoryName(ActualSettingsFile)!); File.WriteAllText(ActualSettingsFile + ".open", Path.GetFullPath(archive)); }
            wake.Set(); Shutdown(); return;
        }
        try
        {
            Preferences pref;
            try { pref = Json.Read<Preferences>(File.ReadAllText(ActualSettingsFile)); } catch { pref = new(); }
            if (TestRoot != null) pref.Root = Path.Combine(TestRoot, "notebooks");
            pref.Startup = Desktop.HasStartup();
            Directory.CreateDirectory(pref.Root);
            NotebookStore store;
            string? archive = e.Args.FirstOrDefault(a => a.EndsWith(".wnote", StringComparison.OrdinalIgnoreCase));
            string? warning = null;
            if (archive != null) store = await NotebookStore.Import(archive, pref.Root);
            else if (pref.NotebookFolder != null && Directory.Exists(pref.NotebookFolder))
            {
                try { store = await NotebookStore.Open(pref.NotebookFolder); }
                catch (Exception ex)
                {
                    warning = "Your previous notebook could not be opened. It has been left in place. A recovery notebook is open.\n\n" + ex.Message;
                    store = await NewStore(pref.Root, "Recovery notebook");
                }
            }
            else store = await NewStore(pref.Root, "My notebook");
            var window = new MainWindow(store, pref); MainWindow = window;
            tray = new System.Windows.Forms.NotifyIcon { Icon = AppArtwork.TrayIcon, Text = "WidgetNotes", Visible = true };
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Show / hide", null, (_, _) => { if (MainWindow is MainWindow active) active.ToggleVisibility(); });
            menu.Items.Add("Find page…", null, (_, _) => { if (MainWindow is MainWindow active) active.ShowPageSearch(); });
            menu.Items.Add("Exit", null, async (_, _) => { if (MainWindow is MainWindow active) await active.ExitApp(); }); tray.ContextMenuStrip = menu;
            tray.DoubleClick += (_, _) => { if (MainWindow is MainWindow active) active.ToggleVisibility(); };
            _ = Task.Run(() => { while (wake.WaitOne()) Dispatcher.BeginInvoke(async () =>
            {
                if (MainWindow is not MainWindow active) return;
                active.Reveal();
                string inbox = ActualSettingsFile + ".open";
                if (File.Exists(inbox)) { string path = await File.ReadAllTextAsync(inbox); File.Delete(inbox); await active.OpenArchive(path); }
            }); });
            MaterialDiagnostics.Start(window);
            window.Show();
            if (warning != null) MessageBox.Show(window, warning, "Notebook recovery");
            else if (store.Recovered) MessageBox.Show(window, "Recovered this notebook from its last good backup. The damaged file was preserved beside it.", "Notebook recovered");
        }
        catch (Exception ex) { MessageBox.Show("WidgetNotes could not start. Your existing notebooks have not been removed.\n\n" + ex.Message, "WidgetNotes"); Shutdown(1); }
    }
    public static async Task<NotebookStore> NewStore(string root, string title)
    {
        var book = new Notebook { Title = title }; var store = new NotebookStore(Path.Combine(root, book.Id), book); await store.Save(Json.Write(book)); return store;
    }
    protected override void OnExit(ExitEventArgs e) { tray?.Dispose(); mutex?.Dispose(); base.OnExit(e); }
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (MainWindow is MainWindow window) { if (!window.SaveForSessionEnd()) e.Cancel = true; else window.PrepareSessionExit(); }
        base.OnSessionEnding(e);
    }
}

