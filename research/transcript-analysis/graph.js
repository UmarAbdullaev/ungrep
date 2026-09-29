// Upper bound for a code+asset graph: collapse every streak of pure-navigation turns into 1 (optimistic) or 2 (realistic) calls.
const fs = require('fs'), path = require('path');
const dir = path.join(process.env.USERPROFILE, '.claude', 'projects', 'D--Projects-Unity-purge-squad');
const src = fs.readFileSync(path.join(__dirname, 'usage.js'), 'utf8');
const mcpNames = {}, cliCmds = {};
const categorize = eval('(' + src.slice(src.indexOf('function categorize'), src.indexOf('const resultLen')).trim() + ')');
const resultLen = (c) => typeof c === 'string' ? c.length : Array.isArray(c) ? c.reduce((a, x) => a + (x.type === 'text' ? (x.text || '').length : x.type === 'image' ? 6000 : 0), 0) : 0;
const inspectRe = /GetComponent|\.transform\.|position|localScale|Debug\.Log|return |ToString\(|string\.Join|\$"/;
const mutateRe = /SaveAsPrefabAsset|PrefabUtility|EditorSceneManager\.(Save|Open|MarkSceneDirty)|AddComponent|Undo\.|SetParent|new GameObject|Instantiate|DestroyImmediate|AssetDatabase\.(CreateAsset|SaveAssets|ImportAsset|DeleteAsset|MoveAsset)|SetDirty|runInBackground|isPlaying/;

function navKind(name, input, cat) {
  const s = JSON.stringify(input || {});
  if (cat === 'unity: eval/run_script') return inspectRe.test(s) && !mutateRe.test(s) ? 'asset/scene inspect' : null;
  if (cat === 'unity: scene/asset inspect+edit' && /get_|list|hierarchy|find/.test(name)) return 'asset/scene inspect';
  if (cat === 'read/search: unity yaml') return 'asset/scene inspect';
  if (cat === 'read: .cs' || cat === 'search: grep/glob') return 'code';
  if (cat === 'shell: search/read') {
    if (/\.md\b/.test(s)) return 'docs';
    if (/editor\.log|\.log\b/i.test(s)) return null;
    return 'code';
  }
  return null;
}

let total = 0, navTurns = 0, allTurns = 0, saveOpt = 0, saveReal = 0, episodes = 0, navResTT = 0;
const kinds = {};
for (const f of fs.readdirSync(dir).filter((f) => f.endsWith('.jsonl'))) {
  const seen = new Set(), turns = [], pend = new Map();
  for (const line of fs.readFileSync(path.join(dir, f), 'utf8').split('\n')) {
    if (!line.trim()) continue; let o; try { o = JSON.parse(line); } catch { continue; }
    if (o.isSidechain) continue; const m = o.message; if (!m || !Array.isArray(m.content)) continue;
    if (o.type === 'assistant') {
      if (m.usage && m.id && !seen.has(m.id)) { seen.add(m.id); const u = m.usage; turns.push({ ctx: (u.input_tokens || 0) + (u.cache_read_input_tokens || 0) + (u.cache_creation_input_tokens || 0), calls: [], res: 0 }); }
      const t = turns[turns.length - 1];
      for (const c of m.content) if (c.type === 'tool_use' && t) { const cat = categorize(c.name, c.input); const k = navKind(c.name.replace(/^mcp__.*__/, ''), c.input, cat); t.calls.push(k); pend.set(c.id, { t, k }); }
    } else if (o.type === 'user') {
      for (const c of m.content) if (c.type === 'tool_result' && pend.has(c.tool_use_id)) { const p = pend.get(c.tool_use_id); if (p.k) p.t.res += resultLen(c.content) / 4; }
    }
  }
  allTurns += turns.length; total += turns.reduce((a, t) => a + t.ctx, 0);
  let run = [];
  const flush = () => {
    if (run.length) {
      episodes++;
      const ctxs = run.map((i) => (turns[i + 1] || turns[i]).ctx);   // the turn that consumes each result
      saveOpt += ctxs.slice(1).reduce((a, b) => a + b, 0);            // streak answered in 1 call
      saveReal += ctxs.slice(2).reduce((a, b) => a + b, 0);           // streak answered in 2 calls
      const res = run.reduce((a, i) => a + turns[i].res, 0);
      navResTT += 0.5 * res * (turns.length - run[run.length - 1]);   // graph output half the size, for the rest of the session
    }
    run = [];
  };
  turns.forEach((t, i) => {
    const nav = t.calls.length && t.calls.every((k) => k);
    if (nav) { navTurns++; run.push(i); for (const k of t.calls) kinds[k] = (kinds[k] || 0) + 1; } else flush();
  });
  flush();
}
const pct = (x) => (100 * x / total).toFixed(1) + '%';
console.log(`turns ${allTurns}, pure-navigation turns ${navTurns} (${(100 * navTurns / allTurns).toFixed(0)}%), streaks ${episodes}`);
console.log('navigation calls by kind:', JSON.stringify(kinds));
console.log(`realistic  (each streak -> 2 calls): turns -${pct(saveReal)}, smaller outputs -${pct(navResTT)}, total -${pct(saveReal + navResTT)}`);
console.log(`optimistic (each streak -> 1 call):  turns -${pct(saveOpt)}, smaller outputs -${pct(navResTT)}, total -${pct(saveOpt + navResTT)}`);
