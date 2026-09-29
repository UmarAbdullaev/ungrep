// Context cost of each tool call = context size of the turn that consumes its result.
const fs = require('fs'), path = require('path');
const dir = path.join(process.env.USERPROFILE, '.claude', 'projects', 'D--Projects-Unity-purge-squad');
const src = fs.readFileSync(path.join(__dirname, 'usage.js'), 'utf8');
const mcpNames = {}, cliCmds = {};
const categorize = eval('(' + src.slice(src.indexOf('function categorize'), src.indexOf('const resultLen')).trim() + ')');
const cost = {}, evalKinds = {}, loops = { editCompile: 0, compileTurns: 0 };
let grand = 0;
const kinds = [
  ['play/runtime state', /runInBackground|isPlaying|EditorApplication\.(isPlaying|EnterPlaymode)|Time\.timeScale|FindObjectsBy|FindObjectOfType|FindAnyObject/],
  ['build/modify scene or prefab', /SaveAsPrefabAsset|PrefabUtility|EditorSceneManager\.(Save|Open|MarkSceneDirty)|AddComponent|Undo\.|SetParent|new GameObject|Instantiate|DestroyImmediate/],
  ['asset create/edit', /AssetDatabase\.(CreateAsset|SaveAssets|ImportAsset|Refresh|DeleteAsset|MoveAsset)|EditorUtility\.SetDirty|SerializedObject/],
  ['inspect (read-only)', /GetComponent|\.transform\.|position|localScale|Debug\.Log|return |ToString\(|string\.Join|\$"/],
];
for (const f of fs.readdirSync(dir).filter((f) => f.endsWith('.jsonl'))) {
  const seen = new Set(); let ctx = 0; const pending = [];
  let lastWasCs = false, inCompile = 0;
  for (const line of fs.readFileSync(path.join(dir, f), 'utf8').split('\n')) {
    if (!line.trim()) continue; let o; try { o = JSON.parse(line); } catch { continue; }
    if (o.isSidechain) continue; const m = o.message; if (!m || !Array.isArray(m.content)) continue;
    if (o.type !== 'assistant') continue;
    if (m.usage && m.id && !seen.has(m.id)) {
      seen.add(m.id); const u = m.usage;
      ctx = (u.input_tokens || 0) + (u.cache_read_input_tokens || 0) + (u.cache_creation_input_tokens || 0);
      // charge this turn's context to the calls whose results it consumed
      if (pending.length) { const share = ctx / pending.length; for (const c of pending) { cost[c] = (cost[c] || 0) + share; grand += share; } pending.length = 0; }
    }
    for (const c of m.content) {
      if (c.type !== 'tool_use') continue;
      const cat = categorize(c.name, c.input); pending.push(cat);
      const short = c.name.replace(/^mcp__.*__/, '');
      if (short === 'eval' || short === 'eval_file' || short === 'run_script') {
        const code = JSON.stringify(c.input || {});
        const k = (kinds.find(([, re]) => re.test(code)) || ['other'])[0];
        evalKinds[k] = (evalKinds[k] || 0) + 1;
      }
      if (cat === 'write: .cs') lastWasCs = true;
      if (/compile|console/.test(cat) || /wait_for|editor_status/.test(short)) { loops.compileTurns++; if (lastWasCs) { loops.editCompile++; lastWasCs = false; } }
    }
  }
}
const rows = Object.entries(cost).sort((a, b) => b[1] - a[1]);
console.log(`context-tokens re-read by turns that consumed tool results: ${Math.round(grand / 1e6)}M (of 754M total)\n`);
for (const [k, v] of rows.slice(0, 18)) console.log(k.padEnd(36), (Math.round(v / 1e5) / 10 + 'M').padStart(8), (100 * v / 754e6).toFixed(1).padStart(6) + '% of all');
console.log('\neval/run_script by purpose:', JSON.stringify(Object.entries(evalKinds).sort((a, b) => b[1] - a[1])));
console.log('compile/console/wait/status calls:', loops.compileTurns, '| edit->compile cycles:', loops.editCompile);
