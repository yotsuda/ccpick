using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace CcPick;

internal static partial class Program
{
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static string ProjectsRoot => Path.Combine(Home, ".claude", "projects");
    static string CachePath => Path.Combine(Home, ".claude", "ccpick-cache.json");
    // User-assigned title overrides (id -> title). Independent of the mtime
    // cache, so a custom name survives session re-scans.
    static string TitlesPath => Path.Combine(Home, ".claude", "ccpick-titles.json");

    static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* stdout redirected */ }

        var cmd = args.Length > 0 ? args[0] : "pick";
        return cmd switch
        {
            "list" => CmdList(),
            "rows" => CmdRows(),
            "show" => CmdShow(args.Length > 1 ? args[1] : ""),
            "rename" => CmdRename(args),
            // `name [text]` is shorthand for `rename last [text]` — name the
            // session you just exited without typing its id or "last".
            "name" => CmdRename(new[] { "rename", "last" }.Concat(args.Skip(1)).ToArray()),
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
        Console.WriteLine("  ccpick                     open the fzf picker, then claude --resume the chosen session");
        Console.WriteLine("                             (Ctrl-E in the picker renames the selected session)");
        Console.WriteLine("  ccpick list                print one row per session: date  [folder]  title");
        Console.WriteLine("  ccpick show <id>           print a one-session preview block");
        Console.WriteLine("  ccpick name <text>         name the most recent session (the one you just exited)");
        Console.WriteLine("  ccpick rename <id> <text>  set a custom title (omit <text> to enter it interactively)");
        Console.WriteLine("  ccpick rename <id> --clear reset to the auto-generated title");
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

        var titles = ReadTitles();
        return rows
            .OrderByDescending(s => s.Mtime)
            .Select(s => titles.TryGetValue(s.Id, out var ov) && !string.IsNullOrWhiteSpace(ov)
                ? s with { Title = ov }
                : s)
            .ToList();
    }

    // ---- title overrides ----

    static Dictionary<string, string> ReadTitles()
    {
        try
        {
            if (File.Exists(TitlesPath))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(TitlesPath)) ?? new();
        }
        catch { /* corrupt -> ignore */ }
        return new();
    }

    static void WriteTitles(Dictionary<string, string> t)
    {
        try { File.WriteAllText(TitlesPath, JsonSerializer.Serialize(t, new JsonSerializerOptions { WriteIndented = true })); }
        catch { /* best effort */ }
    }

    // Current displayed title for one id: override, else cache, else scan.
    static string ResolveTitle(string id)
    {
        var titles = ReadTitles();
        if (titles.TryGetValue(id, out var ov) && !string.IsNullOrWhiteSpace(ov)) return ov;
        var cache = ReadCache();
        if (cache.TryGetValue(id, out var hit)) return hit.Title;
        var path = FindSessionFile(id);
        return path is not null ? Extract(path).title : "(unknown)";
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
    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")] private static partial Regex GuidRx();

    // fzf placeholders can hand us the bare id, the whole "<id>\t<visible>" row,
    // or a quoted/CR-wrapped variant depending on --with-nth and the OS shell.
    // Pull the session GUID out of whatever we get.
    static string ExtractId(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var m = GuidRx().Match(s);
        return m.Success ? m.Value : s.Trim();
    }

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

    // The visible part of a row, space-separated so columns sit tight (a TAB
    // here would render at 8-col tab stops and leave a big gap after the date).
    static string Visible(Session s, bool withFolder)
    {
        var date = s.Mtime.ToString("yyyy-MM-dd HH:mm");
        var t = s.Title.Length > 90 ? s.Title[..90] : s.Title;
        return withFolder ? $"{date}  {CwdLabel(s.Cwd)}  {t}" : $"{date}  {t}";
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
        foreach (var s in sessions) Console.WriteLine(Visible(s, wf));
        return 0;
    }

    // Internal: the exact "<id>\t<visible>" lines fzf consumes. Used by the
    // picker's reload after a rename.
    static int CmdRows()
    {
        var sessions = GetSessions();
        var wf = MultiFolder(sessions);
        foreach (var s in sessions) Console.WriteLine($"{s.Id}\t{Visible(s, wf)}");
        return 0;
    }

    static int CmdRename(string[] args)
    {
        if (args.Length < 2) return Fail("usage: ccpick rename <id|last> [new title | --clear]");
        var id = args[1];
        // "last" = the most recently active session (e.g. the one you just
        // exited), so you can name it without copying its GUID.
        if (string.Equals(id, "last", StringComparison.OrdinalIgnoreCase))
        {
            var latest = GetSessions().FirstOrDefault();
            if (latest is null) return Fail("no sessions found.");
            id = latest.Id;
        }
        var titles = ReadTitles();

        if (args.Length >= 3 && args[2] == "--clear")
        {
            titles.Remove(id);
            WriteTitles(titles);
            Console.WriteLine("title reset to auto.");
            return 0;
        }

        if (args.Length >= 3)
        {
            titles[id] = string.Join(' ', args.Skip(2)).Trim();
            WriteTitles(titles);
            Console.WriteLine("renamed.");
        }
        else
        {
            PromptRename(id);
        }
        return 0;
    }

    // Interactive rename used by both `ccpick rename <id>` and the picker's
    // Ctrl-E. Runs in the ccpick process, so Console input is reliable.
    static void PromptRename(string id)
    {
        // fzf leaves the Windows console in raw-input mode; without restoring
        // line/echo input here, Console.ReadLine() returns empty and the rename
        // silently does nothing. Reset the mode and refresh the input reader.
        ResetConsoleInput();

        Console.WriteLine();
        Console.WriteLine($"current: {ResolveTitle(id)}");
        Console.Write("new title (Enter to cancel, '-' to reset to auto): ");
        var nt = (Console.ReadLine() ?? "").Trim();
        var titles = ReadTitles();
        if (nt == "-")
        {
            titles.Remove(id);
            WriteTitles(titles);
            Console.WriteLine("title reset to auto.");
        }
        else if (nt.Length > 0)
        {
            titles[id] = nt;
            WriteTitles(titles);
            Console.WriteLine("renamed.");
        }
        else
        {
            Console.WriteLine("cancelled.");
        }
    }

    static int CmdShow(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) return Fail("usage: ccpick show <id>");
        var id = ExtractId(arg);

        string? cwd;
        var cache = ReadCache();
        if (cache.TryGetValue(id, out var hit)) { cwd = hit.Cwd; }
        else
        {
            var path = FindSessionFile(id);
            if (path is null) { Console.WriteLine($"session not found: {id}"); return 0; }
            (cwd, _) = Extract(path);
        }

        Console.WriteLine($"id   : {id}");
        Console.WriteLine($"cwd  : {cwd}");
        Console.WriteLine();
        Console.WriteLine(ResolveTitle(id));
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

        while (true)
        {
            var sessions = GetSessions();
            if (sessions.Count == 0) { Console.WriteLine("No sessions found."); return 0; }

            var wf = MultiFolder(sessions);
            var sb = new StringBuilder();
            // "<id>\t<visible>": id (field 1) is hidden from the display but kept
            // for the preview ({1}) and for resuming / renaming the choice.
            foreach (var s in sessions) sb.Append(s.Id).Append('\t').Append(Visible(s, wf)).Append('\n');

            var psi = new ProcessStartInfo("fzf")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                StandardInputEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var a in new[]
            {
                "--ansi", "--delimiter", "\t", "--with-nth", "2",
                // Pass the whole row ({}) — with --with-nth, {1} resolves to the
                // visible field, not the id. ccpick show digs the GUID out.
                "--preview", "ccpick show {}", "--preview-window", "right:45%:wrap",
                // We capture Ctrl-E ourselves: fzf reports the pressed key on the
                // first output line, and the rename prompt then runs in THIS
                // process — which reliably owns the terminal, unlike an
                // execute() child (flaky for interactive input on Windows).
                "--expect", "ctrl-e",
                "--header", "Enter: resume   Ctrl-E: rename   Esc: cancel",
            }) psi.ArgumentList.Add(a);

            string outp;
            using (var p = Process.Start(psi)!)
            {
                p.StandardInput.Write(sb.ToString());
                p.StandardInput.Close();
                outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
            }

            // --expect output: line 0 = key (empty when Enter), line 1 = row.
            var lines = outp.Split('\n');
            var key = lines.Length > 0 ? lines[0].Trim() : "";
            var sel = lines.Skip(1).FirstOrDefault(l => l.Trim().Length > 0);
            if (string.IsNullOrEmpty(sel)) return 0; // Esc / nothing chosen

            var id = ExtractId(sel);

            if (key == "ctrl-e")
            {
                PromptRename(id);
                continue; // re-open the picker so the new title shows
            }

            Console.WriteLine($"claude --resume {id}");
            var cp = new ProcessStartInfo("claude") { UseShellExecute = false };
            cp.ArgumentList.Add("--resume");
            cp.ArgumentList.Add(id);
            try { Process.Start(cp)?.WaitForExit(); }
            catch (Exception ex) { return Fail($"failed to launch claude: {ex.Message}"); }
            return 0;
        }
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

    // ---- console input recovery (Windows) ----

    const int STD_INPUT_HANDLE = -10;
    const uint ENABLE_PROCESSED_INPUT = 0x0001;
    const uint ENABLE_LINE_INPUT = 0x0002;
    const uint ENABLE_ECHO_INPUT = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    // After fzf exits it may leave stdin in raw mode; re-enable cooked line
    // input so Console.ReadLine() works, and refresh the cached input reader.
    static void ResetConsoleInput()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var h = GetStdHandle(STD_INPUT_HANDLE);
                if (GetConsoleMode(h, out var mode))
                    SetConsoleMode(h, mode | ENABLE_PROCESSED_INPUT | ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT);
            }
            Console.InputEncoding = Encoding.UTF8; // recreates Console.In
        }
        catch { /* best effort */ }
    }
}
