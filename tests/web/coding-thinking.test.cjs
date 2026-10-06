const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const { TestNode } = require("./test-dom.cjs");
const source = fs.readFileSync(require.resolve("../../src/Missum.App/Assets/Web/coding-timeline.js"), "utf8");

function harness() {
  let clock = 1_000_000, current, parses = 0, timerId = 0;
  const timers = new Map(), body = new TestNode("body"), pane = new TestNode("section"); pane.hidden = false;
  const document = { body, visibilityState: "visible", getElementById: id => id === "conversation-pane" ? pane : null,
    createElement: tag => new TestNode(tag), createElementNS: (_namespace, tag) => new TestNode(tag), createTextNode: text => new TestNode("#text", text) };
  const context = vm.createContext({ document, URL,
    setTimeout: (callback, delay) => { timers.set(++timerId, { callback, delay }); return timerId; }, clearTimeout: id => timers.delete(id) });
  vm.runInContext(source, context);
  const time = () => new Date(clock).toISOString();
  const render = (message = {}, steps = [], options = {}) => {
    const next = context.missumCodingTimeline.render({ id: "answer", sessionId: "session", role: "assistant", status: "streaming", content: "", ...message }, steps, {
      now: () => clock, previousTimeline: current, codingToolStepsExpanded: false,
      renderMarkdown: text => { parses++; const node = new TestNode("p"); node.textContent = text; return node; }, ...options
    });
    if (current) context.missumCodingTimeline.reconcile(current, next);
    else { current = next; body.append(current); }
    return current;
  };
  const reasoning = (extra = {}) => ({ id: "reasoning-1", tool: "assistant.reasoning", status: "running", detail: "Ich prüfe den Nachweis.", inputJson: '{"round":1,"phase":"main"}', updatedAt: time(), contentOffset: 0, ...extra });
  const progress = (extra = {}) => ({ messageId: "answer", runId: "run-1", status: "Modell generiert", detail: "12.034 Token", generationState: "tokenProgress", generatedTokens: 34, generationUpdatedAt: time(), contextUsed: 12034, ...extra });
  const live = () => current?.querySelector(".coding-live-thinking");
  const label = () => live()?.querySelector(".coding-reasoning__name").textContent;
  const open = () => { const disclosure = live()?.querySelector("details"); assert.ok(disclosure); disclosure.setAttribute("open", ""); for (const listener of disclosure.listeners.get("toggle") || []) listener({ target: disclosure, currentTarget: disclosure }); return disclosure; };
  return { context, document, pane, body, timers, render, reasoning, progress, live, label, open, time, now: () => clock,
    advance: milliseconds => { clock += milliseconds; }, parses: () => parses, current: () => current };
}

test("native live thinking uses the server context total once and places its count in one collapsed reasoning row", () => {
  const h = harness(), reasoning = h.reasoning();
  const timeline = h.render({}, [reasoning], { liveStatus: h.progress({ contextUsed: 12678, generatedTokens: 300, processedPromptTokens: 12000 }) });
  assert.equal(h.label(), "Denke nach · 12.678 Token");
  assert.equal(h.live().getAttribute("data-speech-exclude"), "true");
  assert.equal(h.live().querySelector("details").hasAttribute("open"), false);
  assert.equal(h.live().querySelector(".coding-reasoning__status").textContent, "");
  assert.equal(timeline.querySelector(".coding-live-phase"), null);
  assert.equal(timeline.querySelector('[data-step-id="reasoning-1"]').hasAttribute("hidden"), true);
  h.open(); assert.match(h.live().textContent, /Ich prüfe den Nachweis/);
});

test("prompt evaluation has its own native label and literal explanation, without claiming generated thinking", () => {
  const h = harness(); h.render({}, [], { liveStatus: h.progress({ generationState: "promptProcessing", generatedTokens: 0,
    processedPromptTokens: 2500, totalPromptTokens: 10000, promptProgress: .25, contextUsed: 2500 }) });
  assert.equal(h.label(), "Kontext wird verarbeitet · 25 % · 2.500 Eingangstoken");
  h.open(); assert.match(h.live().textContent, /2\.500 von 10\.000 Eingangstoken verarbeitet/);
  assert.match(h.live().textContent, /Der Denkprozess erscheint, sobald das Modell mit der Generierung beginnt/);
  assert.equal(h.live().textContent.includes("Noch kein Denkprozess"), false);
});

test("zero or unmeasured generation cannot start an empty thinking row, while a fresh positive pulse can", () => {
  const h = harness();
  for (const generatedTokens of [null, 0, -1]) { h.render({}, [], { liveStatus: h.progress({ generatedTokens }) }); assert.equal(h.live(), null); }
  h.render({}, [], { liveStatus: h.progress({ generatedTokens: 1, generationUpdatedAt: null }) }); assert.equal(h.live(), null);
  h.render({}, [], { liveStatus: h.progress({ generatedTokens: 2 }) }); assert.ok(h.live());
  h.open(); assert.match(h.live().textContent, /Noch kein Denkprozess vom Modell übermittelt/);
});

test("token-only updates preserve the open reasoning text and change its label at most once per second", () => {
  const h = harness(), reasoning = h.reasoning();
  h.render({}, [reasoning], { liveStatus: h.progress() }); const row = h.live(); h.open();
  const selected = row.querySelector(".coding-reasoning__body").firstChild.firstChild, parsed = h.parses();
  const labelText = row.querySelector(".coding-reasoning__name").firstChild;
  for (let index = 1; index <= 5; index++) {
    h.advance(100); h.render({}, [reasoning], { liveStatus: h.progress({ generatedTokens: 34 + index, contextUsed: 12034 + index }) });
    assert.equal(row.querySelector(".coding-reasoning__name").firstChild, labelText, "a native header tick does not rewrite an unchanged token label");
  }
  assert.equal(h.label(), "Denke nach · 12.034 Token"); assert.equal(h.live(), row); assert.equal(h.parses(), parsed);
  h.advance(500); h.render({}, [reasoning], { liveStatus: h.progress({ generatedTokens: 1000, contextUsed: 13000 }) });
  assert.equal(h.label(), "Denke nach · 13.000 Token"); assert.equal(h.parses(), parsed);
  assert.equal(row.querySelector(".coding-reasoning__body").firstChild.firstChild, selected); assert.equal(selected.isConnected, true);
});

test("a new answer consumes the thinking interval and a later real pulse waits for the native 750ms quiet period", () => {
  const h = harness(); h.render({}, [], { liveStatus: h.progress() }); assert.ok(h.live());
  h.advance(100); h.render({ content: "Eine neue sichtbare Antwort." }, [], { liveStatus: h.progress() }); assert.equal(h.live(), null);
  h.advance(100); const next = h.progress({ generatedTokens: 35 }); h.render({ content: "Eine neue sichtbare Antwort." }, [], { liveStatus: next }); assert.equal(h.live(), null);
  h.advance(649); h.render({ content: "Eine neue sichtbare Antwort." }, [], { liveStatus: next }); assert.equal(h.live(), null);
  h.advance(1); h.render({ content: "Eine neue sichtbare Antwort." }, [], { liveStatus: next }); assert.ok(h.live());
});

test("repeated counts, internal fragments and older phases cannot resurrect thinking after answer text", () => {
  const h = harness(); h.render({}, [], { liveStatus: h.progress({ generatedTokens: 50 }) });
  h.advance(100); h.render({ content: "Antwort" }, [], { liveStatus: h.progress({ generatedTokens: 50 }) });
  h.advance(1000);
  for (const phase of ["tokenProgress", "codingWaiting", "reasoningDelta", "contentDelta"]) {
    h.render({ content: "Antwort" }, [], { liveStatus: h.progress({ generationState: phase, generatedTokens: 50 }) }); assert.equal(h.live(), null);
  }
  h.advance(100); h.render({ content: "Antwort" }, [], { liveStatus: h.progress({ generationState: "toolSelected" }) });
  h.render({ content: "Antwort" }, [], { liveStatus: h.progress({ generatedTokens: 100, generationUpdatedAt: new Date(h.now() - 100).toISOString() }) });
  assert.equal(h.live(), null);
  h.advance(100); h.render({ content: "Antwort" }, [], { liveStatus: h.progress({ generatedTokens: 101 }) }); assert.ok(h.live());
});

test("stale initial snapshots cannot create a thinking interval and an established row survives a long provider pause", () => {
  const h = harness(), old = h.progress(); h.advance(8000); h.render({}, [], { liveStatus: old }); assert.equal(h.live(), null);
  h.advance(100); h.render({}, [], { liveStatus: h.progress({ generatedTokens: 35 }) }); assert.ok(h.live());
  h.advance(60_000); h.render({}, [], { liveStatus: h.progress({ generationState: "providerRetryWaiting", generatedTokens: null }) }); assert.ok(h.live());
});

test("tool execution blocks first display but cannot extinguish an already visible native thinking row", () => {
  const h = harness(), tool = { id: "read", tool: "coding.read", status: "running", label: "Datei lesen", inputJson: '{"path":"test.py"}' };
  h.render({}, [tool], { liveStatus: h.progress() }); assert.equal(h.live(), null);
  h.advance(100); h.render({}, [{ ...tool, status: "completed" }], { liveStatus: h.progress({ generatedTokens: 35 }) }); assert.ok(h.live());
  h.advance(100); h.render({}, [tool], { liveStatus: h.progress({ generationState: "toolSelected" }) }); assert.ok(h.live());
});

test("hidden conversations cannot first show old progress and returning retains an established latch", () => {
  const h = harness(), first = h.progress(); h.pane.hidden = true; h.render({}, [], { liveStatus: first }); assert.equal(h.live(), null);
  h.advance(8000); h.pane.hidden = false; h.render({}, [], { liveStatus: first }); assert.equal(h.live(), null);
  h.advance(100); h.render({}, [], { liveStatus: h.progress({ generatedTokens: 35 }) }); assert.ok(h.live());
  h.pane.hidden = true; h.render({}, [], { liveStatus: h.progress({ generatedTokens: 35 }) }); assert.equal(h.live(), null);
  h.advance(60_000); h.pane.hidden = false; h.render({}, [], { liveStatus: h.progress({ generationState: "codingWaiting" }) }); assert.ok(h.live());
});

test("message and run ownership prevent counter inheritance and a resumed run starts with its own token baseline", () => {
  const h = harness(); h.render({}, [], { liveStatus: h.progress({ generatedTokens: 999, contextUsed: 12999 }) });
  h.render({ id: "other" }, [], { liveStatus: h.progress() }); assert.equal(h.live(), null, "foreign message metadata is ignored");
  h.render({ status: "completed" }, [], { liveStatus: h.progress() }); assert.equal(h.live(), null);
  h.advance(1000); h.render({}, [], { liveStatus: h.progress({ runId: "run-2", generationState: "generationStarted", generatedTokens: 0, contextUsed: 500 }) }); assert.equal(h.live(), null);
  h.advance(100); h.render({}, [], { liveStatus: h.progress({ runId: "run-2", generatedTokens: 1, contextUsed: 501 }) }); assert.equal(h.label(), "Denke nach · 501 Token");
});

test("the durable reasoning receipt returns when answer text starts or the run ends", () => {
  const h = harness(), reason = h.reasoning(); h.render({}, [reason], { liveStatus: h.progress() });
  const receipt = h.current().querySelector('[data-step-id="reasoning-1"]'); assert.equal(receipt.hasAttribute("hidden"), true);
  h.advance(100); h.render({ content: "Das Ergebnis." }, [reason], { liveStatus: h.progress() });
  assert.equal(receipt.hasAttribute("hidden"), false); assert.equal(h.live(), null);
  h.render({ content: "Das Ergebnis.", status: "completed" }, [{ ...reason, status: "completed" }], { liveStatus: h.progress() });
  assert.equal(h.live(), null); assert.equal(h.current().querySelector(".coding-reasoning__name").textContent, "Denkprozess");
});

test("a later reasoning round opens a fresh collapsed live disclosure instead of inheriting the previous round", () => {
  const h = harness(); const first = h.reasoning(); h.render({}, [first], { liveStatus: h.progress() }); h.open();
  h.advance(1000); const second = h.reasoning({ id: "reasoning-2", inputJson: '{"round":2}', detail: "Eine weitere Prüfung." });
  h.render({}, [{ ...first, status: "completed" }, second], { liveStatus: h.progress({ generatedTokens: 100 }) });
  assert.equal(h.live().querySelector("details").hasAttribute("open"), false);
  h.open(); assert.match(h.live().textContent, /Eine weitere Prüfung/);
});

test("persisted structured progress supports reload while foreign reasoning never takes over the parent thinking text", () => {
  const h = harness(); const step = { id: "progress", tool: "assistant.progress", status: "running", outputJson: JSON.stringify({ generation: { state: "tokenProgress", generatedTokens: 12, contextUsed: 812, updatedAt: h.time() } }) };
  h.render({}, [step, h.reasoning({ agentId: "foreign-agent", detail: "Fremde Überlegung" })]);
  assert.equal(h.label(), "Denke nach · 812 Token"); h.open();
  assert.equal(h.live().textContent.includes("Fremde Überlegung"), false);
});

test("visibility timers are bounded, request no model work and are cancelled when the run completes", () => {
  const h = harness(); let changes = 0;
  const callback = () => changes++;
  h.render({}, [], { liveStatus: h.progress(), onThinkingChanged: callback }); assert.equal(h.timers.size, 1);
  h.advance(100); h.render({ content: "Antwort" }, [], { liveStatus: h.progress(), onThinkingChanged: callback }); assert.equal(h.timers.size, 0);
  h.advance(100); h.render({ content: "Antwort" }, [], { liveStatus: h.progress({ generatedTokens: 35 }), onThinkingChanged: callback });
  assert.equal(h.timers.size, 1); assert.equal([...h.timers.values()][0].delay, 80);
  h.render({ content: "Antwort", status: "completed" }, [], { liveStatus: h.progress(), onThinkingChanged: callback }); assert.equal(h.timers.size, 0); assert.equal(changes, 0);
});

test("terminal snapshots without live telemetry restore a cached historical receipt and cancel its timer", () => {
  const h = harness(), completed = h.reasoning({ status: "completed" });
  h.render({}, [completed], { liveStatus: h.progress(), onThinkingChanged() {} });
  const history = h.current().querySelector('[data-step-id="reasoning-1"]'); assert.equal(history.hasAttribute("hidden"), true);
  h.render({ status: "completed" }, [completed]);
  assert.equal(h.live(), null); assert.equal(history.hasAttribute("hidden"), false); assert.equal(h.timers.size, 0);
});
