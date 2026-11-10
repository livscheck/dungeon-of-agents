using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

// Opens each agent's Claude Code session in the Claude app, reusing the project's existing session when there is one,
// so agents keep running on their own and the user talks to them directly. The game never owns the agent processes.
//   - a Claude app session for the project (open or closed, not archived)
//                                         -> brought up in the app (claude://code/continue?session=local_...)
//   - a background session (claude --bg)  -> a new terminal tab running "claude attach <id>"
//   - an interactive terminal session     -> reported (it is already in a terminal; switch to that tab)
//   - an ended terminal session saved in the project repo
//                                         -> imported into the app (claude://resume?session=<id>) and opened
//   - no session yet                      -> the briefing's first turn runs hidden, as a named session in the
//                                            project repo with the other project repos added, then that session is
//                                            imported into the app, so it shows in the sidebar under the repo with
//                                            the agent's first reply already there
// Only when the Claude app is not installed does a new session start in a Windows Terminal tab instead.
// A session belongs to a project when its folder is the project folder, or its name or folder carries the project
// number. Only the digits have to match, so "PROJ-000123" in an app session title finds PRJ-000123.
// DUNGEON_DRYRUN=1 prints the command instead of opening anything (used for testing).
public static class AgentLauncher
{
    public static bool DryRun => Environment.GetEnvironmentVariable("DUNGEON_DRYRUN") == "1";

    /// <param name="Entrypoint">"claude-desktop" for a session in the Claude app; otherwise the CLI's own value.</param>
    /// <param name="HostId">The Claude app's id for the session (local_...), empty for terminal sessions.</param>
    /// <param name="LastActivity">Unix milliseconds of the session's last activity, 0 when unknown.</param>
    public sealed record Session(string Id, string Name, string Kind, string Status, string Cwd, int Pid,
                                 string Entrypoint = "", string HostId = "", long LastActivity = 0)
    {
        public bool InApp => Entrypoint == "claude-desktop" && HostId.StartsWith("local_", StringComparison.Ordinal);
    }

    static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    static JsonDocument? ReadJson(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return JsonDocument.Parse(sr.ReadToEnd());
    }

    static string Str(JsonElement r, string k) =>
        r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static long Num(JsonElement r, string k) =>
        r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;

    /// <summary>
    /// The running Claude sessions, read straight from ~/.claude/sessions (instant). Asking "claude agents" for the
    /// same list started a whole new Claude process and took about two seconds.
    /// </summary>
    public static List<Session> ListSessions()
    {
        var list = new List<Session>();
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions");
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    using var doc = ReadJson(file);
                    if (doc == null) continue;
                    var r = doc.RootElement;
                    int pid = (int)Num(r, "pid");
                    if (pid <= 0 || !IsAlive(pid)) continue; // stale file from a session that has ended
                    if (TestPrep.IsPrepProcess(pid)) continue; // the game's own hidden test-prep run, not a session to open
                    list.Add(new Session(Str(r, "sessionId"), Str(r, "name"), Str(r, "kind"), Str(r, "status"), Str(r, "cwd"), pid,
                                         Str(r, "entrypoint"), Str(r, "hostSessionId"), Num(r, "updatedAt")));
                }
                catch { /* a half-written or unreadable file: skip it */ }
            }
        }
        catch { /* no sessions folder */ }
        return list;
    }

    /// <summary>
    /// Every Code session the Claude app knows about that is not archived, open or not, read from the app's own
    /// records (%APPDATA%\Claude\claude-code-sessions\...\local_*.json). The folder is the one the session was started
    /// in (originCwd), so a session the app moved into a worktree still matches the project repo.
    /// </summary>
    public static List<Session> ListAppSessions()
    {
        var list = new List<Session>();
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code-sessions");
            foreach (var file in Directory.EnumerateFiles(dir, "local_*.json", SearchOption.AllDirectories))
            {
                try
                {
                    using var doc = ReadJson(file);
                    if (doc == null) continue;
                    var r = doc.RootElement;
                    if (r.TryGetProperty("isArchived", out var a) && a.ValueKind == JsonValueKind.True) continue;
                    string hostId = Str(r, "sessionId");
                    if (!hostId.StartsWith("local_", StringComparison.Ordinal)) continue;
                    string cwd = Str(r, "originCwd");
                    if (cwd == "") cwd = Str(r, "cwd");
                    list.Add(new Session(Str(r, "cliSessionId"), Str(r, "title"), "app", "", cwd, 0,
                                         "claude-desktop", hostId, Num(r, "lastActivityAt")));
                }
                catch { /* a half-written or unreadable file: skip it */ }
            }
        }
        catch { /* the Claude app has no Code sessions folder */ }
        return list;
    }

    /// <summary>
    /// Running sessions plus the Claude app's saved ones. An app session that is also running keeps its live entry
    /// (it has a status), with the app's title when the live entry has no name.
    /// </summary>
    public static List<Session> AllSessions()
    {
        var live = ListSessions();
        var liveByHost = live.Where(s => s.HostId != "").GroupBy(s => s.HostId).ToDictionary(g => g.Key, g => g.First());
        var all = new List<Session>(live.Where(s => s.HostId == "" || !liveByHost.ContainsKey(s.HostId)));
        foreach (var l in liveByHost.Values) all.Add(l);
        foreach (var app in ListAppSessions())
        {
            if (liveByHost.TryGetValue(app.HostId, out var l))
            {
                all.Remove(l);
                all.Add(l with { Name = l.Name != "" ? l.Name : app.Name, Cwd = app.Cwd != "" ? app.Cwd : l.Cwd,
                                 LastActivity = Math.Max(l.LastActivity, app.LastActivity) });
            }
            else all.Add(app);
        }
        return all;
    }

    /// <summary>
    /// Start a named Claude session in a new terminal tab whose first message is <paramref name="prompt"/>. Used only
    /// when the Claude app is not installed. The prompt goes in a file, because a long prompt on a command line would
    /// be mangled by quoting.
    /// </summary>
    public static string LaunchPrompted(string tabTitle, string sessionName, string dir, string prompt, string promptId, string okMessage, IReadOnlyList<string>? extraDirs = null)
    {
        string promptFile = Path.Combine(Path.GetTempPath(), $"dungeon-prompt-{promptId}.txt");
        File.WriteAllText(promptFile, prompt);
        string start = dir != "" && Directory.Exists(dir) ? $"-d \"{dir}\" " : "";
        string cmd = $"& claude --name '{sessionName}' (Get-Content -Raw -LiteralPath '{promptFile}')";
        // The prompt comes before --add-dir, which takes a list and would otherwise swallow it.
        if (extraDirs != null) foreach (var d in extraDirs) cmd += $" --add-dir '{d}'";
        string args = $"new-tab --title \"{tabTitle}\" {start}powershell.exe -NoLogo -NoProfile -NoExit -Command \"{cmd}\"";
        if (DryRun) return $"DRYRUN wt.exe {args}";
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe", args) { UseShellExecute = true });
            return okMessage;
        }
        catch (Exception ex) { return $"Could not open a terminal: {ex.Message}"; }
    }

    /// <summary>
    /// The session that belongs to the project, or null. Best match first: the session's folder is the project
    /// folder, then the full number in its name or folder, then just the number's digits. Ties go to a Claude app
    /// session, then a background session, then a busy one, then the most recently active.
    /// </summary>
    public static Session? FindProjectSession(IEnumerable<Session> sessions, string number, string dir)
    {
        string digits = Regex.Match(number, @"\d{4,}").Value;
        string projectDir = NormalizeDir(dir);

        int Score(Session s)
        {
            if (projectDir != "" && NormalizeDir(s.Cwd) == projectDir) return 3;
            if (number == "") return 0;
            if (s.Name.Contains(number, StringComparison.OrdinalIgnoreCase)
             || s.Cwd.Contains(number, StringComparison.OrdinalIgnoreCase)) return 2;
            if (digits != "" && (Regex.IsMatch(s.Name, $@"(?<!\d){digits}(?!\d)")
                              || Regex.IsMatch(s.Cwd, $@"(?<!\d){digits}(?!\d)"))) return 1;
            return 0;
        }

        return sessions
            .Select(s => (s, score: Score(s)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => x.s.InApp)
            .ThenByDescending(x => x.s.Kind == "background")
            .ThenByDescending(x => x.s.Status == "busy")
            .ThenByDescending(x => x.s.LastActivity)
            .Select(x => x.s)
            .FirstOrDefault();
    }

    /// <summary>
    /// The Store build's launcher alias. It always reaches the installed Store app, while the registered claude://
    /// handler can point at another copy of the app that is not the one running.
    /// </summary>
    static string AppAlias => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                           "Microsoft", "WindowsApps", "claude-desktop.exe");

    static bool AppInstalled
    {
        get
        {
            if (File.Exists(AppAlias)) return true;
            if (!OperatingSystem.IsWindows()) return false;
            try { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\claude\shell\open\command"); return k != null; }
            catch { return false; }
        }
    }

    /// <summary>Hand a claude:// link to the Claude app: through the alias when present, else the registered handler.</summary>
    static void OpenInApp(string url)
    {
        if (File.Exists(AppAlias))
            Process.Start(new ProcessStartInfo(AppAlias, $"\"{url}\"") { UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>
    /// Claude Code keeps each folder's saved sessions in ~/.claude/projects/&lt;folder with every character that is
    /// not a letter or digit turned into '-'&gt;. The drive letter's case varies, so the name is compared ignoring case.
    /// </summary>
    static string? TranscriptFolder(string dir)
    {
        if (dir == "") return null;
        string slug = Regex.Replace(dir.TrimEnd('\\', '/'), "[^A-Za-z0-9]", "-");
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        try
        {
            return Directory.EnumerateDirectories(root)
                .FirstOrDefault(d => string.Equals(Path.GetFileName(d), slug, StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    /// <summary>The newest saved terminal session in <paramref name="dir"/> that is not running now, or null.</summary>
    public static string? LatestEndedCliSession(string dir, IEnumerable<Session> live)
    {
        var folder = TranscriptFolder(dir);
        if (folder == null) return null;
        var running = new HashSet<string>(live.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
        try
        {
            return Directory.EnumerateFiles(folder, "*.jsonl")
                .Select(f => (id: Path.GetFileNameWithoutExtension(f), time: File.GetLastWriteTimeUtc(f)))
                .Where(x => Guid.TryParse(x.id, out _) && !running.Contains(x.id))
                .OrderByDescending(x => x.time)
                .Select(x => x.id)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    static readonly HashSet<string> Creating = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Run the briefing's first turn hidden, as a named session in <paramref name="dir"/>, and return its id once
    /// the reply is saved. Null (with <paramref name="error"/>) when it could not run.
    /// </summary>
    static string? CreateCliSession(string sessionName, string dir, IReadOnlyList<string>? extraDirs, string prompt, out string error)
    {
        error = "";
        string id = Guid.NewGuid().ToString();
        var psi = new ProcessStartInfo("claude")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-p", prompt, "--session-id", id, "--name", sessionName, "--output-format", "json" })
            psi.ArgumentList.Add(a);
        // Last, because --add-dir takes a list and would swallow anything after it.
        var extras = (extraDirs ?? Array.Empty<string>()).Where(Directory.Exists).ToList();
        if (extras.Count > 0) { psi.ArgumentList.Add("--add-dir"); foreach (var d in extras) psi.ArgumentList.Add(d); }

        try
        {
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5 * 60 * 1000))
            {
                try { p.Kill(true); } catch { }
                error = "the first reply took longer than 5 minutes";
                return null;
            }
            if (p.ExitCode != 0)
            {
                string why = stderr.Result.Trim() != "" ? stderr.Result.Trim() : stdout.Result.Trim();
                error = why.Length > 200 ? why[..200] + "..." : why;
                return null;
            }
            return id;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    static string NormalizeDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return "";
        try { return Path.GetFullPath(dir).TrimEnd('\\', '/').ToLowerInvariant(); }
        catch { return dir.TrimEnd('\\', '/').ToLowerInvariant(); }
    }

    /// <summary>Bring up one Claude app session (by its local_... id). Returns a message for the HUD.</summary>
    public static string OpenAppSession(string hostId, string who, string title)
    {
        string url = $"claude://code/continue?session={Uri.EscapeDataString(hostId)}";
        if (DryRun) return $"DRYRUN open {url}";
        try
        {
            OpenInApp(url);
            return $"Opening {who}'s session in the Claude app ('{title}').";
        }
        catch (Exception ex) { return $"Could not open the Claude app: {ex.Message}"; }
    }

    /// <summary>
    /// Open (or start) the agent's session so the user can talk to it. Returns a message for the HUD; while a new
    /// session is being made (about half a minute) <paramref name="progress"/> gets an interim message.
    /// </summary>
    public static string Open(string agent, string number, string title, string status, string needs,
                              string plan, string notes, string testSummary, string dir, IReadOnlyList<string>? extraDirs = null,
                              Action<string>? progress = null)
    {
        bool app = AppInstalled;
        var live = ListSessions();
        var match = FindProjectSession(app ? AllSessions() : live, number, dir);

        if (match != null && match.InApp)
        {
            string url = $"claude://code/continue?session={Uri.EscapeDataString(match.HostId)}";
            if (DryRun) return $"DRYRUN open {url}";
            try
            {
                OpenInApp(url);
                return $"Opening {agent}'s session in the Claude app ('{match.Name}').";
            }
            catch (Exception ex) { return $"Could not open the Claude app: {ex.Message}"; }
        }
        if (match != null && match.Kind == "background")
        {
            string args = $"new-tab --title \"{agent}\" claude attach \"{match.Id}\"";
            if (DryRun) return $"DRYRUN wt.exe {args}";
            try
            {
                Process.Start(new ProcessStartInfo("wt.exe", args) { UseShellExecute = true });
                return $"Opening {agent}'s background session in a new terminal tab.";
            }
            catch (Exception ex) { return $"Could not open a terminal: {ex.Message}"; }
        }
        if (match != null)
            return $"{agent} is already open in a terminal ('{match.Name}', pid {match.Pid}). Switch to that tab to talk to them.";

        string prompt = Briefing.SessionPrompt(number, title, status, needs, plan, notes, testSummary, dir, VaultNotes.Dir);
        string sessionName = $"{agent} - {number}";
        if (app && dir != "" && Directory.Exists(dir))
        {
            string? ended = LatestEndedCliSession(dir, live);
            if (ended != null)
            {
                if (DryRun) return $"DRYRUN open claude://resume?session={ended}";
                try
                {
                    OpenInApp($"claude://resume?session={ended}");
                    return $"Moving {agent}'s earlier terminal session into the Claude app.";
                }
                catch (Exception ex) { return $"Could not open the Claude app: {ex.Message}"; }
            }

            lock (Creating)
            {
                if (!Creating.Add(number)) return $"{agent}'s session is still being set up. It opens in the Claude app when ready.";
            }
            try
            {
                if (DryRun)
                    return $"DRYRUN claude -p <briefing> --name \"{sessionName}\" (in {dir}"
                         + (extraDirs is { Count: > 0 } ? $", --add-dir {string.Join(", ", extraDirs)}" : "")
                         + ") then open claude://resume?session=<id>";
                progress?.Invoke($"Setting up {agent}'s session in {Path.GetFileName(dir)}. It opens in the Claude app in about half a minute.");
                string? id = CreateCliSession(sessionName, dir, extraDirs, prompt, out string error);
                if (id == null) return $"Could not start {agent}'s session: {error}";
                OpenInApp($"claude://resume?session={id}");
                return $"{agent}'s session is ready in the Claude app, with the first reply waiting.";
            }
            catch (Exception ex) { return $"Could not open the Claude app: {ex.Message}"; }
            finally { lock (Creating) Creating.Remove(number); }
        }

        return LaunchPrompted(agent, sessionName, dir, prompt,
            $"briefing-{number}",
            $"Started {agent}'s session in the project repo ({Path.GetFileName(dir)}). It opens with the briefing already loaded.",
            extraDirs);
    }
}
