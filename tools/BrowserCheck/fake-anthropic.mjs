// A stand-in for the Anthropic API: streams a canned reply (text first, then one with a drawing), and saves the pictures it was
// sent to the folder given as the first argument, so they can be looked at.
import http from 'node:http';
import fs from 'node:fs';
const outDir = process.argv[2] || '.';
let calls = 0;
const sse = (type, data) => `event: ${type}\ndata: ${JSON.stringify(data)}\n\n`;
http.createServer((req, res) => {
  let body = '';
  req.on('data', c => body += c);
  req.on('end', () => {
    if (req.method !== 'POST' || !req.url.includes('/messages')) { res.writeHead(404).end(); return; }
    calls++;
    try { const j = JSON.parse(body); let n = 0; for (const msg of j.messages) if (Array.isArray(msg.content)) for (const b of msg.content) if (b.type === 'image') { fs.writeFileSync(outDir + '/sent-' + calls + '-' + (++n) + '.png', Buffer.from(b.source.data, 'base64')); } } catch (e) { console.log('parse', e.message); }
    const svg = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 400 200"><title>A house</title><path d="M50 100 L200 20 L350 100 V180 H50 Z" stroke="currentColor" fill="none" stroke-width="3" stroke-linejoin="round"/><text x="150" y="150" font-family="Ink Free, sans-serif" font-size="20" fill="currentColor">home</text></svg>';
    const text = calls === 1
      ? 'HEARD: hello notebook\n---\nHi there! This is Claude answering in the notebook, on the ruled lines, in blue handwriting.'
      : 'HEARD: [a scribble] and a question\n---\nHere is a quick sketch of what you mean.\n' + svg;
    res.writeHead(200, { 'content-type': 'text/event-stream' });
    res.write(sse('message_start', { type: 'message_start', message: { id: 'msg_' + calls, type: 'message', role: 'assistant', model: 'claude-haiku-5-5', content: [], stop_reason: null, stop_sequence: null, usage: { input_tokens: 10, output_tokens: 1 } } }));
    res.write(sse('content_block_start', { type: 'content_block_start', index: 0, content_block: { type: 'text', text: '' } }));
    let i = 0;
    const timer = setInterval(() => {
      if (i < text.length) {
        res.write(sse('content_block_delta', { type: 'content_block_delta', index: 0, delta: { type: 'text_delta', text: text.slice(i, i + 9) } }));
        i += 9;
      } else {
        clearInterval(timer);
        res.write(sse('content_block_stop', { type: 'content_block_stop', index: 0 }));
        res.write(sse('message_delta', { type: 'message_delta', delta: { stop_reason: 'end_turn', stop_sequence: null }, usage: { output_tokens: 30 } }));
        res.write(sse('message_stop', { type: 'message_stop' }));
        res.end();
      }
    }, 30);
    console.log('request', calls, 'bytes', body.length, 'has image', body.includes('"image"'));
  });
}).listen(18080, '127.0.0.1', () => console.log('fake api on 18080'));

// A second stand-in on 18081: the MCP registry (one server) and an MCP server that asks for no sign-in.
http.createServer((req, res) => {
  let body = '';
  req.on('data', c => body += c);
  req.on('end', () => {
    if (req.url.startsWith('/v0/servers')) {
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ servers: [
        { server: { name: 'io.github.acme/todo-list', title: 'Todo List', description: 'Keeps your tasks', remotes: [{ type: 'streamable-http', url: 'https://mcp.acme.example/mcp' }] }, _meta: { 'io.modelcontextprotocol.registry/official': { status: 'active' } } },
        { server: { name: 'com.example/local-only', description: 'Runs on your PC', packages: [{ registryType: 'npm', identifier: 'x', transport: { type: 'stdio' } }] } },
      ], metadata: { count: 2 } }));
    } else if (req.url === '/open') {
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end('{}');
    } else {
      res.writeHead(404).end();
    }
  });
}).listen(18081, '127.0.0.1');
