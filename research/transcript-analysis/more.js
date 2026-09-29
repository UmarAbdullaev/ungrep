// Two untested levers, on real transcripts.
// A) What Claude WRITES also stays in context: tool inputs (file contents, edit strings, eval code) x turns they persist.
// B) Isolation: streaks of Unity/editor/navigation turns run in a worker subagent with a small context;
//    main thread pays one turn plus a short summary. Added on top of the realistic whole-plugin levers.
const fs = require('fs'), path = require('path');
const dir = path.join(process.env.USERPROFILE, '.claude', 'projects', 'D--Projects-Unity-purge-squad');
const src = fs.readFileSync(path.join(__dirname, 'usage.js'), 'utf8');
const mcpNames = {}, cliCmds = {};
const categorize = eval('(' + src.slice(src.indexOf('function categorize'), src.indexOf('const resultLen')).trim() + ')');
const resultLen = (c) => typeof c === 'string' ? c.length : Array.isArray(c) ? c.reduce((a, x) => a + (x.type === 'text' ? (x.text || '').length : x.type === 'image' ? 6000 : 0), 0) : 0;
const loopRe = /unity: (compile|console)|unity: other mcp/;
const workerRe = /^unity: |unity yaml|^read: \.cs|^search: |^shell: search/;   // delegable: editor ops, play, tests, logs, navigation

const inputTT = {}; let assistantTextTT = 0, thinkingTT = 0, totalCtx = 0;
const sessions = [];
for (const f of fs.readdirSync(dir).filter((f) => f.endsWith('.jsonl'))) {
  const seen = new Set(), turns = [], pend = new Map(), inputs = [];
  for (const line of fs.readFileSync(path.join(dir, f), 'utf8').split('\n')) {
    if (!line.trim()) continue; let o; try { o = JSON.parse(line); } catch { continue; }
    if (o.isSidechain) continue; const m = o.message; if (!m || !Array.isArray(m.content)) continue;
    if (o.type === 'assistant') {
      if (m.usage && m.id && !seen.has(m.id)) { seen.add(m.id); const u = m.usage; turns.push({ ctx: (u.input_tokens || 0) + (u.cache_read_input_tokens || 0) + (u.cache_creation_input_tokens || 0), cats: [], res: 0 }); }
      const t = turns[turns.length - 1]; if (!t) continue;
      for (const c of m.content) {
        if (c.type === 'tool_use') {
          const cat = categorize(c.name, c.input); t.cats.push(cat); pend.set(c.id, t);
          const inp = c.input || {}; const len = (inp.content || '').length + (inp.new_string || '').length + (inp.old_string || '').length + (inp.command || '').length + (inp.code || inp.text || inp.script || '').length;
          inputs.push({ turn: turns.length - 1, tok: len / 4, cat: /\.cs$/i.test(inp.file_path || '') && /AgentScripts|[\\/]Editor[\\/]/i.test(inp.file_path || '') ? 'write: editor builder .cs' : cat });
        } else if (c.type === 'text') inputs.push({ turn: turns.length - 1, tok: (c.text || '').length / 4, cat: '__text' });
        else if (c.type === 'thinking') inputs.push({ turn: turns.length - 1, tok: (c.thinking || '').length / 4, cat: '__thinking' });
      }
    } else if (o.type === 'user') {
      for (const c of m.content) if (c.type === 'tool_result' && pend.has(c.tool_use_id)) pend.get(c.tool_use_id).res += resultLen(c.content) / 4;
    }
  }
  if (!turns.length) continue;
  const resets = []; for (let i = 1; i < turns.length; i++) if (turns[i].ctx < turns[i - 1].ctx * 0.6) resets.push(i);
  for (const x of inputs) {
    const end = resets.find((r) => r > x.turn) ?? turns.length; const tt = x.tok * Math.max(0, end - x.turn - 1);
    if (x.cat === '__text') assistantTextTT += tt; else if (x.cat === '__thinking') thinkingTT += tt; else inputTT[x.cat] = (inputTT[x.cat] || 0) + tt;
  }
  totalCtx += turns.reduce((a, t) => a + t.ctx, 0);
  sessions.push(turns);
}
const pct = (x) => (100 * x / totalCtx).toFixed(1) + '%';
console.log(`A) what Claude writes, persisting in context (share of all ${Math.round(totalCtx / 1e6)}M context-tokens):`);
for (const [k, v] of Object.entries(inputTT).sort((a, b) => b[1] - a[1]).slice(0, 8)) console.log('   ', k.padEnd(30), pct(v));
console.log('    assistant prose'.padEnd(35), pct(assistantTextTT));
console.log('    thinking (if kept by the API)'.padEnd(35), pct(thinkingTT));

// B) isolation on top of realistic levers (graph streak->2, gate loop->1, outputs x0.5, baseline -12k)
function sim(minStreak, share) {
  let total = 0;
  for (const turns of sessions) {
    const cls = turns.map((t) => !t.cats.length ? 'other' : t.cats.every((c) => loopRe.test(c)) ? 'loop' : t.cats.every((c) => workerRe.test(c)) ? 'work' : 'other');
    let shed = 0, prev = 0;
    for (let i = 0; i < turns.length;) {
      let j = i; while (j < turns.length && cls[j] === cls[i]) j++;
      const k = j - i;
      if (cls[i] === 'work' && k >= minStreak && ((i * 7919) % 100) / 100 < share) {
        // worker subagent: small base context, sees only its own streak
        let inner = 15000;
        for (let x = i; x < j; x++) { total += inner; inner += turns[x].res * 0.5 + 300; }
        const t = turns[i]; if (t.ctx < prev * 0.6) shed = 0; prev = t.ctx;
        total += Math.max(0, t.ctx - shed - 12000);      // one main turn for the delegation
        for (let x = i; x < j; x++) shed += turns[x].res + 250; // their outputs never enter main
        shed -= 800;                                        // the summary does
      } else {
        const keep = cls[i] === 'loop' ? 1 : cls[i] === 'work' ? 2 : Infinity;
        for (let x = i; x < j; x++) {
          const t = turns[x]; if (t.ctx < prev * 0.6) shed = 0; prev = t.ctx;
          if (x - i < keep) { total += Math.max(0, t.ctx - shed - 12000); shed += cls[i] === 'other' ? 0 : t.res * 0.5; }
          else shed += t.res + 200;
        }
      }
      i = j;
    }
  }
  return total;
}
console.log(`\nB) worker subagent for Unity/navigation streaks, on top of the realistic plugin (-21%):`);
for (const [m, s, label] of [[3, 0.5, 'half of streaks of 3+ turns'], [3, 1, 'all streaks of 3+ turns'], [2, 1, 'all streaks of 2+ turns']]) {
  console.log('   ', label.padEnd(30), `-${(100 * (1 - sim(m, s) / totalCtx)).toFixed(0)}% total`);
}
