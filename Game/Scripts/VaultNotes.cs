using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

// Reads (and, only when the user clicks a checkbox, updates) the two Agent Viewer notes in the vault folder (Settings.VaultDir):
//   Test Checklist.md  - one "## PRJ-nnnnnn - title" section per project with "- [ ]" test steps
//   Project Dashboard.md - one table row per project, plus flags and an activity log
// The project agents maintain these files; the game is a live view of them. Agents never tick boxes:
// a tick written here is the user's confirmation.

public sealed class ChecklistItem { public string Text = ""; public bool Done; }

public sealed class ChecklistSection
{
    public string Number = "", Title = "", Note = "";
    public List<ChecklistItem> Items = new();
    public int DoneCount => Items.Count(i => i.Done);
}

public sealed class DashboardRow
{
    public Dictionary<string, string> Cells = new();
    public string Get(string key) => Cells.TryGetValue(key, out var v) ? v : "";
    public string Number => Get("Project");
}

public sealed class DashboardData
{
    public string Updated = "";
    public List<string> Headers = new();
    public List<DashboardRow> Rows = new();
    public List<string> Flags = new();
    public List<string> Activity = new();
}

public static class VaultNotes
{
    public static string Dir => Settings.VaultDir;
    public static string ChecklistPath => Path.Combine(Dir, "Test Checklist.md");
    public static string DashboardPath => Path.Combine(Dir, "Project Dashboard.md");

    static readonly Regex SectionRx = new($@"^##\s+({System.Text.RegularExpressions.Regex.Escape(Settings.ProjectPrefix)}-\d+)\s+[\u2014\u2013-]\s+(.*)$");
    static readonly Regex ItemRx = new(@"^- \[( |x|X)\]\s?(.*)$");

    /// <summary>Changes whenever either file is saved; used to know when to re-read.</summary>
    public static string Stamp()
    {
        long a = File.Exists(ChecklistPath) ? File.GetLastWriteTimeUtc(ChecklistPath).Ticks : 0;
        long b = File.Exists(DashboardPath) ? File.GetLastWriteTimeUtc(DashboardPath).Ticks : 0;
        return $"{a}-{b}";
    }

    static string ReadText(string path, out bool bom)
    {
        byte[] bytes;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var ms = new MemoryStream()) { fs.CopyTo(ms); bytes = ms.ToArray(); }
        bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
    }

    static string[] Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    // ---- checklist ---------------------------------------------------------------------

    public static List<ChecklistSection> LoadChecklist()
    {
        var result = new List<ChecklistSection>();
        if (!File.Exists(ChecklistPath)) return result;
        ChecklistSection? cur = null;
        foreach (var raw in Lines(ReadText(ChecklistPath, out _)))
        {
            var line = raw.TrimEnd();
            var m = SectionRx.Match(line);
            if (m.Success)
            {
                cur = new ChecklistSection { Number = m.Groups[1].Value, Title = m.Groups[2].Value.Trim() };
                result.Add(cur);
                continue;
            }
            if (cur == null) continue;
            var im = ItemRx.Match(line);
            if (im.Success) cur.Items.Add(new ChecklistItem { Done = im.Groups[1].Value != " ", Text = im.Groups[2].Value });
            else if (cur.Items.Count == 0 && cur.Note == "" && line.StartsWith("_") && line.EndsWith("_") && line.Length > 2)
                cur.Note = line.Substring(1, line.Length - 2);
        }
        return result;
    }

    /// <summary>Flip one step of one project's section. Re-reads the file first so concurrent agent edits are kept.</summary>
    public static bool ToggleChecklist(string number, int itemIndex)
    {
        var text = ReadText(ChecklistPath, out bool bom);
        var lines = text.Split('\n'); // keep any trailing '\r' on each line so line endings are preserved
        string? cur = null;
        int idx = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r', ' ', '\t');
            var sm = SectionRx.Match(raw);
            if (sm.Success) { cur = sm.Groups[1].Value; idx = -1; continue; }
            if (cur != number) continue;
            var im = ItemRx.Match(raw);
            if (!im.Success) continue;
            if (++idx != itemIndex) continue;
            bool done = im.Groups[1].Value != " ";
            var line = lines[i];
            int open = line.IndexOf("- [", StringComparison.Ordinal);
            lines[i] = line.Substring(0, open + 3) + (done ? " " : "x") + line.Substring(open + 4);
            File.WriteAllText(ChecklistPath, string.Join("\n", lines), new UTF8Encoding(bom));
            return true;
        }
        return false;
    }

    // ---- dashboard ---------------------------------------------------------------------

    static List<string> SplitRow(string line)
    {
        var t = line.Trim();
        if (t.StartsWith("|")) t = t.Substring(1);
        if (t.EndsWith("|")) t = t.Substring(0, t.Length - 1);
        return t.Split('|').Select(c => c.Trim()).ToList();
    }

    public static DashboardData LoadDashboard()
    {
        var d = new DashboardData();
        if (!File.Exists(DashboardPath)) return d;
        var lines = Lines(ReadText(DashboardPath, out _));
        string section = "";
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (line.StartsWith("Last updated:")) { d.Updated = line.Substring("Last updated:".Length).Trim(); continue; }
            if (line.StartsWith("## ")) { section = line.Substring(3).Trim(); continue; }

            if (section == "Projects" && line.StartsWith("|"))
            {
                var cells = SplitRow(line);
                if (cells.Count > 0 && cells[0] == "Project") { d.Headers = cells; continue; }
                if (cells.All(c => c.StartsWith("-"))) continue; // separator row
                if (d.Headers.Count == 0) continue;
                // Notes (the last column) may contain pipes: fold any extra cells back into it.
                while (cells.Count > d.Headers.Count) { cells[cells.Count - 2] += " | " + cells[cells.Count - 1]; cells.RemoveAt(cells.Count - 1); }
                var row = new DashboardRow();
                for (int c = 0; c < d.Headers.Count && c < cells.Count; c++) row.Cells[d.Headers[c]] = cells[c];
                d.Rows.Add(row);
            }
            else if (section == "Flags" && line.StartsWith("- ")) d.Flags.Add(line.Substring(2).Trim());
            else if (section == "Activity log" && line.StartsWith("- ")) d.Activity.Add(line.Substring(2).Trim());
        }
        return d;
    }

    /// <summary>Flip a "[ ]" / "[x]" cell of a project's dashboard row.</summary>
    public static bool ToggleDashboard(string number, string header)
    {
        var text = ReadText(DashboardPath, out bool bom);
        var lines = text.Split('\n');
        int headerCol = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (!line.StartsWith("|")) continue;
            var cells = SplitRow(line);
            if (cells.Count > 0 && cells[0] == "Project") { headerCol = cells.IndexOf(header); continue; }
            if (headerCol < 0 || cells.Count == 0 || cells[0] != number) continue;
            var parts = lines[i].Split('|'); // parts[0] is the text before the first pipe
            int k = headerCol + 1;
            if (k >= parts.Length) return false;
            if (parts[k].Contains("[ ]")) parts[k] = parts[k].Replace("[ ]", "[x]");
            else if (parts[k].Contains("[x]")) parts[k] = parts[k].Replace("[x]", "[ ]");
            else return false;
            lines[i] = string.Join("|", parts);
            File.WriteAllText(DashboardPath, string.Join("\n", lines), new UTF8Encoding(bom));
            return true;
        }
        return false;
    }

    // ---- status mapping ----------------------------------------------------------------

    /// <summary>Map the dashboard's Status text (Not started / In Progress / Ready for local test / Waiting For User Input / Merged ...).</summary>
    public static AgentState StateFromStatus(string status)
    {
        var s = status.Trim().ToLowerInvariant();
        if (s.StartsWith("waiting") || QaCopyCheck.IsWaitingForCopy(s)) return AgentState.AwaitingUser;
        if (s.StartsWith("ready for local test")) return AgentState.AwaitingTest;
        if (s.StartsWith("ready")) return AgentState.ReadyForTesting;
        if (s.StartsWith("not started")) return AgentState.Idle;
        if (s.StartsWith("merged") || s.StartsWith("in qa")) return AgentState.Complete;
        return AgentState.Working;
    }
}
