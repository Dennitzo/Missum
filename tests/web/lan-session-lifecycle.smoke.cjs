// Read-only live QA using a separate temporary headless browser profile.
const { chromium } = require("playwright");
const fs = require("node:fs/promises");
const assert = require("node:assert/strict");
async function main() {
  const browser = await chromium.launch({ executablePath: "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", headless: true, chromiumSandbox: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1266, height: 913 }, colorScheme: "dark" });
    const sent = [], received = [], snapshots = [];
    page.on("websocket", socket => {
      socket.on("framesent", frame => { try { sent.push(JSON.parse(String(frame.payload))); } catch {} });
      socket.on("framereceived", frame => { try { received.push(JSON.parse(String(frame.payload))); } catch {} });
    });
    await page.goto(process.env.MISSUM_LAN_URL || "http://192.168.0.67:8080/assistant/");
    await page.waitForFunction(() => globalThis.missumApp?.getState()?.activeSessionId);
    await page.waitForTimeout(500);
    for (const id of String(process.env.MISSUM_QA_SESSIONS || "").split(",").filter(Boolean)) {
      await page.evaluate(id => missumBridge.post("session.open", { sessionId: id }), id);
      await page.waitForFunction(id => missumApp.getState().activeSessionId === id, id);
      await page.waitForTimeout(500);
      snapshots.push(await page.evaluate(() => { const state = missumApp.getState(); return { sessionId: state.activeSessionId, chatMode: state.chatMode, workspacePath: state.workspacePath, isRunning: state.isRunning, sessions: state.sessions.map(session => ({ id: session.id, title: session.title })), sessionGroups: state.sessionGroups }; }));
    }
    const errors = received.filter(item => ["host.error", "settings.error"].includes(item.type)).map(item => ({ ...item, command: sent.find(command => command.requestId === item.requestId) }));
    const result = { errors, snapshots, sent, eventTypes: received.map(item => item.type) };
    await fs.writeFile(process.env.MISSUM_QA_REPORT || "artifacts/validation/lan-browser/session-lifecycle-inspection.json", JSON.stringify(result, null, 2));
    console.log(JSON.stringify({ errors, commands: sent.map(item => ({ type: item.type, requestId: item.requestId, payload: item.payload })) }, null, 2));
    assert.equal(errors.length, 0, "Opening empty and completed Science sessions must have no host errors.");
  } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
