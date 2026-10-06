const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const source = fs.readFileSync(require.resolve("../../src/Missum.App/Assets/Web/browser-render-timing.js"), "utf8");
function harness(options = {}) {
  let now = 0, nextId = 0, currentScope = "session-a:run-a", state = "", onRender = null;
  const timers = new Map(), renders = [];
  const context = { setTimeout: (callback, delay) => { const id = ++nextId; timers.set(id, { at: now + delay, callback }); return id; }, clearTimeout: id => timers.delete(id) };
  vm.runInNewContext(source, context);
  const scheduler = context.missumRenderTiming.create({ getScope: () => currentScope,
    render: meta => { renders.push({ at: now, state, ...meta }); onRender?.(); }, ...options });
  function advance(milliseconds) {
    const end = now + milliseconds;
    while (true) {
      const candidate = [...timers].filter(([, timer]) => timer.at <= end).sort((left, right) => left[1].at - right[1].at || left[0] - right[0])[0];
      if (!candidate) break;
      now = candidate[1].at; timers.delete(candidate[0]); candidate[1].callback();
    }
    now = end;
  }
  return { scheduler, context, timers, renders, advance,
    scope: value => { currentScope = value; }, state: value => { state = value; }, onRender: callback => { onRender = callback; } };
}

test("streaming bursts merge UI requests on the native 80ms tick while reading the complete latest state", () => {
  const h = harness();
  for (let index = 0; index < 80; index++) { h.state(`full content ${index}`); h.scheduler.request({ follow: index === 0, reason: "delta" }); h.advance(1); }
  assert.equal(h.renders.length, 1); assert.equal(h.renders[0].at, 80);
  assert.equal(h.renders[0].state, "full content 79"); assert.equal(h.renders[0].follow, true);
  h.advance(80); assert.equal(h.renders.length, 1, "idle render polling does not rebuild the browser DOM");
});

test("sustained progress does not postpone rendering like a reset-on-every-packet debounce", () => {
  const h = harness();
  for (let index = 0; index < 25; index++) { h.state(String(index)); h.scheduler.request(); h.advance(16); }
  assert.deepEqual(h.renders.map(item => item.at), [80, 160, 240, 320, 400]);
  assert.equal(h.renders.at(-1).state, "24");
});

test("terminal, snapshot and user actions flush current state immediately and cancel the older scheduled paint", () => {
  for (const reason of ["terminal", "snapshot", "user-action"]) {
    const h = harness(); h.state("partial"); h.scheduler.request({ follow: true }); h.advance(10);
    h.state("finished"); h.scheduler.flush({ reason });
    assert.equal(h.renders.length, 1); assert.equal(h.renders[0].at, 10); assert.equal(h.renders[0].reason, reason);
    assert.equal(h.renders[0].state, "finished"); assert.equal(h.renders[0].follow, true);
    h.advance(1000); assert.equal(h.renders.length, 1); assert.equal(h.timers.size, 0);
  }
});

test("session or prompt scope changes discard old timers and ignore stale terminal flush requests", () => {
  const h = harness(); h.scheduler.request({ scopeKey: "session-a:run-a", follow: true }); h.advance(20);
  h.scope("session-b:run-b"); h.state("new chat");
  assert.equal(h.scheduler.request({ scopeKey: "session-a:run-a", immediate: true }), false);
  h.scheduler.flush({ scopeKey: "session-b:run-b", reason: "snapshot" });
  h.advance(80); assert.equal(h.renders.length, 1);
  assert.equal(h.renders[0].scopeKey, "session-b:run-b"); assert.equal(h.renders[0].follow, false);
});

test("an unannounced scope change still suppresses a pending paint even when no new packet arrives", () => {
  const h = harness(); h.scheduler.request(); h.scope("session-a:next-prompt"); h.advance(80);
  assert.equal(h.renders.length, 0); h.scheduler.request(); h.advance(80);
  assert.equal(h.renders[0].scopeKey, "session-a:next-prompt");
});

test("explicit reset and dispose cancel future paints without interfering with synchronous state updates", () => {
  const h = harness(); h.scheduler.request(); h.scheduler.reset(); h.advance(80);
  assert.equal(h.renders.length, 0); h.state("retained state"); h.scheduler.flush();
  assert.equal(h.renders[0].state, "retained state");
  h.scheduler.request(); h.scheduler.dispose(); h.advance(500);
  assert.equal(h.scheduler.request(), false); assert.equal(h.scheduler.flush(), false);
  assert.equal(h.renders.length, 1); assert.equal(h.timers.size, 0);
});

test("layout work requested during rendering is retained for the next tick rather than silently dropped", () => {
  const h = harness(); let layoutPending = true;
  h.onRender(() => { if (layoutPending) { layoutPending = false; h.scheduler.request({ reason: "layout" }); } });
  h.scheduler.request({ reason: "delta" }); h.advance(160);
  assert.deepEqual(h.renders.map(item => [item.at, item.reason]), [[80, "delta"], [160, "layout"]]);
});

test("a renderer failure does not leave a permanent pending frame that blocks all later updates", () => {
  const h = harness(); h.onRender(() => { throw new Error("paint failed"); });
  assert.throws(() => h.scheduler.flush(), /paint failed/);
  h.onRender(null); h.scheduler.request(); h.advance(80); assert.equal(h.renders.length, 2);
});

test("timing constants match native source and scheduling never invents status or token telemetry", () => {
  const h = harness(); assert.equal(h.context.missumRenderTiming.native.renderMilliseconds, 80);
  assert.equal(h.context.missumRenderTiming.native.labelMilliseconds, 1000); h.scheduler.flush();
  assert.equal(h.renders[0].reason, null); assert.equal(h.renders[0].state, "");
  assert.equal("generatedTokens" in h.renders[0], false); assert.equal("runStatus" in h.renders[0], false);
});
