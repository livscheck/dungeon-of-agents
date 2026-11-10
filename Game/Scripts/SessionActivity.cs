using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// What a busy Claude session is doing right now, in a few words ("Merging branch into QA", "Updating the sync
// list"), read from the end of its conversation log (~/.claude/projects/<folder>/<session id>.jsonl): the latest
// tool call it made. Well-known workflow steps get fixed wording; anything else uses the tool call's own description.
public static class SessionActivity
{
    static readonly Dictionary<string, string> PathCache = new(StringComparer.OrdinalIgnoreCase);
    const int TailBytes = 256 * 1024;

    /// <summary>The conversation log of a running session, or null when it cannot be found.</summary>
    public static string? TranscriptPath(string sessionId, string cwd)
    {
        if (sessionId == "") return null;
        lock (PathCache) if (PathCache.TryGetValue(sessionId, out var cached) && File.Exists(cached)) return cached;
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        string? found = null;
        try
        {
            string slug = Regex.Replace(cwd.TrimEnd('\\', '/'), "[^A-Za-z0-9]", "-");
            var dir = Directory.EnumerateDirectories(root).FirstOrDefault(d => string.Equals(Path.GetFileName(d), slug, StringComparison.OrdinalIgnoreCase));
            if (dir != null && File.Exists(Path.Combine(dir, sessionId + ".jsonl"))) found = Path.Combine(dir, sessionId + ".jsonl");
            found ??= Directory.EnumerateFiles(root, sessionId + ".jsonl", SearchOption.AllDirectories).FirstOrDefault();
        }
        catch { }
        if (found != null) lock (PathCache) PathCache[sessionId] = found;
        return found;
    }

    /// <summary>The step a running session is on, from its latest tool call; "" when there is none to show.</summary>
    public static string Current(string sessionId, string cwd)
    {
        var path = TranscriptPath(sessionId, cwd);
        if (path == null) return "";
        try
        {
            string tail;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, fs.Length - TailBytes);
                fs.Seek(start, SeekOrigin.Begin);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                tail = sr.ReadToEnd();
                if (start > 0) { int nl = tail.IndexOf('\n'); tail = nl >= 0 ? tail[(nl + 1)..] : ""; } // drop the cut-off first line
            }
            var lines = tail.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (line == "" || !line.Contains("\"tool_use\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    if (!r.TryGetProperty("message", out var m) || !m.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.Array) continue;
                    foreach (var part in c.EnumerateArray().Reverse())
                        if (part.TryGetProperty("type", out var t) && t.GetString() == "tool_use")
                            return Describe(part.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                                            part.TryGetProperty("input", out var inp) ? inp : default);
                }
                catch { /* a line still being written */ }
            }
        }
        catch { }
        return "";
    }

    public sealed record Question(string Id, string Text);

    // Wording that asks for a yes/no answer. A line starting "QUESTION (yes/no):" is the explicit marker briefed agents use.
    static readonly Regex QuestionMarker = new(@"^\s*\**QUESTION \(yes/no\):\**\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    static readonly Regex YesNoWording = new(
        @"\b(reply|answer|say|type)\s+[""“']?(yes|go)\b|\ba\s+[""“']?(yes|go)[""”']?\s+(here|in this chat)\b|\(yes/no\)|\byes or no\b|" +
        @"\b(should|shall|may|can) i\b[^.?!\n]*\?|\bdo you want me to\b[^.?!\n]*\?|\bok(ay)? to\b[^.?!\n]*\?",
        RegexOptions.IgnoreCase);

    static readonly Dictionary<string, (long len, long ticks, Question? q)> QuestionCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The yes/no question an idle session ended its last turn on, or null. Only counts when that reply is the latest
    /// thing in the conversation (nothing from the user after it).
    /// </summary>
    public static Question? PendingQuestion(string sessionId, string cwd)
    {
        var path = TranscriptPath(sessionId, cwd);
        if (path == null) return null;
        try
        {
            var fi = new FileInfo(path);
            lock (QuestionCache)
                if (QuestionCache.TryGetValue(path, out var c) && c.len == fi.Length && c.ticks == fi.LastWriteTimeUtc.Ticks) return c.q;

            string tail;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, fs.Length - TailBytes);
                fs.Seek(start, SeekOrigin.Begin);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                tail = sr.ReadToEnd();
                if (start > 0) { int nl = tail.IndexOf('\n'); tail = nl >= 0 ? tail[(nl + 1)..] : ""; }
            }
            Question? found = null;
            var lines = tail.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (line == "") continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    string type = Str(r, "type");
                    if (type != "user" && type != "assistant") continue;
                    if (!r.TryGetProperty("message", out var m) || !m.TryGetProperty("content", out var c)) continue;
                    if (type == "user")
                    {
                        // A tool result belongs to the agent's own turn; anything else is the user (or another session) talking.
                        bool toolResult = c.ValueKind == JsonValueKind.Array && c.EnumerateArray().All(p => Str(p, "type") == "tool_result");
                        if (toolResult) continue;
                        break;
                    }
                    var text = c.ValueKind == JsonValueKind.String ? c.GetString() ?? ""
                        : string.Join("\n", c.EnumerateArray().Where(p => Str(p, "type") == "text").Select(p => Str(p, "text")));
                    if (text.Trim() == "")
                    {
                        if (c.ValueKind == JsonValueKind.Array && c.EnumerateArray().Any(p => Str(p, "type") == "tool_use")) break; // still working
                        continue;
                    }
                    var mk = QuestionMarker.Match(text);
                    if (mk.Success) found = new Question(Str(r, "uuid"), mk.Groups[1].Value.Trim());
                    else if (YesNoWording.IsMatch(text)) found = new Question(Str(r, "uuid"), Excerpt(text));
                    break;
                }
                catch { /* a line still being written */ }
            }
            lock (QuestionCache) QuestionCache[path] = (fi.Length, fi.LastWriteTimeUtc.Ticks, found);
            return found;
        }
        catch { return null; }
    }

    /// <summary>The part of a reply worth showing with Yes/No: from where it asks for the user, up to about 700 characters.</summary>
    static string Excerpt(string text)
    {
        text = text.Trim();
        var m = Regex.Match(text, @"(\*\*)?(still )?waiting on you|\bneed(s)? (your|a) (yes|go|answer|approval)|\b(should|shall|may|can) i\b|\bdo you want me to\b|\bok(ay)? to\b", RegexOptions.IgnoreCase);
        int from = m.Success ? Math.Max(0, text.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1) : Math.Max(0, text.Length - 700);
        var s = text[from..];
        return s.Length > 700 ? s[..700].TrimEnd() + "..." : s;
    }

    static string Str(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // Known skills, by the part of the name after any "plugin:" prefix.
    static readonly Dictionary<string, string> SkillSteps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["update-sync-list"] = "Updating the sync list",
        ["generate-td"] = "Writing the technical design",
        ["test-prep"] = "Copying files for testing",
        ["prep-test"] = "Copying files for testing",
        ["setup-project"] = "Setting up the project",
        ["run-project"] = "Working through the project",
        ["update-team-list"] = "Updating the Team list",
    };

    /// <summary>
    /// The step one shell command really runs. A command is split at ;, &amp;&amp;, || and | and each piece is judged by
    /// how it starts, so text that only mentions git or a sync list (a search pattern, a message) does not count.
    /// The last recognised piece wins: in "fetch, checkout qa, merge" the merge is the point.
    /// </summary>
    static string? CommandStep(string cmd)
    {
        string? found = null;
        bool onQa = false;
        foreach (var raw in Regex.Split(cmd, @"&&|\|\||;|\||\r?\n"))
        {
            var seg = Regex.Replace(raw.Trim(), @"^cd\s+(""[^""]*""|'[^']*'|\S+)\s*", "").Trim();
            string? step = null;
            var g = Regex.Match(seg, @"^git\s+(?:-C\s+(?:""[^""]*""|\S+)\s+)?([\w-]+)(.*)$", RegexOptions.IgnoreCase);
            if (g.Success)
            {
                string verb = g.Groups[1].Value.ToLowerInvariant(), rest = g.Groups[2].Value;
                bool qa = Regex.IsMatch(rest, @"(^|[\s/:])qa(\s|$)", RegexOptions.IgnoreCase);
                if ((verb is "checkout" or "switch") && qa) onQa = true;
                step = verb switch
                {
                    "merge" when rest.Contains("--abort") => "Backing out a merge",
                    "merge" => onQa ? "Merging branch into QA" : "Merging branches",
                    "push" when qa => "Pushing QA",
                    "push" => "Pushing the branch",
                    "commit" => "Committing changes",
                    "checkout" or "switch" or "pull" or "fetch" when qa => "Updating the qa branch",
                    _ => null,
                };
            }
            else if (Regex.IsMatch(seg, @"SyncList_\w*\.txt", RegexOptions.IgnoreCase))
                step = Regex.IsMatch(seg, @"(>>|Add-Content|Set-Content|Out-File|\btee\b|sed\s+-i)", RegexOptions.IgnoreCase)
                    ? "Updating the sync list" : "Checking the sync list";
            else if (Regex.IsMatch(seg, @"^(dotnet\s+build\b|msbuild\b|&\s*""?[^""]*msbuild)", RegexOptions.IgnoreCase))
                step = "Building";
            else if (Settings.LocalTestFolder != "" && Regex.IsMatch(seg,
                         @"^(robocopy|xcopy|copy|cp|Copy-Item)\b.*" + Regex.Escape(Settings.LocalTestFolder.TrimEnd('\\', '/')).Replace(@"\\", @"[\\/]+"),
                         RegexOptions.IgnoreCase))
                step = "Copying files for testing";
            if (step != null) found = step;
        }
        return found;
    }

    /// <summary>Turn one tool call into a short step name.</summary>
    public static string Describe(string tool, JsonElement input)
    {
        string cmd = Str(input, "command"), desc = Str(input, "description"), skill = Str(input, "skill");

        if (tool == "Skill" && skill != "")
        {
            var name = skill.Split(':').Last();
            return SkillSteps.TryGetValue(name, out var s) ? s : $"Running {name}";
        }
        if (tool is "Bash" or "PowerShell" && cmd != "")
        {
            var step = CommandStep(cmd);
            if (step != null) return step;
        }
        if (tool.StartsWith("mcp__tracker__", StringComparison.Ordinal))
            return tool.Contains("update") || tool.Contains("create") || tool.Contains("submit") ? "Updating the project tracker" : "Checking the project tracker";
        if (tool.Contains("outlook") || tool.Contains("teams_")) return "Working in Outlook/Teams";
        if (tool is "AskUserQuestion") return "Asking you a question";
        if (tool is "Edit" or "Write" or "NotebookEdit")
        {
            var f = Path.GetFileName(Str(input, "file_path"));
            return f != "" ? $"Editing {f}" : "Editing files";
        }
        if (tool is "Read" or "Grep" or "Glob") return "Reading the code";
        if (tool is "WebFetch" or "WebSearch") return "Looking things up";
        if (tool is "Agent" or "Task") return "Sending out a helper";
        if (tool.EndsWith("__wrap_up", StringComparison.Ordinal)) return "Wrapping up";
        if (tool.EndsWith("__update_plan", StringComparison.Ordinal)) return "Planning the next steps";
        if (desc != "") return Sentence(desc);
        if (tool.StartsWith("mcp__", StringComparison.Ordinal))
        {
            // mcp__<server>__<tool_name> -> "Using tool name"
            var name = tool.Split("__").Last().Replace('_', ' ');
            return name != "" ? Sentence("Using " + name) : "Using a connector";
        }
        return tool switch { "Bash" or "PowerShell" => "Running commands", "" => "", _ => $"Using {tool}" };
    }

    static string Sentence(string s)
    {
        s = s.Trim().TrimEnd('.');
        if (s.Length > 46) s = s[..45].TrimEnd() + "...";
        return s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;
    }
}
