using System.Text.Json;

namespace Dungeon.Core.Catalog;

public enum SkillSource { User, Plugin }

/// <summary>A Claude Code skill ("Spell" in the UI).</summary>
public sealed record SkillInfo(string Name, string Description, SkillSource Source, string? Plugin, string Path)
{
    /// <summary>Name as typed after the slash: plugin skills are namespaced "plugin:skill".</summary>
    public string InvocationName => Plugin is null ? Name : $"{Plugin}:{Name}";
}

/// <summary>An installed Claude Code plugin ("Relic" in the UI).</summary>
public sealed record PluginInfo(string Name, string Version, bool Enabled, string InstallPath);

/// <summary>A configured MCP server ("Familiar" in the UI).</summary>
public sealed record McpServerInfo(string Name, string? Command, string Scope);

public sealed record CatalogSnapshot(
    IReadOnlyList<SkillInfo> Skills,
    IReadOnlyList<PluginInfo> Plugins,
    IReadOnlyList<McpServerInfo> McpServers);

/// <summary>
/// Read-only scan of the user's Claude Code configuration: ~/.claude/skills, installed plugins
/// (plugins/installed_plugins.json + settings.json enabledPlugins) and MCP servers (~/.claude.json).
/// Never writes anything.
/// </summary>
public sealed class SkillCatalog
{
    readonly string _claudeDir;
    readonly string _claudeJson;

    public SkillCatalog(string? claudeDir = null, string? claudeJsonPath = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _claudeDir = claudeDir ?? Path.Combine(home, ".claude");
        _claudeJson = claudeJsonPath ?? Path.Combine(home, ".claude.json");
    }

    public CatalogSnapshot Scan()
    {
        var skills = new List<SkillInfo>();
        var plugins = ReadPlugins();

        // User skills: ~/.claude/skills/<name>/SKILL.md
        ScanSkillDir(Path.Combine(_claudeDir, "skills"), SkillSource.User, null, skills);

        // Skills from installed + enabled plugins: <installPath>/skills/<name>/SKILL.md
        foreach (var p in plugins.Where(p => p.Enabled))
            ScanSkillDir(Path.Combine(p.InstallPath, "skills"), SkillSource.Plugin, ShortName(p.Name), skills);

        return new CatalogSnapshot(
            skills.OrderBy(s => s.InvocationName, StringComparer.OrdinalIgnoreCase).ToList(),
            plugins,
            ReadMcpServers());
    }

    static string ShortName(string pluginKey) => pluginKey.Split('@')[0];

    static void ScanSkillDir(string dir, SkillSource source, string? plugin, List<SkillInfo> into)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var dirName = Path.GetFileName(sub);
            if (dirName.StartsWith('.')) continue; // .trash, etc.
            var file = Path.Combine(sub, "SKILL.md");
            if (!File.Exists(file)) continue;
            var (name, desc) = ReadFrontmatter(file);
            into.Add(new SkillInfo(name ?? dirName, desc ?? "", source, plugin, file));
        }
    }

    /// <summary>Minimal YAML frontmatter reader: only top-level "name:" and "description:" scalars.</summary>
    internal static (string? name, string? description) ReadFrontmatter(string file)
    {
        string? name = null, desc = null;
        using var reader = new StreamReader(file);
        if (reader.ReadLine()?.Trim() != "---") return (null, null);
        string? line;
        while ((line = reader.ReadLine()) != null && line.Trim() != "---")
        {
            if (line.StartsWith("name:")) name = Unquote(line[5..]);
            else if (line.StartsWith("description:")) desc = Unquote(line[12..]);
        }
        return (name, desc);
    }

    static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && (s[0] == '"' && s[^1] == '"' || s[0] == '\'' && s[^1] == '\'')) s = s[1..^1];
        return s;
    }

    List<PluginInfo> ReadPlugins()
    {
        var result = new List<PluginInfo>();
        var installed = Path.Combine(_claudeDir, "plugins", "installed_plugins.json");
        if (!File.Exists(installed)) return result;

        var enabled = ReadEnabledPlugins();
        using var doc = JsonDocument.Parse(File.ReadAllText(installed));
        if (!doc.RootElement.TryGetProperty("plugins", out var plugins)) return result;

        foreach (var entry in plugins.EnumerateObject())
        {
            // Each key maps to a list of installs (per scope); take the first.
            if (entry.Value.ValueKind != JsonValueKind.Array || entry.Value.GetArrayLength() == 0) continue;
            var inst = entry.Value[0];
            var path = inst.TryGetProperty("installPath", out var ip) ? ip.GetString() ?? "" : "";
            var version = inst.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            result.Add(new PluginInfo(entry.Name, version, enabled.TryGetValue(entry.Name, out var on) && on, path));
        }
        return result;
    }

    Dictionary<string, bool> ReadEnabledPlugins()
    {
        var map = new Dictionary<string, bool>();
        var settings = Path.Combine(_claudeDir, "settings.json");
        if (!File.Exists(settings)) return map;
        using var doc = JsonDocument.Parse(File.ReadAllText(settings));
        if (doc.RootElement.TryGetProperty("enabledPlugins", out var ep) && ep.ValueKind == JsonValueKind.Object)
            foreach (var p in ep.EnumerateObject())
                map[p.Name] = p.Value.ValueKind == JsonValueKind.True;
        return map;
    }

    List<McpServerInfo> ReadMcpServers()
    {
        var result = new List<McpServerInfo>();
        if (!File.Exists(_claudeJson)) return result;
        // JsonDocument is case-sensitive, so the duplicate-casing project keys in ~/.claude.json are fine.
        using var doc = JsonDocument.Parse(File.ReadAllText(_claudeJson));
        if (doc.RootElement.TryGetProperty("mcpServers", out var servers) && servers.ValueKind == JsonValueKind.Object)
            foreach (var s in servers.EnumerateObject())
            {
                var cmd = s.Value.TryGetProperty("command", out var c) ? c.GetString()
                        : s.Value.TryGetProperty("url", out var u) ? u.GetString() : null;
                result.Add(new McpServerInfo(s.Name, cmd, "user"));
            }
        return result;
    }
}
