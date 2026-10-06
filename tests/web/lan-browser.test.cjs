const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");
const { TestNode } = require("./test-dom.cjs");
const webRoot = path.resolve(__dirname, "../../src/Missum.App/Assets/Web");
const source = name => fs.readFileSync(path.join(webRoot, name), "utf8");
const storage = values => ({ getItem: key => values.get(key) || null, setItem: (key, value) => values.set(key, String(value)) });

function bridgeContext(device = new Map(), tab = new Map(), overrides = {}) {
  const sent = [], received = [], httpSent = [], pageEvents = new Map(); let socket, eventSource; let sequence = 0;
  class Socket {
    constructor() { socket = this; this.readyState = 1; this.events = {}; }
    addEventListener(type, callback) { this.events[type] = callback; }
    send(value) { sent.push(JSON.parse(value)); }
    close() { this.readyState = 3; this.events.close?.(); }
  }
  class Events {
    constructor() { eventSource = this; this.events = {}; }
    addEventListener(type, callback) { this.events[type] = callback; }
  }
  const context = vm.createContext({ URL, WebSocket: Socket, EventSource: Events,
    fetch: async (_, request) => { httpSent.push(JSON.parse(request.body)); return { ok: true }; }, localStorage: storage(device), sessionStorage: storage(tab),
    location: { protocol: "http:", href: "http://192.168.1.2:8080/assistant/", origin: "http://192.168.1.2:8080", host: "192.168.1.2:8080", pathname: "/assistant/" },
    crypto: { randomUUID: () => `id-${++sequence}` }, isSecureContext: false,
    navigator: { mediaDevices: { getUserMedia() {}, getDisplayMedia() {} } },
    CustomEvent: class { constructor(type, options) { this.type = type; this.detail = options?.detail; } },
    dispatchEvent: event => received.push(event), addEventListener: (type, callback) => pageEvents.set(type, callback), setTimeout, clearTimeout, ...overrides });
  vm.runInContext(source("bridge.js"), context);
  return { context, sent, received, httpSent, pageEvents, get socket() { return socket; }, get eventSource() { return eventSource; } };
}

test("HTTP capabilities cannot start Mac capture; client draft routes survive reload and separate tabs", () => {
  const devices = new Map(), tabOne = new Map();
  const first = bridgeContext(devices, tabOne);
  assert.equal(first.context.missumBridge.capabilities.microphone, false);
  assert.equal(first.context.missumBridge.capabilities.screen, false);
  first.context.missumBridge.post("session.draft", { sessionId: "chat", draft: "Mac one" });
  const reload = bridgeContext(devices, tabOne); const second = bridgeContext(devices, new Map());
  assert.equal(reload.context.missumBridge.clientId, first.context.missumBridge.clientId);
  assert.notEqual(second.context.missumBridge.clientId, first.context.missumBridge.clientId);
  second.context.missumBridge.post("session.draft", { sessionId: "chat", draft: "Mac two" });
  assert.equal(first.sent[0].payload.draft, "Mac one");
  assert.equal(second.sent[0].payload.draft, "Mac two");
  assert.notEqual(second.sent[0].clientId, first.sent[0].clientId);
  first.context.missumBridge.post("microphone.start", {});
  assert.equal(first.sent.length, 1, "HTTP microphone must never activate PC capture");
  assert.match(first.received.at(-1).detail.payload.message, /HTTP-LAN/);
});

function tabIdentityHarness() {
  const peers = [], deliveries = [], timers = []; let sequence = 0;
  class Channel {
    constructor(name) { this.name = name; peers.push(this); }
    addEventListener(_, callback) { this.callback = callback; }
    postMessage(data) { for (const peer of peers) if (peer !== this && !peer.closed && peer.name === this.name) deliveries.push(() => { if (!peer.closed) peer.callback({ data }); }); }
    close() { this.closed = true; }
  }
  const overrides = { BroadcastChannel: Channel, crypto: { randomUUID: () => `00000000-0000-4000-8000-${String(++sequence).padStart(12, "0")}` }, setTimeout: callback => { timers.push(callback); return timers.length; } };
  return { overrides, deliver: () => { while (deliveries.length) deliveries.shift()(); }, start: () => timers.shift()() };
}

test("duplicated sessionStorage gets a unique tab identity before sending while reload keeps its identity", () => {
  const handshake = tabIdentityHarness(), device = new Map(), originalStorage = new Map();
  const original = bridgeContext(device, originalStorage, handshake.overrides); handshake.start(); original.socket.events.open();
  const originalId = original.context.missumBridge.clientId;
  const clonedStorage = new Map(originalStorage), clone = bridgeContext(device, clonedStorage, handshake.overrides);
  clone.context.missumBridge.post("session.draft", { sessionId: "chat", draft: "Cloned tab draft" });
  assert.equal(clone.context.missumBridge.clientId, originalId, "the copied identity is provisional until its owner responds");
  handshake.deliver(); handshake.start(); clone.socket.events.open();
  assert.notEqual(clone.context.missumBridge.clientId, originalId);
  assert.equal(clone.sent[0].clientId, clone.context.missumBridge.clientId, "queued commands use the final identity");
  assert.equal(clone.sent[0].tabId, clone.context.missumBridge.tabId);
  original.pageEvents.get("pagehide")();
  const reload = bridgeContext(device, originalStorage, handshake.overrides); handshake.deliver(); handshake.start();
  assert.equal(reload.context.missumBridge.clientId, originalId, "reload preserves the original tab's stored state");
  assert.notEqual(reload.context.missumBridge.clientId, clone.context.missumBridge.clientId);
});

test("a late tab collision rebinds the transport without replaying an already submitted job", () => {
  const handshake = tabIdentityHarness(), device = new Map(), originalStorage = new Map();
  const original = bridgeContext(device, originalStorage, handshake.overrides); handshake.start(); original.socket.events.open();
  const clone = bridgeContext(device, new Map(originalStorage), handshake.overrides); handshake.start(); clone.socket.events.open();
  const requestId = clone.context.missumBridge.post("chat.send", { sessionId: "chat", prompt: "Hallo" });
  assert.equal(clone.sent.length, 1);
  handshake.deliver(); clone.socket.events.open();
  assert.notEqual(clone.context.missumBridge.clientId, original.context.missumBridge.clientId);
  assert.equal(clone.sent.length, 1, "no submitted command is resent under another client identity");
  assert.ok(clone.received.some(event => event.detail?.type === "host.error" && event.detail.requestId === requestId));
});

test("bridge v2 rejects old revisions and foreign clients but accepts a new server epoch", () => {
  const { context, socket, received } = bridgeContext();
  const send = envelope => socket.events.message({ data: JSON.stringify({ version: 2, type: "state.snapshot", payload: {}, ...envelope }) });
  send({ hostEpoch: "server-a", revision: 8 }); send({ hostEpoch: "server-a", revision: 7 });
  send({ hostEpoch: "server-a", revision: 9, clientId: "other-client" });
  send({ hostEpoch: "server-b", revision: 1 });
  assert.equal(received.length, 2);
  assert.equal(context.missumBridge.version, 2);
});

test("reconnect retries an unacknowledged chat with its original identity only after a consistent snapshot", async () => {
  const browser = bridgeContext(), { context, socket, sent, httpSent } = browser;
  const wire = envelope => JSON.stringify({ version: 2, hostEpoch: "server-a", ...envelope });
  socket.events.open(); socket.events.message({ data: wire({ type: "state.snapshot", revision: 1, payload: {} }) });
  const requestId = context.missumBridge.post("chat.send", { sessionId: "chat", prompt: "Hallo" });
  socket.events.close(); browser.eventSource.events.open(); await tick();
  assert.equal(httpSent.filter(item => item.type === "chat.send").length, 0);
  browser.eventSource.events.message({ data: wire({ type: "state.snapshot", revision: 2, payload: {} }) }); await tick();
  const retry = httpSent.find(item => item.type === "chat.send");
  assert.equal(retry.requestId, requestId); assert.equal(retry.clientId, sent[0].clientId);
  browser.eventSource.events.message({ data: wire({ type: "action.completed", requestId, clientId: "another.tab", revision: 3, payload: { duplicate: true, originalRequestId: requestId } }) });
  browser.eventSource.events.error(); browser.eventSource.events.open(); await tick();
  browser.eventSource.events.message({ data: wire({ type: "state.snapshot", revision: 4, payload: {} }) }); await tick();
  assert.equal(httpSent.filter(item => item.type === "chat.send").length, 2, "a foreign receipt must not acknowledge this command");
  browser.eventSource.events.message({ data: wire({ type: "action.completed", requestId, clientId: context.missumBridge.clientId, revision: 5, payload: { duplicate: true, originalRequestId: requestId, receiptStatus: "complete" } }) });
  browser.eventSource.events.error(); browser.eventSource.events.open(); await tick();
  browser.eventSource.events.message({ data: wire({ type: "state.snapshot", revision: 6, payload: {} }) }); await tick();
  assert.equal(httpSent.filter(item => item.type === "chat.send").length, 2, "an acknowledged command is never retried again");
});

test("a server restart does not replay an old unacknowledged prompt into restored data", async () => {
  const browser = bridgeContext(), { context, socket, httpSent, received } = browser;
  socket.events.open(); socket.events.message({ data: JSON.stringify({ version: 2, type: "state.snapshot", hostEpoch: "before-restore", revision: 1, payload: {} }) });
  const requestId = context.missumBridge.post("chat.resume", { sessionId: "chat", messageId: "answer" });
  socket.events.close(); browser.eventSource.events.open(); await tick();
  browser.eventSource.events.message({ data: JSON.stringify({ version: 2, type: "state.snapshot", hostEpoch: "after-restore", revision: 1, payload: {} }) }); await tick();
  assert.equal(httpSent.filter(item => item.type === "chat.resume").length, 0);
  assert.ok(received.some(event => event.detail?.type === "host.error" && event.detail.requestId === requestId));
});

test("queue snapshots and own dedup receipts release composer admission without admitting foreign receipts", () => {
  const app = source("app.js"), state = { activeSessionId: "chat", pendingChatSend: { sessionId: "chat", requestId: "send", prompt: "Hallo" }, pendingResume: "resume", isRunning: true, runQueue: {} };
  const elements = { prompt: { value: "" } }; let draftSaves = 0;
  const context = vm.createContext({ state, elements, missumBridge: { isLanBrowser: true, clientId: "mac.tab" },
    renderStatus() {}, renderMessages() {}, renderContext() {}, renderActionMenu() {}, availableActionDescriptors: () => [], normalizeExtensionActionId: () => null, extensionActionIdForToolAction: () => null, toolActionForExtensionActionId: () => null, normalizeToolAction: () => null,
    persistentExtensionActionIds: new Set(), setPromptValue: value => { elements.prompt.value = value; }, scheduleDraftSave: () => { draftSaves += 1; }, showToast() {} });
  for (const name of ["normalizeScheduledRun", "normalizeRunQueue", "applyRunQueue", "handleHostMessage"]) {
    const start = app.indexOf(`  function ${name}(`), ending = app.slice(start).match(/\r?\n {2}\}(?:\r?\n|$)/);
    assert.ok(start >= 0 && ending); vm.runInContext(app.slice(start, start + ending.index + ending[0].length), context);
  }
  context.applyRunQueue({ active: { sessionId: "another-chat", requestId: "send" }, pending: [] });
  assert.ok(state.pendingChatSend);
  context.applyRunQueue({ active: { sessionId: "chat", requestId: "send" }, pending: [{ sessionId: "chat", requestId: "resume" }] });
  assert.equal(state.pendingChatSend, null); assert.equal(state.pendingResume, null);
  state.pendingChatSend = { sessionId: "chat", requestId: "send", prompt: "Hallo" };
  const receipt = clientId => ({ detail: { version: 2, type: "action.completed", clientId, requestId: "send", payload: { duplicate: true, originalRequestId: "send", receiptStatus: "failed" } } });
  context.handleHostMessage(receipt("another.tab")); assert.ok(state.pendingChatSend);
  context.handleHostMessage(receipt("mac.tab"));
  assert.equal(state.pendingChatSend, null); assert.equal(elements.prompt.value, "Hallo"); assert.equal(draftSaves, 1);
});

test("HTTP environments without crypto.randomUUID still submit valid request and steering identities", () => {
  const { context, sent } = bridgeContext();
  context.crypto = {};
  context.missumBridge.post("chat.send", { sessionId: "s", prompt: "Hallo" });
  assert.match(sent[0].requestId, /^[\da-f]{8}-(?:[\da-f]{4}-){3}[\da-f]{12}$/i);
  vm.runInContext(source("run-steering.js"), context);
  const request = context.missumRunSteering.request({ activeSessionId: "s", isRunning: true, activeRunSessionId: "s", activeRunId: "run", activeRunMessageId: "answer" }, "Bitte ändern", context.missumBridge.post);
  assert.match(request.inputId, /^[\da-f]{8}-(?:[\da-f]{4}-){3}[\da-f]{12}$/i);
  assert.equal(sent[1].requestId, request.inputId);
});

test("two Chrome tabs never share a pending steering request through browser storage", () => {
  const device = new Map();
  const first = bridgeContext(device, new Map()), second = bridgeContext(device, new Map());
  const state = { activeSessionId: "chat", isRunning: true, activeRunSessionId: "chat", activeRunId: "run", activeRunMessageId: "answer" };
  for (const browser of [first, second]) vm.runInContext(source("run-steering.js"), browser.context);
  const one = first.context.missumRunSteering.request(state, "Bitte ändern", first.context.missumBridge.post);
  const two = second.context.missumRunSteering.request(state, "Bitte ändern", second.context.missumBridge.post);
  assert.notEqual(one.inputId, two.inputId, "each tab owns its retry identity even in the same chat/run");
  const firstRetry = first.context.missumRunSteering.pendingRetry(state, "Bitte ändern");
  assert.equal(firstRetry.inputId, one.inputId);
});

function audioContext() {
  const callbacks = new Map(), feedback = [], audios = [], controlEvents = [], button = new TestNode("button"); button.hidden = true;
  class AudioMock {
    constructor() { this.events = {}; this.currentTime = 0; this.pauses = 0; this.plays = 0; audios.push(this); }
    addEventListener(type, callback) { this.events[type] = callback; }
    play() { this.plays += 1; return this.blocked ? Promise.reject({ name: "NotAllowedError" }) : Promise.resolve(); }
    pause() { this.pauses += 1; }
    load() {}
    removeAttribute() { this.src = ""; }
  }
  const context = vm.createContext({ Audio: AudioMock, setTimeout, clearTimeout,
    document: { getElementById: () => button },
    CustomEvent: class { constructor(type, options) { this.type = type; this.detail = options.detail; } },
    dispatchEvent: event => controlEvents.push(event),
    addEventListener: (type, callback) => callbacks.set(type, callback),
    missumBridge: { isLanBrowser: true, resourceUrl: value => value, post: (type, payload) => feedback.push({ type, payload }) } });
  vm.runInContext(source("browser-speech.js"), context);
  const emit = (type, payload) => callbacks.get("missum:host-message")({ detail: { type, payload } });
  return { context, feedback, audios, button, emit, callbacks, controlEvents };
}
const tick = () => new Promise(resolve => setImmediate(resolve));

test("F5 WAV sections play in order, preload one successor and report real pause/end positions", async () => {
  const { context, emit, audios, feedback, controlEvents } = audioContext();
  emit("speech.reset", { playbackId: "f5", firstSequence: 0 });
  assert.equal(controlEvents.at(-1).detail.payload.canPauseSpeech, true, "preparing F5 can be paused without microphone capture");
  emit("speech.audio", { playbackId: "f5", sequence: 1, url: "second.wav" });
  assert.equal(audios[0].plays, 0, "an out-of-order successor waits");
  emit("speech.audio", { playbackId: "f5", sequence: 0, url: "first.wav" }); await tick();
  assert.equal(audios[1].plays, 1); assert.equal(audios[0].preload, "auto");
  audios[1].currentTime = 1.25; audios[1].events.timeupdate();
  context.missumBrowserSpeech.togglePause();
  assert.equal(controlEvents.at(-1).detail.payload.isSpeechPaused, true);
  assert.equal(feedback.at(-1).payload.state, "paused");
  assert.equal(feedback.at(-1).payload.positionSeconds, 1.25);
  emit("speech.pause", { playbackId: "f5", paused: false }); await tick();
  audios[1].events.ended(); await tick();
  assert.equal(audios[0].plays, 1);
  assert.ok(feedback.some(item => item.payload.sequence === 0 && item.payload.state === "ended"));
  assert.deepEqual(feedback.map(item => item.payload.eventSequence), feedback.map((_, index) => index + 1));
});

test("stop/disconnect cannot be undone by late F5 WAVs and blocked autoplay has a user restart", async () => {
  const { context, emit, audios, feedback, button, callbacks } = audioContext();
  emit("speech.reset", { playbackId: "f5", firstSequence: 0 });
  emit("speech.audio", { playbackId: "f5", sequence: 0, url: "first.wav" }); await tick();
  context.missumBrowserSpeech.stop();
  emit("speech.reset", { playbackId: "f5", firstSequence: 0 });
  emit("speech.audio", { playbackId: "f5", sequence: 1, url: "late.wav" });
  assert.equal(audios.length, 1);
  emit("speech.reset", { playbackId: "new", firstSequence: 0 });
  emit("speech.audio", { playbackId: "new", sequence: 0, url: "new.wav" });
  audios[1].blocked = true;
  emit("speech.pause", { playbackId: "new", paused: true });
  emit("speech.pause", { playbackId: "new", paused: false }); await tick();
  assert.equal(feedback.at(-1).payload.state, "blocked"); assert.equal(button.hidden, false);
  audios[1].blocked = false; await button.dispatch("click"); await tick();
  assert.equal(button.hidden, true); assert.equal(feedback.at(-1).payload.state, "playing");
  callbacks.get("missum:bridge-disconnected")();
  emit("speech.audio", { playbackId: "new", sequence: 1, url: "late-new.wav" });
  assert.equal(audios.length, 2);
});

function settingsContext() {
  const ids = new Map(), events = new Map(), posts = [];
  function create(tag) {
    const element = new TestNode(tag); let id;
    Object.defineProperty(element, "id", { get: () => id, set: value => { id = value; ids.set(value, element); } });
    element.prepend = child => element.insertBefore(child, element.firstChild);
    element.style = { setProperty(key, value) { this[key] = value; }, getPropertyValue(key) { return this[key] || ""; }, removeProperty(key) { delete this[key]; } };
    return element;
  }
  const page = create("section"); page.id = "settings-page"; page.hidden = true;
  const open = create("button"); open.id = "open-settings";
  const documentElement = create("html"), body = create("body"); body.append(page);
  const context = vm.createContext({ document: { createElement: create, getElementById: id => ids.get(id), documentElement, body },
    addEventListener: (type, callback) => events.set(type, callback), crypto: {}, confirm: () => true,
    missumBridge: { post: (type, payload) => { const id = `request-${posts.length}`; posts.push({ type, payload, id }); return id; } },
    missumPanels: { setView() {} } });
  vm.runInContext(source("settings.js"), context);
  const emit = (type, payload, requestId) => events.get("missum:host-message")({ detail: { type, payload, requestId } });
  const values = { missumAiServerUrl: "http://127.0.0.1:8080", theme: "system", language: "de-DE", accentColor: "#A970FF", backgroundColor: "#181818", isAutomaticSpeechEnabled: false, liveCaptionLanguage: "de", codingToolStepsExpanded: false };
  const snapshot = { revision: 7, resolvedTheme: "dark", values, triggerActions: [{ value: "imageGeneration", label: "Bild erstellen", extensionActionId: "builtin.image/generate" }], triggers: [{ id: "11111111-1111-4111-8111-111111111111", revision: 2, action: "imageGeneration", phrase: "Bild", description: "", isEnabled: true }] };
  emit("settings.snapshot", snapshot);
  return { context, page, ids, emit, posts, snapshot, documentElement, disconnect: () => events.get("missum:bridge-disconnected")() };
}

test("settings send only editable changes with revisions and preserve dirty edits on conflict", async () => {
  const { context, ids, emit, snapshot, documentElement } = settingsContext();
  context.missumSettings.open();
  assert.equal(documentElement.dataset.theme, "dark", "System uses PC theme");
  const gateway = ids.get("settings-missumAiServerUrl"); gateway.value = "http://another-server:8080"; await gateway.dispatch("input");
  const update = context.missumSettings.buildUpdate();
  assert.equal(update.expectedRevision, 7);
  assert.deepEqual(Object.keys(update.values), ["missumAiServerUrl"]);
  assert.equal(update.triggers.length, 0);
  emit("settings.changed", { ...snapshot, revision: 8, values: { ...snapshot.values, language: "en-US" } });
  assert.equal(gateway.value, "http://another-server:8080", "remote save retains local draft");
  assert.equal(context.missumSettings.buildUpdate().expectedRevision, 7, "dirty base revision never silently advances");
});

test("interrupted settings saves unlock after reconnect while preserving the draft and revision check", async () => {
  const { context, page, ids, emit, posts, snapshot, disconnect } = settingsContext();
  context.missumSettings.open();
  const gateway = ids.get("settings-missumAiServerUrl"), save = ids.get("settings-save");
  gateway.value = "http://changed-server:8080"; await gateway.dispatch("input");
  await save.dispatch("click"); assert.equal(save.disabled, true);
  disconnect();
  assert.equal(save.disabled, false);
  assert.equal(gateway.value, "http://changed-server:8080");
  const current = { ...snapshot, revision: 8, values: { ...snapshot.values, missumAiServerUrl: gateway.value } };
  emit("settings.snapshot", current, "reconnect-ready");
  assert.equal(gateway.value, "http://changed-server:8080");
  assert.equal(context.missumSettings.buildUpdate().expectedRevision, 7, "reconnect must not silently rebase the draft");
  const reload = page.querySelector(".settings-actions").children.find(item => item.textContent === "Aktuellen Stand laden");
  await reload.dispatch("click"); emit("settings.snapshot", current, posts.at(-1).id);
  assert.equal(context.missumSettings.buildUpdate().expectedRevision, 8);
  gateway.value = "http://next-server:8080"; await gateway.dispatch("input");
  await save.dispatch("click"); assert.equal(save.disabled, true);
  await reload.dispatch("click");
  assert.equal(save.disabled, false, "explicit reload also releases a missing save acknowledgement");
  emit("settings.snapshot", current, posts.at(-1).id);
  gateway.value = "http://last-server:8080"; await gateway.dispatch("input"); await save.dispatch("click");
  assert.equal(posts.at(-1).type, "settings.update", "the next save is admitted after reloading");
  assert.equal(posts.at(-1).payload.expectedRevision, 8);
});

test("HTTP new trigger fallback produces a valid UUID and deletions include row revision", async () => {
  const { context, page } = settingsContext();
  const add = page.querySelector(".settings-trigger-add"); const [action, phrase, addButton] = add.children;
  action.value = "imageGeneration"; phrase.value = "Erstelle Grafik"; await addButton.dispatch("click");
  const update = context.missumSettings.buildUpdate(); assert.equal(update.triggers.length, 1);
  assert.match(update.triggers[0].id, /^[\da-f]{8}-(?:[\da-f]{4}-){3}[\da-f]{12}$/i);
  assert.equal(update.triggers[0].extensionActionId, "builtin.image/generate");
  const tbody = page.querySelector("tbody"); const checkbox = tbody.children.find(row => row.children[1].firstChild.value === "Bild").firstChild.firstChild;
  checkbox.checked = true; await checkbox.dispatch("change"); await page.querySelector(".settings-trigger-filters").children[2].dispatch("click");
  assert.equal(context.missumSettings.buildUpdate().deletedTriggers[0].revision, 2);
});

test("trigger input edits survive adding, filtering and sorting before save and server reload", async () => {
  const { context, page, ids, posts, emit, snapshot } = settingsContext();
  context.missumSettings.open();
  const [action, phrase, add] = page.querySelector(".settings-trigger-add").children;
  const tbody = page.querySelector("tbody");
  const findRow = name => tbody.children.find(row => row.children[1].firstChild.value === name);
  action.value = "imageGeneration";
  phrase.value = "__Abnahme_A"; await add.dispatch("click");
  let rowA = findRow("__Abnahme_A");
  rowA.children[2].firstChild.value = "Lokaler Browser-Abnahmetest.";
  await rowA.children[2].firstChild.dispatch("input");
  rowA.children[4].firstChild.checked = false; await rowA.children[4].firstChild.dispatch("change");
  phrase.value = "__Abnahme_B"; await add.dispatch("click");
  const rowB = findRow("__Abnahme_B");
  rowB.children[4].firstChild.checked = false; await rowB.children[4].firstChild.dispatch("change");
  const search = page.querySelector(".settings-trigger-filters").children[0];
  search.value = "__Abnahme"; await search.dispatch("input");
  assert.equal(findRow("__Abnahme_A").children[2].firstChild.value, "Lokaler Browser-Abnahmetest.");
  const sort = page.querySelectorAll(".settings-sort").find(button => button.textContent === "Beschreibung");
  await sort.dispatch("click");
  rowA = findRow("__Abnahme_A");
  rowA.children[1].firstChild.value = "__Abnahme_A_Bearbeitet"; await rowA.children[1].firstChild.dispatch("input");
  ids.get("settings-trigger-category").value = "imageGeneration"; await ids.get("settings-trigger-category").dispatch("change");
  assert.equal(findRow("__Abnahme_A_Bearbeitet").children[2].firstChild.value, "Lokaler Browser-Abnahmetest.");
  await ids.get("settings-save").dispatch("click");
  const saved = posts.at(-1);
  assert.equal(saved.type, "settings.update"); assert.equal(saved.payload.expectedRevision, 7);
  assert.equal(saved.payload.triggers.length, 2);
  assert.equal(saved.payload.triggers.find(row => row.phrase === "__Abnahme_A_Bearbeitet").description, "Lokaler Browser-Abnahmetest.");
  assert.ok(saved.payload.triggers.every(row => row.isEnabled === false && row.revision === 0));
  const server = { ...snapshot, revision: 8, triggers: [...snapshot.triggers, ...saved.payload.triggers.map(row => ({ ...row, revision: 1 }))] };
  emit("settings.changed", server, saved.id);
  assert.equal(findRow("__Abnahme_A_Bearbeitet").children[2].firstChild.value, "Lokaler Browser-Abnahmetest.");
  await page.querySelector(".settings-actions").children.find(button => button.textContent === "Aktuellen Stand laden").dispatch("click");
  emit("settings.snapshot", server, posts.at(-1).id);
  assert.equal(findRow("__Abnahme_A_Bearbeitet").children[2].firstChild.value, "Lokaler Browser-Abnahmetest.");
  assert.equal(context.missumSettings.buildUpdate().triggers.length, 0);
});

test("typing a trigger description immediately protects the unsaved draft from a newer settings snapshot", async () => {
  const { context, page, emit, snapshot } = settingsContext();
  const description = page.querySelector("tbody").children[0].children[2].firstChild;
  description.value = "Noch nicht gespeicherter Text"; await description.dispatch("input");
  emit("settings.changed", { ...snapshot, revision: 8 });
  assert.equal(page.querySelector("tbody").children[0].children[2].firstChild.value, "Noch nicht gespeicherter Text");
  assert.equal(context.missumSettings.buildUpdate().expectedRevision, 7);
  assert.equal(context.missumSettings.buildUpdate().triggers[0].description, "Noch nicht gespeicherter Text");
});

function panelsContext() {
  const ids = new Map(), events = new Map(), posts = [], timers = [];
  const body = new TestNode("body");
  for (const id of ["browser-view-panel", "session-tabs", "output-inspector", "settings-page", "conversation-pane", "science-workbench", "model-picker", "local-model-overlay", "prompt-navigation", "tabbar-sidebar-toggle", "inspector-toggle", "browser-host-label"]) {
    const element = new TestNode(id === "model-picker" ? "select" : "div"); ids.set(id, element); body.append(element);
    if (id === "model-picker") Object.defineProperty(element, "options", { get: () => element.children });
  }
  ids.get("local-model-overlay").hidden = true;
  const pane = new TestNode("div"); pane.className = "chat-pane"; body.append(pane);
  const current = { activeSessionId: "chat", chatMode: "claudescience", sessions: [{ id: "chat", title: "Forschung" }], messages: [], scientificResearch: { selectedProjectId: "project-a", projects: [{ id: "project-a" }, { id: "project-b" }] } };
  const context = vm.createContext({ URL,
    location: { href: "http://192.168.1.2:8080/assistant/", origin: "http://192.168.1.2:8080", hostname: "192.168.1.2", hash: "" },
    sessionStorage: storage(new Map()),
    setTimeout: callback => { timers.push(callback); return timers.length; },
    addEventListener: (type, callback) => events.set(type, callback),
    document: { createElement: tag => new TestNode(tag), getElementById: id => ids.get(id), body, addEventListener() {}, querySelector: value => body.querySelector(value), querySelectorAll: value => body.querySelectorAll(value) },
    missumApp: { getState: () => current },
    missumBridge: { isLanBrowser: true, clientId: "mac.tab", resourceUrl: value => value, post: (type, payload) => posts.push({ type, payload }) }
  });
  vm.runInContext(source("browser-panels.js"), context);
  const emit = (type, payload) => { events.get("missum:host-message")({ detail: { type, payload } }); while (timers.length) timers.shift()(); };
  return { context, current, ids, emit, posts };
}

test("Science publication broadcasts keep the selected project and restore cached projects without stale PDFs", () => {
  const { context, current, ids, emit } = panelsContext();
  emit("science.presentation", { projectId: "project-a", revision: 3, publication: { pdfUrl: "science/a.pdf" } });
  context.missumPanels.setView("publication");
  const pdf = () => ids.get("browser-view-panel").querySelector("iframe")?.src;
  assert.match(pdf(), /science\/a\.pdf$/);
  emit("science.presentation", { projectId: "project-b", revision: 4, publication: { pdfUrl: "science/b.pdf" } });
  assert.match(pdf(), /science\/a\.pdf$/, "another Windows project must not replace this Mac PDF");
  current.scientificResearch.selectedProjectId = "project-b"; emit("research.snapshot", {});
  assert.match(pdf(), /science\/b\.pdf$/);
  emit("science.presentation", { projectId: "project-b", revision: 2, publication: { pdfUrl: "science/stale.pdf" } });
  assert.match(pdf(), /science\/b\.pdf$/);
  const currentFrame = ids.get("browser-view-panel").querySelector("iframe");
  emit("science.presentation", { projectId: "project-b", status: "preparing", revision: 5 });
  assert.match(pdf(), /science\/b\.pdf$/, "a new revision keeps the previous PDF while preparing");
  assert.equal(ids.get("browser-view-panel").querySelector("iframe"), currentFrame, "progress preserves the open PDF page");
  current.scientificResearch.detail = { progress: "Neue Werkzeugausgabe" }; emit("research.snapshot", {});
  assert.equal(ids.get("browser-view-panel").querySelector("iframe"), currentFrame);
});
