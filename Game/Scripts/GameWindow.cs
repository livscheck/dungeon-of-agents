using System;
using Godot;

// A draggable popup window with a dark-red title bar and a close button, in the dungeon's style.
public partial class GameWindow : PanelContainer
{
    public VBoxContainer Body { get; } = new();
    public event Action? Closed;

    readonly PanelContainer _bar = new();
    bool _drag;
    Vector2 _grab;

    public GameWindow(string title, Vector2 size)
    {
        CustomMinimumSize = size;
        Size = size;
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        AddThemeStyleboxOverride("panel", Ui.Box(Ui.Bg, Ui.Border, 2));

        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 0);
        AddChild(outer);

        _bar.AddThemeStyleboxOverride("panel", Ui.Box(Ui.Dark, null, 0, 0, 8));
        var row = new HBoxContainer();
        var label = Ui.L(title.ToUpperInvariant(), 18, Ui.Gold);
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(label);
        row.AddChild(Ui.Btn("X", CloseWindow));
        _bar.AddChild(row);
        _bar.GuiInput += OnBarInput;
        outer.AddChild(_bar);

        var pad = new MarginContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            pad.AddThemeConstantOverride(side, 12);
        Body.SizeFlagsVertical = SizeFlags.ExpandFill;
        Body.AddThemeConstantOverride("separation", 8);
        pad.AddChild(Body);
        outer.AddChild(pad);
    }

    void OnBarInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            _drag = mb.Pressed;
            _grab = GetGlobalMousePosition() - GlobalPosition;
            RaiseWindow();
        }
        else if (e is InputEventMouseMotion && _drag)
        {
            var screen = GetViewportRect().Size;
            var p = GetGlobalMousePosition() - _grab;
            GlobalPosition = new Vector2(Mathf.Clamp(p.X, 0, Mathf.Max(0, screen.X - 80)), Mathf.Clamp(p.Y, 0, Mathf.Max(0, screen.Y - 40)));
        }
    }

    public void RaiseWindow() => GetParent()?.MoveChild(this, -1);

    public void OpenWindow() { Visible = true; RaiseWindow(); }

    public void CloseWindow()
    {
        if (!Visible) return;
        Visible = false;
        Closed?.Invoke();
    }

    protected static void Clear(Node n)
    {
        foreach (var c in n.GetChildren()) { n.RemoveChild(c); c.QueueFree(); }
    }
}
