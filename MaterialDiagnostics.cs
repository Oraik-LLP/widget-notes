using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace WidgetNotes;
internal static class MaterialDiagnostics
{
    public static bool Enabled => App.TestRoot != null && App.Arguments.Contains("--material-preview");
    public static void Start(MainWindow window)
    {
        if (!Enabled) return;
        var pattern = new Grid(); for (int i=0;i<8;i++) pattern.ColumnDefinitions.Add(new());
        string[] colors=["#F43F5E","#F97316","#FACC15","#22C55E","#06B6D4","#3B82F6","#A855F7","#EC4899"];
        for (int i=0;i<8;i++) { var panel=new Border {Background=(Brush)new BrushConverter().ConvertFromString(colors[i])!}; Grid.SetColumn(panel,i); pattern.Children.Add(panel); }
        var backdrop = new Window { Title="WidgetNotes material test backdrop",Left=80,Top=80,Width=1120,Height=800,Content=pattern,ShowActivated=false,Topmost=true };
        backdrop.Show(); window.Left=350; window.Top=150; window.Width=520; window.Height=620; window.Topmost=true; window.Title="WidgetNotes material verification";
        window.ContentRendered += async (_,_) =>
        {
            int frame=0; string phase="active";
            void Record() => File.WriteAllText(Path.Combine(App.TestRoot!,"material-state.json"),Json.Write(new {Phase=phase,Frame=frame,Active=window.IsActive,KeptActive=NativeGlass.KeepsActive(window),Backdrop=NativeGlass.Backdrop(window),Background=window.Background.ToString()}));
            var animation=new DispatcherTimer {Interval=TimeSpan.FromSeconds(2)};
            animation.Tick += (_,_) => {frame++;for(int i=0;i<pattern.Children.Count;i++) ((Border)pattern.Children[i]).Background=(Brush)new BrushConverter().ConvertFromString(colors[(i+frame)%colors.Length])!;Record();};
            animation.Start(); await Task.Delay(2000); Record();
            var focus=new Window {Title="WidgetNotes focus verification",Width=240,Height=110,Left=1250,Top=180,Content=new TextBlock{Text="Testing the widget while it is unfocused",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(14)}};
            focus.Show(); await Task.Delay(250); phase="unfocused";Record();
            await Task.Delay(6000);NativeGlass.SendTestMessage(window,0x231);phase="paused";Record();
            await Task.Delay(20000);NativeGlass.SendTestMessage(window,0x232);phase="resumed-unfocused";Record();
            await Task.Delay(25000);animation.Stop();await window.ExitApp();
        };
    }
}
