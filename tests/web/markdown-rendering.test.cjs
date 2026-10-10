const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

// Only the DOM primitives are substituted. Both the production sanitizer and
// the complete production Markdown renderer execute unchanged in the VM.
class DomNode {
  constructor(tagName, text = "") {
    this.tagName = tagName;
    this.childNodes = [];
    this.className = "";
    this.dataset = {};
    this.attributes = {};
    this.listeners = {};
    this.textContent = text;
    this.classList = {
      add: (...names) => { this.className = [...new Set([...this.className.split(" ").filter(Boolean), ...names])].join(" "); },
      remove: (...names) => { this.className = this.className.split(" ").filter(name => !names.includes(name)).join(" "); }
    };
  }
  set textContent(value) { this.text = String(value); this.childNodes = []; }
  get textContent() { return this.text + this.childNodes.map(node => node.textContent).join(""); }
  append(...nodes) {
    for (const node of nodes) this.childNodes.push(typeof node === "string" ? new DomNode("#text", node) : node);
  }
  setAttribute(name, value) { this.attributes[name] = String(value); }
  addEventListener(name, callback) { this.listeners[name] = callback; }
}

function pipeline(source, typeset = false) {
  const webRoot = path.resolve(__dirname, "../../src/Missum.App/Assets/Web");
  const app = fs.readFileSync(path.join(webRoot, "app.js"), "utf8");
  const start = app.indexOf("  function sanitizeVisibleMessageContent(value) {");
  const end = app.indexOf("\n  function ", start + 1);
  assert.ok(start >= 0 && end > start, "the production sanitizer must be found");
  const copied = [];
  const mathCalls = [];
  const katex = typeset ? require(path.join(webRoot, "vendor/katex/0.16.10/katex.min.js")) : null;
  const context = vm.createContext({
    document: {
      createElement: tag => new DomNode(tag),
      createTextNode: text => new DomNode("#text", text),
      createDocumentFragment: () => new DomNode("#fragment")
    },
    URL,
    setTimeout: () => 0,
    ...(typeset ? { katex: { renderToString: (tex, options) => {
      mathCalls.push({ tex, display: options.displayMode });
      return katex.renderToString(tex, options);
    } } } : {}),
    missumBridge: { post: (type, payload) => copied.push({ type, payload }) }
  });
  vm.runInContext(app.slice(start, end), context);
  vm.runInContext(fs.readFileSync(path.join(webRoot, "markdown.js"), "utf8"), context);
  const sanitized = context.sanitizeVisibleMessageContent(source);
  return { sanitized, root: context.missumMarkdown.render(sanitized), copied, mathCalls };
}

function descendants(node, predicate) {
  return [node, ...node.childNodes.flatMap(child => descendants(child, () => true))].filter(predicate);
}

test("the boxed metric relation typesets and keeps its exact original copy source", async () => {
  const source = String.raw`$$\boxed{\text{Metrik (Krümmung)} \quad \longleftrightarrow \quad \text{Energie-Impuls-Dichte}}$$`;
  const { root, copied, mathCalls } = pipeline(source, true);
  const rendered = classes(root, "math-render");
  assert.equal(rendered.length, 1);
  assert.equal(rendered[0].dataset.mathTypeset, "true");
  assert.equal(mathCalls[0].tex, source.slice(2, -2));
  assert.equal(classes(root, "math-source-text")[0].textContent, source);
  classes(root, "math-selectable")[0].listeners.click({ preventDefault() {}, stopPropagation() {} });
  await Promise.resolve();
  assert.equal(copied[0].payload.text, source);
});
const tags = (root, tag) => descendants(root, node => node.tagName === tag);
const classes = (root, name) => descendants(root, node => node.className.split(" ").includes(name));

test("the actual final Coding response renders a Python code block", async () => {
  const source = "Der aktuelle Rückgabeausdruck der Funktion `multiply` ist:\n\n```python\nreturn a * b\n```";
  const { sanitized, root, copied } = pipeline(source);
  assert.equal(sanitized, source);
  assert.equal(classes(root, "code-block").length, 1);
  assert.equal(tags(root, "pre").length, 1);
  assert.equal(tags(tags(root, "pre")[0], "code")[0].textContent, "return a * b");
  assert.equal(tags(root, "code")[0].textContent, "multiply");
  assert.equal(classes(root, "code-header")[0].childNodes[0].textContent, "python");
  await tags(classes(root, "code-block")[0], "button")[0].listeners.click();
  assert.equal(copied[0].type, "message.copy");
  assert.equal(copied[0].payload.text, "return a * b");
});

test("researched card images render inline without allowing script URLs", () => {
  const source = "![Lugia-Karte](https://images.example.org/lugia.png)\n\n[Quelle](https://pokemon.com/cards)\n\n![Unsicher](javascript:alert(1))";
  const { root } = pipeline(source);
  assert.equal(tags(root, "img").length, 1);
  assert.equal(tags(root, "img")[0].src, "https://images.example.org/lugia.png");
  assert.equal(tags(root, "img")[0].referrerPolicy, "no-referrer");
  assert.equal(tags(root, "a").length, 1);
  assert.match(root.textContent, /javascript:alert/);
});

test("complete strong emphasis keeps its closing delimiter", () => {
  const source = "**Geändert (nur die Funktion `multiply`):**\n\n**Tests bestanden.**";
  const { sanitized, root } = pipeline(source);
  assert.equal(sanitized, source);
  assert.deepEqual(tags(root, "strong").map(node => node.textContent), ["Geändert (nur die Funktion multiply):", "Tests bestanden."]);
  assert.equal(tags(root, "code")[0].textContent, "multiply");
  assert.ok(!root.textContent.includes("**"));
});

test("longer tool-output fences preserve literal Markdown fences and exact copied stdout", async () => {
  const stdout = "before\n```python\nprint('<br>')\n```\n````\n**literal bold**\n| left | right |\nafter";
  const fence = "`".repeat(5);
  const { root, copied } = pipeline(`${fence}text\n${stdout}\n${fence}\n\n**Completed**`);
  assert.equal(classes(root, "code-block").length, 1);
  assert.equal(tags(root, "pre")[0].textContent, stdout);
  assert.deepEqual(tags(root, "strong").map(node => node.textContent), ["Completed"]);
  assert.equal(tags(root, "table").length, 0);
  await tags(classes(root, "code-block")[0], "button")[0].listeners.click();
  assert.equal(copied[0].payload.text, stdout);
});

test("br normalization applies to prose while fenced and inline source remain literal", () => {
  const { root } = pipeline("first<br>second\n\n`<br>`\n\n```html\n<div>first<br>second</div>\n```suffix is output\nlast\n```\n");
  assert.ok(tags(root, "p")[0].textContent.includes("first\nsecond"));
  assert.equal(tags(root, "code")[0].textContent, "<br>");
  assert.equal(tags(root, "pre")[0].textContent, "<div>first<br>second</div>\n```suffix is output\nlast");
});

test("inline code survives at the end of a line", () => {
  const source = "Verwende `multiply`\n\nDer Ausdruck ist `a ** 2`.";
  const { sanitized, root } = pipeline(source);
  assert.equal(sanitized, source);
  assert.deepEqual(tags(root, "code").map(node => node.textContent), ["multiply", "a ** 2"]);
  assert.equal(tags(root, "strong").length, 0);
});

test("fenced contents keep indentation, pipes, emphasis markers and literal HTML", () => {
  const code = 'if ready:\n  result = "a | b"\n  print("**done**", "<script>alert(1)</script>")';
  const { root } = pipeline("```python\n" + code + "\n```");
  assert.equal(tags(tags(root, "pre")[0], "code")[0].textContent, code);
  assert.equal(tags(root, "table").length, 0);
  assert.equal(tags(root, "script").length, 0);
  assert.equal(tags(root, "strong").length, 0);
});

test("an unfinished streamed fence remains a code block", () => {
  const { root } = pipeline("```python\nreturn a *");
  assert.equal(tags(tags(root, "pre")[0], "code")[0].textContent, "return a *");
});

test("inline and display LaTeX retain their existing selectable source nodes", () => {
  const source = String.raw`Die Formel ist $a \cdot b$ und \(x^2\).

\[
\frac{a}{b}
\]`;
  const { sanitized, root } = pipeline(source);
  assert.equal(sanitized, source);
  assert.deepEqual(classes(root, "math-source-text").map(node => node.textContent), [
    String.raw`$a \cdot b$`, String.raw`\(x^2\)`, "\\[\n\\frac{a}{b}\n\\]"
  ]);
  assert.equal(classes(root, "display").length, 1);
});

test("the geomagnetic publication matrix retains row breaks before letters and renders its tag", async () => {
  const tex = String.raw`\frac{d}{dt}\begin{pmatrix}B_p\\B_\phi\end{pmatrix} = \begin{pmatrix}-\eta_p & \alpha\\\omega_\Omega & -\eta_\phi\end{pmatrix}\begin{pmatrix}B_p\\B_\phi\end{pmatrix} \tag{3.9}`;
  const source = `$$${tex}$$`;
  const { root, copied, mathCalls } = pipeline(source, true);
  assert.equal(classes(root, "invalid").length, 0);
  assert.equal(classes(root, "math-render")[0].dataset.mathTypeset, "true");
  assert.equal(mathCalls[0].tex, tex, "a valid TeX formula must not be rewritten as JSON escaping");
  assert.ok(classes(root, "math-render")[0].innerHTML.includes('class="mtable"'));
  assert.ok(classes(root, "math-render")[0].innerHTML.includes('class="tag"'));
  classes(root, "math-selectable")[0].listeners.click({ preventDefault() {}, stopPropagation() {} });
  await Promise.resolve();
  assert.equal(copied[0].payload.text, source);
  const inline = String.raw`$M = \begin{pmatrix}-\eta_p & \alpha\\\omega_\Omega & -\eta_\phi\end{pmatrix}$`;
  const inlineResult = pipeline(inline, true);
  assert.equal(classes(inlineResult.root, "invalid").length, 0);
  assert.equal(inlineResult.mathCalls[0].tex, inline.slice(1, -1));
  assert.equal(classes(inlineResult.root, "math-source-text")[0].textContent, inline);
});

test("two four and eight retained JSON backslashes normalize only consistently escaped formulas", () => {
  const cases = [
    String.raw`\frac{1}{2}`,
    String.raw`\begin{aligned}a&=b+c\\d&=e-f\end{aligned}`,
    String.raw`\begin{pmatrix}B_p\\B_\phi\end{pmatrix}`,
    String.raw`\begin{pmatrix}1&2\\3&4\end{pmatrix}`
  ];
  for (const tex of cases) {
    for (const factor of [2, 4, 8]) {
      const source = `$$${tex.replace(/\\/g, "\\".repeat(factor))}$$`;
      const { root, mathCalls } = pipeline(source, true);
      assert.equal(classes(root, "invalid").length, 0, `escaping factor ${factor}: ${tex}`);
      assert.equal(mathCalls[0].tex, tex, `escaping factor ${factor}: ${tex}`);
      assert.equal(classes(root, "math-source-text")[0].textContent, source);
    }
  }
});

test("the two persisted quantum gravity formulas render with Bigl Bigr and exact original copy text", async () => {
  // Original completed assistant response 5f013b62…; both displays already
  // have real ASCII closing dollars and a single backslash per TeX command.
  const cases = [
    String.raw`$$\int \frac{d^{D}q}{(2\pi)^{D}}\; \frac{1}{q^{2}(q+k)^{2}} \;=\; \frac{i}{16\pi^{2}}\left[-\frac{1}{\varepsilon} \;+\; \text{endliche Konstanten} \;+\; \ln\!\Bigl(\frac{\mu^{2}}{-k^{2}}\Bigr) \;+\; \ldots\right]$$`,
    String.raw`$$\int d^{4}x\;\sqrt{-g}\;\Bigl[a\, R_{\mu\nu\rho\sigma}R^{\mu\nu\rho\sigma} \;+\; b\, R_{\mu\nu}R^{\mu\nu} \;+\; c\, R^{2} \;+\; \ldots\Bigr]$$`
  ];
  for (const source of cases) {
    const { root, copied, mathCalls, sanitized } = pipeline(source, true);
    assert.equal(sanitized, source); assert.equal(classes(root, "math-selectable").length, 1);
    assert.equal(classes(root, "display").length, 1); assert.equal(classes(root, "invalid").length, 0);
    assert.equal(classes(root, "math-render")[0].dataset.mathTypeset, "true");
    assert.equal(mathCalls[0].tex, source.slice(2, -2), "valid TeX retains its commands and spacing internally");
    assert.equal(mathCalls[0].display, true);
    assert.equal(classes(root, "math-source-text")[0].textContent, source);
    classes(root, "math-selectable")[0].listeners.click({ preventDefault() {}, stopPropagation() {} });
    await Promise.resolve(); assert.equal(copied[0].payload.text, source);
  }
});

test("live incomplete formulas stay literal while code paths and links keep their backslashes", () => {
  for (const source of [String.raw`$$\int \frac{d^{D}q}{(2\pi)^{D}}`, String.raw`\[\int d^{4}x\;\sqrt{-g}\;\Bigl[`, String.raw`$$\\\\frac{1}{`]) {
    const { root, sanitized, mathCalls } = pipeline(source, true);
    assert.equal(sanitized, source); assert.equal(root.textContent, source);
    assert.equal(classes(root, "math-selectable").length, 0); assert.equal(mathCalls.length, 0);
  }
  const literal = String.raw`C:\\tmp\\Bigl\\formula.tex`;
  const { root, mathCalls } = pipeline(`${literal}\n\n\`${String.raw`$$\\\\frac{1}{2}$$`}\`\n\n[Quelle](https://example.org/Bigl?value=x²)`, true);
  assert.ok(root.textContent.includes(literal)); assert.equal(tags(root, "code")[0].textContent, String.raw`$$\\\\frac{1}{2}$$`);
  assert.equal(tags(root, "a")[0].href, "https://example.org/Bigl?value=x%C2%B2"); assert.equal(mathCalls.length, 0);
});

test("an invalid formula keeps its exact copy source and the real KaTeX parse diagnostic", () => {
  const tex = String.raw`\MissumUnknownCommand{1}`;
  for (const factor of [1, 4, 8]) {
    const source = `$$${tex.replace(/\\/g, "\\".repeat(factor))}$$`;
    const { root, mathCalls } = pipeline(source, true);
    const invalid = classes(root, "invalid");
    assert.equal(invalid.length, 1);
    assert.equal(classes(root, "math-source-text")[0].textContent, source);
    assert.equal(mathCalls[0].tex, tex);
    assert.ok(invalid[0].dataset.mathError.includes("Undefined control sequence"));
    assert.ok(invalid[0].dataset.mathError.includes(String.raw`\MissumUnknownCommand`));
  }
});

test("legacy title metadata is removed while surrounding Markdown remains intact", () => {
  const source = "**MISSUM_SESSION_TITLE:** Verborgener Titel\n\n**Ergebnis:** `multiply`\n\n## **Missum\\_SESSION\\_TITLE:** Auch verborgen";
  const { root, sanitized } = pipeline(source);
  assert.equal(sanitized, "**Ergebnis:** `multiply`");
  assert.equal(tags(root, "strong")[0].textContent, "Ergebnis:");
  assert.equal(tags(root, "code")[0].textContent, "multiply");
  assert.ok(!root.textContent.includes("verborgen"));
});

test("math inside inline code stays literal", () => {
  const { root } = pipeline(String.raw`Nutze \(a+b\), aber kopiere \`$x$\``.replace(/\\`/g, "`"));
  assert.equal(classes(root, "math-source-text").length, 1);
  assert.equal(tags(root, "code")[0].textContent, "$x$");
});

test("persisted live Science notation uses the native conservative math presentation", () => {
  // These unmarked forms occur in the real magnetic-field run 671e9945… .
  const source = "Dipol: 7.94e22 A·m². Modell: dA/dt = A. Potential: μ²/(4c).";
  const { sanitized, root } = pipeline(source, true);
  assert.equal(sanitized, source);
  assert.deepEqual(classes(root, "math-source-text").map(node => node.textContent), [
    "7.94e22", "A·m²", "dA/dt = A", "μ²/(4c)"
  ]);
  const rendered = classes(root, "math-render");
  assert.equal(rendered.length, 4);
  assert.ok(rendered.every(node => node.dataset.mathTypeset === "true" && node.innerHTML.includes('class="katex"')));
  assert.equal(classes(root, "invalid").length, 0);
});

test("all four real geomagnetic equations typeset as whole formulas with the same native presentation", () => {
  const cases = [
    ["dA/dt = A(μ - c A²) + ξ(t)", "dA/dt = A (μ-c A^{2})+ξ (t)"],
    ["τ_K ∝ exp(ΔU/D_eff)", String.raw`τ_{K} \propto \exp (ΔU/D_{eff})`],
    ["ΔU = μ²/(4c)", "ΔU = μ^{2}/(4 c)"],
    ["A·m²", String.raw`A\cdot m^{2}`]
  ];
  for (const [source, latex] of cases) {
    const { root, mathCalls } = pipeline(source, true);
    assert.equal(classes(root, "math-selectable").length, 1, source);
    assert.equal(classes(root, "math-source-text")[0].textContent, source);
    assert.equal(classes(root, "math-render")[0].dataset.mathTypeset, "true");
    assert.equal(classes(root, "invalid").length, 0);
    assert.equal(mathCalls.length, 1);
    assert.equal(mathCalls[0].tex, latex);
  }
});

test("legacy scientific subscripts powers and exponents retain exact selectable copy text", async () => {
  const source = "m_P = 2.176e-8; τ_K; μ²; 1,5e+3; x^{12}.";
  const { root, copied } = pipeline(source, true);
  assert.deepEqual(classes(root, "math-source-text").map(node => node.textContent), [
    "m_P = 2.176e-8", "τ_K", "μ²", "1,5e+3", "x^{12}"
  ]);
  const wrapper = classes(root, "math-selectable")[0];
  wrapper.listeners.click({ preventDefault() {}, stopPropagation() {} });
  await Promise.resolve();
  assert.equal(copied.length, 1);
  assert.equal(copied[0].type, "message.copy");
  assert.equal(copied[0].payload.text, "m_P = 2.176e-8");
  assert.equal(wrapper.attributes["aria-label"], "LaTeX: m_P = 2.176e-8");
});

test("legacy recognition preserves paths identifiers URLs assignments and incomplete equations", () => {
  const source = String.raw`foo_bar build/τ_K code.py C:\tmp\x^2.py user@x².test and temperature=x² stay literal.
Incomplete: x² +
Explicit remains $a$.`;
  const { root } = pipeline(source, true);
  assert.deepEqual(classes(root, "math-source-text").map(node => node.textContent), ["$a$"]);
  assert.ok(root.textContent.includes(String.raw`build/τ_K code.py C:\tmp\x^2.py`));
  assert.ok(root.textContent.includes("temperature=x²"));
  assert.ok(root.textContent.includes("Incomplete: x² +"));
  const delta = pipeline("ΔU.py samples/ΔU ΔUnknown=1\nτ_K ∝ exp(ΔU/", true);
  assert.equal(classes(delta.root, "math-selectable").length, 0);
});

test("scientific legacy notation stays literal in single multi-backtick and unfinished code", () => {
  const source = "`x²` and ``A·m² with $x$``\n\n```python\nvalue = 'μ²'\n```\n\n`unfinished x²";
  const { root } = pipeline(source, true);
  assert.equal(classes(root, "math-selectable").length, 0);
  assert.deepEqual(tags(root, "code").map(node => node.textContent), ["x²", "A·m² with $x$", "value = 'μ²'"]);
  assert.ok(root.textContent.endsWith("`unfinished x²"));
});

test("bounded legacy parsing leaves oversized expressions readable and later explicit math typeset", () => {
  const oversized = "x²+".repeat(600) + "x²";
  const { root } = pipeline(`${oversized}\n\n$z$`, true);
  assert.deepEqual(classes(root, "math-source-text").map(node => node.textContent), ["$z$"]);
  assert.ok(root.textContent.startsWith(oversized));
});

test("legacy math presentation coexists with Markdown tables emphasis and safe source links", () => {
  const source = "| Größe | Wert |\n| --- | --- |\n| **Dipol** | 7.94e22 A·m² |\n\n[Original](https://example.org/result/x²) und $\\ell_P$.";
  const { root } = pipeline(source, true);
  assert.equal(tags(root, "table").length, 1);
  assert.equal(tags(root, "strong")[0].textContent, "Dipol");
  assert.equal(tags(root, "a")[0].href, "https://example.org/result/x%C2%B2");
  assert.deepEqual(classes(root, "math-source-text").map(node => node.textContent), ["7.94e22", "A·m²", "$\\ell_P$"]);
  assert.equal(classes(root, "invalid").length, 0);
});
