// Aggregates this project's Claude Code transcripts: tool calls and tool-result size by category.
const fs = require('fs');
const path = require('path');
const dir = path.join(process.env.USERPROFILE, '.claude', 'projects', 'D--Projects-Unity-purge-squad');

const cats = {};
const bump = (cat, chars, n = 1) => {
  const c = (cats[cat] ??= { calls: 0, chars: 0 });
  c.calls += n; c.chars += chars;
};
const usage = { input: 0, cacheWrite: 0, cacheRead: 0, output: 0, turns: 0 };
const bigReads = [];
const mcpNames = {};
const cliCmds = {};
let sessions = 0;

function categorize(name, input) {
  const s = JSON.stringify(input || {}).toLowerCase();
  const file = (input && (input.file_path || input.path || '')) + '';
  const isYaml = /\.(unity|prefab|asset|mat|anim|controller|overridecontroller|meta|inputactions)\b/i;
  if (name.startsWith('mcp__')) {
    const short = name.replace(/^mcp__[^_]+(?:_[^_]+)*?__/, '');
    const isUnity = /unity|pipeline/i.test(name);
    if (isUnity) {
      mcpNames[short] = (mcpNames[short] || 0) + 1;
      if (/console|log/.test(short)) return 'unity: console/logs';
      if (/test/.test(short)) return 'unity: tests';
      if (/compile|refresh|status|reimport|domain/.test(short)) return 'unity: compile/refresh/status';
      if (/play|screenshot|capture|pause|stop/.test(short)) return 'unity: play/screenshot';
      if (/eval|execute|run_script|script/.test(short)) return 'unity: eval/run_script';
      if (/scene|gameobject|component|prefab|hierarchy|asset|find|inspect|get_/.test(short)) return 'unity: scene/asset inspect+edit';
      return 'unity: other mcp';
    }
    if (/browser|chrome/i.test(name)) return 'browser';
    return 'other mcp';
  }
  if (name === 'Bash' || name === 'PowerShell') {
    const cmd = (input && input.command) || '';
    const m = cmd.match(/unity(?:\.exe)?\s+command\b[^\n]*?\s(?:--project-path\s+"[^"]+"\s+)?([a-z_]+)/i);
    if (/unity(\.exe)?\s+command/i.test(cmd)) {
      const k = m ? m[1] : '?';
      cliCmds[k] = (cliCmds[k] || 0) + 1;
      if (/console|log/.test(k)) return 'unity: console/logs';
      if (/test/.test(k)) return 'unity: tests';
      if (/compile|refresh|status|reimport/.test(k)) return 'unity: compile/refresh/status';
      if (/play|screenshot/.test(k)) return 'unity: play/screenshot';
      if (/eval|run_script|execute/.test(k)) return 'unity: eval/run_script';
      return 'unity: other cli';
    }
    if (/editor\.log/i.test(cmd)) return 'unity: console/logs';
    if (/\bgit\b/.test(cmd)) return 'shell: git';
    if (/\b(grep|rg|find|ls|cat|head|tail|sed|wc|Select-String|Get-ChildItem|Get-Content)\b/i.test(cmd)) {
      return isYaml.test(cmd) ? 'read/search: unity yaml' : 'shell: search/read';
    }
    if (/\b(sleep|Start-Sleep|timeout)\b/i.test(cmd)) return 'shell: waiting';
    return 'shell: other';
  }
  if (name === 'Read') {
    if (isYaml.test(file)) return 'read/search: unity yaml';
    if (/\.cs$/i.test(file)) return 'read: .cs';
    if (/\.md$/i.test(file)) return 'read: .md (plan/map/docs)';
    if (/\.(png|jpg|jpeg)$/i.test(file)) return 'read: image';
    return 'read: other';
  }
  if (name === 'Grep' || name === 'Glob') {
    return isYaml.test(s) ? 'read/search: unity yaml' : 'search: grep/glob';
  }
  if (name === 'Edit' || name === 'Write' || name === 'MultiEdit') {
    if (/\.cs$/i.test(file)) return 'write: .cs';
    if (/\.md$/i.test(file)) return 'write: .md';
    return 'write: other';
  }
  if (name === 'Agent' || name === 'Task') return 'subagent';
  if (name === 'ToolSearch') return 'toolsearch';
  return 'other: ' + name;
}

const resultLen = (content) => {
  if (typeof content === 'string') return content.length;
  if (Array.isArray(content)) return content.reduce((a, c) => a + (c.type === 'text' ? (c.text || '').length : c.type === 'image' ? 6000 : 0), 0);
  return 0;
};

for (const f of fs.readdirSync(dir).filter((f) => f.endsWith('.jsonl'))) {
  sessions++;
  const pending = new Map();
  const seenMsg = new Set();
  for (const line of fs.readFileSync(path.join(dir, f), 'utf8').split('\n')) {
    if (!line.trim()) continue;
    let o; try { o = JSON.parse(line); } catch { continue; }
    if (o.isSidechain) continue;
    const msg = o.message;
    if (!msg || !Array.isArray(msg.content)) continue;
    if (o.type === 'assistant') {
      if (msg.usage && msg.id && !seenMsg.has(msg.id)) {
        seenMsg.add(msg.id);
        usage.input += msg.usage.input_tokens || 0;
        usage.cacheWrite += msg.usage.cache_creation_input_tokens || 0;
        usage.cacheRead += msg.usage.cache_read_input_tokens || 0;
        usage.output += msg.usage.output_tokens || 0;
        usage.turns++;
      }
      for (const c of msg.content) if (c.type === 'tool_use') pending.set(c.id, { cat: categorize(c.name, c.input), name: c.name, input: c.input });
    } else if (o.type === 'user') {
      for (const c of msg.content) {
        if (c.type !== 'tool_result') continue;
        const p = pending.get(c.tool_use_id);
        if (!p) continue;
        const len = resultLen(c.content);
        bump(p.cat, len);
        if (len > 40000) bigReads.push({ len, cat: p.cat, what: (p.input && (p.input.file_path || p.input.command || p.input.pattern || '')).toString().slice(0, 110) });
      }
    }
  }
}

const total = Object.values(cats).reduce((a, c) => ({ calls: a.calls + c.calls, chars: a.chars + c.chars }), { calls: 0, chars: 0 });
console.log(`sessions ${sessions}, model turns ${usage.turns}`);
console.log(`tokens: output ${usage.output}, fresh input ${usage.input}, cache write ${usage.cacheWrite}, cache read ${usage.cacheRead}`);
console.log(`tool calls ${total.calls}, tool result ~${Math.round(total.chars / 4)} tokens\n`);
console.log('category'.padEnd(36), 'calls', '  %calls', ' result~tok', ' %result');
for (const [k, v] of Object.entries(cats).sort((a, b) => b[1].chars - a[1].chars)) {
  console.log(k.padEnd(36), String(v.calls).padStart(5), (100 * v.calls / total.calls).toFixed(1).padStart(7) + '%', String(Math.round(v.chars / 4)).padStart(11), (100 * v.chars / total.chars).toFixed(1).padStart(7) + '%');
}
console.log('\nunity mcp tools:', JSON.stringify(Object.entries(mcpNames).sort((a, b) => b[1] - a[1]).slice(0, 25)));
console.log('unity cli cmds:', JSON.stringify(Object.entries(cliCmds).sort((a, b) => b[1] - a[1]).slice(0, 25)));
console.log('\nbiggest single tool results (>40k chars):');
for (const b of bigReads.sort((a, b) => b.len - a.len).slice(0, 15)) console.log(String(Math.round(b.len / 4)).padStart(7), 'tok', b.cat.padEnd(30), b.what);
