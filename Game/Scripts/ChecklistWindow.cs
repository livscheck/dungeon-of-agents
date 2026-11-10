using System;
using System.Collections.Generic;
using Godot;

// The Test Checklist popup: one project at a time, top to bottom, with progress and a highlighted next step.
// Ticking a box is the user's confirmation and is written straight back to Test Checklist.md in the vault.
public partial class ChecklistWindow : GameWindow
{
    public event Action? Changed;                 // a box was ticked/unticked: the notes need re-reading
    public event Action<string>? OpenClaude;       // open this project's agent in Claude
    public event Action? OpenDashboard;
    public event Action<string>? CompleteTesting;  // testing passed: the agent commits and merges to QA

    public string Number { get; private set; } = "";

    List<ChecklistSection> _data = new();
    readonly HFlowContainer _tabs = new();
    readonly Label _head = Ui.L("", 22, Ui.Text);
    readonly PanelContainer _prepBox = new();
    readonly Label _prepText = Ui.L("", 14, Ui.Text, wrap: true);
    Button _prepBtn = null!;

    /// <summary>Supplied by the game: the test-environment prep state for a project (null when it does not apply).</summary>
    public Func<string, PrepInfo?> PrepFor { get; set; } = _ => null;
    public event Action<string>? RunPrep;
    public event Action<string>? ProjectPicked;    // the user switched to another project's tab
    readonly RichTextLabel _note;
    readonly ProgressBar _bar = Ui.Bar(0, 1, 260);
    readonly Label _count = Ui.L("", 15, Ui.Dim);
    readonly VBoxContainer _items = new();
    readonly ScrollContainer _scroll = new() { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };

    public ChecklistWindow() : base("Test Checklist", new Vector2(800, 650))
    {
        _tabs.AddThemeConstantOverride("h_separation", 6);
        Body.AddChild(_tabs);

        // Test-environment strip: shows whether test-prep has finished, so the user knows when it is safe to test.
        var prepRow = new HBoxContainer();
        prepRow.AddThemeConstantOverride("separation", 10);
        prepRow.AddChild(_prepText);
        _prepBtn = Ui.Btn("Run prep again", () => RunPrep?.Invoke(Number), 14);
        prepRow.AddChild(_prepBtn);
        _prepBox.AddThemeStyleboxOverride("panel", Ui.Box(new Color(0.1f, 0.1f, 0.12f), Ui.Border, 1, 3, 8));
        _prepBox.AddChild(prepRow);
        _prepBox.Visible = false;
        Body.AddChild(_prepBox);
        _head.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        Body.AddChild(_head);
        _note = Ui.Rich("", 14, Ui.Dim);
        Body.AddChild(_note);

        var prog = new HBoxContainer();
        prog.AddThemeConstantOverride("separation", 10);
        prog.AddChild(_bar);
        prog.AddChild(_count);
        Body.AddChild(prog);

        _items.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _items.AddThemeConstantOverride("separation", 4);
        _scroll.AddChild(_items);
        Body.AddChild(_scroll);

        var foot = new HBoxContainer();
        foot.AddThemeConstantOverride("separation", 8);
        foot.AddChild(Ui.Btn("Complete testing", () => CompleteTesting?.Invoke(Number)));
        foot.AddChild(Ui.Btn("Open in Claude", () => OpenClaude?.Invoke(Number)));
        foot.AddChild(Ui.Btn("Project Dashboard", () => OpenDashboard?.Invoke()));
        foot.AddChild(Ui.L("Ticking a step confirms it and saves to the note. Agents never tick for you.", 13, Ui.Dim));
        Body.AddChild(foot);
    }

    public void ShowFor(string number, List<ChecklistSection> data)
    {
        Number = number;
        _data = data;
        Rebuild();
        OpenWindow();
    }

    public void UpdateData(List<ChecklistSection> data)
    {
        _data = data;
        if (Visible) Rebuild();
    }

    /// <summary>Update the test-environment strip from the current prep state.</summary>
    public void RefreshPrep()
    {
        var info = PrepFor(Number);
        _prepBox.Visible = Number != "";
        if (Number == "") return;

        Color col;
        string text;
        _prepBtn.Text = info == null ? "Prepare test environment" : "Run prep again";
        switch (info?.State)
        {
            case null:
                col = Ui.Dim;
                text = "Test environment not prepared yet.";
                break;
            case PrepState.Running:
                col = Ui.Blue;
                text = "Preparing the test environment in the background (test-prep). Wait for this to say ready before you test.";
                break;
            case PrepState.Done:
                col = Ui.Green;
                text = $"Test environment ready ({info!.Finished:HH:mm}). {info.Message}";
                break;
            default:
                col = Ui.Red;
                text = "Test environment prep failed. " + info!.Message;
                break;
        }
        _prepText.Text = text;
        _prepText.AddThemeColorOverride("font_color", col);
        _prepBtn.Disabled = info?.State == PrepState.Running;
        _prepBox.AddThemeStyleboxOverride("panel", Ui.Box(new Color(col.R * 0.15f, col.G * 0.15f, col.B * 0.15f), col, 1, 3, 8));
    }

    void Rebuild()
    {
        RefreshPrep();
        Clear(_tabs);
        Clear(_items);

        foreach (var s in _data)
        {
            var section = s;
            var b = new Button
            {
                Text = $"{section.Number.Replace(Settings.ProjectPrefix + "-", "").TrimStart('0')}  {section.DoneCount}/{section.Items.Count}",
                ToggleMode = true,
                ButtonPressed = section.Number == Number,
                FocusMode = FocusModeEnum.None,
            };
            b.AddThemeFontSizeOverride("font_size", 14);
            b.Pressed += () => { Number = section.Number; ProjectPicked?.Invoke(Number); Rebuild(); };
            _tabs.AddChild(b);
        }

        var cur = _data.Find(x => x.Number == Number);
        if (cur == null)
        {
            bool missing = Number != "" && _data.Count > 0;
            _head.Text = _data.Count == 0 ? "No test checklist found in the vault."
                       : missing ? $"{Number} has no test checklist yet." : "Pick a project above.";
            _note.Text = missing
                ? $"Its agent has not written a {Number} section in Test Checklist.md. Open them in Claude and ask for one, or pick another project above."
                : "";
            _bar.MaxValue = 1; _bar.Value = 0; _count.Text = "";
            return;
        }

        _head.Text = $"{cur.Number}  -  {cur.Title}";
        _note.Text = Ui.Md2Bb(cur.Note);
        _bar.MaxValue = Math.Max(1, cur.Items.Count);
        _bar.Value = cur.DoneCount;
        _count.Text = $"{cur.DoneCount} of {cur.Items.Count} steps confirmed";

        int next = cur.Items.FindIndex(i => !i.Done);
        for (int idx = 0; idx < cur.Items.Count; idx++)
        {
            int index = idx;
            var item = cur.Items[idx];
            bool isNext = idx == next;

            var row = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            row.AddThemeStyleboxOverride("panel", isNext
                ? Ui.Box(new Color(0.20f, 0.16f, 0.06f), Ui.Gold, 1, 3, 6)
                : Ui.Box(Ui.Card, null, 0, 3, 6));
            var hb = new HBoxContainer();
            hb.AddThemeConstantOverride("separation", 10);

            var box = Ui.Check("", item.Done, 17); box.SizeFlagsVertical = SizeFlags.ShrinkBegin;
            box.Toggled += _ =>
            {
                try { VaultNotes.ToggleChecklist(Number, index); }
                catch (Exception ex) { GD.PrintErr($"Could not update the checklist note: {ex.Message}"); }
                Changed?.Invoke();
            };
            hb.AddChild(box);

            var text = Ui.Rich(item.Text, 15, item.Done ? Ui.Dim : Ui.Text);
            text.Text = (isNext ? "[color=#ffd15a][b]NEXT[/b][/color]  " : "") + (item.Done ? "[s]" + Ui.Md2Bb(item.Text) + "[/s]" : Ui.Md2Bb(item.Text));
            hb.AddChild(text);
            row.AddChild(hb);
            _items.AddChild(row);
        }
    }
}
