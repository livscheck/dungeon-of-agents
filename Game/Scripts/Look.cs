using System.Linq;
using Godot;

public enum Facing { Down, Up, Left, Right }

// Shared material/mesh/sprite helpers.
public static class Look
{
    /// <summary>Camera pitch above the floor, in degrees. The camera looks north (-Z) from the south.</summary>
    public const float CamPitchDeg = 56f;

    /// <summary>World direction that is "up the screen". A point this far from the floor appears this high on screen.</summary>
    public static readonly Vector3 CamUp = new(0f, Mathf.Cos(Mathf.DegToRad(CamPitchDeg)), -Mathf.Sin(Mathf.DegToRad(CamPitchDeg)));

    /// <summary>World direction toward the camera. Moving a node along it changes only its depth, not where it appears on screen.</summary>
    public static readonly Vector3 TowardCamera = new(0f, Mathf.Sin(Mathf.DegToRad(CamPitchDeg)), Mathf.Cos(Mathf.DegToRad(CamPitchDeg)));

    /// <summary>How far sprites are pulled toward the camera so walls in front of them cannot cut into them.</summary>
    public const float SpriteDepthShift = 3.2f;

    static Shader? _psx;

    static ShaderMaterial Base(Color tint, int mode, bool bricks, Color? emission, float emissionEnergy)
    {
        _psx ??= GD.Load<Shader>("res://Shaders/psx.gdshader");
        var m = new ShaderMaterial { Shader = _psx };
        m.SetShaderParameter("tint", tint);
        m.SetShaderParameter("mode", mode);
        m.SetShaderParameter("use_bricks", bricks);
        if (emission.HasValue)
        {
            m.SetShaderParameter("emission_color", emission.Value);
            m.SetShaderParameter("emission_energy", emissionEnergy);
        }
        return m;
    }

    /// <summary>Stone-block wall material (or plain colour when bricks is false).</summary>
    public static ShaderMaterial Mat(Color tint, bool bricks = false, Color? emission = null, float emissionEnergy = 0f)
        => Base(tint, 0, bricks, emission, emissionEnergy);

    /// <summary>Cobblestone floor material (projected from above).</summary>
    public static ShaderMaterial Cobble(Color tint) => Base(tint, 1, false, null, 0f);

    public static MeshInstance3D Box(Vector3 size, Vector3 pos, Material mat)
    {
        return new MeshInstance3D { Mesh = new BoxMesh { Size = size }, Position = pos, MaterialOverride = mat };
    }
}

/// <summary>The four directional views of a character: front (Down), back (Up), Left and Right.</summary>
public sealed class SpriteSet
{
    public ImageTexture Down { get; }
    readonly ImageTexture _up, _left, _right;

    SpriteSet(ImageTexture down, ImageTexture up, ImageTexture left, ImageTexture right)
    {
        Down = down; _up = up; _left = left; _right = right;
    }

    public ImageTexture Get(Facing f) => f switch
    {
        Facing.Up => _up,
        Facing.Left => _left,
        Facing.Right => _right,
        _ => Down,
    };

    /// <summary>
    /// Loads Name.png (front) plus Name_up / Name_left / Name_right when present. A character with no separate files
    /// is read from its sheet (NN_Name.png: front, facing left, back, facing right, side by side).
    /// </summary>
    public static SpriteSet Load(string name, System.Func<ImageTexture> fallback)
    {
        if (SpriteFactory.FromFile(name) == null && SpriteFactory.FromSheet(name) is { } views)
            return new SpriteSet(views[0], views[2], views[1], views[3]);
        var down = SpriteFactory.FromFile(name) ?? fallback();
        return new SpriteSet(
            down,
            SpriteFactory.FromFile(name + "_up") ?? down,
            SpriteFactory.FromFile(name + "_left") ?? down,
            SpriteFactory.FromFile(name + "_right") ?? down);
    }
}

/// <summary>Loads character art from the project's portraits folder, or draws a simple original silhouette.</summary>
public static class SpriteFactory
{
    static string Dir => System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "portraits"));

    /// <summary>File names use underscores where character names have spaces ("The Witch" is The_Witch.png).</summary>
    public static string FileBase(string name) => name.Replace(' ', '_');

    public static ImageTexture? FromFile(string name)
    {
        var file = System.IO.Path.Combine(Dir, FileBase(name) + ".png");
        if (!System.IO.File.Exists(file)) return null;
        var img = Image.LoadFromFile(file);
        if (img == null || img.IsEmpty()) return null;
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>
    /// The four views cut from the character's sheet (NN_Name.png), in sheet order: front, left, back, right.
    /// Views can touch or overlap a quarter line, so each cut is made at the emptiest column near it. Every view
    /// keeps the sheet's full height so they all draw at the same size.
    /// </summary>
    public static ImageTexture[]? FromSheet(string name)
    {
        if (!System.IO.Directory.Exists(Dir)) return null;
        var file = System.IO.Directory.EnumerateFiles(Dir, "??_" + FileBase(name) + ".png").FirstOrDefault();
        if (file == null) return null;
        var img = Image.LoadFromFile(file);
        if (img == null || img.IsEmpty()) return null;
        int w = img.GetWidth(), h = img.GetHeight();
        var filled = new int[w];
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
                if (img.GetPixel(x, y).A > 0.08f) filled[x]++;

        var cuts = new int[5];
        cuts[4] = w;
        for (int k = 1; k < 4; k++)
        {
            int target = w * k / 4, best = target, reach = w / 8;
            for (int x = System.Math.Max(cuts[k - 1] + 1, target - reach); x <= System.Math.Min(w - 1, target + reach); x++)
                if (filled[x] < filled[best] || (filled[x] == filled[best] && System.Math.Abs(x - target) < System.Math.Abs(best - target)))
                    best = x;
            cuts[k] = best;
        }

        var views = new ImageTexture[4];
        for (int k = 0; k < 4; k++)
        {
            int x0 = cuts[k], x1 = cuts[k + 1];
            while (x0 < x1 - 1 && filled[x0] == 0) x0++;      // trim the empty columns either side
            while (x1 > x0 + 1 && filled[x1 - 1] == 0) x1--;
            views[k] = ImageTexture.CreateFromImage(img.GetRegion(new Rect2I(x0, 0, x1 - x0, h)));
        }
        return views;
    }

    /// <summary>A small hooded figure (original placeholder art), optionally holding a torch.</summary>
    public static ImageTexture Silhouette(Color robe, bool torch)
    {
        const int W = 48, H = 96;
        var img = Image.CreateEmpty(W, H, false, Image.Format.Rgba8);
        img.Fill(new Color(0, 0, 0, 0));
        var ink = new Color(0.05f, 0.04f, 0.05f);
        var dark = robe.Darkened(0.35f);
        var skin = new Color(0.55f, 0.47f, 0.40f);

        void Ell(float cx, float cy, float rx, float ry, Color c)
        {
            for (int y = (int)(cy - ry) - 1; y <= (int)(cy + ry) + 1; y++)
                for (int x = (int)(cx - rx) - 1; x <= (int)(cx + rx) + 1; x++)
                {
                    if (x < 0 || y < 0 || x >= W || y >= H) continue;
                    float dx = (x - cx) / rx, dy = (y - cy) / ry;
                    if (dx * dx + dy * dy <= 1f) img.SetPixel(x, y, c);
                }
        }
        void Rect(int x0, int y0, int x1, int y1, Color c)
        {
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    if (x >= 0 && y >= 0 && x < W && y < H) img.SetPixel(x, y, c);
        }

        for (int pass = 0; pass < 2; pass++)
        {
            int grow = pass == 0 ? 1 : 0;
            var c = pass == 0 ? ink : robe;
            for (int y = 26; y <= 90; y++)
            {
                int half = 8 + (int)((y - 26) * 0.2f) + grow;
                Rect(24 - half, y, 24 + half, y, c);
            }
        }
        Rect(14, 28, 18, 60, dark);
        Rect(30, 28, 34, 60, dark);
        Ell(24, 15, 11, 12, ink);
        Ell(24, 15, 10, 11, robe);
        Ell(24, 17, 6.5f, 7.5f, skin.Darkened(0.55f));
        img.SetPixel(21, 17, new Color(0.85f, 0.8f, 0.7f));
        img.SetPixel(27, 17, new Color(0.85f, 0.8f, 0.7f));
        Rect(20, 80, 28, 90, dark);

        if (torch)
        {
            Rect(37, 36, 38, 78, new Color(0.3f, 0.2f, 0.12f));
            Rect(36, 31, 39, 35, new Color(1.0f, 0.75f, 0.25f));
            Rect(37, 29, 38, 30, new Color(1.0f, 0.95f, 0.6f));
        }
        return ImageTexture.CreateFromImage(img);
    }
}
