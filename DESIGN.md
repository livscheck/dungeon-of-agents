# The Dungeon of Agents: Architecture Design

Personal Windows app that manages Claude Code agents working on projects. First-person 3D, PSX-era dungeon-crawler look inspired by Fear & Hunger. Integrates with the user's Claude Code skills, plugins and MCP servers.

Decisions: all agents are Claude Code; personal use only; 3D look very close to Fear & Hunger; integrates with existing skills and plugins.

## 1. Goals

- See every agent, its project and its state at a glance.
- Start, pause, stop and redirect agents; approve or deny risky actions.
- The theme is functional: world state mirrors agent state.

## 2. Theme-to-domain mapping

| Fear & Hunger idea | App concept |
|---|---|
| Dungeon / floor | Project / repo |
| Adventurer | An agent (Claude Code session) |
| Party | Agents assigned to one project |
| Quest | A task or prompt |
| Hunger | Token / cost budget; zero = starved (paused) |
| Limbs | Tool permissions (arm = Edit/Write, leg = Bash, ...) |
| Blood / HP | Health: error rate, failing tests, stuck loops |
| Sanity | Context-window fill |
| Moon / time | Wall clock, scheduled runs |
| Death | Crashed or killed agent; grave keeps the transcript |
| Feigned death + red rune | Agent waiting for the user (looks dead, marker says "answer me") |
| Journal | Event log and transcripts |
| Spell | Skill |
| Relic | Plugin |
| Familiar | MCP server |

## 3. Architecture

Two projects:

- `Dungeon.Core`: plain .NET 8 library, no engine references, headless-testable.
  Orchestrator, Agent Supervisor, Claude Code runtime, Permission Broker,
  Budget/Health engine, Project Registry, Skill Catalog, Event Bus, SQLite + JSONL persistence.
- `Dungeon.Game`: Godot 4 (.NET) project. Thin view layer: subscribes to Core's event bus, sends commands back.

```
Godot view (Hub, Chambers, Altar, Crypt, HUD, Spellbook)
        | view models / commands
Dungeon.Core: Supervisor, Permission Broker, Budget/Health, Registry, SkillCatalog, Event Bus
        | IAgentRuntime                      | IStore
ClaudeCodeRuntime (child process,            SQLite + JSONL transcripts
stream-json over stdio, Job Object)
        |
claude CLI -> project working dirs (git worktree per agent)
```

### Components

- **IAgentRuntime**: Start, Send, Pause, Stop, event stream. ClaudeCodeRuntime launches
  `claude -p --output-format stream-json --input-format stream-json` in the agent's worktree.
  Each process is placed in a Windows Job Object so closing the app kills the agents.
- **Agent Supervisor**: lifecycle state machine
  Summoned -> Exploring -> AwaitingUser (needs input or approval from the user) -> Resting (paused) -> Starved (budget) -> Fallen (crashed) / Triumphant (done). Stall detection, concurrency limits.
- **Waiting-on-user rule**: any agent waiting for input from the user (a question, a permission approval, a plan to confirm) is shown as DEAD: the figure lies slumped/collapsed, its torch is snuffed, and the room goes cold. A pulsing marker (a red rune sigil floating above the corpse, with a faint heartbeat sound) distinguishes "waiting for you" from a truly crashed agent (Fallen), which has no marker and moves to the Crypt. Interacting with the marked corpse opens the pending question. Answering revives it. The marker also appears on the Hub door, the roster entry and the 2D dashboard, so waiting agents are visible from anywhere.
- **Permission Broker**: queues permission prompts as "offerings"; approve / deny / always-for-agent. Default is deny or ask; destructive commands are never auto-approved.
- **Budget/Health engine**: Hunger from tokens/cost vs cap, Sanity from context fill, Blood from failures. Threshold events.
- **Project Registry**: repo path + metadata; one git worktree and branch per agent.
- **Skill Catalog**: see section 5.
- **Persistence**: SQLite tables Project, Agent, Quest, Run, Event, Approval, BudgetSample; per-agent JSONL transcript.

## 4. 3D look: Fear & Hunger as a first-person crawler

- **Shaders**: vertex snapping/wobble, affine texture warp, low-res render target (~320x240, nearest-neighbor upscale), colour quantization with dithering, heavy fog, vignette.
- **Lighting**: mostly dark; flickering torches are the main light and also carry state (an agent's room dims when it starves or dies).
- **Models**: low-poly modular stone corridors, rusted gates, bone/flesh/cloth props, grimy 64x64 hand-painted textures.
- **HUD**: F&H-style flat dark menu windows over the 3D view; the character screen with the four-limb diagram is the agent status panel.
- **Audio**: drones, drips, footsteps, a muffled thud on approval; mute toggle.
- **Assets**: original only. No ripped Fear & Hunger assets (copyrighted); CC0 packs may fill gaps.

### Scenes

- **The Hub**: stone hallway; each project is a door with a nameplate; a lit torch above means an agent is working inside.
- **Project Chamber**: each agent is a robed 3D figure at a workstation; interact to open its panel.
- **Altar Room**: the permission queue as offerings on an altar.
- **Crypt**: finished and dead agents; transcripts as tombstones.
- **Fallback**: a key toggles a flat 2D dashboard listing all agents, for when walking is too slow.

## 5. Skills and plugin integration

Agents are `claude` processes, so they inherit skills, plugins, MCP servers and the global CLAUDE.md automatically. The app adds a catalog layer.

- **SkillCatalog** scans `~/.claude/skills`, the installed-plugins directory, and `enabledPlugins`/marketplaces in settings.json; reads each SKILL.md frontmatter and plugin manifest; lists MCP servers (e.g. a project-tracker server) from Claude config. Exact locations and formats to be verified in phase 1 rather than assumed.
- **Spellbook UI**: pick an agent and a skill, fill arguments, app sends `/<skill> args` as the prompt.
- **Quest templates** from existing workflows: your own project, planning and wrap-up skills.
- **Project doors** populated read-only from the cached project-tracker list; setup-project creates branch/clone.
- **Scheduled rituals**: existing Task Scheduler morning/evening flows appear as timed events; can be triggered on demand.
- **Plugin reload awareness**: unknown-skill errors surface the "run /reload-plugins" hint.

### Guardrails (Permission Broker default deny/ask)

- No writes to shared or network drives unless a skill explicitly allows it.
- Never touch desktop applications the agent did not launch.
- Agents inherit the global CLAUDE.md; the app does not override it.

## 6. Technology

| Concern | Choice |
|---|---|
| Language / engine | C# .NET 8, Godot 4 (.NET) |
| Core | Plain .NET class library, Microsoft.Extensions.Hosting (DI, logging) |
| Storage | SQLite (Microsoft.Data.Sqlite) + JSONL |
| Audio | Godot audio |
| Packaging | Godot Windows export |
| Tests | xUnit with a fake IAgentRuntime replaying recorded streams |

## 7. Security and safety

- Agents run only inside their assigned worktree with explicit tool allow-lists.
- No secrets in SQLite; use Windows Credential Manager or DPAPI.
- Hard spend caps per agent and per day; a kill switch stops every process.
- Transcripts stay local.

## 8. Roadmap

1. Core skeleton: ClaudeCodeRuntime + console harness, Job Object cleanup.
2. Catalog: SkillCatalog reads skills/plugins/MCP; CLI dump to verify.
3. Godot shell: flat 2D debug UI bound to Core (roster, transcript, altar).
4. 3D Hub: PSX shader pipeline, modular corridor kit, doors, torches, first-person movement, agent figures.
5. Spellbook and quests: casting skills, templates, project-tracker door population.
6. Health/hunger/sanity wired to the world (light dimming, figures slumping).
7. Polish: audio, Crypt, dashboard fallback, scheduled rituals, packaging.

## 9. Risks

- Art workload is the biggest cost; phases 1-3 deliver a useful tool before art.
- Claude Code stream-json and permission-prompt interfaces must be confirmed against the installed version (phase 1 spike).
- Godot C# on Windows needs the .NET SDK and a compatible Godot version.
