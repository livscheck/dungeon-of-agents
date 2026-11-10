using System;
using Godot;

// The player's hooded avatar, seen from the angled overhead camera. WASD / arrow keys walk on the
// floor plane (up = north). Movement is blocked by the Walkable callback (walls). The sprite
// switches between its front, back, left and right views to match the direction of travel.
public partial class PlayerController : Node3D
{
    const float SpriteHeight = 2.1f;

    public Func<Vector3, bool> Walkable { get; set; } = _ => true;
    public bool Enabled { get; set; } = true;

    Sprite3D _sprite = null!;
    SpriteSet _set = null!;
    Facing _facing = Facing.Down;
    float _pixel;
    double _t;
    bool _moving;

    public override void _Ready()
    {
        _set = SpriteSet.Load("Player", () => SpriteFactory.Silhouette(new Color(0.16f, 0.18f, 0.24f), torch: true));
        _pixel = SpriteHeight / _set.Down.GetHeight();
        _sprite = new Sprite3D
        {
            PixelSize = _pixel,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
            AlphaCut = SpriteBase3D.AlphaCutMode.OpaquePrepass,
            DoubleSided = true,
            Shaded = false, // always readable, like the party leader in the game
        };
        AddChild(_sprite);
        SetFacing(Facing.Down);

        AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.55f, BottomRadius = 0.55f, Height = 0.01f, RadialSegments = 20, Rings = 1 },
            Position = new Vector3(0, 0.01f, 0),
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(0, 0, 0, 0.55f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            },
        });
        // The player's own torch: the lit pool that follows you through the dark.
        AddChild(new OmniLight3D
        {
            Position = new Vector3(0.5f, 2.4f, 0.4f),
            LightColor = new Color(1.0f, 0.9f, 0.76f),
            OmniRange = 12f,
            LightEnergy = 2.4f,
        });
    }

    void SetFacing(Facing f)
    {
        _facing = f;
        var tex = _set.Get(f);
        _sprite.Texture = tex;
        _sprite.Offset = new Vector2(0, tex.GetHeight() / 2f); // feet at the origin
    }

    static bool Down(Key k) => Input.IsKeyPressed(k) || Input.IsPhysicalKeyPressed(k);

    public override void _Process(double delta)
    {
        _t += delta;
        float d = (float)delta;
        _moving = false;

        if (Enabled)
        {
            var dir = Vector2.Zero;
            if (Down(Key.W) || Down(Key.Up)) dir.Y -= 1;
            if (Down(Key.S) || Down(Key.Down)) dir.Y += 1;
            if (Down(Key.A) || Down(Key.Left)) dir.X -= 1;
            if (Down(Key.D) || Down(Key.Right)) dir.X += 1;
            if (dir != Vector2.Zero)
            {
                _moving = true;
                float speed = Down(Key.Shift) ? 10f : 6f;
                var step = dir.Normalized() * speed * d;
                // Axis-separated so you slide along walls.
                var p = Position;
                var tryX = new Vector3(p.X + step.X, 0, p.Z);
                if (Walkable(tryX)) p = tryX;
                var tryZ = new Vector3(p.X, 0, p.Z + step.Y);
                if (Walkable(tryZ)) p = tryZ;
                Position = p;

                // Face the dominant direction of travel.
                var want = Mathf.Abs(dir.X) > Mathf.Abs(dir.Y)
                    ? (dir.X < 0 ? Facing.Left : Facing.Right)
                    : (dir.Y < 0 ? Facing.Up : Facing.Down);
                if (want != _facing) SetFacing(want);
            }
        }

        float bob = _moving ? Mathf.Abs(Mathf.Sin((float)_t * 11f)) * 0.12f : 0f;
        // Pulled toward the camera so walls in front of the avatar cannot cut into it.
        _sprite.Position = Look.TowardCamera * Look.SpriteDepthShift + new Vector3(0, bob, 0);
    }
}
