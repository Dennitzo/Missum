// Optional live QA: NODE_PATH must contain a Playwright installation.
// Launches a separate headless browser with Playwright's temporary profile.
const fs = require("node:fs/promises");
const path = require("node:path");
const { chromium } = require("playwright");
const assert = require("node:assert/strict");

async function main() {
  const target = process.env.MISSUM_LAN_URL || "http://192.168.0.67:8080/assistant/";
  const browserPath = process.env.MISSUM_TEST_BROWSER || "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe";
  const output = path.resolve(process.env.MISSUM_QA_OUTPUT || "artifacts/validation/lan-browser/headless");
  await fs.mkdir(output, { recursive: true });
  const browser = await chromium.launch({ executablePath: browserPath, headless: true, chromiumSandbox: true });
  let activePage; const collectedCommands = [], failedResponses = [];
  try {
    const context = await browser.newContext({ viewport: { width: Number(process.env.MISSUM_QA_WIDTH) || 1266, height: Number(process.env.MISSUM_QA_HEIGHT) || 913 }, colorScheme: process.env.MISSUM_QA_COLOR_SCHEME || "dark", acceptDownloads: true });
    const page = await context.newPage();
    activePage = page;
    const errors = [], networkFailures = [], commands = collectedCommands;
    page.on("response", response => { if (response.status() >= 400) failedResponses.push({ url: response.url(), status: response.status() }); });
    page.on("pageerror", error => { errors.push(error.message); console.log(JSON.stringify({ pageerror: error.message })); });
    page.on("console", message => { if (message.type() === "error") console.log(JSON.stringify({ browserConsole: message.text() })); });
    page.on("requestfailed", request => networkFailures.push({ url: request.url(), error: request.failure()?.errorText }));
    page.on("websocket", socket => socket.on("framesent", frame => {
      try { const item = JSON.parse(String(frame.payload)); commands.push({ type: item.type, requestId: item.requestId, payload: item.payload }); } catch { /* Browser control frame. */ }
    }));
    await page.addInitScript(() => {
      window.__missumQaEvents = [];
      window.addEventListener("missum:host-message", event => {
        const { type, payload, requestId } = event.detail;
        window.__missumQaEvents.push({ type, payload, requestId });
        if (window.__missumQaEvents.length > 2000) window.__missumQaEvents.shift();
      });
    });
    const response = await page.goto(target, { waitUntil: "domcontentloaded" });
    console.log(JSON.stringify({ loaded: response.status(), runtime: await page.evaluate(() => ({ title: document.title, bridge: Boolean(globalThis.missumBridge), app: Boolean(globalThis.missumApp) })) }));
    await page.screenshot({ path: path.join(output, "assistant-loading.png"), fullPage: true });
    await page.waitForFunction(() => globalThis.missumApp?.getState()?.activeSessionId, { timeout: 30000 });
    await page.waitForFunction(() => document.getElementById("model-picker").options.length > 1, null, { timeout: 45000 });
    async function selectPersistedSession(sessionId) {
      // Read-only setup: ask the real host to select an existing saved chat.
      // Lists are mode-scoped; known fixture IDs come from earlier genuine UI
      // responses, and the native mode flyout has no projectless history list.
      if (await page.evaluate(id => missumApp.getState().activeSessionId !== id, sessionId)) {
        await page.evaluate(id => missumBridge.post("session.open", { sessionId: id }), sessionId);
        await page.waitForFunction(id => missumApp.getState().activeSessionId === id, sessionId);
      }
    }
    if (process.env.MISSUM_QA_PARITY === "1") {
      const sessionId = process.env.MISSUM_QA_REFERENCE_SESSION || "5e5ddbe0-c599-4c69-ba26-2458f2a09a1d";
      await selectPersistedSession(sessionId);
    }
    await page.locator("#message-scroll").evaluate(element => { element.scrollTop = 0; });
    await page.screenshot({ path: path.join(output, "assistant-initial.png"), fullPage: true });
    const details = await page.evaluate(() => ({
      title: document.title, url: location.href, secureContext: isSecureContext,
      clientId: missumBridge.clientId, tabId: missumBridge.tabId, capabilities: missumBridge.capabilities,
      sessionId: missumApp.getState().activeSessionId, mode: missumApp.getState().chatMode,
      selectedModelId: missumApp.getState().selectedModelId,
      models: [...document.getElementById("model-picker").options].map(option => ({ value: option.value, label: option.textContent })),
      sidebarWidth: document.querySelector(".session-pane").getBoundingClientRect().width,
      appearance: { prefersDark: matchMedia("(prefers-color-scheme: dark)").matches, theme: document.documentElement.dataset.theme, userBubble: getComputedStyle(document.documentElement).getPropertyValue("--user-bubble").trim(), surfaceRaised: getComputedStyle(document.documentElement).getPropertyValue("--surface-raised").trim() },
      buttons: [...document.querySelectorAll("button")].filter(element => element.getBoundingClientRect().width).map(element => ({ id: element.id, title: element.title, aria: element.getAttribute("aria-label"), text: element.textContent.trim(), disabled: element.disabled })),
      events: window.__missumQaEvents.map(event => ({ type: event.type, requestId: event.requestId }))
    }));
    const result = { status: response.status(), details, errors, networkFailures, commands, checks: [] };
    const check = (name, data = {}) => { result.checks.push({ name, ...data }); console.log(JSON.stringify({ check: name, ...data })); };
    const eventCount = () => page.evaluate(() => window.__missumQaEvents.length);
    async function waitEvent(type, since = 0, timeout = 45000) {
      await page.waitForFunction(({ type, since }) => window.__missumQaEvents.slice(since).some(event => event.type === type), { type, since }, { timeout });
      return page.evaluate(({ type, since }) => window.__missumQaEvents.slice(since).find(event => event.type === type), { type, since });
    }
    async function picture(name) { await page.screenshot({ path: path.join(output, `${name}.png`), fullPage: true }); }
    assert.equal(details.secureContext, false);
    assert.deepEqual(details.capabilities, { microphone: false, screen: false });
    assert.equal(details.sidebarWidth, 288);
    if (details.appearance.theme === "dark") assert.equal(details.appearance.userBubble, details.appearance.surfaceRaised, "Native gray bubbles must override the legacy dark-media purple color.");
    check("http-lan-initial", { status: response.status(), model: details.selectedModelId, clientId: details.clientId });
    const cssPage = await context.newPage();
    await cssPage.setContent('<html><body><div class="message user"><div class="message-body">Palette QA</div></div></body></html>');
    for (const file of ["styles.css", "browser-panels.css"]) {
      const resource = await page.request.get(new URL(file, target).href);
      assert.equal(resource.status(), 200);
      await cssPage.addStyleTag({ content: await resource.text() });
    }
    for (const theme of ["light", "dark", "high-contrast"]) {
      const palette = await cssPage.evaluate(theme => { document.documentElement.dataset.theme = theme; const style = getComputedStyle(document.documentElement); return Object.fromEntries(["--user-bubble", "--layer-strong", "--surface-raised", "--text", "--muted"].map(name => [name, style.getPropertyValue(name).trim()])); }, theme);
      assert.equal(palette["--user-bubble"], theme === "high-contrast" ? "Canvas" : palette[theme === "dark" ? "--surface-raised" : "--layer-strong"]);
      if (theme === "high-contrast") { assert.equal(palette["--text"], "CanvasText"); assert.equal(palette["--layer-strong"], "Canvas"); }
      check("native-theme-cascade", { theme, palette });
    }
    const profileLayout = await cssPage.evaluate(() => {
      document.documentElement.dataset.theme = "dark";
      const footer = document.createElement("div"); footer.id = "active-tool-chips"; footer.style.width = "220px";
      footer.innerHTML = '<div class="active-tool-chip" data-icon="research"><svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="8" /></svg><select class="active-tool-chip__profile"><option>Mathematische Untersuchung</option></select><button class="active-tool-chip__details">Stand</button><button class="active-tool-chip__remove">×</button></div>';
      document.body.append(footer);
      const chip = footer.firstElementChild, profile = chip.querySelector("select"), status = chip.querySelector(".active-tool-chip__details");
      const rect = element => { const r = element.getBoundingClientRect(); return { left: r.left, right: r.right, width: r.width }; };
      return { chip: rect(chip), profile: rect(profile), status: rect(status) };
    });
    assert.ok(profileLayout.profile.width > 0 && profileLayout.status.width > 0);
    assert.ok(profileLayout.profile.right <= profileLayout.status.left + 1 && profileLayout.status.right <= profileLayout.chip.right + 1);
    check("research-profile-published-css-layout", { fixture: true, ...profileLayout });
    await cssPage.close();
    if (process.env.MISSUM_QA_PARITY === "1") {
      await page.locator("#mode-switcher-button").click(); await picture("mode-menu");
      assert.equal(await page.locator("#mode-switcher-menu [data-chat-mode]").count(), 3);
      assert.equal(await page.locator("#mode-switcher-menu .session-item").count(), 0);
      const modeBounds = await page.locator("#mode-switcher-menu").boundingBox();
      assert.equal(modeBounds.width, 240); await page.locator("#mode-switcher-button").click();
      await page.locator(".coding-step__title").first().click();
      await page.waitForFunction(() => document.querySelector(".coding-step__title")?.parentElement.open === true);
      await page.locator(".coding-step__title").first().locator("..").locator(".coding-step__content").waitFor({ state: "visible" }); await picture("tool-expanded");
      await page.locator(".coding-step__title").first().click();
      await page.locator("#prompt").fill("Prüfe die vorhandene Rechnung und erläutere das Ergebnis kurz.\nDieser längere Entwurf bleibt nur in diesem Browserclient gespeichert.");
      await page.locator("#reasoning-button").click();
      await page.waitForFunction(() => document.getElementById("reasoning-range").disabled === false);
      await picture("composer-model-popup");
      await page.locator("#open-model-dialog").click(); await picture("model-dialog");
      await page.locator("#cancel-local-model").click();
      await page.locator("#toggle-session-search").click(); await picture("sidebar-search");
      await page.locator("#toggle-session-search").click();
      await page.locator("#prompt").fill("");
      await page.locator("#inspector-toggle").click(); await picture("outputs");
      const inspectorBounds = await page.locator("#output-inspector").boundingBox();
      assert.equal(inspectorBounds.width, 304); check("native-output-overlay", inspectorBounds);
      await page.locator("#inspector-toggle").click();
      check("native-parity-views", { screenshots: ["assistant-initial", "tool-expanded", "composer-model-popup", "model-dialog", "sidebar-search"] });
      await page.setViewportSize({ width: 1840, height: 913 });
      await page.waitForTimeout(250); // Let the app's sidebar resize transition settle.
      const wideBounds = await page.evaluate(() => { const rect = selector => { const value = document.querySelector(selector).getBoundingClientRect(); return { x: value.x, width: value.width, right: value.right }; }; return { list: rect("#message-list"), scroll: rect("#message-scroll"), assistant: rect(".message.assistant .message-body"), listMaxWidth: getComputedStyle(document.getElementById("message-list")).maxWidth }; });
      assert.equal(wideBounds.listMaxWidth, "none"); assert.ok(wideBounds.list.width > 1080, "The conversation must expand beyond the old fixed column on a wide window.");
      assert.ok(wideBounds.list.right - wideBounds.assistant.right < 1, "Assistant responses use the available message width.");
      await picture("chat-wide"); check("native-full-width-chat", wideBounds);
      await page.setViewportSize({ width: Number(process.env.MISSUM_QA_WIDTH) || 1266, height: Number(process.env.MISSUM_QA_HEIGHT) || 913 });
      await page.locator("#tools-button").click(); await picture("plus-menu");
      await page.getByRole("menuitem", { name: /Dateien und Ordner/ }).click(); await picture("plus-files-submenu");
      await page.locator("#prompt").click();
      if (process.env.MISSUM_QA_TOOL_CODING_SESSION) {
        await selectPersistedSession(process.env.MISSUM_QA_TOOL_CODING_SESSION);
        await page.locator("#message-scroll").evaluate(element => { element.scrollTop = 0; });
        await page.locator(".coding-step__title").first().click();
        await page.waitForFunction(() => document.querySelector(".coding-step__title")?.parentElement.open === true);
        await page.locator(".coding-step__title").first().locator("..").locator(".coding-step__content").waitFor({ state: "visible" }); await picture("codex-tool-expanded");
        await selectPersistedSession(process.env.MISSUM_QA_REFERENCE_SESSION || "5e5ddbe0-c599-4c69-ba26-2458f2a09a1d");
      }
      if (process.env.MISSUM_QA_FOOTER === "1") {
        await page.locator("#tools-button").click(); await page.getByRole("menuitemcheckbox", { name: "Websuche", exact: true }).click();
        await page.locator('#active-tool-chips button.active-tool-chip[aria-label="Websuche abwählen"]').waitFor();
        await picture("composer-selected-tool");
        await page.setViewportSize({ width: 900, height: 913 }); await page.waitForTimeout(250); await picture("composer-selected-tool-narrow");
        const footer = await page.evaluate(() => { const rect = selector => { const r = document.querySelector(selector).getBoundingClientRect(); return { left: r.left, right: r.right, top: r.top, bottom: r.bottom, width: r.width, height: r.height }; }; return { composer: rect(".composer"), chips: rect("#active-tool-chips"), plus: rect("#tools-button"), send: rect("#send"), model: rect("#reasoning-button"), abovePromptChips: document.querySelectorAll("#context-strip .active-tool-chip:not(.composer-speech-status):not(.attachment-summary)").length }; });
        assert.equal(footer.abovePromptChips, 0); assert.equal(footer.send.width, 36);
        assert.ok(footer.send.right <= footer.composer.right && footer.chips.right <= footer.model.left);
        assert.ok(Math.abs(footer.plus.top - footer.send.top) < 1); check("native-footer-tool-chip", footer);
        await page.locator('#active-tool-chips button.active-tool-chip[aria-label="Websuche abwählen"]').click();
        await page.waitForFunction(() => !document.querySelector('#active-tool-chips button.active-tool-chip[aria-label="Websuche abwählen"]'));
        await page.setViewportSize({ width: Number(process.env.MISSUM_QA_WIDTH) || 1266, height: Number(process.env.MISSUM_QA_HEIGHT) || 913 });
      }
      await fs.writeFile(path.join(output, "parity-inspection.json"), JSON.stringify({ status: response.status(), errors, checks: result.checks, screenshots: ["assistant-initial", "tool-expanded", "mode-menu", "outputs", "plus-menu", "chat-wide", "codex-tool-expanded"] }, null, 2));
    }
    if (process.env.MISSUM_QA_SETTINGS_OVERLAY === "1") {
      await page.locator("#inspector-toggle").click();
      await page.locator("#output-inspector").waitFor({ state: "visible" });
      const before = await eventCount();
      await page.locator("#browser-menu-0").click();
      await page.getByRole("menuitem", { name: "Einstellungen", exact: true }).click();
      const snapshot = (await waitEvent("settings.snapshot", before)).payload;
      assert.equal(await page.locator("#output-inspector").isVisible(), false);
      const hit = await page.locator("#settings-save").evaluate(button => { const r = button.getBoundingClientRect(), target = document.elementFromPoint(r.x + r.width / 2, r.y + r.height / 2); return { intercepted: !button.contains(target), target: target?.id || target?.className, x: r.x, y: r.y, width: r.width, height: r.height }; });
      assert.equal(hit.intercepted, false, "Save is not covered by the assistant's open outputs overlay.");
      const savedSince = await eventCount(); await page.locator("#settings-save").click();
      const saved = (await waitEvent("settings.changed", savedSince)).payload;
      assert.deepEqual(saved.values, snapshot.values, "The visibility test saves the unchanged settings.");
      await picture("settings-save-with-outputs-hidden");
      await page.getByRole("button", { name: "Zum Assistenten", exact: true }).click();
      await page.locator("#output-inspector").waitFor({ state: "visible" });
      check("settings-overlay-save-hit-test", { ...hit, revision: saved.revision, overlayRestored: true });
      await page.locator("#inspector-toggle").click();
    }
    if (process.env.MISSUM_QA_FOLDER === "1") {
      const parentFolder = path.resolve("artifacts/validation/lan-browser"), workspace = path.join(parentFolder, "workspace");
      const unicodeName = "Ordner ÄÖ 漢字 & Test", unicodePath = path.join(parentFolder, unicodeName);
      async function openFolderPicker() { await page.locator(".sidebar-projects-heading").hover(); await page.locator("#new-session").click(); await page.locator("#server-workspace-overlay").waitFor({ state: "visible" }); }
      await openFolderPicker();
      await page.locator(".server-workspace-place").filter({ hasText: /^Dieser PC$/ }).click();
      await page.waitForFunction(() => document.querySelectorAll("#server-workspace-list button").length > 0 && !document.getElementById("server-workspace-status").textContent.includes("geladen"));
      await picture("workspace-drives");
      await page.locator("#server-workspace-path").fill(path.join(parentFolder, "missing-folder-for-qa")); await page.locator("#server-workspace-path").press("Enter");
      await page.waitForFunction(() => document.getElementById("server-workspace-status").classList.contains("server-workspace-status--error"));
      assert.equal(await page.locator("#server-workspace-select").isEnabled(), false); await picture("workspace-invalid-path");
      await page.waitForFunction(() => document.querySelectorAll(".toast").length === 0, null, { timeout: 10000 });
      await page.locator("#server-workspace-path").fill(parentFolder); await page.locator("#server-workspace-path").press("Enter");
      await page.locator('#server-workspace-list button[aria-label="workspace"]').waitFor(); await picture("workspace-folder-list");
      await page.locator("#server-workspace-search").fill("ÄÖ");
      await page.locator(`#server-workspace-list button[aria-label="${unicodeName}"]`).waitFor();
      await page.waitForFunction(() => !document.querySelector('#server-workspace-list button[aria-label="workspace"]') && !document.getElementById("server-workspace-select").disabled);
      assert.equal(await page.locator('#server-workspace-list button[aria-label="workspace"]').count(), 0); await picture("workspace-unicode-filter");
      await page.locator(`#server-workspace-list button[aria-label="${unicodeName}"]`).dblclick();
      await page.waitForFunction(path => document.getElementById("server-workspace-path").value === path && !document.getElementById("server-workspace-select").disabled, unicodePath); await picture("workspace-unicode-open");
      await page.getByRole("button", { name: "Übergeordneter Ordner", exact: true }).click();
      await page.locator('#server-workspace-list button[aria-label="workspace"]').waitFor();
      const row = page.locator('#server-workspace-list button[aria-label="workspace"]'); await row.click();
      await picture("workspace-selected-folder");
      const beforeCreate = commands.filter(command => command.type === "session.projectCreate").length;
      await page.getByRole("button", { name: "Abbrechen", exact: true }).click();
      assert.equal(commands.filter(command => command.type === "session.projectCreate").length, beforeCreate);
      for (const mode of process.env.MISSUM_QA_FOLDER_CREATE === "0" ? [] : ["general", "coding", "claudescience"]) {
        const chosenName = mode === "general" ? unicodeName : "workspace", chosenPath = mode === "general" ? unicodePath : workspace;
        await page.locator("#mode-switcher-button").click(); await page.locator(`#mode-switcher-menu [data-chat-mode="${mode}"]`).click(); await page.waitForFunction(mode => missumApp.getState().chatMode === mode, mode);
        const old = await page.evaluate(() => missumApp.getState().activeSessionId);
        await openFolderPicker(); await page.locator("#server-workspace-path").fill(parentFolder); await page.locator("#server-workspace-path").press("Enter");
        await page.locator(`#server-workspace-list button[aria-label="${chosenName}"]`).click(); await page.locator("#server-workspace-select").click();
        await page.waitForFunction(({ old, workspace }) => missumApp.getState().activeSessionId !== old && missumApp.getState().workspacePath === workspace, { old, workspace: chosenPath });
        assert.equal(await page.evaluate(() => missumApp.getState().chatMode), mode); await picture(`workspace-created-${mode}`);
        check("server-workspace-selected", { mode, workspacePath: await page.evaluate(() => missumApp.getState().workspacePath), sessionId: await page.evaluate(() => missumApp.getState().activeSessionId) });
      }
      if (process.env.MISSUM_QA_PEER_FOLDER) {
        const selectedPath = path.resolve(process.env.MISSUM_QA_PEER_FOLDER), name = path.basename(selectedPath);
        await page.locator("#mode-switcher-button").click(); await page.locator('#mode-switcher-menu [data-chat-mode="general"]').click();
        await page.waitForFunction(() => missumApp.getState().chatMode === "general");
        const old = await page.evaluate(() => missumApp.getState().activeSessionId);
        await openFolderPicker(); await page.locator("#server-workspace-path").fill(parentFolder); await page.locator("#server-workspace-path").press("Enter");
        await page.locator("#server-workspace-list button").filter({ hasText: name }).click(); await page.locator("#server-workspace-select").click();
        await page.waitForFunction(({ old, selectedPath }) => missumApp.getState().activeSessionId !== old && missumApp.getState().workspacePath === selectedPath, { old, selectedPath });
        check("server-workspace-native-peer", { name, workspacePath: selectedPath, sessionId: await page.evaluate(() => missumApp.getState().activeSessionId) });
        await picture("workspace-native-peer-created");
      }
    }
    if (process.env.MISSUM_QA_OBSERVE_SESSION) {
      await selectPersistedSession(process.env.MISSUM_QA_OBSERVE_SESSION);
      if (["1", "precise"].includes(process.env.MISSUM_QA_STEER_ARTIFACT)) {
        const since = await eventCount(), commandStart = commands.length;
        const correction = process.env.MISSUM_QA_STEER_ARTIFACT === "precise"
          ? "Einmalige abschließende Pfadkorrektur für den bestehenden Auftrag: Python workingDirectory '.' bedeutet /sandbox/work. Path('artifacts') war daher falsch und erzeugte work/artifacts/. Verwende im Skript ausdrücklich out_dir = pathlib.Path('../artifacts'); out_dir.mkdir(parents=True, exist_ok=True); plt.savefig(out_dir / 'testwerte_balkendiagramm.png'). Führe das korrigierte Skript einmal mit research.code.execute aus. Lies dann den ECHTEN outputHashes-Schlüssel aus dessen Ergebnis. Nur wenn er exakt artifacts/testwerte_balkendiagramm.png ist, trage den neuen experimentRecordId und exakt diesen Pfad in section.figureCaptions und experimentIds ein und prüfe research.deliverables.verify einmal. Finalisiere danach. Falls die Prüfung immer noch scheitert, beende mit ehrlicher Limitierung und den bereits korrekt berechneten Zahlen; keine weitere Schleife, keine Literatur und keine neuen Subagenten."
          : "Korrektur für den laufenden Auftrag: Die bestehende Diagrammdatei unter work/testwerte_balkendiagramm.png genügt der Publikationsprüfung nicht. Erzeuge das gleiche PNG mit Python unter artifacts/testwerte_balkendiagramm.png, führe das Skript erneut über research.code.execute aus und verwende den NEUEN tatsächlichen experimentRecordId samt gemessenem outputHashes-Pfad artifacts/testwerte_balkendiagramm.png in section.figureCaptions und experimentIds. Prüfe danach einmal research.deliverables.verify und finalisiere die kurze Analyse. Ändere keine Zahlen, suche keine Literatur und starte keinen neuen Subagenten.";
        await page.locator("#prompt").fill(correction);
        assert.equal(await page.locator("#send").getAttribute("aria-label"), "Umlenken");
        await page.locator("#send").click();
        await waitEvent("chat.steer.accepted", since, 60000);
        const steering = commands.slice(commandStart).find(command => command.type === "chat.steer");
        assert.ok(steering); assert.equal(commands.slice(commandStart).filter(command => command.type === "chat.send").length, 0);
        check("existing-science-steered", { inputId: steering.payload.inputId, expectedRunId: steering.payload.expectedRunId, sessionId: steering.payload.sessionId }); await picture("science-steered");
      }
      async function jobProgress() { return page.evaluate(() => { const current = missumApp.getState(), latest = [...current.messages].reverse().find(message => message.role === "assistant"); const child = [...__missumQaEvents].reverse().find(event => ["subagent.snapshot", "subagents.snapshot"].includes(event.type))?.payload; return { sessionId: current.activeSessionId, running: current.isRunning, status: current.runStatus, updatedAt: latest?.updatedAt, tools: (latest?.toolSteps || []).map(step => ({ tool: step.tool, status: step.status, updatedAt: step.updatedAt })), subagent: child, recentEvents: __missumQaEvents.slice(-8).map(event => event.type) }; }); }
      check("existing-job-observed", await jobProgress()); await picture("science-in-progress");
      const observationTimer = setInterval(() => jobProgress().then(progress => console.log(JSON.stringify({ check: "existing-job-progress", ...progress }))).catch(() => {}), 30000);
      try {
      if (process.env.MISSUM_QA_ALLOW_RUNNING !== "1") await page.waitForFunction(() => !missumApp.getState().isRunning && missumApp.getState().messages.some(message => message.role === "assistant" && message.content && message.status === "completed"), null, { timeout: 900000 });
      } finally { clearInterval(observationTimer); }
      const scienceResult = await page.evaluate(() => ({ sessionId: missumApp.getState().activeSessionId, messages: missumApp.getState().messages, scientificResearch: missumApp.getState().scientificResearch, isRunning: missumApp.getState().isRunning, tabs: [...document.querySelectorAll(".session-view-tab")].map(item => item.textContent), sidebar: document.getElementById("session-list").innerText }));
      check(scienceResult.isRunning ? "existing-science-running" : "existing-science-completed", scienceResult);
      assert.equal(scienceResult.tabs.includes("Forschung"), false);
      await picture("claude-science");
      await page.getByRole("tab", { name: "Publikation", exact: true }).click();
      await page.waitForSelector("#browser-view-panel .publication-frame", { timeout: 180000 });
      const publication = await page.evaluate(() => ({ text: document.getElementById("browser-view-panel").innerText, frames: [...document.getElementById("browser-view-panel").querySelectorAll("iframe")].map(frame => frame.src) }));
      const pdfResponse = await page.request.get(publication.frames[0]);
      assert.equal(pdfResponse.status(), 200); assert.equal((await pdfResponse.body()).subarray(0, 4).toString(), "%PDF");
      publication.pdfBytes = (await pdfResponse.body()).length;
      await page.waitForTimeout(2500); // The real PDF viewer loads after the application inserts its frame.
      await picture("science-publication");
      check("science-publication", publication);
      await page.getByRole("tab", { name: "Simulation", exact: true }).click();
      await page.waitForFunction(() => [...document.getElementById("browser-view-panel").querySelectorAll("img")].some(image => image.complete && image.naturalWidth > 0), null, { timeout: 180000 });
      await picture("science-simulation");
      check("science-simulation", await page.evaluate(() => ({ text: document.getElementById("browser-view-panel").innerText, images: [...document.getElementById("browser-view-panel").querySelectorAll("img")].map(image => ({ src: image.src, width: image.naturalWidth, height: image.naturalHeight })), frames: [...document.getElementById("browser-view-panel").querySelectorAll("iframe")].map(frame => frame.src) })));
      assert.equal(commands.filter(command => command.type === "chat.send").length, 0, "Observing the existing job never submits it a second time.");
    }
    if (process.env.MISSUM_QA_INTERACTIVE === "1") {
      const workspace = path.resolve("artifacts/validation/lan-browser/workspace");
      const fixture = path.resolve("artifacts/validation/lan-browser/testwerte.txt");
      const chosenModel = details.models.find(model => /Qwen3\.8-27B/i.test(model.value))?.value;
      assert.ok(chosenModel, "Installed Qwen test model must be in the real model menu.");
      async function chooseModel() {
        await page.locator("#reasoning-button").click();
        await page.locator("#open-model-dialog").click();
        await page.locator("#model-picker").selectOption(chosenModel);
        if (await page.locator("#local-model-effort option[value=none]").count()) await page.locator("#local-model-effort").selectOption("none");
        await page.locator("#apply-local-model").click();
        await page.waitForFunction(model => missumApp.getState().selectedModelId === model, chosenModel);
      }
      await chooseModel();
      async function noReasoning() {
        await page.waitForFunction(() => !document.getElementById("reasoning-button").disabled, null, { timeout: 45000 });
        await page.locator("#reasoning-button").click();
        await page.getByRole("menuitemradio", { name: "Aus", exact: true }).click();
        await page.locator("#prompt").click();
        await page.waitForFunction(() => document.getElementById("reasoning-button").title === "Reasoning: Aus");
      }
      await noReasoning();
      check("model-reasoning-selection", { model: chosenModel, reasoning: "none" });
      let before = await eventCount();
      await page.locator("#browser-menu-0").click();
      await page.getByRole("menuitem", { name: "Einstellungen", exact: true }).click();
      const settings = (await waitEvent("settings.snapshot", before)).payload;
      await page.getByRole("button", { name: "Verbindung testen und Fähigkeiten laden", exact: true }).click();
      const connection = (await waitEvent("settings.connectionResult", before)).payload;
      assert.equal(connection.isReachable, true);
      await page.locator("#settings-theme").selectOption("light");
      assert.equal(await page.evaluate(() => document.documentElement.dataset.theme), "light");
      await picture("settings-preview");
      page.once("dialog", dialog => dialog.accept());
      before = await eventCount(); await page.getByRole("button", { name: "Aktuellen Stand laden", exact: true }).click();
      await waitEvent("settings.snapshot", before);
      await page.getByRole("button", { name: "Zum Assistenten", exact: true }).click();
      check("settings-unsaved-connection-preview", { revision: settings.revision, gatewayReady: connection.isReady });
      async function createProject() {
        const old = await page.evaluate(() => missumApp.getState().activeSessionId);
        await page.locator(".sidebar-projects-heading").hover();
        await page.locator("#new-session").click();
        await page.locator("#server-workspace-path").fill(workspace); await page.locator("#server-workspace-path").press("Enter");
        await page.waitForFunction(path => document.getElementById("server-workspace-path").value === path && !document.getElementById("server-workspace-select").disabled, workspace);
        await page.locator("#server-workspace-select").click();
        await page.waitForFunction(old => missumApp.getState().activeSessionId !== old && !missumApp.getState().isRunning, old, { timeout: 45000 });
      }
      async function upload() {
        await page.locator("#tools-button").click();
        await page.getByRole("menuitem", { name: /Dateien und Ordner/ }).click();
        const pick = page.waitForEvent("filechooser");
        await page.getByRole("menuitem", { name: "Dateien hinzufügen", exact: true }).click();
        await (await pick).setFiles(fixture);
        await page.waitForFunction(() => missumApp.getState().attachments.some(item => /testwerte\.txt/.test(item.fileName || item.name || "")) || missumApp.getState().documents.some(item => /testwerte\.txt/.test(item.fileName || item.name || "")), null, { timeout: 45000 });
      }
      async function send(prompt, name, reload = false) {
        await page.locator("#prompt").fill(prompt);
        const session = await page.evaluate(() => missumApp.getState().activeSessionId);
        before = await eventCount();
        await page.locator("#send").click();
        await waitEvent("chat.started", before, 60000);
        check(`${name}-started`, { session });
        if (reload) {
          const client = await page.evaluate(() => missumBridge.clientId);
          await page.reload({ waitUntil: "domcontentloaded" });
          await page.waitForFunction(session => missumApp?.getState()?.activeSessionId === session, session);
          assert.equal(await page.evaluate(() => missumBridge.clientId), client);
          before = 0;
          check(`${name}-reload`, { clientId: client });
        }
        await page.waitForFunction(() => !missumApp.getState().isRunning && !missumApp.getState().isAiBusy && missumApp.getState().messages.some(message => message.role === "assistant" && message.content && !["pending", "streaming"].includes(message.status)), null, { timeout: name === "claude-science" ? 900000 : 240000 });
        const response = await page.evaluate(() => {
          const state = missumApp.getState(), message = [...state.messages].reverse().find(item => item.role === "assistant");
          return { sessionId: state.activeSessionId, messageId: message.id, text: message.content, status: message.status, toolSteps: message.toolSteps, artifacts: message.artifacts, runStatus: state.runStatus };
        });
        await picture(name);
        check(`${name}-completed`, response);
        return response;
      }
      let general;
      if (process.env.MISSUM_QA_RESUME_SESSION) {
        const sessionId = process.env.MISSUM_QA_RESUME_SESSION;
        const title = await page.evaluate(id => missumApp.getState().sessions.find(session => session.id === id)?.title, sessionId);
        assert.ok(title); await page.locator("#toggle-session-search").click(); await page.locator("#session-search").fill(title);
        await page.locator("#session-list .session-item").filter({ hasText: title }).first().locator(".session-item__open").click();
        await page.waitForFunction(id => missumApp.getState().activeSessionId === id, sessionId); await page.locator("#toggle-session-search").click();
        general = await page.evaluate(() => { const current = missumApp.getState(), message = [...current.messages].reverse().find(item => item.role === "assistant" && item.toolSteps?.some(step => /^math\./.test(step.tool))); return { sessionId: current.activeSessionId, messageId: message.id, text: message.content, status: message.status, toolSteps: message.toolSteps }; });
      } else {
        await createProject(); await upload();
        check("attachment-upload", { names: await page.evaluate(() => [...missumApp.getState().attachments, ...missumApp.getState().documents].map(item => item.fileName || item.name)) });
        await page.locator("#inspector-toggle").click(); await picture("outputs-with-upload");
        await page.getByRole("button", { name: "Alle Quellen anzeigen", exact: true }).click(); await picture("sources-tab");
        assert.match(await page.locator("#browser-view-panel").textContent(), /testwerte\.txt/);
        await page.locator("#session-tabs .session-view-tab").first().click();
        general = await send("Nutze das Rechenwerkzeug für die Zahlen im Anhang testwerte.txt: Berechne Summe und Mittelwert. Antworte mit genau zwei kurzen Sätzen auf Deutsch.", "chatgpt", true);
      }
      assert.match(general.text, /60/); assert.match(general.text, /20/);
      assert.equal(general.status, "completed"); assert.ok(general.toolSteps.some(step => /^math\./.test(step.tool)), "General prompt must execute a real calculation tool.");
      const peer = await context.newPage(); await peer.addInitScript(() => { window.__speechPackets = []; addEventListener("missum:host-message", event => { if (event.detail.type === "speech.audio") window.__speechPackets.push(event.detail.payload); }); });
      await peer.goto(target); await peer.waitForFunction(() => missumApp?.getState()?.activeSessionId);
      assert.notEqual(await peer.evaluate(() => missumBridge.clientId), await page.evaluate(() => missumBridge.clientId));
      before = await eventCount();
      await page.locator(`article[data-message-id="${general.messageId}"]`).getByRole("button", { name: "Nachricht vorlesen", exact: true }).click();
      const audio = (await waitEvent("speech.audio", before, 120000)).payload;
      assert.match(audio.url, /gateway-artifacts\//);
      await page.waitForFunction(() => document.getElementById("composer-speech-pause").disabled === false);
      await page.waitForFunction(() => !document.getElementById("browser-audio-resume").hidden || document.querySelector("[data-speech-source-active]") || !missumApp.getState().speechStatus.active, null, { timeout: 20000 });
      if (await page.locator("#browser-audio-resume").isVisible()) await page.locator("#browser-audio-resume").click();
      await page.waitForSelector("[data-speech-source-active]", { timeout: 15000 });
      await picture("f5-reading-highlight");
      await page.locator("#composer-speech-pause").click();
      await page.waitForFunction(() => missumApp.getState().microphone?.isSpeechPaused || document.getElementById("composer-speech-pause").getAttribute("aria-label") === "Fortsetzen");
      await page.locator("#composer-speech-pause").click();
      if (await page.locator("#browser-audio-resume").isVisible()) await page.locator("#browser-audio-resume").click();
      await page.waitForFunction(since => window.__missumQaEvents.slice(since).some(event => event.type === "speech.status" && event.payload?.active === false), before, { timeout: 120000 });
      const feedback = commands.filter(item => item.type === "speech.playbackProgress" && item.payload.playbackId === audio.playbackId);
      assert.ok(feedback.some(item => item.payload.state === "ended"), "Real HTMLAudio must finish the WAV.");
      assert.ok(feedback.some(item => item.payload.positionSeconds > 0), "HTMLAudio must report real media progress.");
      assert.ok(feedback.some(item => item.payload.state === "paused"), "Pause uses real media feedback.");
      assert.equal(await peer.evaluate(() => window.__speechPackets.length), 0, "The non-originating client never receives this playback audio."); await peer.close();
      check("f5-real-htmlaudio", { playbackId: audio.playbackId, states: [...new Set(feedback.map(item => item.payload.state))], maxPosition: Math.max(...feedback.map(item => item.payload.positionSeconds)) });
      await page.locator(`article[data-message-id="${general.messageId}"] .message-content p`).last().click({ button: "right" });
      before = await eventCount(); await page.getByRole("menuitem", { name: "Ab hier vorlesen", exact: true }).click();
      const readFromAudio = (await waitEvent("speech.audio", before, 120000)).payload;
      assert.ok(commands.some(command => command.type === "microphone.speak" && command.payload.messageId === general.messageId && command.payload.startAnchor), "Read from here must send the genuine current message anchor.");
      await page.waitForFunction(() => !document.getElementById("composer-speech-stop").disabled);
      before = await eventCount(); await page.locator("#composer-speech-stop").click(); await waitEvent("speech.reset", before);
      await page.waitForFunction(() => !missumApp.getState().speechStatus.active && !missumApp.getState().microphone.isSpeaking);
      check("f5-read-from-stop", { playbackId: readFromAudio.playbackId, highlightsAfterStop: await page.locator("[data-speech-source-active]").count() });
      const pdfDownload = page.waitForEvent("download", { timeout: 60000 });
      await page.locator(`article[data-message-id="${general.messageId}"] .message-content`).click({ button: "right" });
      await page.getByRole("menuitem", { name: "Nachricht als PDF exportieren", exact: true }).click();
      const pdf = await pdfDownload; const pdfPath = path.join(output, pdf.suggestedFilename()); await pdf.saveAs(pdfPath);
      assert.equal((await fs.readFile(pdfPath)).subarray(0, 4).toString(), "%PDF");
      check("message-pdf-download", { file: pdfPath, bytes: (await fs.stat(pdfPath)).size });
      async function mode(value) {
        await page.locator("#mode-switcher-button").click();
        await page.locator(`#mode-switcher-menu [data-chat-mode="${value}"]`).click();
        await page.waitForFunction(value => missumApp.getState().chatMode === value, value);
        await chooseModel();
        await noReasoning();
      }
      async function automaticSpeech(value) {
        const since = await eventCount(); await page.locator("#browser-menu-0").click(); await page.getByRole("menuitem", { name: "Einstellungen", exact: true }).click();
        await waitEvent("settings.snapshot", since); await page.getByLabel("Antworten automatisch vorlesen", { exact: true }).setChecked(value);
        const savedSince = await eventCount(); await page.locator("#settings-save").click(); const saved = await waitEvent("settings.changed", savedSince);
        assert.equal(saved.payload.values.isAutomaticSpeechEnabled, value); await page.getByRole("button", { name: "Zum Assistenten", exact: true }).click();
        check("automatic-speech-setting", { value, revision: saved.payload.revision });
      }
      await mode("coding"); await createProject();
      await automaticSpeech(true);
      const autoPeer = await context.newPage(); await autoPeer.addInitScript(() => { window.__speechPackets = []; addEventListener("missum:host-message", event => { if (event.detail.type === "speech.audio") window.__speechPackets.push(event.detail.payload); }); });
      await autoPeer.goto(target); await autoPeer.waitForFunction(() => missumApp?.getState()?.activeSessionId);
      const autoSince = await eventCount(), commandSince = commands.length;
      try {
        const coding = await send("Lies README.md im Projektordner mit dem Lesewerkzeug. Antworte auf Deutsch in genau einer Zeile, wofür das Projekt gedacht ist. Ändere keine Dateien.", "codex");
        assert.equal(coding.status, "completed"); assert.ok(coding.toolSteps.some(step => /^coding\.(read|list)/.test(step.tool)), "Codex must read the genuine workspace.");
        const automatic = (await waitEvent("speech.audio", autoSince, 120000)).payload;
        if (await page.locator("#browser-audio-resume").isVisible()) await page.locator("#browser-audio-resume").click();
        await page.waitForFunction(since => window.__missumQaEvents.slice(since).some(event => event.type === "speech.status" && event.payload?.active === false), autoSince, { timeout: 120000 });
        const autoFeedback = commands.slice(commandSince).filter(command => command.type === "speech.playbackProgress" && command.payload.playbackId === automatic.playbackId);
        assert.ok(autoFeedback.some(command => command.payload.state === "ended" && command.payload.positionSeconds > 0));
        assert.equal(commands.slice(commandSince).filter(command => command.type === "microphone.speak").length, 0, "Automatic reading has no manual speech command.");
        assert.equal(await autoPeer.evaluate(() => window.__speechPackets.length), 0);
        check("f5-automatic-origin-client", { playbackId: automatic.playbackId, messageId: coding.messageId, otherClientAudioPackets: 0, endedFrames: autoFeedback.filter(command => command.payload.state === "ended").length });
      } finally {
        if (!autoPeer.isClosed()) await autoPeer.close();
        if (!page.isClosed()) { if (await page.locator("#composer-speech-stop").isVisible() && await page.locator("#composer-speech-stop").isEnabled()) await page.locator("#composer-speech-stop").click(); await automaticSpeech(Boolean(settings.values.isAutomaticSpeechEnabled)); }
      }
      await mode("claudescience"); await createProject(); await upload();
      const scienceRun = await send("Analysiere nur die drei Zahlen aus testwerte.txt lokal mit Python: Summe, Mittelwert und ein kleines Balkendiagramm. Keine Literatur oder Websuche. Beende nach dieser kurzen Datenanalyse mit einer knappen deutschen Antwort.", "claude-science");
      assert.equal(scienceRun.status, "completed"); assert.match(scienceRun.text, /60/); assert.match(scienceRun.text, /20/);
      const science = await page.evaluate(() => ({ state: missumApp.getState().scientificResearch, tabs: [...document.querySelectorAll(".session-view-tab")].map(item => item.textContent) }));
      check("science-views", science);
      await page.getByRole("tab", { name: "Publikation", exact: true }).click(); await picture("science-publication");
      await page.getByRole("tab", { name: "Simulation", exact: true }).click(); await picture("science-simulation");
    }
    result.commands = commands; result.errors = errors; result.networkFailures = networkFailures;
    result.hostErrors = (await page.evaluate(() => window.__missumQaEvents.filter(event => ["host.error", "settings.error"].includes(event.type))))
      .map(event => ({ ...event, command: commands.find(command => command.requestId === event.requestId) }));
    await fs.writeFile(path.join(output, "inspection.json"), JSON.stringify(result, null, 2));
    console.log(JSON.stringify(result, null, 2));
    assert.equal(result.hostErrors.filter(event => !(event.payload?.type === "workspace.browse" && event.command?.payload?.path?.endsWith("missing-folder-for-qa"))).length, 0, "The real UI must not receive unexplained host errors.");
    if (errors.length || response.status() !== 200) process.exitCode = 1;
    else for (const file of ["failure.json", "failure.png"]) await fs.rm(path.join(output, file), { force: true });
  } catch (error) {
    if (activePage && !activePage.isClosed()) {
      await activePage.screenshot({ path: path.join(output, "failure.png"), fullPage: true });
      await fs.writeFile(path.join(output, "failure.json"), JSON.stringify({ error: error.message, commands: collectedCommands, failedResponses, runtime: await activePage.evaluate(() => ({ events: window.__missumQaEvents, speechProgress: missumApp.getState().speechProgress, speechStatus: missumApp.getState().speechStatus, microphone: missumApp.getState().microphone, content: document.body.innerText })) }, null, 2));
    }
    throw error;
  } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
