using Godot;

// Every character gets an arrow: above its nameplate while it is on screen, at the screen edge (with its name)
// while it is off screen, so any of them can be found on the large map. Agents that need the user get a large bright arrow (red: waiting on you, gold: ready for testing or a
// question); everyone else gets a smaller, dimmer one in the colour of their state. Attention arrows draw last, on top.
public partial class AttentionOverlay : Control
{
    public Hub3D? Hub { get; set; }

    public AttentionOverlay()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        if (Hub == null || Hub.Camera == null) return;
        var size = GetViewportRect().Size;
        var center = size / 2f;
        var margin = new Vector2(70, 80);  // where edge arrows sit
        var visible = new Vector2(24, 24); // a character this far inside the screen counts as on screen
        var font = ThemeDB.FallbackFont;
        var placed = new System.Collections.Generic.List<Vector2>(); // edge arrows already drawn, to keep them apart

        foreach (var pass in new[] { false, true })
        foreach (var f in Hub.Figures)
        {
            if (f.NeedsAttention != pass) continue;
            bool urgent = pass;
            Color col = urgent
                ? (f.State == AgentState.AwaitingUser ? new Color(1f, 0.3f, 0.28f) : new Color(1f, 0.82f, 0.35f))
                : f.State switch
                {
                    AgentState.Working or AgentState.PrepForTesting => new Color(0.62f, 0.85f, 0.62f, 0.75f),
                    AgentState.Complete => new Color(0.55f, 0.8f, 0.55f, 0.75f),
                    _ => new Color(0.72f, 0.72f, 0.76f, 0.7f),
                };
            float s = urgent ? 1f : 0.7f;

            var sp = Hub.Camera.UnprojectPosition(f.GlobalPosition + Vector3.Up);
            if (sp.X > visible.X && sp.X < size.X - visible.X && sp.Y > visible.Y && sp.Y < size.Y - visible.Y)
            {
                // On screen: a down-pointing arrow just above the nameplate, bobbing gently.
                var top = Hub.Camera.UnprojectPosition(f.ArrowAnchor);
                float bob = 4f * Mathf.Sin((float)Time.GetTicksMsec() / 260f + f.GetInstanceId() % 7);
                var tip = top + new Vector2(0, bob);
                DrawColoredPolygon(new[] { tip, tip + new Vector2(-11, -16) * s, tip + new Vector2(11, -16) * s }, col);
                continue;
            }

            var dir = sp - center;
            if (dir.LengthSquared() < 1f) continue;
            float sx = (size.X / 2f - margin.X) / Mathf.Max(Mathf.Abs(dir.X), 0.001f);
            float sy = (size.Y / 2f - margin.Y) / Mathf.Max(Mathf.Abs(dir.Y), 0.001f);
            var p = center + dir * Mathf.Min(sx, sy);
            var n = dir.Normalized();
            var perp = new Vector2(-n.Y, n.X);
            // Two characters off the same edge would land on one spot: slide this one along the edge until it is clear.
            for (int tries = 0; tries < 12 && placed.Exists(q => q.DistanceTo(p) < 70f); tries++)
                p += perp * (tries % 2 == 0 ? 1 : -1) * 75f * (tries / 2 + 1);
            // Keep clear of the banners across the top of the screen.
            p = new Vector2(Mathf.Clamp(p.X, margin.X, size.X - margin.X), Mathf.Clamp(p.Y, 140f, size.Y - margin.Y));
            placed.Add(p);
            DrawColoredPolygon(new[] { p + n * 16 * s, p - n * 8 * s + perp * 11 * s, p - n * 8 * s - perp * 11 * s }, col);
            DrawString(font, p - n * (urgent ? 38 : 30) + new Vector2(-60, 6), f.AgentName, HorizontalAlignment.Center, 120, urgent ? 18 : 15, col);
        }
    }
}
