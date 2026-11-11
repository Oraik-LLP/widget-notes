using System.IO.Compression;

namespace tagme;

public static class SelfTest
{
    public static int Run(string[] args)
    {
        string root = args.FirstOrDefault(a => a.StartsWith("--test-root="))?[12..] ?? Path.Combine(Path.GetTempPath(), "tagmeTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var log = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); log.Add("PASS " + name); }
        async Task Reject(Func<Task> action, string name) { try { await action(); } catch { log.Add("PASS " + name); return; } throw new Exception(name); }
        try
        {
            Task.Run(async () =>
            {
                var book = new Notebook(); var block = book.Pages[0].Blocks[0]; block.Items = Editing.Lines("one\r\ntwo\n\nthree");
                Check(block.Items.Count == 3 && block.Items[2].Text == "three", "multiline paste normalization");
                var markdown = MarkdownImport.Parse("claude-answer.md", "---\ntitle: Plan\nsource: Claude\n---\n# Plan\n\n- [ ] Ship\n- [x] Review\n\n```md\n- [ ] sample\n```");
                Check(markdown.Title == "Claude · Plan" && markdown.Sections.SelectMany(s => s.Tasks ?? []).Count() == 2 && markdown.Sections.Any(s => s.Text.Contains("sample")), "agent Markdown import and fenced task preservation");
                var pref = new Preferences(); Check(!Editing.RequiresConfirmation(block, pref), "completion confirmation default off"); pref.ConfirmComplete = true; Check(Editing.RequiresConfirmation(block, pref), "global confirmation"); block.ConfirmCompletion = false; Check(!Editing.RequiresConfirmation(block, pref), "per-list override"); block.ConfirmCompletion = null; Check(Editing.RequiresConfirmation(block, pref), "per-list inheritance");
                var store = new NotebookStore(Path.Combine(root, "original"), book); await store.Save(Json.Write(book)); book.Title = "Project A"; await store.Save(Json.Write(book));
                Check(File.Exists(Path.Combine(store.Folder, "notebook.json.bak")), "last-good backup created");
                var open = await NotebookStore.Open(store.Folder); Check(open.Book.Title == "Project A", "save and reopen");
                await File.WriteAllTextAsync(Path.Combine(store.Folder, "notebook.json.tmp"), "interrupted"); open = await NotebookStore.Open(store.Folder); Check(open.Book.Title == "Project A", "interrupted temp save preserves committed data");
                string source = Path.Combine(root, "sample.bin"); await File.WriteAllBytesAsync(source, [1, 2, 3, 4, 5]); string media = await store.ImportMedia(source); book.Pages[0].Blocks.Add(new() { Kind = "file", Media = media, FileName = "sample.bin", X = 450 });
                string archive = Path.Combine(root, "project.wnote"); await store.Export(archive, Json.Write(book)); var imported = await NotebookStore.Import(archive, Path.Combine(root, "imports"));
                Check(imported.Book.Title == book.Title && imported.Book.Pages[0].Blocks.Count == 2, "portable notebook round trip");
                Check(File.ReadAllBytes(Path.Combine(imported.Folder, media)).SequenceEqual(new byte[] { 1, 2, 3, 4, 5 }), "portable attachments preserved byte-for-byte");
                Check(Editing.PlainText(book).Contains("[ ] one") && Editing.PlainText(book).Contains("sample.bin"), "text export includes tasks and attachments");
                var many = new List<Task>(); for (int i = 0; i < 12; i++) { book.Title = "Revision " + i; many.Add(store.Save(Json.Write(book))); } await Task.WhenAll(many); open = await NotebookStore.Open(store.Folder); Check(open.Book.Title.StartsWith("Revision "), "concurrent saves produce valid JSON");
                await File.WriteAllTextAsync(Path.Combine(store.Folder, "notebook.json"), "broken json"); open = await NotebookStore.Open(store.Folder); Check(open.Recovered && File.Exists(Path.Combine(store.Folder, "notebook.json.damaged")), "corruption recovers backup and preserves damaged file");
                string future = Path.Combine(root, "future"); Directory.CreateDirectory(future); var v2 = new Notebook { Version = 999 }; File.WriteAllText(Path.Combine(future, "notebook.json"), Json.Write(v2)); File.WriteAllText(Path.Combine(future, "notebook.json.bak"), Json.Write(new Notebook()));
                await Reject(async () => await NotebookStore.Open(future), "future version rejected even with old backup"); Check(File.ReadAllText(Path.Combine(future, "notebook.json")).Contains("999"), "future notebook not overwritten");
                string corrupt = Path.Combine(root, "corrupt.wnote"); File.WriteAllText(corrupt, "not a zip"); await Reject(async () => await NotebookStore.Import(corrupt, root), "corrupt archive rejected");
                string evil = Path.Combine(root, "unsafe.wnote"); using (var zip = ZipFile.Open(evil, ZipArchiveMode.Create)) { var e = zip.CreateEntry("../escaped.txt"); using var writer = new StreamWriter(e.Open()); writer.Write("bad"); }
                await Reject(async () => await NotebookStore.Import(evil, root), "archive traversal rejected"); Check(!File.Exists(Path.Combine(root, "escaped.txt")), "archive traversal writes no escaped file");
                string missing = Path.Combine(root, "missing.wnote"); using (var zip = ZipFile.Open(missing, ZipArchiveMode.Create)) { var e = zip.CreateEntry("notebook.json"); using var writer = new StreamWriter(e.Open()); writer.Write(Json.Write(book)); }
                await Reject(async () => await NotebookStore.Import(missing, root), "missing attachment rejected");
                string locked = Path.Combine(root, "blocked"); File.WriteAllText(locked, "occupied"); var failed = new NotebookStore(locked, new Notebook()); await Reject(() => failed.Save(Json.Write(failed.Book)), "unavailable folder returns save failure"); Check(failed.Book.Pages.Count == 1, "failed save retains in-memory document");
                var invalid = new Notebook(); invalid.Pages[0].Blocks[0].X = double.NaN; await Reject(() => { NotebookStore.Validate(invalid); return Task.CompletedTask; }, "non-finite geometry rejected");
                invalid = new(); invalid.Pages[0].Blocks.Add(new() { Id = invalid.Pages[0].Blocks[0].Id }); await Reject(() => { NotebookStore.Validate(invalid); return Task.CompletedTask; }, "duplicate identifiers rejected");
                var shortcut = Desktop.ParseShortcut("Ctrl+Alt+N"); Check(shortcut.Key == 0x4e && (shortcut.Mods & 3) == 3, "shortcut parser");
                log.Add("ALL TESTS PASSED");
            }).GetAwaiter().GetResult();
            File.WriteAllLines(Path.Combine(root, "self-test.txt"), log); return 0;
        }
        catch (Exception ex) { log.Add("FAIL " + ex); File.WriteAllLines(Path.Combine(root, "self-test.txt"), log); return 1; }
    }
}
