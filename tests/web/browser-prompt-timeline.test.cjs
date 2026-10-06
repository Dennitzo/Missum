const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const { TestNode } = require("./test-dom.cjs");
const source = fs.readFileSync(require.resolve("../../src/Missum.App/Assets/Web/browser-prompt-timeline.js"), "utf8");
const css = fs.readFileSync(require.resolve("../../src/Missum.App/Assets/Web/browser-prompt-timeline.css"), "utf8");

function harness() {
  const documentEvents = new Map(), globalEvents = new Map(), frames = new Map(), observers = [], scrolls = [];
  let document, frameId = 0;
  const emit = (registry, type, event = {}) => { for (const listener of registry.get(type) || []) listener({ type, ...event }); };
  function create(tag) {
    const element = new TestNode(tag);
    element.removeEventListener = (type, listener) => { element.listeners.set(type, (element.listeners.get(type) || []).filter(item => item !== listener)); };
    element.fire = (type, extra = {}) => emit(element.listeners, type, { target: element, currentTarget: element, preventDefault() {}, stopPropagation() {}, ...extra });
    element.focus = () => { if (document.activeElement === element) return; document.activeElement?.fire?.("blur"); document.activeElement = element; element.fire("focus"); };
    const remove = element.remove.bind(element);
    element.remove = () => { if (element.parentNode && document?.activeElement === element) { element.fire("blur"); document.activeElement = null; } remove(); };
    return element;
  }
  const body = create("body");
  document = { body, activeElement: null, documentElement: { clientWidth: 1266, clientHeight: 913 }, createElement: create, createElementNS: (_namespace, tag) => create(tag),
    addEventListener: (type, listener) => { if (!documentEvents.has(type)) documentEvents.set(type, []); documentEvents.get(type).push(listener); },
    removeEventListener: (type, listener) => documentEvents.set(type, (documentEvents.get(type) || []).filter(item => item !== listener)) };
  class Observer {
    constructor(callback) { this.callback = callback; this.targets = []; this.disconnected = false; observers.push(this); }
    observe(target) { this.targets.push(target); }
    disconnect() { this.disconnected = true; }
    notify() { if (!this.disconnected) this.callback(); }
  }
  const parent = create("main"); parent.bounds = { left: 288, top: 44, right: 1266, bottom: 913 }; body.append(parent);
  const container = create("nav"); container.className = "prompt-navigation"; parent.append(container);
  const scroller = create("section"); scroller.bounds = { left: 288, top: 98, right: 1266, bottom: 698, height: 600 }; scroller.clientHeight = 600;
  scroller.scrollHeight = 2200; scroller.scrollTop = 0; parent.append(scroller);
  const messageRoot = create("div"); scroller.append(messageRoot);
  scroller.scrollTo = options => { scrolls.push(options); scroller.scrollTop = options.top; scroller.fire("scroll"); };
  const context = { document, Date, innerWidth: 1266, innerHeight: 913, ResizeObserver: Observer, MutationObserver: Observer,
    requestAnimationFrame: callback => { frames.set(++frameId, callback); return frameId; }, cancelAnimationFrame: id => frames.delete(id),
    addEventListener: (type, listener) => { if (!globalEvents.has(type)) globalEvents.set(type, []); globalEvents.get(type).push(listener); },
    removeEventListener: (type, listener) => globalEvents.set(type, (globalEvents.get(type) || []).filter(item => item !== listener)),
    matchMedia: () => ({ matches: false }) };
  vm.runInNewContext(source, context);
  const article = (id, offset, root = messageRoot) => {
    const item = create("article"); item.dataset.messageId = id; item.offset = offset;
    item.getBoundingClientRect = () => ({ top: scroller.bounds.top + 20 + item.offset - scroller.scrollTop, bottom: scroller.bounds.top + item.offset + 100 - scroller.scrollTop, left: 390, right: 1200 });
    root.append(item); return item;
  };
  const render = (messages, options = {}) => context.missumPromptTimeline.render({ container, scroller, messageRoot, messages, scopeKey: "session:chat", ...options });
  const markers = () => container.querySelectorAll(".browser-prompt-timeline-marker");
  const preview = () => body.querySelector(".browser-prompt-timeline-preview");
  const active = () => markers().find(marker => marker.getAttribute("aria-current") === "location")?.dataset.promptId;
  const flush = () => { const queued = [...frames.values()]; frames.clear(); queued.forEach(callback => callback()); };
  const keyboard = (element, key) => { emit(documentEvents, "keydown", { key }); element.fire("keydown", { key }); };
  return { context, document, body, parent, container, scroller, messageRoot, observers, frames, scrolls, article, render, markers, preview, active, flush, keyboard, emit,
    documentEvents, globalEvents, create };
}
const user = (id, content = `Frage ${id}`, extra = {}) => ({ id, role: "user", content, ...extra });
const answer = (id, content = `Antwort ${id}`, extra = {}) => ({ id, role: "assistant", content, ...extra });

test("native active selection follows the viewport threshold and its strict bottom boundary", () => {
  const h = harness(), select = h.context.missumPromptTimeline.selectActive;
  assert.equal(select([], 0, 600, 2000), null);
  const prompts = [{ id: "third", offset: 1200 }, { id: "first", offset: 0 }, { id: "second", offset: 600 }];
  assert.equal(select(prompts, 503, 600, 2000), "first");
  assert.equal(select(prompts, 504, 600, 2000), "second");
  assert.equal(select(prompts, 539, 200, 2000), "first");
  assert.equal(select(prompts, 540, 200, 2000), "second");
  assert.equal(select(prompts, 1992, 600, 2000), "third");
  assert.equal(select(prompts, 0, 600, 0), "third", "a non-scrollable conversation selects its last prompt");
  assert.equal(select([["first", 0], ["second", 1200]], 992, 0, 1000), "first", "eight pixels from the bottom is not yet the bottom special case");
  assert.equal(select([["first", 0], ["second", 1200]], 993, 0, 1000), "second");
});

test("preview normalization matches native whitespace and exact character limits without parsing Markdown", () => {
  const preview = harness().context.missumPromptTimeline.previewText;
  assert.equal(preview(" \n  Frage\t mit\r\nAbständen  ", 118), "Frage mit Abständen");
  assert.equal(preview("**Wörtlich** <script>kein HTML</script>", 118), "**Wörtlich** <script>kein HTML</script>");
  assert.equal(preview("a".repeat(118), 118), "a".repeat(118));
  assert.equal(preview("b".repeat(119), 118), "b".repeat(117) + "…");
  assert.equal(preview(null, 190), "");
  assert.throws(() => preview("x", 1), { name: "RangeError" });
});

test("one real prompt already shows a centered short native marker without numbered circles", () => {
  const h = harness(); h.article("first", 0);
  h.render([user("first", "Eine Frage")]);
  const marker = h.markers()[0];
  assert.equal(h.container.hidden, false); assert.equal(h.markers().length, 1);
  assert.equal(marker.textContent, ""); assert.match(marker.getAttribute("aria-label"), /^Zu Prompt 1 springen: Eine Frage$/);
  assert.equal(marker.querySelector("span").style.width, "20px");
  assert.equal(marker.style.top, "271px"); assert.equal(h.container.style.height, "556px");
  assert.equal(h.container.style.top, "74px"); assert.equal(h.container.style.left, "20px");
  assert.equal(h.preview().hidden, true);
  assert.equal(h.container.querySelector(".prompt-navigation-dot"), null);
});

test("marker rows use a centered fourteen pixel stride and compress many prompts into the rail", () => {
  const h = harness(); const messages = ["first", "second", "third"].map(id => user(id));
  messages.forEach((message, index) => h.article(message.id, index * 600)); h.render(messages);
  assert.deepEqual(h.markers().map(marker => Number.parseFloat(marker.style.top)), [257, 271, 285]);
  const crowded = Array.from({ length: 80 }, (_, index) => user(`crowded-${index}`));
  h.messageRoot.replaceChildren(); crowded.forEach((message, index) => h.article(message.id, index * 100)); h.render(crowded);
  const positions = h.markers().map(marker => Number.parseFloat(marker.style.top));
  assert.equal(positions[0], 0); assert.equal(positions.at(-1), 542);
  assert.ok(positions[1] - positions[0] < 14); assert.equal(positions.at(-1) + 14, 556);
});

test("hover shows safe prompt and subsequent assistant previews with the native text caps and bookmark", () => {
  const h = harness(); h.article("first", 0); h.article("second", 700);
  const prompt = "<img onerror=alert(1)> " + "P".repeat(150), reply = "<script>never execute</script> " + "A".repeat(230);
  h.render([user("first", prompt), answer("reply", reply), user("second")]);
  const marker = h.markers()[0]; marker.fire("pointerenter");
  assert.equal(h.preview().hidden, false); assert.equal(marker.querySelector("span").style.width, "22px");
  assert.equal(h.preview().querySelector(".browser-prompt-timeline-preview__prompt").textContent, "1) " + prompt.slice(0, 117) + "…");
  assert.equal(h.preview().querySelector(".browser-prompt-timeline-preview__answer").textContent, reply.slice(0, 189) + "…");
  assert.equal(h.preview().querySelectorAll("script,img").length, 0);
  assert.equal(h.preview().querySelector("svg").getAttribute("aria-hidden"), "true");
  assert.equal(marker.getAttribute("aria-describedby"), h.preview().id);
  marker.fire("pointerleave"); assert.equal(h.preview().hidden, true); assert.equal(marker.querySelector("span").style.width, "20px");
});

test("programmatic or pointer focus does not open a preview, keyboard navigation does", () => {
  const h = harness(); h.article("first", 0); h.article("second", 700); h.render([user("first"), user("second")]);
  const [first, second] = h.markers(); first.focus(); assert.equal(h.preview().hidden, true);
  h.document.activeElement = null; h.emit(h.documentEvents, "keydown", { key: "Tab" }); first.focus();
  assert.equal(h.preview().hidden, false); assert.equal(first.querySelector("span").style.width, "22px");
  h.keyboard(first, "ArrowDown"); assert.equal(h.document.activeElement, second); assert.match(h.preview().textContent, /^2\)/);
  h.keyboard(second, "Home"); assert.equal(h.document.activeElement, first);
  h.keyboard(first, "End"); assert.equal(h.document.activeElement, second);
  h.keyboard(second, "Escape"); assert.equal(h.preview().hidden, true);
  first.focus(); assert.equal(h.preview().hidden, true, "an automatic focus transfer after Escape remains silent");
});

test("scroll closes previews and updates the active line using actual message positions", () => {
  const h = harness(); h.article("first", 0); h.article("second", 600); h.article("third", 1200);
  h.render([user("first"), user("second"), user("third")]); h.markers()[0].fire("pointerenter");
  h.scroller.scrollTop = 504; h.scroller.fire("scroll"); assert.equal(h.preview().hidden, true); h.flush();
  assert.equal(h.active(), "second"); assert.deepEqual(h.markers().map(marker => marker.querySelector("span").style.width), ["7px", "20px", "7px"]);
  h.scroller.scrollTop = 1594; h.scroller.fire("scroll"); h.flush(); assert.equal(h.active(), "third");
});

test("click jumps smoothly to the selected prompt, clamps at the bottom and never uses a foreign root", () => {
  const h = harness(); h.article("first", 300); h.article("second", 5000);
  const foreign = h.create("div"); h.body.append(foreign); h.article("first", 10000, foreign);
  h.render([user("first"), user("second")]); h.markers()[0].fire("click"); h.flush();
  assert.deepEqual(h.scrolls.map(item => ({ ...item })), [{ top: 300, behavior: "smooth" }]);
  h.markers()[1].fire("click"); h.flush(); assert.equal(h.scrolls.at(-1).top, 1600);
  h.context.matchMedia = () => ({ matches: true }); h.markers()[0].fire("click");
  assert.equal(h.scrolls.at(-1).behavior, "auto");
});

test("streaming answer updates keep the focused marker, refresh an open preview and reveal a previously empty reply", () => {
  const h = harness(); h.article("first", 0); const first = user("first");
  h.render([first, answer("reply", "", { status: "streaming" })]); const marker = h.markers()[0];
  h.emit(h.documentEvents, "keydown", { key: "Tab" }); marker.focus();
  assert.equal(h.preview().querySelector(".browser-prompt-timeline-preview__answer").hidden, true);
  h.render([first, answer("reply", "Die Antwort wächst live.", { status: "streaming" })]);
  assert.equal(h.markers()[0], marker); assert.equal(h.document.activeElement, marker);
  assert.equal(h.preview().hidden, false); assert.equal(h.preview().querySelector(".browser-prompt-timeline-preview__answer").textContent, "Die Antwort wächst live.");
  h.render([first, answer("reply", "Abgeschlossen.", { status: "completed" })]);
  assert.equal(h.preview().querySelector(".browser-prompt-timeline-preview__answer").textContent, "Abgeschlossen.");
  assert.equal((h.scroller.listeners.get("scroll") || []).length, 1);
});

test("resize and growing answers remeasure the rail and prompt offsets without rebuilding buttons", () => {
  const h = harness(); h.article("first", 0); const second = h.article("second", 600);
  h.render([user("first"), user("second")]); const buttons = h.markers();
  second.offset = 900; h.scroller.scrollTop = 504;
  h.observers.filter(observer => !observer.disconnected).forEach(observer => observer.notify()); h.flush();
  assert.equal(h.active(), "first", "the growing answer moves the next prompt below the reading threshold");
  h.scroller.clientHeight = 400; h.scroller.bounds.height = 400; h.emit(h.globalEvents, "resize"); h.flush();
  assert.equal(h.container.style.height, "356px"); assert.deepEqual(h.markers().map(marker => Number.parseFloat(marker.style.top)), [164, 178]);
  assert.deepEqual(h.markers(), buttons);
});

test("switching to a child transcript clears old previews and makes old click closures inert even for identical IDs", () => {
  const h = harness(); h.article("shared-id", 400); h.render([user("shared-id", "Privater Hauptchat"), answer("a", "Alte Hauptantwort")]);
  const parentMarker = h.markers()[0]; parentMarker.fire("pointerenter");
  h.messageRoot.replaceChildren(); h.article("shared-id", 900);
  h.render([user("shared-id", "Subagent-Auftrag"), answer("a", "Neue Kinderantwort")], { scopeKey: "session:subagent:child" });
  assert.notEqual(h.markers()[0], parentMarker); assert.equal(h.preview().hidden, true); assert.equal(h.preview().textContent, "");
  parentMarker.fire("click"); parentMarker.fire("pointerenter"); assert.equal(h.scrolls.length, 0); assert.equal(h.preview().hidden, true);
  h.markers()[0].fire("pointerenter"); assert.match(h.preview().textContent, /Subagent-Auftrag/); assert.match(h.preview().textContent, /Neue Kinderantwort/);
  assert.equal(h.preview().textContent.includes("Haupt"), false);
});

test("hiding and disposing the timeline releases listeners and pending work cannot resurrect removed markers", () => {
  const h = harness(); h.article("first", 0); h.render([user("first")]);
  h.scroller.fire("scroll"); h.render([user("first")], { visible: false }); h.flush(); assert.equal(h.container.hidden, true);
  h.render([], { visible: true }); assert.equal(h.markers().length, 0); assert.equal(h.container.hidden, true);
  h.context.missumPromptTimeline.dispose(h.container);
  assert.equal((h.scroller.listeners.get("scroll") || []).length, 0); assert.ok(h.observers.every(observer => observer.disconnected));
  assert.equal(h.frames.size, 0); assert.equal(h.preview(), null);
  assert.equal((h.documentEvents.get("keydown") || []).length, 0); assert.equal((h.globalEvents.get("resize") || []).length, 0);
});

test("blank attachment-only prompts and missing DOM targets cannot create phantom or stale navigation", () => {
  const h = harness(); h.article("first", 0); h.render([user("attachment", " \n "), user("first", "Die erste sichtbare Frage"), user("missing")]);
  assert.equal(h.markers().filter(marker => !marker.hidden).length, 1);
  assert.equal(h.markers().find(marker => marker.dataset.promptId === "missing").hidden, true);
  h.markers().find(marker => marker.dataset.promptId === "first").fire("pointerenter");
  assert.match(h.preview().querySelector(".browser-prompt-timeline-preview__prompt").textContent, /^2\)/, "preview numbering counts chronological user messages as native does");
  h.messageRoot.replaceChildren(); h.observers.filter(observer => !observer.disconnected).forEach(observer => observer.notify()); h.flush();
  assert.equal(h.container.hidden, true); assert.equal(h.preview().hidden, true);
  h.markers().find(marker => marker.dataset.promptId === "first").fire("pointerenter");
  assert.equal(h.preview().hidden, true, "a stale pointer callback cannot preview a prompt without its current DOM target");
});

test("changing to a subagent scroller detaches the parent observers and navigates only the child root", () => {
  const h = harness(); h.article("shared", 600); h.render([user("shared", "Hauptchat")]);
  const originalObservers = [...h.observers];
  const childScroller = h.create("section"), childRoot = h.create("div"), childMessage = h.create("article");
  childScroller.bounds = { top: 98, left: 288, right: 1266, bottom: 498, height: 400 }; childScroller.clientHeight = 400;
  childScroller.scrollHeight = 1400; childScroller.scrollTop = 0;
  childScroller.scrollTo = options => { h.scrolls.push(options); childScroller.scrollTop = options.top; childScroller.fire("scroll"); };
  childMessage.dataset.messageId = "shared";
  childMessage.getBoundingClientRect = () => ({ top: 98 + 20 + 900 - childScroller.scrollTop, bottom: 1100, left: 390, right: 1200 });
  childRoot.append(childMessage); childScroller.append(childRoot); h.parent.append(childScroller);
  h.render([user("shared", "Subagent")], { scroller: childScroller, messageRoot: childRoot, scopeKey: "session:subagent:child" });
  assert.equal((h.scroller.listeners.get("scroll") || []).length, 0); assert.ok(originalObservers.every(observer => observer.disconnected));
  assert.equal((childScroller.listeners.get("scroll") || []).length, 1);
  h.scroller.fire("scroll"); assert.equal(h.frames.size, 0);
  h.markers()[0].fire("click"); h.flush(); assert.equal(h.scrolls.at(-1).top, 900);
  assert.equal(h.scroller.scrollTop, 0); assert.equal(h.container.style.height, "356px");
});

test("replacing a streaming child message root keeps its focused marker and refreshes the open answer preview", () => {
  const h = harness(); h.article("first", 200);
  h.render([user("first", "Kind-Auftrag"), answer("reply", "Erste Ausgabe")], { scopeKey: "session:subagent:child" });
  const marker = h.markers()[0], originalObservers = [...h.observers];
  h.emit(h.documentEvents, "keydown", { key: "Tab" }); marker.focus();
  assert.equal(h.preview().hidden, false);
  const replacement = h.create("div"); h.messageRoot.replaceWith(replacement); h.article("first", 700, replacement);
  h.render([user("first", "Kind-Auftrag"), answer("reply", "Aktuelle gestreamte Kind-Antwort")], { messageRoot: replacement, scopeKey: "session:subagent:child" });
  assert.equal(h.markers()[0], marker); assert.equal(h.document.activeElement, marker);
  assert.equal(h.preview().hidden, false); assert.match(h.preview().textContent, /Aktuelle gestreamte Kind-Antwort/);
  assert.equal(h.preview().textContent.includes("Erste Ausgabe"), false);
  assert.ok(originalObservers.every(observer => observer.disconnected));
  assert.equal((h.scroller.listeners.get("scroll") || []).length, 1);
  marker.fire("click"); h.flush(); assert.equal(h.scrolls.at(-1).top, 700, "the retained button now targets the new child article");
});

test("removing a prompt while replacing the same scoped root removes its stale marker and open preview", () => {
  const h = harness(); h.article("removed", 100);
  h.render([user("removed", "Gelöschter Auftrag"), answer("reply", "Alte Antwort")]);
  const stale = h.markers()[0]; stale.fire("pointerenter");
  const replacement = h.create("div"); h.messageRoot.replaceWith(replacement); h.article("current", 500, replacement);
  h.render([user("current", "Aktueller Auftrag"), answer("new", "Neue Antwort")], { messageRoot: replacement });
  assert.deepEqual(h.markers().map(marker => marker.dataset.promptId), ["current"]);
  assert.equal(stale.isConnected, false); assert.equal(h.preview().hidden, true); assert.equal(h.preview().textContent, "");
  stale.fire("click"); stale.fire("pointerenter"); assert.equal(h.scrolls.length, 0); assert.equal(h.preview().hidden, true);
  h.markers()[0].fire("pointerenter"); assert.match(h.preview().textContent, /Aktueller Auftrag/);
  assert.equal(h.preview().textContent.includes("Gelöschter"), false);
});

test("chronological dates and duplicate live overlays give each prompt one marker with the newest visible answer", () => {
  const h = harness(); h.article("first", 0); h.article("second", 800);
  const first = user("first", "Erste Frage", { createdAt: "2026-10-06T10:00:00Z" });
  const second = user("second", "Zweite Frage", { createdAt: "2026-10-06T10:02:00Z" });
  const oldAnswer = answer("reply", "Alter gespeicherter Anfang", { createdAt: "2026-10-06T10:01:00Z" });
  h.render([second, oldAnswer, first, { ...oldAnswer, content: "Aktuelle laufende Antwort" }, { ...first, content: "Aktuelle erste Frage" }]);
  assert.deepEqual(h.markers().map(marker => marker.dataset.promptId), ["first", "second"]);
  h.markers()[0].fire("pointerenter");
  assert.match(h.preview().textContent, /Aktuelle erste Frage/); assert.match(h.preview().textContent, /Aktuelle laufende Antwort/);
  assert.equal(h.preview().textContent.includes("Alter gespeicherter"), false);
});

test("native visual dimensions, line clamps and reduced-motion behavior are explicit in the dedicated CSS", () => {
  assert.match(css, /\.browser-prompt-timeline-marker\s*\{[^}]*width:\s*30px;[^}]*height:\s*14px;/s);
  assert.match(css, /\.browser-prompt-timeline-marker__line\s*\{[^}]*width:\s*7px;[^}]*height:\s*2px;/s);
  assert.match(css, /\.browser-prompt-timeline-preview\s*\{[^}]*width:\s*326px;[^}]*border-radius:\s*14px;/s);
  assert.match(css, /preview__prompt\s*\{[^}]*line-clamp:\s*2;/);
  assert.match(css, /preview__answer\s*\{[^}]*line-clamp:\s*3;/);
  assert.match(css, /preview__bookmark\s*\{[^}]*width:\s*14px;[^}]*height:\s*14px;/);
  assert.match(css, /prefers-reduced-motion:\s*reduce[^}]*transition:\s*none/s);
});
