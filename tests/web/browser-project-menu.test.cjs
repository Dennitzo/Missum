const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");
const source = fs.readFileSync(path.resolve(__dirname, "../../src/Missum.App/Assets/Web/app.js"), "utf8");
function harness(confirm = true) {
  const posts = [], errors = [], listeners = new Map(), sessions = [{ id: "one", sessionGroupId: "group" }, { id: "two", sessionGroupId: "group" }, { id: "other", sessionGroupId: "other" }];
  const context = vm.createContext({ state: { sessions }, confirm: () => confirm, Promise, Error, setTimeout, clearTimeout,
    addEventListener: (name, callback) => { if (!listeners.has(name)) listeners.set(name, new Set()); listeners.get(name).add(callback); }, removeEventListener: (name, callback) => listeners.get(name)?.delete(callback),
    post: (type, payload) => { const requestId = `request-${posts.length}`; posts.push({ type, payload, requestId }); return requestId; }, showToast: error => errors.push(error) });
  for (const name of ["acknowledgedCommand", "deleteWorkspaceProject"]) {
    const start = source.indexOf(`  ${name === "deleteWorkspaceProject" ? "async " : ""}function ${name}(`), ending = source.slice(start).match(/\r?\n {2}\}(?:\r?\n|$)/);
    assert.ok(start >= 0 && ending); vm.runInContext(source.slice(start, start + ending.index + ending[0].length), context);
  }
  const emit = (type, requestId, payload = {}) => { for (const callback of [...listeners.get("missum:host-message") || []]) callback({ detail: { type, requestId, payload } }); };
  return { context, posts, errors, sessions, emit, listeners };
}
const flush = () => new Promise(resolve => setImmediate(resolve));
test("project deletion awaits every existing server receipt before deleting the empty group", async () => {
  const host = harness(), completed = host.context.deleteWorkspaceProject({ id: "group", name: "Projekt" });
  assert.equal(host.posts.length, 1); assert.equal(host.posts[0].payload.sessionId, "one");
  host.emit("state.snapshot", "another-client-request"); await flush(); assert.equal(host.posts.length, 1);
  host.emit("state.snapshot", host.posts[0].requestId); await flush(); assert.equal(host.posts[1].payload.sessionId, "two");
  host.emit("state.snapshot", host.posts[1].requestId); await flush(); assert.equal(host.posts[2].type, "session.groupDeleteEmpty");
  assert.equal(host.posts[2].payload.groupId, "group"); host.emit("action.completed", host.posts[2].requestId); await completed;
  assert.equal(host.sessions.length, 3, "the browser never optimistically deletes session data"); assert.equal(host.errors.length, 0);
  assert.equal(host.listeners.get("missum:host-message").size, 0);
});
test("project deletion stops on a server refusal and leaves the group untouched", async () => {
  const host = harness(), completed = host.context.deleteWorkspaceProject({ id: "group", name: "Projekt" });
  host.emit("host.error", host.posts[0].requestId, { message: "Sitzung läuft" }); await completed;
  assert.equal(host.posts.length, 1); assert.deepEqual(host.errors, ["Sitzung läuft"]); assert.equal(host.sessions.length, 3);
});
test("project deletion cancellation and disconnect do not submit further destructive commands", async () => {
  const cancelled = harness(false); await cancelled.context.deleteWorkspaceProject({ id: "group", name: "Projekt" }); assert.equal(cancelled.posts.length, 0);
  const host = harness(), completed = host.context.deleteWorkspaceProject({ id: "group", name: "Projekt" });
  for (const listener of host.listeners.get("missum:bridge-disconnected")) listener(); await completed;
  assert.equal(host.posts.length, 1); assert.match(host.errors[0], /Verbindung unterbrochen/);
});
