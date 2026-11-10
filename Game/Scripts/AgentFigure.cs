using System.Collections.Generic;
using Godot;

// A character that roams the dungeon. The state is readable at a glance:
//  - Working / PrepForTesting: walks between rooms, torch lit
//  - AwaitingUser:   struck down where it stood: lies dead in a red sigil with a monster over it
//  - Fallen:         lies dead, no sigil, no monster
//  - Starved:        walks slowly, dim and grey, torch guttering
//  - ReadyForTesting / AwaitingTest: stands still and prompts the user (gold plate)
// The sprite shows its front, back, left or right view to match the direction it is walking.
// A name tag sits below the feet and a plate above the head shows what the agent is working on.
public partial class AgentFigure : Node3D
{
    const float SpriteHeight = 2.1f;
    const float MonsterShift = 4.6f;
    const float PrepSeconds = 12f; // demo: how long the prep-for-testing step takes

    public event System.Action<AgentFigure, AgentState, AgentState>? StateChanged;

    public string AgentName { get; set; } = "Agent";

    /// <summary>Whose art the character wears: its own by default, or a drawn character's when its name has none.</summary>
    public string? LookName { get; set; }
    public string ProjectName { get; set; } = "Project";
    public string ProjectNumber { get; set; } = "";
    public AgentState State { get; private set; } = AgentState.Working;
    public DungeonMap Map { get; set; } = null!;
    /// <summary>True while the player is talking to this agent: it stops and faces them.</summary>
    public bool Paused { get; set; }
    /// <summary>Close-up of the face for the dialogue box (top part of the front view).</summary>
    public Texture2D? Portrait { get; private set; }
    public bool NeedsAttention => AgentStateInfo.NeedsUser(State) || PendingQuestion != null;

    /// <summary>A yes/no question the agent's session ended its last reply with (answered with Yes/No in the dialogue).</summary>
    public SessionActivity.Question? PendingQuestion { get; private set; }

    public void SetQuestion(SessionActivity.Question? q)
    {
        if (q?.Id == PendingQuestion?.Id && q?.Text == PendingQuestion?.Text) return;
        PendingQuestion = q;
        if (_phase != null) _phase.Text = Trim(StatusLine, 44);
    }

    // What the project agent reports in the vault's Project Dashboard (live).
    public string StatusRaw { get; private set; } = "";
    public string PhaseText { get; private set; } = "";
    public string Notes { get; private set; } = "";
    public string Plan { get; private set; } = "";
    public string ProjectDir { get; set; } = "";

    /// <summary>For a character that plays a Claude app session rather than a project: the app's session id (local_...).</summary>
    public string SessionHostId { get; set; } = "";
    public bool IsSession => SessionHostId != "";
    /// <summary>First line of the plate above the head: the project number, or a session's folder name.</summary>
    public string Badge { get; set; } = "";
    string PlateTop => ProjectNumber != "" ? ProjectNumber : Badge;

    /// <summary>A session's last-turn summary (what it did) and what it needs from the user, for the dialogue box.</summary>
    public string SessionDetail { get; private set; } = "";
    public string SessionNeeds { get; private set; } = "";

    /// <summary>Show what the character's Claude app session is doing (see SessionCast for the mapping).</summary>
    public void ApplySession(SessionCast.Info s)
    {
        SessionDetail = s.Detail;
        SessionNeeds = s.NeedsAction;
        if (s.FolderName != Badge) { Badge = s.FolderName; if (_title != null) _title.Text = $"{PlateTop}\n{Trim(ProjectName, 26)}"; }
        ApplyVault($"{s.Phase} ({s.Detail})", s.NeedsAction, "", s.Title, s.State);
    }

    // The state the dashboard row reports, and whether the agent's Claude session is in the middle of a turn.
    AgentState _reported = AgentState.Working;
    bool _sessionBusy;

    static bool IsDown(AgentState s) => s == AgentState.AwaitingUser || s == AgentState.Fallen || s == AgentState.Idle;

    /// <summary>Apply the dashboard row for this agent. The state only changes when the reported status changes.</summary>
    public void ApplyVault(string statusRaw, string notes, string plan, string title, AgentState state)
    {
        Notes = notes;
        Plan = plan;
        if (title != "" && title != ProjectName) { ProjectName = title; if (_title != null) _title.Text = $"{PlateTop}\n{Trim(ProjectName, 26)}"; }
        int p = statusRaw.IndexOf(" (", System.StringComparison.Ordinal);
        PhaseText = p > 0 ? statusRaw.Substring(0, p) : statusRaw;
        bool changed = statusRaw != StatusRaw;
        StatusRaw = statusRaw;
        _reported = state;
        if (changed) SetState(_sessionBusy && IsDown(state) ? AgentState.Working : state);
    }

    /// <summary>The step the agent's session is on right now ("Merging branch into QA"), while it is busy; "" otherwise.</summary>
    public string Activity { get; private set; } = "";

    /// <summary>The detail in the status's parentheses ("Merged to QA (waiting for files to be copied)"), when short enough for the plate.</summary>
    public string StatusDetail
    {
        get
        {
            int a = StatusRaw.IndexOf(" (", System.StringComparison.Ordinal), b = StatusRaw.LastIndexOf(')');
            if (a < 0 || b <= a) return "";
            var d = StatusRaw.Substring(a + 2, b - a - 2).Trim();
            return IsSession || d.Length > 40 ? "" : d;
        }
    }

    /// <summary>What the plate's status line says: the live step while busy, else the status and its short detail.</summary>
    public string StatusLine
    {
        get
        {
            if (Activity != "") return Activity;
            if (PendingQuestion != null) return "Asks you: yes or no?";
            if (CopyProgress != "") return CopyProgress;
            string p = PhaseText != "" ? PhaseText : AgentStateInfo.Label(State);
            return StatusDetail != "" ? $"{p}: {StatusDetail}" : p;
        }
    }

    /// <summary>While the agent waits for the user's copy into QA: how far the QA folder check has got ("" otherwise).</summary>
    public string CopyProgress { get; private set; } = "";

    public void SetCopyProgress(string text)
    {
        if (text == CopyProgress) return;
        CopyProgress = text;
        if (_phase != null) _phase.Text = Trim(StatusLine, 44);
    }

    public void SetActivity(string activity)
    {
        if (activity == Activity) return;
        Activity = activity;
        if (_phase != null) _phase.Text = Trim(StatusLine, 44);
    }

    /// <summary>
    /// The agent's Claude session started or finished a turn. While it is busy the agent is plainly working, so a
    /// row that still says it is waiting, fallen or not started does not keep it lying down; when the turn ends it
    /// goes back to what the row reports. Other states (ready for testing, prepping) are left alone.
    /// </summary>
    public void SetSessionBusy(bool busy)
    {
        if (busy == _sessionBusy) return;
        _sessionBusy = busy;
        if (busy && IsDown(State)) SetState(AgentState.Working);
        else if (!busy && State == AgentState.Working && _reported != AgentState.Working) SetState(_reported);
    }

    // The monster that struck this agent down while it waits on the user.
    public string MonsterName { get; set; } = "";
    public string MonsterDisplay => MonsterName.Replace('_', ' ');

    SpriteSet _set = null!;
    Sprite3D _sprite = null!;
    Facing _facing = Facing.Down;
    OmniLight3D _torch = null!;
    MeshInstance3D _flame = null!, _shadow = null!, _sigil = null!, _stand = null!;
    OmniLight3D _sigilLight = null!;
    Label3D _nameTag = null!, _title = null!, _phase = null!;
    Node3D _plate = null!;
    MeshInstance3D _plateBg = null!;
    double _t;
    double _prepLeft;
    Color _tint = Colors.White;
    float _dist = 99f;

    // wandering
    readonly List<Vector3> _path = new();
    int _pi;
    double _idle = 0.4;
    readonly RandomNumberGenerator _rng = new();
    bool _walking;

    // monster
    Sprite3D? _monster;
    bool _monsterActive;
    float _monsterAlpha, _lunge;

    static readonly Dictionary<string, float> MonsterHeights = new()
    {
        ["Hollow_Wanderer"] = 2.7f, ["Choir_of_Faces"] = 3.0f, ["Weeping_Saint"] = 2.9f, ["Gullet_Crawler"] = 2.2f,
        ["Bound_Oracle"] = 3.2f, ["Throne_of_Whispers"] = 3.3f, ["Thorn_Bishop"] = 2.9f, ["Blind_Empress"] = 3.1f,
        ["Bone_Stalker"] = 2.4f, ["Rootbound_God"] = 3.3f, ["Stone_Lament"] = 3.0f, ["Twin_Soul"] = 2.6f,
    };

    bool IsDead => State == AgentState.AwaitingUser || State == AgentState.Fallen;

    static Color PhaseColor(AgentState s) => s switch
    {
        AgentState.AwaitingUser => new Color(1f, 0.3f, 0.28f),
        AgentState.ReadyForTesting => new Color(1f, 0.82f, 0.35f),
        AgentState.AwaitingTest => new Color(1f, 0.82f, 0.35f),
        AgentState.PrepForTesting => new Color(0.6f, 0.78f, 1f),
        AgentState.Fallen => new Color(0.6f, 0.6f, 0.62f),
        AgentState.Starved => new Color(0.7f, 0.7f, 0.68f),
        AgentState.Idle => new Color(0.62f, 0.62f, 0.65f),
        AgentState.Complete => new Color(0.62f, 0.86f, 0.62f),
        _ => new Color(0.7f, 0.9f, 0.7f),
    };

    public override void _Ready()
    {
        _rng.Randomize();
        _set = SpriteSet.Load(LookName ?? AgentName,() => SpriteFactory.Silhouette(new Color(0.35f, 0.2f, 0.18f), torch: false));
        var front = _set.Down;
        Portrait = new AtlasTexture { Atlas = front, Region = new Rect2(0, 0, front.GetWidth(), Mathf.Max(8, front.GetHeight() * 0.38f)) };

        _sprite = new Sprite3D
        {
            PixelSize = SpriteHeight / front.GetHeight(),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
            AlphaCut = SpriteBase3D.AlphaCutMode.OpaquePrepass,
            DoubleSided = true,
            Shaded = false, // brightness is set in _Process: bright near the player and their own torch, dim far away
        };
        AddChild(_sprite);
        SetFacing(Facing.Down);

        _shadow = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.6f, BottomRadius = 0.6f, Height = 0.01f, RadialSegments = 20, Rings = 1 },
            Position = new Vector3(0, 0.01f, 0),
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(0, 0, 0, 0.55f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            },
        };
        AddChild(_shadow);

        // The torch this agent carries.
        var iron = Look.Mat(new Color(0.12f, 0.11f, 0.12f));
        _stand = Look.Box(new Vector3(0.07f, 1.5f, 0.07f), new Vector3(0.8f, 0.75f, -0.1f), iron);
        AddChild(_stand);
        _torch = new OmniLight3D
        {
            Position = new Vector3(0.8f, 1.85f, -0.1f),
            LightColor = new Color(1.0f, 0.88f, 0.72f),
            OmniRange = 7.5f,
            LightEnergy = 1.8f,
        };
        AddChild(_torch);
        _flame = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.09f, Height = 0.18f, RadialSegments = 8, Rings = 4 },
            Position = _torch.Position,
            MaterialOverride = Look.Mat(new Color(1, 0.8f, 0.45f), emission: new Color(1, 0.7f, 0.35f), emissionEnergy: 3f),
        };
        AddChild(_flame);

        // Red summoning circle on the floor (shown while waiting on the user).
        _sigil = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(4.6f, 4.6f) },
            Position = new Vector3(0, 0.03f, 0),
            MaterialOverride = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/sigil.gdshader") },
            Visible = false,
        };
        AddChild(_sigil);
        _sigilLight = new OmniLight3D
        {
            Position = new Vector3(0, 0.6f, 0),
            LightColor = new Color(1, 0.08f, 0.08f),
            OmniRange = 4.5f,
            LightEnergy = 0f,
        };
        AddChild(_sigilLight);

        BuildLabels();
        SetState(State);
    }

    static string Trim(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "...";

    void BuildLabels()
    {
        // Name tag: sits below the feet.
        _nameTag = new Label3D
        {
            Text = AgentName,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            Modulate = new Color(0.85f, 0.85f, 0.87f),
            OutlineModulate = Colors.Black,
            PixelSize = 0.012f,
            FontSize = 30,
            Shaded = false,
            NoDepthTest = true,
        };
        AddChild(_nameTag);

        // Floating plate above the head: what the agent is working on, and which phase it is in.
        string title = $"{PlateTop}\n{Trim(ProjectName, 26)}";
        int longest = Mathf.Max(Mathf.Min(PlateTop.Length, 26), Mathf.Min(ProjectName.Length, 26));
        float w = Mathf.Max(longest * 0.18f, 2.0f) + 0.4f;
        _plate = new Node3D();
        _plateBg = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(w, 1.3f) },
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(0.02f, 0.02f, 0.03f, 0.68f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
            },
        };
        _plate.AddChild(_plateBg);
        _title = new Label3D
        {
            Text = title,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            Modulate = new Color(0.92f, 0.88f, 0.72f),
            OutlineModulate = Colors.Black,
            PixelSize = 0.012f,
            FontSize = 26,
            Shaded = false,
            NoDepthTest = true,
            Position = Look.CamUp * 0.2f,
            RenderPriority = 2,
            OutlineRenderPriority = 1,
        };
        _plate.AddChild(_title);
        _phase = new Label3D
        {
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            OutlineModulate = Colors.Black,
            PixelSize = 0.012f,
            FontSize = 24,
            Shaded = false,
            NoDepthTest = true,
            Position = Look.CamUp * -0.45f,
            RenderPriority = 2,
            OutlineRenderPriority = 1,
        };
        _plate.AddChild(_phase);
        AddChild(_plate);
    }

    void SetFacing(Facing f)
    {
        _facing = f;
        var tex = _set.Get(f);
        _sprite.Texture = tex;
        _sprite.Offset = new Vector2(0, tex.GetHeight() / 2f); // feet at the origin
    }

    static Facing FacingOf(Vector3 dir)
        => Mathf.Abs(dir.X) > Mathf.Abs(dir.Z) ? (dir.X < 0 ? Facing.Left : Facing.Right) : (dir.Z < 0 ? Facing.Up : Facing.Down);

    /// <summary>Turn to look at a point (used while talking to the player).</summary>
    public void FaceToward(Vector3 p)
    {
        if (_sprite == null || IsDead) return;
        var f = FacingOf(p - Position);
        if (f != _facing) SetFacing(f);
    }

    public void SetState(AgentState s)
    {
        var old = State;
        State = s;
        if (_sprite == null) return;

        _path.Clear(); _pi = 0; _idle = 0.5;
        if (s == AgentState.PrepForTesting) _prepLeft = PrepSeconds;
        _sigil.Visible = s == AgentState.AwaitingUser;
        _shadow.Visible = !IsDead;
        _stand.Visible = !IsDead;
        UpdateMonster();

        // Labels are placed along the screen-up direction so they land below the feet / above the head on screen.
        if (IsDead)
        {
            // Lie flat AND sideways (head to the west): seen from above, a head-up figure would look standing.
            SetFacing(Facing.Down);
            _sprite.Billboard = BaseMaterial3D.BillboardModeEnum.Disabled;
            _sprite.RotationDegrees = new Vector3(-90, 90, 0);
            _sprite.Position = new Vector3(SpriteHeight / 2f, 0.05f, 0);
            var mid = new Vector3(SpriteHeight / 2f, 0, 0);
            _nameTag.Position = mid + Look.CamUp * -1.3f;
            _plate.Position = mid + Look.CamUp * 4.3f;
        }
        else
        {
            _sprite.Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
            _sprite.RotationDegrees = Vector3.Zero;
            _sprite.Position = Look.TowardCamera * Look.SpriteDepthShift;
            _nameTag.Position = Look.CamUp * -0.5f;
            _plate.Position = Look.CamUp * (SpriteHeight + 0.95f);
        }
        _phase.Text = Trim(StatusLine, 44);
        _phase.Modulate = PhaseColor(s);
        _tint = s switch
        {
            AgentState.AwaitingUser => new Color(0.6f, 0.55f, 0.55f),
            AgentState.Fallen => new Color(0.38f, 0.38f, 0.42f),
            AgentState.Starved => new Color(0.62f, 0.62f, 0.6f),
            AgentState.ReadyForTesting or AgentState.AwaitingTest => new Color(1.1f, 1.06f, 0.92f),
            _ => Colors.White,
        };

        if (old != s) StateChanged?.Invoke(this, old, s);
    }

    /// <summary>
    /// Where the on-screen arrow points: just above the nameplate when it shows, else just above the head (or above
    /// the body when lying down).
    /// </summary>
    public Vector3 ArrowAnchor =>
        _plate != null && _plate.Visible ? _plate.GlobalPosition + Look.CamUp * 0.95f
        : IsDead ? GlobalPosition + new Vector3(SpriteHeight / 2f, 0, 0) + Look.CamUp * 0.9f
        : GlobalPosition + Look.CamUp * (SpriteHeight + 0.45f);

    /// <summary>Labels are drawn near the player, and always for agents that need the user.</summary>
    public void UpdateLabel(float distanceToPlayer)
    {
        _dist = distanceToPlayer;
        bool show = NeedsAttention || distanceToPlayer < 13f;
        _nameTag.Visible = show;
        _plate.Visible = show;
    }

    // ---- wandering -----------------------------------------------------------------------

    void Wander(float d)
    {
        _walking = false;
        bool canMove = !Paused && (State == AgentState.Working || State == AgentState.PrepForTesting || State == AgentState.Starved);
        if (!canMove || Map == null) return;

        if (_pi >= _path.Count)
        {
            _idle -= d;
            if (_idle > 0) return;
            bool slow = State == AgentState.Starved;
            _path.Clear(); _pi = 0;
            var dest = Map.RandomFloorNear(Position, slow ? 6f : 20f, _rng);
            if (dest.HasValue)
            {
                var p = Map.FindPath(Position, dest.Value);
                if (p != null) _path.AddRange(p);
            }
            _idle = slow ? _rng.RandfRange(3f, 7f) : _rng.RandfRange(0.6f, 3.5f);
            return;
        }

        var target = _path[_pi];
        var to = target - Position; to.Y = 0;
        float speed = State switch { AgentState.PrepForTesting => 2.1f, AgentState.Starved => 0.9f, _ => 1.8f };
        float step = speed * d;
        if (to.Length() <= step)
        {
            Position = new Vector3(target.X, 0, target.Z);
            _pi++;
        }
        else
        {
            var dir = to.Normalized();
            Position += dir * step;
            var f = FacingOf(dir);
            if (f != _facing) SetFacing(f);
        }
        _walking = true;
    }

    // ---- monster -------------------------------------------------------------------------

    void UpdateMonster()
    {
        bool want = State == AgentState.AwaitingUser && MonsterName != "";
        if (want && !_monsterActive)
        {
            _monsterActive = true;
            if (_monster == null)
            {
                var tex = SpriteFactory.FromFile(MonsterName);
                if (tex == null) { _monsterActive = false; return; }
                float h = MonsterHeights.TryGetValue(MonsterName, out var hh) ? hh : 2.8f;
                _monster = new Sprite3D
                {
                    Texture = tex,
                    PixelSize = h / tex.GetHeight(),
                    Offset = new Vector2(0, tex.GetHeight() / 2f),
                    Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                    TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
                    DoubleSided = true,
                    Shaded = false,
                    Modulate = new Color(1, 1, 1, 0),
                };
                AddChild(_monster);
            }
            _lunge = 0f;
        }
        else if (!want && _monsterActive) _monsterActive = false; // banished: fades out in AnimateMonster
    }

    void AnimateMonster(float d, float t)
    {
        if (_monster == null) return;
        _monsterAlpha = Mathf.MoveToward(_monsterAlpha, _monsterActive ? 1f : 0f, d * (_monsterActive ? 2.5f : 1.6f));
        _monster.Modulate = new Color(0.9f, 0.85f, 0.85f, _monsterAlpha);
        if (_monsterActive) _lunge = Mathf.MoveToward(_lunge, 1f, d * 2.2f);
        // Lunges in from the dark, then looms over the body, breathing. Pulled toward the camera so walls cannot cut it.
        float k = 1f - Mathf.Pow(1f - _lunge, 3f);
        var rest = new Vector3(1.5f, 0, -1.0f);
        var from = new Vector3(3.4f, 0, -3.4f);
        _monster.Position = from.Lerp(rest, k) + Look.TowardCamera * MonsterShift + new Vector3(0, 0.04f * Mathf.Abs(Mathf.Sin(t * 1.8f)), 0);
        float br = 1f + 0.025f * Mathf.Sin(t * 1.6f);
        _monster.Scale = new Vector3(br, br, 1f);
        if (!_monsterActive && _monsterAlpha <= 0.01f) { _monster.QueueFree(); _monster = null; }
    }

    // ---- frame update --------------------------------------------------------------------

    public override void _Process(double delta)
    {
        _t += delta;
        float d = (float)delta;
        float t = (float)_t;

        // Demo: the prep-for-testing step finishes by itself, then the user is prompted to test.
        if (State == AgentState.PrepForTesting && !Paused)
        {
            _prepLeft -= delta;
            if (_prepLeft <= 0) SetState(AgentState.AwaitingTest);
        }

        Wander(d);

        float torch = 1.8f, lift = 0f, stretch = 1f;
        switch (State)
        {
            case AgentState.Working:
            case AgentState.PrepForTesting:
                lift = _walking ? 0.07f * Mathf.Abs(Mathf.Sin(t * 9f)) : 0.03f * Mathf.Sin(t * 3f); break;
            case AgentState.Starved: torch = 0.3f; stretch = 0.9f + 0.015f * Mathf.Sin(t * 1.4f); break;
            case AgentState.ReadyForTesting:
            case AgentState.AwaitingTest: torch = 2.4f; lift = 0.04f * (0.5f + 0.5f * Mathf.Sin(t * 1.6f)); break;
            case AgentState.Idle: torch = 0.9f; break;
            case AgentState.Complete: torch = 2.0f; lift = 0.03f * (0.5f + 0.5f * Mathf.Sin(t * 1.4f)); break;
            case AgentState.AwaitingUser:
            case AgentState.Fallen: torch = 0f; break;
        }

        if (!IsDead)
        {
            _sprite.Position = Look.TowardCamera * Look.SpriteDepthShift + new Vector3(0, lift, 0);
            _sprite.Scale = new Vector3(1, stretch, 1);
            _shadow.Scale = new Vector3(1f - lift, 1, 1f - lift);
        }

        float flick = 0.97f + 0.03f * Mathf.Sin(t * 2.3f);
        _torch.LightEnergy = Mathf.Lerp(_torch.LightEnergy, torch * flick, 1f - Mathf.Exp(-6f * d));
        _flame.Visible = _torch.LightEnergy > 0.15f;

        float beat = 0.5f + 0.5f * Mathf.Sin(t * 1.2f);
        _sigilLight.LightEnergy = State == AgentState.AwaitingUser ? 0.9f + 0.5f * beat : 0f;

        // Brightness: lit near the player's torch and near the agent's own torch or sigil, dim far away.
        float b = Mathf.Clamp(1.25f - _dist / 13f, 0.3f, 1f);
        if (_torch.LightEnergy > 0.5f || State == AgentState.AwaitingUser) b = Mathf.Max(b, 0.7f);
        _sprite.Modulate = new Color(_tint.R * b, _tint.G * b, _tint.B * b, 1f);

        AnimateMonster(d, t);
    }
}
