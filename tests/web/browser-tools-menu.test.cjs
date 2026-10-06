const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const { TestNode } = require("./test-dom.cjs");
const source = fs.readFileSync(require.resolve("../../src/Missum.App/Assets/Web/browser-tools-menu.js"), "utf8");
const css = fs.readFileSync(require.resolve("../../src/Missum.App/Assets/Web/browser-tools-menu.css"), "utf8");

function descriptor(actionId, displayName, iconKey, selectable = true, extra = {}) {
  return { actionId, displayName, iconKey, description: `${displayName}: Detail`,
    actionKind: selectable ? "selectableTool" : "immediate",
    selectionBehavior: selectable ? "toggle" : "none", ...extra };
}
function builtins() {
  // Same normalized catalog order as availableActionDescriptors(); the native
  // page promotes Documents and Plan and moves immediate actions to the end.
  return [
    descriptor("builtin.workspace/attach-files-and-folders", "Dateien und Ordner", "attachment", false),
    descriptor("builtin.coding/plan-mode", "Planmodus", "plan", true, { supportedChatModes: ["coding"] }),
    descriptor("builtin.web/web-search", "Websuche", "web"),
    descriptor("builtin.web/deep-research", "Deep Research", "research"),
    descriptor("builtin.media/image-analysis", "Bild analysieren", "image"),
    descriptor("builtin.media/audio-analysis", "Audio analysieren", "audio"),
    descriptor("builtin.media/video-analysis", "Video analysieren", "video"),
    descriptor("builtin.documents/create", "Dokumente erstellen", "document"),
    descriptor("builtin.image/generate", "Bild erstellen", "image"),
    descriptor("builtin.audiobook/create", "Hörbuch erstellen", "audiobook"),
    descriptor("builtin.documents/export-chat-pdf", "Chat als PDF exportieren", "pdf", false),
    descriptor("builtin.speech/translate", "Übersetzen", "translate"),
    descriptor("builtin.speech/read-aloud", "Vorlesen", "speech"),
    descriptor("builtin.speech/live-captions", "Live-Untertitel", "captions", false)
  ];
}
function harness(extra = {}) {
  const calls = [], active = new Set(), reasons = new Map(), globalEvents = new Map();
  let document;
  function create(tag) {
    const result = new TestNode(tag);
    result.style.setProperty = (key, value) => { result.style[key] = value; };
    result.focus = () => { document.activeElement = result; };
    result.click = () => result.dispatch("click");
    return result;
  }
  const body = create("body");
  document = { body, activeElement: null, createElement: create,
    createElementNS: (_, tag) => create(tag), documentElement: { clientWidth: 1200, clientHeight: 900 } };
  const menu = create("div"), content = create("div"), heading = create("div");
  menu.id = "tools-menu"; menu.hidden = true; heading.className = "tools-menu__title";
  menu.append(heading, content); body.append(menu);
  const trigger = create("button"), composer = create("div"); body.append(trigger, composer);
  composer.bounds = { top: 800, bottom: 890, left: 180, right: 1116, width: 936 };
  const context = { document, innerWidth: 1200, innerHeight: 900,
    addEventListener: (type, listener) => {
      if (!globalEvents.has(type)) globalEvents.set(type, []);
      globalEvents.get(type).push(listener);
    } };
  vm.runInNewContext(source, context);
  const api = context.missumToolsMenu;
  const options = { menu, content, trigger, composer, descriptors: builtins(), mode: "general", sessionId: "general-1", isLan: true,
    invoke: item => calls.push(["invoke", item.actionId]),
    openFiles: () => calls.push(["files"]), openWorkspace: () => calls.push(["workspace"]),
    openPublication: () => calls.push(["publication"]),
    isActive: item => active.has(item.actionId), disabledReason: item => reasons.get(item.actionId) || null, ...extra };
  api.render(options);
  const rows = () => content.querySelectorAll(".native-tools-row");
  const labels = () => rows().map(row => row.querySelector(".native-tools-label").textContent);
  const find = label => rows().find(row => row.getAttribute("aria-label") === label);
  const key = key => {
    let prevented = false, stopped = false;
    for (const handler of menu.listeners.get("keydown") || []) handler({ key, target: document.activeElement,
      preventDefault() { prevented = true; }, stopPropagation() { stopped = true; }, stopImmediatePropagation() {} });
    return { prevented, stopped };
  };
  const open = () => { api.setOpen(menu, true); rows().find(row => !row.disabled)?.focus(); };
  return { api, options, document, menu, content, heading, trigger, composer, rows, labels, find, key, open,
    calls, active, reasons, context, globalEvents };
}

test("General uses the native flat order and inline document description without obsolete group headings", () => {
  const h = harness();
  assert.deepEqual(h.labels(), ["Dateien und Ordner", "Dokumente erstellen", "Websuche", "Bild analysieren", "Audio analysieren",
    "Video analysieren", "Bild erstellen", "Hörbuch erstellen", "Übersetzen", "Vorlesen", "Chat als PDF exportieren", "Live-Untertitel"]);
  assert.equal(h.find("Dokumente erstellen").querySelector(".native-tools-description").textContent, "Word, PDF, Tabellen und Präsentationen");
  assert.equal(h.content.querySelectorAll("section,strong,.action-menu__copy,.action-menu__group-title").length, 0);
  assert.equal(h.heading.textContent, "Hinzufügen");
  assert.equal(h.menu.getAttribute("aria-label"), "Hinzufügen");
  assert.equal(h.rows().every(row => row.children[1].classList.contains("native-tools-label")
    && row.children[2].classList.contains("native-tools-description")), true);
});

test("Codex adds Planmodus and Science adds the real publication route without a separate Deep Research tool", async () => {
  const coding = harness({ mode: "coding" });
  assert.deepEqual(coding.labels().slice(0, 3), ["Dateien und Ordner", "Planmodus", "Dokumente erstellen"]);
  await coding.find("Planmodus").click();
  assert.deepEqual(coding.calls, [["invoke", "builtin.coding/plan-mode"]]);
  const science = harness({ mode: "claudescience" });
  assert.deepEqual(science.labels().slice(0, 3), ["Dateien und Ordner", "Publikation öffnen", "Dokumente erstellen"]);
  assert.equal(science.labels().includes("Planmodus"), false);
  assert.equal(science.labels().includes("Deep Research"), false);
  science.open(); await science.find("Publikation öffnen").click();
  assert.deepEqual(science.calls, [["publication"]]);
  assert.equal(science.menu.hidden, true);
});

test("extension descriptors remain mode filtered and selectable tools precede built-ins while immediate extensions remain available", () => {
  const extension = descriptor("org.example.tools/analyze", "Erweiterung analysieren", "research");
  const execute = descriptor("org.example.tools/export", "Erweiterung exportieren", "extension", false);
  const wrongMode = descriptor("org.example.tools/codex", "Nur Codex", "code", true, { supportedChatModes: ["coding"] });
  const h = harness({ descriptors: [...builtins(), extension, execute, wrongMode, extension] });
  assert.deepEqual(h.labels().slice(0, 4), ["Dateien und Ordner", "Dokumente erstellen", "Erweiterung analysieren", "Websuche"]);
  assert.equal(h.labels().at(-1), "Erweiterung exportieren");
  assert.equal(h.labels().includes("Nur Codex"), false);
  assert.equal(h.labels().filter(value => value === "Erweiterung analysieren").length, 1);
});

test("the popup spans the actual composer, clamps to the viewport and uses the native 320px scrolling limit", () => {
  const h = harness(); h.open();
  assert.equal(h.menu.style.width, "936px");
  assert.equal(h.menu.style.left, "180px");
  assert.equal(h.menu.style.bottom, "104px");
  assert.equal(h.menu.style["--native-tools-max-height"], "320px");
  h.context.innerWidth = 390; h.composer.bounds = { top: 500, bottom: 650, left: 14, right: 950 };
  h.api.update(h.menu);
  assert.equal(h.menu.style.width, "374px");
  assert.equal(h.menu.style.left, "8px");
  h.composer.bounds.top = 200; h.api.update(h.menu);
  assert.equal(h.menu.style["--native-tools-max-height"], "188px");
});

test("Files opens the native two-choice submenu and browser file/project adapters are invoked only on their actual choices", async () => {
  const h = harness(); h.open(); await h.find("Dateien und Ordner").click();
  assert.deepEqual(h.labels(), ["Dateien hinzufügen", "Projektordner auswählen"]);
  assert.equal(h.menu.style.width, "360px");
  assert.equal(h.menu.getAttribute("aria-label"), "Dateien und Ordner");
  assert.equal(h.heading.querySelector("button").getAttribute("aria-label"), "Zurück zu Hinzufügen");
  assert.deepEqual(h.calls, []);
  assert.equal(h.document.activeElement, h.find("Dateien hinzufügen"));
  await h.find("Dateien hinzufügen").click();
  assert.deepEqual(h.calls, [["files"]]);
  assert.equal(h.menu.hidden, true);
  assert.equal(h.menu.dataset.toolsView, "main");
  h.open(); await h.find("Dateien und Ordner").click(); await h.find("Projektordner auswählen").click();
  assert.deepEqual(h.calls, [["files"], ["workspace"]]);
});

test("submenu keyboard navigation returns to Files and main Escape restores the invoking Plus button", () => {
  const h = harness(); h.open();
  assert.deepEqual(h.key("ArrowRight"), { prevented: true, stopped: true });
  assert.equal(h.document.activeElement, h.find("Dateien hinzufügen"));
  h.key("ArrowDown"); assert.equal(h.document.activeElement, h.find("Projektordner auswählen"));
  h.key("Escape"); assert.equal(h.document.activeElement, h.find("Dateien und Ordner"));
  assert.equal(h.menu.hidden, false);
  h.key("ArrowRight"); h.key("ArrowLeft"); assert.equal(h.document.activeElement, h.find("Dateien und Ordner"));
  h.key("Escape"); assert.equal(h.menu.hidden, true); assert.equal(h.document.activeElement, h.trigger);
});

test("HTTP capture actions stay visible with a usable file alternative and media analysis stays enabled", async () => {
  const microphone = descriptor("org.example.capture/microphone", "Mikrofon", "audio", false);
  const screen = descriptor("org.example.capture/screen-capture", "Bildschirmaufnahme", "video", false);
  const h = harness({ descriptors: [...builtins(), microphone, screen] }); h.open();
  for (const label of ["Live-Untertitel", "Mikrofon", "Bildschirmaufnahme"]) {
    const row = h.find(label); assert.equal(row.disabled, true);
    assert.match(row.querySelector(".native-tools-description").textContent, /anhängen;.*HTTP/);
    await row.click();
  }
  assert.deepEqual(h.calls, []);
  for (const label of ["Bild analysieren", "Audio analysieren", "Video analysieren", "Vorlesen"]) assert.equal(h.find(label).disabled, false);
  await h.find("Audio analysieren").click();
  assert.deepEqual(h.calls, [["invoke", "builtin.media/audio-analysis"]]);
});

test("state updates show selection without recoloring native icons and respect live disabled resolution over stale descriptor data", () => {
  const items = builtins(); const pdf = items.find(item => item.iconKey === "pdf");
  pdf.disabledReason = "Der Chat enthält noch keine Nachrichten.";
  const h = harness({ descriptors: items });
  assert.equal(h.find("Chat als PDF exportieren").disabled, false, "the live resolver supersedes the stale descriptor");
  h.active.add("builtin.web/web-search");
  h.reasons.set("builtin.documents/export-chat-pdf", "Der Chat enthält noch keine Nachrichten.");
  h.api.update(h.menu);
  const web = h.find("Websuche");
  assert.equal(web.getAttribute("role"), "menuitemcheckbox");
  assert.equal(web.getAttribute("aria-checked"), "true");
  assert.equal(web.querySelector(".native-tools-status").hidden, false);
  assert.equal(web.querySelector("svg").style.color, "#4c94f2");
  assert.equal(h.find("Hörbuch erstellen").querySelector("svg").style.color, "#f19d38");
  assert.equal(h.find("Chat als PDF exportieren").querySelector("svg").style.color, "#da5052");
  assert.equal(h.find("Chat als PDF exportieren").disabled, true);
  h.active.clear(); h.api.update(h.menu); assert.equal(web.getAttribute("aria-checked"), "false");
});

test("arrow and edge keys skip disabled rows, preserve focus on refresh and let Tab dismiss without trapping focus", () => {
  const h = harness(); h.reasons.set("builtin.documents/create", "Nicht bereit"); h.open();
  h.key("ArrowDown"); assert.equal(h.document.activeElement, h.find("Websuche"));
  h.key("End"); assert.equal(h.document.activeElement, h.find("Chat als PDF exportieren"));
  h.key("Home"); assert.equal(h.document.activeElement, h.find("Dateien und Ordner"));
  h.key("ArrowUp"); assert.equal(h.document.activeElement, h.find("Chat als PDF exportieren"));
  h.api.render(h.options); assert.equal(h.document.activeElement, h.find("Chat als PDF exportieren"));
  assert.deepEqual(h.key("Tab"), { prevented: false, stopped: false }); assert.equal(h.menu.hidden, true);
});

test("changing mode or session dismisses a stale submenu and rebinding routes uses the current session callbacks", async () => {
  const h = harness(); h.open(); await h.find("Dateien und Ordner").click();
  const next = { ...h.options, mode: "coding", sessionId: "codex-2", openFiles: () => h.calls.push(["files-codex-2"]) };
  h.api.render(next);
  assert.equal(h.menu.hidden, true);
  assert.equal(h.menu.dataset.toolsView, "main");
  assert.equal(h.trigger.getAttribute("aria-expanded"), "false");
  assert.equal(h.labels()[1], "Planmodus");
  h.open(); await h.find("Dateien und Ordner").click(); await h.find("Dateien hinzufügen").click();
  assert.deepEqual(h.calls, [["files-codex-2"]]);
  h.open(); h.api.render({ ...next, sessionId: "codex-3" }); assert.equal(h.menu.hidden, true);
});

test("descriptor text is rendered as text and disabled native reasons suppress invocation even after a state change", async () => {
  const unsafe = descriptor("org.example.tools/html", "<img src=x onerror=alert(1)>", "extension", true,
    { description: "<script>bad()</script>" });
  const h = harness({ descriptors: [unsafe] }); h.open();
  const row = h.find(unsafe.displayName);
  assert.equal(row.querySelector(".native-tools-description").textContent, unsafe.description);
  assert.equal(row.querySelectorAll("img,script").length, 0);
  h.reasons.set(unsafe.actionId, "Jetzt gesperrt");
  await row.click(); assert.deepEqual(h.calls, []);
  h.api.update(h.menu); assert.equal(row.title, "Jetzt gesperrt");
});

test("native styling provides composer-wide compact rows, inline ellipsis, theme and high-contrast support", () => {
  assert.match(css, /position:\s*fixed/);
  assert.match(css, /border-radius:\s*24px/);
  assert.match(css, /grid-template-columns:\s*16px max-content minmax\(0, 1fr\) 16px/);
  assert.match(css, /height:\s*28px/);
  assert.match(css, /overflow-y:\s*auto/);
  assert.match(css, /html\[data-theme="dark"\]/);
  assert.match(css, /forced-colors:\s*active/);
  assert.match(css, /--native-tools-label:\s*var\(--text,/);
});
