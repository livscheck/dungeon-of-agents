# Dungeon of Agents

A top-down PSX-style dungeon where every Claude Code session is a character. Walk up to one to see what it's
working on, read what it needs from you, and open it in Claude.

## What you get out of the box (base mode)

- **One character per Claude Code session** in the Claude desktop app. Each session keeps the same character
  from run to run (`Game/data/session-characters.json`).
- **A status line on each character**: Working / Needs you / Done / Resting, plus the step it's on right now
  (read from the end of its transcript, e.g. "Running tests", "Editing Main.cs").
- **Body language matches state**: working agents walk with a lit torch; agents waiting on you are struck down
  with a red marker, and the banner counts them ("2 AGENTS AWAITING YOU").
- **E** speak (shows what it did and what it needs), **T** open the session in Claude, "send away" hides a
  session (undo by editing `Game/data/hidden-sessions.json`).

No config is needed for this: the game reads `%APPDATA%\Claude\claude-code-sessions` and `~/.claude`.

## Setup

1. Install the **.NET 8 SDK** (or newer) and **Godot 4.7.2 .NET (mono)** for Windows from godotengine.org.
2. Install the **Claude desktop app** and use its Code tab (that's where sessions come from).
3. Copy `Game/data/settings.example.json` to `Game/data/settings.json` (one is already there) and set `userName`.
4. Open `Game/project.godot` in Godot, let it import, then press **F5** (or Build, then Play).

Keep the folder layout: `portraits/` must sit next to `Game/` — the characters' art is loaded from there.

## Making it yours

| You want to... | Where |
|---|---|
| Your own characters | Drop art in `portraits/`. A drawn character is `<Name>.png` (facing down) plus `<Name>_up/_left/_right.png`; `NN_<Name>.png` is its portrait. Free names are handed to new sessions in file order. Pin a name to a session in `Game/data/session-characters.json`. |
| Different status wording | `SessionActivity.cs` (maps tool calls/skills to "what it's doing" text), `SessionCast.cs` (Working / Needs you / Done / Resting rules), `AgentState.cs`. |
| Different map / rooms | `DungeonMap.cs`, `Hub3D.cs`. |
| Different look | `Shaders/psx.gdshader`, `Shaders/post.gdshader`, `Look.cs`. |
| Different keys / HUD | `Main.cs` (`_Input`, `BuildHud`), `Ui.cs`. |

Test without touching anything real: set the environment variable `DUNGEON_DRYRUN=1` and every action that would
launch or message Claude only prints what it would do.

## The optional project layer (`"projectWorkflow": true`)

This part was built around one specific team's workflow (numbered projects, a dashboard and test
checklist kept in an Obsidian vault, local test prep, a "Complete testing" button that hands an agent the go-ahead
to commit/merge to QA, and a check that QA files were copied). **It's off by default** and you'll likely want to
replace it rather than configure it. What's there:

| Piece | Files | What it does |
|---|---|---|
| Fixed project figures | `data/projects.json`, `Hub3D.cs` | One named agent per project, in a room |
| Dashboard + checklist | `VaultNotes.cs`, `DashboardWindow.cs`, `ChecklistWindow.cs`, `templates/` | Live view of two markdown notes the agents maintain; your ticks are written back |
| Briefing | `Briefing.cs`, `AgentLauncher.cs` | First message to a project's session: status, notes, rules |
| Test prep | `TestPrep.cs` | Runs a skill in the background to set up local testing |
| Handoff | `QaHandoff.cs` | "Complete testing" / yes-no answers sent into the agent's session with a per-project code |
| QA copy check | `QaCopyCheck.cs` | Compares files listed in sync lists against a shared QA folder (read-only) |

All machine- and person-specific values are in `Game/data/settings.json` (see the comments in
`settings.example.json`) and read through `Game/Scripts/Settings.cs`.

## Other folders

- `src/` — `Dungeon.Core` (reads skills, plugins and MCP servers from your Claude config) and a small CLI to dump
  them. Not needed to run the game.
- `DESIGN.md` — the original design notes and theme mapping. Some of it is aspirational / specific to the
  original owner's setup.
