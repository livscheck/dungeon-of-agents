using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

// The Project Dashboard popup: one card per project instead of a 14-column table. Status, test progress,
// the manual checkbox steps (clicking one is the user's confirmation and is written to the note), tracker and
// Team List state, and the agent's notes, with buttons to open the checklist, open the agent in Claude,
// or walk to the agent in the dungeon.
public partial class DashboardWindow : GameWindow
{
    public event Action<string>? OpenChecklist;
    public event Action<string>? OpenClaude;
    public event Action<string>? Locate;
    public event Action? Changed;

    /// <summary>Supplied by the game: the agent's face for a project number.</summary>
    public Func<string, Texture2D?> FaceFor { get; set; } = _ => null;

    DashboardData _d = new();
    List<ChecklistSection> _checks = new();
    readonly HashSet<string> _expanded = new();
    readonly Label _updated = Ui.L("", 14, Ui.Dim);
    readonly HFlowContainer _chips = new();
    readonly VBoxContainer _cards = new();
    readonly VBoxContainer _footer = new();
    ScrollContainer _scroll = null!;
    readonly Dictionary<string, Control> _cardByNumber = new();

    public DashboardWindow() : base("Project Dashboard", new Vector2(980, 650))
    {
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 14);
        _chips.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _chips.AddThemeConstantOverride("h_separation", 8);
        top.AddChild(_chips);
        top.AddChild(_updated);
        Body.AddChild(top);

        var scroll = _scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var inner = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        inner.AddThemeConstantOverride("separation", 10);
        _cards.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _cards.AddThemeConstantOverride("separation", 10);
        _footer.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _footer.AddThemeConstantOverride("separation", 4);
        inner.AddChild(_cards);
        inner.AddChild(_footer);
        scroll.AddChild(inner);
        Body.AddChild(scroll);
    }

    public void UpdateData(DashboardData d, List<ChecklistSection> checks)
    {
        _d = d;
        _checks = checks;
        if (Visible) Rebuild();
    }

    public new void OpenWindow()
    {
        base.OpenWindow();
        Rebuild();
    }

    /// <summary>Open the dashboard with this project's card expanded (all notes shown) and scrolled into view.</summary>
    public void FocusProject(string number)
    {
        _expanded.Add(number);
        base.OpenWindow();
        Rebuild();
        // Wait a moment for the layout, then scroll to the card.
        GetTree().CreateTimer(0.08).Timeout += () =>
        {
            if (_cardByNumber.TryGetValue(number, out var card) && IsInstanceValid(card)) _scroll.EnsureControlVisible(card);
        };
    }

    static int Priority(string status)
    {
        var s = status.ToLowerInvariant();
        if (s.StartsWith("waiting")) return 0;
        if (s.StartsWith("ready")) return 1;
        if (s.StartsWith("in progress")) return 2;
        if (s.StartsWith("not started")) return 3;
        return 4;
    }

    static bool IsCheckbox(string cell) => cell.Trim() is "[ ]" or "[x]" or "[X]";

    void Rebuild()
    {
        Clear(_cards);
        Clear(_chips);
        Clear(_footer);

        _updated.Text = _d.Updated != "" ? $"Updated {(_d.Updated.Length > 10 ? _d.Updated.Substring(0, 10) : _d.Updated)}   (live)" : "";

        // Status chips with counts.
        foreach (var g in _d.Rows.GroupBy(r => Shorten(r.Get("Status"))).OrderBy(g => Priority(g.Key)))
        {
            var chip = new PanelContainer();
            var col = Ui.StatusColor(g.Key);
            chip.AddThemeStyleboxOverride("panel", Ui.Box(new Color(col.R * 0.25f, col.G * 0.25f, col.B * 0.25f), col, 1, 10, 5));
            chip.AddChild(Ui.L($"{g.Count()}  {g.Key}", 14, col));
            _chips.AddChild(chip);
        }

        _cardByNumber.Clear();
        foreach (var row in _d.Rows.OrderBy(r => Priority(r.Get("Status"))))
        {
            var card = BuildCard(row);
            _cardByNumber[row.Number] = card;
            _cards.AddChild(card);
        }

        if (_d.Flags.Count > 0)
        {
            _footer.AddChild(Ui.L("FLAGS", 15, Ui.Gold));
            foreach (var f in _d.Flags) _footer.AddChild(Ui.Rich("-  " + f, 14, Ui.Dim));
        }
        if (_d.Activity.Count > 0)
        {
            _footer.AddChild(Ui.L("ACTIVITY", 15, Ui.Gold));
            foreach (var a in _d.Activity.AsEnumerable().Reverse().Take(5)) _footer.AddChild(Ui.Rich("-  " + a, 14, Ui.Dim));
        }
    }

    static string Shorten(string status)
    {
        int p = status.IndexOf(" (", StringComparison.Ordinal);
        return p > 0 ? status.Substring(0, p) : status;
    }

    Control BuildCard(DashboardRow row)
    {
        string number = row.Number;
        string status = row.Get("Status");
        var section = _checks.Find(c => c.Number == number);
        var accent = Ui.StatusColor(status);

        var card = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        card.AddThemeStyleboxOverride("panel", Ui.Box(Ui.Card, new Color(accent.R * 0.55f, accent.G * 0.55f, accent.B * 0.55f), 1, 4, 10));
        var hb = new HBoxContainer();
        hb.AddThemeConstantOverride("separation", 12);

        var face = new TextureRect
        {
            Texture = FaceFor(number),
            CustomMinimumSize = new Vector2(70, 70),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            SizeFlagsVertical = SizeFlags.ShrinkBegin,
        };
        hb.AddChild(face);

        var vb = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        vb.AddThemeConstantOverride("separation", 5);

        // title + status chip
        var title = new HBoxContainer();
        title.AddThemeConstantOverride("separation", 10);
        var name = Ui.L(section != null ? $"{number}  -  {section.Title}" : number, 18, Ui.Text);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        name.ClipText = true;
        title.AddChild(name);
        var chip = new PanelContainer();
        chip.AddThemeStyleboxOverride("panel", Ui.Box(new Color(accent.R * 0.28f, accent.G * 0.28f, accent.B * 0.28f), accent, 1, 10, 4));
        chip.AddChild(Ui.L(Shorten(status), 14, accent));
        title.AddChild(chip);
        vb.AddChild(title);

        string plan = row.Get("Plan");
        if (plan != "") vb.AddChild(Ui.Rich("Plan:  " + plan, 14, Ui.Dim));

        // test progress
        if (section != null && section.Items.Count > 0)
        {
            var pr = new HBoxContainer();
            pr.AddThemeConstantOverride("separation", 8);
            pr.AddChild(Ui.Bar(section.DoneCount, section.Items.Count, 180));
            pr.AddChild(Ui.L($"{section.DoneCount} of {section.Items.Count} test steps confirmed", 14, Ui.Dim));
            vb.AddChild(pr);
        }

        // manual checkbox steps (the "[ ]" columns)
        var steps = new HFlowContainer();
        steps.AddThemeConstantOverride("h_separation", 14);
        foreach (var header in _d.Headers)
        {
            var cell = row.Get(header);
            if (!IsCheckbox(cell)) continue;
            string h = header;
            var cb = Ui.Check(h.Replace($" ({Settings.UserName})", ""), cell.Trim() != "[ ]", 13);
            cb.Toggled += _ =>
            {
                try { VaultNotes.ToggleDashboard(number, h); }
                catch (Exception ex) { GD.PrintErr($"Could not update the dashboard note: {ex.Message}"); }
                Changed?.Invoke();
            };
            steps.AddChild(cb);
        }
        vb.AddChild(steps);

        // Tracker / Team List
        var sync = new List<string>();
        foreach (var key in new[] { "Tracker status", "Team List progress", "Tracker = List?" })
        {
            var v = row.Get(key);
            if (v != "") sync.Add($"{key}: {Ui.Plain(v)}");
        }
        if (sync.Count > 0) vb.AddChild(Ui.L(string.Join("      ", sync), 13, Ui.Dim, wrap: true));

        // notes (collapsed to a few lines until expanded)
        string notes = row.Get("Notes");
        if (notes != "")
        {
            bool open = _expanded.Contains(number);
            vb.AddChild(Ui.Rich(open ? notes : Ui.Excerpt(notes, 260), 14, new Color(0.78f, 0.77f, 0.74f)));
            if (Ui.Plain(notes).Length > 260)
                vb.AddChild(Ui.Btn(open ? "show less" : "show all notes", () =>
                {
                    if (!_expanded.Add(number)) _expanded.Remove(number);
                    Rebuild();
                }, 13));
        }
        hb.AddChild(vb);

        // actions
        var actions = new VBoxContainer();
        actions.AddThemeConstantOverride("separation", 6);
        actions.AddChild(Ui.Btn("Test checklist", () => OpenChecklist?.Invoke(number)));
        actions.AddChild(Ui.Btn("Open in Claude", () => OpenClaude?.Invoke(number)));
        actions.AddChild(Ui.Btn("Find in dungeon", () => Locate?.Invoke(number)));
        hb.AddChild(actions);

        card.AddChild(hb);
        return card;
    }
}
