using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

// Everything that depends on who is running the game and how their machine is laid out, read once from
// data/settings.json (or the file named by the DUNGEON_SETTINGS environment variable). Missing keys fall back to
// the defaults below. See README.md for what each one does.
public static class Settings
{
    sealed class Model
    {
        public string UserName { get; set; } = "the user";
        public bool ProjectWorkflow { get; set; }
        public string ProjectPrefix { get; set; } = "PRJ";
        public string VaultDir { get; set; } = "";
        public string LocalTestFolder { get; set; } = @"C:\MyApp";
        public string QaRoot { get; set; } = "";
        public List<string> QaSyncLists { get; set; } = new();
        public string PluginConfig { get; set; } = "";
        public List<RepoRoot> RepoRoots { get; set; } = new();
        public string RunProjectSkill { get; set; } = "my-plugin:run-project";
        public string TestPrepSkill { get; set; } = "my-plugin:test-prep";
        public string SyncListSkill { get; set; } = "update-sync-list";
        public string TdSkill { get; set; } = "generate-td";
    }

    public sealed class RepoRoot
    {
        public string Path { get; set; } = "";
        public bool Interface { get; set; }
    }

    static Model? _m;

    static Model M
    {
        get
        {
            if (_m != null) return _m;
            var path = Environment.GetEnvironmentVariable("DUNGEON_SETTINGS")
                ?? System.IO.Path.Combine(Godot.ProjectSettings.GlobalizePath("res://"), "data", "settings.json");
            try
            {
                _m = File.Exists(path)
                    ? JsonSerializer.Deserialize<Model>(File.ReadAllText(path),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                    : null;
            }
            catch (Exception ex) { Godot.GD.PrintErr($"Could not read {path}: {ex.Message}"); }
            if (_m == null) Godot.GD.PrintErr($"No settings at {path}; copy data/settings.example.json to data/settings.json.");
            return _m ??= new Model();
        }
    }

    /// <summary>Your first name, as the agents should address you and as it appears in the dashboard's manual columns.</summary>
    public static string UserName => M.UserName;

    /// <summary>
    /// Off (the default): base mode. Every Claude Code session is a character with a status line; nothing else.
    /// On: also the project layer built for a project workflow (data/projects.json figures, the vault dashboard and
    /// checklist windows, test prep, "Complete testing" handoff, QA copy check). See README.md before turning it on.
    /// </summary>
    public static bool ProjectWorkflow => M.ProjectWorkflow;

    /// <summary>Project numbers look like PREFIX-000123 (dashboard rows, checklist headings, clone folder names).</summary>
    public static string ProjectPrefix => M.ProjectPrefix;

    /// <summary>Folder holding "Project Dashboard.md" and "Test Checklist.md". DUNGEON_VAULT_DIR still overrides it.</summary>
    public static string VaultDir => Environment.GetEnvironmentVariable("DUNGEON_VAULT_DIR") ?? M.VaultDir;

    /// <summary>The local folder test files are copied into (shown in agent activity and briefings).</summary>
    public static string LocalTestFolder => M.LocalTestFolder;

    /// <summary>The shared QA folder the QA copy check compares against. Empty turns the check off. Read-only.</summary>
    public static string QaRoot => M.QaRoot;

    /// <summary>Sync-list files, relative to QaRoot, that list each project's files.</summary>
    public static IReadOnlyList<string> QaSyncLists => M.QaSyncLists;

    /// <summary>JSON file with primaryRoot / secondaryRoot / ...Interfaces keys (the workflow plugin's config).</summary>
    public static string PluginConfig => M.PluginConfig != ""
        ? Environment.ExpandEnvironmentVariables(M.PluginConfig)
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "my-plugin", "config.json");

    /// <summary>Folders holding PRJ-&lt;number&gt;-&lt;title&gt; clones, used when the plugin config is missing.</summary>
    public static IReadOnlyList<RepoRoot> RepoRoots => M.RepoRoots;

    public static string RunProjectSkill => M.RunProjectSkill;
    public static string TestPrepSkill => M.TestPrepSkill;
    public static string SyncListSkill => M.SyncListSkill;
    public static string TdSkill => M.TdSkill;
}
