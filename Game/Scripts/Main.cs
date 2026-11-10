using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;

// Root: the 3D world (angled overhead camera), a post-process overlay, the HUD and the popup windows.
// The agents' states come live from the Agent Viewer notes in the vault (Project Dashboard / Test Checklist).
// Keys: WASD/arrows walk, Shift run, E speak, 1-3 choose, Tab project dashboard, C test checklist,
//       T open the nearby agent in Claude, wheel zoom, Esc close, F1-F7 demo states for the nearby agent.
public partial class Main : Control
{
    Hub3D _hub = null!;
    Label _banner = null!, _bannerReady = null!, _hint = null!, _toast = null!, _help = null!;
    PanelContainer _dialog = null!;
    TextureRect _face = null!;
    Label _dlgName = null!, _dlgText = null!, _dlgOptions = null!;
    ScrollContainer _dlgScroll = null!;
    bool _dlgOverflows; // the message is taller than the box may grow: it scrolls (wheel, PgUp/PgDn)

    /// <summary>
    /// Size the message area to its text: as tall as the wrapped text needs, up to about 60% of the screen together
    /// with the name and the options; beyond that the text scrolls.
    /// </summary>
    void FitDialogue()
    {
        float need = _dlgText.GetLineCount() * (_dlgText.GetLineHeight() + _dlgText.GetThemeConstant("line_spacing")) + 8;
        float max = Mathf.Max(160, GetViewportRect().Size.Y * 0.6f - 150);
        _dlgOverflows = need > max;
        float h = Mathf.Clamp(need, 120, max);
        if (Mathf.Abs(_dlgScroll.CustomMinimumSize.Y - h) > 1) _dlgScroll.CustomMinimumSize = new Vector2(0, h);
    }
    DashboardWindow _dashWin = null!;
    ChecklistWindow _checkWin = null!;

    // live notes
    DashboardData _dash = new();
    List<ChecklistSection> _checks = new();
    string _stamp = "";
    double _pollT;

    // dialogue
    AgentFigure? _talking;
    readonly List<(string label, Action act)> _actions = new();
    double _typed, _toastLeft;
    int _sel; // highlighted dialogue option (Up/Down to move, Enter to confirm)

    // headless/self-test hooks
    string? _shotPath, _openArg;
    readonly List<(Key key, int frames)> _script = new();
    int _scriptIdx = -1, _scriptCount, _frame, _shotFrame = 45;
    bool _keyDown;

    bool AnyWindow => _dashWin.Visible || _checkWin.Visible;

    public override void _Ready()
    {
        // Start maximized: Windows then sizes the game to the usable screen, so nothing sits behind the taskbar.
        if (DisplayServer.GetName() != "headless") DisplayServer.WindowSetMode(DisplayServer.WindowMode.Maximized);

        foreach (var a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("--shot=")) _shotPath = a.Substring(7);
            else if (a.StartsWith("--wait=")) _shotFrame = int.Parse(a.Substring(7));
            else if (a.StartsWith("--open=")) _openArg = a.Substring(7);
            else if (a.StartsWith("--script="))
                foreach (var tok in a.Substring(9).Split(','))
                {
                    var p = tok.Split(':');
                    _script.Add((Enum.Parse<Key>(p[0], true), int.Parse(p[1])));
                }
        }

        var vc = new SubViewportContainer { Stretch = true, StretchShrink = 1, TextureFilter = TextureFilterEnum.Linear };
        vc.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(vc);
        var vp = new SubViewport { RenderTargetUpdateMode = SubViewport.UpdateMode.Always, Msaa3D = Viewport.Msaa.Msaa4X };
        vc.AddChild(vp);
        _hub = new Hub3D();
        vp.AddChild(_hub);

        foreach (var a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("--at="))
            {
                var p = a.Substring(5).Split(',');
                _hub.Player.Position = new Vector3(float.Parse(p[0]), 0, float.Parse(p[1]));
            }
            else if (a.StartsWith("--place=")) // test hook: --place=Name:x,z puts an agent somewhere (e.g. against a wall)
            {
                var np = a.Substring(8).Split(':');
                var xz = np[1].Split(',');
                var f = _hub.Figures.Find(x => x.AgentName == np[0]);
                if (f != null) f.Position = new Vector3(float.Parse(xz[0]), 0, float.Parse(xz[1]));
            }
        }

        var post = new ColorRect { MouseFilter = MouseFilterEnum.Ignore, Color = Colors.White };
        post.SetAnchorsPreset(LayoutPreset.FullRect);
        post.Material = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/post.gdshader") };
        AddChild(post);

        AddChild(new AttentionOverlay { Hub = _hub });
        BuildHud();

        _dashWin = new DashboardWindow { Position = new Vector2(24, 40), FaceFor = FaceForProject };
        _checkWin = new ChecklistWindow { Position = new Vector2(440, 56) };
        AddChild(_dashWin);
        AddChild(_checkWin);
        _dashWin.OpenChecklist += ShowChecklist;
        _dashWin.OpenClaude += OpenClaudeFor;
        _dashWin.Locate += LocateAgent;
        _dashWin.Changed += ReloadVault;
        _checkWin.OpenClaude += OpenClaudeFor;
        _checkWin.OpenDashboard += () => _dashWin.OpenWindow();
        _checkWin.CompleteTesting += CompleteTesting;
        _checkWin.PrepFor = TestPrep.Get;
        _checkWin.RunPrep += n => { TestPrep.Reset(n); StartPrep(n); _checkWin.RefreshPrep(); };
        _checkWin.ProjectPicked += n => { PrepIfNeeded(n); _checkWin.RefreshPrep(); };
        _checkWin.Changed += ReloadVault;

        ReloadVault();
        // Toasts for later changes only (the first load just sets the starting states).
        foreach (var f in _hub.Figures) f.StateChanged += OnAgentState;
        _hub.FigureAdded += f => f.StateChanged += OnAgentState;
        SessionCast.Init();
        PollSessionCast();

        if (_script.Count > 0 && _shotFrame == 45) _shotFrame = 30;
        if (_openArg == "dashboard") _dashWin.OpenWindow();
        else if (_openArg != null && _openArg.StartsWith("checklist:")) ShowChecklist(_openArg.Substring(10));
    }

    // ---- live notes -------------------------------------------------------------------

    void ReloadVault()
    {
        if (!Settings.ProjectWorkflow) return; // base mode: only Claude sessions, no dashboard/checklist notes
        try
        {
            _checks = VaultNotes.LoadChecklist();
            _dash = VaultNotes.LoadDashboard();
            _stamp = VaultNotes.Stamp();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Could not read the vault notes: {ex.Message}");
            return;
        }
        _hub.ApplyVault(_dash, _checks);
        _dashWin.UpdateData(_dash, _checks);
        _checkWin.UpdateData(_checks);
    }

    Texture2D? FaceForProject(string number) => _hub.Figures.Find(f => f.ProjectNumber == number)?.Portrait;

    /// <summary>
    /// Open a project's test checklist. If the project is ready to test, the local test environment is prepared
    /// first (the test-prep skill), once per time it becomes ready, so it is ready before the user tests.
    /// </summary>
    void ShowChecklist(string number)
    {
        PrepIfNeeded(number);
        _checkWin.ShowFor(number, _checks);
    }

    /// <summary>Start test prep for a project whose checklist is being shown, unless it already ran or is running.</summary>
    void PrepIfNeeded(string number)
    {
        if (number != "" && TestPrep.Get(number) == null) StartPrep(number);
    }

    void StartPrep(string number)
    {
        var f = _hub.Figures.Find(x => x.ProjectNumber == number);
        if (f == null) return;
        TestPrep.Begin(number);
        Toast($"Preparing the test environment for {f.AgentName} ({number}) in the background...", 6);
        GD.Print(TestPrep.Launch(number, ProjectRepo.Find(number).primary));
    }

    void OnPrepChanged(string number)
    {
        var info = TestPrep.Get(number);
        if (info == null) return;
        string who = _hub.Figures.Find(x => x.ProjectNumber == number)?.AgentName ?? number;
        switch (info.State)
        {
            case PrepState.Done:
                Toast($"{who}: test environment ready. Start at step 1 of the checklist.", 8);
                break;
            case PrepState.Failed:
                Toast($"{who}: test prep failed. {info.Message}", 8);
                break;
        }
        _checkWin.RefreshPrep();
    }

    void ShowDashboard() => _dashWin.OpenWindow();

    void OpenClaudeFor(string number)
    {
        var f = _hub.Figures.Find(x => !x.IsSession && x.ProjectNumber == number);
        // An agent with no session yet is not in the dungeon: start its first session from its projects.json entry.
        var entry = f == null ? _hub.ProjectEntry(number) : null;
        if (f == null && entry == null) return;
        string agent = f?.AgentName ?? entry!.Value.Agent, title = f?.ProjectName ?? entry!.Value.Project;
        Toast($"Opening {agent} in Claude...", 2);
        var repos = ProjectRepo.Find(number);
        string dir = repos.primary != "" ? repos.primary : f?.ProjectDir ?? entry!.Value.Dir; // the terminal opens in the project's git repo
        var extra = repos.extra;
        string status = f == null ? "" : f.StatusRaw != "" ? f.StatusRaw : AgentStateInfo.Label(f.State);
        string needs = f == null ? "" : Briefing.NeedsRaw(f.StatusRaw, f.Notes, f.Plan);
        string plan = f?.Plan ?? "", notes = f?.Notes ?? "";
        string tests = Briefing.TestSummary(_checks.Find(c => c.Number == number));
        System.Threading.Tasks.Task.Run(() =>
        {
            var msg = AgentLauncher.Open(agent, number, title, status, needs, plan, notes, tests, dir, extra,
                p => Callable.From(() => { Toast(p, 30); GD.Print(p); }).CallDeferred());
            Callable.From(() => { Toast(msg, 6); GD.Print(msg); }).CallDeferred();
        });
    }

    void LocateAgent(string number)
    {
        var f = _hub.Figures.Find(x => x.ProjectNumber == number);
        if (f == null) return;
        var spot = f.Position + new Vector3(0, 0, 2.2f);
        if (!_hub.Map.IsWalkable(spot, 0.3f)) spot = f.Position + new Vector3(2.2f, 0, 0);
        if (_hub.Map.IsWalkable(spot, 0.3f)) _hub.Player.Position = spot;
        _dashWin.CloseWindow();
        Toast($"You walk over to {f.AgentName}.", 2.5);
    }

    void OnAgentState(AgentFigure a, AgentState from, AgentState to)
    {
        // Once a project is no longer ready to test, its next "ready" is a fresh one and needs a fresh prep.
        if (to != AgentState.AwaitingTest && to != AgentState.ReadyForTesting) TestPrep.Reset(a.ProjectNumber);
        switch (to)
        {
            case AgentState.AwaitingTest:
            case AgentState.ReadyForTesting:
                Toast($"{a.AgentName}: {a.ProjectNumber} is ready for testing. Press C near them, or Tab, for the checklist.", 8);
                break;
            case AgentState.AwaitingUser:
                Toast($"{a.AgentName} was struck down by the {a.MonsterDisplay}. They need your word.", 7);
                break;
            case AgentState.Working when from == AgentState.AwaitingUser:
                Toast($"{a.AgentName} rises: the {a.MonsterDisplay} is driven off.", 5);
                break;
        }
    }

    // ---- HUD ---------------------------------------------------------------------------

    Label MakeLabel(int size, Color color)
    {
        var l = new Label { MouseFilter = MouseFilterEnum.Ignore };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color);
        l.AddThemeColorOverride("font_outline_color", Colors.Black);
        l.AddThemeConstantOverride("outline_size", 6);
        return l;
    }

    void BuildHud()
    {
        _help = MakeLabel(14, new Color(0.7f, 0.7f, 0.72f));
        _help.Text = Settings.ProjectWorkflow
            ? "WASD walk   Shift run   E speak   Tab dashboard   C checklist   T open in Claude   Wheel zoom   Esc close"
            : "WASD walk   Shift run   E speak   T open in Claude   Wheel zoom   Esc close";
        _help.Position = new Vector2(12, 8);
        AddChild(_help);

        _banner = MakeLabel(26, new Color(1f, 0.32f, 0.30f));
        _banner.HorizontalAlignment = HorizontalAlignment.Center;
        _banner.SetAnchorsPreset(LayoutPreset.CenterTop);
        _banner.Position = new Vector2(-300, 34);
        _banner.CustomMinimumSize = new Vector2(600, 0);
        AddChild(_banner);

        _bannerReady = MakeLabel(24, new Color(1f, 0.82f, 0.35f));
        _bannerReady.HorizontalAlignment = HorizontalAlignment.Center;
        _bannerReady.SetAnchorsPreset(LayoutPreset.CenterTop);
        _bannerReady.Position = new Vector2(-300, 68);
        _bannerReady.CustomMinimumSize = new Vector2(600, 0);
        AddChild(_bannerReady);

        _hint = MakeLabel(22, new Color(0.9f, 0.88f, 0.82f));
        _hint.HorizontalAlignment = HorizontalAlignment.Center;
        _hint.SetAnchorsPreset(LayoutPreset.CenterBottom);
        _hint.Position = new Vector2(-440, -90);
        _hint.CustomMinimumSize = new Vector2(880, 0);
        AddChild(_hint);

        _toast = MakeLabel(21, new Color(0.96f, 0.92f, 0.65f));
        _toast.HorizontalAlignment = HorizontalAlignment.Center;
        _toast.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _toast.SetAnchorsPreset(LayoutPreset.CenterTop);
        _toast.Position = new Vector2(-450, 108);
        _toast.CustomMinimumSize = new Vector2(900, 0);
        AddChild(_toast);

        // Dialogue box: dark red panel along the bottom with the speaker's portrait on the left.
        _dialog = new PanelContainer { Visible = false, MouseFilter = MouseFilterEnum.Ignore };
        var sb = new StyleBoxFlat { BgColor = new Color(0.30f, 0.04f, 0.06f, 0.96f), BorderColor = new Color(0.78f, 0.74f, 0.68f, 0.9f) };
        sb.SetBorderWidthAll(2);
        sb.SetContentMarginAll(14);
        _dialog.AddThemeStyleboxOverride("panel", sb);
        _dialog.SetAnchorsPreset(LayoutPreset.BottomWide);
        _dialog.OffsetTop = -300;
        _dialog.OffsetBottom = -14;
        _dialog.OffsetLeft = 60;
        _dialog.OffsetRight = -60;
        _dialog.GrowVertical = GrowDirection.Begin; // a long message makes the box taller upward, never off the bottom
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 20);
        _face = new TextureRect
        {
            CustomMinimumSize = new Vector2(150, 200),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = TextureFilterEnum.Nearest,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        row.AddChild(_face);
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _dlgName = MakeLabel(20, new Color(0.9f, 0.82f, 0.62f));
        _dlgText = MakeLabel(21, new Color(0.96f, 0.93f, 0.88f));
        _dlgText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _dlgText.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _dlgOptions = MakeLabel(20, new Color(0.95f, 0.85f, 0.55f));
        // The message scrolls inside the box once it is taller than the box may grow (see FitDialogue).
        _dlgScroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 120),
        };
        _dlgScroll.AddChild(_dlgText);
        col.AddChild(_dlgName);
        col.AddChild(_dlgScroll);
        col.AddChild(_dlgOptions);
        row.AddChild(col);
        _dialog.AddChild(row);
        AddChild(_dialog);
    }

    // ---- dialogue ----------------------------------------------------------------------

    readonly HashSet<string> _answered = new(); // questions answered with Yes/No this run (hidden until the agent replies)

    void OpenDialogue(AgentFigure f)
    {
        _talking = f;
        f.Paused = true;                       // the agent stops and turns to face you
        f.FaceToward(_hub.Player.Position);
        if (f.IsSession) { OpenSessionDialogue(f); return; }
        if (f.PendingQuestion != null) { OpenQuestionDialogue(f, f.PendingQuestion); return; }

        string status = f.StatusRaw != "" ? Ui.Plain(f.StatusRaw) : AgentStateInfo.Label(f.State);
        if (f.Activity != "") status = $"Right now: {f.Activity}. Status: {status}";
        string needs = Briefing.NeedsForUser(f.StatusRaw, f.Notes, f.Plan);
        string text;
        if (f.Notes == "" && needs == "") text = AgentStateInfo.Dialogue(f.AgentName, f.ProjectName, f.State, f.MonsterDisplay).text;
        else
        {
            text = status + ".";
            if (needs != "") text += "\n" + needs;
            var sec = _checks.Find(c => c.Number == f.ProjectNumber);
            if (sec != null && (f.State == AgentState.AwaitingTest || f.State == AgentState.ReadyForTesting))
                text += $"\n{sec.DoneCount} of {sec.Items.Count} test steps confirmed.";
        }
        if (f.State == AgentState.AwaitingUser && f.MonsterName != "") text = $"The {f.MonsterDisplay} struck me down.\n" + text;

        string number = f.ProjectNumber;
        _actions.Clear();
        bool testing = f.State == AgentState.AwaitingTest || f.State == AgentState.ReadyForTesting;
        string checklistLabel = _checks.Any(c => c.Number == number) ? "Test checklist" : "Test checklist (not written yet)";
        if (testing)
        {
            _actions.Add(("Complete testing (commit and merge to QA)", () => CompleteTesting(number)));
            _actions.Add((checklistLabel, () => ShowChecklist(number)));
        }
        if (f.State == AgentState.AwaitingUser)
            _actions.Add(("Open in Claude to discuss it", () => OpenClaudeFor(number)));
        _actions.Add(("Read the full briefing", () => _dashWin.FocusProject(number)));
        if (f.State != AgentState.AwaitingUser)
            _actions.Add(("Open in Claude", () => OpenClaudeFor(number)));
        if (!testing)
            _actions.Add((checklistLabel, () => ShowChecklist(number)));

        _sel = 0;
        _dlgName.Text = $"{f.AgentName}  -  {f.ProjectNumber}  {f.ProjectName}";
        _dlgText.Text = text;
        _dlgText.VisibleCharacters = 0;
        _typed = 0;
        _face.Texture = f.Portrait;
        _dlgOptions.Text = "";
        _dialog.Visible = true;
        _dlgScroll.ScrollVertical = 0;
    }

    /// <summary>The agent ended its last reply on a yes/no question: show it with Yes and No.</summary>
    void OpenQuestionDialogue(AgentFigure f, SessionActivity.Question q)
    {
        string number = f.ProjectNumber, agent = f.AgentName;
        _actions.Clear();
        _actions.Add(("Yes", () => AnswerQuestion(f, q, true)));
        _actions.Add(("No", () => AnswerQuestion(f, q, false)));
        _actions.Add(("Open in Claude", () => OpenClaudeFor(number)));

        _sel = 0;
        _dlgName.Text = $"{agent}  -  {number}  {f.ProjectName}";
        _dlgText.Text = "I need a yes or no from you.\n" + Ui.Plain(q.Text);
        _dlgText.VisibleCharacters = 0;
        _typed = 0;
        _face.Texture = f.Portrait;
        _dlgOptions.Text = "";
        _dialog.Visible = true;
        _dlgScroll.ScrollVertical = 0;
    }

    void AnswerQuestion(AgentFigure f, SessionActivity.Question q, bool yes)
    {
        string agent = f.AgentName, number = f.ProjectNumber;
        _answered.Add(q.Id);
        f.SetQuestion(null);
        string dir;
        lock (_repoDirs)
            if (!_repoDirs.TryGetValue(number, out dir!)) _repoDirs[number] = dir = ProjectRepo.Find(number).primary;
        Toast($"Giving {agent} your answer: {(yes ? "yes" : "no")}...", 30);
        System.Threading.Tasks.Task.Run(() =>
        {
            var msg = QaHandoff.SendAnswer(agent, number, dir, q, yes);
            Callable.From(() =>
            {
                if (!msg.Contains("has your answer")) _answered.Remove(q.Id); // not delivered: let the question show again
                Toast(msg, 8); GD.Print(msg);
            }).CallDeferred();
        });
    }

    /// <summary>A session character speaks from its session's last-turn summary.</summary>
    void OpenSessionDialogue(AgentFigure f)
    {
        string text = f.State switch
        {
            AgentState.Working => f.Activity != "" ? $"I am in the middle of it: {char.ToLowerInvariant(f.Activity[0])}{f.Activity[1..]}." : "I am in the middle of it. Do not disturb me yet.",
            AgentState.AwaitingUser => (f.MonsterName != "" ? $"The {f.MonsterDisplay} struck me down." : "Something struck me down."),
            AgentState.Complete => "My work here is done.",
            _ => "I am resting until you call on me.",
        };
        if (f.SessionDetail != "") text += $"\n{Ui.Plain(f.SessionDetail)}.";
        if (f.SessionNeeds != "") text += $"\nI need you to: {Ui.Plain(f.SessionNeeds)}";

        _actions.Clear();
        _actions.Add(("Open in Claude", () => OpenSession(f)));
        _actions.Add(("Send them away (no character for this session)", () => SendAway(f)));

        _sel = 0;
        _dlgName.Text = $"{f.AgentName}  -  {f.Badge}  {f.ProjectName}";
        _dlgText.Text = text;
        _dlgText.VisibleCharacters = 0;
        _typed = 0;
        _face.Texture = f.Portrait;
        _dlgOptions.Text = "";
        _dialog.Visible = true;
        _dlgScroll.ScrollVertical = 0;
    }

    void CloseDialogue()
    {
        _dialog.Visible = false;
        if (_talking != null) _talking.Paused = false;
        _talking = null;
    }

    void Choose(int index)
    {
        if (_talking == null || index >= _actions.Count) return;
        var act = _actions[index].act;
        CloseDialogue();
        act();
    }

    void Toast(string msg, double seconds = 3.5) { _toast.Text = msg; _toastLeft = seconds; }

    // ---- input -------------------------------------------------------------------------

    public override void _Input(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true } mb && !AnyWindow && !_dialog.Visible)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp) _hub.Zoom(-1.5f);
            else if (mb.ButtonIndex == MouseButton.WheelDown) _hub.Zoom(1.5f);
        }

        if (e is not InputEventKey { Pressed: true, Echo: false } k) return;
        switch (k.Keycode)
        {
            case Key.E:
            case Key.Enter:
            case Key.Space:
                if (AnyWindow) break;
                if (_dialog.Visible)
                {
                    if (_dlgText.VisibleCharacters < _dlgText.Text.Length) _dlgText.VisibleCharacters = _dlgText.Text.Length; // skip typing
                    else if (_actions.Count > 0) Choose(_sel);                                                                  // confirm the highlighted option
                    else CloseDialogue();
                }
                else if (_hub.Focused != null) OpenDialogue(_hub.Focused);
                break;
            case Key.Up:
            case Key.Down:
                if (_dialog.Visible && _actions.Count > 0)
                    _sel = (_sel + (k.Keycode == Key.Down ? 1 : _actions.Count - 1)) % _actions.Count;
                break;
            case Key.Pageup:
            case Key.Pagedown:
                if (_dialog.Visible) _dlgScroll.ScrollVertical += (k.Keycode == Key.Pagedown ? 1 : -1) * (int)(_dlgScroll.Size.Y * 0.8f);
                break;
            case Key.Key1: if (_dialog.Visible) Choose(0); break;
            case Key.Key2: if (_dialog.Visible) Choose(1); break;
            case Key.Key3: if (_dialog.Visible) Choose(2); break;
            case Key.Escape:
                if (_checkWin.Visible) _checkWin.CloseWindow();
                else if (_dashWin.Visible) _dashWin.CloseWindow();
                else if (_dialog.Visible) CloseDialogue();
                break;
            case Key.Tab:
                if (!Settings.ProjectWorkflow) break;
                if (_dashWin.Visible) _dashWin.CloseWindow(); else _dashWin.OpenWindow();
                break;
            case Key.C:
                if (!Settings.ProjectWorkflow) break;
                if (_checkWin.Visible) _checkWin.CloseWindow();
                else ShowChecklist(_hub.Focused is { IsSession: false } pf ? pf.ProjectNumber : (_checks.Count > 0 ? _checks[0].Number : ""));
                break;
            case Key.T:
                if (_hub.Focused is { IsSession: true } sf) OpenSession(sf);
                else if (_hub.Focused != null) OpenClaudeFor(_hub.Focused.ProjectNumber);
                break;
            case Key.F1: _hub.Focused?.SetState(AgentState.Working); break;
            case Key.F2: _hub.Focused?.SetState(AgentState.AwaitingUser); break;
            case Key.F3: _hub.Focused?.SetState(AgentState.Starved); break;
            case Key.F4: _hub.Focused?.SetState(AgentState.Fallen); break;
            case Key.F5: _hub.Focused?.SetState(AgentState.ReadyForTesting); break;
            case Key.F6: _hub.Focused?.SetState(AgentState.PrepForTesting); break;
            case Key.F7: _hub.Focused?.SetState(AgentState.AwaitingTest); break;
        }
    }

    int _copyTick = 17; // first check about 3 seconds after start
    bool _copyPollRunning;
    readonly HashSet<string> _toldInQa = new(); // projects whose agent has been told the files are in QA, this run

    /// <summary>
    /// For each agent waiting on the hand copy from staging to QA, check the QA folder (QaCopyCheck). Show the count
    /// on the agent, and once every sync-listed file is there and matches the repo, tell the agent so it sets "In QA".
    /// </summary>
    void PollQaCopies()
    {
        if (_copyPollRunning || !Settings.ProjectWorkflow || Settings.QaRoot == "") return;
        var waiting = _hub.Figures.Where(f => !f.IsSession && QaCopyCheck.IsWaitingForCopy(f.StatusRaw))
                                  .Select(f => (f.AgentName, f.ProjectNumber)).ToList();
        foreach (var f in _hub.Figures.Where(f => f.CopyProgress != "" && !QaCopyCheck.IsWaitingForCopy(f.StatusRaw)))
            f.SetCopyProgress("");
        if (waiting.Count == 0) return;
        _copyPollRunning = true;
        System.Threading.Tasks.Task.Run(() =>
        {
            var results = new Dictionary<string, QaCopyCheck.Result>();
            foreach (var (agent, number) in waiting)
            {
                try
                {
                    string dir;
                    lock (_repoDirs)
                        if (!_repoDirs.TryGetValue(number, out dir!)) _repoDirs[number] = dir = ProjectRepo.Find(number).primary;
                    var r = QaCopyCheck.Check(number, dir);
                    results[number] = r;
                    bool tell;
                    lock (_toldInQa) tell = r.Done && _toldInQa.Add(number);
                    if (tell)
                    {
                        var msg = QaHandoff.SendFilesCopied(agent, number, dir, r.Total);
                        Callable.From(() => { Toast($"{number}: all {r.Total} file(s) are in QA. {msg}", 10); GD.Print(msg); }).CallDeferred();
                    }
                }
                catch (Exception ex) { GD.PrintErr($"QA folder check for {number} failed: {ex.Message}"); }
            }
            Callable.From(() =>
            {
                foreach (var f in _hub.Figures)
                    if (!f.IsSession && results.TryGetValue(f.ProjectNumber, out var r))
                        f.SetCopyProgress(r.Total == 0 ? "Waiting for the QA sync list"
                            : r.Done ? "Files are in QA" : $"Waiting for files to be copied ({r.Copied} of {r.Total})");
                _copyPollRunning = false;
            }).CallDeferred();
        });
    }

    bool _castPollRunning;
    int _castTick;
    readonly Dictionary<string, string> _repoDirs = new(); // project number -> its primary repo (looked up once)

    /// <summary>
    /// Keep one character per Claude app session (SessionCast): add characters for new sessions, update what each
    /// shows, and remove the ones whose session was archived or hidden. Sessions that belong to a project agent are
    /// left to that agent.
    /// </summary>
    void PollSessionCast()
    {
        if (_castPollRunning) return;
        _castPollRunning = true;
        var projects = _hub.ProjectEntries.Where(p => p.Number != "").ToList();
        System.Threading.Tasks.Task.Run(() =>
        {
            List<SessionCast.Info> sessions = new();
            var owned = new HashSet<string>();
            var hidden = new HashSet<string>();
            var shown = new Dictionary<string, bool>(); // project number -> whether its agent belongs in the dungeon
            try
            {
                sessions = SessionCast.Load();
                hidden = SessionCast.Hidden();
                if (sessions.Count > 0) // an empty list is more likely a failed read than no sessions: change nothing
                {
                    var all = AgentLauncher.AllSessions();
                    var archived = SessionCast.Archived();
                    var liveIds = new HashSet<string>(sessions.Select(s => s.HostId));
                    var links = SessionCast.Links();
                    bool changed = false;
                    foreach (var (agent, n, _) in projects)
                    {
                        // The agent's session: one titled for it, else the one it had before if still there.
                        string? id = sessions.Where(s => SessionCast.TitledFor(s.Title, agent, n))
                                             .OrderByDescending(s => s.LastActivity).FirstOrDefault()?.HostId;
                        if (id == null && links.TryGetValue(n, out var old) && liveIds.Contains(old)) id = old;
                        if (id == null && !links.ContainsKey(n))
                        {
                            // Never linked yet. An archived session titled for it means it had one and it is gone;
                            // otherwise take any session working in the project's folder.
                            var gone = archived.FirstOrDefault(a => SessionCast.TitledFor(a.Title, agent, n));
                            if (gone.HostId != null) { links[n] = gone.HostId; changed = true; }
                            else
                            {
                                string dir;
                                lock (_repoDirs)
                                    if (!_repoDirs.TryGetValue(n, out dir!)) _repoDirs[n] = dir = ProjectRepo.Find(n).primary;
                                var m = AgentLauncher.FindProjectSession(all, n, dir);
                                if (m != null && m.InApp && liveIds.Contains(m.HostId)) id = m.HostId;
                            }
                        }
                        if (id != null)
                        {
                            if (!links.TryGetValue(n, out var cur) || cur != id) { links[n] = id; changed = true; }
                            owned.Add(id);
                            shown[n] = true;
                        }
                        else shown[n] = false; // no session (never started, deleted or archived): no agent
                    }
                    if (changed) SessionCast.SaveLinks();
                }
            }
            catch (Exception ex) { GD.PrintErr($"Could not read the Claude app sessions: {ex.Message}"); }
            Callable.From(() =>
            {
                try
                {
                    bool added = false;
                    foreach (var (n, show) in shown)
                    {
                        bool had = _hub.Figures.Any(f => !f.IsSession && f.ProjectNumber == n);
                        var removed = _hub.SetProjectShown(n, show);
                        if (removed != null && _talking == removed) CloseDialogue();
                        added |= show && !had;
                    }
                    if (added && Settings.ProjectWorkflow) ReloadVault();
                    ApplySessionCast(sessions, owned, hidden);
                }
                finally { _castPollRunning = false; }
            }).CallDeferred();
        });
    }

    void ApplySessionCast(List<SessionCast.Info> sessions, HashSet<string> owned, HashSet<string> hidden)
    {
        var wanted = sessions.Where(s => !owned.Contains(s.HostId) && !hidden.Contains(s.HostId)).ToDictionary(s => s.HostId);
        foreach (var f in _hub.Figures.Where(f => f.IsSession).ToList())
        {
            if (wanted.TryGetValue(f.SessionHostId, out var s)) { f.ApplySession(s); wanted.Remove(f.SessionHostId); }
            else
            {
                if (_talking == f) CloseDialogue();
                _hub.RemoveFigure(f);
            }
        }
        SessionCast.Prune(sessions.Select(s => s.HostId));
        foreach (var s in wanted.Values.OrderBy(s => s.LastActivity))
        {
            string name = SessionCast.CharacterFor(s.HostId, _hub.Figures.Select(f => f.AgentName));
            _hub.AddSessionFigure(s, name);
        }
    }

    string _confirmComplete = "";
    double _confirmUntil;

    /// <summary>
    /// The user's testing passed: hand the project to its agent to commit, push and merge into qa (QaHandoff).
    /// With checklist steps still unticked, the first press only warns; pressing again within 10 seconds sends it.
    /// </summary>
    void CompleteTesting(string number)
    {
        var f = _hub.Figures.Find(x => !x.IsSession && x.ProjectNumber == number);
        if (f == null) { Toast("Pick a project first.", 3); return; }
        var sec = _checks.Find(c => c.Number == number);
        int done = sec?.DoneCount ?? 0, total = sec?.Items.Count ?? 0;
        double now = Time.GetTicksMsec() / 1000.0;
        if (done < total && !(_confirmComplete == number && now < _confirmUntil))
        {
            _confirmComplete = number;
            _confirmUntil = now + 10;
            Toast($"Only {done} of {total} test steps are ticked for {number}. Press Complete testing again within 10 seconds to send it to {f.AgentName} anyway.", 10);
            return;
        }
        _confirmComplete = "";

        string agent = f.AgentName;
        string dir;
        lock (_repoDirs)
            if (!_repoDirs.TryGetValue(number, out dir!)) _repoDirs[number] = dir = ProjectRepo.Find(number).primary;
        Toast($"Sending {agent} your go-ahead to commit and merge {number} to QA...", 30);
        System.Threading.Tasks.Task.Run(() =>
        {
            var msg = QaHandoff.Send(agent, number, dir, done, total,
                p => Callable.From(() => { Toast(p, 30); GD.Print(p); }).CallDeferred());
            Callable.From(() => { Toast(msg, 10); GD.Print(msg); }).CallDeferred();
        });
    }

    void OpenSession(AgentFigure f)
    {
        Toast($"Opening {f.AgentName}'s session in Claude...", 2);
        string id = f.SessionHostId, who = f.AgentName, title = f.ProjectName;
        System.Threading.Tasks.Task.Run(() =>
        {
            var msg = AgentLauncher.OpenAppSession(id, who, title);
            Callable.From(() => { Toast(msg, 6); GD.Print(msg); }).CallDeferred();
        });
    }

    void SendAway(AgentFigure f)
    {
        try
        {
            SessionCast.Hide(f.SessionHostId, f.ProjectName);
            Toast($"{f.AgentName} leaves the dungeon. Their session stays in Claude; remove it from data/hidden-sessions.json to bring them back.", 7);
            if (_talking == f) CloseDialogue();
            _hub.RemoveFigure(f);
        }
        catch (Exception ex) { Toast($"Could not hide that session: {ex.Message}", 6); }
    }

    bool _sessionPollRunning;

    /// <summary>
    /// Tell each agent whether its Claude session (app or terminal) is in the middle of a turn, so an agent that is
    /// working is not drawn lying down just because its dashboard row has not caught up yet.
    /// </summary>
    void PollSessions()
    {
        if (_sessionPollRunning) return;
        _sessionPollRunning = true;
        var numbers = _hub.Figures.Where(f => !f.IsSession).Select(f => f.ProjectNumber).Where(n => n != "").ToList();
        var hostIds = _hub.Figures.Where(f => f.IsSession).Select(f => f.SessionHostId).ToList();
        System.Threading.Tasks.Task.Run(() =>
        {
            var busy = new Dictionary<string, bool>();
            var activity = new Dictionary<string, string>(); // project number or app session id -> current step
            var questions = new Dictionary<string, SessionActivity.Question?>(); // project number -> pending yes/no
            try
            {
                var live = AgentLauncher.ListSessions();
                foreach (var n in numbers)
                {
                    var s = live.Where(s => s.Status == "busy" && AgentLauncher.FindProjectSession(new[] { s }, n, "") != null)
                                .OrderByDescending(s => s.LastActivity).FirstOrDefault();
                    busy[n] = s != null;
                    activity[n] = s != null ? SessionActivity.Current(s.Id, s.Cwd) : "";
                    // An idle project session that ended on a yes/no question.
                    var idle = s == null ? AgentLauncher.FindProjectSession(live, n, "") : null;
                    questions[n] = idle != null && idle.Status != "busy" ? SessionActivity.PendingQuestion(idle.Id, idle.Cwd) : null;
                }
                foreach (var h in hostIds)
                {
                    var s = live.FirstOrDefault(s => s.HostId == h && s.Status == "busy");
                    activity[h] = s != null ? SessionActivity.Current(s.Id, s.Cwd) : "";
                }
            }
            catch { }
            Callable.From(() =>
            {
                foreach (var f in _hub.Figures)
                {
                    string key = f.IsSession ? f.SessionHostId : f.ProjectNumber;
                    if (!f.IsSession && busy.TryGetValue(key, out bool b)) f.SetSessionBusy(b);
                    if (activity.TryGetValue(key, out var a)) f.SetActivity(a);
                    if (!f.IsSession && questions.TryGetValue(key, out var q))
                    {
                        if (q != null && f.PendingQuestion == null) Toast($"{f.AgentName} has a yes or no question for you. Speak to them (E).", 6);
                        f.SetQuestion(q != null && _answered.Contains(q.Id) ? null : q);
                    }
                }
                _sessionPollRunning = false;
            }).CallDeferred();
        });
    }

    // ---- frame update ------------------------------------------------------------------

    public override void _Process(double delta)
    {
        // Re-read the notes whenever an agent (or the user in Obsidian) saves them.
        _pollT += delta;
        if (_pollT >= 1.0)
        {
            _pollT = 0;
            if (Settings.ProjectWorkflow) try { if (VaultNotes.Stamp() != _stamp) ReloadVault(); } catch { }
            foreach (var n in TestPrep.Poll()) OnPrepChanged(n);
            PollSessions();
            if (++_castTick >= 3) { _castTick = 0; PollSessionCast(); }
            if (++_copyTick >= 20) { _copyTick = 0; PollQaCopies(); }
        }

        int waiting = 0, ready = 0;
        foreach (var f in _hub.Figures)
        {
            if (f.State == AgentState.AwaitingUser) waiting++;
            else if (f.State == AgentState.AwaitingTest || f.State == AgentState.ReadyForTesting) ready++;
        }
        _banner.Text = waiting > 0 ? $"{waiting} AGENT{(waiting > 1 ? "S" : "")} AWAITING YOU" : "";
        _bannerReady.Text = ready > 0 ? $"{ready} READY FOR TESTING" : "";

        _hub.Player.Enabled = !(AnyWindow || _dialog.Visible);

        var fo = _hub.Focused;
        _hint.Text = _dialog.Visible || AnyWindow || fo == null ? "" : fo.IsSession
            ? $"{fo.AgentName} - {fo.Badge}: {fo.ProjectName}\n{fo.StatusLine}    [E] Speak   [T] Open in Claude"
            : $"{fo.AgentName} - {fo.ProjectNumber} {fo.ProjectName}\n{fo.StatusLine}    [E] Speak   [C] Checklist   [T] Open in Claude";

        if (_dialog.Visible)
        {
            FitDialogue();
            _typed += delta * 60;
            int shown = Math.Min(_dlgText.Text.Length, (int)_typed);
            _dlgText.VisibleCharacters = Math.Max(_dlgText.VisibleCharacters, shown);
            if (_dlgText.VisibleCharacters >= _dlgText.Text.Length && _actions.Count > 0)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < _actions.Count; i++) sb.Append($"{(i == _sel ? ">" : " ")}  [{i + 1}]  {_actions[i].label}\n");
                sb.Append("   Up/Down choose    Enter confirm" + (_dlgOverflows ? "    PgUp/PgDn or wheel to scroll" : ""));
                _dlgOptions.Text = sb.ToString();
            }
        }

        if (_toastLeft > 0) { _toastLeft -= delta; if (_toastLeft <= 0) _toast.Text = ""; }

        RunSelfTest();
    }

    // Scripted key presses for headless checks: --script=W:60,E:2 (hold each key for N frames), --shot=<png>.
    void RunSelfTest()
    {
        _frame++;
        if (_script.Count > 0 && _frame >= 8)
        {
            if (_scriptIdx < 0) { _scriptIdx = 0; _scriptCount = 0; _keyDown = false; }
            if (_scriptIdx < _script.Count)
            {
                var (key, frames) = _script[_scriptIdx];
                if (!_keyDown)
                {
                    Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });
                    _keyDown = true; _scriptCount = 0;
                }
                else if (++_scriptCount >= frames)
                {
                    Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false });
                    _keyDown = false; _scriptIdx++; _scriptCount = 0;
                    GD.Print($"SELFTEST step {_scriptIdx}/{_script.Count} done; player at {_hub.Player.Position}; dialogue open: {_dialog.Visible}; dashboard: {_dashWin.Visible}; checklist: {_checkWin.Visible}");
                }
            }
            else if (_shotPath != null && ++_scriptCount == _shotFrame) Shoot();
        }
        else if (_script.Count == 0 && _shotPath != null && _frame == _shotFrame) Shoot();
    }

    void Shoot()
    {
        foreach (var f in _hub.Figures)
            GD.Print($"AGENT {f.AgentName,-6} {(f.IsSession ? "session:" + f.Badge : f.ProjectNumber)} {f.State,-15} '{f.StatusLine}' in {_hub.Map.RoomName(f.Position),-11} {(f.IsSession ? f.ProjectName : "")}");
        var img = GetViewport().GetTexture()?.GetImage(); // null when running headless: nothing is drawn
        if (img != null) { img.SavePng(_shotPath!); GD.Print($"Saved screenshot: {_shotPath}"); }
        else GD.Print("No screenshot (headless run).");
        GetTree().Quit();
    }
}
