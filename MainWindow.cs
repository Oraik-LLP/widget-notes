using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WidgetNotes;

public sealed class MainWindow : Window
{
    NotebookStore store;
    readonly Preferences pref;
    NotePage page;
    readonly DispatcherTimer autosave = new() { Interval = TimeSpan.FromMilliseconds(550) };
    readonly Stack<string> undo = new();
    readonly TextBlock status = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock counter = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    readonly Grid root = new();
    readonly Grid windowRoot = new();
    readonly Border shellFrame = new() { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1) };
    readonly Grid content = new();
    readonly StackPanel sidebar = new();
    readonly Canvas canvas = new() { Background = Brushes.Transparent, Width = 2000, Height = 1600, ClipToBounds = true };
    readonly StackPanel widget = new();
    readonly ScaleTransform zoom = new(1, 1);
    readonly TextBox title = new() { FontSize = 26, FontWeight = FontWeights.SemiBold, BorderThickness = new Thickness(0), Padding = new Thickness(0, 6, 0, 6), Background = Brushes.Transparent };
    readonly TextBox search = new() { Margin = new Thickness(8), MinHeight = 30, ToolTip = "Search all page titles and text" };
    readonly TextBlock notebookTitle = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    readonly Border headerGlass;
    readonly Border sidebarGlass;
    readonly Border footerGlass;
    readonly Button expandButton;
    readonly Button pinButton;
    readonly Button maximizeButton;
    bool changingMode;
    readonly SemaphoreSlim flushGate = new(1, 1);
    bool expanded, rendering, dirty, exiting, busy;
    long revision;
    Brush surface = Brushes.White, foreground = Brushes.Black, muted = Brushes.Gray, accent = Brushes.Blue, border = Brushes.LightGray;
    Brush glassPanel = Brushes.White, glassRaised = Brushes.White, inputSurface = Brushes.White;
    IntPtr handle;
    bool shortcutRegistered;
    string? appliedTheme;
    bool nativeBackdrop;
    ContextMenu? lastMenu;
    public MainWindow(NotebookStore initial, Preferences settings)
    {
        store = initial; pref = settings; pref.Pinned = false;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/WidgetNotes;component/Themes/Controls.xaml") });
        page = store.Book.Pages.FirstOrDefault(p => p.Id == pref.LastPage) ?? store.Book.Pages[0];
        Title = "WidgetNotes"; Icon = AppArtwork.WindowIcon; FontFamily = new FontFamily("Segoe UI Variable, Segoe UI"); FontSize = 13;
        MinWidth = 360; MinHeight = 400; Width = Math.Clamp(pref.Width, 340, 900); Height = Math.Clamp(pref.Height, 360, 1000);
        Left = double.IsFinite(pref.Left) ? pref.Left : 60; Top = double.IsFinite(pref.Top) ? pref.Top : 60;
        ClampPosition(); Topmost = false; ShowInTaskbar = true; ShowActivated = true; NativeGlass.Configure(this);
        Content = windowRoot; shellFrame.Child = root; windowRoot.Children.Add(shellFrame); root.Margin = new Thickness(3);
        AllowDrop = true; root.SnapsToDevicePixels = true; root.UseLayoutRounding = true;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new DockPanel { LastChildFill = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        pinButton = Btn("Lock", () => { pref.LockWidget = !pref.LockWidget; pinButton!.Content = pref.LockWidget ? "Unlock" : "Lock"; ResizeMode = pref.LockWidget && !expanded ? ResizeMode.NoResize : ResizeMode.CanResize; SavePreferences(); }, "Lock or unlock the compact window position");
        pinButton.Content = pref.LockWidget ? "Unlock" : "Lock";
        if (pref.LockWidget) ResizeMode = ResizeMode.NoResize;
        expandButton = Btn("Expand ↗", () => Expand(!expanded), "Expand notebook / return to widget");
        actions.Children.Add(expandButton); actions.Children.Add(Btn("⋯", ShowMenu, "Notebook menu and settings"));
        actions.Children.Add(ChromeButton("−", () => WindowState = WindowState.Minimized, "Minimize"));
        maximizeButton = ChromeButton("□", ToggleMaximize, "Maximize or restore"); actions.Children.Add(maximizeButton);
        actions.Children.Add(ChromeButton("×", async () => await ExitApp(), "Close WidgetNotes"));
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(actions, true);
        DockPanel.SetDock(actions, Dock.Right); header.Children.Add(actions);
        var brand = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new System.Windows.Controls.Image { Source = AppArtwork.WindowIcon, Width = 25, Height = 25, Margin = new Thickness(2, 0, 9, 0), Stretch = Stretch.Uniform });
        brand.Children.Add(notebookTitle);
        var dragArea = new Grid(); dragArea.Children.Add(brand);
        header.Children.Add(dragArea);
        headerGlass = new Border { Margin = new Thickness(14, 7, 10, 8), Padding = new Thickness(2), CornerRadius = new CornerRadius(14), Child = header };
        root.Children.Add(headerGlass);
        Grid.SetRow(content, 1); root.Children.Add(content);
        content.ColumnDefinitions.Add(new() { Width = new GridLength(0) }); content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        sidebarGlass = new Border { CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1), Margin = new Thickness(11, 0, 5, 0), Padding = new Thickness(8), Child = sidebar };
        Grid.SetColumn(sidebarGlass, 0); content.Children.Add(sidebarGlass);
        var pageArea = new Grid { Margin = new Thickness(11, 0, 11, 0) }; Grid.SetColumn(pageArea, 1); content.Children.Add(pageArea);
        pageArea.RowDefinitions.Add(new() { Height = GridLength.Auto }); pageArea.RowDefinitions.Add(new() { Height = GridLength.Auto }); pageArea.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        title.TextChanged += (_, _) => { if (rendering) return; page.Title = title.Text; Changed(); UpdatePageLabels(); };
        title.GotKeyboardFocus += (_, _) => Remember(); pageArea.Children.Add(title);
        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        tools.Children.Add(Btn("+ List", () => AddBlock("checklist"), "Add checklist block"));
        tools.Children.Add(Btn("+ Note", () => AddBlock("text"), "Add text block"));
        tools.Children.Add(Btn("+ Image", async () => await PickMedia(true), "Import image"));
        var attach = Btn("Attach", async () => await PickMedia(false), "Attach any file"); tools.Children.Add(attach);
        tools.Children.Add(Btn("Import Markdown", async () => await PickMarkdown(), "Import AI output or Markdown files"));
        Grid.SetRow(tools, 1); pageArea.Children.Add(tools);
        var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = widget };
        scroller.Name = "WidgetScroll"; Grid.SetRow(scroller, 2); pageArea.Children.Add(scroller);
        canvas.LayoutTransform = zoom;
        var canvasScroll = new ScrollViewer { Content = canvas, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed, Name = "CanvasScroll" };
        Grid.SetRow(canvasScroll, 2); pageArea.Children.Add(canvasScroll);
        var footer = new Grid();
        footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var nav = new StackPanel { Orientation = Orientation.Horizontal };
        nav.Children.Add(Btn("‹", () => Navigate(-1), "Previous page")); nav.Children.Add(counter); nav.Children.Add(Btn("›", () => Navigate(1), "Next page"));
        nav.Children.Add(Btn("+", ShowNewPageMenu, "New checklist or note page"));
        nav.MouseWheel += (_, e) => { Navigate(e.Delta < 0 ? 1 : -1); e.Handled = true; };
        footer.Children.Add(nav); Grid.SetColumn(status, 1); footer.Children.Add(status);
        var retry = Btn("↻", async () => await Flush(), "Save / retry failed save"); Grid.SetColumn(retry, 2); footer.Children.Add(retry);
        footerGlass = new Border { Margin = new Thickness(11, 7, 11, 10), Padding = new Thickness(8, 3, 8, 3), CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(1), Child = footer };
        Grid.SetRow(footerGlass, 2); root.Children.Add(footerGlass);
        autosave.Tick += async (_, _) => { autosave.Stop(); await Flush(); };
        search.TextChanged += (_, _) => RenderSidebar();
        AutomationProperties.SetName(search, "Search pages");
        AutomationProperties.SetName(title, "Page title");
        PreviewKeyDown += OnKeys;
        Drop += async (_, e) =>
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Handled = true;
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            string[] markdown = files.Where(IsMarkdownFile).ToArray();
            string[] attachments = files.Where(f => !IsMarkdownFile(f)).ToArray();
            if (markdown.Length > 0) await ImportMarkdownFiles(markdown);
            if (attachments.Length > 0) await ImportFiles(attachments);
        };
        DragOver += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; } };
        Closing += OnClosing;
        SourceInitialized += (_, _) => { handle = new WindowInteropHelper(this).Handle; HwndSource.FromHwnd(handle)?.AddHook(WndProc); ApplyTheme(); TryShortcut(pref.Shortcut, false); };
        Loaded += (_, _) => { if (App.TestRoot == null) Expand(!pref.CompactMode); nativeBackdrop = NativeGlass.Apply(this, IsDark); WriteRuntimeReport(); };
        Closed += (_, _) => autosave.Stop();
        Activated += (_, _) => ApplyTheme();
        LocationChanged += (_, _) => { if (IsVisible && !changingMode) CaptureWindowBounds(); };
        SizeChanged += (_, _) => { if (IsVisible && !changingMode) CaptureWindowBounds(); };
        StateChanged += (_, _) => { maximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□"; };
        ContentRendered += async (_, _) => { if (App.Arguments.Contains("--backdrop-test") && App.TestRoot != null) await LiveBackdropTest(); else if (App.Arguments.Contains("--desktop-test") || App.Arguments.Contains("--application-test")) await ApplicationTest(); else if (App.Arguments.Contains("--ui-test") || App.Arguments.Contains("--benchmark")) await RunUiVerification(); };
        ApplyTheme(); Render(); SavePreferences(); status.Text = "Saved locally";

        void SyncMode() { scroller.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible; canvasScroll.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed; attach.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed; }
        ModeChanged += SyncMode; SyncMode();
    }
    event Action? ModeChanged;
    Button Btn(string label, Action action, string? help = null)
    {
        var b = new Button { Content = label, Margin = new Thickness(3), Padding = new Thickness(9, 6, 9, 6), MinHeight = 32, ToolTip = help ?? label, Cursor = Cursors.Hand };
        if (Resources[typeof(Button)] is Style style) b.Style = style;
        AutomationProperties.SetName(b, help ?? label); b.Click += (_, _) => action(); return b;
    }
    bool IsDark => pref.Theme == "Dark" || pref.Theme == "System" && Desktop.Dark;
    Button ChromeButton(string label, Action action, string help)
    {
        var button = Btn(label, action, help); button.Style = (Style)Resources["WindowButton"]; return button;
    }
    void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void ApplyTheme()
    {
        bool dark = IsDark;
        string key = $"{pref.Theme}:{dark}:{pref.Accent}:{Desktop.Accent}:{SystemParameters.HighContrast}";
        if (appliedTheme == key && NativeGlass.AcceptedAccentState(this) >= 0) return;
        nativeBackdrop = NativeGlass.Apply(this, dark);
        if (appliedTheme == key) return;
        appliedTheme = key;
        Brush B(string value) { var result = (Brush)new BrushConverter().ConvertFromString(value)!; result.Freeze(); return result; }
        foreground = B(dark ? "#F0F5FC" : "#17263D"); muted = B(dark ? "#B5C3D6" : "#607188");
        border = B(dark ? "#24FFFFFF" : "#220D294A");
        glassPanel = B(dark ? "#821A293F" : "#B8FFFFFF");
        glassRaised = B(dark ? "#70334A65" : "#98FFFFFF");
        inputSurface = B(dark ? "#8A0D1C30" : "#96E8EFF8");
        surface = B(dark ? "#36101F34" : "#35FFFFFF");
        accent = B(pref.Accent == "Orange" ? dark ? "#FFBA7A" : "#C75C22" : pref.Accent == "Windows" ? Desktop.Accent : dark ? "#70CBFF" : "#1671D9");
        Resources["TextBrush"] = foreground; Resources["MutedBrush"] = muted;
        Resources["LineBrush"] = border; Resources["PanelBrush"] = glassPanel;
        Resources["ControlBrush"] = glassRaised; Resources["InputBrush"] = inputSurface;
        Resources["AccentBrush"] = accent; Resources["HoverBrush"] = B(dark ? "#B449617D" : "#D6E0EDFC");
        Resources["MenuBrush"] = B(dark ? "#FB233047" : "#FCF4F7FC");
        Resources["ScrollBrush"] = B(dark ? "#648EA3BD" : "#60768DA5");
        windowRoot.Background = surface; root.Background = Brushes.Transparent; shellFrame.BorderBrush = border;
        Foreground = foreground; headerGlass.Background = Brushes.Transparent;
        sidebarGlass.Background = glassPanel; sidebarGlass.BorderBrush = border;
        footerGlass.Background = Brushes.Transparent; footerGlass.BorderBrush = Brushes.Transparent;
        status.Foreground = muted; counter.Foreground = muted; notebookTitle.Foreground = foreground;
        title.Foreground = foreground; search.Background = inputSurface; search.Foreground = foreground;
        if (IsLoaded) Render();
    }
    void Render()
    {
        rendering = true; title.Text = page.Title; notebookTitle.Text = "WidgetNotes  ·  " + store.Book.Title; notebookTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        widget.Children.Clear(); canvas.Children.Clear();
        if (expanded)
        {
            foreach (var b in page.Blocks) canvas.Children.Add(CanvasCard(b));
            canvas.Width = Math.Max(2000, page.Blocks.Select(b => b.X + b.Width + 100).DefaultIfEmpty(0).Max()); canvas.Height = Math.Max(1600, page.Blocks.Select(b => b.Y + b.Height + 100).DefaultIfEmpty(0).Max());
        }
        else
        {
            foreach (var b in Editing.ReadingOrder(page).Where(b => b.Kind is "text" or "checklist"))
            {
                var section = new StackPanel();
                var line = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
                var opts = Btn("···", () => BlockMenu(b, false), "Block options");
                opts.MinWidth = 32; opts.MinHeight = 28; opts.Padding = new Thickness(5, 2, 5, 2); opts.Background = Brushes.Transparent;
                DockPanel.SetDock(opts, Dock.Right); line.Children.Add(opts);
                string label = b.Kind == "checklist" ? "CHECKLIST" : "NOTE";
                string count = b.Kind == "checklist" ? $"{b.Items.Count(t => t.Done)} of {b.Items.Count} done" : "";
                var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var badge = new Border { Background = inputSurface, CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 8, 0) };
                badge.Child = new TextBlock { Text = label, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = accent, VerticalAlignment = VerticalAlignment.Center };
                heading.Children.Add(badge);
                if (count.Length > 0) heading.Children.Add(new TextBlock { Text = count, FontSize = 10, Foreground = muted, VerticalAlignment = VerticalAlignment.Center });
                line.Children.Add(heading);
                section.Children.Add(line);
                section.Children.Add(BlockContent(b));
                widget.Children.Add(new Border { Background = glassPanel, BorderBrush = border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(15), Padding = new Thickness(12, 10, 12, 11), Margin = new Thickness(0, 3, 0, 9), Child = section });
            }
            int attachments = page.Blocks.Count(b => b.Kind is "image" or "file");
            if (attachments > 0) widget.Children.Add(Btn($"{attachments} attachment{(attachments == 1 ? "" : "s")} · Open workspace ↗", () => Expand(true)));
            if (page.Blocks.Count == 0)
            {
                var empty = new StackPanel { Margin = new Thickness(5, 8, 5, 8) };
                empty.Children.Add(new TextBlock { Text = "A little room to think", FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = foreground, Margin = new Thickness(0, 0, 0, 5) });
                empty.Children.Add(new TextBlock { Text = "Add a list or a note to get started.", Foreground = muted, TextWrapping = TextWrapping.Wrap });
                widget.Children.Add(new Border { Background = glassPanel, BorderBrush = border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(15), Padding = new Thickness(15), Margin = new Thickness(0, 3, 0, 9), Child = empty });
            }
        }
        UpdatePageLabels(); RenderSidebar(); rendering = false;
    }
    void UpdatePageLabels() { counter.Text = $" {store.Book.Pages.IndexOf(page) + 1} / {store.Book.Pages.Count} "; }
    FrameworkElement BlockContent(Block b)
    {
        if (b.Kind == "text")
        {
            var text = new TextBox { Text = b.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, BorderThickness = new Thickness(0), Padding = new Thickness(4), Background = Brushes.Transparent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            AutomationProperties.SetName(text, "Note text"); text.GotKeyboardFocus += (_, _) => Remember(); text.TextChanged += (_, _) => { b.Text = text.Text; Changed(); }; return text;
        }
        if (b.Kind == "checklist")
        {
            var list = new StackPanel();
            foreach (var todo in b.Items) list.Children.Add(TodoRow(b, todo));
            var input = new TextBox { Margin = new Thickness(2, 9, 2, 2), Padding = new Thickness(9, 6, 9, 6), MinHeight = 36, ToolTip = "Type a task, then press Enter. Paste multiple lines to add several tasks." };
            AutomationProperties.SetName(input, "Add a task");
            var addRow = new DockPanel(); var add = Btn("+", Add, "Add task"); DockPanel.SetDock(add, Dock.Right); addRow.Children.Add(add); addRow.Children.Add(input); list.Children.Add(addRow);
            void Add()
            {
                var items = Editing.Lines(input.Text); if (items.Count == 0) return; Remember(); b.Items.AddRange(items); input.Clear(); Changed();
                int index = list.Children.Count - 1; foreach (var t in items) list.Children.Insert(index++, TodoRow(b, t));
            }
            input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Add(); e.Handled = true; } };
            DataObject.AddPastingHandler(input, (_, e) => { if (e.DataObject.GetData(DataFormats.UnicodeText) is string s && (s.Contains('\n') || s.Contains('\r'))) { e.CancelCommand(); input.SelectedText = s; Add(); } });
            return list;
        }
        if (b.Kind == "image")
        {
            var image = new System.Windows.Controls.Image { Stretch = Stretch.Uniform, ToolTip = b.FileName, Margin = new Thickness(4) };
            var hint = new TextBlock { Text = "Loading image…", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = muted, TextWrapping = TextWrapping.Wrap };
            var imagePanel = new Grid(); imagePanel.Children.Add(image); imagePanel.Children.Add(hint);
            _ = LoadImage(image, Path.Combine(store.Folder, b.Media!), Math.Clamp((int)(b.Width * VisualTreeHelper.GetDpi(this).DpiScaleX), 200, 1600), hint);
            return imagePanel;
        }
        return Btn("↗ " + b.FileName, () => OpenAttachment(b), "Open attachment with its default application");
    }
    async Task LoadImage(System.Windows.Controls.Image image, string file, int width, TextBlock? hint = null)
    {
        try
        {
            image.Source = await Task.Run(() =>
            {
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = width; bitmap.UriSource = new Uri(file); bitmap.EndInit(); bitmap.Freeze(); return bitmap;
            });
            if (hint != null) hint.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { image.ToolTip = "Image unavailable: " + ex.Message; if (hint != null) hint.Text = "Image unavailable\n" + Path.GetFileName(file); }
    }
    Grid TodoRow(Block block, Todo todo)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var check = new CheckBox { IsChecked = todo.Done, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0), Foreground = accent, ToolTip = "Complete task" };
        AutomationProperties.SetName(check, "Complete " + todo.Text);
        var text = new TextBox { Text = todo.Text, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Padding = new Thickness(2, 5, 2, 5), Background = Brushes.Transparent, Opacity = todo.Done ? .55 : 1 };
        AutomationProperties.SetName(text, "Task text"); Grid.SetColumn(text, 1); row.Children.Add(check); row.Children.Add(text);
        text.GotKeyboardFocus += (_, _) => Remember(); text.TextChanged += (_, _) => { todo.Text = text.Text; AutomationProperties.SetName(check, "Complete " + todo.Text); Changed(); };
        check.Click += (_, _) =>
        {
            bool next = check.IsChecked == true;
            if (next && Editing.RequiresConfirmation(block, pref) && !ConfirmPrompt("Complete task", "Mark this task as complete?\n\n" + todo.Text)) { check.IsChecked = todo.Done; return; }
            Remember(); todo.Done = next; text.Opacity = next ? .55 : 1; Changed();
        };
        text.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Remember(); var next = new Todo(); block.Items.Insert(block.Items.IndexOf(todo) + 1, next); Changed(); Render(); Dispatcher.BeginInvoke(() => FocusTask(next.Id)); e.Handled = true; } };
        DataObject.AddPastingHandler(text, (_, e) =>
        {
            if (e.DataObject.GetData(DataFormats.UnicodeText) is string s && (s.Contains('\n') || s.Contains('\r')))
            {
                e.CancelCommand(); Remember(); string combined = text.Text[..text.SelectionStart] + s + text.Text[(text.SelectionStart + text.SelectionLength)..]; var lines = Editing.Lines(combined);
                if (lines.Count > 0) { todo.Text = lines[0].Text; block.Items.InsertRange(block.Items.IndexOf(todo) + 1, lines.Skip(1)); } Changed(); Render();
            }
        });
        var remove = Btn("×", () => { if (!Confirm("Delete this task?")) return; Remember(); block.Items.Remove(todo); Changed(); Render(); }, "Delete task"); Grid.SetColumn(remove, 2); row.Children.Add(remove);
        text.Tag = todo.Id; return row;
    }
    void FocusTask(string id)
    {
        foreach (var t in Descendants<TextBox>(root)) if (t.Tag as string == id) { t.Focus(); break; }
    }
    public static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); if (child is T t) yield return t; foreach (var c in Descendants<T>(child)) yield return c; }
    }
    FrameworkElement CanvasCard(Block b)
    {
        var card = new Border { Width = b.Width, Height = b.Height, Background = glassPanel, BorderBrush = border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18), Padding = new Thickness(2) };
        Canvas.SetLeft(card, b.X); Canvas.SetTop(card, b.Y);
        var grid = new Grid(); grid.RowDefinitions.Add(new() { Height = new GridLength(32) }); grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); card.Child = grid;
        var header = new Grid { Background = Brushes.Transparent }; header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var grip = new Thumb { Cursor = Cursors.SizeAll, Opacity = .01, Background = Brushes.Transparent };
        header.Children.Add(new TextBlock { Text = "⠿  " + b.Kind.ToUpperInvariant(), FontSize = 10, Foreground = muted, Margin = new Thickness(10, 8, 0, 0), IsHitTestVisible = false }); header.Children.Add(grip);
        grip.DragStarted += (_, _) => Remember(); grip.DragDelta += (_, e) => { b.X = Math.Clamp(b.X + e.HorizontalChange, 0, 100000); b.Y = Math.Clamp(b.Y + e.VerticalChange, 0, 100000); Canvas.SetLeft(card, b.X); Canvas.SetTop(card, b.Y); Changed(); };
        grip.DragCompleted += (_, _) => GrowCanvas();
        var menu = Btn("⋯", () => BlockMenu(b, true), "Block options"); Grid.SetColumn(menu, 1); header.Children.Add(menu); grid.Children.Add(header);
        var body = new ScrollViewer { Content = BlockContent(b), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(8, 0, 8, 10) }; Grid.SetRow(body, 1); grid.Children.Add(body);
        var resize = new Thumb { Width = 15, Height = 15, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.SizeNWSE, Opacity = .5, ToolTip = "Resize block" }; Grid.SetRow(resize, 1); grid.Children.Add(resize);
        AutomationProperties.SetName(grip, "Move " + b.Kind + " block"); AutomationProperties.SetName(resize, "Resize " + b.Kind + " block");
        resize.DragStarted += (_, _) => Remember(); resize.DragDelta += (_, e) => { b.Width = Math.Clamp(b.Width + e.HorizontalChange, 180, 10000); b.Height = Math.Clamp(b.Height + e.VerticalChange, 120, 10000); card.Width = b.Width; card.Height = b.Height; Changed(); }; resize.DragCompleted += (_, _) => { GrowCanvas(); if (b.Kind == "image" && Descendants<System.Windows.Controls.Image>(card).FirstOrDefault() is {} image) _ = LoadImage(image, Path.Combine(store.Folder, b.Media!), Math.Clamp((int)(b.Width * VisualTreeHelper.GetDpi(this).DpiScaleX), 200, 1600)); };
        return card;
    }
    void GrowCanvas() { canvas.Width = Math.Max(canvas.Width, page.Blocks.Select(b => b.X + b.Width + 100).DefaultIfEmpty(2000).Max()); canvas.Height = Math.Max(canvas.Height, page.Blocks.Select(b => b.Y + b.Height + 100).DefaultIfEmpty(1600).Max()); }
    void BlockMenu(Block b, bool canvasMode)
    {
        var menu = CreateMenu();
        if (b.Kind == "checklist")
        {
            foreach (var (label, val) in new (string, bool?)[] { ("Completion: follow global setting", null), ("Completion: always confirm", true), ("Completion: no confirmation", false) })
            { var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = b.ConfirmCompletion == val }; item.Click += (_, _) => { Remember(); b.ConfirmCompletion = val; Changed(); }; menu.Items.Add(item); }
        }
        if (b.Media != null) { var open = new MenuItem { Header = "Open attachment" }; open.Click += (_, _) => OpenAttachment(b); menu.Items.Add(open); }
        if (canvasMode)
        { var position = new MenuItem { Header = "Position and size…" }; position.Click += (_, _) => EditGeometry(b); menu.Items.Add(position); }
        var del = new MenuItem { Header = "Delete block" }; del.Click += (_, _) => { if (!Confirm("Delete this block and its contents?")) return; Remember(); page.Blocks.Remove(b); Changed(); Render(); }; menu.Items.Add(del); menu.IsOpen = true;
    }
    void EditGeometry(Block b)
    {
        var fields = new[] { b.X, b.Y, b.Width, b.Height }.Select(n => new TextBox { Text = n.ToString("0", System.Globalization.CultureInfo.InvariantCulture), Margin = new Thickness(0, 0, 0, 8) }).ToArray();
        var panel = new StackPanel { Margin = new Thickness(18) }; string[] labels = ["Left", "Top", "Width", "Height"];
        for (int i = 0; i < fields.Length; i++) { panel.Children.Add(new TextBlock { Text = labels[i] }); panel.Children.Add(fields[i]); }
        var dialog = Dialog("Block position and size", panel, 320, 430);
        panel.Children.Add(Btn("Apply", () => { var n = new double[4]; for (int i = 0; i < 4; i++) if (!double.TryParse(fields[i].Text, out n[i]) || !double.IsFinite(n[i])) return; Remember(); b.X = Math.Clamp(n[0], 0, 100000); b.Y = Math.Clamp(n[1], 0, 100000); b.Width = Math.Clamp(n[2], 180, 10000); b.Height = Math.Clamp(n[3], 120, 10000); Changed(); Render(); dialog.Close(); })); dialog.ShowDialog();
    }
    void AddBlock(string kind)
    {
        Remember(); var b = new Block { Kind = kind }; PlaceBlock(page, b); page.Blocks.Add(b); Changed(); Render();
    }
    static void PlaceBlock(NotePage target, Block block)
    {
        // Find a nearby empty slot, including when existing cards have been moved or resized.
        for (int slot = 0; slot < 10000; slot++)
        {
            double x = 32 + slot % 2 * 380, y = 32 + slot / 2 * 320;
            var rect = new Rect(x - 12, y - 12, block.Width + 24, block.Height + 24);
            if (target.Blocks.All(b => !rect.IntersectsWith(new Rect(b.X, b.Y, b.Width, b.Height)))) { block.X = x; block.Y = y; return; }
        }
        block.X = 32; block.Y = target.Blocks.Select(b => b.Y + b.Height + 32).DefaultIfEmpty(32).Max();
    }
    void NewPage(string kind)
    {
        Remember(); page = new NotePage { Title = kind == "checklist" ? "New checklist" : "New note", Blocks = [new() { Kind = kind }] }; store.Book.Pages.Add(page); Changed(); Render(); title.Focus(); title.SelectAll();
    }
    void ShowNewPageMenu() { var m = CreateMenu(); Menu(m, "Checklist page", () => NewPage("checklist")); Menu(m, "Note page", () => NewPage("text")); m.IsOpen = true; }
    void Navigate(int delta)
    {
        int i = store.Book.Pages.IndexOf(page) + delta; if (i < 0 || i >= store.Book.Pages.Count) return;
        page = store.Book.Pages[i]; Render(); SavePreferences();
        if (!pref.ReducedMotion && SystemParameters.ClientAreaAnimation)
        {
            var transform = new TranslateTransform(0, delta > 0 ? 14 : -14); content.RenderTransform = transform;
            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }
    }
    public void Expand(bool value)
    {
        if (expanded == value) { Show(); Activate(); return; }
        CaptureWindowBounds(); changingMode = true; WindowState = WindowState.Normal; expanded = value;
        MinWidth = expanded ? 900 : 360; MinHeight = expanded ? 600 : 400;
        Width = expanded ? Math.Clamp(pref.WorkspaceWidth, 900, 1800) : Math.Clamp(pref.Width, 360, 900);
        Height = expanded ? Math.Clamp(pref.WorkspaceHeight, 600, 1200) : Math.Clamp(pref.Height, 400, 1000);
        Left = expanded ? pref.WorkspaceLeft : pref.Left; Top = expanded ? pref.WorkspaceTop : pref.Top; ClampPosition();
        ResizeMode = !expanded && pref.LockWidget ? ResizeMode.NoResize : ResizeMode.CanResize;
        ShowInTaskbar = true; Topmost = false; changingMode = false;
        content.ColumnDefinitions[0].Width = new GridLength(expanded ? 224 : 0);
        sidebarGlass.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        sidebar.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        expandButton.Content = expanded ? "Compact" : "Workspace"; pinButton.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        maximizeButton.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed; title.FontSize = expanded ? 28 : 24;
        pref.CompactMode = !expanded; ModeChanged?.Invoke(); Render(); nativeBackdrop = NativeGlass.Apply(this, IsDark);
        Show(); Activate(); SavePreferences(); WriteRuntimeReport();
    }
    void CaptureWindowBounds()
    {
        if (WindowState != WindowState.Normal || changingMode) return;
        if (expanded) { pref.WorkspaceLeft = Left; pref.WorkspaceTop = Top; pref.WorkspaceWidth = Width; pref.WorkspaceHeight = Height; }
        else { pref.Left = Left; pref.Top = Top; pref.Width = Width; pref.Height = Height; }
    }
    void WriteRuntimeReport()
    {
        try
        {
            var report = new { Version = typeof(App).Assembly.GetName().Version?.ToString(), Path = Environment.ProcessPath,
                Renderer = "Windows compositor acrylic", AccentState = NativeGlass.AcceptedAccentState(this), NativeBackdrop = nativeBackdrop,
                TopLevel = NativeGlass.IsTopLevel(this), Taskbar = ShowInTaskbar, Mode = expanded ? "Workspace" : "Compact" };
            File.WriteAllText(Path.ChangeExtension(App.ActualSettingsFile, ".runtime.json"), Json.Write(report));
        }
        catch { /* Runtime diagnostics must never prevent opening a notebook. */ }
    }
    void ClampPosition()
    {
        // Work in device-independent pixels; select the physical monitor then convert its work area.
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)(Left * scale), (int)(Top * scale))).WorkingArea;
        double l = screen.Left / scale, t = screen.Top / scale, w = screen.Width / scale, h = screen.Height / scale;
        Width = Math.Min(Width, w); Height = Math.Min(Height, h); Left = Math.Clamp(Left, l, Math.Max(l, l + w - Width)); Top = Math.Clamp(Top, t, Math.Max(t, t + h - Height));
    }
    void RenderSidebar()
    {
        if (!expanded) return;
        bool focus = search.IsKeyboardFocusWithin; int caret = search.CaretIndex;
        sidebar.Children.Clear(); sidebar.Children.Add(new TextBlock { Text = "SEARCH PAGES", FontSize = 10, Foreground = muted, Margin = new Thickness(8, 8, 8, 0) }); sidebar.Children.Add(search);
        var books = Btn("Notebooks ▾", ShowNotebookMenu, "Switch or create notebooks"); sidebar.Children.Add(books);
        var scroll = new ScrollViewer { MaxHeight = Math.Max(200, ActualHeight - 240), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; var items = new StackPanel(); scroll.Content = items; sidebar.Children.Add(scroll);
        string term = search.Text.Trim();
        foreach (var p in store.Book.Pages.Where(p => term.Length == 0 || (p.Title + " " + string.Join(" ", p.Blocks.Select(b => b.Text + " " + b.FileName + " " + string.Join(" ", b.Items.Select(t => t.Text))))).Contains(term, StringComparison.OrdinalIgnoreCase)))
        {
            var button = Btn((p == page ? "• " : "") + p.Title, () => { page = p; Render(); SavePreferences(); }, p.Title); button.HorizontalContentAlignment = HorizontalAlignment.Left; if (p == page) button.BorderBrush = accent; items.Children.Add(button);
        }
        var controls = new WrapPanel(); controls.Children.Add(Btn("↑", () => MovePage(-1), "Move page up")); controls.Children.Add(Btn("↓", () => MovePage(1), "Move page down")); controls.Children.Add(Btn("Delete", DeletePage, "Delete current page")); sidebar.Children.Add(controls);
        var z = new WrapPanel(); z.Children.Add(Btn("−", () => SetZoom(zoom.ScaleX - .1), "Zoom out")); z.Children.Add(Btn($"{zoom.ScaleX:P0}", () => SetZoom(1), "Reset zoom")); z.Children.Add(Btn("+", () => SetZoom(zoom.ScaleX + .1), "Zoom in")); sidebar.Children.Add(z);
        sidebar.Children.Add(Btn("Rename notebook", RenameNotebook));
        sidebar.Children.Add(Btn("Settings", Settings));
        if (focus) { search.Focus(); search.CaretIndex = caret; }
    }
    void SetZoom(double value) { zoom.ScaleX = zoom.ScaleY = Math.Clamp(value, .4, 2); RenderSidebar(); }
    void MovePage(int delta) { int i = store.Book.Pages.IndexOf(page), next = i + delta; if (next < 0 || next >= store.Book.Pages.Count) return; Remember(); store.Book.Pages.RemoveAt(i); store.Book.Pages.Insert(next, page); Changed(); Render(); }
    void DeletePage() { if (!Confirm("Delete this page and all of its blocks?")) return; Remember(); store.Book.Pages.Remove(page); if (store.Book.Pages.Count == 0) store.Book.Pages.Add(new()); page = store.Book.Pages[0]; Changed(); Render(); }
    void RenameNotebook() { string? value = Ask("Rename notebook", "Notebook name", store.Book.Title); if (!string.IsNullOrWhiteSpace(value)) { Remember(); store.Book.Title = value; Changed(); Render(); } }
    bool Confirm(string message) => !pref.ConfirmDelete || ConfirmPrompt("Confirm deletion", message);
    bool ConfirmPrompt(string heading, string message)
    {
        var body = new StackPanel { Margin = new Thickness(24, 10, 24, 24) };
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });
        var dialog = Dialog(heading, body, 440, 250);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(Btn("Cancel", () => dialog.DialogResult = false));
        buttons.Children.Add(Btn("Confirm", () => dialog.DialogResult = true)); body.Children.Add(buttons);
        return dialog.ShowDialog() == true;
    }
    void Remember() { if (rendering) return; if (undo.Count >= 40) { var keep = undo.Take(30).Reverse().ToArray(); undo.Clear(); foreach (var s in keep) undo.Push(s); } undo.Push(Json.Write(store.Book)); }
    void Undo() { if (!undo.TryPop(out var snapshot)) return; string id = page.Id; store.ReplaceBook(Json.Read<Notebook>(snapshot)); page = store.Book.Pages.FirstOrDefault(p => p.Id == id) ?? store.Book.Pages[0]; Changed(); Render(); }
    void Changed() { if (rendering) return; dirty = true; revision++; status.Text = "Unsaved · Undo"; autosave.Stop(); autosave.Start(); }
    public async Task<bool> Flush()
    {
        await flushGate.WaitAsync();
        try
        {
            while (dirty)
            {
                long saving = revision; string snapshot = Json.Write(store.Book); status.Text = "Saving…";
                try { await store.Save(snapshot); if (saving == revision) dirty = false; status.Text = dirty ? "Unsaved changes" : "Saved locally"; }
                catch (Exception ex) { status.Text = "Save failed · ↻ retry"; status.ToolTip = ex.Message; return false; }
            }
            SavePreferences(); return true;
        }
        finally { flushGate.Release(); }
    }
    void SavePreferences()
    {
        pref.NotebookFolder = store.Folder; pref.LastPage = page.Id; CaptureWindowBounds();
        try { Directory.CreateDirectory(Path.GetDirectoryName(App.ActualSettingsFile)!); NotebookStore.AtomicWrite(App.ActualSettingsFile, Json.Write(pref)); }
        catch (Exception ex) { status.Text = "Settings save failed"; status.ToolTip = ex.Message; }
    }
    public void ToggleVisibility()
    {
        if (IsVisible && (!expanded || IsActive)) { SavePreferences(); Hide(); }
        else { Reveal(); }
    }
    public void Reveal() { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; ClampPosition(); Activate(); }
    Window ModalOwner => this;
    async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (exiting) return;
        e.Cancel = true;
        if (await Flush()) { exiting = true; Close(); System.Windows.Application.Current.Shutdown(); }
        else MessageBox.Show(this, "Your changes could not be saved. Use Retry or export a portable recovery copy.", "Save failed");
    }
    public async Task ExitApp() { if (!await Flush()) { MessageBox.Show(ModalOwner, "The notebook could not be saved. Your edits remain open. Use Retry or export a recovery copy before exiting.", "Save required"); return; } exiting = true; if (shortcutRegistered) Desktop.UnregisterHotKey(handle, 1); Close(); System.Windows.Application.Current.Shutdown(); }
    public bool SaveForSessionEnd()
    {
        autosave.Stop();
        try { store.Save(Json.Write(store.Book)).GetAwaiter().GetResult(); dirty = false; SavePreferences(); return true; }
        catch (Exception ex) { status.Text = "Save failed · ↻ retry"; status.ToolTip = ex.Message; return false; }
    }
    public void MarkRecoveredChanges() => Changed();
    public void PrepareSessionExit() => exiting = true;
    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x312 && wParam.ToInt32() == 1) { ToggleVisibility(); handled = true; }
        if (msg == 0x231) NativeGlass.ApplyMode(this, IsDark, 1);
        if (msg == 0x232) NativeGlass.Apply(this, IsDark);
        if (msg == 0x7e) Dispatcher.BeginInvoke(ClampPosition);
        if (msg == 0x31e) Dispatcher.BeginInvoke(() => nativeBackdrop = NativeGlass.Apply(this, IsDark));
        if (msg is 0x31a or 0x1a) Dispatcher.BeginInvoke(ApplyTheme);
        return IntPtr.Zero;
    }
    bool TryShortcut(string value, bool warn)
    {
        string previous = pref.Shortcut;
        try
        {
            var parsed = Desktop.ParseShortcut(value); if (shortcutRegistered) Desktop.UnregisterHotKey(handle, 1);
            shortcutRegistered = Desktop.RegisterHotKey(handle, 1, parsed.Mods, parsed.Key);
            if (!shortcutRegistered)
            {
                var old = Desktop.ParseShortcut(previous); shortcutRegistered = Desktop.RegisterHotKey(handle, 1, old.Mods, old.Key);
                throw new InvalidOperationException("That shortcut is in use. Choose another combination. The tray icon is always available.");
            }
            pref.Shortcut = value; return true;
        }
        catch (Exception ex) { if (warn) MessageBox.Show(ModalOwner, ex.Message, "Shortcut unavailable"); else { status.Text = "Shortcut unavailable · use tray"; status.ToolTip = ex.Message; } return false; }
    }
    void OnKeys(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { _ = Flush(); e.Handled = true; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) { if (Keyboard.FocusedElement is TextBox t && t.CanUndo) return; Undo(); e.Handled = true; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N) { NewPage("checklist"); e.Handled = true; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F) { Expand(true); search.Focus(); e.Handled = true; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.PageDown or Key.PageUp) { Navigate(e.Key == Key.PageDown ? 1 : -1); e.Handled = true; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V && Clipboard.ContainsImage()) { _ = PasteImage(); e.Handled = true; }
        if (e.Key == Key.Escape && expanded) { Expand(false); e.Handled = true; }
    }
    async Task PickMedia(bool images)
    {
        var picker = new OpenFileDialog { Multiselect = true, Filter = images ? "Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff|All files|*.*" : "All files|*.*" }; if (picker.ShowDialog(ModalOwner) == true) await ImportFiles(picker.FileNames);
    }
    static bool IsMarkdownFile(string file) => Path.GetExtension(file).ToLowerInvariant() is ".md" or ".markdown" or ".mdown";
    async Task PickMarkdown()
    {
        var picker = new OpenFileDialog { Multiselect = true, Filter = "Markdown files|*.md;*.markdown;*.mdown|All files|*.*", Title = "Import AI output or Markdown notes" };
        if (picker.ShowDialog(ModalOwner) == true) await ImportMarkdownFiles(picker.FileNames);
    }
    async Task ImportMarkdownFiles(string[] files)
    {
        if (busy) return;
        busy = true;
        try
        {
            var notes = new List<MarkdownNote>();
            foreach (string file in files)
            {
                if (!File.Exists(file)) continue;
                var info = new FileInfo(file);
                if (info.Length > 20L * 1024 * 1024) throw new InvalidDataException($"{Path.GetFileName(file)} is larger than 20 MB.");
                status.Text = "Reading Markdown…";
                string content = await File.ReadAllTextAsync(file);
                notes.Add(await Task.Run(() => MarkdownImport.Parse(file, content)));
            }
            if (notes.Count == 0) return;
            if (!await Flush()) return;
            Remember();
            foreach (var note in notes)
            {
                var importedPage = new NotePage { Title = note.Title, Blocks = [] };
                foreach (var section in note.Sections)
                {
                    var block = new Block
                    {
                        Kind = section.Kind,
                        Text = section.Text,
                        Items = section.Tasks ?? [],
                        Width = section.Kind == "checklist" ? 360 : 480,
                        Height = section.Kind == "checklist" ? 280 : 360
                    };
                    PlaceBlock(importedPage, block);
                    importedPage.Blocks.Add(block);
                }
                store.Book.Pages.Add(importedPage);
            }
            page = store.Book.Pages[^1]; Changed(); Render(); SavePreferences();
            status.Text = $"Imported {notes.Count} Markdown file{(notes.Count == 1 ? "" : "s")}";
        }
        catch (Exception ex) { MessageBox.Show(ModalOwner, ex.Message, "Markdown import failed"); }
        finally { busy = false; }
    }
    async Task PasteImage()
    {
        string temp = Path.Combine(Path.GetTempPath(), "WidgetNotes-" + Guid.NewGuid().ToString("N") + ".png");
        try { var image = Clipboard.GetImage(); if (image == null) return; var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using (var f = File.Create(temp)) encoder.Save(f); await ImportFiles([temp], "Pasted image.png"); }
        catch (Exception ex) { MessageBox.Show(ModalOwner, ex.Message, "Could not paste image"); } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    async Task ImportFiles(string[] files, string? name = null)
    {
        if (busy) return; busy = true; var targetStore = store; var targetPage = page; Remember();
        try
        {
            foreach (string file in files)
            {
                if (!File.Exists(file)) continue; status.Text = "Importing…"; string relative = await targetStore.ImportMedia(file);
                bool image = new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff" }.Contains(Path.GetExtension(file).ToLowerInvariant());
                var block = new Block { Kind = image ? "image" : "file", FileName = name ?? Path.GetFileName(file), Media = relative, Height = image ? 280 : 120 }; PlaceBlock(targetPage, block); targetPage.Blocks.Add(block); Changed();
            }
            Render();
        }
        catch (Exception ex) { MessageBox.Show(ModalOwner, ex.Message, "Import failed"); }
        finally { busy = false; }
    }
    void OpenAttachment(Block b) { try { Desktop.Shell(Path.Combine(store.Folder, b.Media!)); } catch (Exception ex) { MessageBox.Show(ModalOwner, ex.Message, "Attachment could not be opened"); } }
    ContextMenu CreateMenu() => lastMenu = new ContextMenu { Resources = Resources, Style = (Style)Resources[typeof(ContextMenu)] };
    void Menu(ItemsControl menu, string label, Action action) { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item); }
    void ShowMenu()
    {
        var menu = CreateMenu(); Menu(menu, "Notebooks…", ShowNotebookMenu); Menu(menu, "Rename notebook…", RenameNotebook); Menu(menu, "New page…", ShowNewPageMenu);
        Menu(menu, "Move page earlier", () => MovePage(-1)); Menu(menu, "Move page later", () => MovePage(1)); Menu(menu, "Delete page…", DeletePage); menu.Items.Add(new Separator());
        Menu(menu, "Undo last change", Undo); Menu(menu, "Import AI / Markdown…", async () => await PickMarkdown()); Menu(menu, "Save / retry", async () => await Flush()); Menu(menu, "Export portable notebook…", ExportArchive); Menu(menu, "Open .wnote file…", async () => { var p = new OpenFileDialog { Filter = "WidgetNotes notebook|*.wnote" }; if (p.ShowDialog(ModalOwner) == true) await OpenArchive(p.FileName); });
        Menu(menu, "Export text…", ExportText); Menu(menu, "Print / Save as PDF…", Print); Menu(menu, "Open notebook folder", () => Desktop.Shell(store.Folder)); menu.Items.Add(new Separator());
        Menu(menu, "Settings…", Settings); Menu(menu, "Exit", async () => await ExitApp()); menu.IsOpen = true;
    }
    void ShowNotebookMenu()
    {
        var menu = CreateMenu(); Menu(menu, "+ New notebook…", async () => { if (busy || !await Flush()) return; string? name = Ask("New notebook", "Notebook name", "New notebook"); if (string.IsNullOrWhiteSpace(name)) return; try { await SwitchStore(await App.NewStore(pref.Root, name)); } catch (Exception ex) { MessageBox.Show(ModalOwner, ex.Message, "Could not create notebook"); } });
        Menu(menu, "Open notebook folder…", async () => { var dialog = new OpenFolderDialog { Title = "Select a folder containing notebook.json" }; if (dialog.ShowDialog(ModalOwner) == true) await OpenFolder(dialog.FolderName); });
        foreach (string folder in Directory.EnumerateDirectories(pref.Root))
        {
            string file = Path.Combine(folder, "notebook.json"); if (!File.Exists(file)) continue;
            string name; try { name = Json.Read<Notebook>(File.ReadAllText(file)).Title; } catch { name = Path.GetFileName(folder) + " (unreadable)"; }
            Menu(menu, name, async () => await OpenFolder(folder));
        }
        menu.IsOpen = true;
    }
    async Task OpenFolder(string folder) { if (busy || !await Flush()) return; try { await SwitchStore(await NotebookStore.Open(folder)); } catch (Exception ex) { MessageBox.Show(ModalOwner, ex.Message, "Could not open notebook"); } }
    public async Task OpenArchive(string file)
    {
        if (busy || !await Flush()) return; busy = true;
        try { var imported = await NotebookStore.Import(file, pref.Root); await SwitchStore(imported); }
        catch (Exception ex) { MessageBox.Show(ModalOwner, ex.Message, "Could not open notebook"); } finally { busy = false; }
    }
    async Task SwitchStore(NotebookStore next)
    {
        if (!await Flush()) return; store = next; page = store.Book.Pages[0]; undo.Clear(); Render(); SavePreferences();
        if (next.Recovered) MessageBox.Show(ModalOwner, "Recovered this notebook from its last good backup.", "Notebook recovered");
    }
    async void ExportArchive()
    {
        if (busy) return; var dialog = new SaveFileDialog { Filter = "WidgetNotes notebook|*.wnote", FileName = "Notebook.wnote" }; if (dialog.ShowDialog(ModalOwner) != true) return;
        busy = true; string snapshot = Json.Write(store.Book); long saving = revision;
        try
        {
            // Export is also a recovery route if the working directory is unavailable.
            try { await store.Export(dialog.FileName, snapshot); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                string recovery = Path.Combine(Path.GetTempPath(), "WidgetNotesRecovery-" + Guid.NewGuid().ToString("N")); var copy = new NotebookStore(recovery, Json.Read<Notebook>(snapshot));
                try
                {
                    foreach (var media in store.Book.Pages.SelectMany(p => p.Blocks).Select(b => b.Media).Where(m => m != null).Distinct()) { string target = Path.Combine(recovery, media!); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(Path.Combine(store.Folder, media!), target); }
                    await copy.Export(dialog.FileName, snapshot);
                }
                finally { if (Directory.Exists(recovery)) Directory.Delete(recovery, true); }
            }
            status.Text = "Portable copy exported"; if (revision != saving) status.Text += " · newer edits pending";
        }
        catch (Exception ex) { MessageBox.Show(ModalOwner, ex.Message, "Export failed"); } finally { busy = false; }
    }
    async void ExportText() { var d = new SaveFileDialog { Filter = "Text file|*.txt", FileName = "Notes.txt" }; if (d.ShowDialog(ModalOwner) == true) try { await File.WriteAllTextAsync(d.FileName, Editing.PlainText(store.Book)); } catch (Exception ex) { MessageBox.Show(ModalOwner, ex.Message, "Export failed"); } }
    async void Print()
    {
        try
        {
            var dialog = new PrintDialog(); if (dialog.ShowDialog() != true) return;
            var doc = new FlowDocument { FontFamily = FontFamily, FontSize = 12, PagePadding = new Thickness(40), ColumnWidth = double.PositiveInfinity };
            foreach (var p in store.Book.Pages)
            {
                doc.Blocks.Add(new Paragraph(new Run(p.Title)) { FontSize = 22, FontWeight = FontWeights.Bold });
                foreach (var b in Editing.ReadingOrder(p))
                {
                    if (b.Kind == "image") { var image = new System.Windows.Controls.Image { MaxHeight = 420, MaxWidth = 600, Stretch = Stretch.Uniform }; await LoadImage(image, Path.Combine(store.Folder, b.Media!), 1200); doc.Blocks.Add(new BlockUIContainer(image)); }
                    else doc.Blocks.Add(new Paragraph(new Run(b.Kind == "text" ? b.Text : b.Kind == "checklist" ? string.Join("\n", b.Items.Select(t => $"[{(t.Done ? "x" : " ")}] {t.Text}")) : "Attachment: " + b.FileName)));
                }
            }
            doc.PageWidth = dialog.PrintableAreaWidth; doc.PageHeight = dialog.PrintableAreaHeight; dialog.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, store.Book.Title);
        }
        catch (Exception ex) { MessageBox.Show(ModalOwner, ex.Message, "Print failed"); }
    }
    Window Dialog(string label, FrameworkElement body, double width = 430, double height = 600)
    {
        var dialog = new Window { Title = label, Owner = this, Width = width, Height = Math.Min(height, SystemParameters.WorkArea.Height - 40), MinWidth = 320, MinHeight = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Foreground = foreground, FontFamily = FontFamily, ShowInTaskbar = false, Resources = Resources };
        NativeGlass.Configure(dialog); dialog.ResizeMode = ResizeMode.NoResize;
        var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) }); layout.RowDefinitions.Add(new RowDefinition());
        var header = new DockPanel { Margin = new Thickness(20, 4, 10, 0) };
        var close = ChromeButton("×", () => dialog.Close(), "Close dialog"); DockPanel.SetDock(close, Dock.Right);
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(close, true); header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = label, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }); layout.Children.Add(header);
        Grid.SetRow(body, 1); layout.Children.Add(body);
        dialog.Content = new Border { Background = surface, BorderBrush = border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Child = layout };
        dialog.SourceInitialized += (_, _) => NativeGlass.Apply(dialog, IsDark);
        dialog.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { dialog.Close(); e.Handled = true; } };
        return dialog;
    }
    string? Ask(string label, string prompt, string initial)
    {
        var p = new StackPanel { Margin = new Thickness(18) }; p.Children.Add(new TextBlock { Text = prompt }); var input = new TextBox { Text = initial, Margin = new Thickness(0, 10, 0, 10), Padding = new Thickness(6) }; p.Children.Add(input); var d = Dialog(label, p, 380, 190); string? result = null;
        var ok = Btn("Save", () => { if (string.IsNullOrWhiteSpace(input.Text)) return; result = input.Text.Trim(); d.DialogResult = true; }); ok.IsDefault = true; p.Children.Add(ok); d.Loaded += (_, _) => { input.Focus(); input.SelectAll(); }; d.ShowDialog(); return result;
    }
    void Settings() => Settings(false);
    void Settings(bool diagnostic)
    {
        var panel = new StackPanel { Margin = new Thickness(22) };
        var settingsLayout = new Grid(); settingsLayout.RowDefinitions.Add(new RowDefinition()); settingsLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        settingsLayout.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var d = Dialog("WidgetNotes settings", settingsLayout, 520, 760);
        panel.Children.Add(new TextBlock { Text = "WIDGETNOTES  /  PREFERENCES", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = accent, Margin = new Thickness(0, 0, 0, 5) });
        panel.Children.Add(new TextBlock { Text = "Make it feel like yours", FontSize = 25, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(new TextBlock { Text = "A calmer space for lists, notes, and the little things to remember.", Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });

        StackPanel Group(string heading, string? detail = null)
        {
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = heading.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = accent, Margin = new Thickness(0, 0, 0, 10) });
            if (!string.IsNullOrWhiteSpace(detail)) body.Children.Add(new TextBlock { Text = detail, Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            panel.Children.Add(new Border { Background = glassPanel, BorderBrush = border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(15), Margin = new Thickness(0, 0, 0, 12), Child = body });
            return body;
        }
        ComboBox Choice(StackPanel group, string label, string[] choices, string current)
        {
            group.Children.Add(new TextBlock { Text = label, Foreground = foreground, Margin = new Thickness(0, 2, 0, 4) });
            var choice = new ComboBox { ItemsSource = choices, SelectedItem = current, Margin = new Thickness(0, 0, 0, 9), Padding = new Thickness(9, 5, 9, 5), MinHeight = 36, Background = inputSurface, Foreground = foreground, BorderBrush = border };
            group.Children.Add(choice); return choice;
        }
        CheckBox Check(StackPanel group, string label, bool current, string? detail = null)
        {
            var row = new StackPanel { Margin = new Thickness(0, 5, 0, 5) };
            var choice = new CheckBox { Content = label, IsChecked = current, Foreground = foreground, Margin = new Thickness(0, 1, 0, 1), FontWeight = FontWeights.Medium };
            row.Children.Add(choice);
            if (!string.IsNullOrWhiteSpace(detail)) row.Children.Add(new TextBlock { Text = detail, Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(23, 2, 0, 0), FontSize = 11 });
            group.Children.Add(row); return choice;
        }

        var look = Group("Appearance", "A soft glass surface that follows your Windows theme.");
        var theme = Choice(look, "Theme", ["System", "Light", "Dark"], pref.Theme);
        var color = Choice(look, "Accent color", ["Blue", "Orange", "Windows"], pref.Accent);
        var motion = Check(look, "Reduce motion", pref.ReducedMotion, "Use shorter, quieter page transitions.");

        var desktop = Group("Application & startup", "Keep your notebook close without getting in the way.");
        var startup = Check(desktop, "Open WidgetNotes when I sign in", Desktop.HasStartup(), "The installer enables this by default. You can turn it off here any time.");
        var pin = Check(desktop, "Lock compact window size", pref.LockWidget, "Keeps the optional compact window at its current size.");
        desktop.Children.Add(new TextBlock { Text = "Show / hide shortcut", Foreground = foreground, Margin = new Thickness(0, 8, 0, 4) });
        var hotkey = new TextBox { Text = pref.Shortcut, Margin = new Thickness(0, 0, 0, 5) }; desktop.Children.Add(hotkey);

        var confirmations = Group("Confirmations", "Choose when WidgetNotes should pause before a change.");
        var delete = Check(confirmations, "Confirm before deleting", pref.ConfirmDelete);
        var complete = Check(confirmations, "Confirm before completing tasks", pref.ConfirmComplete, "Each checklist can override this in its options menu.");

        var storage = Group("Notebook storage", "Your notes stay on this device.");
        var location = new TextBlock { Text = pref.Root, TextWrapping = TextWrapping.Wrap, Foreground = muted, Margin = new Thickness(0, 0, 0, 9) };
        storage.Children.Add(location);
        string nextRoot = pref.Root;
        storage.Children.Add(Btn("Choose folder…", () => { var f = new OpenFolderDialog(); if (f.ShowDialog(d) == true) { nextRoot = f.FolderName; location.Text = nextRoot; } }));
        storage.Children.Add(new TextBlock { Text = "This changes where new and imported notebooks are stored. Existing notebooks remain where they are.", TextWrapping = TextWrapping.Wrap, Foreground = muted, Margin = new Thickness(0, 8, 0, 0), FontSize = 11 });
        var save = Btn("Save settings", () =>
        {
            if (!TryShortcut(hotkey.Text.Trim(), true)) return;
            try { Directory.CreateDirectory(nextRoot); Desktop.Startup(startup.IsChecked == true); }
            catch (Exception ex) { MessageBox.Show(d, ex.Message, "Settings could not be applied"); return; }
            pref.Root = nextRoot; pref.Startup = startup.IsChecked == true; pref.Theme = (string)theme.SelectedItem; pref.Accent = (string)color.SelectedItem; pref.LockWidget = pin.IsChecked == true; pref.Pinned = false; pref.ConfirmDelete = delete.IsChecked == true; pref.ConfirmComplete = complete.IsChecked == true; pref.ReducedMotion = motion.IsChecked == true;
            Topmost = false; ResizeMode = !expanded && pref.LockWidget ? ResizeMode.NoResize : expanded ? ResizeMode.CanResize : ResizeMode.CanResize; pinButton.Content = pref.LockWidget ? "Unlock" : "Lock"; SavePreferences(); d.Close(); ApplyTheme();
        });
        save.MinHeight = 40; save.MinWidth = 140;
        var settingsActions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        settingsActions.Children.Add(Btn("Cancel", () => d.Close())); settingsActions.Children.Add(save);
        var settingsFooter = new Border { Background = glassPanel, BorderBrush = border, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 8, 16, 12), Child = settingsActions };
        Grid.SetRow(settingsFooter, 1); settingsLayout.Children.Add(settingsFooter);
        panel.Children.Add(new TextBlock { Text = "WidgetNotes 1.4 · Live Windows glass\nCtrl+N new page  ·  Ctrl+F search  ·  Ctrl+S save\nCtrl+Z undo  ·  Esc compact view", Foreground = muted, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, FontSize = 11 });
        if (diagnostic)
        {
            d.ContentRendered += async (_, _) =>
            {
                try
                {
                    await d.Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
                    CaptureElement((FrameworkElement)d.Content, IsDark ? "settings-dark.png" : "settings-light.png");
                    theme.IsDropDownOpen = true; await Task.Delay(100);
                    var popup = (Popup)theme.Template.FindName("PART_Popup", theme);
                    if (popup.Child is not FrameworkElement dropdown || dropdown.ActualHeight < 20) throw new Exception("Theme dropdown failed to render");
                    CaptureElement(dropdown, IsDark ? "dropdown-dark.png" : "dropdown-light.png");
                    theme.IsDropDownOpen = false;
                }
                catch (Exception ex) { d.Tag = ex; }
                finally { d.Close(); }
            };
        }
        d.ShowDialog();
        if (diagnostic && d.Tag is Exception failure) throw new InvalidOperationException("Settings visual verification failed", failure);
    }
    async Task RunUiVerification()
    {
        // This mode uses an isolated --test-root and never installs or changes startup settings.
        if (App.TestRoot == null) return;
        double ready = App.LaunchTime.Elapsed.TotalMilliseconds;
        await File.WriteAllTextAsync(Path.Combine(App.TestRoot, "ready.txt"), ready.ToString("F0"));
        if (App.Arguments.Contains("--ui-test")) await UiTest(ready); else await Benchmark(ready);
    }
    async Task LiveBackdropTest()
    {
        await BackdropDiagnostics.Run(this, windowRoot, App.TestRoot!);
        await ExitApp();
    }
    async Task ApplicationTest()
    {
        if (App.TestRoot == null) return;
        var log = new List<string>();
        try
        {
            Expand(true); await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
            if (!NativeGlass.IsTopLevel(this) || !ShowInTaskbar || WindowStyle != WindowStyle.None) throw new Exception("Application chrome or taskbar state failed");
            log.Add("PASS workspace is a top-level application with custom chrome");
            bool handled = false; WndProc(handle, 0x231, IntPtr.Zero, IntPtr.Zero, ref handled);
            int movingState = NativeGlass.AcceptedAccentState(this);
            WndProc(handle, 0x232, IntPtr.Zero, IntPtr.Zero, ref handled);
            int idleState = NativeGlass.AcceptedAccentState(this);
            if (movingState != 3 || idleState != 4) throw new Exception($"Compositor states: moving={movingState}, idle={idleState}");
            log.Add("PASS compositor switches to fast blur during movement and restores live acrylic afterward");
            Expand(false);
            if (!NativeGlass.IsTopLevel(this) || !ShowInTaskbar) throw new Exception("Compact mode must stay a top-level window");
            log.Add("PASS compact mode preserves top-level compositor backdrop");
            log.Add($"NativeBackdrop={nativeBackdrop}; AccentState={NativeGlass.AcceptedAccentState(this)}; BackdropType={NativeGlass.Backdrop(this)}");
            log.Add("ALL APPLICATION CHECKS PASSED");
        }
        catch (Exception ex) { log.Add("FAIL " + ex); }
        await File.WriteAllLinesAsync(Path.Combine(App.TestRoot, "desktop-test.txt"), log); await ExitApp();
    }
    async Task Benchmark(double ready)
    {
        await Task.Delay(2000); using var process = Process.GetCurrentProcess(); process.Refresh(); long memory = process.WorkingSet64; var cpu = process.TotalProcessorTime; var clock = Stopwatch.StartNew(); await Task.Delay(3000); process.Refresh();
        string report = $"ReadyMs={ready:F0}\nWorkingSetMB={memory / 1048576d:F1}\nIdleCpuPercent={(process.TotalProcessorTime - cpu).TotalMilliseconds / clock.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100:F3}\nRuntime={Environment.Version}\nProcessPath={Environment.ProcessPath}\n";
        await File.WriteAllTextAsync(Path.Combine(App.TestRoot!, "benchmark.txt"), report); await ExitApp();
    }
    async Task UiTest(double ready)
    {
        var results = new List<string>();
        try
        {
            if (!NativeGlass.IsTopLevel(this) || !ShowInTaskbar) throw new Exception("Window is not a normal top-level application");
            results.Add("PASS top-level window with taskbar presence");
            results.Add($"NativeBackdrop={nativeBackdrop}; BackdropType={NativeGlass.Backdrop(this)}");
            pref.ConfirmDelete = false; pref.ReducedMotion = true; page.Blocks[0].Items = Editing.Lines("Write ideas\nPlan tomorrow\nTake a break"); page.Blocks[0].Items[1].Done = true; Changed(); Render();
            await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
            Capture("widget-light.png"); results.Add("PASS widget renders task text boxes and checkboxes");
            if (Descendants<CheckBox>(root).Count() != 3) throw new Exception("Task control count");
            var row = Descendants<TextBox>(root).First(t => t.Tag as string == page.Blocks[0].Items[0].Id); row.Text = "Updated through actual text control";
            if (page.Blocks[0].Items[0].Text != row.Text) throw new Exception("Text input did not update model"); results.Add("PASS text control updates document");
            var latency = Stopwatch.StartNew(); row.Text += " · quick edit"; await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.Render); latency.Stop(); results.Add($"TextEditThroughRenderMs={latency.Elapsed.TotalMilliseconds:F1}");
            var check = Descendants<CheckBox>(root).First(); check.IsChecked = true; check.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (!page.Blocks[0].Items[0].Done) throw new Exception("Checkbox did not update model"); Undo(); if (page.Blocks[0].Items[0].Done) throw new Exception("Undo failed"); results.Add("PASS completion and undo");
            NewPage("text"); page.Title = "Ideas"; page.Blocks[0].Text = "A small notebook, a little more room to think."; Navigate(-1); if (page.Title == "Ideas") throw new Exception("Navigation failed"); results.Add("PASS page creation and navigation");
            var taskInput = Descendants<TextBox>(root).First(t => AutomationProperties.GetName(t) == "Add a task");
            taskInput.Text = "Added through the task input"; taskInput.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            if (!page.Blocks[0].Items.Any(t => t.Text == "Added through the task input")) throw new Exception("Add task input failed"); results.Add("PASS Enter commits task input");
            var paste = new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText, "Pasted task one\nPasted task two"), false, DataFormats.UnicodeText) { RoutedEvent = DataObject.PastingEvent }; taskInput.RaiseEvent(paste);
            if (!page.Blocks[0].Items.Any(t => t.Text == "Pasted task two")) throw new Exception("Multiline paste control failed"); results.Add("PASS multiline paste creates individual tasks");
            Hide(); ToggleVisibility();
            results.Add($"TrayState: IsVisible={IsVisible}; IsActive={IsActive}; Expanded={expanded}; ShowInTaskbar={ShowInTaskbar}");
            if (!IsVisible) throw new Exception("Tray show failed"); results.Add("PASS tray show/hide entrypoint restores widget");
            Expand(true); AddBlock("text"); page.Blocks.Last().Text = "Drag blocks by their headers. Resize from the corner."; Changed(); Render();
            var target = page.Blocks.Last(); var grip = Descendants<Thumb>(canvas).Where(t => t.Cursor == Cursors.SizeAll).Last(); grip.RaiseEvent(new DragStartedEventArgs(0, 0)); grip.RaiseEvent(new DragDeltaEventArgs(80, 100)); grip.RaiseEvent(new DragCompletedEventArgs(80, 100, false));
            if (target.X < 100 || target.Y < 100) throw new Exception("Drag failed"); results.Add("PASS canvas drag");
            var resize = Descendants<Thumb>(canvas).Where(t => t.Cursor == Cursors.SizeNWSE).Last(); double oldWidth = target.Width; resize.RaiseEvent(new DragDeltaEventArgs(40, 40)); if (target.Width <= oldWidth) throw new Exception("Resize failed"); results.Add("PASS canvas resize");
            search.Text = "Ideas"; UpdateLayout(); if (!Descendants<Button>(sidebar).Any(b => b.Content?.ToString()?.Contains("Ideas") == true)) throw new Exception("Search failed"); search.Clear(); results.Add("PASS search");
            string sample = Path.Combine(App.TestRoot!, "sample.png"); var drawing = new DrawingVisual(); using (var dc = drawing.RenderOpen()) { dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0, 103, 192)), null, new Rect(0, 0, 400, 240), 16, 16); dc.DrawEllipse(Brushes.White, null, new Point(200, 120), 65, 65); } var sampleBitmap = new RenderTargetBitmap(400, 240, 96, 96, PixelFormats.Pbgra32); sampleBitmap.Render(drawing); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(sampleBitmap)); using (var f = File.Create(sample)) png.Save(f);
            await ImportFiles([sample]); if (!page.Blocks.Any(b => b.Kind == "image" && File.Exists(Path.Combine(store.Folder, b.Media!)))) throw new Exception("Image import failed"); results.Add("PASS image import and display");
            string markdown = Path.Combine(App.TestRoot!, "claude-export.md");
            await File.WriteAllTextAsync(markdown, "---\ntitle: Release plan\nsource: Claude\n---\n# Release plan\n\n- [ ] Verify installer\n- [x] Update the widget\n\n```md\n- [ ] leave this as code\n```\n");
            await ImportMarkdownFiles([markdown]);
            if (page.Title != "Claude · Release plan" || page.Blocks.SelectMany(b => b.Items).Count() != 2 || page.Blocks.All(b => !b.Text.Contains("leave this as code"))) throw new Exception("Markdown agent import parsing");
            results.Add("PASS agent Markdown import creates editable tasks and preserves fenced examples");
            await Task.Delay(250); await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle); Capture("workspace-light.png"); pref.Theme = "Dark"; pref.Accent = "Orange"; ApplyTheme(); await Task.Delay(250); await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle); Capture("workspace-dark.png"); Expand(false); Render(); await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle); Capture("widget-dark.png");
            ShowMenu(); await Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
            if (lastMenu == null || lastMenu.ActualHeight < 100) throw new Exception("Notebook menu failed to render");
            CaptureElement(lastMenu, "menu-dark.png"); lastMenu.IsOpen = false;
            Settings(true); pref.Theme = "Light"; ApplyTheme(); Settings(true);
            results.Add("PASS rounded notebook menu, settings dialog, and theme dropdown render in both themes");
            if (!await Flush()) throw new Exception("Save failed"); var reopened = await NotebookStore.Open(store.Folder); if (reopened.Book.Pages.Count != 3) throw new Exception("Persistence failed"); results.Add("PASS shared document persistence");
            if (!SaveForSessionEnd()) throw new Exception("Session-end save failed"); results.Add("PASS synchronous session-end save");
            string roundtrip = Path.Combine(App.TestRoot!, "notebook.wnote"); await store.Export(roundtrip, Json.Write(store.Book)); var imported = await NotebookStore.Import(roundtrip, Path.Combine(App.TestRoot!, "roundtrip")); if (!imported.Book.Pages[0].Blocks.Any(b => b.Kind == "image" && File.Exists(Path.Combine(imported.Folder, b.Media!)))) throw new Exception("Image round trip failed"); results.Add("PASS portable notebook preserves rendered image");
            results.Add($"ReadyMs={ready:F0}");
        }
        catch (Exception ex) { results.Add("FAIL " + ex); }
        results.Add(results.Any(line => line.StartsWith("FAIL ", StringComparison.Ordinal)) ? "UI TESTS FAILED" : "ALL UI TESTS PASSED");
        await File.WriteAllLinesAsync(Path.Combine(App.TestRoot!, "ui-test.txt"), results); await ExitApp();
    }
    static void CaptureElement(FrameworkElement element, string file)
    {
        element.UpdateLayout();
        int width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth)), height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(App.TestRoot!, file)); encoder.Save(stream);
    }
    void Capture(string file)
    {
        UpdateLayout(); var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(App.TestRoot!, file)); encoder.Save(stream);
    }
}




