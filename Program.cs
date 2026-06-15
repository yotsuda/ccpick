using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace CcPick;

internal static partial class Program
{
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static string ProjectsRoot => Path.Combine(Home, ".claude", "projects");
    static string CachePath => Path.Combine(Home, ".claude", "ccpick-cache.json");

    static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* stdout redirected */ }

        var cmd = args.Length > 0 ? args[0] : "pick";
        return cmd switch
        {
            "list" => CmdList(),
            "show" => CmdShow(args.Length > 1 ? args[1] : ""),
            "pick" => CmdPick(),
            "-h" or "--help" or "help" => CmdHelp(),
            _ => Fail($"unknown command: {cmd}")
        };
    }

    static int Fail(string msg) { Console.Error.WriteLine(msg); return 1; }

    static int CmdHelp()
    {
        Console.WriteLine("ccpick - Claude Code session picker (fzf-based)");
        Console.WriteLine();
        Console.WriteLine("  ccpick            open the fzf picker, then claude --resume the chosen session");
        Console.WriteLine("  ccpick list       print one TAB-separated row per session: id<TAB>date<TAB>cwd<TAB>title");
        Console.WriteLine("  ccpick show <id>  print a one-session preview block");
        return 0;
    }

    // ---- session model ----

    sealed record Session(string Id, DateTime Mtime, string? Cwd, string Title);

    sealed class CacheEntry
    {
        public long Mtime { get; set; }
        public string? Cwd { get; set; }
        public string Title { get; set; } = "";
    }

    static List<Session> GetSessions()
    {
        var files = new List<FileInfo>();
        if (Directory.Exists(ProjectsRoot))
        {
            // Top-level <guid>.jsonl per slug dir only; subagents/ logs live in
            // subfolders and are intentionally skipped (non-recursive).
            foreach (var dir in Directory.EnumerateDirectories(ProjectsRoot))
                foreach (var f in Directory.EnumerateFiles(dir, "*.jsonl"))
                    files.Add(new FileInfo(f));
        }

        var cache = ReadCache();
        var rows = new List<Session>();
        var stale = new List<FileInfo>();

        foreach (var f in files)
        {
            var id = Path.GetFileNameWithoutExtension(f.Name);
            if (cache.TryGetValue(id, out var hit) && hit.Mtime == f.LastWriteTimeUtc.Ticks)
                rows.Add(new Session(id, f.LastWriteTime, hit.Cwd, hit.Title));
            else
                stale.Add(f);
        }

        if (stale.Count > 0)
        {
            var scanned = new ConcurrentBag<(string id, FileInfo f, string? cwd, string title)>();
            Parallel.ForEach(stale, new ParallelOptions { MaxDegreeOfParallelism = 8 }, f =>
            {
                var (cwd, title) = Extract(f.FullName);
                scanned.Add((Path.GetFileNameWithoutExtension(f.Name), f, cwd, title));
            });

            foreach (var s in scanned)
            {
                cache[s.id] = new CacheEntry { Mtime = s.f.LastWriteTimeUtc.Ticks, Cwd = s.cwd, Title = s.title };
                rows.Add(new Session(s.id, s.f.LastWriteTime, s.cwd, s.title));
            }

            var live = files.Select(f => Path.GetFileNameWithoutExtension(f.Name)).ToHashSet();
            foreach (var dead in cache.Keys.Where(k => !live.Contains(k)).ToList())
                cache.Remove(dead);
            WriteCache(cache);
        }

        return rows.OrderByDescending(s => s.Mtime).ToList();
    }

    // Read up to 60 lines; pull the first cwd and the first usable title
    // (a `summary` line, else the first real user prompt).
    static (string? cwd, string title) Extract(string path)
    {
        string? cwd = null, title = null;
        int count = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (++count > 60) break;
            if (line.Length < 2) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                if (cwd is null && root.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String)
                    cwd = cwdEl.GetString();

                if (title is null)
                {
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type == "summary" && root.TryGetProperty("summary", out var sEl) && sEl.ValueKind == JsonValueKind.String)
                    {
                        title = sEl.GetString();
                    }
                    else if (type == "user" && !(root.TryGetProperty("isMeta", out var im) && im.ValueKind == JsonValueKind.True))
                    {
                        var text = ExtractUserText(root);
                        if (text is not null)
                        {
                            text = CleanTitle(text);
                            if (text.Length > 3 && !text.StartsWith("Caveat:", StringComparison.Ordinal))
                                title = text;
                        }
                    }
                }
            }
            if (title is not null && cwd is not null) break;
        }

        title ??= "(no prompt)";
        if (title.Length > 300) title = title[..300];
        return (cwd, title);
    }

    static string? ExtractUserText(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return null;
        if (!msg.TryGetProperty("content", out var content)) return null;

        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();

        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("type", out var ty) && ty.GetString() == "text"
                    && item.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                    return tx.GetString();
            }
        }
        return null;
    }

    static string CleanTitle(string s)
    {
        s = TagRx().Replace(s, " ");
        s = WsRx().Replace(s, " ");
        return s.Trim();
    }

    [GeneratedRegex("<[^>]+>")] private static partial Regex TagRx();
    [GeneratedRegex("\\s+")] private static partial Regex WsRx();

    static string LeafOf(string p)
    {
        p = p.TrimEnd('/', '\\');
        int i = p.LastIndexOfAny(new[] { '/', '\\' });
        return i >= 0 ? p[(i + 1)..] : p;
    }

    // The folder column distinguishes sessions run from different project dirs.
    // For sessions started in the home dir its leaf is just the username, which
    // is noise when most sessions live there — collapse those to "~".
    static string CwdLabel(string? cwd)
    {
        if (cwd is null) return "?";
        var norm = cwd.TrimEnd('/', '\\');
        if (string.Equals(norm, Home.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase))
            return "~";
        return LeafOf(cwd);
    }

    // Show the folder column only when sessions actually span >1 folder;
    // otherwise (e.g. everything started from ~) it's a constant and just noise.
    static bool MultiFolder(IEnumerable<Session> ss) =>
        ss.Select(s => CwdLabel(s.Cwd)).Distinct().Count() > 1;

    static string Row(Session s, bool withFolder)
    {
        var date = s.Mtime.ToString("yyyy-MM-dd HH:mm");
        var t = s.Title.Length > 90 ? s.Title[..90] : s.Title;
        return withFolder
            ? $"{s.Id}\t{date}\t{CwdLabel(s.Cwd)}\t{t}"
            : $"{s.Id}\t{date}\t{t}";
    }

    // ---- cache ----

    static Dictionary<string, CacheEntry> ReadCache()
    {
        try
        {
            if (File.Exists(CachePath))
                return JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(CachePath))
                       ?? new();
        }
        catch { /* corrupt cache -> rebuild */ }
        return new();
    }

    static void WriteCache(Dictionary<string, CacheEntry> c)
    {
        try { File.WriteAllText(CachePath, JsonSerializer.Serialize(c)); }
        catch { /* best effort */ }
    }

    // ---- commands ----

    static int CmdList()
    {
        var sessions = GetSessions();
        var wf = MultiFolder(sessions);
        foreach (var s in sessions) Console.WriteLine(Row(s, wf));
        return 0;
    }

    static int CmdShow(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return Fail("usage: ccpick show <id>");

        string? cwd; string title;
        var cache = ReadCache();
        if (cache.TryGetValue(id, out var hit)) { cwd = hit.Cwd; title = hit.Title; }
        else
        {
            var path = FindSessionFile(id);
            if (path is null) { Console.WriteLine($"session not found: {id}"); return 0; }
            (cwd, title) = Extract(path);
        }

        Console.WriteLine($"id   : {id}");
        Console.WriteLine($"cwd  : {cwd}");
        Console.WriteLine();
        Console.WriteLine(title);
        return 0;
    }

    static string? FindSessionFile(string id)
    {
        if (!Directory.Exists(ProjectsRoot)) return null;
        foreach (var dir in Directory.EnumerateDirectories(ProjectsRoot))
        {
            var p = Path.Combine(dir, id + ".jsonl");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    static int CmdPick()
    {
        if (!ExistsOnPath("fzf"))
            return Fail("fzf not found. Install it: winget install fzf  (or: brew install fzf / apt install fzf)");

        var sessions = GetSessions();
        if (sessions.Count == 0) { Console.WriteLine("No sessions found."); return 0; }

        var wf = MultiFolder(sessions);
        var sb = new StringBuilder();
        foreach (var s in sessions) sb.Append(Row(s, wf)).Append('\n');

        var psi = new ProcessStartInfo("fzf")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        // col 1 is the (hidden) id; remaining display cols depend on whether the
        // folder column is present.
        var withNth = wf ? "2,3,4" : "2,3";
        foreach (var a in new[]
        {
            "--ansi", "--delimiter", "\t", "--with-nth", withNth,
            // `show` is now a fast exe, so a live preview is affordable again.
            "--preview", "ccpick show {1}", "--preview-window", "right:45%:wrap",
            "--header", "Enter: resume   Esc: cancel   (type to fuzzy-filter)",
        }) psi.ArgumentList.Add(a);

        string choice;
        using (var p = Process.Start(psi)!)
        {
            p.StandardInput.Write(sb.ToString());
            p.StandardInput.Close();
            choice = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
        }

        var line = choice.Split('\n').FirstOrDefault(l => l.Trim().Length > 0);
        if (string.IsNullOrEmpty(line)) return 0; // cancelled (Esc)

        var id = line.Split('\t')[0].Trim();
        Console.WriteLine($"claude --resume {id}");

        var cp = new ProcessStartInfo("claude") { UseShellExecute = false };
        cp.ArgumentList.Add("--resume");
        cp.ArgumentList.Add(id);
        try { Process.Start(cp)?.WaitForExit(); }
        catch (Exception ex) { return Fail($"failed to launch claude: {ex.Message}"); }
        return 0;
    }

    static bool ExistsOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var exts = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat", "" }
            : new[] { "" };
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var ext in exts)
            {
                try { if (File.Exists(Path.Combine(dir, exe + ext))) return true; }
                catch { /* bad PATH entry */ }
            }
        }
        return false;
    }
}
