const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const source = fs.readFileSync(require.resolve("../../src/Missum.App/Assets/Web/app.js"), "utf8").replace(/\r\n/g, "\n");
const context = vm.createContext({});
for (const name of ["codingToolLabel", "codingToolSummary"]) {
  const start = source.indexOf(`  function ${name}(`);
  const end = source.indexOf("\n  function ", start + 1);
  assert.ok(start >= 0 && end > start, `production ${name} is present`);
  vm.runInContext(source.slice(start, end), context);
}

test("all eight Science tool captions match the native Missum German titles", () => {
  const captions = {
    "research.read": "Forschungsstand lesen", "research.update": "Forschungsstand ergänzen",
    "research.code.write": "Python-Datei vorbereiten", "research.code.execute": "Python-Analyse ausführen",
    "research.code.test": "Berechnung prüfen", "research.code.benchmark": "Berechnung vergleichen",
    "research.deliverables.verify": "Forschungsergebnisse prüfen", "math.formalProof": "Lean-Beweis prüfen"
  };
  for (const [tool, title] of Object.entries(captions)) assert.equal(context.codingToolLabel(tool), title);
  assert.equal(context.codingToolLabel("coding.read"), "Datei lesen");
  assert.equal(context.codingToolLabel("org.example.custom.inspect"), "org.example.custom.inspect");
});

test("Research read and update headers use the native purpose, single title and research object count", () => {
  assert.equal(context.codingToolSummary({ tool: "research.read" }), "Hypothesen, offene Prüfungen und Publikationsstand");
  const summary = changes => context.codingToolSummary({ tool: "research.update", inputJson: JSON.stringify({ changes }) });
  assert.equal(summary([{ id: "hypothesis-1", data: { title: "Symmetrie prüfen" } }]), "Symmetrie prüfen");
  assert.equal(summary([{ id: "hypothesis-1" }, { id: "question-2", data: { title: "Offene Prüfung" } }]), "2 Forschungsobjekte");
  assert.equal(summary([{ id: "fallback-id", data: {} }]), "fallback-id");
  assert.equal(summary([{}, { data: { title: "" } }]), "0 Forschungsobjekte");
});

test("the native compact research title limit retains the original receipt and malformed metadata falls back", () => {
  const title = "Forschungsstand ".repeat(15);
  const step = { tool: "research.update", inputJson: JSON.stringify({ changes: [{ id: "long", data: { title } }] }) };
  const original = step.inputJson;
  assert.equal(context.codingToolSummary(step), `${title.slice(0, 97)}…`);
  assert.equal(step.inputJson, original);
  for (const inputJson of ["bad json", "null", "[]", "{}", '{"changes":null}']) assert.equal(context.codingToolSummary({ tool: "research.update", inputJson }), null);
  assert.equal(context.codingToolSummary({ tool: "coding.command", inputJson: '{}' }), null);
});

test("Python execution header keeps the native executable, script arguments and captured exit facts", () => {
  const step = { tool: "research.code.execute", inputJson: JSON.stringify({ executable: "python", arguments: ["sympy_ect_verify.py"] }) };
  assert.equal(context.codingToolSummary(step), "python sympy_ect_verify.py");
  assert.equal(context.codingToolSummary({ ...step, outputJson: JSON.stringify({ exitCode: 0, sources: [{ url: "a" }, { url: "b" }], truncated: true }) }),
    "python sympy_ect_verify.py · 2 Quellen · Exitcode 0 · Ausgabe gekürzt");
  assert.equal(context.codingToolSummary({ ...step, outputJson: JSON.stringify({ exitCode: 1, errorCode: "validation_failed" }) }),
    "python sympy_ect_verify.py · Exitcode 1 · validation_failed");
  assert.equal(context.codingToolSummary({ ...step, inputJson: "malformed" }), null);
});
