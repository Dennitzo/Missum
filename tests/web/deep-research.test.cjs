const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");
const { TestNode } = require("./test-dom.cjs");

const webRoot = path.resolve(__dirname, "../../src/Missum.App/Assets/Web");
const source = fs.readFileSync(path.join(webRoot, "app.js"), "utf8");

function harness(storage = new Map()) {
  const state = { activeSessionId: "session-a", selectedExtensionActionId: null, persistentExtensionActionId: null,
    selectedToolAction: null, persistentToolAction: null, runQueue: { active: null, pending: [], queueDepth: 0, isIdle: true },
    chatMode: "general", deepResearch: false, deepResearchProfile: "auto", documents: [], attachments: [], messages: [], isRunning: false, isAiBusy: false,
    scientificResearch: { available: true, projects: [], selectedProjectId: null, detail: null, disabledReason: null } };
  const elements = Object.fromEntries(["activeTools", "toolsButton", "prompt", "contextStrip", "researchOverlay", "researchDisabledReason",
    "researchProjectList", "researchEmpty", "researchPreview", "researchProfile", "researchTitle", "researchStatus", "researchQuestion",
    "researchAutonomy", "researchVerification", "researchUpdated", "researchCheckpoint", "researchNodeList", "researchEdgeList",
    "researchHypothesisList", "researchClaimList", "researchVerificationList", "researchExperimentList",
    "researchWorkList", "researchEvidenceList", "researchReport",
    "researchCheckpointState"].map(key => [key, new TestNode("div")]));
  elements.prompt.value = "";
  const body = new TestNode("body");
  const menuItem = new TestNode("button");
  menuItem.dataset.toolToggle = "deepResearch";
  body.append(menuItem, elements.activeTools);
  const posts = [], notices = [];
  const noOp = () => {};
  const context = vm.createContext({ state, elements,
    document: { body, documentElement: { lang: "de" }, createElement: tag => new TestNode(tag),
      querySelectorAll: selector => body.querySelectorAll(selector), querySelector: selector => body.querySelector(selector) },
    localStorage: { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value), removeItem: key => storage.delete(key) },
    persistentToolActions: new Set(["audiobook"]),
    persistentExtensionActionIds: new Set(["builtin.audiobook/create"]),
    legacyExtensionActionIds: {
      imageAnalysis: "builtin.media/image-analysis",
      audiobook: "builtin.audiobook/create",
      deepResearch: "builtin.web/deep-research"
    },
    toolVisuals: { coding: ["Coding", "code"], webSearch: ["Websuche", "search"], imageAnalysis: ["Bild analysieren", "image"] },
    createToolIcon: () => new TestNode("svg"),
    isAudioCaptureActive: () => false, isScreenClipActive: () => false,
    renderStatus: noOp, renderMessages: noOp, chatScroll: { jump: noOp }, clearTimeout: noOp,
    renderScienceWorkbench: noOp,
    crypto: { randomUUID: () => "request-1" },
    post: (type, payload) => { posts.push({ type, payload }); return "sent"; },
    showToast: message => notices.push(message),
    setPromptValue: value => { elements.prompt.value = value; },
    availableActionDescriptors: () => [
      { actionId: "builtin.web/deep-research", toolToggle: "deepResearch" },
      { actionId: "builtin.media/image-analysis", toolAction: "imageAnalysis" }
    ]
  });
  context.renderContext = () => { elements.activeTools.replaceChildren(); context.renderDeepResearch(); };
  vm.runInContext("let draftTimer = 0; let pendingDraft = null;", context);
  for (const name of ["normalizeToolAction", "normalizeExtensionActionId", "extensionActionIdForToolAction", "toolActionForExtensionActionId",
    "normalizeScheduledRun", "normalizeRunQueue", "scheduledRunForSession", "ensureEditableContext", "readMigratedStorage", "persistDeepResearch", "restoreDeepResearch",
    "selectDeepResearch", "renderDeepResearch", "openScientificResearch", "closeScientificResearch", "renderScientificResearch",
    "selectToolAction", "clearCompletedOneShotToolAction", "postChatRequest", "submitPrompt", "updateContextStripVisibility", "timeLabel"]) {
    const start = source.search(new RegExp(`  (?:async )?function ${name}\\(`));
    const ending = source.slice(start).match(/\r?\n {2}\}(?:\r?\n|$)/);
    assert.ok(start >= 0 && ending, name);
    vm.runInContext(source.slice(start, start + ending.index + ending[0].length), context);
  }
  return { context, state, elements, menuItem, storage, posts, notices };
}

test("Deep Research adds a real request flag without replacing General or Coding mode, and deselection clears it", async () => {
  for (const mode of ["general", "coding"]) {
    const { context, state, elements, posts, menuItem } = harness();
    state.chatMode = mode;
    context.selectDeepResearch(true);
    assert.equal(posts.at(-1).type, "action.invoke");
    assert.equal(posts.at(-1).payload.actionId, "builtin.web/deep-research");
    assert.equal(posts.at(-1).payload.enabled, true);
    assert.equal(state.deepResearch, false, "selection changes only after native acknowledgement");
    context.selectDeepResearch(true, false);
    assert.equal(state.selectedToolAction, null);
    assert.equal(menuItem.getAttribute("aria-checked"), "true");
    assert.equal(menuItem.classList.contains("active"), true);
    assert.equal(elements.activeTools.querySelectorAll(".active-tool-chip").length, 0, "restored legacy research state does not create a non-native chip");
    context.updateContextStripVisibility();
    assert.equal(elements.contextStrip.hidden, true, "General research stays in the footer chip row without leaving an empty row above the prompt");
    elements.prompt.value = "Recherchiere das Thema mit mehreren Quellen.";
    await context.submitPrompt();
    assert.equal(posts.at(-1).type, "chat.send");
    assert.equal(posts.at(-1).payload.deepResearch, true);
    assert.equal(posts.at(-1).payload.extensionActionId, null);
    assert.equal(Object.hasOwn(posts.at(-1).payload, "toolAction"), false);
    assert.equal(posts.at(-1).payload.chatMode, mode);
    assert.equal(posts.at(-1).payload.sessionId, "session-a");
    state.pendingChatSend = null;
    context.selectDeepResearch(false);
    assert.equal(posts.at(-1).type, "action.invoke");
    assert.equal(posts.at(-1).payload.enabled, false);
    context.selectDeepResearch(false, false);
    assert.equal(menuItem.getAttribute("aria-checked"), "false");
    assert.equal(elements.activeTools.children.length, 0);
    elements.prompt.value = "Beantworte jetzt eine normale Frage.";
    await context.submitPrompt();
    assert.equal(posts.at(-1).payload.deepResearch, false);
    assert.equal(posts.at(-1).payload.extensionActionId, null);
  }
});

test("legacy research state remains readable without the old profile and Stand chip", () => {
  const { context, elements, posts } = harness();
  context.selectDeepResearch(true, false);
  assert.equal(elements.activeTools.children.length, 0);
  context.openScientificResearch();
  assert.equal(posts.at(-1).type, "research.list");
  assert.equal(posts.at(-1).payload.sessionId, "session-a");
  const html = fs.readFileSync(path.join(webRoot, "index.html"), "utf8");
  assert.doesNotMatch(html, /id="research-overlay"|Forschungsstand öffnen/);
  for (const view of ["overview", "research", "data", "analysis", "figures", "manuscript", "review", "provenance"])
    assert.match(html, new RegExp(`data-science-view="${view}"`));
});

test("research selection is scoped to its session and survives tab recreation without leaking", () => {
  const first = harness();
  first.context.selectDeepResearch(true, false);
  first.state.activeSessionId = "session-b";
  first.context.restoreDeepResearch();
  assert.equal(first.state.deepResearch, false);
  first.state.activeSessionId = "session-a";
  first.context.restoreDeepResearch();
  assert.equal(first.state.deepResearch, true);
  const restored = harness(first.storage);
  restored.context.restoreDeepResearch();
  assert.equal(restored.state.deepResearch, true);
  restored.context.selectDeepResearch(false, false);
  const reopened = harness(restored.storage);
  reopened.context.restoreDeepResearch();
  assert.equal(reopened.state.deepResearch, false);
});

test("completed media selection clears research while Coding remains a chat mode", () => {
  const { context, state } = harness();
  state.chatMode = "coding";
  context.selectDeepResearch(true, false);
  context.selectToolAction("coding", false);
  assert.equal(state.selectedToolAction, null, "Coding is never represented as a tool selection");
  assert.equal(state.chatMode, "coding");
  assert.equal(state.deepResearch, true);
  context.selectToolAction("imageAnalysis", false);
  assert.equal(state.deepResearch, false);
  context.selectToolAction(null, false);
  context.selectDeepResearch(true, false);
  assert.equal(state.selectedToolAction, null);
  assert.equal(state.deepResearch, true);
});

test("legacy research profile remains session-scoped, persisted, and sent with the existing action", async () => {
  const first = harness();
  first.context.selectDeepResearch(true, false);
  assert.equal(first.elements.activeTools.querySelector("select"), null);
  first.state.deepResearchProfile = "mathematicalInvestigation";
  first.context.persistDeepResearch();
  assert.equal(first.state.deepResearchProfile, "mathematicalInvestigation");
  first.elements.prompt.value = "Untersuche die Gleichung und prüfe Gegenbeispiele.";
  await first.context.submitPrompt();
  assert.equal(first.posts.at(-1).payload.deepResearchProfile, "mathematicalInvestigation");

  const reopened = harness(first.storage);
  reopened.context.restoreDeepResearch();
  assert.equal(reopened.state.deepResearchProfile, "mathematicalInvestigation");
  reopened.state.activeSessionId = "session-b";
  reopened.context.restoreDeepResearch();
  assert.equal(reopened.state.deepResearchProfile, "auto");
});

test("selected tools send only their canonical extension action ID", async () => {
  const { context, state, elements, posts } = harness();
  context.selectToolAction("imageAnalysis", false);
  assert.equal(state.selectedExtensionActionId, "builtin.media/image-analysis");
  elements.prompt.value = "Untersuche den vorhandenen Bildinhalt.";
  await context.submitPrompt();
  const request = posts.at(-1);
  assert.equal(request.type, "chat.send");
  assert.equal(request.payload.extensionActionId, "builtin.media/image-analysis");
  assert.equal(Object.hasOwn(request.payload, "toolAction"), false);
});

test("the running request cannot be silently changed by toggling research", () => {
  const { context, state, notices } = harness();
  state.isRunning = true;
  context.selectDeepResearch(true);
  assert.equal(state.deepResearch, false);
  assert.equal(notices.length, 1);
  state.isRunning = false;
  context.selectDeepResearch(true);
  assert.equal(state.deepResearch, false);
  context.selectDeepResearch(true, false);
  state.pendingChatSend = { requestId: "sending" };
  context.selectDeepResearch(false);
  assert.equal(state.deepResearch, true);
});

test("the data-driven action catalog keeps Deep Research independent from chat mode", () => {
  const html = fs.readFileSync(path.join(webRoot, "index.html"), "utf8");
  assert.doesNotMatch(source, /const defaultActionDescriptors/);
  assert.match(source, /availableActionDescriptors\(\)\.find\(item => item\.actionId === actionId\)/);
  assert.match(html, /id="tools-menu-content" class="action-menu__content"/);
});

test("a browser-started prompt waits for its shared reasoning acknowledgement and sends once", async () => {
  const { context, state, elements, posts } = harness();
  let saved;
  context.missumReasoningSelection = { commitForSubmission: () => new Promise(resolve => { saved = resolve; }) };
  elements.prompt.value = "Beweise die Behauptung.";
  const sending = context.submitPrompt();
  assert.equal(state.reasoningSubmissionPending, true);
  await context.submitPrompt();
  assert.equal(posts.some(item => item.type === "chat.send"), false);
  saved(); await sending;
  assert.equal(state.reasoningSubmissionPending, false);
  assert.equal(posts.filter(item => item.type === "chat.send").length, 1);
});

test("failed reasoning saves or navigation while saving retain the local prompt without starting a job", async () => {
  for (const changed of ["error", "session", "draft", "run"]) {
    const { context, state, elements, posts } = harness();
    let saved, failed;
    context.missumReasoningSelection = { commitForSubmission: () => new Promise((resolve, reject) => { saved = resolve; failed = reject; }) };
    elements.prompt.value = "Mein neuer Auftrag";
    const sending = context.submitPrompt();
    if (changed === "error") failed(new Error("Reasoning konnte nicht gespeichert werden."));
    else {
      if (changed === "session") state.activeSessionId = "session-b";
      if (changed === "draft") elements.prompt.value = "Bearbeiteter Entwurf";
      if (changed === "run") state.isRunning = true;
      saved();
    }
    await sending;
    assert.equal(state.reasoningSubmissionPending, false);
    assert.equal(posts.some(item => item.type === "chat.send"), false);
    assert.equal(elements.prompt.value, changed === "draft" ? "Bearbeiteter Entwurf" : "Mein neuer Auftrag");
  }
});
