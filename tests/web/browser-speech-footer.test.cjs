const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { TestNode } = require("./test-dom.cjs");
const root = path.resolve(__dirname, "../../src/Missum.App/Assets/Web");
const app = fs.readFileSync(path.join(root, "app.js"), "utf8").replace(/\r\n/g, "\n");
const icons = ["messageCopyIcon", "messageDoneIcon", "messageSpeechIcon", "messageSpeechStopIcon", "messageSpeechPauseIcon", "messageSpeechResumeIcon"];

function extract(name) {
  const start = app.indexOf(`  function ${name}(`);
  const ending = app.slice(start).match(/\n {2}\}(?:\n|$)/);
  assert.ok(start >= 0 && ending, `production helper ${name} exists`);
  return app.slice(start, start + ending.index + ending[0].length);
}

function harness() {
  let document; const allowedIcons = new Set(), posts = [], calls = { messages: 0, context: 0, status: 0, highlights: 0 };
  class Node extends TestNode {
    set innerHTML(value) { assert.ok(allowedIcons.has(String(value)), "only production's static icon SVG may use innerHTML"); this.markup = String(value); this.textContent = ""; }
    get innerHTML() { return this.markup || ""; }
    focus() { document.activeElement = this; }
  }
  const body = new Node("body");
  document = { body, activeElement: null, createElement: tag => new Node(tag), createTextNode: value => new Node("#text", value),
    createDocumentFragment: () => new Node("#fragment"),
    querySelectorAll: selector => body.querySelectorAll(selector), querySelector: selector => body.querySelector(selector) };
  const elements = Object.fromEntries(["activeTools", "documents", "contextStrip", "codingChanges", "prompt", "messageList", "messageScroll"].map(name => [name, new Node("div")]));
  for (const item of Object.values(elements)) body.append(item);
  elements.codingChanges.hidden = true; elements.prompt.value = "Privater unveränderter Entwurf";
  const state = { activeSessionId: "session-a", messages: [], speechStatus: { active: false }, speechProgress: {}, microphone: {},
    documents: [], attachments: [], pendingDocumentImports: [], chatMode: "general", messageRunStatus: new Map(), selectedToolAction: null,
    selectedExtensionActionId: null, isRunning: false, isAiBusy: false, activeActionIds: new Set() };
  const context = vm.createContext({ document, state, elements, URL, setTimeout() {},
    missumBridge: { isLanBrowser: true, clientId: "client-a" },
    post: (type, payload) => { posts.push({ type, payload }); return `request-${posts.length}`; },
    renderMessages: () => calls.messages++, renderContext: () => calls.context++, renderStatus: () => calls.status++,
    renderMicrophone() {}, clearSpeechHighlight: () => calls.highlights++, applySpeechHighlight: () => calls.highlights++,
    clearCompletedOneShotToolAction() {}, syncVoiceCaptureSuspension() {}, showToast() {},
    renderCodingWorkspace() {}, renderDeepResearch() {}, renderScienceWorkbench() {},
    isAudioCaptureActive: () => false, isScreenClipActive: () => false, ensureEditableContext: () => true,
    createToolIcon: () => new Node("svg"), availableActionDescriptors: () => state.actionDescriptors || [],
    toolVisuals: { textToSpeech: ["Vorlesen", "speech"], webSearch: ["Websuche", "web"] }, actionIcons: { speech: "speech", extension: "extension" } });
  for (const name of icons) {
    const declaration = app.match(new RegExp(`^  const ${name} = ([^\\n]+);$`, "m"));
    assert.ok(declaration, `static icon ${name} exists`);
    vm.runInContext(`globalThis.${name} = ${declaration[1]};`, context); allowedIcons.add(context[name]);
  }
  for (const name of ["createMessageIconAction", "createMessageFooterLink", "flashMessageAction", "isMessageSpeechActive",
    "updateMessageSpeechFooter", "createMessageFooter", "updateContextStripVisibility", "renderSpeechStatus", "updateSpeechControlIdentity", "updateSpeechProgress", "handleHostMessage"])
    vm.runInContext(extract(name), context, { filename: `app.js:${name}` });
  vm.runInContext(fs.readFileSync(path.join(root, "coding-timeline.js"), "utf8"), context, { filename: "coding-timeline.js" });
  const renderContext = extract("renderContext");
  const make = (extra = {}) => {
    const message = { id: "answer-a", sessionId: "session-a", role: "assistant", status: "completed", content: "Eine verständliche Antwort.", ...extra };
    state.messages.push(message);
    const article = new Node("article"); article.dataset.messageId = message.id; article.className = `message ${message.role}`;
    const footer = context.createMessageFooter(message, article); article.append(footer); elements.messageList.append(article);
    return { message, article, footer, read: () => footer.querySelector(".message-action--speech"),
      pause: () => footer.querySelector(".message-action--speech-pause"), status: () => footer.querySelector(".message-speech-status") };
  };
  const dispatch = (type, payload) => context.handleHostMessage({ detail: { type, payload } });
  const progress = (extra = {}) => ({ playbackId: "playback-1", eventSequence: 1, sessionId: "session-a", sourceMessageId: "answer-a", sourceKind: "message", ownerClientId: "client-a", sourceUnits: [], sourceUnitIds: [], state: "buffering", ...extra });
  const activate = extra => { state.speechStatus = { active: true, status: "Vorlesen", detail: "F5" }; state.microphone = { isSpeaking: true, canPauseSpeech: true, isSpeechPaused: false, status: "Vorlesen" }; dispatch("speech.progress", progress(extra)); };
  return { context, document, elements, state, posts, calls, make, dispatch, progress, activate,
    renderChips() { vm.runInContext(renderContext, context); context.renderContext(); } };
}

test("speech composer DOM and bindings are removed and native message actions retain their 28px footprint", () => {
  const html = fs.readFileSync(path.join(root, "index.html"), "utf8"), css = fs.readFileSync(path.join(root, "browser-panels.css"), "utf8");
  assert.doesNotMatch(html, /composer-speech-/); assert.doesNotMatch(app, /composer-speech-|elements\.composerSpeech/);
  assert.match(css, /\.message-action\s*\{[^}]*width:\s*28px;[^}]*height:\s*28px;/);
  assert.match(css, /\.message-action--speech-pause\[hidden\]/);
});

test("read-aloud selection and active playback never produce a composer chip or an empty upper strip", () => {
  const h = harness(); h.state.speechStatus.active = true;
  for (const scenario of [
    { selectedToolAction: "textToSpeech", selectedExtensionActionId: null, actionDescriptors: [] },
    { selectedToolAction: null, selectedExtensionActionId: "builtin.speech/read-aloud", actionDescriptors: [{ actionId: "builtin.speech/read-aloud", displayName: "Vorlesen", iconKey: "speech" }] },
    { selectedToolAction: "textToSpeech", selectedExtensionActionId: "custom.read-aloud", actionDescriptors: [{ actionId: "custom.read-aloud", displayName: "Vorlesen", toolAction: "textToSpeech", iconKey: "speech" }] }
  ]) {
    Object.assign(h.state, scenario); h.renderChips();
    assert.equal(h.elements.activeTools.children.length, 0); assert.equal(h.elements.contextStrip.hidden, true);
  }
});

test("explicit source session and message IDs select only the owning footer even when source units are not yet available", () => {
  const h = harness(), source = h.make(), other = h.make({ id: "other-answer" }), foreign = h.make({ sessionId: "session-b" });
  h.activate();
  assert.equal(source.read().getAttribute("aria-label"), "Vorlesen beenden"); assert.equal(source.pause().hidden, false);
  assert.equal(other.read().getAttribute("aria-label"), "Nachricht vorlesen"); assert.equal(other.pause().hidden, true);
  assert.equal(foreign.read().getAttribute("aria-label"), "Nachricht vorlesen"); assert.equal(foreign.pause().hidden, true);
  assert.equal(h.context.isMessageSpeechActive("answer-a", "session-a"), true);
  assert.equal(h.context.isMessageSpeechActive("answer-a", "session-b"), false);
});

test("global speaking state and text similarity cannot activate an unrelated or ownerless footer", () => {
  const h = harness(), source = h.make(); h.state.speechStatus.active = true; h.state.microphone.isSpeaking = true;
  for (const speechProgress of [{}, { sessionId: "session-a" }, { sourceMessageId: "answer-a" }, { sessionId: "other", sourceMessageId: "answer-a", state: "playing" }]) {
    h.state.speechProgress = speechProgress; h.context.renderSpeechStatus();
    assert.equal(source.read().getAttribute("aria-label"), "Nachricht vorlesen"); assert.equal(source.pause().hidden, true);
  }
});

test("footer read, pause, resume and stop dispatch their source-specific or playback commands without an AI run", async () => {
  const h = harness(), source = h.make();
  await source.read().dispatch("click"); assert.deepEqual(JSON.parse(JSON.stringify(h.posts.at(-1))), { type: "microphone.speak", payload: { sessionId: "session-a", messageId: "answer-a", text: source.message.content } });
  h.activate(); await source.pause().dispatch("click");
  assert.deepEqual(JSON.parse(JSON.stringify(h.posts.at(-1))), { type: "microphone.toggleSpeechPause", payload: { sessionId: "session-a", messageId: "answer-a", playbackId: "playback-1" } });
  h.dispatch("microphone.changed", { isSpeaking: true, canPauseSpeech: true, isSpeechPaused: true, status: "Pausiert" });
  assert.equal(source.pause().getAttribute("aria-label"), "Vorlesen fortsetzen"); assert.equal(source.pause().getAttribute("aria-pressed"), "true");
  assert.equal(source.status().textContent, "Vorlesen pausiert");
  await source.pause().dispatch("click"); assert.equal(h.posts.at(-1).type, "microphone.toggleSpeechPause");
  await source.read().dispatch("click");
  assert.deepEqual(JSON.parse(JSON.stringify(h.posts.at(-1))), { type: "microphone.stopSpeech", payload: { sessionId: "session-a", messageId: "answer-a", playbackId: "playback-1" } });
  assert.equal(h.posts.some(command => /^(chat\.|session\.|settings\.)/.test(command.type)), false);
});

test("speech progress and repeated microphone updates preserve focused controls and never recreate the conversation", () => {
  const h = harness(), source = h.make(); h.activate(); const pause = source.pause(), read = source.read(), status = source.status(); pause.focus();
  for (let sequence = 2; sequence <= 6; sequence++) {
    h.dispatch("speech.progress", h.progress({ eventSequence: sequence, state: "playing", sourceUnitIds: [`unit-${sequence}`] }));
    h.dispatch("microphone.changed", { isSpeaking: true, canPauseSpeech: true, isSpeechPaused: sequence % 2 === 0, status: "Vorlesen" });
    assert.equal(source.pause(), pause); assert.equal(source.read(), read); assert.equal(source.status(), status);
    assert.equal(h.document.activeElement, pause); assert.equal(pause.isConnected, true);
  }
  assert.equal(h.calls.messages, 0); assert.equal(h.calls.context, 0); assert.equal(h.elements.prompt.value, "Privater unveränderter Entwurf");
});

test("copy and read-aloud remain visible while the answer streams, including automatic playback", () => {
  const h = harness(), source = h.make({ status: "streaming", content: "Die Antwort wächst." });
  assert.equal(source.footer.hidden, false); assert.ok(source.read()); assert.ok(source.pause()); assert.ok(source.status());
  assert.equal(source.read().disabled, false); assert.equal(source.pause().hidden, true);
  h.activate(); assert.equal(source.footer.hidden, false);
  assert.equal(source.read().getAttribute("aria-label"), "Vorlesen beenden"); assert.equal(source.read().disabled, false);
  assert.equal(source.pause().hidden, false); assert.equal(h.calls.messages, 0);
});

test("an empty pending answer keeps its footer and enables read-aloud when its first text arrives", async () => {
  const h = harness(), source = h.make({ status: "pending", content: "" });
  const read = source.read(), pause = source.pause(), copy = source.footer.firstChild;
  assert.equal(source.footer.hidden, false); assert.equal(read.disabled, true); assert.equal(pause.hidden, true);
  await read.dispatch("click"); assert.equal(h.posts.length, 0);
  const updated = { ...source.message, status: "streaming", content: "Erster Satz." };
  h.state.messages = [updated];
  const next = h.context.createMessageFooter(updated, source.article, source.footer);
  const retained = h.context.missumCodingTimeline.reconcile(source.footer, next);
  assert.equal(retained, source.footer); assert.equal(source.read(), read); assert.equal(source.pause(), pause);
  assert.equal(source.footer.firstChild, copy); assert.equal(read.disabled, false);
  await read.dispatch("click"); assert.equal(h.posts.at(-1).payload.text, "Erster Satz.");
});

test("streaming message rerenders preserve focused playback controls and copy the newest text", async () => {
  const h = harness(), source = h.make({ status: "streaming", content: "Anfang." }); h.activate();
  const read = source.read(), pause = source.pause(), status = source.status(), copy = source.footer.firstChild;
  pause.focus(); pause.classList.add("hover-probe");
  for (let revision = 1; revision <= 4; revision++) {
    const updated = { ...source.message, content: `Anfang. Neuer Abschnitt ${revision}.`, status: revision === 4 ? "completed" : "streaming" };
    h.state.messages = [updated];
    const next = h.context.createMessageFooter(updated, source.article, source.footer);
    assert.equal(source.footer.parentNode, source.article, "constructing the next render does not detach the live footer");
    assert.equal(h.context.missumCodingTimeline.reconcile(source.footer, next), source.footer);
    assert.equal(source.footer.firstChild, copy); assert.equal(source.read(), read); assert.equal(source.pause(), pause); assert.equal(source.status(), status);
    assert.equal(h.document.activeElement, pause); assert.equal(pause.isConnected, true); assert.equal(pause.classList.contains("hover-probe"), true);
    await copy.dispatch("click"); assert.equal(h.posts.at(-1).payload.text, updated.content);
  }
  h.dispatch("speech.status", { active: false, ownerClientId: "client-a", playbackId: "playback-1" });
  await read.dispatch("click"); assert.equal(h.posts.at(-1).type, "microphone.speak"); assert.equal(h.posts.at(-1).payload.text, "Anfang. Neuer Abschnitt 4.");
  assert.equal(h.elements.prompt.value, "Privater unveränderter Entwurf");
});

test("automatic reset and status bind buffering playback to its footer before any audio progress arrives", async () => {
  const h = harness(), source = h.make({ status: "streaming", content: "Der erste Satz." }), other = h.make({ id: "other-answer" });
  const identity = { playbackId: "auto-playback", sessionId: "session-a", sourceMessageId: "answer-a", ownerClientId: "client-a" };
  h.dispatch("speech.reset", identity);
  h.dispatch("microphone.changed", { isSpeaking: true, canPauseSpeech: true, isSpeechPaused: false, status: "Spricht" });
  const messagesBeforeStatus = h.calls.messages;
  h.dispatch("speech.status", { ...identity, active: true, status: "Antwort wird fortlaufend vorgelesen" });
  assert.equal(source.read().getAttribute("aria-label"), "Vorlesen beenden"); assert.equal(source.pause().hidden, false);
  assert.equal(other.pause().hidden, true); assert.equal(h.state.speechProgress.eventSequence, 0); assert.equal(h.calls.messages, messagesBeforeStatus);
  await source.pause().dispatch("click");
  assert.deepEqual(JSON.parse(JSON.stringify(h.posts.at(-1).payload)), { sessionId: "session-a", messageId: "answer-a", playbackId: "auto-playback" });
  await source.read().dispatch("click"); assert.equal(h.posts.at(-1).type, "microphone.stopSpeech");
});

test("foreign client speech events cannot select or stop another client's footer", () => {
  const h = harness(), source = h.make(), other = h.make({ id: "other-answer" }); h.activate();
  const before = h.state.speechProgress;
  const foreign = { playbackId: "foreign", sessionId: "session-a", sourceMessageId: "other-answer", ownerClientId: "client-b" };
  h.dispatch("speech.reset", foreign); h.dispatch("speech.status", { ...foreign, active: true });
  h.dispatch("speech.progress", h.progress({ ...foreign, eventSequence: 100, state: "buffering" }));
  h.dispatch("speech.status", { ...foreign, active: false });
  assert.equal(h.state.speechProgress, before); assert.equal(h.state.speechStatus.active, true);
  assert.equal(source.pause().hidden, false); assert.equal(other.pause().hidden, true);
  h.state.speechProgress = { ...before, ownerClientId: "client-b" }; h.context.renderSpeechStatus();
  assert.equal(source.pause().hidden, true);
});

test("native automatic chunk controls use their owning message and queue without inventing highlight source IDs", async () => {
  const h = harness(), source = h.make({ status: "streaming" }); h.context.missumBridge.isLanBrowser = false;
  h.activate({ ownerClientId: "desktop", sourceMessageId: null, controlMessageId: "answer-a", controlPlaybackId: "native-queue", playbackId: "native-chunk" });
  assert.equal(h.state.speechProgress.sourceMessageId, null); assert.equal(source.pause().hidden, false);
  await source.pause().dispatch("click");
  assert.deepEqual(JSON.parse(JSON.stringify(h.posts.at(-1).payload)), { sessionId: "session-a", messageId: "answer-a", playbackId: "native-queue" });
  await source.read().dispatch("click"); assert.equal(h.posts.at(-1).payload.playbackId, "native-queue");
  h.state.speechProgress.sourceMessageId = "answer-a";
  const omittedSource = h.progress({ ownerClientId: "desktop", controlMessageId: "answer-a", controlPlaybackId: "native-queue", playbackId: "native-chunk", eventSequence: 2, state: "playing" });
  delete omittedSource.sourceMessageId;
  h.dispatch("speech.progress", omittedSource);
  assert.equal(h.state.speechProgress.sourceMessageId, null, "WhenWritingNull DTOs cannot retain an unrelated highlight identity");
  assert.equal(source.pause().hidden, false);
});

test("terminal progress wins over a lagging active status and stale playback events cannot re-enable its controls", () => {
  const h = harness(), source = h.make(); h.activate();
  h.dispatch("speech.progress", h.progress({ eventSequence: 4, state: "completed" }));
  assert.equal(source.read().getAttribute("aria-label"), "Nachricht vorlesen"); assert.equal(source.pause().hidden, true);
  h.dispatch("speech.progress", h.progress({ eventSequence: 3, state: "playing" }));
  assert.equal(source.pause().hidden, true);
  h.dispatch("speech.progress", h.progress({ playbackId: "old-playback", eventSequence: 50, state: "playing" }));
  assert.equal(source.pause().hidden, true);
  h.dispatch("speech.status", { active: true, playbackId: "playback-1", sessionId: "session-a", sourceMessageId: "answer-a", ownerClientId: "client-a" });
  h.dispatch("speech.progress", h.progress({ eventSequence: 10, state: "playing" }));
  assert.equal(source.pause().hidden, true, "late active telemetry cannot revive a completed playback");
  h.dispatch("speech.status", { active: false, status: "Abgebrochen" });
  assert.equal(source.status().hidden, true); assert.equal(h.state.speechProgress.sourceMessageId, null);
  h.dispatch("speech.reset", { playbackId: "playback-2", sessionId: "session-a", sourceMessageId: "answer-a", ownerClientId: "client-a" });
  h.dispatch("speech.status", { active: true, playbackId: "playback-2", sessionId: "session-a", sourceMessageId: "answer-a", ownerClientId: "client-a" });
  assert.equal(source.pause().hidden, false, "an explicitly started new playback can use the same message again");
  h.dispatch("speech.status", { active: false, playbackId: "playback-1", ownerClientId: "client-a" });
  assert.equal(source.pause().hidden, false, "the old playback cannot terminate its replacement");
  h.dispatch("speech.status", { active: true, playbackId: "playback-1", sessionId: "session-a", sourceMessageId: "other-answer", ownerClientId: "client-a" });
  assert.equal(h.state.speechProgress.playbackId, "playback-2"); assert.equal(source.pause().hidden, false);
});

test("navigation and a recreated footer use playback truth instead of an optimistic paused state", () => {
  const h = harness(), source = h.make(); h.activate();
  h.state.activeSessionId = "session-b"; source.article.remove(); const foreign = h.make({ id: "other", sessionId: "session-b" });
  h.context.renderSpeechStatus(); assert.equal(foreign.pause().hidden, true);
  const returned = h.make({ id: "answer-a", sessionId: "session-a" });
  assert.equal(returned.pause().hidden, false); assert.equal(returned.pause().getAttribute("aria-label"), "Vorlesen pausieren");
  h.state.microphone.isSpeechPaused = true; h.state.speechProgress.state = "paused"; h.context.renderSpeechStatus();
  assert.equal(returned.pause().getAttribute("aria-label"), "Vorlesen fortsetzen");
  h.dispatch("speech.status", { active: false, status: "Abgebrochen" });
  h.state.microphone.isSpeechPaused = true; const reloaded = h.make({ id: "answer-a", sessionId: "session-a" });
  assert.equal(reloaded.pause().hidden, true); assert.equal(reloaded.read().getAttribute("aria-label"), "Nachricht vorlesen");
});
