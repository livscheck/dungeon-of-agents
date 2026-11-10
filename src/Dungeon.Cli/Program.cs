using Dungeon.Core.Catalog;

// Dumps the skill/plugin/MCP catalog so it can be checked against the real Claude Code setup.
var snap = new SkillCatalog().Scan();

Console.WriteLine($"PLUGINS (Relics): {snap.Plugins.Count}");
foreach (var p in snap.Plugins)
    Console.WriteLine($"  {(p.Enabled ? "[on] " : "[off]")} {p.Name} v{p.Version}");

Console.WriteLine($"\nSKILLS (Spells): {snap.Skills.Count}");
foreach (var s in snap.Skills)
{
    var d = s.Description.Length > 70 ? s.Description[..70] + "..." : s.Description;
    Console.WriteLine($"  /{s.InvocationName,-45} {d}");
}

Console.WriteLine($"\nMCP SERVERS (Familiars): {snap.McpServers.Count}");
foreach (var m in snap.McpServers)
    Console.WriteLine($"  {m.Name}  ({m.Command})");
