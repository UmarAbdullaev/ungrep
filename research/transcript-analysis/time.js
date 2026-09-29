// Wall-clock: active session time vs time spent in compile/console/wait turns and in Unity tool calls.
const fs = require('fs'), path = require('path');
const dir = path.join(process.env.USERPROFILE, '.claude', 'projects', 'D--Projects-Unity-purge-squad');
const src = fs.readFileSync(path.join(__dirname, 'usage.js'), 'utf8');
const mcpNames = {}, cliCmds = {};
const categorize = eval('(' + src.slice(src.indexOf('function categorize'), src.indexOf('const resultLen')).trim() + ')');
const loop = /unity: (compile|console)|unity: other mcp/;
let active = 0, loopT = 0, unityT = 0, modelT = 0;
for (const f of fs.readdirSync(dir).filter((f) => f.endsWith('.jsonl'))) {
  const ev = [];
  for (const line of fs.readFileSync(path.join(dir, f), 'utf8').split('\n')) {
    if (!line.trim()) continue; let o; try { o = JSON.parse(line); } catch { continue; }
    if (o.isSidechain || !o.timestamp || !o.message) continue;
    ev.push({ t: Date.parse(o.timestamp), o });
  }
  const call = new Map();
  for (let i = 1; i < ev.length; i++) { const d = ev[i].t - ev[i - 1].t; if (d > 0 && d < 10 * 60e3) active += d; }
  for (let i = 0; i < ev.length; i++) {
    const { t, o } = ev[i]; const c = o.message.content; if (!Array.isArray(c)) continue;
    if (o.type === 'assistant') for (const x of c) if (x.type === 'tool_use') call.set(x.id, { t, cat: categorize(x.name, x.input) });
    if (o.type === 'user') for (const x of c) if (x.type === 'tool_result' && call.has(x.tool_use_id)) {
      const s = call.get(x.tool_use_id); const d = t - s.t; if (d <= 0 || d > 10 * 60e3) continue;
      if (/^unity/.test(s.cat)) unityT += d; if (loop.test(s.cat)) loopT += d;
      // model time to react to this result
      const nxt = ev.slice(i + 1).find((e) => e.o.type === 'assistant'); if (nxt && loop.test(s.cat)) { const m = nxt.t - t; if (m > 0 && m < 5 * 60e3) modelT += m; }
    }
  }
}
const h = (ms) => (ms / 3.6e6).toFixed(1) + ' h';
console.log('active session time', h(active));
console.log('all Unity tool execution', h(unityT), `(${(100 * unityT / active).toFixed(0)}%)`);
console.log('compile/console/wait: tool time', h(loopT), '+ model reacting', h(modelT), '=', h(loopT + modelT), `(${(100 * (loopT + modelT) / active).toFixed(0)}% of active)`);
