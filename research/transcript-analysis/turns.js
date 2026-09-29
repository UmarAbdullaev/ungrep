// Token-turns: how long each tool result stayed in context (result tokens x turns until session end or compaction).
const fs = require('fs');
const path = require('path');
const dir = path.join(process.env.USERPROFILE, '.claude', 'projects', 'D--Projects-Unity-purge-squad');
const src = fs.readFileSync(path.join(__dirname, 'usage.js'), 'utf8');
const mcpNames = {}, cliCmds = {};
const categorize = eval('(' + src.slice(src.indexOf('function categorize'), src.indexOf('const resultLen')).trim() + ')');
const resultLen = (c) => typeof c === 'string' ? c.length : Array.isArray(c) ? c.reduce((a, x) => a + (x.type === 'text' ? (x.text || '').length : x.type === 'image' ? 6000 : 0), 0) : 0;

const tt = {};           // category -> token-turns
let allTurnsCtx = 0, allTurns = 0;
const baselines = [], sessionsInfo = [];
let compactions = 0;

for (const f of fs.readdirSync(dir).filter((f) => f.endsWith('.jsonl'))) {
  const pending = new Map();
  const seen = new Set();
  const turns = [];      // context size per model turn
  const results = [];    // {turn, tok, cat}
  for (const line of fs.readFileSync(path.join(dir, f), 'utf8').split('\n')) {
    if (!line.trim()) continue;
    let o; try { o = JSON.parse(line); } catch { continue; }
    if (o.isSidechain) continue;
    const m = o.message;
    if (!m || !Array.isArray(m.content)) continue;
    if (o.type === 'assistant') {
      if (m.usage && m.id && !seen.has(m.id)) {
        seen.add(m.id);
        const u = m.usage;
        turns.push((u.input_tokens || 0) + (u.cache_read_input_tokens || 0) + (u.cache_creation_input_tokens || 0));
      }
      for (const c of m.content) if (c.type === 'tool_use') pending.set(c.id, categorize(c.name, c.input));
    } else if (o.type === 'user') {
      for (const c of m.content) if (c.type === 'tool_result' && pending.has(c.tool_use_id)) results.push({ turn: turns.length, tok: resultLen(c.content) / 4, cat: pending.get(c.tool_use_id) });
    }
  }
  if (!turns.length) continue;
  // compaction = context drops by more than 40% between consecutive turns
  const resets = [];
  for (let i = 1; i < turns.length; i++) if (turns[i] < turns[i - 1] * 0.6) resets.push(i);
  compactions += resets.length;
  for (const r of results) {
    const end = resets.find((x) => x > r.turn) ?? turns.length;
    tt[r.cat] = (tt[r.cat] || 0) + r.tok * Math.max(0, end - r.turn);
  }
  const sum = turns.reduce((a, b) => a + b, 0);
  allTurnsCtx += sum; allTurns += turns.length;
  baselines.push(turns[0]);
  sessionsInfo.push({ turns: turns.length, avg: Math.round(sum / turns.length), max: Math.max(...turns), first: turns[0], resets: resets.length });
}

const total = Object.values(tt).reduce((a, b) => a + b, 0);
console.log(`turns ${allTurns}, avg context per turn ${Math.round(allTurnsCtx / allTurns)} tok, total context-tokens ${Math.round(allTurnsCtx / 1e6)}M, compactions ${compactions}`);
console.log(`first-turn context (system + tools + CLAUDE.md + CODEMAP): median ${baselines.sort((a, b) => a - b)[baselines.length >> 1]} tok`);
console.log(`tool results account for ${Math.round(total / 1e6)}M of ${Math.round(allTurnsCtx / 1e6)}M context-tokens (${(100 * total / allTurnsCtx).toFixed(0)}%)\n`);
console.log('category'.padEnd(36), 'token-turns', ' % of tool share');
for (const [k, v] of Object.entries(tt).sort((a, b) => b[1] - a[1]).slice(0, 16)) console.log(k.padEnd(36), (Math.round(v / 1e5) / 10 + 'M').padStart(11), (100 * v / total).toFixed(1).padStart(8) + '%');
console.log('\nsessions (turns / avg ctx / max ctx / first ctx / compactions):');
for (const s of sessionsInfo.sort((a, b) => b.turns - a.turns).slice(0, 12)) console.log(`  ${String(s.turns).padStart(4)}  ${String(s.avg).padStart(7)}  ${String(s.max).padStart(7)}  ${String(s.first).padStart(6)}  ${s.resets}`);
