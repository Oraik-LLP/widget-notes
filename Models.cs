using System.Text.Json;

namespace WidgetNotes;

public sealed class Notebook
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "My notebook";
    public List<NotePage> Pages { get; set; } = [new()];
}
public sealed class NotePage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Today";
    public List<Block> Blocks { get; set; } = [new()];
}
public sealed class Block
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "checklist";
    public double X { get; set; } = 32;
    public double Y { get; set; } = 32;
    public double Width { get; set; } = 340;
    public double Height { get; set; } = 260;
    public string Text { get; set; } = "";
    public List<Todo> Items { get; set; } = [];
    public bool? ConfirmCompletion { get; set; }
    public string? Media { get; set; }
    public string? FileName { get; set; }
}
public sealed class Todo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public bool Done { get; set; }
}
public sealed class Preferences
{
    public string Root { get; set; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WidgetNotes");
    public string? NotebookFolder { get; set; }
    public string? LastPage { get; set; }
    public string Theme { get; set; } = "System";
    public string Accent { get; set; } = "Blue";
    public bool ConfirmDelete { get; set; } = true;
    public bool ConfirmComplete { get; set; }
    public bool ReducedMotion { get; set; }
    public bool Pinned { get; set; }
    public bool LockWidget { get; set; }
    public bool CompactMode { get; set; }
    public bool CanvasMode { get; set; }
    public double WorkspaceLeft { get; set; } = 100;
    public double WorkspaceTop { get; set; } = 80;
    public double WorkspaceWidth { get; set; } = 1180;
    public double WorkspaceHeight { get; set; } = 800;
    public bool Startup { get; set; }
    public string Shortcut { get; set; } = "Ctrl+Alt+N";
    public double Left { get; set; } = 60;
    public double Top { get; set; } = 60;
    public double Width { get; set; } = 360;
    public double Height { get; set; } = 480;
}
public static class Json
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string text) => JsonSerializer.Deserialize<T>(text, Options) ?? throw new InvalidDataException("Empty document.");
}
public static class Editing
{
    public static List<Todo> Lines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => new Todo { Text = s.Trim() }).ToList();
    public static bool RequiresConfirmation(Block block, Preferences settings) => block.ConfirmCompletion ?? settings.ConfirmComplete;
    public static IEnumerable<Block> ReadingOrder(NotePage page) => page.Blocks.OrderBy(b => b.Y).ThenBy(b => b.X);
    public static string PlainText(Notebook book) => string.Join("\n\n", book.Pages.Select(p => p.Title + "\n" + string.Join("\n\n", ReadingOrder(p).Select(b => b.Kind switch { "checklist" => string.Join("\n", b.Items.Select(t => $"[{(t.Done ? "x" : " ")}] {t.Text}")), "text" => b.Text, _ => $"[{b.Kind}: {b.FileName}]" }))));
}
