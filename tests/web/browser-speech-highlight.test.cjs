const assert = require("node:assert/strict"), fs = require("node:fs"), path = require("node:path"), vm = require("node:vm"), test = require("node:test");
const { TestNode } = require("./test-dom.cjs");
const source = fs.readFileSync(path.resolve(__dirname, "../../src/Missum.App/Assets/Web/app.js"), "utf8");
function setup(timeline) {
  const list = new TestNode("div"), article = new TestNode("article"), body = new TestNode("div"), content = new TestNode("div");
  article.dataset.messageId = "message"; body.className = "message-body"; content.className = timeline ? "coding-timeline" : "message-content";
  const narration = new TestNode("div"); narration.className = "coding-narration";
  const text = new TestNode("p"); text.textContent = "Dieser Satz wird vorgelesen.";
  if (timeline) { narration.append(text); content.append(narration); } else content.append(text);
  const excluded = new TestNode("div"); excluded.setAttribute("data-speech-exclude", "true"); const reasoning = new TestNode("p"); reasoning.textContent = "Denkprozess"; excluded.append(reasoning); if (timeline) content.append(excluded);
  body.append(content); article.append(body); list.append(article);
  const highlights = new Map(), unit = { id: "sentence", kind: "paragraph", blockIndex: 0, text: text.textContent };
  const context = vm.createContext({ state: { activeSessionId: "session", speechProgress: { sessionId: "session", sourceMessageId: "message", sourceUnits: [unit], activeSourceUnitIds: [unit.id] } }, elements: { messageList: list }, speechHighlightName: "test-speech",
    CSS: { highlights }, Highlight: class { constructor(...ranges) { this.ranges = ranges; } },
    speechSourceRangeMap: (root, units) => new Map(units.map(source => [source.id, { block: context.speechBlockCandidates(root, source.kind)[source.blockIndex], range: "real-block-range" }])) });
  for (const name of ["clearSpeechHighlight", "speechBlockCandidates", "activeSpeechArticle", "applySpeechHighlight"]) {
    const start = source.indexOf(`  function ${name}(`), ending = source.slice(start).match(/\r?\n {2}\}(?:\r?\n|$)/); assert.ok(start >= 0 && ending); vm.runInContext(source.slice(start, start + ending.index + ending[0].length), context);
  }
  return { context, text, reasoning, highlights };
}
test("F5 progress highlights the real narration inside a direct native tool timeline", () => {
  const host = setup(true); host.context.applySpeechHighlight(); assert.equal(host.text.getAttribute("data-speech-source-active"), "true");
  assert.equal(host.reasoning.getAttribute("data-speech-source-active"), null); assert.equal(host.highlights.size, 1);
  host.context.state.speechProgress.activeSourceUnitIds = []; host.context.applySpeechHighlight(); assert.equal(host.text.getAttribute("data-speech-source-active"), null); assert.equal(host.highlights.size, 0);
});
test("F5 progress still supports plain message bodies and never marks a foreign session", () => {
  const host = setup(false); host.context.applySpeechHighlight(); assert.equal(host.text.getAttribute("aria-current"), "true");
  host.context.state.speechProgress.sessionId = "other"; host.context.applySpeechHighlight(); assert.equal(host.text.getAttribute("aria-current"), null); assert.equal(host.highlights.size, 0);
});
