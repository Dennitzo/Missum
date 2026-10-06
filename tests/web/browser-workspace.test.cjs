const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const { TestNode } = require("./test-dom.cjs");
const source = fs.readFileSync(require.resolve("../../src/Missum.App/Assets/Web/browser-workspace.js"), "utf8");
const css = fs.readFileSync(require.resolve("../../src/Missum.App/Assets/Web/browser-workspace.css"), "utf8");
const roots = [{ name: "C:\\ · Windows", path: "C:\\" }, { name: "D:\\ · Projekte", path: "D:\\" }];
function listing(path = null, directories = [], extra = {}) {
  return { serverName: "MISSUM-PC", path, parentPath: path && !/^[A-Z]:\\$/i.test(path) ? "C:\\" : null,
    breadcrumbs: path ? [{ name: "C:\\", path: "C:\\" }, { name: path.split("\\").at(-1) || path, path }] : [],
    roots, directories, offset: 0, totalDirectories: directories.length, hasMore: false, ...extra };
}
function directory(name, base = "C:\\Projects") { return { name, path: `${base}\\${name}` }; }
function harness() {
  const events = new Map(), timers = new Map(), calls = []; let timerId = 0, requestId = 0, document;
  function create(tag) {
    const element = new TestNode(tag); element.focus = () => { document.activeElement = element; };
    element.click = () => element.dispatch("click"); return element;
  }
  const body = create("body");
  document = { body, activeElement: null, createElement: create, createElementNS: (_, tag) => create(tag),
    getElementById: id => [body, ...body.querySelectorAll("*")].find(element => element.id === id) || null };
  const trigger = create("button"); trigger.id = "original-project-button"; body.append(trigger); trigger.focus();
  const legacy = create("div"); legacy.id = "workspace-overlay"; legacy.hidden = true; legacy.append(create("input")); body.append(legacy);
  const bridge = { newRequestId: () => `request-${++requestId}`, post: (type, payload, id) => { calls.push({ type, payload, requestId: id }); return id; } };
  const context = { document, missumBridge: bridge,
    addEventListener: (type, callback) => { if (!events.has(type)) events.set(type, []); events.get(type).push(callback); },
    setTimeout: (callback, delay) => { const id = ++timerId; timers.set(id, { callback, delay }); return id; },
    clearTimeout: id => timers.delete(id) };
  vm.runInNewContext(source, context);
  const byId = id => document.getElementById(id);
  const emit = (type, detail) => { for (const callback of events.get(type) || []) callback({ detail }); };
  const respond = (payload, id = calls.at(-1)?.requestId) => emit("missum:host-message", { type: "workspace.list", requestId: id, payload });
  const error = (text, id = calls.at(-1)?.requestId) => emit("missum:host-message", { type: "host.error", requestId: id, payload: { message: text } });
  const fire = delay => { for (const [id, timer] of [...timers]) if (timer.delay === delay) { timers.delete(id); timer.callback(); } };
  const button = label => byId("server-workspace-overlay").querySelectorAll("button").find(element => element.textContent === label || element.getAttribute("aria-label") === label);
  const folders = () => byId("server-workspace-list").querySelectorAll(".server-workspace-folder");
  const input = async (id, value) => { const element = byId(id); element.value = value; await element.dispatch("input"); };
  const key = (element, key, extra = {}) => {
    let prevented = false, stopped = false;
    const event = { key, target: element, preventDefault() { prevented = true; }, stopPropagation() { stopped = true; }, ...extra };
    for (const listener of element.listeners.get("keydown") || []) listener(event);
    const dialog = byId("server-workspace-overlay");
    if (element !== dialog && !stopped) for (const listener of dialog.listeners.get("keydown") || []) listener(event);
    return prevented;
  };
  const open = options => context.missumWorkspace.open(options);
  const cancel = () => key(byId("server-workspace-overlay"), "Escape");
  return { context, document, trigger, legacy, bridge, events, timers, calls, byId, respond, error, fire, button, folders, input, key, open, cancel };
}

test("Computer starts with real server drives, disables selection and preserves the old overlay untouched", async () => {
  const h = harness(); const selected = h.open();
  assert.equal(h.byId("server-workspace-overlay").open, true);
  assert.equal(h.document.activeElement, h.byId("server-workspace-path"));
  assert.deepEqual(h.calls.map(call => [call.type, call.payload.path, call.payload.filter, call.payload.offset]), [["workspace.browse", null, "", 0]]);
  h.respond(listing());
  assert.deepEqual(h.folders().map(row => row.getAttribute("aria-label")), roots.map(root => root.name));
  assert.equal(h.button("Ordner auswählen").disabled, true);
  assert.equal(h.byId("server-workspace-server").textContent, "Server: MISSUM-PC");
  assert.equal(h.byId("server-workspace-search").disabled, true);
  assert.equal(h.legacy.hidden, true); assert.equal(h.legacy.children.length, 1);
  h.cancel(); assert.equal(await selected, null); assert.equal(h.document.activeElement, h.trigger);
});

test("initial workspace wins over deduplicated recent projects and selecting returns only the server-validated canonical path", async () => {
  const h = harness(); const selected = h.open({ initialPath: "  C:\\Projects\\Missum  ", recentPaths: ["D:\\Other", "d:\\other\\", { workspacePath: "C:\\Projects", name: "Meine Projekte" }] });
  assert.equal(h.calls[0].payload.path, "C:\\Projects\\Missum");
  h.respond(listing("C:\\Projects\\Missum"));
  assert.equal(h.button("Ordner auswählen").disabled, false);
  assert.equal(h.byId("server-workspace-overlay").querySelectorAll(".server-workspace-recent").length, 2);
  await h.button("Ordner auswählen").click();
  assert.equal(await selected, "C:\\Projects\\Missum");
  assert.equal(h.byId("server-workspace-overlay").open, false);
  assert.equal(h.document.activeElement, h.trigger);
  assert.equal(h.calls.length, 1, "picker only browses; project mutation belongs to the invoking mode-aware callback");
});

test("recent project supplies the initial folder when there is no active workspace", async () => {
  const h = harness(); const selected = h.open({ recentPaths: [{ path: "D:\\Research", name: "Forschung" }] });
  assert.equal(h.calls[0].payload.path, "D:\\Research");
  h.respond(listing("D:\\Research"));
  assert.equal(h.byId("server-workspace-overlay").querySelector(".server-workspace-recent").textContent, "Forschung");
  h.cancel(); assert.equal(await selected, null);
});

test("single-click child selection validates the selected child and returns that child rather than its parent", async () => {
  const h = harness(); const selected = h.open({ initialPath: "C:\\Projects" });
  h.respond(listing("C:\\Projects", [directory("Missum")]));
  await h.folders()[0].click();
  assert.equal(h.button("Ordner auswählen").textContent, "„Missum“ auswählen");
  assert.match(h.byId("server-workspace-overlay").querySelector(".server-workspace-summary").textContent, /Projects\\Missum/);
  assert.equal(h.calls.length, 1);
  await h.button("Ordner auswählen").click();
  assert.equal(h.calls.at(-1).payload.path, "C:\\Projects\\Missum");
  assert.equal(h.byId("server-workspace-overlay").open, true);
  assert.equal(h.button("Ordner auswählen").disabled, true);
  h.respond(listing("C:\\Projects\\Missum"));
  assert.equal(await selected, "C:\\Projects\\Missum");
  assert.equal(h.byId("server-workspace-overlay").open, false);
});

test("inaccessible selected child stays in the dialog with its path and cannot accidentally resolve the parent", async () => {
  const h = harness(); const selected = h.open({ initialPath: "C:\\Projects" }); let result = "pending";
  selected.then(value => { result = value; });
  h.respond(listing("C:\\Projects", [directory("Restricted")]));
  await h.folders()[0].click(); await h.button("Ordner auswählen").click();
  h.error("Zugriff auf den ausgewählten Ordner wurde verweigert."); await Promise.resolve();
  assert.equal(result, "pending");
  assert.equal(h.byId("server-workspace-overlay").open, true);
  assert.equal(h.byId("server-workspace-path").value, "C:\\Projects\\Restricted");
  assert.equal(h.button("Ordner auswählen").disabled, true);
  h.cancel(); assert.equal(await selected, null);
});

test("path edits during child validation prevent automatic selection even when an edit restores the same text", async () => {
  const h = harness(); const selected = h.open({ initialPath: "C:\\Projects" }); let result = "pending";
  selected.then(value => { result = value; });
  h.respond(listing("C:\\Projects", [directory("Missum")]));
  await h.folders()[0].click(); await h.button("Ordner auswählen").click(); const childRequest = h.calls.at(-1).requestId;
  await h.input("server-workspace-path", "D:\\New choice");
  h.respond(listing("C:\\Projects\\Missum"), childRequest); await Promise.resolve();
  assert.equal(result, "pending"); assert.equal(h.byId("server-workspace-path").value, "D:\\New choice");
  assert.equal(h.button("Ordner auswählen").disabled, true);
  await h.button("Öffnen").click(); h.respond(listing("D:\\New choice", [directory("Child", "D:\\New choice")]));
  await h.folders()[0].click(); await h.button("Ordner auswählen").click();
  await h.input("server-workspace-path", "D:\\Temporarily edited"); await h.input("server-workspace-path", "D:\\New choice\\Child");
  h.respond(listing("D:\\New choice\\Child")); await Promise.resolve();
  assert.equal(result, "pending"); assert.equal(h.byId("server-workspace-overlay").open, true);
  await h.button("Ordner auswählen").click(); assert.equal(await selected, "D:\\New choice\\Child");
});

test("a different-path reply or disconnect during selected-child validation never accepts an unintended folder", async () => {
  const h = harness(); const selected = h.open({ initialPath: "C:\\Projects" }); let result = "pending";
  selected.then(value => { result = value; });
  h.respond(listing("C:\\Projects", [directory("Missum")]));
  await h.folders()[0].click(); await h.button("Ordner auswählen").click();
  h.respond(listing("C:\\Projects")); await Promise.resolve();
  assert.equal(result, "pending"); assert.equal(h.button("Ordner auswählen").disabled, true);
  assert.equal(h.byId("server-workspace-path").value, "C:\\Projects\\Missum");
  assert.match(h.byId("server-workspace-status").textContent, /anderen Ordner/);
  await h.input("server-workspace-path", "C:\\Projects"); await h.button("Öffnen").click();
  h.respond(listing("C:\\Projects", [directory("Missum")]));
  await h.folders()[0].click(); await h.button("Ordner auswählen").click(); const childRequest = h.calls.at(-1).requestId;
  for (const callback of h.events.get("missum:bridge-disconnected")) callback();
  h.respond(listing("C:\\Projects\\Missum"), childRequest); await Promise.resolve();
  assert.equal(result, "pending"); assert.equal(h.byId("server-workspace-path").value, "C:\\Projects\\Missum");
  h.cancel(); assert.equal(await selected, null);
});

test("single click highlights a directory while double click or Enter navigates, and Up and breadcrumbs browse the server", async () => {
  const h = harness(); const selected = h.open({ initialPath: "C:\\Projects" });
  h.respond(listing("C:\\Projects", [directory("Missum"), directory("Science")]));
  const first = h.folders()[0]; await first.click();
  assert.equal(first.classList.contains("selected"), true); assert.equal(h.calls.length, 1);
  first.focus();
  await first.dispatch("dblclick"); assert.equal(h.calls.at(-1).payload.path, "C:\\Projects\\Missum");
  assert.equal(h.button("Ordner auswählen").disabled, true);
  h.respond(listing("C:\\Projects\\Missum", [], { parentPath: "C:\\Projects" }));
  assert.equal(h.document.activeElement, h.byId("server-workspace-path"), "navigation keeps keyboard focus in the modal when the new folder is empty");
  await h.button("Übergeordneter Ordner").click(); assert.equal(h.calls.at(-1).payload.path, "C:\\Projects");
  h.respond(listing("C:\\Projects", [directory("Science")]));
  h.folders()[0].focus(); assert.equal(h.key(h.folders()[0], "Enter"), true);
  assert.equal(h.calls.at(-1).payload.path, "C:\\Projects\\Science");
  h.respond(listing("C:\\Projects\\Science"));
  const root = h.byId("server-workspace-overlay").querySelectorAll(".server-workspace-crumb")[1];
  await root.click(); assert.equal(h.calls.at(-1).payload.path, "C:\\");
  h.respond(listing("C:\\", [], { parentPath: null })); await h.button("Übergeordneter Ordner").click();
  assert.equal(h.calls.at(-1).payload.path, null);
  h.cancel(); await selected;
});

test("absolute path input uses Go or Enter and local edits survive pending success, failures and an unrelated host error", async () => {
  const h = harness(); const selected = h.open({ initialPath: "C:\\Projects" }); const originalId = h.calls[0].requestId;
  await h.input("server-workspace-path", "D:\\My unsaved path");
  h.respond(listing("C:\\Projects"), originalId);
  assert.equal(h.byId("server-workspace-path").value, "D:\\My unsaved path");
  assert.equal(h.button("Ordner auswählen").disabled, true);
  h.error("Unrelated", "different-request"); assert.doesNotMatch(h.byId("server-workspace-status").textContent, /Unrelated/);
  h.key(h.byId("server-workspace-path"), "Enter"); assert.equal(h.calls.at(-1).payload.path, "D:\\My unsaved path");
  h.error("Zugriff verweigert."); assert.equal(h.byId("server-workspace-path").value, "D:\\My unsaved path");
  assert.equal(h.byId("server-workspace-status").getAttribute("role"), "alert");
  await h.input("server-workspace-path", "D:\\Existing"); await h.button("Öffnen").click();
  h.respond(listing("D:\\Existing")); assert.equal(h.button("Ordner auswählen").disabled, false);
  h.cancel(); await selected;
});

test("request IDs reject out-of-order browse results and errors after a newer path or a closed dialog", async () => {
  const h = harness(); const selected = h.open({ initialPath: "C:\\First" }); const first = h.calls.at(-1).requestId;
  await h.input("server-workspace-path", "D:\\Second"); await h.button("Öffnen").click(); const second = h.calls.at(-1).requestId;
  h.respond(listing("C:\\First", [directory("stale")]), first); h.error("Old access denied", first);
  assert.equal(h.folders().length, 0); assert.match(h.byId("server-workspace-status").textContent, /geladen/);
  h.respond(listing("D:\\Second", [directory("current", "D:\\Second")]), second);
  assert.equal(h.folders()[0].textContent, "current");
  h.cancel(); await selected; h.respond(listing("C:\\First"), first);
  assert.equal(h.byId("server-workspace-overlay").open, false);
  const reopened = h.open({ initialPath: "C:\\Third" }); h.respond(listing("D:\\Second"), second);
  assert.equal(h.byId("server-workspace-path").value, "C:\\Third");
  assert.equal(h.button("Ordner auswählen").disabled, true);
  assert.equal(h.events.get("missum:host-message").length, 1);
  h.cancel(); await reopened;
});

test("server filtering starts at offset zero, debounces typing, invalidates older pages and preserves manual path edits", async () => {
  const h = harness(); const selected = h.open({ initialPath: "C:\\Projects" });
  h.respond(listing("C:\\Projects", [directory("Alpha")], { hasMore: true, totalDirectories: 250 }));
  await h.input("server-workspace-search", "s"); await h.input("server-workspace-search", "science");
  assert.equal(h.calls.length, 1); assert.equal(h.button("Ordner auswählen").disabled, true);
  assert.equal(h.button("Weitere Ordner laden (1 von 250)").disabled, true);
  await h.input("server-workspace-path", "D:\\Local draft"); h.fire(250);
  assert.deepEqual([h.calls.at(-1).payload.path, h.calls.at(-1).payload.filter, h.calls.at(-1).payload.offset], ["C:\\Projects", "science", 0]);
  const firstFilter = h.calls.at(-1).requestId;
  await h.input("server-workspace-search", "missum"); h.fire(250); const secondFilter = h.calls.at(-1).requestId;
  h.respond(listing("C:\\Projects", [directory("Science")]), firstFilter);
  assert.equal(h.folders()[0].textContent, "Alpha");
  h.respond(listing("C:\\Projects", [directory("Missum")]), secondFilter);
  assert.equal(h.folders()[0].textContent, "Missum");
  assert.equal(h.byId("server-workspace-path").value, "D:\\Local draft");
  assert.equal(h.button("Ordner auswählen").disabled, true);
  h.cancel(); await selected;
});

test("Load more requests the next server offset with the existing query, appends without duplicates and leaves typed paths intact", async () => {
  const h = harness(); const selected = h.open({ initialPath: "C:\\Projects" });
  const page = Array.from({ length: 200 }, (_, index) => directory(`Folder-${String(index).padStart(3, "0")}`));
  h.respond(listing("C:\\Projects", page, { totalDirectories: 235, hasMore: true }));
  assert.equal(h.folders().length, 200);
  await h.input("server-workspace-path", "D:\\Preserved");
  await h.button("Weitere Ordner laden (200 von 235)").click();
  assert.equal(h.calls.at(-1).payload.offset, 200); assert.equal(h.calls.at(-1).payload.filter, "");
  const remaining = Array.from({ length: 35 }, (_, index) => directory(`Folder-${index + 200}`));
  h.respond(listing("C:\\Projects", remaining, { offset: 200, totalDirectories: 235 }));
  assert.equal(h.folders().length, 235); assert.equal(h.byId("server-workspace-path").value, "D:\\Preserved");
  assert.equal(h.button("Weitere Ordner laden").hidden, true);
  h.cancel(); await selected;
});

test("timeout rejects a late answer and preserves the draft until an explicit retry validates the folder", async () => {
  const h = harness(); const selected = h.open({ initialPath: "C:\\Slow" }); const stale = h.calls[0].requestId;
  await h.input("server-workspace-path", "D:\\Retry this"); h.fire(30000);
  assert.match(h.byId("server-workspace-status").textContent, /nicht rechtzeitig/);
  h.respond(listing("C:\\Slow"), stale); assert.equal(h.button("Ordner auswählen").disabled, true);
  assert.equal(h.byId("server-workspace-path").value, "D:\\Retry this");
  await h.button("Öffnen").click(); h.respond(listing("D:\\Retry this"));
  await h.button("Ordner auswählen").click(); assert.equal(await selected, "D:\\Retry this");
});

test("disconnect keeps the modal and path draft, reconnect never silently accepts an earlier validation or retries navigation", async () => {
  const h = harness(); const selected = h.open({ initialPath: "C:\\Projects" }); h.respond(listing("C:\\Projects"));
  await h.input("server-workspace-path", "D:\\Typed before disconnect");
  for (const listener of h.events.get("missum:bridge-disconnected")) listener();
  assert.match(h.byId("server-workspace-status").textContent, /unterbrochen/);
  assert.equal(h.byId("server-workspace-overlay").open, true);
  assert.equal(h.byId("server-workspace-path").value, "D:\\Typed before disconnect");
  for (const listener of h.events.get("missum:bridge-ready")) listener();
  assert.equal(h.calls.length, 1); assert.equal(h.button("Ordner auswählen").disabled, true);
  await h.button("Öffnen").click(); h.respond(listing("D:\\Typed before disconnect"));
  assert.equal(h.button("Ordner auswählen").disabled, false);
  h.cancel(); await selected;
});

test("unreachable folders and malformed directory lists cannot enable selection or replace unsubmitted text", async () => {
  const h = harness(); const selected = h.open({ initialPath: "C:\\Missing" });
  h.error("Der Ordner wurde nicht gefunden."); assert.equal(h.button("Ordner auswählen").disabled, true);
  assert.equal(h.byId("server-workspace-path").value, "C:\\Missing");
  await h.button("Öffnen").click(); h.respond(listing("C:\\Missing", [{ name: "Bad" }]));
  assert.match(h.byId("server-workspace-status").textContent, /ungültige Ordnerliste/);
  assert.equal(h.button("Ordner auswählen").disabled, true);
  h.cancel(); await selected;
});

test("synchronous bridge failures remain in the picker and preserve the requested path for retry", async () => {
  const h = harness(); h.bridge.post = () => { throw new Error("Nicht erlaubter Bridge-Typ: workspace.browse"); };
  const selected = h.open({ initialPath: "C:\\Not yet published" });
  assert.match(h.byId("server-workspace-status").textContent, /workspace\.browse/);
  assert.equal(h.byId("server-workspace-path").value, "C:\\Not yet published");
  assert.equal(h.button("Ordner auswählen").disabled, true);
  assert.equal(h.timers.size, 0);
  h.cancel(); assert.equal(await selected, null);
});

test("the modal traps Tab, folder arrows navigate focus, Alt-Up navigates parent and cancel restores focus", async () => {
  const h = harness(); const selected = h.open({ initialPath: "C:\\Projects" });
  h.respond(listing("C:\\Projects", [directory("A"), directory("B")]));
  const close = h.button("Ordnerauswahl schließen"), choose = h.button("Ordner auswählen");
  close.focus(); assert.equal(h.key(close, "Tab", { shiftKey: true }), true); assert.equal(h.document.activeElement, choose);
  assert.equal(h.key(choose, "Tab"), true); assert.equal(h.document.activeElement, close);
  h.folders()[0].focus(); h.key(h.folders()[0], "ArrowDown"); assert.equal(h.document.activeElement, h.folders()[1]);
  h.key(h.folders()[1], "Home"); assert.equal(h.document.activeElement, h.folders()[0]);
  assert.equal(h.key(h.byId("server-workspace-path"), "ArrowUp", { altKey: true }), true);
  assert.equal(h.calls.at(-1).payload.path, "C:\\");
  await h.button("Abbrechen").click(); assert.equal(await selected, null); assert.equal(h.document.activeElement, h.trigger);
});

test("a second open call shares the pending selection and never changes its original workspace context", async () => {
  const h = harness(); const selected = h.open({ initialPath: "C:\\Original" });
  assert.equal(h.open({ initialPath: "D:\\Other" }), selected); assert.equal(h.calls.length, 1);
  h.respond(listing("C:\\Original")); await h.button("Ordner auswählen").click();
  assert.equal(await selected, "C:\\Original");
});

test("server folder names and project labels render as text while styling supplies native layout, responsive and high contrast modes", async () => {
  const h = harness(); const selected = h.open({ initialPath: "C:\\Projects", recentPaths: [{ path: "D:\\Project", name: "<script>bad</script>" }] });
  h.respond(listing("C:\\Projects", [directory("<img src=x onerror=bad>")]));
  assert.equal(h.folders()[0].textContent, "<img src=x onerror=bad>");
  assert.equal(h.byId("server-workspace-overlay").querySelectorAll("script,img").length, 0);
  assert.match(css, /Segoe UI/); assert.match(css, /grid-template-columns:\s*210px minmax\(0, 1fr\)/);
  assert.match(css, /forced-colors:\s*active/); assert.match(css, /max-width:\s*600px/);
  h.cancel(); await selected;
});
