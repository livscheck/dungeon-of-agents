using System;
using System.Text.RegularExpressions;
using Godot;

// Small helpers for building the popup windows in code.
public static class Ui
{
    public static readonly Color Bg = new(0.07f, 0.07f, 0.08f, 0.97f);
    public static readonly Color Border = new(0.46f, 0.46f, 0.50f);
    public static readonly Color Dark = new(0.14f, 0.03f, 0.04f);
    public static readonly Color Card = new(0.11f, 0.11f, 0.125f);
    public static readonly Color Text = new(0.90f, 0.88f, 0.84f);
    public static readonly Color Dim = new(0.62f, 0.62f, 0.65f);
    public static readonly Color Gold = new(1f, 0.82f, 0.35f);
    public static readonly Color Red = new(1f, 0.32f, 0.30f);
    public static readonly Color Green = new(0.62f, 0.86f, 0.62f);
    public static readonly Color Blue = new(0.62f, 0.80f, 1f);

    public static StyleBoxFlat Box(Color bg, Color? border = null, int borderWidth = 0, int radius = 0, int margin = 0)
    {
        var sb = new StyleBoxFlat { BgColor = bg };
        if (border.HasValue) { sb.BorderColor = border.Value; sb.SetBorderWidthAll(borderWidth); }
        sb.SetCornerRadiusAll(radius);
        sb.SetContentMarginAll(margin);
        return sb;
    }

    public static Label L(string text, int size = 15, Color? color = null, bool wrap = false)
    {
        var l = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color ?? Text);
        if (wrap) { l.AutowrapMode = TextServer.AutowrapMode.WordSmart; l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; }
        return l;
    }

    public static RichTextLabel Rich(string markdown, int size = 15, Color? color = null)
    {
        var r = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        r.AddThemeFontSizeOverride("normal_font_size", size);
        r.AddThemeFontSizeOverride("bold_font_size", size);
        r.AddThemeColorOverride("default_color", color ?? Text);
        r.Text = Md2Bb(markdown);
        return r;
    }

    /// <summary>A checkbox that stays visible on the dark background (grey box, gold when ticked).</summary>
    public static Button Check(string text, bool pressed, int size = 14)
    {
        var b = new Button { ToggleMode = true, ButtonPressed = pressed, Flat = true, FocusMode = Control.FocusModeEnum.None, Alignment = HorizontalAlignment.Left };
        b.AddThemeFontSizeOverride("font_size", size);
        void Refresh()
        {
            b.Text = (b.ButtonPressed ? "[x]  " : "[   ]  ") + text;
            var col = b.ButtonPressed ? Gold : Text;
            foreach (var n in new[] { "font_color", "font_pressed_color", "font_hover_color", "font_hover_pressed_color", "font_focus_color" })
                b.AddThemeColorOverride(n, col);
        }
        Refresh();
        b.Toggled += _ => Refresh();
        return b;
    }

    /// <summary>A progress bar with a visible track and a gold fill.</summary>
    public static ProgressBar Bar(double value, double max, float width)
    {
        var b = new ProgressBar
        {
            MinValue = 0, MaxValue = Math.Max(1, max), Value = value, ShowPercentage = false,
            CustomMinimumSize = new Vector2(width, 12), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        b.AddThemeStyleboxOverride("background", Box(new Color(0.22f, 0.22f, 0.25f), new Color(0.4f, 0.4f, 0.44f), 1, 2));
        b.AddThemeStyleboxOverride("fill", Box(Gold, null, 0, 2));
        return b;
    }

    public static Button Btn(string text, Action onClick, int size = 15)
    {
        var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
        b.AddThemeFontSizeOverride("font_size", size);
        b.Pressed += onClick;
        return b;
    }

    static readonly Regex Wiki = new(@"\[\[([^\]]+)\]\]");
    static readonly Regex Code = new(@"`([^`]+)`");
    static readonly Regex Bold = new(@"\*\*(.+?)\*\*");

    /// <summary>Markdown (as used in the vault notes) to Godot BBCode.</summary>
    public static string Md2Bb(string s)
    {
        s = Wiki.Replace(s, m => m.Groups[1].Value.Replace("#", " > "));
        s = s.Replace("[", "[lb]");
        s = Code.Replace(s, "[color=#dcc98e]$1[/color]");
        s = Bold.Replace(s, "[b]$1[/b]");
        return s;
    }

    /// <summary>Markdown stripped to plain text.</summary>
    public static string Plain(string s)
    {
        // Code-block fences (```lang) carry no words: drop those lines, keep what is between them.
        s = System.Text.RegularExpressions.Regex.Replace(s, @"^[ \t]*```[^\n]*\n?", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        s = Wiki.Replace(s, m => m.Groups[1].Value.Replace("#", " > "));
        s = Code.Replace(s, "$1");
        return Bold.Replace(s, "$1");
    }

    public static string Excerpt(string s, int max)
    {
        s = Plain(s).Trim();
        if (s.Length <= max) return s;
        int cut = s.LastIndexOf(". ", max, StringComparison.Ordinal);
        if (cut < max / 2) cut = s.LastIndexOf(' ', max);
        return (cut > 0 ? s.Substring(0, cut + 1) : s.Substring(0, max)).TrimEnd() + " ...";
    }

    public static Color StatusColor(string status)
    {
        var s = status.ToLowerInvariant();
        if (s.StartsWith("waiting")) return Red;
        if (s.StartsWith("ready")) return Gold;
        if (s.StartsWith("in progress")) return Blue;
        if (s.StartsWith("merged")) return Green;
        return Dim;
    }
}
