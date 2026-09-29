# Transcript analysis (prototype of `stats`)

The scripts behind the Evidence section of `SPEC.md`. They read the local Claude Code
transcripts of one project (`~/.claude/projects/D--Projects-Unity-purge-squad/*.jsonl`,
hard-coded) and print where the context tokens went. Run with `node <script>.js`.

Quick prototypes, kept for reproducibility. Module `stats` replaces them.

| Script | Answers |
| --- | --- |
| `usage.js` | Tool calls and tool-output size by category; defines `categorize`, which the others load. |
| `turns.js` | Average context per turn, first-turn baseline, how long each category's output stayed in context. |
| `calls.js` | Context re-read by the turns that consumed each category's results; what the `eval` calls were for. |
| `time.js` | Active session time vs time in the compile/console/wait loop. |
| `graph.js` | Upper bound for a graph: every streak of navigation turns collapsed to 1 or 2 calls. |
| `combined.js` | Graph + compile gate + compact output + smaller map, simulated together. |
| `more.js` | What Claude writes (persisting tool inputs) and the worker-subagent lever. |
| `reread.js` | Unchanged files read again. |

"Context tokens" = input + cache read + cache write per model turn, summed over turns.
A drop of more than 40% between consecutive turns is treated as a compaction.
