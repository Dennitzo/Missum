// Development-only source asset server, with real HTTP/WebSocket forwarding.
// Preserving the browser's Host and Origin allows normal same-origin checks.
const http = require("node:http"), fs = require("node:fs/promises"), path = require("node:path");
const assets = path.resolve("src/Missum.App/Assets/Web"), upstreamPort = 8090;
const upstreamPath = value => value.replace(/^\/assistant(?=\/|\?)/, "");
const types = { ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".css": "text/css; charset=utf-8", ".svg": "image/svg+xml", ".ttf": "font/ttf", ".woff": "font/woff", ".woff2": "font/woff2", ".png": "image/png" };
const server = http.createServer(async (request, response) => {
  const url = new URL(request.url, "http://localhost");
  const relative = decodeURIComponent(url.pathname.replace(/^\/assistant\/?/, "")) || "index.html";
  const source = path.resolve(assets, relative), type = types[path.extname(source)];
  if (request.method === "GET" && url.pathname.startsWith("/assistant/") && source.startsWith(`${assets}${path.sep}`) && type) {
    try { const body = await fs.readFile(source); response.writeHead(200, { "Content-Type": type, "Cache-Control": "no-store" }); return response.end(body); } catch { /* Generated artifacts belong to Missum. */ }
  }
  const proxy = http.request({ host: "127.0.0.1", port: upstreamPort, method: request.method, path: upstreamPath(request.url), headers: request.headers }, incoming => {
    response.writeHead(incoming.statusCode, incoming.headers); incoming.pipe(response);
  });
  proxy.on("error", () => { if (!response.headersSent) response.writeHead(502); response.end("Missum host unavailable"); }); request.pipe(proxy);
});
server.on("upgrade", (request, socket, head) => {
  const proxy = http.request({ host: "127.0.0.1", port: upstreamPort, path: upstreamPath(request.url), headers: request.headers });
  proxy.on("upgrade", (response, upstream, upstreamHead) => {
    socket.write(`HTTP/1.1 ${response.statusCode} ${response.statusMessage}\r\n${Object.entries(response.headers).map(([key, value]) => `${key}: ${value}`).join("\r\n")}\r\n\r\n`);
    if (head.length) upstream.write(head); if (upstreamHead.length) socket.write(upstreamHead);
    socket.on("error", () => upstream.destroy()); upstream.on("error", () => socket.destroy()); socket.pipe(upstream); upstream.pipe(socket);
  });
  proxy.on("error", () => socket.destroy()); proxy.end();
});
server.listen(8094, "192.168.0.67", () => console.log("Source QA http://192.168.0.67:8094/assistant/"));
