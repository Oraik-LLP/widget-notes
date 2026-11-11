using System.IO;
using System.IO.Compression;
using System.Text;

namespace tagme;

public sealed class NotebookStore
{
    readonly SemaphoreSlim gate = new(1, 1);
    public string Folder { get; }
    public Notebook Book { get; private set; }
    public bool Recovered { get; private set; }
    public NotebookStore(string folder, Notebook book) { Folder = Path.GetFullPath(folder); Book = book; }
    public void ReplaceBook(Notebook book) { Validate(book); Book = book; }

    public static async Task<NotebookStore> Open(string folder)
    {
        string file = Path.Combine(folder, "notebook.json");
        Notebook book; bool recovered = false;
        try { book = await Read(file); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            // A future format must never be silently replaced by an older backup.
            if (ex is UnsupportedVersionException) throw;
            if (!File.Exists(file + ".bak")) throw new InvalidDataException("This notebook cannot be read and has no usable backup.", ex);
            book = await Read(file + ".bak"); recovered = true;
            File.Copy(file, file + ".damaged", true);
            File.Copy(file + ".bak", file, true);
        }
        return new(folder, book) { Recovered = recovered };
    }
    static async Task<Notebook> Read(string file)
    {
        var book = Json.Read<Notebook>(await File.ReadAllTextAsync(file)); Validate(book); return book;
    }
    public static void Validate(Notebook b)
    {
        if (b.Version != 1) throw new UnsupportedVersionException("This notebook version is unsupported. Open it with a compatible tagme version.");
        if (string.IsNullOrWhiteSpace(b.Id) || b.Pages is null || b.Pages.Count == 0 || b.Pages.Count > 10000) throw new InvalidDataException("Invalid notebook pages.");
        var ids = new HashSet<string>();
        void Id(string id) { if (string.IsNullOrEmpty(id) || !ids.Add(id)) throw new InvalidDataException("Missing or duplicate document identifier."); }
        foreach (var p in b.Pages)
        {
            Id(p.Id); if (p.Blocks is null) throw new InvalidDataException("Invalid blocks.");
            foreach (var block in p.Blocks)
            {
                Id(block.Id);
                if (block.Kind is not ("text" or "checklist" or "image" or "file") || block.Items is null || !double.IsFinite(block.X) || !double.IsFinite(block.Y) || !double.IsFinite(block.Width) || !double.IsFinite(block.Height) || block.X < 0 || block.Y < 0 || block.X > 100000 || block.Y > 100000 || block.Width < 120 || block.Height < 100 || block.Width > 10000 || block.Height > 10000) throw new InvalidDataException("Invalid canvas block.");
                foreach (var t in block.Items) Id(t.Id);
                if (block.Kind is "image" or "file")
                    if (block.Media is null || !block.Media.StartsWith("media/", StringComparison.Ordinal) || block.Media.Contains("..") || block.Media.Contains('\\') || Path.IsPathRooted(block.Media)) throw new InvalidDataException("Invalid attachment path.");
            }
        }
    }
    public Task Save(string snapshot) => Task.Run(async () =>
    {
        await gate.WaitAsync();
        try { Directory.CreateDirectory(Path.Combine(Folder, "media")); AtomicWrite(Path.Combine(Folder, "notebook.json"), snapshot); }
        finally { gate.Release(); }
    });
    public static void AtomicWrite(string file, string text)
    {
        string temp = file + ".tmp";
        using (var f = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
        { var data = Encoding.UTF8.GetBytes(text); f.Write(data); f.Flush(true); }
        if (File.Exists(file)) File.Replace(temp, file, file + ".bak", true); else File.Move(temp, file);
    }
    public async Task<string> ImportMedia(string source)
    {
        string ext = Path.GetExtension(source); if (ext.Length > 20) ext = ".bin";
        string relative = "media/" + Guid.NewGuid().ToString("N") + ext;
        await gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.Combine(Folder, "media"));
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            await using var output = new FileStream(Path.Combine(Folder, relative), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await input.CopyToAsync(output);
        }
        finally { gate.Release(); }
        return relative;
    }
    public async Task Export(string target, string snapshot)
    {
        await Save(snapshot);
        await gate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
                    {
                        zip.CreateEntryFromFile(Path.Combine(Folder, "notebook.json"), "notebook.json");
                        var book = Json.Read<Notebook>(snapshot);
                        foreach (var media in book.Pages.SelectMany(p => p.Blocks).Where(b => b.Media != null).Select(b => b.Media!).Distinct()) zip.CreateEntryFromFile(Path.Combine(Folder, media), media, CompressionLevel.Fastest);
                    }
                    File.Move(temp, target, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            });
        }
        finally { gate.Release(); }
    }
    public static async Task<NotebookStore> Import(string archive, string root)
    {
        string folder = Path.Combine(root, "Imported-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Task.Run(() =>
            {
                using var zip = ZipFile.OpenRead(archive);
                if (zip.Entries.Count > 20000 || zip.Entries.Sum(e => e.Length) > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("Notebook exceeds the 4 GB import limit.");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in zip.Entries)
                {
                    var name = e.FullName;
                    if (name.Contains('\\') || name.Contains(':') || name.Split('/').Any(s => s is ".." or ".") || !(name == "notebook.json" || name.StartsWith("media/", StringComparison.Ordinal)) || !names.Add(name)) throw new InvalidDataException("Unsafe or duplicate archive entry.");
                    var path = Path.GetFullPath(Path.Combine(folder, name));
                    if (!path.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe archive path.");
                }
                var entry = zip.GetEntry("notebook.json") ?? throw new InvalidDataException("Notebook data is missing.");
                if (entry.Length > 32 * 1024 * 1024) throw new InvalidDataException("Notebook metadata is too large.");
                using var reader = new StreamReader(entry.Open()); var book = Json.Read<Notebook>(reader.ReadToEnd()); Validate(book);
                foreach (var block in book.Pages.SelectMany(p => p.Blocks).Where(b => b.Media != null))
                    if (!names.Contains(block.Media!)) throw new InvalidDataException("An attachment is missing from the archive.");
                Directory.CreateDirectory(folder);
                foreach (var e in zip.Entries)
                {
                    if (e.FullName.EndsWith('/')) continue;
                    string path = Path.Combine(folder, e.FullName); Directory.CreateDirectory(Path.GetDirectoryName(path)!); e.ExtractToFile(path);
                }
            });
            return await Open(folder);
        }
        catch { if (Directory.Exists(folder)) Directory.Delete(folder, true); throw; }
    }
}
public sealed class UnsupportedVersionException(string message) : IOException(message);
