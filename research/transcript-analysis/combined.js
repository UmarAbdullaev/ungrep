// Whole-plugin what-if on real transcripts, levers simulated together (no double counting).
// Loop streak (recompile/wait/status/console turns) -> gone (optimistic) or 1 turn (realistic).
// Navigation streak (search/read/yaml/inspect turns) -> 1 call (optimistic) or 2 calls (realistic).
// Kept navigation/noisy outputs are half the size; baseline -12k (generated per-folder map instead of a monolithic one).
const fs = require('fs'), path = require('path');
const dir = path.join(process.env.USERPROFILE, '.claude', 'projects', 'D--Projects-Unity-purge-squad');
const src = fs.readFileSync(path.join(__dirname, 'usage.js'), 'utf8');
const mcpNames = {}, cliCmds = {};
const categorize = eval('(' + src.slice(src.indexOf('function categorize'), src.indexOf('const resultLen')).trim() + ')');
const resultLen = (c) => typeof c === 'string' ? c.length : Array.isArray(c) ? c.reduce((a, x) => a + (x.type === 'text' ? (x.text || '').length : x.type === 'image' ? 6000 : 0), 0) : 0;
const inspectRe = /GetComponent|\.transform\.|position|localScale|Debug\.Log|return |ToString\(|string\.Join|\$"/;
const mutateRe = /SaveAsPrefabAsset|PrefabUtility|EditorSceneManager\.(Save|Open|MarkSceneDirty)|AddComponent|Undo\.|SetParent|new GameObject|Instantiate|DestroyImmediate|AssetDatabase\.(CreateAsset|SaveAssets|ImportAsset|DeleteAsset|MoveAsset)|SetDirty|runInBackground|isPlaying/;
const loopRe = /unity: (compile|console)|unity: other mcp/;
const noisyRe = /unity: (console|tests)/;

function kindOf(name, input, cat) {
  if (loopRe.test(cat)) return 'loop';
  const s = JSON.stringify(input || {});
  if (cat === 'unity: eval/run_script') return inspectRe.test(s) && !mutateRe.test(s) ? 'nav' : 'other';
  if (cat === 'read/search: unity yaml' || cat === 'read: .cs' || cat === 'search: grep/glob') return 'nav';
  if (cat === 'unity: scene/asset inspect+edit') return /get_|list|hierarchy|find/.test(name) ? 'nav' : 'other';
  if (cat === 'shell: search/read') return /editor\.log|\.log\b/i.test(s) ? 'other' : 'nav';
  if (noisyRe.test(cat)) return 'noisy';
  return 'other';
}

const sessions = [];
for (const f of fs.readdirSync(dir).filter((f) => f.endsWith('.jsonl'))) {
  const seen = new Set(), turns = [], pend = new Map();
  for (const line of fs.readFileSync(path.join(dir, f), 'utf8').split('\n')) {
    if (!line.trim()) continue; let o; try { o = JSON.parse(line); } catch { continue; }
    if (o.isSidechain) continue; const m = o.message; if (!m || !Array.isArray(m.content)) continue;
    if (o.type === 'assistant') {
      if (m.usage && m.id && !seen.has(m.id)) { seen.add(m.id); const u = m.usage; turns.push({ ctx: (u.input_tokens || 0) + (u.cache_read_input_tokens || 0) + (u.cache_creation_input_tokens || 0), kinds: [], res: 0, noisyRes: 0 }); }
      const t = turns[turns.length - 1];
      for (const c of m.content) if (c.type === 'tool_use' && t) { const k = kindOf(c.name.replace(/^mcp__.*__/, ''), c.input, categorize(c.name, c.input)); t.kinds.push(k); pend.set(c.id, { t, k }); }
    } else if (o.type === 'user') {
      for (const c of m.content) if (c.type === 'tool_result' && pend.has(c.tool_use_id)) { const p = pend.get(c.tool_use_id); const n = resultLen(c.content) / 4; p.t.res += n; if (p.k !== 'other') p.t.noisyRes += n; }
    }
  }
  for (const t of turns) t.cls = t.kinds.length && t.kinds.every((k) => k === 'loop') ? 'loop' : t.kinds.length && t.kinds.every((k) => k === 'nav') ? 'nav' : 'other';
  if (turns.length) sessions.push(turns);
}

function simulate({ keepLoop, keepNav, shrink, lean }) {
  let total = 0, kept = 0;
  for (const turns of sessions) {
    // mark removed turns: in each streak of loop/nav turns keep only the first N
    const removed = new Array(turns.length).fill(false);
    for (let i = 0; i < turns.length;) {
      const cls = turns[i].cls; let j = i; while (j < turns.length && turns[j].cls === cls) j++;
      const keep = cls === 'loop' ? keepLoop : cls === 'nav' ? keepNav : Infinity;
      for (let x = i + keep; x < j; x++) removed[x] = true;
      i = j;
    }
    let shed = 0, prev = 0;
    for (let i = 0; i < turns.length; i++) {
      const t = turns[i];
      if (t.ctx < prev * 0.6) shed = 0; prev = t.ctx;       // a real compaction resets everything
      if (!removed[i]) { total += Math.max(0, t.ctx - shed - lean); kept++; }
      shed += removed[i] ? t.res + 200 : t.noisyRes * shrink; // removed turns never add their output; kept ones add less
    }
  }
  return { total, kept };
}
const base = simulate({ keepLoop: Infinity, keepNav: Infinity, shrink: 0, lean: 0 });
const row = (label, o) => { const r = simulate(o); console.log(label.padEnd(44), `${Math.round(r.total / 1e6)}M`.padStart(5), `-${(100 * (1 - r.total / base.total)).toFixed(0)}%`.padStart(5), `turns ${r.kept} (-${(100 * (1 - r.kept / base.kept)).toFixed(0)}%)`); };
console.log('actual'.padEnd(44), `${Math.round(base.total / 1e6)}M`.padStart(5), '     ', `turns ${base.kept}`);
row('graph only, realistic (streak -> 2 calls)', { keepLoop: Infinity, keepNav: 2, shrink: 0.5, lean: 0 });
row('graph only, optimistic (streak -> 1 call)', { keepLoop: Infinity, keepNav: 1, shrink: 0.5, lean: 0 });
row('compile gate only, realistic (1 turn left)', { keepLoop: 1, keepNav: Infinity, shrink: 0, lean: 0 });
row('WHOLE PLUGIN, realistic', { keepLoop: 1, keepNav: 2, shrink: 0.5, lean: 12000 });
row('WHOLE PLUGIN, optimistic', { keepLoop: 0, keepNav: 1, shrink: 0.6, lean: 12000 });
