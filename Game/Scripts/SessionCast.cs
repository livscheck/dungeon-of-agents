using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

// Gives every Claude app Code session a character in the dungeon, unless the user has said not to.
//   - Sessions come from the app's own records (%APPDATA%\Claude\claude-code-sessions\...\local_*.json); archived
//     ones are left out. Whether a session is mid-turn comes from the running-session files (~/.claude/sessions).
//   - A session that already belongs to a project agent (Sahir's, Vale's...) gets no second character.
//   - Hidden sessions are listed in data/hidden-sessions.json. The game adds to it when the user sends a character
//     away; the user (or Claude, when asked) can also edit it by hand. Removing an entry brings the character back.
//   - Each session keeps the same character from run to run (data/session-characters.json).
// What a character shows comes from the session itself:
//   busy                                      -> Working (walks, torch lit)
//   last turn blocked, or it needs something  -> AwaitingUser (struck down, needs you)
//   last turn completed                       -> Complete (stands, done)
//   anything else                             -> Idle (stands quietly, resting)
// The words come from the app's own post-turn summary: what it did, and what it needs from the user.
public static class SessionCast
{
    public sealed record Info(string HostId, string Title, string Folder, bool Busy, string Category, string Detail,
                              string NeedsAction, long LastActivity)
    {
        public AgentState State =>
            Busy ? AgentState.Working
            : NeedsAction != "" || Category == "blocked" ? AgentState.AwaitingUser
            : Category == "completed" ? AgentState.Complete
            : AgentState.Idle;

        public string Phase => State switch
        {
            AgentState.Working => "Working",
            AgentState.AwaitingUser => "Needs you",
            AgentState.Complete => "Done",
            _ => "Resting",
        };

        /// <summary>The folder's name, shown on the character's plate in place of a project number.</summary>
        public string FolderName
        {
            get
            {
                var name = Path.GetFileName(Folder.TrimEnd('\\', '/'));
                return name.StartsWith("scratch-", StringComparison.OrdinalIgnoreCase) || name == "" ? "No folder" : name;
            }
        }
    }

    static string _root = "";

    /// <summary>Call once on the main thread at startup: Godot's path lookup is not for background threads.</summary>
    public static void Init() => _root = Godot.ProjectSettings.GlobalizePath("res://");

    static string LooksPath => Path.Combine(DataDir, "agent-looks.json");
    static Dictionary<string, string>? _looks;

    static Dictionary<string, string> Looks()
    {
        if (_looks != null) return _looks;
        try { _looks = File.Exists(LooksPath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(LooksPath)) : null; }
        catch { _looks = null; }
        return _looks ??= new();
    }

    static bool HasArt(string name)
    {
        var dir = Path.GetFullPath(Path.Combine(_root, "..", "portraits"));
        string b = name.Replace(' ', '_');
        return File.Exists(Path.Combine(dir, b + ".png")) || (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "??_" + b + ".png").Any());
    }

    /// <summary>
    /// The art a project agent wears (main thread). An agent drawn under its own name wears that. One with no art
    /// (a name the game gave a new project) wears a free drawn character instead of a hooded silhouette, and keeps
    /// it from run to run (data/agent-looks.json). Null means its own art.
    /// </summary>
    public static string? LookFor(string agent)
    {
        if (_root == "") Init();
        if (HasArt(agent)) return null;
        var looks = Looks();
        var drawn = SheetCharacters().ToList(); // only the sheet characters: the others are project agents' own looks
        if (looks.TryGetValue(agent, out var look) && drawn.Contains(look)) return look;
        var taken = new HashSet<string>(Cast().Values.Concat(looks.Values), StringComparer.OrdinalIgnoreCase);
        look = drawn.FirstOrDefault(n => !taken.Contains(n));
        if (look == null) return null;
        looks[agent] = look;
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(LooksPath, JsonSerializer.Serialize(looks, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* keeps working for this run */ }
        return look;
    }

    /// <summary>The game's data folder (Game\data), safe to use from any thread once Init has run.</summary>
    public static string DataDir => Path.Combine(_root, "data");
    static string HiddenPath => Path.Combine(DataDir, "hidden-sessions.json");
    static string CastPath => Path.Combine(DataDir, "session-characters.json");

    static string Str(JsonElement r, string k) =>
        r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static long Num(JsonElement r, string k) =>
        r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;

    /// <summary>Every app session that is not archived, newest first, with whether it is in the middle of a turn.</summary>
    public static List<Info> Load()
    {
        var busy = new HashSet<string>(AgentLauncher.ListSessions().Where(s => s.InApp && s.Status == "busy").Select(s => s.HostId));
        var list = new List<Info>();
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code-sessions");
            foreach (var file in Directory.EnumerateFiles(dir, "local_*.json", SearchOption.AllDirectories))
            {
                try
                {
                    string text;
                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
                    using var doc = JsonDocument.Parse(text);
                    var r = doc.RootElement;
                    if (r.TryGetProperty("isArchived", out var a) && a.ValueKind == JsonValueKind.True) continue;
                    string id = Str(r, "sessionId");
                    if (!id.StartsWith("local_", StringComparison.Ordinal)) continue;
                    string folder = Str(r, "originCwd");
                    if (folder == "") folder = Str(r, "cwd");
                    string category = "", detail = "", needs = "";
                    if (r.TryGetProperty("postTurnSummary", out var s) && s.ValueKind == JsonValueKind.Object)
                    {
                        category = Str(s, "status_category");
                        detail = Str(s, "status_detail");
                        needs = Str(s, "needs_action");
                    }
                    string title = Str(r, "title");
                    if (title == "") title = Path.GetFileName(folder.TrimEnd('\\', '/'));
                    list.Add(new Info(id, title, folder, busy.Contains(id), category, detail, needs, Num(r, "lastActivityAt")));
                }
                catch { /* a half-written or unreadable file: skip it this time */ }
            }
        }
        catch { /* the Claude app has no Code sessions yet */ }
        return list.OrderByDescending(i => i.LastActivity).ToList();
    }

    /// <summary>Archived app sessions: (session id, title). Used to tell which project agents once had a session.</summary>
    public static List<(string HostId, string Title)> Archived()
    {
        var list = new List<(string, string)>();
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code-sessions");
            foreach (var file in Directory.EnumerateFiles(dir, "local_*.json", SearchOption.AllDirectories))
            {
                try
                {
                    string text;
                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
                    using var doc = JsonDocument.Parse(text);
                    var r = doc.RootElement;
                    if (!(r.TryGetProperty("isArchived", out var a) && a.ValueKind == JsonValueKind.True)) continue;
                    string id = Str(r, "sessionId");
                    if (id.StartsWith("local_", StringComparison.Ordinal)) list.Add((id, Str(r, "title")));
                }
                catch { /* unreadable: skip it */ }
            }
        }
        catch { /* no sessions folder */ }
        return list;
    }

    // ---- which session belongs to which project agent ------------------------------------

    static string LinksPath => Path.Combine(DataDir, "project-sessions.json");
    static Dictionary<string, string>? _links;

    /// <summary>
    /// Project number -> the app session that project agent last had. Background poll thread only (one poll at a
    /// time). Kept so an agent whose session was deleted or archived can be told apart from one never started.
    /// </summary>
    public static Dictionary<string, string> Links()
    {
        if (_links != null) return _links;
        try { _links = File.Exists(LinksPath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(LinksPath)) : null; }
        catch { _links = null; }
        return _links ??= new();
    }

    public static void SaveLinks()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(LinksPath, JsonSerializer.Serialize(Links(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* keeps working for this run */ }
    }

    /// <summary>A session title the game gives a project agent's session: "Ember - PRJ-000123 ...".</summary>
    public static bool TitledFor(string title, string agent, string number)
    {
        if (!title.StartsWith(agent, StringComparison.OrdinalIgnoreCase)) return false;
        if (title.Length > agent.Length && char.IsLetterOrDigit(title[agent.Length])) return false; // "Emberly" is not "Ember"
        string digits = System.Text.RegularExpressions.Regex.Match(number, @"\d{4,}").Value;
        return digits != "" && title.Contains(digits, StringComparison.Ordinal);
    }

    // ---- hidden sessions ---------------------------------------------------------------

    sealed class HiddenEntry { public string session { get; set; } = ""; public string title { get; set; } = ""; }

    static List<HiddenEntry> ReadHidden()
    {
        try { return File.Exists(HiddenPath) ? JsonSerializer.Deserialize<List<HiddenEntry>>(File.ReadAllText(HiddenPath)) ?? new() : new(); }
        catch { return new(); }
    }

    /// <summary>The app session ids the user does not want a character for.</summary>
    public static HashSet<string> Hidden() => new(ReadHidden().Select(h => h.session).Where(s => s != ""));

    /// <summary>Stop showing a character for this session (kept in data/hidden-sessions.json, with its title for reference).</summary>
    public static void Hide(string hostId, string title)
    {
        var list = ReadHidden();
        if (list.Any(h => h.session == hostId)) return;
        list.Add(new HiddenEntry { session = hostId, title = title });
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(HiddenPath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ---- which character plays which session ---------------------------------------------

    static Dictionary<string, string>? _cast;

    static Dictionary<string, string> Cast()
    {
        if (_cast != null) return _cast;
        try { _cast = File.Exists(CastPath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CastPath)) : null; }
        catch { _cast = null; }
        return _cast ??= new();
    }

    static void SaveCast(Dictionary<string, string> cast)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(CastPath, JsonSerializer.Serialize(cast, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* keeps working for this run */ }
    }

    /// <summary>
    /// Forget the characters of sessions that were archived or deleted, so their characters are free for new
    /// sessions. Main thread only. An empty list is treated as a failed read and changes nothing.
    /// </summary>
    public static void Prune(IEnumerable<string> liveHostIds)
    {
        var live = new HashSet<string>(liveHostIds);
        if (live.Count == 0) return;
        var cast = Cast();
        var gone = cast.Keys.Where(k => !live.Contains(k)).ToList();
        if (gone.Count == 0) return;
        foreach (var k in gone) cast.Remove(k);
        SaveCast(cast);
    }

    /// <summary>
    /// The character for a session: the one it had before, else the first free character with art that no project
    /// agent uses, else a numbered "Wanderer" drawn as a silhouette.
    /// </summary>
    public static string CharacterFor(string hostId, IEnumerable<string> namesInUse)
    {
        var cast = Cast();
        // A session keeps its character, unless it was a Wanderer and a drawn character is now free to take over.
        bool wanderer = cast.TryGetValue(hostId, out var name) && name.StartsWith("Wanderer ", StringComparison.Ordinal);
        if (name != null && !wanderer) return name;
        var taken = new HashSet<string>(namesInUse, StringComparer.OrdinalIgnoreCase);
        foreach (var v in cast.Values) taken.Add(v);
        foreach (var v in Looks().Values) taken.Add(v);
        var spare = SpareCharacters().FirstOrDefault(n => !taken.Contains(n));
        if (wanderer && spare == null) return name!;
        name = spare ?? "";
        for (int i = 1; name == ""; i++)
            if (!taken.Contains($"Wanderer {i}")) name = $"Wanderer {i}";
        cast[hostId] = name;
        SaveCast(cast);
        return name;
    }

    /// <summary>
    /// Characters drawn in all four directions in the portraits folder, except the player: first those with separate
    /// files (Name.png plus Name_up/_left/_right.png), then those drawn as one sheet (NN_Name.png), in sheet order.
    /// A sheet whose name already has its own Name.png is skipped: it is a character or monster already in the game.
    /// Sheet names show with spaces ("The Witch").
    /// </summary>
    static IEnumerable<string> SpareCharacters()
    {
        var dir = Path.GetFullPath(Path.Combine(_root, "..", "portraits"));
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*_up.png").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string n = Path.GetFileName(f)[..^"_up.png".Length];
            if (n.Equals("Player", StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(Path.Combine(dir, n + ".png"))) yield return n;
        }
        foreach (var n in SheetCharacters()) yield return n;
    }

    static IEnumerable<string> SheetCharacters()
    {
        var dir = Path.GetFullPath(Path.Combine(_root, "..", "portraits"));
        if (!Directory.Exists(dir)) yield break;
        var sheet = new System.Text.RegularExpressions.Regex(@"^\d\d_(.+)\.png$");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(dir, "??_*.png").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var m = sheet.Match(Path.GetFileName(f));
            if (!m.Success) continue;
            string baseName = m.Groups[1].Value;
            if (baseName.Equals("Player", StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(dir, baseName + ".png"))) continue;
            if (seen.Add(baseName)) yield return baseName.Replace('_', ' ');
        }
    }
}
