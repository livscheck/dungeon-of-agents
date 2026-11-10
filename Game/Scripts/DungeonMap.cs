using System;
using System.Collections.Generic;
using Godot;

// The dungeon: nine rooms joined by corridors, stored as a tile grid (1 tile = 1 world unit).
// Provides walkability tests, A* paths for wandering agents, and builds the floor/wall geometry.
public sealed class DungeonMap
{
    public const int W = 64, H = 46;
    const float WallTall = 4.4f, WallShort = 1.2f;

    public sealed record Room(string Name, int X, int Z, int Width, int Height, Color Tint)
    {
        public Vector3 Center => new(X + Width / 2f, 0, Z + Height / 2f);
    }

    public List<Room> Rooms { get; } = new();
    readonly int[,] _room = new int[W, H]; // -2 rock/wall, -1 corridor floor, >=0 room id
    AStarGrid2D _astar = null!;

    public DungeonMap()
    {
        for (int x = 0; x < W; x++) for (int z = 0; z < H; z++) _room[x, z] = -2;

        AddRoom("Great Hall", 24, 17, 16, 12, new Color(0.48f, 0.48f, 0.49f));
        AddRoom("Armory",     4,  3, 12,  9, new Color(0.44f, 0.44f, 0.45f));
        AddRoom("Library",   25,  2, 14,  9, new Color(0.47f, 0.46f, 0.45f));
        AddRoom("Crypt",     48,  3, 12, 10, new Color(0.42f, 0.42f, 0.45f));
        AddRoom("Vault",      3, 18, 11, 10, new Color(0.40f, 0.42f, 0.45f));
        AddRoom("Shrine",    50, 18, 11, 10, new Color(0.48f, 0.45f, 0.45f));
        AddRoom("Cells",      4, 34, 12,  9, new Color(0.41f, 0.41f, 0.41f));
        AddRoom("Catacombs", 25, 35, 14,  9, new Color(0.38f, 0.38f, 0.40f));
        AddRoom("Ossuary",   48, 34, 12,  9, new Color(0.45f, 0.44f, 0.42f));

        // Corridors (3 wide), each given as a tile rectangle.
        Corridor(14, 22, 10, 3);  // Vault   <-> Great Hall
        Corridor(40, 22, 10, 3);  // Great Hall <-> Shrine
        Corridor(31, 11,  3, 6);  // Library <-> Great Hall
        Corridor(31, 29,  3, 6);  // Great Hall <-> Catacombs
        Corridor( 7, 12,  3, 6);  // Armory  <-> Vault
        Corridor(54, 13,  3, 5);  // Crypt   <-> Shrine
        Corridor( 7, 28,  3, 6);  // Vault   <-> Cells
        Corridor(54, 28,  3, 6);  // Shrine  <-> Ossuary
        Corridor(16,  5,  9, 3);  // Armory  <-> Library
        Corridor(39,  5,  9, 3);  // Library <-> Crypt
        Corridor(16, 38,  9, 3);  // Cells   <-> Catacombs
        Corridor(39, 38,  9, 3);  // Catacombs <-> Ossuary

        _astar = new AStarGrid2D
        {
            Region = new Rect2I(0, 0, W, H),
            CellSize = Vector2.One,
            DiagonalMode = AStarGrid2D.DiagonalModeEnum.OnlyIfNoObstacles,
        };
        _astar.Update();
        for (int x = 0; x < W; x++)
            for (int z = 0; z < H; z++)
                _astar.SetPointSolid(new Vector2I(x, z), !IsFloor(x, z));
    }

    void AddRoom(string name, int x, int z, int w, int h, Color tint)
    {
        int id = Rooms.Count;
        Rooms.Add(new Room(name, x, z, w, h, tint));
        for (int i = x; i < x + w; i++) for (int j = z; j < z + h; j++) _room[i, j] = id;
    }

    void Corridor(int x, int z, int w, int h)
    {
        for (int i = x; i < x + w; i++)
            for (int j = z; j < z + h; j++)
                if (_room[i, j] == -2) _room[i, j] = -1;
    }

    public bool IsFloor(int x, int z) => x >= 0 && z >= 0 && x < W && z < H && _room[x, z] != -2;

    /// <summary>True if a circle of the given radius around p stays on floor.</summary>
    public bool IsWalkable(Vector3 p, float r = 0.3f)
        => IsFloor((int)Mathf.Floor(p.X - r), (int)Mathf.Floor(p.Z - r))
        && IsFloor((int)Mathf.Floor(p.X + r), (int)Mathf.Floor(p.Z - r))
        && IsFloor((int)Mathf.Floor(p.X - r), (int)Mathf.Floor(p.Z + r))
        && IsFloor((int)Mathf.Floor(p.X + r), (int)Mathf.Floor(p.Z + r));

    public Vector2I TileOf(Vector3 p) => new((int)Mathf.Floor(p.X), (int)Mathf.Floor(p.Z));
    public Vector3 TileCenter(Vector2I t) => new(t.X + 0.5f, 0, t.Y + 0.5f);

    public Vector3 RoomCenter(string name)
    {
        foreach (var r in Rooms) if (r.Name == name) return r.Center;
        return Rooms[0].Center;
    }

    public string RoomName(Vector3 p)
    {
        var t = TileOf(p);
        if (t.X < 0 || t.Y < 0 || t.X >= W || t.Y >= H) return "the dark";
        int id = _room[t.X, t.Y];
        return id >= 0 ? Rooms[id].Name : "a corridor";
    }

    /// <summary>A walkable path from a to b as world points (tile centres), or null.</summary>
    public List<Vector3>? FindPath(Vector3 a, Vector3 b)
    {
        var ta = TileOf(a); var tb = TileOf(b);
        if (!IsFloor(ta.X, ta.Y) || !IsFloor(tb.X, tb.Y)) return null;
        var ids = _astar.GetIdPath(ta, tb);
        if (ids.Count == 0) return null;
        var pts = new List<Vector3>(ids.Count);
        foreach (var id in ids) pts.Add(TileCenter(id));
        if (pts.Count > 0) pts.RemoveAt(0); // already standing on the first tile
        return pts;
    }

    public Vector3? RandomFloorNear(Vector3 p, float radius, RandomNumberGenerator rng)
    {
        for (int i = 0; i < 40; i++)
        {
            var a = rng.RandfRange(0, Mathf.Tau);
            var d = rng.RandfRange(radius * 0.25f, radius);
            var q = new Vector3(p.X + Mathf.Cos(a) * d, 0, p.Z + Mathf.Sin(a) * d);
            var t = TileOf(q);
            if (IsFloor(t.X, t.Y) && IsWalkable(TileCenter(t), 0.4f)) return TileCenter(t);
        }
        return null;
    }

    bool IsWallTile(int x, int z)
    {
        if (IsFloor(x, z)) return false;
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
                if (IsFloor(x + dx, z + dz)) return true;
        return false;
    }

    /// <summary>Builds merged floor and wall boxes under the parent, and reports room corners for candelabras.</summary>
    public void BuildGeometry(Node3D parent, Action<Vector3, float> addCandelabra)
    {
        var wallMat = Look.Mat(new Color(0.36f, 0.36f, 0.37f), bricks: true);
        var mats = new Dictionary<int, ShaderMaterial> { [-1] = Look.Cobble(new Color(0.42f, 0.42f, 0.43f)) };
        for (int i = 0; i < Rooms.Count; i++) mats[i] = Look.Cobble(Rooms[i].Tint);

        // Floors: merge horizontal runs belonging to the same room.
        for (int z = 0; z < H; z++)
        {
            int x = 0;
            while (x < W)
            {
                if (!IsFloor(x, z)) { x++; continue; }
                int id = _room[x, z], x0 = x;
                while (x < W && IsFloor(x, z) && _room[x, z] == id) x++;
                parent.AddChild(Look.Box(new Vector3(x - x0, 0.4f, 1f), new Vector3((x0 + x) / 2f, -0.2f, z + 0.5f), mats[id]));
            }
        }

        // Walls: tall where there is floor to the south (a visible face), otherwise a low parapet.
        for (int z = 0; z < H; z++)
        {
            int x = 0;
            while (x < W)
            {
                if (!IsWallTile(x, z)) { x++; continue; }
                bool tall = IsFloor(x, z + 1);
                int x0 = x;
                while (x < W && IsWallTile(x, z) && IsFloor(x, z + 1) == tall) x++;
                float h = tall ? WallTall : WallShort;
                parent.AddChild(Look.Box(new Vector3(x - x0, h, 1f), new Vector3((x0 + x) / 2f, h / 2f, z + 0.5f), wallMat));
            }
        }

        // Candelabras in two corners of each room, four in the Great Hall.
        float phase = 0;
        foreach (var r in Rooms)
        {
            var corners = new List<Vector3>
            {
                new(r.X + 1.3f, 0, r.Z + 1.5f),
                new(r.X + r.Width - 1.3f, 0, r.Z + r.Height - 1.3f),
            };
            if (r.Name == "Great Hall")
            {
                corners.Add(new(r.X + r.Width - 1.3f, 0, r.Z + 1.5f));
                corners.Add(new(r.X + 1.3f, 0, r.Z + r.Height - 1.3f));
            }
            foreach (var c in corners) addCandelabra(c, phase += 1.3f);
        }
    }
}
