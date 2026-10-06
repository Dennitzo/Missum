// Read-only project navigation and reversible tool selection in a new temporary
// headless browser. Run only after the coordinator confirms the published host.
const { chromium } = require("playwright");
const assert = require("node:assert/strict");
const fs = require("node:fs/promises");
const path = require("node:path");

async function main() {
  const output = path.resolve(process.env.MISSUM_QA_OUTPUT || "artifacts/validation/lan-browser/parity/project-footer-final");
  await fs.mkdir(output, { recursive: true });
  const browser = await chromium.launch({ executablePath: "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", headless: true, chromiumSandbox: true });
  const sent = [], hostErrors = [], pageErrors = [], checks = [];
  try {
    const page = await browser.newPage({ viewport: { width: 1266, height: 913 }, colorScheme: "dark" });
    await page.addInitScript(() => {
      window.__projectFooterHostErrors = [];
      addEventListener("missum:host-message", event => { if (event.detail.type === "host.error") window.__projectFooterHostErrors.push(event.detail); });
    });
    page.on("pageerror", error => pageErrors.push(error.message));
    page.on("request", request => {
      if (request.method() !== "POST" || !new URL(request.url()).pathname.endsWith("/message")) return;
      try { sent.push(JSON.parse(request.postData())); } catch {}
    });
    page.on("websocket", socket => {
      socket.on("framesent", frame => { try { sent.push(JSON.parse(String(frame.payload))); } catch {} });
    });
    await page.goto(process.env.MISSUM_LAN_URL || "http://192.168.0.67:8080/assistant/");
    await page.waitForFunction(() => globalThis.missumApp?.getState()?.activeSessionId);
    const sessionId = process.env.MISSUM_QA_SESSION || "bad5d93d-e748-4974-acd1-412d4fe9017b";
    await page.evaluate(sessionId => missumBridge.post("session.open", { sessionId }), sessionId);
    await page.waitForFunction(sessionId => missumApp.getState().activeSessionId === sessionId && missumApp.getState().workspacePath, sessionId);
    const workspacePath = await page.evaluate(() => missumApp.getState().workspacePath);
    assert.equal(await page.locator(".workspace-context-chip").count(), 0);
    await page.locator("#tools-button").click();
    const web = page.getByRole("menuitemcheckbox", { name: "Websuche", exact: true });
    const originallySelected = await web.getAttribute("aria-checked") === "true";
    if (originallySelected) {
      await web.click();
      await page.waitForFunction(() => !document.querySelector('#active-tool-chips button[aria-label="Websuche abwählen"]'));
      await page.locator("#tools-button").click();
    }
    await web.click();
    const chip = page.getByRole("button", { name: "Websuche abwählen", exact: true });
    await chip.waitFor({ state: "visible" });
    for (const width of [1266, 900]) {
      await page.setViewportSize({ width, height: 913 }); await page.waitForTimeout(250);
      const data = await page.evaluate(() => {
        const rect = selector => { const r = document.querySelector(selector).getBoundingClientRect(); return { left: r.left, right: r.right, top: r.top, width: r.width }; };
        return { workspacePath: missumApp.getState().workspacePath, labels: [...document.querySelectorAll("#active-tool-chips > .active-tool-chip")].map(chip => chip.getAttribute("aria-label")), workspaceChips: document.querySelectorAll(".workspace-context-chip").length, plus: rect("#tools-button"), send: rect("#send"), model: rect("#reasoning-button"), chips: rect("#active-tool-chips") };
      });
      assert.equal(data.workspacePath, workspacePath); assert.equal(data.workspaceChips, 0);
      assert.deepEqual(data.labels, ["Websuche abwählen"]);
      assert.equal(data.send.width, 36); assert.ok(data.chips.right <= data.model.left);
      assert.ok(Math.abs(data.plus.top - data.send.top) < 1);
      checks.push({ width, ...data }); await page.screenshot({ path: path.join(output, `project-footer-${width}.png`), fullPage: true });
    }
    await page.setViewportSize({ width: 1266, height: 913 }); await page.waitForTimeout(250);
    await page.locator("#inspector-toggle").click(); await page.locator("#output-inspector").waitFor({ state: "visible" });
    assert.equal(await page.locator("#output-inspector .inspector-workspace").getAttribute("title"), workspacePath);
    assert.equal((await page.locator("#output-inspector").boundingBox()).width, 304);
    assert.equal(await page.locator(".workspace-context-chip").count(), 0);
    checks.push({ name: "project-outputs", workspacePath, text: await page.locator("#output-inspector").innerText() });
    await page.screenshot({ path: path.join(output, "project-outputs.png"), fullPage: true });
    await page.locator("#output-inspector").getByRole("button", { name: "Ausgaben schließen", exact: true }).click();
    await chip.click();
    await page.waitForFunction(() => !document.querySelector('#active-tool-chips button[aria-label="Websuche abwählen"]'));
    assert.equal(await page.locator("#active-tool-chips > .active-tool-chip").count(), 0);
    if (originallySelected) { await page.locator("#tools-button").click(); await web.click(); await chip.waitFor({ state: "visible" }); }
    hostErrors.push(...await page.evaluate(() => window.__projectFooterHostErrors));
    assert.ok(sent.some(command => command.type === "session.open" && command.payload?.sessionId === sessionId), "Both bridge transports are captured.");
    assert.ok(sent.filter(command => command.type === "action.invoke").length >= 2);
    assert.equal(sent.filter(command => ["chat.send", "chat.resume", "chat.steer", "settings.update", "session.projectCreate", "microphone.speak"].includes(command.type)).length, 0);
    assert.equal(hostErrors.length, 0); assert.equal(pageErrors.length, 0);
    const result = { sessionId, workspacePath, checks, sent, hostErrors, pageErrors };
    await fs.writeFile(path.join(output, "inspection.json"), JSON.stringify(result, null, 2));
    console.log(JSON.stringify(result, null, 2));
  } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
