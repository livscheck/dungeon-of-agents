using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

// Finds a project's git clone(s). Clone folders are named PRJ-<number>-<title> under the roots in the plugin's
// per-user config (Settings.PluginConfig). A project always has an app clone
// (the primary) and may also have .NET interface clones (extra).
public static class ProjectRepo
{
    public static (string primary, List<string> extra) Find(string number)
    {
        string digits = number.Replace(Settings.ProjectPrefix + "-", "");
        var roots = new List<(string path, bool iface)>();
        try
        {
            var cfg = Settings.PluginConfig;
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            foreach (var (key, iface) in new[] { ("primaryRoot", false), ("secondaryRoot", false), ("primaryInterfaces", true), ("secondaryInterfaces", true) })
                if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String) roots.Add((v.GetString() ?? "", iface));
        }
        catch { /* no config: fall back to repoRoots in settings.json */ }
        if (roots.Count == 0)
        {
            foreach (var r in Settings.RepoRoots) roots.Add((r.Path, r.Interface));
        }

        string primary = "";
        var extra = new List<string>();
        foreach (var (path, iface) in roots)
        {
            if (!Directory.Exists(path)) continue;
            foreach (var d in Directory.EnumerateDirectories(path, $"{Settings.ProjectPrefix}-{digits}-*"))
            {
                if (!iface && primary == "") primary = d;
                else extra.Add(d);
            }
        }
        if (primary == "" && extra.Count > 0) { primary = extra[0]; extra.RemoveAt(0); }
        return (primary, extra);
    }
}
