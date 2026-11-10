# Dungeon of Agents — notes for Claude

Godot 4.7 (.NET / C#) game in `Game/`. Every Claude Code desktop-app session is drawn as a character with a status
line. Read `README.md` first.

- Build check: `dotnet build Game/DungeonGame.csproj`. Run: open `Game/project.godot` in Godot 4.7.2 mono.
- Headless screenshot for checking changes: `<godot console exe> --path Game -- --shot=out.png --wait=90`
  (also `--script=W:60,E:2` to hold keys for N frames). Set `DUNGEON_DRYRUN=1` so nothing launches or messages
  Claude while testing.
- Per-user values live in `Game/data/settings.json`, read via `Game/Scripts/Settings.cs`. Add new user-specific
  values there rather than hardcoding paths or names.
- Base mode (`projectWorkflow: false`) = `SessionCast.cs` (which sessions, which state) + `SessionActivity.cs`
  (current step text) + `AgentFigure.cs` (drawing). The project layer (`VaultNotes`, `Briefing`, `TestPrep`,
  `QaHandoff`, `QaCopyCheck`, `ProjectRepo`) is one team's project workflow; when the user wants their own workflow,
  replace those rather than bending them.
- `portraits/` must stay next to `Game/`; character art is found by file name (`<Name>_up.png` etc.).
- Godot APIs (e.g. `ProjectSettings.GlobalizePath`) are main-thread only; background polling in `Main.cs` uses
  `Task.Run` and hands results back with `Callable.From(...).CallDeferred()`.
