using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

// Turns an agent's dashboard notes into the part that matters: what it needs from the user.
public static class Briefing
{
    static readonly Regex SentenceSplit = new(@"(?<=[.!?])\s+(?=[A-Z0-9""`])");
    static readonly string[] Keys =
    {
        "waiting on", "waiting for", "waits on", "stopped before", "awaiting", "needs ", "need ", "approve", "approval",
        "review", "say go", "confirm", "decide", "your call", "tell me",
    };

    /// <summary>The actionable sentences from the notes (what the agent is waiting for), as written in the note.</summary>
    public static string NeedsRaw(string status, string notes, string plan)
    {
        var picks = new List<string>();
        foreach (var raw in SentenceSplit.Split(Ui.Plain(notes)))
        {
            var s = raw.Trim();
            if (s.Length < 12) continue;
            var l = s.ToLowerInvariant();
            if (Keys.Any(k => l.Contains(k))) picks.Add(Ui.Excerpt(s, 230));
            if (picks.Count == 2) break;
        }
        if (picks.Count == 0)
        {
            int a = status.IndexOf('(');
            if (a >= 0) picks.Add(status.Substring(a).Trim('(', ')', ' '));
        }
        if (picks.Count == 0 && plan != "") picks.Add(Ui.Excerpt(plan, 200));
        return string.Join(" ", picks);
    }

    /// <summary>The same, reworded to address the user directly ("waiting on <UserName>" becomes "waiting on you").</summary>
    public static string NeedsForUser(string status, string notes, string plan)
    {
        var t = NeedsRaw(status, notes, plan);
        var name = Regex.Escape(Settings.UserName);
        t = Regex.Replace(t, $@"\b{name}'s\b", "your");
        t = Regex.Replace(t, $@"\b{name}\b", "you");
        return t;
    }

    /// <summary>One line on the test checklist: how far along it is and the next unconfirmed step.</summary>
    public static string TestSummary(ChecklistSection? s)
    {
        if (s == null || s.Items.Count == 0) return "";
        var next = s.Items.Find(i => !i.Done);
        return $"{s.DoneCount} of {s.Items.Count} test steps confirmed." + (next != null ? $" Next step for them: {Ui.Excerpt(next.Text, 180)}" : " All steps confirmed.");
    }

    /// <summary>
    /// The first message sent to a new Claude session. It carries everything needed for the opening reply, so the
    /// agent can answer at once instead of reading files (slow, and it would stop to ask permission to read
    /// outside its working folder).
    /// </summary>
    public static string SessionPrompt(string number, string title, string status, string needs, string plan, string notes,
                                       string testSummary, string dir, string vaultDir)
    {
        var sb = new StringBuilder();
        string U = Settings.UserName;
        sb.Append($"You are the project agent for {number} - {title}. {U} has just opened this session from their Agent Viewer game to talk with you.\n\n");
        sb.Append("Everything you need for your first reply is below, so answer straight away without reading files or running tools.\n\n");
        sb.Append($"STATUS (Project Dashboard): {status}\n");
        if (plan != "") sb.Append($"PLAN: {Ui.Plain(plan)}\n");
        if (needs != "") sb.Append($"WAITING ON: {needs}\n");
        if (testSummary != "") sb.Append($"TESTING: {testSummary}\n");
        sb.Append($"YOUR NOTES (from your dashboard row):\n{Ui.Plain(notes)}\n\n");
        sb.Append($"Reply to {U} in at most five short lines: where the work stands, exactly what you need from them, and what you will do next. Then wait for them.\n\n");
        sb.Append($"Only if they ask for more detail, read your row in \"{vaultDir}\\Project Dashboard.md\", the {number} section of \"{vaultDir}\\Test Checklist.md\"");
        if (dir != "") sb.Append($", or the project repository at \"{dir}\" (your working folder)");
        sb.Append(". Follow the rules written in those notes: edit only your own row and section, and never tick a checkbox for them.\n\n");
        sb.Append($"Keep the Status cell of your {number} row in that dashboard current: their game draws you from it, so a stale status shows you as stuck. ");
        sb.Append("Update it whenever the work moves on, not only at the end, using the dashboard's status key: \"In Progress\" while you work, ");
        sb.Append("\"Waiting For User Input (what you need from them)\" when you stop for them, ");
        sb.Append($"\"Ready for local test\" once the changes are in place in their local test folder ({Settings.LocalTestFolder}) for them to test, ");
        sb.Append("then \"Merged to QA\" and \"Merged to Production\". ");
        sb.Append("After the status, put what you are doing or waiting on in parentheses, under 40 characters, and keep it current as you go, ");
        sb.Append("for example \"In Progress (merging branch into QA)\", \"In Progress (updating sync list)\" or ");
        sb.Append("\"Merged to QA (waiting for files to be copied)\". Their game shows it on your nameplate.\n\n");
        sb.Append(QaHandoff.Authorization(number));
        return sb.ToString();
    }
}
