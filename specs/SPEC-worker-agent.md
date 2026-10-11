# Spec: worker-agent

Part of unitygraph (see `../SPEC.md`, module table). Status: **built 2026-10-11** (W1). Builds on
`query`, `editor-link`, `editor-ops`, `play-input` and `unity-loop`: the worker does with `ug`
what the main thread would, in its own context.

## Objective

Every tool result stays in the main thread's context and is read again on every later turn
(SPEC design rule 1). A multi-step Unity chore (play to a state, edit and check, set up a scene)
leaves ten or twenty intermediate answers there that nothing needs after the chore is done. On
purge-squad's real sessions a worker subagent for such streaks was the biggest lever left: −36%
to −41% context with the graph and the compile gate, against −21% to −33% without (SPEC
Evidence; the simulation assumed a worker starting at ~15k context and a verdict of at most 800
tokens).

The worker runs the chore in its own context with `ug` and hands back one verdict. The main
thread pays the call (its prompt) and the verdict, once.

## Scope

In: the subagent file `agents/unity-worker.md`; the delegation rule (one line in
`skills/unity/SKILL.md`, the rest in `skills/unity/worker.md`, read on demand); the verdict
contract; failure behaviour; what the worker may not do; the `worker-agent` switch; counting
delegations in `ug stats`; budget tests; a dry run on purge-squad.

Out: code edits (the main thread writes code; the worker has no Edit or Write tool); code
navigation alone (one `ug explain` answers it, under the 3-step rule; Claude Code's own Explore
agent covers long reading streaks); several workers on one editor at once; a SubagentStop hook or
any new hook event; Bash command allowlists (a plugin cannot ship them, see Limits).

## The subagent file

`agents/unity-worker.md`, found by convention (every Markdown file under a plugin's `agents/`
defines one agent; `plugin.json` needs no `agents` key). Claude Code calls it by its scoped name
`unitygraph:unity-worker`.

```yaml
---
name: unity-worker
description: Unity editor chores of 3+ ug calls (play to a state, edit then check in play mode, scene setup). Give the chore, how to check it, known addresses, keep or undo edits. Returns a verdict of at most 800 tokens; don't redo the chore. 1-2 calls: do them inline.
tools: Bash, Read, Grep
model: sonnet
maxTurns: 60
skills:
  - unitygraph:unity
---
```

- **description** is the only part loaded every session. It carries the 3-step rule and the call
  shape, because the main thread decides to delegate (and writes the prompt) before it has loaded
  the skill.
- **tools**: `Bash` for `ug`, `Read` for the `file:line` an answer names and for `ug shot`
  pictures, `Grep` for logs and files the graph does not cover. No `Edit`/`Write` (code is the
  main thread's), no `Agent` (subagents can nest up to three levels; the worker does not), no
  MCP tools (a missing `tools` line would inherit them all, Unity MCP servers included).
- **model**: `sonnet`, SPEC Decisions #6. The call's `model` parameter overrides it per call;
  `CLAUDE_CODE_SUBAGENT_MODEL` does not (front matter wins) unless
  `CLAUDE_CODE_SUBAGENT_MODEL_FORCE=1` is set too. `bench` measures the redo rate with Haiku;
  under ~5% moves the default to Haiku.
- **maxTurns 60**: a hard stop. The rooms runs of O10 took 23-31 calls for two cleared rooms; the
  prompt asks the worker to stop by itself after about 40 tool calls. Past 60 turns Claude Code
  ends the run with partial output.
- **skills**: the `unity` skill's body is put into the worker's context at start (its command list,
  addresses and rules: ~600 tokens there, none in the main thread). The docs do not say whether a
  plugin agent names a plugin skill as `unitygraph:unity` or `unity`; the prompt therefore says to
  read `${CLAUDE_PLUGIN_ROOT}/skills/unity/SKILL.md` when the skill is not in its context. The
  dry run checks which happened.
- `${CLAUDE_PLUGIN_ROOT}` is replaced in agent and skill bodies; it is not set in the Bash
  tool's environment, so the body spells the `ug` path out as the skill does. (A plugin's `bin/` is
  also on the Bash tool's `PATH` while the plugin is enabled; the full path works either way.)
- Not used: `hooks`, `mcpServers`, `permissionMode` and `initialPrompt` (ignored for plugin
  agents), `memory`, `background`, `isolation` (a worktree has no open editor), `omitClaudeMd`
  (the project's own rules apply to the worker too), `effort`, `color`.

Format checked against Claude Code's docs on 2026-10-11 (plugins/components "agents" and
"frontmatter fields in plugin agents", sub-agents "supported frontmatter fields", "choose a
model", "preload skills", plugins manifest reference "where each variable resolves" and
"executables"), and with Claude Code 2.1.295: `claude plugin validate` passes and `claude plugin
details unitygraph@unitygraph-dev` lists `Agents (1) unity-worker`, always-on ~93 tokens
(unity ~30, unity-worker ~70).

## Delegation rule

Delegate a Unity chore of **3 or more `ug` calls** whose intermediate answers the main thread does
not need: play to a state and report it; edit, then check in play mode; scene or UI setup with
checks; reproduce a bug in play mode and bring back the console lines and the state; profile a
scenario. A 1 or 2 call chore stays inline (one `ug set`, `ug inspect`, `ug compile`, a graph
question): delegating it costs the call, the worker's start and the verdict for no saving.
Inline too: code edits, chores whose next step needs the main thread's judgment or the user's
answer, and anything one `ug batch` does in one call (it counts as one call).

The prompt names the chore and when it is done, how to check it and any limit ("at most 120 s
of play"), the addresses already known, whether to keep or undo the edits, and approvals the user
already gave. The main thread uses the verdict and does not run the chore again; `recheck`
commands are there if it must look.

One worker per editor at a time: workers share play mode and the Undo stack. A background run
(`run_in_background`) is fine while the main thread does other work.

Where it lives: the agent description (always loaded) has the rule and the call shape;
`SKILL.md` has one line pointing at the agent and at `worker.md`; `worker.md` (~430 tokens, read
on demand) has the examples above, the prompt shape and what to do with each verdict.

## Verdict contract

The worker's whole final message, plain text, at most **800 tokens** (3,200 bytes):

```
verdict: done | partly done | not done | blocked: <why> | needs approval: <what> | not run: worker-agent is off
did: one line per change with its address (Main:Level/Enemy, Player:PlayerController.speed 5 -> 7.5)
checked: <what> by `<ug command>`: <result>
evidence: file:line, object and asset paths, console ids, picture paths
left: what is undone or unchecked, and why | nothing
state: play mode, scenes modified and unsaved, undo steps (`ug undo` xN reverts them)
recheck: the `ug` commands that show the result again
```

- Only what was read back counts as checked; a value the worker set but did not read back goes
  under `left`.
- Exact error text and numbers. No dumps: no trees, console or answers pasted past the lines that
  matter. A picture is its path and one line on what it shows.
- Evidence anchors are what lets the main thread continue without repeating the chore (SPEC
  Overhead budget: "a worker verdict missing a detail makes the main thread repeat the chore").

## Failure behaviour

- **First call `ug status`.** It names the editor's state and, only when the switch is off, the
  `worker-agent` line (below).
- **Unity not reachable:** the worker does what the graph answers and stops: `blocked: Unity not
  reachable (<reason>)`. It never starts, restarts or closes an editor.
- **Busy editor:** `ug compile` and `ug play` wait out reloads themselves; still busy after their
  wait: `blocked`.
- **Compile errors in the project:** the `ug compile` lines (at most 20) go in the verdict and the
  worker stops: `blocked: scripts do not compile (N errors)`. The project's code is the main
  thread's to fix. Errors in the worker's own `ug eval` snippet are its own: it fixes them.
- **A chore that cannot finish:** one step failing twice the same way, about 40 tool calls, or a
  time the prompt gave running out: stop, `not done` or `partly done`, with what was tried, the
  last state read and the evidence. `maxTurns: 60` is the hard stop behind it.
- **Before every answer:** play mode stopped unless the chore said to leave it playing (play-input
  releases held input on stop), nothing saved unasked, and the edits it kept counted under `state`.

## What the worker may not do

- `ug save`, prefab-asset edits and `ug asset` writes (written at once, no undo), unless the chore
  says so.
- `run`/`eval` of code the chore does not need (the skill's Ask list).
- git of any kind; `ug package add|remove`; `ug bridge install|remove`; `ug asset delete`; editing
  project files; starting, closing or restarting Unity; spawning agents.
- When a chore needs one of these anyway, the worker stops with `needs approval: <what, why>`.
  A subagent cannot ask the user; the main thread asks, then delegates again with the answer.

**Limits.** These are prompt rules plus the tool list. Claude Code applies only the `agent` and
`subagentStatusLine` settings from a plugin, so a plugin cannot ship `permissions.deny` rules for
Bash commands; the user's own permission rules apply to subagents as to the main thread. `ug`
itself refuses `Packages/` and vendored folders, File/Quit and Build menus, and never saves
without `ug save` (SPEC-editor-ops safety rules). Whether the plugin's hooks (guards, compile
gate) fire on a subagent's tool calls is not documented; the worker edits no files, so neither
has anything to do there.

## Switch

`modules.worker-agent` in `.claude/unitygraph.json` (default on). Claude Code lists every agent
under a plugin's `agents/`, and no plugin setting hides one, so the switch works through the
worker's first call: with the module off, `ug status` ends with `worker-agent is off in
.claude/unitygraph.json: chores run inline, not in unity-worker`, and the worker answers `verdict:
not run: worker-agent is off` with no other call. `worker.md` tells the main thread to do chores
inline after that answer. With the module on, `ug status` prints nothing more (0 tokens).

## Overhead

| What | Budget | Measured |
|---|---|---|
| Skill and agent listing lines, every session | ≤ 300 tokens together | 104 (bytes / 4: skill 27, agent 78 with its `(Tools: ...)`); `claude plugin details`: ~93 |
| `unity` skill body, on first use | ≤ 600 tokens | 599 (the delegation line replaced the UI line; hints the answers already state were cut) |
| `worker.md`, read on demand by the main thread | none set | ~433 |
| Worker body, in the worker's own context | none set | ~628 |
| Verdict, in the main thread | ≤ 800 tokens | 738 and 486 in the dry run, plus ~180 of Claude Code's hand-back frame |

`AgentBudgetTests` checks the listing budget the way `SkillBudgetTests` checks the body: the two
lines Claude Code lists (`- unitygraph:unity: <description>` and `- unitygraph:unity-worker:
<description> (Tools: Bash, Read, Grep)`), bytes / 4, at most 300. It also checks the front matter
against the fields a plugin agent supports, `model: sonnet`, the tool list, the preloaded skill,
and that the prompt keeps every verdict line.

## Measuring

**`ug stats`** counts delegations (SPEC Overhead budget: "`stats` counts short delegations"). One
line, only when the report or its baseline has any:

```
unity-worker 5 delegations | 1 short (under 3 tool calls) | worker context 157.0k | verdict avg 298 tokens, 1 over 800 | 1 without a result
```

- A delegation is an `Agent` (or older `Task`) call on the main thread whose `subagent_type` is
  `unity-worker` or ends in `:unity-worker`, started inside the report's time window. A
  subagent's own entries (`isSidechain`) are not the main thread's.
- Tool calls inside the worker and the verdict come from what Claude Code records in the main
  transcript: a foreground run's `toolUseResult` carries `totalToolUseCount` and the verdict as
  the worker wrote it in `content` (the tool result's own text adds Claude Code's hand-back frame
  and indent), or `handbackReport.text` when the report was handed back as a message; a
  background run answers `async_launched` there, and its verdict arrives later as a
  `<task-notification>` naming the call's `<tool-use-id>`, with `<tool_uses>N</tool_uses>` and the
  verdict in `<result>`. A resumed worker notifies again under the same id: the largest count and
  the largest verdict are kept. These shapes are observed in this machine's transcripts (Claude
  Code 2.1.2xx), not documented: the docs call the transcript format internal. The SDK's
  `AgentOutput` type documents the same fields (`status`, `totalToolUseCount`, ...).
- Worker context: what the workers read, summed over their turns the way the main thread's context
  is (input + cache read + cache write per turn, a turn counted once however many lines it was
  written on), from each worker's own transcript `<session>/subagents/agent-<id>.jsonl`. The agent
  id comes from `toolUseResult.agentId` (foreground and `async_launched` alike) or the
  notification's `<task-id>`. A worker whose transcript is gone adds 0; one still running adds
  what it read so far. The `totalTokens` and `<subagent_tokens>` the main transcript records are
  only the worker's last context, not what it read: in this machine's transcripts 60.7k recorded
  against 2.14M summed over 44 turns, 29.6k against 153k over 6. The main thread's own context
  line stays the main thread's; worker context is beside it, so the two add up to what the session
  read. (Added 2026-10-11 at the user's ask: "will not the agents make it heavy on spending".)
- Short: fewer than 3 tool calls inside the worker. Over budget: a verdict past 800 tokens
  (bytes / 4). Without a result: no count seen yet (still running, or the session ended first).
- `--json` gains `"worker": {"delegations", "short", "overBudget", "noResult", "verdictTokens",
  "contextTokens"}`; a report written before W1 reads back with no `worker` and compares as zero. The calls stay in
  the `subagent` category as before, so earlier reports stay comparable.

**Dry run** (below): chores of 3+ steps on purge-squad, once inline and once delegated, main-thread
tokens for each.

**`bench` (B1)** measures the redo rate (the main thread repeating a chore after a verdict), with
Sonnet and with Haiku (Decisions #6).

## Testing

- `tests/UnityGraph.Stats.Tests/WorkerDelegationsTests` on a transcript in the recorded shapes
  (`Fixtures/worker/delegations.jsonl`): a foreground result, a short one, a background run
  notified twice, a hand-back, another agent type, a subagent's own Agent call, a background shell
  notification, a run with no result; the workers' own transcripts beside it
  (`Fixtures/worker/delegations/subagents/`: a turn written on two lines, a resumed run, a running
  one, a missing one, another agent type's); the time window; directory totals; the text line
  (golden), an old baseline without `worker`, a baseline-only line, the JSON round trip.
- `tests/UnityGraph.Cli.Tests/AgentBudgetTests`: listing budget, front matter, verdict contract,
  the skill's pointers.
- `EditorCommandTests`: `ug status` with the switch off (editor reachable and not), and silent
  with it on.
- `claude plugin validate .claude-plugin/marketplace.json` passes.

## Dry run

What a chore costs is mostly turns, not answers: every main-thread tool call is a turn that reads
the whole main context again (purge-squad's sessions: 245.5k per turn on average, `ug stats`
all time). A delegated chore costs the main thread two turns (the call and the verdict) plus the
prompt and the verdict, and the worker's own turns over its smaller context. It saves when the main
context is much larger than the worker's and the chore takes more than a few calls; a short chore
early in a session can cost more delegated than inline.

Inline, 2026-10-11, done by the main thread of this session (main context 112-119k per turn),
purge-squad as found: Boot open and already modified, the game's save backed up and put back,
`git status` the same after.

| Chore | Calls (turns) | Main-thread context read | Answers left in context |
|---|---|---|---|
| 1. Play until room 1 is cleared, report the HUD | 4 (4) | 0.45M | ~0.9k |
| 2. Add an AudioSource to `Boot`, set two fields, check in play mode, undo | 9 (9) | 1.05M | ~1.5k |

- Chore 1: `play`; `batch "click DEPLOY" "wait 3" "eval --file state.cs"`; `batch "wait 6" "eval
  --file state.cs"` (room 1/10 cleared, gate open, HP 100/100, level-up pick open); `batch "ui"
  "stop"` (the three boon cards, New Cards, +1 Pick).
- Chore 2 was not checked in play mode: `ug inspect Boot` and `ug find --live` answered not
  found once playing, and the main thread took it for Boot unloading. The worker found why: Boot
  moves to DontDestroyOnLoad, which `ug`'s live lookups do not search (a `ug` gap, filed
  separately). Two calls went to an address clash (`Boot:AudioSource` reads as scene Boot, object
  AudioSource; `--component AudioSource` works; filed separately) and two to `--help`. Three
  `ug undo` removed the component; Boot was left as found.

Delegated, 2026-10-11: `claude -p` (Claude Code 2.1.295, the user's default model, Opus 5.5) in
purge-squad with `--plugin-dir` this repo, `--allowedTools "Bash(*bin/ug*)" Read Grep Agent`, the
same two chores asked in a sentence each. The main thread wrote the worker's prompt itself; the
worker ran on Sonnet 5.5. Boot compared with a snapshot after the undo chore: the same. The game's
save put back, `git status` the same.

| Chore | Main turns, context | Prompt | Verdict | Worker calls (turns) | Worker first → last context | Worker context | Cost main / worker | Wall |
|---|---|---|---|---|---|---|---|---|
| 1 | 2, 161k | 559 | 738, `done` | 18 (17) | 34.4k → 45.0k | 669k | $0.50 / $0.29 | 112 s |
| 2 | 2, 161k | 302 | 486, `done` | 15 (14) | 33.9k → 41.0k | 534k | $0.46 / $0.22 | 83 s |

- Both verdicts were under 800 tokens and complete enough that the main thread answered from
  them in one turn: no chore was redone (redo rate 0 of 2). Chore 2 was checked in play mode,
  where the inline run was not.
- The main thread's tool result is larger than the verdict: Claude Code frames a foreground
  result (a ~470-byte `[Subagent hand-back]` notice) and indents every line by two spaces. Chore
  2: 1,943 bytes written, 2,664 bytes read by the main thread, ~180 tokens per delegation on top
  of the verdict. `ug stats` counts the verdict as written, the budget being on it.
- A worker starts at ~34k context (system prompt, tools, the preloaded skill, the agent prompt,
  CLAUDE.md and memory), not the ~15k SPEC Evidence assumed.
- The worker made 15-18 calls where the inline runs made 4 and 9. Part was the main thread's
  bigger ask in chore 1 (HUD, run state, console); part was overhead: 3 and 5 `--help` lookups,
  and 1 and 2 calls refused by the permission rule because the worker chained `ug` through a
  shell variable (`$U undo`). The worker prompt now says to write `ug` by its full path in every
  command.
- `totalTokens` recorded in the main transcript: 46,369 and 41,838, the workers' last context;
  what they read: 669k and 534k.

What the main thread reads for a chore of N calls at main context C: inline (N + 1)·C, delegated
2·C plus the worker's context.

| Chore | C = 80k inline / delegated | C = 245k (purge-squad's average) inline / delegated |
|---|---|---|
| 1 (inline 4 calls) | 400k / 829k (+107%) | 1.23M / 1.16M (−5%) |
| 2 (inline 9 calls) | 800k / 694k (−13%) | 2.45M / 1.02M (−58%) |

Worker context is Sonnet's, read mostly from cache: $0.29 bought 669k of it in chore 1, against
$0.50 for the main thread's 161k (a fresh session, a third of it cache writes). So delegation pays
for chores that take many calls in a large session, and loses on a lean chore early in a session;
the 3-call rule keeps the smallest chores inline, and `ug stats` now shows the workers' context
beside the main thread's so a project can see which way it goes.

## Decisions

1. The worker has `Bash, Read, Grep` only: no file edits, no nested agents, no MCP tools.
2. Navigation alone is not a worker chore: the graph answers it in one or two calls.
3. `maxTurns: 60` as the hard stop, about 40 tool calls as the worker's own stop.
4. The switch works through `ug status`, since a plugin agent cannot be hidden.
5. `ug stats` keeps delegations in the `subagent` category and adds the worker line and `worker`
   JSON beside it; the report version stays 1 (an old report reads back without `worker`).
6. The skill body stays at 599 tokens: the delegation line replaced the UI line (both pointers in
   one line), and three hints the answers already state were cut (`one call beats grep`, `Run more
   if needed`, `--help` in the `perf` list).
7. The model stays `sonnet` (the user asked whether a fixed model limits it, 2026-10-11, and chose
   to keep it): Sonnet costs less per token for chores that are mostly play, set and read back. The
   call's `model` parameter picks another model for one chore. `inherit` would follow the main
   thread and run every chore at its rates. No per-project setting exists: a plugin cannot fill its
   agent's front matter from `.claude/unitygraph.json`.
8. `ug stats` reports worker context (the user's choice, 2026-10-11), summed from the workers' own
   transcripts, since the counts Claude Code records in the main transcript are a last context, not
   a cost.

## Open points

1. Settled by the dry run: `skills: [unitygraph:unity]` preloads the skill (its text is in both
   workers' first message; neither read `SKILL.md`).
2. Whether the plugin's hooks fire on the worker's tool calls (not documented; nothing depends on
   it today).
