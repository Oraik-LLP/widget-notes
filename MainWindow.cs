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
    readonly Image glassBackdrop = new() { Stretch = Stretch.Fill, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    readonly DispatcherTimer glassRefresh = new() { Interval = TimeSpan.FromMilliseconds(180) };
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
    readonly DesktopAttachment desktopAttachment;
    readonly DispatcherTimer desktopWatch = new() { Interval = TimeSpan.FromSeconds(2) };
    Window? modalOwner;
    bool changingMode, desktopUnavailable;
    readonly SemaphoreSlim flushGate = new(1, 1);
    bool expanded, rendering, dirty, exiting, busy;
    long revision;
    double compactLeft, compactTop, compactWidth, compactHeight;
    Brush surface = Brushes.White, foreground = Brushes.Black, muted = Brushes.Gray, accent = Brushes.Blue, border = Brushes.LightGray;
    Brush glassPanel = Brushes.White, glassRaised = Brushes.White, inputSurface = Brushes.White;
    IntPtr handle;
    bool shortcutRegistered;
    bool glassCaptureInProgress;
    LiquidGlassEffect? glassEffect;
    Brush? glassFallback;
    public MainWindow(NotebookStore initial, Preferences settings)
    {
        store = initial; pref = settings; pref.Pinned = false;
        desktopAttachment = new DesktopAttachment(this);
        page = store.Book.Pages.FirstOrDefault(p => p.Id == pref.LastPage) ?? store.Book.Pages[0];
        Title = "WidgetNotes"; Icon = AppArtwork.WindowIcon; FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        MinWidth = 340; MinHeight = 360; Width = Math.Clamp(pref.Width, 340, 900); Height = Math.Clamp(pref.Height, 360, 1000);
        Left = double.IsFinite(pref.Left) ? pref.Left : 60; Top = double.IsFinite(pref.Top) ? pref.Top : 60;
        ClampPosition(); Topmost = false; ShowInTaskbar = false; ShowActivated = false; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResizeWithGrip;
        Content = windowRoot; windowRoot.Children.Add(glassBackdrop); windowRoot.Children.Add(root);
        AllowDrop = true; root.SnapsToDevicePixels = true; root.UseLayoutRounding = true;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new DockPanel { LastChildFill = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        pinButton = Btn("Lock", () => { pref.LockWidget = !pref.LockWidget; pinButton!.Content = pref.LockWidget ? "Unlock" : "Lock"; ResizeMode = pref.LockWidget && !expanded ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip; SavePreferences(); }, "Lock or unlock the widget's desktop position");
        pinButton.Content = pref.LockWidget ? "Unlock" : "Lock";
        if (pref.LockWidget) ResizeMode = ResizeMode.NoResize;
        expandButton = Btn("Expand ↗", () => Expand(!expanded), "Expand notebook / return to widget");
        actions.Children.Add(pinButton); actions.Children.Add(expandButton); actions.Children.Add(Btn("⋯", ShowMenu, "Notebook menu and settings"));
        DockPanel.SetDock(actions, Dock.Right); header.Children.Add(actions);
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new System.Windows.Controls.Image { Source = AppArtwork.WindowIcon, Width = 25, Height = 25, Margin = new Thickness(2, 0, 9, 0), Stretch = Stretch.Uniform });
        brand.Children.Add(notebookTitle);
        var dragArea = new Grid(); dragArea.Children.Add(brand);
        var widgetGrip = new Thumb { Opacity = .01, Cursor = Cursors.SizeAll, ToolTip = "Drag to move the desktop widget" };
        AutomationProperties.SetName(widgetGrip, "Move desktop widget"); dragArea.Children.Add(widgetGrip);
        widgetGrip.DragDelta += (_, e) => MoveWidget(e.HorizontalChange, e.VerticalChange);
        widgetGrip.DragCompleted += (_, _) => SavePreferences();
        header.Children.Add(dragArea);
        headerGlass = new Border { Margin = new Thickness(11, 10, 11, 8), Padding = new Thickness(5, 3, 5, 3), CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Child = header };
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
        Loaded += (_, _) => { AttachToDesktop(); desktopWatch.Start(); ScheduleGlassRefresh(); };
        glassRefresh.Tick += async (_, _) => { glassRefresh.Stop(); await RefreshGlassBackdrop(); };
        desktopWatch.Tick += (_, _) => { if (!expanded && !changingMode && !desktopAttachment.IsAttached) AttachToDesktop(); };
        Closed += (_, _) => { desktopWatch.Stop(); autosave.Stop(); modalOwner?.Close(); if (!exiting) ((App)System.Windows.Application.Current).RecoverDesktopWindow(store, pref); };
        Activated += (_, _) => { if (pref.Theme == "System" || pref.Accent == "Windows") ApplyTheme(); };
        LocationChanged += (_, _) => { if (IsVisible && !expanded && !changingMode) CaptureCompact(); if (IsVisible && !changingMode) ScheduleGlassRefresh(); };
        SizeChanged += (_, _) => { if (IsVisible && !expanded && !changingMode) CaptureCompact(); if (IsVisible && !changingMode) ScheduleGlassRefresh(); };
        ContentRendered += async (_, _) => { if (App.Arguments.Contains("--desktop-test")) await DesktopTest(); else if (App.Arguments.Contains("--ui-test") || App.Arguments.Contains("--benchmark")) await RunUiVerification(); };
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
    void ApplyTheme()
    {
        bool dark = pref.Theme == "Dark" || pref.Theme == "System" && Desktop.Dark;
        Brush B(string value)
        {
            var brush = (Brush)new BrushConverter().ConvertFromString(value)!;
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }
        Color C(string value) => (Color)ColorConverter.ConvertFromString(value)!;
        surface = B(dark ? "#F31A2A42" : "#F4F8FBFF");
        foreground = B(dark ? "#F4F8FF" : "#19263A");
        muted = B(dark ? "#B7C5D9" : "#68788D");
        border = B(dark ? "#52FFFFFF" : "#879BB5");
        glassPanel = B(dark ? "#5830445F" : "#62FFFFFF");
        glassRaised = B(dark ? "#70445C7A" : "#78FFFFFF");
        inputSurface = B(dark ? "#70101D30" : "#82EDF5FB");
        var hoverSurface = B(dark ? "#A5486484" : "#C8F5FCFF");
        accent = B(pref.Accent == "Orange" ? dark ? "#FFFFB36A" : "#FF8A42" : pref.Accent == "Windows" ? Desktop.Accent : dark ? "#68D9FF" : "#1683E8");
        var background = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        background.GradientStops.Add(new GradientStop(C(dark ? "#7C294566" : "#88FFFFFF"), 0));
        background.GradientStops.Add(new GradientStop(C(dark ? "#741C2B43" : "#78EAF2FA"), .48));
        background.GradientStops.Add(new GradientStop(C(dark ? "#80141D31" : "#82E3EDF6"), 1));
        background.Freeze(); glassFallback = background;
        Background = Brushes.Transparent; windowRoot.Background = glassBackdrop.Source == null ? background : Brushes.Transparent; root.Background = Brushes.Transparent;
        headerGlass.Background = glassPanel; headerGlass.BorderBrush = border;
        sidebarGlass.Background = glassPanel; sidebarGlass.BorderBrush = border;
        footerGlass.Background = glassPanel; footerGlass.BorderBrush = border;
        status.Foreground = muted; counter.Foreground = muted; title.Foreground = foreground; search.Background = inputSurface; search.Foreground = foreground;

        var buttonStyle = new Style(typeof(Button));
        buttonStyle.Setters.Add(new Setter(Control.BackgroundProperty, glassRaised));
        buttonStyle.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
        buttonStyle.Setters.Add(new Setter(Control.BorderBrushProperty, border));
        buttonStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        buttonStyle.Setters.Add(new Setter(Control.FontSizeProperty, 12d));
        buttonStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Medium));
        var template = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        bd.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp); template.VisualTree = bd;
        buttonStyle.Setters.Add(new Setter(Control.TemplateProperty, template));
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, hoverSurface));
        hover.Setters.Add(new Setter(Control.BorderBrushProperty, accent));
        buttonStyle.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Control.BackgroundProperty, accent));
        pressed.Setters.Add(new Setter(Control.ForegroundProperty, B("#FFFFFFFF")));
        buttonStyle.Triggers.Add(pressed);
        var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Control.BorderBrushProperty, accent)); buttonStyle.Triggers.Add(focus);
        Resources[typeof(Button)] = buttonStyle;

        var menuStyle = new Style(typeof(ContextMenu));
        menuStyle.Setters.Add(new Setter(Control.BackgroundProperty, glassPanel));
        menuStyle.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
        menuStyle.Setters.Add(new Setter(Control.BorderBrushProperty, border));
        Resources[typeof(ContextMenu)] = menuStyle;
        var menuItemStyle = new Style(typeof(MenuItem));
        menuItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
        menuItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14, 8, 14, 8)));
        Resources[typeof(MenuItem)] = menuItemStyle;

        var tbStyle = new Style(typeof(TextBox));
        tbStyle.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
        tbStyle.Setters.Add(new Setter(Control.BackgroundProperty, inputSurface));
        tbStyle.Setters.Add(new Setter(Control.BorderBrushProperty, border));
        tbStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        tbStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 7, 10, 7)));
        tbStyle.Setters.Add(new Setter(Control.FontSizeProperty, 13d));
        tbStyle.Setters.Add(new Setter(TextBoxBase.CaretBrushProperty, accent));
        var textTemplate = new ControlTemplate(typeof(TextBox));
        var textBorder = new FrameworkElementFactory(typeof(Border));
        textBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        textBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        textBorder.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        textBorder.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        var contentHost = new FrameworkElementFactory(typeof(ScrollViewer));
        contentHost.Name = "PART_ContentHost";
        textBorder.AppendChild(contentHost); textTemplate.VisualTree = textBorder;
        tbStyle.Setters.Add(new Setter(Control.TemplateProperty, textTemplate));
        var textFocus = new Trigger { Property = IsKeyboardFocusWithinProperty, Value = true };
        textFocus.Setters.Add(new Setter(Control.BorderBrushProperty, accent)); tbStyle.Triggers.Add(textFocus);
        Resources[typeof(TextBox)] = tbStyle;

        var checkStyle = new Style(typeof(CheckBox));
        checkStyle.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
        checkStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        Resources[typeof(CheckBox)] = checkStyle;
        Desktop.WindowTheme(this, dark, expanded && glassBackdrop.Source == null);
        if (glassBackdrop.Source != null) ScheduleGlassRefresh();
        if (IsLoaded) Render();
    }
    void ScheduleGlassRefresh()
    {
        if (App.TestRoot != null || !IsLoaded || !IsVisible) return;
        glassRefresh.Stop(); glassRefresh.Start();
    }
    async Task RefreshGlassBackdrop()
    {
        if (App.TestRoot != null || glassCaptureInProgress || !IsVisible || handle == IntPtr.Zero || ActualWidth < 1 || ActualHeight < 1) return;
        glassCaptureInProgress = true;
        try
        {
            Point topLeft = PointToScreen(new Point(0, 0));
            Point bottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));
            int x = (int)Math.Round(topLeft.X), y = (int)Math.Round(topLeft.Y);
            int width = (int)Math.Round(bottomRight.X - topLeft.X), height = (int)Math.Round(bottomRight.Y - topLeft.Y);
            if (width is < 80 or > 5000 || height is < 80 or > 5000) return;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            BitmapSource? snapshot = ScreenCaptureHelper.CaptureWindowRegion(handle, x, y, width, height);
            if (snapshot == null) return;
            glassEffect ??= new LiquidGlassEffect();
            glassEffect.TextureSize = new Point(snapshot.PixelWidth, snapshot.PixelHeight);
            glassEffect.GlassCenter = new Point(snapshot.PixelWidth / 2d, snapshot.PixelHeight / 2d);
            glassEffect.GlassSize = new Point(snapshot.PixelWidth, snapshot.PixelHeight);
            glassEffect.BlurIntensity = .3f;
            glassBackdrop.Effect = glassEffect;
            glassBackdrop.Source = snapshot;
            glassBackdrop.Visibility = Visibility.Visible;
            windowRoot.Background = Brushes.Transparent;
            bool dark = pref.Theme == "Dark" || pref.Theme == "System" && Desktop.Dark;
            Desktop.WindowTheme(this, dark, false);
        }
        catch
        {
            glassBackdrop.Source = null; glassBackdrop.Effect = null; glassBackdrop.Visibility = Visibility.Collapsed;
            windowRoot.Background = glassFallback ?? Brushes.Transparent;
            bool dark = pref.Theme == "Dark" || pref.Theme == "System" && Desktop.Dark;
            Desktop.WindowTheme(this, dark, expanded);
        }
        finally { glassCaptureInProgress = false; }
    }
    void Render()
    {
        rendering = true; title.Text = page.Title; notebookTitle.Text = store.Book.Title; notebookTitle.TextTrimming = TextTrimming.CharacterEllipsis;
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
            if (next && Editing.RequiresConfirmation(block, pref) && MessageBox.Show(ModalOwner, "Mark this task as complete?\n\n" + todo.Text, "Complete task", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { check.IsChecked = todo.Done; return; }
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
        var menu = new ContextMenu();
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
    void ShowNewPageMenu() { var m = new ContextMenu(); Menu(m, "Checklist page", () => NewPage("checklist")); Menu(m, "Note page", () => NewPage("text")); m.IsOpen = true; }
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
        if (expanded == value) { Show(); if (expanded) Activate(); return; }
        changingMode = true;
        if (value)
        {
            CaptureCompact(); compactLeft = pref.Left; compactTop = pref.Top; compactWidth = pref.Width; compactHeight = pref.Height;
            desktopAttachment.Detach(); expanded = true; WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.CanResize; ShowInTaskbar = true; ShowActivated = true;
            MinWidth = 850; MinHeight = 600; Width = Math.Min(1280, SystemParameters.WorkArea.Width); Height = Math.Min(850, SystemParameters.WorkArea.Height); Left = compactLeft; Top = compactTop; ClampPosition();
        }
        else
        {
            WindowState = WindowState.Normal; expanded = false; ShowInTaskbar = false; ShowActivated = false; WindowStyle = WindowStyle.None; ResizeMode = pref.LockWidget ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
            MinWidth = 340; MinHeight = 360; Width = compactWidth; Height = compactHeight; Left = compactLeft; Top = compactTop; ClampPosition();
            pref.Left = Left; pref.Top = Top; pref.Width = Width; pref.Height = Height; AttachToDesktop();
        }
        Topmost = false; changingMode = false;
        content.ColumnDefinitions[0].Width = new GridLength(expanded ? 210 : 0); sidebar.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed; expandButton.Content = expanded ? "Widget ↙" : "Expand ↗"; ModeChanged?.Invoke(); ApplyTheme();
        pinButton.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        Show(); if (expanded || desktopUnavailable) Activate(); SavePreferences(); ScheduleGlassRefresh();
    }
    void AttachToDesktop()
    {
        if (expanded) return;
        bool previous = changingMode; changingMode = true;
        var bounds = new Rect(pref.Left, pref.Top, Math.Clamp(pref.Width, 340, 900), Math.Clamp(pref.Height, 360, 1000));
        if (desktopAttachment.Attach(bounds))
        {
            if (desktopUnavailable) { desktopUnavailable = false; ShowInTaskbar = false; Show(); status.Text = "Desktop reconnected"; }
        }
        else
        {
            desktopUnavailable = true;
            ShowInTaskbar = true;
            status.Text = "Desktop unavailable · floating widget";
            status.ToolTip = desktopAttachment.LastError;
        }
        changingMode = previous;
    }
    void MoveWidget(double x, double y)
    {
        if (expanded || pref.LockWidget || !desktopAttachment.IsAttached) return;
        var bounds = desktopAttachment.Bounds; bounds.Offset(x, y);
        var dpi = VisualTreeHelper.GetDpi(this);
        var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)(bounds.Left * dpi.DpiScaleX), (int)(bounds.Top * dpi.DpiScaleY))).WorkingArea;
        bounds.X = Math.Clamp(bounds.X, area.Left / dpi.DpiScaleX, Math.Max(area.Left / dpi.DpiScaleX, area.Right / dpi.DpiScaleX - bounds.Width));
        bounds.Y = Math.Clamp(bounds.Y, area.Top / dpi.DpiScaleY, Math.Max(area.Top / dpi.DpiScaleY, area.Bottom / dpi.DpiScaleY - bounds.Height));
        changingMode = true; desktopAttachment.Place(bounds); changingMode = false; CaptureCompact();
    }
    void CaptureCompact()
    {
        if (WindowState != WindowState.Normal || expanded || changingMode) return;
        if (desktopAttachment.IsAttached) { var r = desktopAttachment.Bounds; pref.Left = r.Left; pref.Top = r.Top; pref.Width = r.Width; pref.Height = r.Height; }
        else { pref.Left = Left; pref.Top = Top; pref.Width = Width; pref.Height = Height; }
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
        if (focus) { search.Focus(); search.CaretIndex = caret; }
    }
    void SetZoom(double value) { zoom.ScaleX = zoom.ScaleY = Math.Clamp(value, .4, 2); RenderSidebar(); }
    void MovePage(int delta) { int i = store.Book.Pages.IndexOf(page), next = i + delta; if (next < 0 || next >= store.Book.Pages.Count) return; Remember(); store.Book.Pages.RemoveAt(i); store.Book.Pages.Insert(next, page); Changed(); Render(); }
    void DeletePage() { if (!Confirm("Delete this page and all of its blocks?")) return; Remember(); store.Book.Pages.Remove(page); if (store.Book.Pages.Count == 0) store.Book.Pages.Add(new()); page = store.Book.Pages[0]; Changed(); Render(); }
    void RenameNotebook() { string? value = Ask("Rename notebook", "Notebook name", store.Book.Title); if (!string.IsNullOrWhiteSpace(value)) { Remember(); store.Book.Title = value; Changed(); Render(); } }
    bool Confirm(string message) => !pref.ConfirmDelete || MessageBox.Show(ModalOwner, message, "Confirm deletion", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
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
        pref.NotebookFolder = store.Folder; pref.LastPage = page.Id; CaptureCompact();
        try { Directory.CreateDirectory(Path.GetDirectoryName(App.ActualSettingsFile)!); NotebookStore.AtomicWrite(App.ActualSettingsFile, Json.Write(pref)); }
        catch (Exception ex) { status.Text = "Settings save failed"; status.ToolTip = ex.Message; }
    }
    public void ToggleVisibility()
    {
        if (IsVisible && (!expanded || IsActive)) { SavePreferences(); Hide(); }
        else { Reveal(); }
    }
    public void Reveal()
    {
        if (expanded) { Show(); WindowState = WindowState.Normal; ClampPosition(); Activate(); }
        else
        {
            if (!desktopAttachment.IsAttached) AttachToDesktop();
            Show();
            if (desktopUnavailable) Activate(); else desktopAttachment.KeepDesktopOrder();
        }
    }
    Window ModalOwner
    {
        get
        {
            if (expanded) return this;
            // Modal dialogs need a top-level owner. Never disable Explorer by using its child as owner.
            modalOwner ??= new Window { Title = "WidgetNotes dialogs", ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
            var bounds = desktopAttachment.IsAttached ? desktopAttachment.Bounds : new Rect(pref.Left, pref.Top, pref.Width, pref.Height);
            modalOwner.Left = bounds.Left; modalOwner.Top = bounds.Top; modalOwner.Width = bounds.Width; modalOwner.Height = bounds.Height;
            new WindowInteropHelper(modalOwner).EnsureHandle(); return modalOwner;
        }
    }
    async void OnClosing(object? sender, CancelEventArgs e) { if (exiting) return; e.Cancel = true; if (await Flush()) Hide(); else MessageBox.Show(ModalOwner, "Your changes could not be saved. They remain open. Use Retry or export a portable recovery copy.", "Save failed"); }
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
        if (msg == 0x7e) Dispatcher.BeginInvoke(() => { if (expanded) ClampPosition(); else MoveWidget(0, 0); });
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
    void Menu(ItemsControl menu, string label, Action action) { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item); }
    void ShowMenu()
    {
        var menu = new ContextMenu(); Menu(menu, "Notebooks…", ShowNotebookMenu); Menu(menu, "Rename notebook…", RenameNotebook); Menu(menu, "New page…", ShowNewPageMenu);
        Menu(menu, "Move page earlier", () => MovePage(-1)); Menu(menu, "Move page later", () => MovePage(1)); Menu(menu, "Delete page…", DeletePage); menu.Items.Add(new Separator());
        Menu(menu, "Undo last change", Undo); Menu(menu, "Import AI / Markdown…", async () => await PickMarkdown()); Menu(menu, "Save / retry", async () => await Flush()); Menu(menu, "Export portable notebook…", ExportArchive); Menu(menu, "Open .wnote file…", async () => { var p = new OpenFileDialog { Filter = "WidgetNotes notebook|*.wnote" }; if (p.ShowDialog(ModalOwner) == true) await OpenArchive(p.FileName); });
        Menu(menu, "Export text…", ExportText); Menu(menu, "Print / Save as PDF…", Print); Menu(menu, "Open notebook folder", () => Desktop.Shell(store.Folder)); menu.Items.Add(new Separator());
        Menu(menu, "Settings…", Settings); Menu(menu, "Exit", async () => await ExitApp()); menu.IsOpen = true;
    }
    void ShowNotebookMenu()
    {
        var menu = new ContextMenu(); Menu(menu, "+ New notebook…", async () => { if (busy || !await Flush()) return; string? name = Ask("New notebook", "Notebook name", "New notebook"); if (string.IsNullOrWhiteSpace(name)) return; try { await SwitchStore(await App.NewStore(pref.Root, name)); } catch (Exception ex) { MessageBox.Show(ModalOwner, ex.Message, "Could not create notebook"); } });
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
        bool dark = pref.Theme == "Dark" || pref.Theme == "System" && Desktop.Dark;
        var dialog = new Window { Title = label, Owner = ModalOwner, Content = body, Width = width, Height = height, MinWidth = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = glassPanel, Foreground = foreground, FontFamily = FontFamily, ShowInTaskbar = false };
        dialog.Resources = Resources;
        dialog.SourceInitialized += (_, _) => Desktop.WindowTheme(dialog, dark, true);
        return dialog;
    }
    string? Ask(string label, string prompt, string initial)
    {
        var p = new StackPanel { Margin = new Thickness(18) }; p.Children.Add(new TextBlock { Text = prompt }); var input = new TextBox { Text = initial, Margin = new Thickness(0, 10, 0, 10), Padding = new Thickness(6) }; p.Children.Add(input); var d = Dialog(label, p, 380, 190); string? result = null;
        var ok = Btn("Save", () => { if (string.IsNullOrWhiteSpace(input.Text)) return; result = input.Text.Trim(); d.DialogResult = true; }); ok.IsDefault = true; p.Children.Add(ok); d.Loaded += (_, _) => { input.Focus(); input.SelectAll(); }; d.ShowDialog(); return result;
    }
    void Settings()
    {
        var panel = new StackPanel { Margin = new Thickness(22) };
        var d = Dialog("WidgetNotes settings", new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 520, 760);
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

        var desktop = Group("Desktop & startup", "Keep your notebook close without getting in the way.");
        var startup = Check(desktop, "Open WidgetNotes when I sign in", Desktop.HasStartup(), "The installer enables this by default. You can turn it off here any time.");
        var pin = Check(desktop, "Lock widget position", pref.LockWidget, "Prevents accidental dragging or resizing on the desktop.");
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
            Topmost = false; ResizeMode = !expanded && pref.LockWidget ? ResizeMode.NoResize : expanded ? ResizeMode.CanResize : ResizeMode.CanResizeWithGrip; pinButton.Content = pref.LockWidget ? "Unlock" : "Lock"; SavePreferences(); d.Close(); ApplyTheme();
        });
        save.MinHeight = 42; save.HorizontalAlignment = HorizontalAlignment.Stretch; save.Margin = new Thickness(0, 3, 0, 10); panel.Children.Add(save);
        panel.Children.Add(new TextBlock { Text = "WidgetNotes 1.2 · Desktop-attached notebook\nCtrl+N new page  ·  Ctrl+F search  ·  Ctrl+S save\nCtrl+Z undo  ·  Esc return to widget", Foreground = muted, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, FontSize = 11 });
        d.ShowDialog();
    }
    async Task RunUiVerification()
    {
        // This mode uses an isolated --test-root and never installs or changes startup settings.
        if (App.TestRoot == null) return;
        double ready = App.LaunchTime.Elapsed.TotalMilliseconds;
        await File.WriteAllTextAsync(Path.Combine(App.TestRoot, "ready.txt"), ready.ToString("F0"));
        if (App.Arguments.Contains("--ui-test")) await UiTest(ready); else await Benchmark(ready);
    }
    async Task DesktopTest()
    {
        if (App.TestRoot == null) return;
        var log = new List<string>();
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); log.Add("PASS " + label); }
        Window? cover = null, dialog = null;
        try
        {
            Check(desktopAttachment.IsAttached && DesktopAttachment.IsChildOfDesktop(handle), "compact widget is an Explorer desktop child");
            Check(!Topmost && !DesktopAttachment.IsTopmost(handle) && !ShowInTaskbar, "compact widget has no topmost or taskbar presence");
            var before = desktopAttachment.Bounds; pref.LockWidget = false; MoveWidget(20, 20); var after = desktopAttachment.Bounds;
            Check(Math.Abs(after.Left - before.Left - 20) < 2, "header movement uses desktop screen coordinates");
            pinButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); MoveWidget(30, 30);
            Check(pref.LockWidget && desktopAttachment.IsAttached && Math.Abs(desktopAttachment.Bounds.Left - after.Left) < 2, "position-lock button prevents movement and preserves attachment");
            pinButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            desktopAttachment.Place(new Rect(SystemParameters.WorkArea.Right - 470, SystemParameters.WorkArea.Bottom - 570, before.Width, before.Height));
            after = desktopAttachment.Bounds;
            var center = PointToScreen(new Point(Width / 2, Height / 2));
            cover = new Window { Title = "WidgetNotes overlap test", Width = 450, Height = 550, Left = after.Left - 10, Top = after.Top - 10, Background = Brushes.CornflowerBlue };
            cover.Show(); cover.Activate(); await Task.Delay(250);
            var coverHandle = new WindowInteropHelper(cover).Handle;
            var atPoint = DesktopAttachment.WindowAt(center);
            log.Add($"Probe: point={center}, widget={handle}, cover={coverHandle}, hit={atPoint}, root={DesktopAttachment.RootWindow(atPoint)}, class={DesktopAttachment.ClassName(DesktopAttachment.RootWindow(atPoint))}");
            Check(DesktopAttachment.RootWindow(atPoint) != DesktopAttachment.RootWindow(handle), "ordinary application window covers the widget");
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            try
            {
                cover.WindowState = WindowState.Minimized;
                shell.MinimizeAll(); await Task.Delay(1200);
                var desktopPoint = DesktopAttachment.WindowAt(center);
                log.Add($"Desktop probe: hit={desktopPoint}, root={DesktopAttachment.RootWindow(desktopPoint)}, class={DesktopAttachment.ClassName(desktopPoint)}");
                Check(desktopPoint == handle, "widget remains visible on Show Desktop / minimize all");
            }
            finally { shell.UndoMinimizeALL(); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
            cover.Close(); cover = null;
            dialog = Dialog("Desktop widget settings test", new TextBlock { Text = "Modal dialog test", Margin = new Thickness(20) }, 380, 160);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (_, _) => { timer.Stop(); dialog.DialogResult = true; }; timer.Start(); Check(dialog.ShowDialog() == true, "desktop widget opens and closes a modal dialog safely"); dialog = null;
            Expand(true); await Task.Delay(150);
            Check(!desktopAttachment.IsAttached && !DesktopAttachment.IsChild(handle) && ShowInTaskbar && !Topmost, "workspace detaches into a normal application window");
            page.Blocks[0].Items.Add(new Todo { Text = "Desktop attachment preserves notebook content" }); Changed();
            Expand(false); await Task.Delay(150);
            Check(desktopAttachment.IsAttached && !ShowInTaskbar && !DesktopAttachment.IsTopmost(handle), "returning to widget reattaches to desktop");
            Check(page.Blocks[0].Items.Last().Text.Contains("preserves"), "content preserved across desktop/workspace switches");
            Hide(); Reveal(); Check(IsVisible && desktopAttachment.IsAttached, "tray reveal preserves desktop attachment");
            Check(await Flush(), "desktop widget changes save normally");
        }
        catch (Exception ex) { log.Add("FAIL " + ex); }
        finally { dialog?.Close(); cover?.Close(); }
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
            var shaderResource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/WidgetNotes;component/Assets/GlassyEffect.ps", UriKind.Absolute));
            if (shaderResource == null || shaderResource.Stream.Length < 1000) throw new Exception("Liquid-glass shader resource could not be loaded");
            shaderResource.Stream.Dispose(); results.Add("PASS packaged liquid-glass shader resource loads");
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
            results.Add($"TrayState: IsVisible={IsVisible}; IsActive={IsActive}; Expanded={expanded}; Attached={desktopAttachment.IsAttached}; DesktopUnavailable={desktopUnavailable}; ShowInTaskbar={ShowInTaskbar}");
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
            if (!await Flush()) throw new Exception("Save failed"); var reopened = await NotebookStore.Open(store.Folder); if (reopened.Book.Pages.Count != 3) throw new Exception("Persistence failed"); results.Add("PASS shared document persistence");
            if (!SaveForSessionEnd()) throw new Exception("Session-end save failed"); results.Add("PASS synchronous session-end save");
            string roundtrip = Path.Combine(App.TestRoot!, "notebook.wnote"); await store.Export(roundtrip, Json.Write(store.Book)); var imported = await NotebookStore.Import(roundtrip, Path.Combine(App.TestRoot!, "roundtrip")); if (!imported.Book.Pages[0].Blocks.Any(b => b.Kind == "image" && File.Exists(Path.Combine(imported.Folder, b.Media!)))) throw new Exception("Image round trip failed"); results.Add("PASS portable notebook preserves rendered image");
            results.Add($"ReadyMs={ready:F0}");
        }
        catch (Exception ex) { results.Add("FAIL " + ex); }
        results.Add(results.Any(line => line.StartsWith("FAIL ", StringComparison.Ordinal)) ? "UI TESTS FAILED" : "ALL UI TESTS PASSED");
        await File.WriteAllLinesAsync(Path.Combine(App.TestRoot!, "ui-test.txt"), results); await ExitApp();
    }
    void Capture(string file)
    {
        UpdateLayout(); var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(App.TestRoot!, file)); encoder.Save(stream);
    }
}




