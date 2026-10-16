// Standalone real-Chromium check. Deliberately separate from windows/build.ps1.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { chromium } = require("playwright");

async function main() {
  const browser = await chromium.launch({
    executablePath: process.env.MISSUM_TEST_BROWSER || "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
    headless: true
  });
  try {
    const page = await browser.newPage();
    const web = path.resolve(__dirname, "../../src/Missum.App/Assets/DocumentRendering");
    await page.setContent('<main id="message-list"></main><textarea id="composer"></textarea><div id="outside"></div>');
    await page.addStyleTag({ path: path.join(web, "vendor/katex/0.16.10/katex.min.css") });
    await page.addScriptTag({ path: path.join(web, "vendor/katex/0.16.10/katex.min.js") });
    await page.addScriptTag({ path: path.join(web, "markdown.js") });
    const results = await page.evaluate(() => {
      const scope = document.getElementById("message-list");
      const selection = window.getSelection();
      const results = [];
      function render(source) {
        selection.removeAllRanges();
        scope.replaceChildren(missumMarkdown.render(source));
      }
      function copy(name, start, from, end, to, expected, reverse = false) {
        selection.setBaseAndExtent(reverse ? end : start, reverse ? to : from, reverse ? start : end, reverse ? from : to);
        const original = selection.toString();
        const data = new DataTransfer();
        const event = new ClipboardEvent("copy", { bubbles: true, cancelable: true, clipboardData: data });
        scope.dispatchEvent(event);
        results.push({ name, actual: data.getData("text/plain"), expected,
          prevented: event.defaultPrevented, shouldPrevent: expected !== null,
          selectionPreserved: selection.toString() === original });
      }
      render(String.raw`Davor $\frac{x}{y}=z^2$ danach.`);
      const paragraph = scope.querySelector("p");
      const formula = scope.querySelector(".math-source-text").firstChild;
      copy("partial prose and inline formula", paragraph.firstChild, 2, paragraph.lastChild, 3, String.raw`vor $\frac{x}{y}=z^2$ da`);
      copy("backward selection", paragraph.firstChild, 2, paragraph.lastChild, 3, String.raw`vor $\frac{x}{y}=z^2$ da`, true);
      copy("partial formula includes complete original", formula, 3, formula, 8, String.raw`$\frac{x}{y}=z^2$`);
      copy("ordinary text uses native clipboard", paragraph.firstChild, 1, paragraph.firstChild, 4, null);
      const display = String.raw`$$\begin{pmatrix}a_1 & b^2\\c & d\end{pmatrix}$$`;
      render("Davor.\n\n" + display + "\n\n$$x=y$$\n\nDanach.");
      copy("multiple block formulas retain boundaries", scope, 0, scope, scope.childNodes.length,
        "Davor.\n" + display + "\n$$x=y$$\nDanach.");
      render("$x^2$\n\n```python\na = 1\n\n  b = 2\n```\n\nEnde.");
      copy("code indentation and blank lines", scope, 0, scope, scope.childNodes.length,
        "$x^2$\na = 1\n\n  b = 2\nEnde.");
      render("| Formel | Wert |\n|---|---|\n| $x_1$ | 2 |\n| $y^2$ | 3 |");
      copy("table cells and formulas", scope, 0, scope, scope.childNodes.length,
        "Formel\tWert\n$x_1$\t2\n$y^2$\t3");
      const outside = document.getElementById("outside");
      outside.textContent = "Außerhalb";
      copy("selection outside chat is not intercepted", scope, 0, outside.firstChild, 5, null);
      render(String.raw`Ungültig $\notARealCommand{x}$ bleibt erhalten.`);
      copy("invalid formula keeps source", scope, 0, scope, scope.childNodes.length,
        String.raw`Ungültig $\notARealCommand{x}$ bleibt erhalten.`);
      return results;
    });
    for (const result of results) {
      assert.equal(result.prevented, result.shouldPrevent, result.name);
      assert.equal(result.actual, result.expected ?? "", result.name);
      assert.equal(result.selectionPreserved, true, result.name);
    }
    const artifact = path.resolve(__dirname, "../../artifacts/formula-copy-20261011");
    fs.mkdirSync(artifact, { recursive: true });
    fs.writeFileSync(path.join(artifact, "web-clipboard-validation.json"), JSON.stringify({ passed: true, checks: results }, null, 2));
    console.log(`Passed ${results.length} real Chromium selection/clipboard checks.`);
  } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
