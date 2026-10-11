# Delegating to unity-worker

Each tool result stays in the main context and is paid again on every later turn. The worker does a chore in its own context and returns one verdict of at most 800 tokens.

Delegate (3+ `ug` calls, the steps in between not needed here):
- play to a state and report it (clear a room, open a menu, reach a level, then read the HUD or values);
- edit, then check in play mode (add a component, set fields, play, read them back, undo);
- scene or UI setup with checks (several objects, a `ug batch`, a picture);
- reproduce a bug in play mode and bring back the console lines and the state;
- profile a scenario (`perf --record` while playing a stretch).

Inline: 1-2 calls (one `set`, `inspect`, `compile`, a graph question); code edits; chores whose next step needs your judgment or the user's answer; a `ug batch` that does it all in one call.

Prompt, short:
- the chore and when it is done ("room 1 cleared: RunDirector reports 1 cleared");
- how to check it, and limits ("at most 120 s of play");
- addresses you already know (`Boot/AdOverlay/Bar`, `Assets/Prefabs/X.prefab`);
- keep or undo the edits; approvals the user gave ("saving Main.unity is approved").

The verdict has `verdict`, `did`, `checked`, `evidence`, `left`, `state`, `recheck` lines. Use it; do not run the chore again. `needs approval`: ask the user, then delegate again with the answer. `blocked`: fix the cause (compile errors are yours to fix) or tell the user. `not run: worker-agent is off`: do chores inline this session.

One worker per editor at a time: they share play mode and the Undo stack. `run_in_background` when you have other work meanwhile. Model: Sonnet by default; the call's `model` parameter overrides it.
