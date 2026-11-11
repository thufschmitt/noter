// A stand-in for ChatGPT's Codex backend (the Responses API, streamed): a canned reply (text first, then one with a table and a
// drawing), and saves the pictures it was sent to the folder given as the first argument, so they can be looked at.
import http from 'node:http';
import fs from 'node:fs';
const outDir = process.argv[2] || '.';
let calls = 0;
const sse = data => `data: ${JSON.stringify(data)}\n\n`;
http.createServer((req, res) => {
  let body = '';
  req.on('data', c => body += c);
  req.on('end', () => {
    if (req.method !== 'POST' || !req.url.endsWith('/responses')) { res.writeHead(404).end(); return; }
    calls++;
    try {
      const j = JSON.parse(body);
      let n = 0;
      for (const item of j.input) if (Array.isArray(item.content)) for (const c of item.content)
        if (c.type === 'input_image') fs.writeFileSync(outDir + '/sent-' + calls + '-' + (++n) + '.png', Buffer.from(c.image_url.split(',')[1], 'base64'));
    } catch (e) { console.log('parse', e.message); }
    const svg = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 400 200"><title>A house</title><path d="M50 100 L200 20 L350 100 V180 H50 Z" stroke="currentColor" fill="none" stroke-width="3" stroke-linejoin="round"/><text x="150" y="150" font-family="Ink Free, sans-serif" font-size="20" fill="currentColor">home</text></svg>';
    const text = calls === 1
      ? 'HEARD: hello notebook\n---\nHi there! This is the model answering in the notebook, on the ruled lines, in blue handwriting.'
      : 'HEARD: [a scribble] and a question\n---\nHere is your week, with a sketch.\n<figure aria-label="Your week"><table><tr><th>Day</th><th>Plan</th></tr><tr><td>Mon</td><td>gym</td></tr></table>' + svg + '<script>parent.hacked = true</script></figure>';
    res.writeHead(200, { 'content-type': 'text/event-stream' });
    res.write(sse({ type: 'response.created', response: { id: 'resp_' + calls } }));
    res.write(sse({ type: 'response.output_item.added', item: { type: 'message', id: 'msg_' + calls, role: 'assistant', content: [] } }));
    let i = 0;
    const timer = setInterval(() => {
      if (i < text.length) {
        res.write(sse({ type: 'response.output_text.delta', delta: text.slice(i, i + 9) }));
        i += 9;
      } else {
        clearInterval(timer);
        res.write(sse({ type: 'response.output_item.done', item: { type: 'message', id: 'msg_' + calls, role: 'assistant', content: [{ type: 'output_text', text }] } }));
        res.write(sse({ type: 'response.completed', response: { id: 'resp_' + calls, usage: { input_tokens: 10, output_tokens: 30 } } }));
        res.end();
      }
    }, 30);
    console.log('request', calls, 'bytes', body.length, 'has image', body.includes('"input_image"'));
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
