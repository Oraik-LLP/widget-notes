namespace tagme;

internal sealed record MarkdownSection(string Kind, string Text, List<Todo>? Tasks = null);
internal sealed record MarkdownNote(string Title, string? Source, List<MarkdownSection> Sections);

internal static class MarkdownImport
{
    const int MaxCharacters = 20 * 1024 * 1024;

    public static MarkdownNote Parse(string fileName, string markdown)
    {
        if (markdown.Length > MaxCharacters) throw new InvalidDataException("Markdown files must be 20 MB or smaller.");

        string? title = null, source = null;
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        if (lines.Count > 0 && lines[0].Trim() == "---")
        {
            int end = lines.FindIndex(1, line => line.Trim() is "---" or "...");
            if (end > 0)
            {
                foreach (string line in lines.Skip(1).Take(end - 1))
                {
                    int colon = line.IndexOf(':');
                    if (colon < 1) continue;
                    string key = line[..colon].Trim().ToLowerInvariant();
                    string value = Unquote(line[(colon + 1)..].Trim());
                    if (key == "title" && value.Length > 0) title = value;
                    if (key is "source" or "agent" or "provider" && value.Length > 0) source = value;
                }
                lines = lines.Skip(end + 1).ToList();
            }
        }

        string? detected = DetectSource(source, fileName);
        var sections = new List<MarkdownSection>();
        var prose = new List<string>();
        var tasks = new List<Todo>();
        bool inFence = false;
        foreach (string rawLine in lines)
        {
            string trimmed = rawLine.TrimStart();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inFence = !inFence;
                prose.Add(rawLine);
                continue;
            }
            if (!inFence && TryHeading(rawLine, out string heading) && (title == null || title.Equals(heading, StringComparison.OrdinalIgnoreCase)))
            {
                title ??= heading;
                continue;
            }
            if (!inFence && TryTask(rawLine, out bool done, out string task))
            {
                FlushProse();
                tasks.Add(new Todo { Text = task, Done = done });
            }
            else
            {
                FlushTasks();
                prose.Add(rawLine);
            }
        }
        FlushTasks(); FlushProse();

        string baseTitle = title ?? Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(baseTitle)) baseTitle = "Imported note";
        string pageTitle = detected == null || baseTitle.StartsWith(detected + " · ", StringComparison.OrdinalIgnoreCase)
            ? baseTitle : $"{detected} · {baseTitle}";
        if (sections.Count == 0) sections.Add(new MarkdownSection("text", ""));
        return new MarkdownNote(pageTitle, detected, sections);

        void FlushProse()
        {
            string text = string.Join("\n", prose).Trim();
            if (text.Length > 0) sections.Add(new MarkdownSection("text", text));
            prose.Clear();
        }
        void FlushTasks()
        {
            if (tasks.Count > 0) sections.Add(new MarkdownSection("checklist", "", tasks.ToList()));
            tasks.Clear();
        }
    }

    static string Unquote(string value) => value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')) ? value[1..^1] : value;

    static bool TryHeading(string line, out string value)
    {
        string trimmed = line.TrimStart();
        int count = trimmed.TakeWhile(c => c == '#').Count();
        value = count is >= 1 and <= 6 && trimmed.Length > count && char.IsWhiteSpace(trimmed[count]) ? trimmed[(count + 1)..].Trim() : "";
        return value.Length > 0;
    }

    static bool TryTask(string line, out bool done, out string task)
    {
        string value = line.TrimStart();
        done = false; task = "";
        if (value.Length < 6 || value[0] is not ('-' or '*' or '+') || !char.IsWhiteSpace(value[1]) || value[2] != '[' || value[4] != ']' || !char.IsWhiteSpace(value[5])) return false;
        done = value[3] is 'x' or 'X';
        if (value[3] is not (' ' or 'x' or 'X')) return false;
        task = value[6..].Trim();
        return task.Length > 0;
    }

    static string? DetectSource(string? source, string fileName)
    {
        string probe = (source + " " + fileName).ToLowerInvariant();
        if (probe.Contains("claude")) return "Claude";
        if (probe.Contains("gemini")) return "Gemini";
        if (probe.Contains("antigravity")) return "Antigravity";
        if (probe.Contains("chatgpt") || probe.Contains("openai") || probe.Contains("gpt")) return "GPT";
        if (probe.Contains("codex")) return "Codex";
        return string.IsNullOrWhiteSpace(source) ? null : source.Trim();
    }
}
