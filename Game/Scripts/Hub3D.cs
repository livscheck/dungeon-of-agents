using System.Collections.Generic;
using System.Linq;
using Godot;

// The dungeon, seen from an angled overhead camera: nine grey stone rooms joined by corridors, with the
// agents roaming freely between them. Only torchlight shows anything; everything else is black.
// Project data comes from data/projects.json until Dungeon.Core drives this from live project-tracker data.
public partial class Hub3D : Node3D
{
    const float CamPitchDeg = Look.CamPitchDeg;
    const float CamDistance = 40f;

    public DungeonMap Map { get; } = new();
    public PlayerController Player { get; private set; } = null!;
    public Camera3D Camera { get; private set; } = null!;
    public List<AgentFigure> Figures { get; } = new();
    public AgentFigure? Focused { get; private set; }

    readonly List<(OmniLight3D light, float baseE, float phase)> _flickers = new();
    double _t;
    float _zoom = 18f;

    record Entry(string Agent, string Number, string Project, AgentState State, string Room, string Dir);
    Entry[] _entries = System.Array.Empty<Entry>();

    // Which monster strikes each agent down when it ends up waiting on the user (demo assignment).
    static readonly string[] Monsters =
        { "Bone_Stalker", "Gullet_Crawler", "Hollow_Wanderer", "Thorn_Bishop", "Weeping_Saint", "Stone_Lament" };

    static readonly string[] DefaultRooms = { "Armory", "Library", "Crypt", "Vault", "Shrine", "Catacombs", "Cells", "Ossuary" };

    static Entry[] LoadProjects()
    {
        var list = new List<Entry>();
        try
        {
            using var f = FileAccess.Open("res://data/projects.json", FileAccess.ModeFlags.Read);
            using var doc = System.Text.Json.JsonDocument.Parse(f.GetAsText());
            int i = 0;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                string Get(string k) => e.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
                var state = System.Enum.TryParse<AgentState>(Get("state"), true, out var s) ? s : AgentState.Working;
                var room = Get("room");
                list.Add(new Entry(Get("agent"), Get("number"), Get("project"), state, room != "" ? room : DefaultRooms[i % DefaultRooms.Length], Get("dir")));
                i++;
            }
        }
        catch (System.Exception ex) { GD.PrintErr($"Could not read data/projects.json: {ex.Message}"); }
        return list.ToArray();
    }

    public override void _Ready()
    {
        _entries = LoadProjects();
        BuildEnvironment();
        Map.BuildGeometry(this, AddCandelabra);

        for (int i = 0; i < _entries.Length; i++) AddProjectFigure(i);

        Player = new PlayerController { Position = Map.RoomCenter("Great Hall") + new Vector3(0, 0, 3.5f), Walkable = p => Map.IsWalkable(p, 0.3f) };
        AddChild(Player);

        Camera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            Size = _zoom,
            Near = 0.1f,
            Far = 200f,
            Current = true,
            RotationDegrees = new Vector3(-CamPitchDeg, 0, 0),
        };
        AddChild(Camera);
        PlaceCamera(Player.Position, 1f);
    }

    public event System.Action<AgentFigure>? FigureAdded;

    /// <summary>Every project agent in data/projects.json, shown or not: (agent, project number, project folder).</summary>
    public List<(string Agent, string Number, string Dir)> ProjectEntries =>
        _entries.Select(e => (e.Agent, e.Number, e.Dir)).ToList();

    /// <summary>A project's agent name, title and folder from data/projects.json, shown or not.</summary>
    public (string Agent, string Project, string Dir)? ProjectEntry(string number)
    {
        int i = System.Array.FindIndex(_entries, e => e.Number == number);
        return i < 0 ? null : (_entries[i].Agent, _entries[i].Project, _entries[i].Dir);
    }

    AgentFigure AddProjectFigure(int i)
    {
        var e = _entries[i];
        var fig = new AgentFigure
        {
            AgentName = e.Agent,
            LookName = SessionCast.LookFor(e.Agent),
            ProjectName = e.Project,
            ProjectNumber = e.Number,
            ProjectDir = e.Dir,
            MonsterName = Monsters[i % Monsters.Length],
            Map = Map,
            Position = Map.RoomCenter(e.Room),
        };
        AddChild(fig);
        fig.SetState(e.State);
        Figures.Add(fig);
        FigureAdded?.Invoke(fig);
        return fig;
    }

    /// <summary>
    /// Show or remove a project agent. Returns the figure being removed (so the caller can close its dialogue),
    /// or null when nothing was removed.
    /// </summary>
    public AgentFigure? SetProjectShown(string number, bool shown)
    {
        var fig = Figures.Find(f => !f.IsSession && f.ProjectNumber == number);
        if (shown)
        {
            if (fig != null) return null;
            int i = System.Array.FindIndex(_entries, e => e.Number == number);
            if (i >= 0) AddProjectFigure(i);
            return null;
        }
        if (fig != null) RemoveFigure(fig);
        return fig;
    }

    static readonly string[] AllMonsters =
        { "Bone_Stalker", "Gullet_Crawler", "Hollow_Wanderer", "Thorn_Bishop", "Weeping_Saint", "Stone_Lament",
          "Choir_of_Faces", "Bound_Oracle", "Throne_of_Whispers", "Blind_Empress", "Rootbound_God", "Twin_Soul" };

    /// <summary>
    /// Bring in a character for a Claude app session. It starts in the room with the fewest characters (the Great
    /// Hall is kept for the player).
    /// </summary>
    public AgentFigure AddSessionFigure(SessionCast.Info s, string character)
    {
        var counts = Map.Rooms.Where(r => r.Name != "Great Hall")
            .Select(r => (r.Name, n: Figures.Count(f => Map.RoomName(f.Position) == r.Name)))
            .OrderBy(x => x.n).ThenBy(x => x.Name).ToList();
        string room = counts.Count > 0 ? counts[0].Name : "Great Hall";
        int seed = System.Math.Abs(s.HostId.GetHashCode());
        var fig = new AgentFigure
        {
            AgentName = character,
            ProjectName = s.Title,
            SessionHostId = s.HostId,
            Badge = s.FolderName,
            ProjectDir = s.Folder,
            MonsterName = AllMonsters[seed % AllMonsters.Length],
            Map = Map,
            Position = Map.RoomCenter(room) + new Vector3((seed % 5) - 2, 0, ((seed / 5) % 3) - 1),
        };
        AddChild(fig);
        fig.ApplySession(s);
        Figures.Add(fig);
        FigureAdded?.Invoke(fig);
        return fig;
    }

    public void RemoveFigure(AgentFigure f)
    {
        Figures.Remove(f);
        if (Focused == f) Focused = null;
        f.QueueFree();
    }

    /// <summary>Push the live Project Dashboard rows and checklist titles onto the agents.</summary>
    public void ApplyVault(DashboardData dash, List<ChecklistSection> checks)
    {
        foreach (var f in Figures)
        {
            if (f.IsSession) continue;
            var row = dash.Rows.Find(r => r.Number == f.ProjectNumber);
            if (row == null) continue;
            var title = checks.Find(c => c.Number == f.ProjectNumber)?.Title ?? "";
            string status = row.Get("Status");
            f.ApplyVault(status, row.Get("Notes"), row.Get("Plan"), title, VaultNotes.StateFromStatus(status));
        }
    }

    void BuildEnvironment()
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = Colors.Black,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.13f, 0.13f, 0.14f),
            AmbientLightEnergy = 0.6f,
        };
        AddChild(new WorldEnvironment { Environment = env });
    }

    /// <summary>An iron stand with three candle flames, like the ones lighting the game's halls.</summary>
    void AddCandelabra(Vector3 pos, float phase)
    {
        var iron = Look.Mat(new Color(0.1f, 0.1f, 0.11f));
        AddChild(Look.Box(new Vector3(0.12f, 2.2f, 0.12f), pos + new Vector3(0, 1.1f, 0), iron));
        AddChild(Look.Box(new Vector3(0.7f, 0.1f, 0.1f), pos + new Vector3(0, 2.2f, 0), iron));
        AddChild(Look.Box(new Vector3(0.5f, 0.1f, 0.5f), pos + new Vector3(0, 0.05f, 0), iron));
        var flameMat = Look.Mat(new Color(1, 0.85f, 0.5f), emission: new Color(1, 0.7f, 0.35f), emissionEnergy: 3.5f);
        foreach (float dx in new[] { -0.3f, 0f, 0.3f })
            AddChild(new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 0.07f, Height = 0.2f, RadialSegments = 6, Rings = 3 },
                Position = pos + new Vector3(dx, 2.4f, 0),
                MaterialOverride = flameMat,
            });
        var light = new OmniLight3D
        {
            Position = pos + new Vector3(0, 2.7f, 0.2f),
            LightColor = new Color(1f, 0.9f, 0.78f),
            OmniRange = 9f,
            LightEnergy = 1.6f,
        };
        AddChild(light);
        _flickers.Add((light, 1.6f, phase));
    }

    void PlaceCamera(Vector3 target, float t)
    {
        float pitch = Mathf.DegToRad(CamPitchDeg);
        // Look slightly north of the player so what is ahead stays in view.
        var want = target + new Vector3(0, 1.0f, -4.5f) + new Vector3(0, Mathf.Sin(pitch), Mathf.Cos(pitch)) * CamDistance;
        Camera.Position = t >= 1f ? want : Camera.Position.Lerp(want, t);
    }

    public void Zoom(float delta) => _zoom = Mathf.Clamp(_zoom + delta, 8f, 34f);

    public override void _Process(double delta)
    {
        _t += delta;
        float d = (float)delta;
        foreach (var (light, baseE, phase) in _flickers)
        {
            float t = (float)_t;
            light.LightEnergy = baseE * (0.97f + 0.03f * Mathf.Sin(t * 2.1f + phase));
        }

        PlaceCamera(Player.Position, 1f - Mathf.Exp(-6f * d));
        Camera.Size = Mathf.Lerp(Camera.Size, _zoom, 1f - Mathf.Exp(-8f * d));

        // Focus = nearest agent within reach of the player.
        AgentFigure? best = null;
        float bestD = 3.2f;
        foreach (var f in Figures)
        {
            float dist = new Vector2(Player.Position.X - f.Position.X, Player.Position.Z - f.Position.Z).Length();
            f.UpdateLabel(dist);
            if (dist < bestD) { bestD = dist; best = f; }
        }
        Focused = best;
    }
}
