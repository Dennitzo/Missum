const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { TestNode } = require("./test-dom.cjs");

const webRoot = path.resolve(__dirname, "../../src/Missum.App/Assets/Web");
function harness() {
  const posts = [], body = new TestNode("body");
  const document = { body, createElement: tag => new TestNode(tag), createTextNode: text => new TestNode("#text", text), createDocumentFragment: () => new TestNode("#fragment") };
  const context = vm.createContext({ document, URL, missumBridge: { post: (type, payload) => posts.push({ type, payload }) } });
  for (const name of ["vendor/highlightjs/11.11.1/highlight.min.js", "code-highlighting.js", "markdown.js", "coding-timeline.js"])
    vm.runInContext(fs.readFileSync(path.join(webRoot, name), "utf8"), context, { filename: name });
  const render = (step, options = {}) => context.missumCodingTimeline.render({ id: "answer", role: "assistant", content: "", status: step.status === "running" ? "streaming" : "completed" }, [step], {
    codingToolStepsExpanded: true, renderMarkdown: source => context.missumMarkdown.render(source), ...options
  });
  return { context, body, posts, render };
}
const receipt = (input, output, extra = {}) => ({ id: "step", tool: "math.evaluate", label: "Berechnen", status: "completed", inputJson: JSON.stringify(input), outputJson: JSON.stringify(output), ...extra });
const section = (root, type) => root.querySelector(`.coding-receipt-section--${type}`);
const field = (root, label) => root.querySelectorAll(".coding-fact").find(item => item.querySelector("dt")?.textContent === label)?.querySelector("dd");

test("recorded Math sum and mean receipts retain separate labelled parameters and results without JSON whitespace", async () => {
  // Values captured from the real LAN reference chat, speech-api-owner-events.json.
  for (const [operation, left, right, expected] of [["add", 12, 18, 30], ["add", 30, 30, 60], ["divide", 60, 3, 20]]) {
    const h = harness(), input = { operation, left: [left], right: [right] }, output = { operation, result: [expected], toolStatus: "completed", partial: false };
    const timeline = h.render(receipt(input, output));
    assert.deepEqual(timeline.querySelectorAll(".coding-receipt-heading").map(item => item.textContent), ["Parameter", "Ergebnis"]);
    assert.equal(field(section(timeline, "input"), "Linke Werte").textContent, String(left));
    assert.equal(field(section(timeline, "input"), "Rechte Werte").textContent, String(right));
    assert.equal(field(section(timeline, "output"), "Ergebnis").textContent, String(expected));
    assert.equal(timeline.querySelector("pre"), null, "numeric arrays are readable values rather than indented JSON code dumps");
    assert.equal(timeline.textContent.includes("toolStatus"), false);
    assert.equal(timeline.textContent.includes("Unvollständige Ausgabe"), false);
    await timeline.querySelector(".coding-step__copy").dispatch("click");
    const copied = JSON.parse(h.posts.at(-1).payload.text);
    assert.deepEqual(copied.input, input); assert.deepEqual(copied.output, output, "copy preserves every transport field even when the visible receipt is simplified");
  }
});

test("nested objects, arrays and encoded JSON remain individually labelled and preserve text whitespace", () => {
  const h = harness(), input = { variables: { x: 3, options: { exact: true, labels: ["eins", "zwei"] } },
    matrix: [[1, 2], [3, 4]], metadata: JSON.stringify({ origin: "Referenz", enabled: false }) };
  const output = { result: [{ value: 5, statement: "**Begründung**\n\nDie Werte stimmen." }], notes: "Erste Zeile\n\n    eingerückte Zeile" };
  const timeline = h.render(receipt(input, output));
  assert.equal(field(section(timeline, "input"), "x").textContent, "3");
  assert.equal(field(section(timeline, "input"), "exact").textContent, "Ja");
  assert.equal(field(section(timeline, "input"), "labels").textContent, "eins, zwei");
  assert.equal(field(section(timeline, "input"), "origin").textContent, "Referenz");
  assert.equal(field(section(timeline, "input"), "enabled").textContent, "Nein");
  assert.deepEqual(field(section(timeline, "input"), "matrix").querySelectorAll("li").map(item => item.textContent), ["1, 2", "3, 4"]);
  assert.equal(field(section(timeline, "output"), "notes").textContent, output.notes);
  assert.equal(section(timeline, "output").querySelector("strong").textContent, "Begründung");
  assert.equal(timeline.textContent.includes("[object Object]"), false);
  assert.equal(timeline.textContent.includes('"origin"'), false, "encoded JSON is parsed into labelled values instead of displayed twice escaped");
});

test("process receipts keep the exact command, full stdout and stderr, plus nested diagnostics and copy payloads", async () => {
  const h = harness(), stdout = "Erster Test\r\n" + "Vollständige Ausgabe 日本語\n".repeat(250) + "LETZTE ZEILE";
  const stderr = "Warnung: <script>never execute</script>\nDiagnose bleibt vollständig.";
  const input = { executable: "python.exe", arguments: ["checks.py", "--label", "Mein Projekt"], workingDirectory: "C:\\Projects\\Rechner", timeoutSeconds: 120, environment: { mode: "prüfung", paths: ["src", "tests"] } };
  const output = { stdout, stderr, exitCode: 1, elapsedMilliseconds: 2500, diagnostics: { checks: [{ name: "A", passed: false }, { name: "B", passed: true }] } };
  const timeline = h.render(receipt(input, output, { tool: "coding.command", label: "Befehl ausführen" }));
  assert.equal(section(timeline, "input").querySelector(".coding-output--command pre").textContent, "python.exe checks.py --label 'Mein Projekt'");
  assert.equal(field(section(timeline, "input"), "Arbeitsordner").textContent, input.workingDirectory);
  assert.equal(field(section(timeline, "input"), "mode").textContent, "prüfung");
  assert.equal(section(timeline, "output").querySelector(".coding-output--terminal pre").textContent, stdout);
  assert.equal(section(timeline, "output").querySelector(".coding-output--stderr pre").textContent, stderr);
  assert.deepEqual(section(timeline, "output").querySelectorAll(".coding-fact").filter(item => item.querySelector("dt")?.textContent === "passed").map(item => item.querySelector("dd").textContent), ["Nein", "Ja"]);
  assert.equal(timeline.querySelector("script"), null);
  await section(timeline, "output").querySelector(".coding-output--terminal button").dispatch("click");
  assert.equal(h.posts.at(-1).payload.text, stdout);
});

test("tool Markdown and source code use safe distinct renderers without executing model HTML", () => {
  const h = harness(), code = "import math\nprint('<script>never execute</script>')\n";
  const timeline = h.render(receipt({ source: code }, { summary: "## Befund\n\n- **Bestanden**\n- [gefährlich](javascript:alert(1))\n\n<script>alert(1)</script>" }, { tool: "research.code.write" }));
  assert.equal(section(timeline, "input").querySelector(".coding-output--source code").textContent, code);
  assert.equal(section(timeline, "input").querySelector(".hljs-keyword").textContent, "import");
  assert.equal(section(timeline, "output").querySelector("h2").textContent, "Befund");
  assert.equal(section(timeline, "output").querySelectorAll("li").length, 2);
  assert.equal(timeline.querySelector("script"), null);
  assert.equal(timeline.querySelector("iframe"), null);
  assert.equal(timeline.querySelectorAll("a").some(link => String(link.href).startsWith("javascript:")), false);
  assert.equal(h.posts.length, 0);
});

test("primitive false and zero receipts remain visible and encoded output JSON becomes structured data", () => {
  const h = harness();
  const primitive = h.render(receipt(0, false));
  assert.equal(section(primitive, "input").querySelector(".coding-value-text").textContent, "0");
  assert.equal(section(primitive, "output").querySelector(".coding-value-text").textContent, "Nein");
  const encoded = h.render(receipt({}, JSON.stringify({ result: [30], description: "**Berechnet**" })));
  assert.equal(field(section(encoded, "output"), "Ergebnis").textContent, "30");
  assert.equal(section(encoded, "output").querySelector("strong").textContent, "Berechnet");
});

test("JSON-looking source and research process streams retain their exact text and independent copy actions", async () => {
  const h = harness(), source = '{\n  "value": [1, 2],\n  "label": "Original"\n}\n';
  const stdout = '{ "result": [30], "successful": true }\r\n', stderr = '[ "original warning" ]\n';
  const written = h.render(receipt({ source }, {}, { tool: "research.code.write" }));
  assert.equal(section(written, "input").querySelector(".coding-output--source code").textContent, source);
  await section(written, "input").querySelector(".coding-output--source button").dispatch("click");
  assert.equal(h.posts.at(-1).payload.text, source);
  const executed = h.render(receipt({ path: "analyse.py" }, { stdout, stderr, exitCode: 1 }, { tool: "research.code.execute" }));
  assert.equal(section(executed, "output").querySelector(".coding-output--terminal pre").textContent, stdout);
  assert.equal(section(executed, "output").querySelector(".coding-output--stderr pre").textContent, stderr);
  assert.equal(field(section(executed, "output"), "successful"), undefined, "literal process JSON does not masquerade as result metadata");
  await section(executed, "output").querySelector(".coding-output--stderr button").dispatch("click");
  assert.equal(h.posts.at(-1).payload.text, stderr);
});

test("receipt updates retain selected nested text and reopening uses the latest data and header", async () => {
  const h = harness(), first = receipt({ executable: "python", arguments: ["check.py"] }, { stdout: "Erstes Ergebnis", diagnostics: { detail: "Ich prüfe" } }, { tool: "coding.command", label: "Befehl ausführen", status: "running" });
  const current = h.render(first); h.body.append(current);
  const nested = field(section(current, "output"), "detail").firstChild.firstChild;
  const latest = { ...first, outputJson: JSON.stringify({ stdout: "Erstes Ergebnis\nAlles geprüft", diagnostics: { detail: "Ich prüfe den letzten Befund" } }) };
  h.context.missumCodingTimeline.reconcile(current, h.render(latest, { previousTimeline: current }));
  assert.equal(field(section(current, "output"), "detail").firstChild.firstChild, nested);
  assert.equal(nested.textContent, "Ich prüfe den letzten Befund");
  assert.equal(nested.isConnected, true);
  const disclosure = current.querySelector("details");
  disclosure.removeAttribute("open"); await disclosure.dispatch("toggle");
  assert.equal(disclosure.querySelector(".coding-step__content"), null);
  const finished = { ...latest, status: "completed", outputJson: JSON.stringify({ stdout: "Aktueller Abschluss", exitCode: 0, diagnostics: { detail: "Der neue Befund" } }) };
  h.context.missumCodingTimeline.reconcile(current, h.render(finished, { previousTimeline: current }));
  disclosure.setAttribute("open", ""); await disclosure.dispatch("toggle");
  assert.match(disclosure.querySelector(".coding-step__name").textContent, /Aktueller Abschluss/);
  assert.equal(field(section(disclosure, "output"), "detail").textContent, "Der neue Befund");
  await disclosure.querySelector(".coding-step__copy").dispatch("click");
  assert.equal(JSON.parse(h.posts.at(-1).payload.text).output.stdout, "Aktueller Abschluss");
});

test("closed structured receipts build no body and keep complete deeply nested data available on demand", async () => {
  const h = harness(); let nested = { value: "TIEFSTER BEFUND" };
  for (let level = 0; level < 20; level++) nested = { child: nested };
  const timeline = h.render(receipt({}, { nested }), { codingToolStepsExpanded: false });
  assert.equal(timeline.querySelector(".coding-receipt-section"), null);
  const disclosure = timeline.querySelector("details"); disclosure.setAttribute("open", ""); await disclosure.dispatch("toggle");
  assert.ok(disclosure.textContent.includes("TIEFSTER BEFUND"));
  await disclosure.querySelector(".coding-step__copy").dispatch("click");
  assert.deepEqual(JSON.parse(h.posts.at(-1).payload.text).output, { nested });
  disclosure.removeAttribute("open"); await disclosure.dispatch("toggle");
  assert.equal(timeline.querySelector(".coding-receipt-section"), null);
});
