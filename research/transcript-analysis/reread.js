// Re-reads of a file already in context and unchanged since (no Edit/Write to it, no compaction in between).
const fs = require('fs'), path = require('path');
const dir = path.join(process.env.USERPROFILE, '.claude', 'projects', 'D--Projects-Unity-purge-squad');
const resultLen = (c) => typeof c === 'string' ? c.length : Array.isArray(c) ? c.reduce((a, x) => a + (x.type === 'text' ? (x.text || '').length : 0), 0) : 0;
let totalCtx = 0, dupTT = 0, dupCount = 0, reads = 0; const byExt = {};
for (const f of fs.readdirSync(dir).filter((f) => f.endsWith('.jsonl'))) {
  const seen = new Set(), turns = [], pend = new Map(), dups = [];
  let lastRead = new Map();                               // key -> turn index of last read
  for (const line of fs.readFileSync(path.join(dir, f), 'utf8').split('\n')) {
    if (!line.trim()) continue; let o; try { o = JSON.parse(line); } catch { continue; }
    if (o.isSidechain) continue; const m = o.message; if (!m || !Array.isArray(m.content)) continue;
    if (o.type === 'assistant') {
      if (m.usage && m.id && !seen.has(m.id)) {
        seen.add(m.id); const u = m.usage; const ctx = (u.input_tokens || 0) + (u.cache_read_input_tokens || 0) + (u.cache_creation_input_tokens || 0);
        if (turns.length && ctx < turns[turns.length - 1] * 0.6) lastRead = new Map();   // compaction: nothing is in context anymore
        turns.push(ctx);
      }
      for (const c of m.content) {
        if (c.type !== 'tool_use') continue; const p = (c.input && c.input.file_path) || '';
        if ((c.name === 'Edit' || c.name === 'Write' || c.name === 'MultiEdit') && p) for (const k of [...lastRead.keys()]) if (k.startsWith(p + '|')) lastRead.delete(k);
        if (c.name === 'Read' && p) {
          reads++; const key = p + '|' + (c.input.offset || 0) + '|' + (c.input.limit || 0);
          if (lastRead.has(key)) pend.set(c.id, { turn: turns.length - 1, ext: path.extname(p) || '(none)' });
          lastRead.set(key, turns.length - 1);
        }
      }
    } else if (o.type === 'user') {
      for (const c of m.content) if (c.type === 'tool_result' && pend.has(c.tool_use_id)) { const d = pend.get(c.tool_use_id); dups.push({ ...d, tok: resultLen(c.content) / 4 }); }
    }
  }
  totalCtx += turns.reduce((a, b) => a + b, 0);
  const resets = []; for (let i = 1; i < turns.length; i++) if (turns[i] < turns[i - 1] * 0.6) resets.push(i);
  for (const d of dups) {
    const end = resets.find((r) => r > d.turn) ?? turns.length; const tt = d.tok * Math.max(0, end - d.turn - 1) + (turns[d.turn + 1] || 0) * 0; // persistence only
    dupTT += tt; dupCount++; byExt[d.ext] = (byExt[d.ext] || 0) + tt;
  }
}
console.log(`Read calls ${reads}, unchanged re-reads ${dupCount} (${(100 * dupCount / reads).toFixed(0)}%)`);
console.log(`their output persisting in context: ${(100 * dupTT / totalCtx).toFixed(1)}% of all context-tokens (+ one turn each)`);
console.log('by extension:', JSON.stringify(Object.entries(byExt).sort((a, b) => b[1] - a[1]).map(([k, v]) => [k, (100 * v / totalCtx).toFixed(2) + '%'])));
