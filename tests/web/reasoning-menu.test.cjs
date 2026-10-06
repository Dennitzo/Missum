const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const { TestNode } = require("./test-dom.cjs");
const file = "../../src/Missum.App/Assets/Web/reasoning-menu.js";
const { options, selection, modelCatalog } = require(file);

function harness() {
  const nodes = new Map(), documentEvents = new Map(), sent = [];
  const hostEvents = new Map(), timers = new Map(); let timerId = 0;
  const node = id => {
    if (!nodes.has(id)) {
      const item = new TestNode(id === "reasoning-range" ? "input" : ["model-picker", "local-model-effort"].includes(id) ? "select" : "button");
      item.focus = () => { document.activeElement = item; };
      nodes.set(id, item);
    }
    return nodes.get(id);
  };
  const document = { getElementById: node, activeElement: null,
    addEventListener: (type, handler) => documentEvents.set(type, handler), createElement: tag => {
      const item = new TestNode(tag);
      item.focus = () => { document.activeElement = item; };
      return item;
    } };
  node("reasoning-menu").hidden = true;
  const context = { document, missumBridge: { post(type, payload) {
    const id = String(sent.length + 1); sent.push({ type, payload, id }); return id;
  } }, addEventListener: (type, handler) => { hostEvents.set(type, handler); },
  setTimeout: (callback, delay) => { const id = ++timerId; timers.set(id, { callback, delay }); return id; },
  clearTimeout: id => timers.delete(id) };
  vm.runInNewContext(fs.readFileSync(require.resolve(file), "utf8"), context);
  const emit = (type, payload, requestId) => hostEvents.get("missum:host-message")({ detail: { type, payload, requestId } });
  const snapshot = (payload = {}, requestId = sent.at(-1)?.id) => emit("reasoning.snapshot", {
    modelId: "A", role: "coding", selected: "auto", defaultLevel: "high", levels: ["auto", "low", "high"], available: true, ...payload
  }, requestId);
  const catalog = (models = [
    { id: "A", name: "Modell A", reasoningEfforts: ["low", "high"], defaultReasoningEffort: "high" },
    { id: "B", name: "Modell B", reasoningEfforts: ["low", "medium", "high"], defaultReasoningEffort: "medium" }
  ]) => {
    node("model-picker").replaceChildren(...models.map(model => {
      const item = new TestNode("option"); item.value = model.id; item.textContent = model.name;
      item._missumModel = model; return item;
    }));
    hostEvents.get("missum:models-updated")();
  };
  return { node, document, sent, emit, snapshot, disconnect: () => hostEvents.get("missum:bridge-disconnected")(),
    catalog, outsideClick: () => documentEvents.get("click")(), timers,
    commit: () => context.missumReasoningSelection.commitForSubmission(),
    fireTimers: () => { for (const [id, timer] of [...timers]) { timers.delete(id); timer.callback(); } },
    key: key => { let prevented = false; documentEvents.get("keydown")({ key, preventDefault() { prevented = true; } }); return prevented; } };
}

test("reconnect clears lost reasoning get/set acknowledgements and reloads the same model's authoritative choices", async () => {
  const h = harness(), state = { reasoningModelId: "A", reasoningRole: "coding" };
  h.emit("state.snapshot", state); const lostGet = h.sent.at(-1).id;
  h.disconnect(); h.emit("state.snapshot", state);
  assert.equal(h.sent.at(-1).type, "reasoning.get"); assert.notEqual(h.sent.at(-1).id, lostGet);
  h.snapshot({ selected: "low" }, lostGet); assert.equal(h.node("reasoning-button").disabled, true);
  h.snapshot({ selected: "high" }); assert.equal(h.node("reasoning-button").disabled, false);
  await h.node("reasoning-button").dispatch("click"); h.snapshot({ selected: "high" });
  const beforeDraft = h.sent.length;
  await h.node("reasoning-options").children[0].dispatch("click");
  assert.equal(h.sent.length, beforeDraft, "a dot changes the local draft until the popup closes");
  h.key("Escape"); const lostSet = h.sent.at(-1).id;
  assert.equal(h.sent.at(-1).type, "reasoning.set");
  h.disconnect(); h.emit("state.snapshot", state);
  assert.equal(h.sent.at(-1).type, "reasoning.get", "a lost write is reconciled through a fresh read");
  h.snapshot({ selected: "high" }, lostSet); assert.equal(h.node("reasoning-button").disabled, true);
  h.snapshot({ selected: "low" });
  assert.equal(h.node("reasoning-button").disabled, false); assert.equal(h.node("reasoning-button").title, "Reasoning: Niedrig");
  assert.equal(h.sent.filter(request => request.type === "reasoning.set").length, 1);
});

test("only explicit supported levels are offered, including when old capabilities contain auto", () => {
  assert.deepEqual(options({}), []);
  assert.deepEqual(options({ levels: ["auto", " AUTO ", "low", "future_level", "low", "<script>", null] }).map(item => item.value), ["low", "future_level"]);
  assert.equal(selection({ selected: "auto", defaultLevel: "high", levels: ["auto", "low", "high"] }), "high");
  assert.equal(selection({ selected: "low", defaultLevel: "high", levels: ["low", "high"] }), "low");
  assert.equal(selection({ selected: "unavailable", defaultLevel: "auto", levels: ["auto", "medium"] }), "medium");
  assert.equal(selection({ selected: "auto", defaultLevel: "auto", levels: ["auto"] }), null);
});

test("model switches reject stale responses and opening the menu survives its actual refresh roundtrip", async () => {
  const h = harness();
  h.emit("state.snapshot", { reasoningModelId: "A", reasoningRole: "coding" });
  h.emit("state.snapshot", { reasoningModelId: "B", reasoningRole: "coding" });
  h.snapshot({ selected: "high" }, "1");
  assert.equal(h.node("reasoning-button").disabled, true);
  assert.equal(h.node("reasoning-options").children.length, 0);
  h.snapshot({ modelId: "B" }, "2");
  assert.equal(h.node("reasoning-button").title, "Reasoning: Hoch");
  assert.equal(h.node("reasoning-options").children.length, 2);
  await h.node("reasoning-button").dispatch("click");
  assert.equal(h.sent.at(-1).type, "reasoning.get");
  assert.equal(h.node("reasoning-menu").hidden, false, "refreshing an opened menu must not close it");
  assert.equal(h.node("reasoning-options").children.every(item => item.disabled), true);
  h.snapshot({ modelId: "B" });
  assert.equal(h.node("reasoning-menu").hidden, false);
  assert.equal(h.document.activeElement, h.node("reasoning-range"));
  const beforeDraft = h.sent.length;
  await h.node("reasoning-options").children[0].dispatch("click");
  assert.equal(h.sent.length, beforeDraft);
  assert.equal(h.node("reasoning-menu").hidden, false);
  h.outsideClick();
  assert.equal(h.sent.at(-1).type, "reasoning.set");
  assert.deepEqual(JSON.parse(JSON.stringify(h.sent.at(-1).payload)), { modelId: "B", role: "coding", effort: "low" });
  assert.equal(h.node("reasoning-button").title, "Reasoning: Hoch", "the displayed selection waits for confirmation");
  h.snapshot({ modelId: "B", selected: "low" });
  assert.equal(h.node("reasoning-menu").hidden, true);
  assert.equal(h.node("reasoning-button").title, "Reasoning: Niedrig");
  assert.equal(h.node("reasoning-button").attributes["aria-label"], "Reasoning: Niedrig");
  h.emit("chat.started", {});
  assert.equal(h.node("reasoning-button").disabled, true);
  h.emit("chat.completed", {});
  assert.equal(h.node("reasoning-button").disabled, false);
});

test("keyboard navigation contains no automatic item and cannot choose while a request is pending", async () => {
  const h = harness();
  h.emit("state.snapshot", { reasoningModelId: "A", reasoningRole: "coding" });
  h.snapshot({ levels: ["auto", "low", "medium", "high"] });
  await h.node("reasoning-button").dispatch("click");
  const sentBefore = h.sent.length;
  await h.node("reasoning-options").children[0].dispatch("click");
  h.key("ArrowDown");
  assert.equal(h.sent.length, sentBefore);
  h.snapshot({ levels: ["auto", "low", "medium", "high"] });
  assert.equal(h.document.activeElement, h.node("reasoning-range"));
  assert.equal(h.key("ArrowDown"), false, "range arrow keys keep their native browser behavior");
  assert.equal(h.document.activeElement, h.node("reasoning-range"));
  h.node("reasoning-options").children[0].focus();
  h.key("Home");
  assert.equal(h.document.activeElement.textContent, "Niedrig");
  h.key("ArrowDown");
  assert.equal(h.document.activeElement.textContent, "Mittel");
  h.key("End");
  assert.equal(h.document.activeElement.textContent, "Hoch");
  h.key("ArrowDown");
  assert.equal(h.document.activeElement.textContent, "Niedrig");
  assert.equal(h.node("reasoning-options").textContent.includes("Automatisch"), false);
  h.key("Escape");
  assert.equal(h.node("reasoning-menu").hidden, true);
  assert.equal(h.document.activeElement, h.node("reasoning-button"));
});

test("models without explicit levels expose no fallback choice and the button is disabled", async () => {
  for (const profile of [{ levels: [] }, { levels: ["auto"], defaultLevel: "auto" }, { levels: [], available: false }]) {
    const h = harness();
    h.emit("state.snapshot", { reasoningModelId: "A", reasoningRole: "coding" });
    h.snapshot(profile);
    assert.equal(h.node("reasoning-button").disabled, true);
    assert.equal(h.node("reasoning-options").children.length, 0);
    assert.equal(h.node("reasoning-button").title.includes("Aus"), false);
    await h.node("reasoning-button").dispatch("click");
    h.key("Enter");
    assert.equal(h.sent.length, 1);
    assert.equal(h.node("reasoning-menu").hidden, true);
  }
});

test("metadata errors clear the choices and a later same-model snapshot can recover without reintroducing auto", () => {
  const h = harness();
  h.emit("state.snapshot", { reasoningModelId: "A", reasoningRole: "coding" });
  h.emit("host.error", { message: "Fixture connection lost" }, "1");
  assert.equal(h.node("reasoning-button").disabled, true);
  assert.equal(h.node("reasoning-options").children.length, 0);
  h.emit("state.snapshot", { reasoningModelId: "A", reasoningRole: "coding" });
  h.snapshot({ selected: "auto", defaultLevel: "on", levels: ["auto", "on"] });
  assert.equal(h.node("reasoning-button").disabled, false);
  assert.equal(h.node("reasoning-button").title, "Reasoning: An");
  assert.deepEqual(h.node("reasoning-options").children.map(item => item.textContent), ["An"]);
});

test("a completed run refreshes reasoning after the native runtime recovered", () => {
  const h = harness();
  h.emit("state.snapshot", { reasoningModelId: "A", reasoningRole: "general" });
  h.emit("host.error", { message: "Runtime wird gestartet" }, "1");
  const sentBefore = h.sent.length;

  h.emit("chat.started", {});
  h.emit("chat.completed", {});

  assert.equal(h.sent.length, sentBefore + 1);
  assert.equal(h.sent.at(-1).type, "reasoning.get");
  assert.deepEqual(JSON.parse(JSON.stringify(h.sent.at(-1).payload)), { modelId: "A", role: "general" });
});

test("shared PC reasoning broadcasts update the same selected model without accepting a different model or role", () => {
  const h = harness();
  h.emit("state.snapshot", { reasoningModelId: "A", reasoningRole: "coding" });
  h.snapshot({ selected: "low" });
  const count = h.sent.length;
  h.emit("reasoning.snapshot", { modelId: "A", role: "coding", selected: "high", levels: ["low", "high"], available: true });
  assert.equal(h.node("reasoning-button").title, "Reasoning: Hoch");
  assert.equal(h.sent.length, count, "a server broadcast does not create a second settings write");
  for (const data of [{ modelId: "B", role: "coding" }, { modelId: "A", role: "general" }])
    h.emit("reasoning.snapshot", { ...data, selected: "low", levels: ["low", "high"], available: true });
  assert.equal(h.node("reasoning-button").title, "Reasoning: Hoch");
});

test("another client's chat activity does not enable or disable this session's model selection", () => {
  const h = harness();
  h.emit("state.snapshot", { reasoningModelId: "A", reasoningRole: "coding", activeSessionId: "session-a" });
  h.snapshot();
  h.emit("chat.started", { sessionId: "session-b" });
  assert.equal(h.node("reasoning-button").disabled, false);
  h.emit("chat.started", { sessionId: "session-a" });
  assert.equal(h.node("reasoning-button").disabled, true);
  h.emit("chat.completed", { sessionId: "session-b" });
  assert.equal(h.node("reasoning-button").disabled, true);
  h.emit("chat.completed", { sessionId: "session-a" });
  assert.equal(h.node("reasoning-button").disabled, false);
});

test("the model catalog keeps installed chat models and prefers metadata for the current role", () => {
  const items = [
    { id: "A", role: "general", name: "General" },
    { id: "A", role: "coding", name: "Coding" },
    { id: "B", role: "general", downloaded: false },
    { id: "vision", role: "vision" },
    { modelId: "C", name: "Role-independent" }
  ];
  assert.deepEqual(modelCatalog({ models: items }, "coding").map(item => item.name), ["Coding", "Role-independent"]);
  assert.deepEqual(modelCatalog({ items }, "general").map(item => item.name), ["General", "Role-independent"]);
  assert.deepEqual(modelCatalog([]), []);
});

test("slider and dots edit a shared draft and commit once when the popup closes", async () => {
  const h = harness();
  h.emit("state.snapshot", { reasoningModelId: "A", reasoningRole: "coding" }); h.snapshot({ selected: "high", levels: ["low", "medium", "high"] });
  await h.node("reasoning-button").dispatch("click"); h.snapshot({ selected: "high", levels: ["low", "medium", "high"] });
  const before = h.sent.length;
  h.node("reasoning-range").value = "1"; await h.node("reasoning-range").dispatch("input");
  assert.equal(h.node("reasoning-title").textContent, "Reasoning: Mittel");
  assert.equal(h.node("reasoning-range").getAttribute("aria-valuetext"), "Mittel");
  assert.equal(h.node("reasoning-options").children[1].getAttribute("aria-checked"), "true");
  assert.equal(h.node("selected-reasoning-label").textContent, "Hoch", "the authoritative pill stays unchanged while editing");
  await h.node("reasoning-options").children[0].dispatch("click");
  assert.equal(h.node("reasoning-range").value, "0");
  assert.equal(h.sent.length, before);
  h.key("Escape");
  assert.equal(h.sent.length, before + 1);
  assert.equal(h.sent.at(-1).type, "reasoning.set");
  assert.deepEqual(JSON.parse(JSON.stringify(h.sent.at(-1).payload)), { modelId: "A", role: "coding", effort: "low" });
  h.outsideClick(); h.key("Escape");
  assert.equal(h.sent.length, before + 1, "repeated closing cannot replay the draft");
  h.snapshot({ selected: "low", levels: ["low", "medium", "high"] });
  assert.equal(h.node("selected-reasoning-label").textContent, "Niedrig");
  await h.node("reasoning-button").dispatch("click"); h.snapshot({ selected: "low", levels: ["low", "medium", "high"] });
  await h.node("reasoning-options").children[0].dispatch("click"); h.outsideClick();
  assert.equal(h.sent.filter(item => item.type === "reasoning.set").length, 1, "closing an unchanged draft makes no write");
});

test("running, model changes and disconnect discard an uncommitted reasoning draft", async () => {
  for (const transition of ["running", "model", "disconnect"]) {
    const h = harness();
    h.emit("state.snapshot", { reasoningModelId: "A", reasoningRole: "coding" }); h.snapshot();
    await h.node("reasoning-button").dispatch("click"); h.snapshot();
    await h.node("reasoning-options").children[0].dispatch("click");
    if (transition === "running") h.emit("chat.started", {});
    else if (transition === "model") h.emit("state.snapshot", { reasoningModelId: "B", reasoningRole: "coding" });
    else h.disconnect();
    h.outsideClick(); h.key("Escape");
    assert.equal(h.node("reasoning-menu").hidden, true, transition);
    assert.equal(h.sent.some(item => item.type === "reasoning.set"), false, transition);
  }
});

async function openModelDraft(h, model = "B") {
  h.catalog();
  h.emit("state.snapshot", { reasoningModelId: "A", reasoningRole: "coding" }); h.snapshot();
  await h.node("open-model-dialog").dispatch("click");
  h.node("model-picker").value = model; await h.node("model-picker").dispatch("change");
}

test("model dialog uses unsaved model metadata locally and Cancel preserves the current model and effort", async () => {
  for (const cancel of ["button", "Escape", "backdrop"]) {
    const h = harness(); await openModelDraft(h);
    assert.equal(h.document.activeElement, h.node("model-picker"));
    assert.deepEqual(h.node("local-model-effort").children.map(item => item.value), ["low", "medium", "high"]);
    assert.equal(h.node("local-model-effort").value, "medium");
    h.node("local-model-effort").value = "low";
    const before = h.sent.length;
    if (cancel === "button") await h.node("cancel-local-model").dispatch("click");
    else if (cancel === "Escape") assert.equal(h.key("Escape"), true);
    else await h.node("local-model-overlay").dispatch("click");
    assert.equal(h.sent.length, before, "a canceled draft makes no model or reasoning write");
    assert.equal(h.sent.filter(item => item.type === "reasoning.get").length, 1, "unselected model choices come from the local catalog");
    assert.equal(h.node("local-model-overlay").hidden, true);
    assert.equal(h.node("model-picker").value, "A");
    assert.equal(h.node("selected-reasoning-label").textContent, "Hoch");
    assert.equal(h.document.activeElement, h.node("reasoning-button"));
    await h.node("open-model-dialog").dispatch("click");
    assert.equal(h.node("model-picker").value, "A");
    assert.equal(h.node("local-model-effort").value, "high");
  }
});

test("same-model host updates and catalog refresh preserve an open model dialog draft", async () => {
  const h = harness(); await openModelDraft(h);
  h.node("local-model-effort").value = "low";
  const before = h.sent.length;
  h.emit("state.snapshot", { reasoningModelId: "A", reasoningRole: "coding", isRunning: false });
  h.catalog();
  assert.equal(h.node("local-model-overlay").hidden, false);
  assert.equal(h.node("model-picker").value, "B");
  assert.equal(h.node("local-model-effort").value, "low");
  assert.equal(h.sent.length, before);
});

test("model Apply waits for its confirming selection snapshot before setting effort exactly once", async () => {
  const h = harness(); await openModelDraft(h);
  h.node("local-model-effort").value = "high";
  const before = h.sent.length;
  await h.node("apply-local-model").dispatch("click");
  const apply = h.sent.at(-1);
  assert.equal(h.sent.length, before + 1);
  assert.equal(apply.type, "models.select");
  assert.deepEqual(JSON.parse(JSON.stringify(apply.payload)), { modelId: "B" });
  assert.equal(h.node("local-model-overlay").hidden, true);
  h.emit("state.snapshot", { reasoningModelId: "A", reasoningRole: "coding" });
  h.emit("state.snapshot", { reasoningModelId: "B", reasoningRole: "coding" }, "unrelated-request");
  assert.equal(h.sent.some(item => item.type === "reasoning.set"), false, "only the Apply acknowledgement commits its effort");
  h.emit("state.snapshot", { reasoningModelId: "B", reasoningRole: "coding" }, apply.id);
  assert.equal(h.sent.at(-1).type, "reasoning.set");
  assert.deepEqual(JSON.parse(JSON.stringify(h.sent.at(-1).payload)), { modelId: "B", role: "coding", effort: "high" });
  const writeId = h.sent.at(-1).id;
  h.emit("state.snapshot", { reasoningModelId: "B", reasoningRole: "coding" }, apply.id);
  assert.equal(h.sent.filter(item => item.type === "reasoning.set").length, 1);
  h.snapshot({ modelId: "B", selected: "high" }, writeId);
  assert.equal(h.node("selected-model-label").textContent, "Modell B");
  assert.equal(h.node("selected-reasoning-label").textContent, "Hoch");
});

test("a rejected or disconnected model Apply cannot leak its effort into a later model change", async () => {
  for (const failure of ["host.error", "disconnect"]) {
    const h = harness(); await openModelDraft(h);
    h.node("local-model-effort").value = "low";
    await h.node("apply-local-model").dispatch("click");
    const apply = h.sent.at(-1);
    if (failure === "host.error") h.emit("host.error", { message: "Modell nicht verfügbar" }, apply.id);
    else h.disconnect();
    h.emit("state.snapshot", { reasoningModelId: "B", reasoningRole: "coding" }, apply.id);
    assert.equal(h.sent.some(item => item.type === "reasoning.set"), false, failure);
    assert.equal(h.sent.at(-1).type, "reasoning.get", "the independently selected model is read instead of receiving a stale draft");
  }
});

test("mode and chat navigation snapshots immediately update the reasoning role before opening its popup", async () => {
  const h = harness(); h.catalog(); h.emit("state.snapshot", { reasoningModelId: "A", reasoningRole: "general" }); h.snapshot({ role: "general", selected: "high" });
  h.emit("session.changed", { reasoningModelId: "A", reasoningRole: "coding", isRunning: false });
  assert.equal(h.sent.at(-1).payload.role, "coding"); h.snapshot({ selected: "low" });
  await h.node("reasoning-button").dispatch("click"); assert.equal(h.sent.at(-1).payload.role, "coding");
  h.snapshot({ selected: "low" }); assert.equal(h.node("reasoning-menu").hidden, false); assert.equal(h.node("reasoning-button").title, "Reasoning: Niedrig");
});
test("the combined model pill cannot reopen during Apply and is released even for models without reasoning choices", async () => {
  const h = harness(); await openModelDraft(h); await h.node("apply-local-model").dispatch("click"); const apply = h.sent.at(-1);
  assert.equal(h.node("reasoning-button").disabled, true); const before = h.sent.length; await h.node("reasoning-button").dispatch("click"); assert.equal(h.sent.length, before);
  h.emit("state.snapshot", { reasoningModelId: "B", reasoningRole: "coding" }, apply.id); h.snapshot({ modelId: "B", selected: "medium" }); assert.equal(h.node("reasoning-button").disabled, false);
  h.catalog([{ id: "B", name: "Modell B", reasoningEfforts: [], defaultReasoningEffort: null }]); h.snapshot({ modelId: "B", levels: [], available: false }, "ignore");
  await h.node("open-model-dialog").dispatch("click"); h.node("local-model-effort").value = ""; await h.node("apply-local-model").dispatch("click"); const withoutEffort = h.sent.at(-1);
  assert.equal(h.node("reasoning-button").disabled, true); h.emit("state.snapshot", { reasoningModelId: "B", reasoningRole: "coding" }, withoutEffort.id); assert.equal(h.node("reasoning-button").disabled, false);
});

async function openReasoningDraft(h) {
  h.emit("state.snapshot", { reasoningModelId: "A", reasoningRole: "coding", isRunning: false }); h.snapshot({ selected: "high" });
  await h.node("reasoning-button").dispatch("click"); h.snapshot({ selected: "high" });
  await h.node("reasoning-options").children[0].dispatch("click");
}

test("submission commits the open slider before the document click and waits for the server set acknowledgement", async () => {
  const h = harness(); await openReasoningDraft(h);
  let settled = false;
  const ready = h.commit().then(result => { settled = true; return result; });
  assert.equal(h.sent.at(-1).type, "reasoning.set"); const id = h.sent.at(-1).id;
  h.outsideClick(); await Promise.resolve();
  assert.equal(settled, false);
  assert.equal(h.sent.filter(item => item.type === "reasoning.set").length, 1);
  h.snapshot({ selected: "low" }, id);
  assert.deepEqual(JSON.parse(JSON.stringify(await ready)), { modelId: "A", role: "coding", effort: "low" });
  assert.equal(h.timers.size, 0);
});

test("a peer broadcast cannot acknowledge a pending write and simultaneous submission waiters share its single commit", async () => {
  const h = harness(); await openReasoningDraft(h); h.outsideClick();
  let settled = 0; const first = h.commit().then(() => settled++), second = h.commit().then(() => settled++);
  const id = h.sent.at(-1).id;
  h.snapshot({ selected: "low" }, null); await Promise.resolve();
  assert.equal(settled, 0); assert.equal(h.timers.size, 2);
  h.snapshot({ selected: "low" }, id); await Promise.all([first, second]);
  assert.equal(settled, 2); assert.equal(h.timers.size, 0);
  assert.equal(h.sent.filter(item => item.type === "reasoning.set").length, 1);
});

test("submission waits across model Apply followed by its selected effort commit", async () => {
  const h = harness(); await openModelDraft(h); h.node("local-model-effort").value = "high";
  await h.node("apply-local-model").dispatch("click"); const apply = h.sent.at(-1);
  let settled = false; const ready = h.commit().then(result => { settled = true; return result; });
  h.emit("state.snapshot", { reasoningModelId: "B", reasoningRole: "coding", isRunning: false }, apply.id);
  assert.equal(h.sent.at(-1).type, "reasoning.set"); await Promise.resolve(); assert.equal(settled, false);
  h.snapshot({ modelId: "B", selected: "high" }, h.sent.at(-1).id);
  assert.deepEqual(JSON.parse(JSON.stringify(await ready)), { modelId: "B", role: "coding", effort: "high" });
  assert.equal(h.timers.size, 0);
});

test("own model and reasoning errors reject submission without treating a later snapshot as success", async () => {
  for (const operation of ["reasoning", "model"]) {
    const h = harness();
    if (operation === "reasoning") await openReasoningDraft(h);
    else { await openModelDraft(h); await h.node("apply-local-model").dispatch("click"); }
    const ready = h.commit(); const id = h.sent.at(-1).id;
    const rejected = assert.rejects(ready, /Übernahme fehlgeschlagen/);
    h.emit("host.error", { message: "Übernahme fehlgeschlagen" }, id); await rejected;
    assert.equal(h.timers.size, 0);
    await assert.rejects(h.commit(), /Übernahme fehlgeschlagen/);
  }
});

test("disconnect and timeout reject pending submissions and dispose every waiter timer", async () => {
  for (const failure of ["disconnect", "timeout"]) {
    const h = harness(); await openReasoningDraft(h);
    const ready = h.commit(); const old = h.sent.at(-1).id;
    const rejected = assert.rejects(ready, failure === "disconnect" ? /unterbrochen/ : /Serverbestätigung/);
    if (failure === "disconnect") h.disconnect(); else h.fireTimers();
    await rejected; assert.equal(h.timers.size, 0);
    h.snapshot({ selected: "low" }, old);
    await assert.rejects(h.commit(), failure === "disconnect" ? /unterbrochen/ : /Serverbestätigung/);
  }
});

test("unexpected model-role transitions and mismatched set acknowledgements cannot silently submit", async () => {
  const h = harness(); await openReasoningDraft(h);
  const ready = h.commit(); const rejected = assert.rejects(ready, /Modell.*geändert/);
  h.emit("state.snapshot", { reasoningModelId: "A", reasoningRole: "general" }); await rejected;
  assert.equal(h.timers.size, 0);
  const wrong = harness(); await openReasoningDraft(wrong);
  const mismatch = assert.rejects(wrong.commit(), /andere Reasoning-Auswahl/);
  wrong.snapshot({ selected: "high" }); await mismatch;
  assert.equal(wrong.timers.size, 0);
});

test("model Apply must confirm the requested model before the submission barrier succeeds", async () => {
  const h = harness(); await openModelDraft(h); await h.node("apply-local-model").dispatch("click"); const apply = h.sent.at(-1);
  const rejected = assert.rejects(h.commit(), /anderes Modell/);
  h.emit("state.snapshot", { reasoningModelId: "A", reasoningRole: "coding" }, apply.id); await rejected;
  assert.equal(h.sent.some(item => item.type === "reasoning.set"), false);
  assert.equal(h.timers.size, 0);
});

test("authoritative reconnect reads release later submissions and an unchanged choice needs no mutation", async () => {
  const h = harness(); await openReasoningDraft(h);
  const rejected = assert.rejects(h.commit(), /unterbrochen/); h.disconnect(); await rejected;
  h.emit("state.snapshot", { reasoningModelId: "A", reasoningRole: "coding" });
  const ready = h.commit(); h.snapshot({ selected: "low" });
  assert.equal((await ready).effort, "low");
  const before = h.sent.length; assert.equal((await h.commit()).effort, "low"); assert.equal(h.sent.length, before);
});
