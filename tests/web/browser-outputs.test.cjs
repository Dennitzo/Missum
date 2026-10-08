const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { TestNode } = require("./test-dom.cjs");
const { modelCatalog } = require("../../src/Missum.App/Assets/Web/reasoning-menu.js");

const webRoot = path.resolve(__dirname, "../../src/Missum.App/Assets/Web");
const source = name => fs.readFileSync(path.join(webRoot, name), "utf8");
const plain = value => JSON.parse(JSON.stringify(value));

function harness(initial = {}, saved = new Map(), preferences = {}) {
  const ids = new Map(), events = new Map(), documentEvents = new Map(), posts = [], timers = [];
  const body = new TestNode("body");
  const document = { body, activeElement: null, createElement(tag) {
    const element = new TestNode(tag); element.focus = () => { document.activeElement = element; };
    return element;
  }, getElementById: id => ids.get(id), querySelector: selector => body.querySelector(selector),
    querySelectorAll: selector => body.querySelectorAll(selector), addEventListener(type, callback) {
      if (!documentEvents.has(type)) documentEvents.set(type, []); documentEvents.get(type).push(callback);
    } };
  for (const id of ["browser-view-panel", "session-tabs", "output-inspector", "settings-page", "conversation-pane", "science-workbench", "model-picker", "local-model-overlay", "local-model-effort", "prompt-navigation", "tabbar-sidebar-toggle", "inspector-toggle", "browser-host-label"]) {
    const element = document.createElement(["model-picker", "local-model-effort"].includes(id) ? "select" : "div");
    ids.set(id, element); body.append(element);
    if (id === "model-picker") Object.defineProperty(element, "options", { get: () => element.children });
  }
  for (const id of ["output-inspector", "settings-page", "local-model-overlay"]) ids.get(id).hidden = true;
  const pane = document.createElement("div"); pane.className = "chat-pane"; body.append(pane);
  const current = { activeSessionId: "chat-a", chatMode: "coding", selectedModelId: "A", model: "A",
    workspacePath: "C:\\Projects\\Rechner", sessions: [{ id: "chat-a", title: "Rechner" }, { id: "chat-b", title: "Andere Sitzung" }],
    messages: [], documents: [], attachments: [], artifactPreviewUrls: new Map(),
    scientificResearch: { projects: [], detail: { works: [] } }, ...initial };
  const context = vm.createContext({ URL, document, window: { innerWidth: 1200, innerHeight: 800 }, location: {
    href: "http://192.168.1.2:8080/assistant/", origin: "http://192.168.1.2:8080", hostname: "192.168.1.2", hash: ""
  }, sessionStorage: { getItem: key => saved.get(key) || null, setItem: (key, value) => saved.set(key, value) },
    localStorage: preferences.storage || { getItem: key => saved.get(key) || null, setItem: (key, value) => saved.set(key, value) },
    setTimeout: callback => { timers.push(callback); return timers.length; },
    addEventListener: (type, callback) => events.set(type, callback),
    dispatchEvent: event => events.get(event.type)?.(event),
    CustomEvent: class { constructor(type, options) { this.type = type; this.detail = options?.detail; } },
    missumModelCatalog: modelCatalog, missumApp: { getState: () => current, toggleSidebar() {} },
    missumBridge: { isLanBrowser: true, clientId: preferences.clientId || "mac.tab", deviceId: preferences.deviceId, resourceUrl: value => value,
      post(type, payload) { posts.push({ type, payload }); return `request-${posts.length}`; } } });
  vm.runInContext(source("browser-panels.js"), context, { filename: "browser-panels.js" });
  const flush = () => { while (timers.length) timers.shift()(); };
  const emit = (type, payload = {}) => { events.get("missum:host-message")({ detail: { type, payload } }); flush(); };
  const click = async element => { assert.ok(element, "the requested control exists"); await element.dispatch("click"); flush(); };
  const buttons = (root, label) => root.querySelectorAll("button").filter(element => element.textContent === label);
  return { context, current, ids, posts, pane, emit, click, buttons, documentEvents, saved, flush };
}

const receipt = (id, tool, input, output, extra = {}) => ({ id, tool,
  inputJson: JSON.stringify(input || {}), outputJson: JSON.stringify(output || {}), ...extra });
const message = (id, extra = {}) => ({ id, role: "assistant", status: "completed", content: "Antwort", createdAt: "2026-10-05T10:00:00Z", ...extra });

const agent = (index, extra = {}) => ({ agentId: `agent-${index}`, runId: `run-${index}`, sessionId: "chat-a", title: `Aufgabe ${index}`, status: index % 2 ? "completed" : "cancelled", planetIndex: index,
  createdAt: `2026-10-06T${String(index).padStart(2,"0")}:00:00Z`, messages: [message(`child-${index}`)], ...extra });

test("explicit outputs preference survives both on and off reloads without changing native server settings", async () => {
  const saved = new Map(), first = harness({}, saved);
  assert.equal(first.ids.get("output-inspector").hidden, true);
  assert.equal(saved.has("assistant.outputs-visible:v1:mac"), false, "initial read must not invent a stored preference");
  await first.click(first.ids.get("inspector-toggle"));
  assert.equal(saved.get("assistant.outputs-visible:v1:mac"), "1");
  const reopened = harness({}, saved);
  assert.equal(reopened.ids.get("output-inspector").hidden, false);
  assert.equal(reopened.ids.get("inspector-toggle").getAttribute("aria-expanded"), "true");
  await reopened.click(reopened.ids.get("inspector-toggle"));
  assert.equal(saved.get("assistant.outputs-visible:v1:mac"), "0");
  const closed = harness({}, saved); assert.equal(closed.ids.get("output-inspector").hidden, true);
  assert.equal([...first.posts, ...reopened.posts, ...closed.posts].some(item => item.type === "settings.update" || item.type === "ui.outputs"), false);
});

test("new browser tabs share device outputs preference while another browser device has its own default", async () => {
  const saved = new Map(), first = harness({}, saved, { clientId: "device.tab-a", deviceId: "device" });
  await first.click(first.ids.get("inspector-toggle"));
  const second = harness({}, saved, { clientId: "device.tab-b", deviceId: "device" });
  assert.equal(second.ids.get("output-inspector").hidden, false);
  const other = harness({}, saved, { clientId: "other.tab-c", deviceId: "other" });
  assert.equal(other.ids.get("output-inspector").hidden, true);
  const fallback = harness({}, saved, { clientId: "device.tab-new" });
  assert.equal(fallback.ids.get("output-inspector").hidden, false, "legacy bridge fallback removes the tab segment");
});

test("automatic Sources hide keeps the persistent open intent across reload and return to chat", async () => {
  const saved = new Map(), first = harness({}, saved);
  await first.click(first.ids.get("inspector-toggle"));
  first.context.missumPanels.setView("sources");
  assert.equal(first.ids.get("output-inspector").hidden, true);
  assert.equal(saved.get("assistant.outputs-visible:v1:mac"), "1");
  const reopened = harness({ activeSessionId: null }, saved);
  reopened.current.activeSessionId = "chat-a";
  reopened.emit("state.snapshot", {});
  assert.equal(reopened.pane.dataset.view, "sources");
  assert.equal(reopened.ids.get("output-inspector").hidden, true);
  assert.equal(reopened.ids.get("inspector-toggle").getAttribute("aria-expanded"), "false");
  reopened.context.missumPanels.setView("chat");
  assert.equal(reopened.ids.get("output-inspector").hidden, false);
  assert.equal(saved.get("assistant.outputs-visible:v1:mac"), "1");
});

test("unavailable or damaged browser storage keeps outputs functional without writing server preferences", async () => {
  const blocked = { getItem() { throw new Error("storage blocked"); }, setItem() { throw new Error("storage blocked"); } };
  const h = harness({}, new Map(), { storage: blocked });
  assert.equal(h.ids.get("output-inspector").hidden, true);
  await h.click(h.ids.get("inspector-toggle")); assert.equal(h.ids.get("output-inspector").hidden, false);
  h.context.missumPanels.setView("sources"); h.context.missumPanels.setView("chat");
  assert.equal(h.ids.get("output-inspector").hidden, false);
  const bad = harness({}, new Map([["assistant.outputs-visible:v1:mac", "bad-value"]]));
  assert.equal(bad.ids.get("output-inspector").hidden, true);
  assert.equal(h.posts.some(item => item.type === "settings.update"), false);
});

test("dedicated view tabs automatically hide outputs and their toggle while restoring the prior chat choice", async () => {
  for (const view of ["sources", "review", "publication", "simulation", "subagents", "settings"]) {
    const h = harness({ chatMode: "claudescience" });
    const inspector = h.ids.get("output-inspector"), toggle = h.ids.get("inspector-toggle");
    await h.click(toggle); assert.equal(inspector.hidden, false);
    h.context.missumPanels.setView(view);
    assert.equal(inspector.hidden, true, `${view} hides the overlay`);
    assert.equal(toggle.hidden, true, `${view} hides the unavailable toggle`);
    assert.equal(toggle.getAttribute("aria-expanded"), "false");
    h.context.missumPanels.render();
    assert.equal(inspector.hidden, true, "background updates cannot reopen the overlay in a dedicated view");
    await h.click(toggle); // A hidden menu/programmatic route must not alter the chat preference.
    h.context.missumPanels.setView("chat");
    assert.equal(inspector.hidden, false, `${view} keeps the previous open choice`);
    assert.equal(toggle.hidden, false); assert.equal(toggle.getAttribute("aria-expanded"), "true");
    assert.equal(toggle.getAttribute("aria-label"), "Ausgaben schließen");
  }
});

test("All sources hides the current overlay without permanently switching off its remembered preference", async () => {
  const h = harness({ messages: [message("source", { sources: [{ url: "https://one.example/source", title: "Ergebnis" }] })] });
  const inspector = h.ids.get("output-inspector"), toggle = h.ids.get("inspector-toggle");
  await h.click(toggle); await h.click(inspector.querySelector(".inspector-all-sources"));
  assert.equal(h.pane.dataset.view, "sources"); assert.equal(inspector.hidden, true);
  h.context.missumPanels.setView("chat"); assert.equal(inspector.hidden, false);
  await h.click(toggle); assert.equal(inspector.hidden, true);
  h.context.missumPanels.setView("sources"); h.context.missumPanels.setView("chat");
  assert.equal(inspector.hidden, true, "an explicitly closed overlay also stays closed on return");
  assert.equal(toggle.hidden, false); assert.equal(toggle.getAttribute("aria-expanded"), "false");
});

test("subagent conversations support outputs while their overview suppresses it without changing preference", async () => {
  const h = harness(); h.emit("subagent.snapshot", agent(0));
  const inspector = h.ids.get("output-inspector"), toggle = h.ids.get("inspector-toggle");
  await h.click(toggle); h.context.missumPanels.setView("subagent", "agent-0");
  assert.equal(inspector.hidden, false); assert.equal(toggle.hidden, false);
  h.context.missumPanels.setView("subagents");
  assert.equal(inspector.hidden, true); assert.equal(toggle.getAttribute("aria-expanded"), "false");
  h.context.missumPanels.setView("subagent", "agent-0");
  assert.equal(inspector.hidden, false); assert.equal(toggle.getAttribute("aria-expanded"), "true");
});

test("seven terminal children stay behind a compact planet summary and open the overview and child tabs only on demand", async () => {
  const h=harness({chatMode:"claudescience"});
  h.emit("subagents.snapshot",{subagents:Array.from({length:7},(_,index)=>agent(index))});
  const tabs=h.ids.get("session-tabs");
  assert.deepEqual(tabs.children.map(tab=>tab.textContent),["Rechner","Publikation","Simulation"]);
  await h.click(h.ids.get("inspector-toggle"));
  const inspector=h.ids.get("output-inspector"), summary=inspector.querySelector(".inspector-subagent-summary");
  assert.equal(summary.getAttribute("aria-label"),"7 fertig");
  assert.equal(summary.querySelectorAll(".subagent-planet").length,4);
  assert.equal(inspector.querySelectorAll(".subagent-overview-row").length,0);
  assert.equal(inspector.textContent.includes("Aufgabe 0"),false);
  await h.click(summary);
  assert.equal(h.pane.dataset.view,"subagents");
  const entries=h.ids.get("browser-view-panel").querySelectorAll(".subagent-overview-entry");
  assert.equal(entries.length,7); assert.equal(entries[0].getAttribute("aria-label"),"Aufgabe 6 · Abgebrochen");
  assert.deepEqual(tabs.children.map(tab=>tab.textContent),["Rechner","Publikation","Simulation","Subagenten"]);
  await h.click(entries[0]);
  assert.equal(h.pane.dataset.view,"subagent");
  assert.deepEqual(tabs.children.map(tab=>tab.textContent),["Rechner","Publikation","Simulation","Subagent · Aufgabe 6","Subagenten"]);
  assert.equal(h.ids.get("browser-view-panel").textContent.includes("Antwort"),true);
  await h.click(tabs.children[3].querySelector(".session-view-tab__close"));
  assert.equal(h.pane.dataset.view,"chat");
  assert.equal(tabs.textContent.includes("Subagent · Aufgabe 6"),false);
  h.emit("subagent.snapshot",agent(6,{projectionRevision:3}));
  assert.equal(tabs.textContent.includes("Subagent · Aufgabe 6"),false,"a completion snapshot never reopens a manually closed tab");
  await h.click(tabs.children[3].querySelector(".session-view-tab__close"));
  assert.equal(tabs.textContent.includes("Subagenten"),false);
});

test("mixed active and terminal children show working planets and a separate finished count", async () => {
  const h=harness();
  h.emit("subagents.snapshot",{subagents:[agent(0,{status:"running",isRunning:true}),agent(1,{status:"queued"}),agent(2),agent(3,{status:"completed",isRunning:true}),agent(4,{sessionId:"chat-b",status:"running"})]});
  await h.click(h.ids.get("inspector-toggle"));
  const summary=h.ids.get("output-inspector").querySelector(".inspector-subagent-summary");
  assert.equal(summary.getAttribute("aria-label"),"2 arbeiten · 2 fertig");
  assert.equal(summary.querySelectorAll(".subagent-planet").length,2);
  assert.equal(summary.querySelector(".inspector-subagent-finished").textContent,"2 fertig");
  assert.deepEqual(h.ids.get("session-tabs").children.map(tab=>tab.textContent),["Rechner","Subagent · Aufgabe 0","Subagent · Aufgabe 1"]);
});

test("child tabs preserve close choices across reload and resume and avoid focus and scroll resets on token snapshots", async () => {
  const h=harness(), running=agent(0,{status:"running",isRunning:true,projectionRevision:1});
  h.emit("subagent.snapshot",running);
  const tabs=h.ids.get("session-tabs"), original=tabs.children[1]; tabs.scrollLeft=77;
  h.emit("subagent.snapshot",{...running,projectionRevision:2,messages:[message("child-new",{content:"Weitere Tokens"})]});
  assert.equal(tabs.children[1],original,"token updates preserve the tab controls and keyboard focus");
  assert.equal(tabs.scrollLeft,77);
  await h.click(original.querySelector(".session-view-tab__close"));
  h.emit("subagent.snapshot",{...running,runId:"attempt-2",createdAt:"2026-10-07T01:00:00Z",projectionRevision:1});
  assert.equal(tabs.children.length,1,"resuming the same agent respects explicit closure");
  const reloaded=harness({},h.saved);
  reloaded.emit("subagent.snapshot",{...running,runId:"attempt-2",createdAt:"2026-10-07T01:00:00Z"});
  assert.equal(reloaded.ids.get("session-tabs").children.length,1);
  reloaded.context.missumPanels.setView("subagents");
  await reloaded.click(reloaded.ids.get("browser-view-panel").querySelector(".subagent-overview-entry"));
  assert.equal(reloaded.pane.dataset.view,"subagent");
  const third=harness({},reloaded.saved);
  third.emit("state.snapshot",{subagents:[{...running,runId:"attempt-2",createdAt:"2026-10-07T01:00:00Z"}]});
  assert.equal(third.ids.get("session-tabs").textContent.includes("Subagent · Aufgabe 0"),true,"an explicitly opened view remains available on reconnect");
});

test("review and sources tabs are lazy, closeable, and use independent native SVG icons", async () => {
  const h=harness({chatMode:"claudescience",changesSummary:{files:[{path:"code.py",addedLines:1,removedLines:0}]},messages:[message("source",{sources:[{url:"https://one.example/source",title:"Ergebnis"}]})]});
  const tabs=h.ids.get("session-tabs");
  assert.equal(tabs.children.length,3,"receipts alone do not open review tabs");
  assert.equal(tabs.children[0].querySelector("svg").dataset.mode,"claudescience");
  assert.ok(tabs.children[1].querySelector(".session-tab-icon--publication"));
  assert.ok(tabs.children[2].querySelector(".session-tab-icon--simulation"));
  await h.click(h.ids.get("inspector-toggle"));
  await h.click(h.ids.get("output-inspector").querySelector(".inspector-changes"));
  assert.equal(tabs.children.at(-1).textContent,"Änderungen · 1 Datei");
  await h.click(tabs.children.at(-1).querySelector(".session-view-tab__close"));
  assert.equal(h.pane.dataset.view,"chat"); assert.equal(tabs.children.length,3);
  await h.click(h.ids.get("output-inspector").querySelector(".inspector-all-sources"));
  assert.equal(tabs.children.at(-1).textContent,"Quellen");
  await h.click(tabs.children.at(-1).querySelector(".session-view-tab__close"));
  assert.equal(tabs.children.length,3); assert.equal(h.pane.dataset.view,"chat");
});

test("planet SVGs match native persisted index signatures, including identities beyond the 4096 palette", async () => {
  const h=harness();
  h.emit("subagents.snapshot",{subagents:[agent(0,{planetIndex:0}),agent(1,{planetIndex:1}),agent(2,{planetIndex:2}),agent(3,{planetIndex:4096})]});
  h.context.missumPanels.setView("subagents");
  const planets=h.ids.get("browser-view-panel").querySelectorAll(".subagent-planet");
  assert.deepEqual(planets.map(planet=>planet.getAttribute("data-planet-signature")),["801EEF:1:4:2","6F84EB:3:6:3","54BDDA:2:1:1","ED7E98:1:4:2"]);
  assert.equal(new Set(planets.map(planet=>planet.querySelector("linearGradient").getAttribute("id"))).size,4);
  assert.ok(planets.every(planet=>planet.getAttribute("aria-hidden")==="true" && planet.querySelector("clipPath")));
  const old=planets.at(-1).getAttribute("data-planet-signature");
  h.emit("subagent.snapshot",agent(0,{planetIndex:0,title:"Umbenannt",projectionRevision:5}));
  assert.equal(h.ids.get("browser-view-panel").querySelectorAll(".subagent-planet").at(-1).getAttribute("data-planet-signature"),old);
});

test("the workspace copy action is an accessible borderless glyph and retains the exact PC path", async () => {
  const h=harness(); await h.click(h.ids.get("inspector-toggle"));
  const copy=h.ids.get("output-inspector").querySelector(".inspector-copy-path");
  assert.equal(copy.getAttribute("aria-label"),"Projektpfad kopieren"); assert.ok(copy.querySelector("svg"));
  await h.click(copy); assert.deepEqual(plain(h.posts.at(-1)),{type:"message.copy",payload:{text:h.current.workspacePath}});
  const css=source("browser-panels.css");
  assert.match(css,/\.inspector-copy-path\s*\{[^}]*border:\s*0/);
  assert.match(css,/\.session-view-tab\[aria-selected=true\]\s*\{[^}]*var\(--accent-subtle/);
  assert.match(css,/\.session-tab-icon--sources\s*\{[^}]*var\(--icon-web/);
});

function renderLifecycle(h,steps,options={}) {
  vm.runInContext(source("coding-timeline.js"),h.context);
  return h.context.missumCodingTimeline.render({id:"parent-answer",role:"assistant",status:"completed",content:""},steps,{
    renderMarkdown:text=>{const paragraph=h.context.document.createElement("p");paragraph.textContent=text;return paragraph;},
    createSubagentReceipt:step=>h.context.missumPanels.createSubagentReceipt(step),...options
  });
}
const delegation = (id,tool,agentId,runId,extra={}) => ({id,tool,status:"completed",inputJson:JSON.stringify({agentId,runId,task:"Lokale Analyse mit Python"}),outputJson:JSON.stringify({agentId,runId,title:"Lokale Analyse",status:"completed",resultDelivered:true}),...extra});

test("native child lifecycle receipts use exact start and completion captions and open only the matching child", async () => {
  const h=harness();h.emit("subagent.snapshot",agent(0,{resultDelivered:true}));
  const start=delegation("start","subagent","agent-0","run-0"),end=delegation("end","subagent.completed","agent-0","run-0");
  const timeline=renderLifecycle(h,[start,end]);
  const rows=timeline.querySelectorAll(".subagent-lifecycle");assert.equal(rows.length,2);
  assert.equal(rows[0].querySelector(".subagent-lifecycle__caption").textContent,"hat die Arbeit begonnen");
  assert.equal(rows[1].querySelector(".subagent-lifecycle__caption").textContent,"hat die Arbeit beendet");
  assert.equal(rows[0].querySelector(".subagent-planet").getAttribute("data-planet-index"),"0");
  assert.ok(rows.every(row=>row.dataset.speechExclude==="true"));assert.equal(timeline.querySelector("details"),null);
  await h.click(rows[0]);assert.equal(h.pane.dataset.view,"subagent");
  assert.equal(h.ids.get("session-tabs").textContent.includes("Subagent · Aufgabe 0"),true);
  assert.equal(h.posts.some(post=>post.type.startsWith("chat.")),false);
});

test("legacy completed receipts stay completed while mapped modern starts keep their original lifecycle identity", () => {
  const h=harness();h.emit("subagent.snapshot",agent(0,{status:"completed"}));
  const legacy=delegation("old","subagent","agent-0","run-0",{outputJson:JSON.stringify({agentId:"agent-0",runId:"run-0",status:"completed",isRunning:false})});
  const row=renderLifecycle(h,[legacy]).querySelector(".subagent-lifecycle");
  assert.equal(row.querySelector(".subagent-lifecycle__caption").textContent,"hat die Arbeit beendet");
  h.emit("subagent.snapshot",agent(0,{runId:"resumed",status:"running",isRunning:true,resultDelivered:false,createdAt:"2026-10-07T01:00:00Z"}));
  row._refreshSubagentReceipt();assert.equal(row.querySelector(".subagent-lifecycle__caption").textContent,"hat die Arbeit beendet");
});

test("initial missing child projections activate existing lifecycle rows with the native index when the snapshot arrives", async () => {
  const h=harness(),receipt=delegation("deferred","subagent","agent-0","run-0",{outputJson:JSON.stringify({agentId:"agent-0",runId:"run-0",title:"Aufgabe 0"})});
  const timeline=renderLifecycle(h,[receipt]),row=timeline.querySelector(".subagent-lifecycle");h.context.document.body.append(timeline);
  assert.equal(row.disabled,true);
  h.emit("subagent.snapshot",agent(0,{status:"running",isRunning:true,resultDelivered:false,planetIndex:12}));
  assert.equal(row.disabled,false);assert.equal(row.querySelector(".subagent-planet").getAttribute("data-planet-index"),"12");
  await h.click(row);assert.equal(h.pane.dataset.view,"subagent");
  h.current.activeSessionId="chat-b";h.emit("state.snapshot",{subagents:[]});assert.equal(row.disabled,true,"an old-session row never navigates a different parent's child");
});

test("only accepted manager calls with persisted child receipts are coalesced and failures remain generic disclosures", () => {
  const h=harness(),stored=delegation("stored","subagent","agent-0","run-0");
  const manager=(id,tool,extra={})=>delegation(id,tool,"agent-0","run-0",extra);
  const timeline=renderLifecycle(h,[stored,manager("spawn","subagent.spawn"),manager("wait","subagent.wait"),manager("resume","subagent.resume"),
    manager("denied","subagent.spawn",{status:"denied"}),manager("rejected","subagent.wait",{outputJson:'{"success":false,"status":"rejected","agentId":"agent-0","runId":"run-0"}'}),
    manager("unmatched","subagent.spawn",{inputJson:'{"agentId":"other","runId":"different"}',outputJson:'{"agentId":"other","runId":"different"}'})]);
  assert.equal(timeline.querySelectorAll(".subagent-lifecycle").length,1);
  assert.deepEqual(timeline.querySelectorAll(".coding-step").map(row=>row.dataset.stepId),["denied","rejected","unmatched"]);
  const noReceipt=renderLifecycle(h,[manager("unconfirmed","subagent.spawn")]);assert.equal(noReceipt.querySelectorAll(".coding-step").length,1);
  const fallback=renderLifecycle(h,[stored],{createSubagentReceipt:undefined});assert.equal(fallback.querySelectorAll(".coding-step").length,1,"generic callers keep all data without native projection support");
});

test("native research summary hooks refresh unchanged step headers and null retains generic metadata", () => {
  const h=harness(),step={id:"research",tool:"research.read",label:"Forschungsstand lesen",status:"completed",inputJson:'{"projectId":"project"}',outputJson:'{}'};
  const first=renderLifecycle(h,[step],{toolSummary:()=>"Hypothesen, offene Prüfungen und Publikationsstand"});
  assert.equal(first.querySelector(".coding-step__name").textContent,"Forschungsstand lesen · Hypothesen, offene Prüfungen und Publikationsstand");
  const updated=renderLifecycle(h,[step],{previousTimeline:first,toolSummary:()=>"Neue Forschungsdaten"});
  h.context.missumCodingTimeline.reconcile(first,updated);assert.equal(first.querySelector(".coding-step__name").textContent,"Forschungsstand lesen · Neue Forschungsdaten");
  const fallback=renderLifecycle(h,[{...step,tool:"coding.read",inputJson:'{"path":"README.md"}'}],{toolSummary:()=>null});
  assert.equal(fallback.querySelector(".coding-step__name").textContent.includes("README.md"),true);
});

test("native tool glyphs distinguish blue web globes, violet code braces and research magnifiers", () => {
  const h=harness();
  const steps=["web.search","web.fetch","research.code.write","research.code.execute","coding.read","coding.write","math.formalProof","research.read","research.update","math.evaluate"].map((tool,index)=>({id:`icon-${index}`,tool,status:"completed",inputJson:"{}",outputJson:"{}"}));
  const timeline=renderLifecycle(h,steps),icons=timeline.querySelectorAll(".coding-step__icon");
  assert.deepEqual(icons.map(icon=>icon.dataset.iconKey),["web","web","code","code","code","code","research","research","research","research"]);
  const globe=icons[0].querySelector("path").getAttribute("d"),braces=icons[2].querySelector("path").getAttribute("d"),magnifier=icons[7].querySelector("path").getAttribute("d");
  assert.notEqual(globe,braces);assert.notEqual(braces,magnifier);assert.ok(globe.includes("M1 8h14"));
  assert.ok(icons.every(icon=>icon.querySelector("svg").getAttribute("viewBox")==="0 0 16 16"));
  const css=source("browser-panels.css");assert.match(css,/\.coding-step__icon\s*\{[^}]*width:\s*14px;[^}]*height:\s*14px;[^}]*border:\s*0/);
  assert.match(css,/data-icon-key=web\]\s*\{[^}]*var\(--icon-web/);assert.match(css,/data-icon-key=code\]\s*\{[^}]*var\(--icon-code/);
});

test("native sidebar selection fills the project row and outputs use chain and open-window glyphs", async () => {
  const css=source("browser-panels.css"),html=source("index.html");
  assert.match(css,/\.session-group__body\s*\{\s*padding-left:\s*0/);
  assert.match(css,/\.session-item__open\s*\{[^}]*padding:\s*5px 8px 5px 36px/);
  const button=html.slice(html.indexOf('<button id="inspector-toggle"'),html.indexOf('</button>',html.indexOf('<button id="inspector-toggle"')));
  assert.match(button,/M14 3h7v7M21 3 10 14/);assert.doesNotMatch(button,/M15 4v16/);
  const h=harness({messages:[message("source",{sources:[{url:"https://native.example/source"}]})]});await h.click(h.ids.get("inspector-toggle"));
  const link=h.ids.get("output-inspector").querySelector(".inspector-all-sources path");assert.ok(link.getAttribute("d").includes("a3 3"));
  assert.match(css,/\.output-inspector > \.inspector-subagents\s*\{[^}]*gap:\s*2px;[^}]*margin:\s*10px 0 8px/);
  assert.match(css,/\.output-inspector > \.inspector-sources\s*\{[^}]*gap:\s*2px;[^}]*margin:\s*0/);
});

test("settings hides the outputs overlay and restores the unchanged assistant preference", async () => {
  const h = harness();
  await h.click(h.ids.get("inspector-toggle"));
  const inspector = h.ids.get("output-inspector"), toggle = h.ids.get("inspector-toggle");
  assert.equal(inspector.hidden, false);
  h.context.missumPanels.setView("settings");
  assert.equal(inspector.hidden, true, "the overlay cannot intercept Save in settings");
  assert.equal(toggle.getAttribute("aria-expanded"), "false", "ARIA describes current visibility while the open preference is retained separately");
  assert.equal(toggle.hidden, true);
  h.emit("settings.changed", { revision: 7 });
  assert.equal(inspector.hidden, true, "live settings events cannot uncover the overlay");
  h.context.missumPanels.setView("chat");
  assert.equal(inspector.hidden, false, "returning to the assistant restores the open overlay even with identical contents");
  await h.click(toggle);
  h.context.missumPanels.setView("settings");
  h.context.missumPanels.setView("chat");
  assert.equal(inspector.hidden, true, "an explicitly closed overlay remains closed");
});

test("the Deep Research profile shrinks inside its footer chip while the status action stays visible", () => {
  const css = source("styles.css");
  const profile = /\.active-tool-chip__profile\s*\{([^}]+)\}/.exec(css)?.[1] || "";
  const details = /\.active-tool-chip__details\s*\{([^}]+)\}/.exec(css)?.[1] || "";
  assert.match(profile, /flex:\s*1\s*;/);
  assert.match(profile, /min-width:\s*0\s*;/);
  assert.match(profile, /max-width:\s*100%\s*;/);
  assert.match(details, /flex:\s*none\s*;/);
});

test("Science has only the native chat, publication and simulation views and discards the retired research view on reload", () => {
  const h = harness({ chatMode: "claudescience" });
  assert.deepEqual(h.ids.get("session-tabs").children.map(tab => tab.textContent), ["Rechner", "Publikation", "Simulation"]);
  h.context.missumPanels.setView("research");
  assert.equal(h.pane.dataset.view, "chat");
  assert.equal(h.ids.get("conversation-pane").hidden, false);
  assert.equal(h.ids.get("science-workbench").hidden, true);
  h.saved.set("assistant.view:mac.tab:chat-b", JSON.stringify({ view: "research" }));
  h.current.activeSessionId = "chat-b"; h.emit("session.changed", {});
  assert.equal(h.pane.dataset.view, "chat", "a saved legacy view cannot resurrect the removed workbench");
});

test("native mode popup has three choices and the chat uses the full available width", () => {
  const html = source("index.html"), css = source("browser-panels.css");
  const menu = html.slice(html.indexOf('id="mode-switcher-menu"'), html.indexOf('id="open-settings"'));
  assert.equal((menu.match(/data-chat-mode=/g) || []).length, 3);
  assert.doesNotMatch(menu, /recent-session-list/);
  assert.match(menu, /Erstellen, lernen und erkunden/);
  assert.match(menu, /Deep Research, Analysen und Nachweise/);
  const listRule = /\.message-list\s*\{([^}]+)\}/.exec(css)?.[1] || "";
  assert.match(listRule, /width:\s*100%/); assert.match(listRule, /max-width:\s*none/);
  assert.match(css, /\.mode-switcher__menu\s*\{[^}]*width:\s*min\(240px,/);
});

test("Science renders real figure resources without filename hints or optional script paths", () => {
  const h = harness({ chatMode: "claudescience", scientificResearch: { projects: [], selectedProjectId: "project", detail: null } });
  h.emit("science.presentation", { projectId: "project", simulation: { artifacts: [
    { id: "figure", title: "Drei Werte", url: "http://192.168.1.2:8080/assistant/science-resources/figure" },
    { id: "interactive", title: "Interaktiv", url: "http://192.168.1.2:8080/assistant/science-resources/html", kind: "interactive", scriptUrl: "http://192.168.1.2:8080/assistant/science-resources/script" }
  ] } });
  h.context.missumPanels.setView("simulation");
  assert.equal(h.ids.get("browser-view-panel").querySelector("img").src, "http://192.168.1.2:8080/assistant/science-resources/figure");
  assert.equal(h.ids.get("browser-view-panel").querySelector("iframe").src, "http://192.168.1.2:8080/assistant/science-resources/html");
  assert.equal(h.ids.get("browser-view-panel").querySelector("iframe").getAttribute("sandbox"), "allow-scripts");
  assert.equal(h.ids.get("browser-view-panel").querySelector(".simulation-source summary").textContent, "HTML-/JavaScript-Code");
  assert.equal(h.ids.get("browser-view-panel").querySelectorAll("a").some(link => /\/(html|script)$/.test(link.href)), false,
    "interactive simulations and their source stay inside the Simulation tab");
});

test("the outputs overlay opens and closes through its accessible controls and keeps the native compact structure", async () => {
  const h = harness(), inspector = h.ids.get("output-inspector"), toggle = h.ids.get("inspector-toggle");
  assert.equal(inspector.hidden, true);
  await h.click(toggle);
  assert.equal(inspector.hidden, false);
  assert.equal(toggle.getAttribute("aria-expanded"), "true");
  assert.equal(toggle.getAttribute("aria-label"), "Ausgaben schließen");
  assert.equal(inspector.querySelector(".inspector-workspace-label").textContent, "Rechner");
  assert.equal(inspector.querySelector(".inspector-empty").textContent, "Noch keine Quellen");
  assert.equal(inspector.querySelector(".browser-panel-card"), null, "the inspector uses compact rows instead of activity cards");
  assert.equal(inspector.textContent.includes("Aktivität"), false);
  await h.click(inspector.querySelectorAll("button").find(button => button.getAttribute("aria-label") === "Ausgaben schließen"));
  assert.equal(inspector.hidden, true);
  assert.equal(toggle.getAttribute("aria-expanded"), "false");
  assert.equal(toggle.getAttribute("aria-label"), "Ausgaben einblenden");
  const rule = /\.output-inspector\s*\{([^}]+)\}/.exec(source("browser-panels.css"))?.[1] || "";
  assert.match(rule, /position:\s*absolute/);
  assert.match(rule, /width:\s*min\(304px,/);
  assert.match(rule, /border-radius:\s*24px/);
});

test("sources include web and research receipts, explicit sources, works and attachments without unrelated tool URLs", () => {
  const h = harness({ documents: [{ id: "doc", fileName: "Referenz.pdf", pageCount: 4 }],
    attachments: [{ id: "doc", fileName: "Referenz.pdf" }, { id: "attachment", fileName: "Messdaten.csv" }],
    scientificResearch: { projects: [], detail: { works: [{ url: "https://papers.example/study", title: "Studie" }] } } });
  const messages = [message("answer", { sources: [{ url: "https://explicit.example/source", title: "Explizite Quelle" }], toolSteps: [
    receipt("search", "web.search", { query: "Multiplikation" }, { results: [{ url: "https://search.example/page", title: "Suchtreffer" }] }),
    receipt("research", "research.collect", {}, { sources: [{ canonicalUrl: "https://research.example/work", title: "Forschungsquelle" }] }),
    receipt("coding", "coding.read", {}, { url: "https://coding.example/private-result" }),
    receipt("image", "web.search", {}, { thumbnailUrl: "https://images.example/thumb", imageUrl: "https://images.example/image", iconUrl: "https://images.example/icon" })
  ] })];
  const actions = h.context.missumPanels.sourceActions(h.current, messages);
  const urls = Array.from(actions).flatMap(action => Array.from(action.sources, source => source.href));
  assert.deepEqual(urls.sort(), ["https://explicit.example/source", "https://papers.example/study", "https://research.example/work", "https://search.example/page"].sort());
  assert.equal(actions.filter(action => action.id === "attachment:doc").length, 1);
  assert.equal(actions.find(action => action.id === "attachment:doc").document, true);
  assert.equal(actions.find(action => action.id === "attachment:attachment").document, false);
  assert.ok(actions.some(action => action.title === "Websuche: Multiplikation"));
});

test("source URL extraction accepts only absolute HTTP URLs while rejecting relative, local, executable and thumbnail metadata", async () => {
  const h = harness({ scientificResearch: { projects: [], detail: { works: [
    { url: "/science/local.json", title: "Lokales Ergebnis" }, { canonicalUrl: "https://valid.example/work", title: "Gültige Arbeit" }
  ] } } });
  const invalid = ["relative/page.html", "/results.json", "//external.example/page", "C:\\notes\\page.html", "file:///C:/notes/page.html", "javascript:alert(1)", "data:text/html,<script>alert(1)</script>"];
  const messages = [message("links", { sources: invalid.map(url => ({ url })), toolSteps: [receipt("web", "web.fetch",
    { url: "http://valid.example/original" }, { finalUrl: "https://valid.example/final", results: invalid.map(href => ({ href })), thumbnailUrl: "https://valid.example/thumb" })] })];
  const actions = h.context.missumPanels.sourceActions(h.current, messages);
  const urls = Array.from(actions).flatMap(action => Array.from(action.sources, source => source.href));
  assert.deepEqual(urls.sort(), ["http://valid.example/original", "https://valid.example/final", "https://valid.example/work"].sort());
  assert.equal(urls.some(url => url.startsWith("http://192.168.1.2")), false, "resource-relative paths never turn into Web findings");
  assert.ok(actions.every(action => action.attachment || action.sources.length > 0), "a source action whose URLs were all rejected must not create an empty inspector row");
  const invalidOnly = harness({ messages: [message("invalid", { sources: invalid.map(url => ({ url })) })] });
  await invalidOnly.click(invalidOnly.ids.get("inspector-toggle"));
  assert.equal(invalidOnly.ids.get("output-inspector").querySelector(".inspector-all-sources"), null);
  assert.equal(invalidOnly.ids.get("output-inspector").querySelector(".inspector-empty").textContent, "Noch keine Quellen");
});

test("replayed source receipts deduplicate by action and fetch redirect aliases reuse known research titles", () => {
  const finalUrl = "https://source.example/final", originalUrl = "https://source.example/original";
  const h = harness({ scientificResearch: { projects: [], detail: { works: [{ url: finalUrl, title: "Titel aus dem Forschungskatalog" }] } } });
  const old = receipt("fetch", "web.fetch", { url: originalUrl }, { finalUrl }, { updatedAt: "2026-10-05T09:00:00Z" });
  const latest = { ...old, updatedAt: "2026-10-05T10:00:00Z" };
  const actions = h.context.missumPanels.sourceActions(h.current, [message("old", { toolSteps: [old] }), message("latest", { toolSteps: [latest] })]);
  assert.equal(actions.length, 1, "replay and known work URL create neither duplicate actions nor duplicate research rows");
  assert.equal(actions[0].time, latest.updatedAt);
  assert.equal(actions[0].sources.length, 2);
  assert.deepEqual(Array.from(actions[0].sources, source => source.title), ["Titel aus dem Forschungskatalog", "Titel aus dem Forschungskatalog"]);
});

test("only child sessions belonging to the active chat contribute sources and artifacts, including after a delayed foreign snapshot", async () => {
  const h = harness();
  const child = (id, sessionId, domain) => ({ agentId: id, sessionId, name: id, messages: [message(id, { toolSteps: [
    receipt(`${id}-web`, "web.fetch", { url: `https://${domain}/source` }, {})
  ], artifacts: [{ id: `${id}-artifact`, fileName: `${id}.txt` }] })] });
  h.emit("subagents.snapshot", { subagents: [child("local", "chat-a", "local.example"), child("foreign", "chat-b", "foreign.example"), child("unowned", undefined, "unowned.example")] });
  await h.click(h.ids.get("inspector-toggle"));
  const inspector = h.ids.get("output-inspector");
  assert.ok(inspector.textContent.includes("https://local.example/source"));
  assert.ok(inspector.textContent.includes("local.txt"));
  assert.equal(inspector.textContent.includes("foreign.example"), false);
  assert.equal(inspector.textContent.includes("foreign.txt"), false);
  assert.equal(inspector.textContent.includes("unowned"), false);
  await h.click(inspector.querySelector(".inspector-all-sources"));
  assert.ok(h.ids.get("browser-view-panel").textContent.includes("https://local.example/source"));
  assert.equal(h.ids.get("browser-view-panel").textContent.includes("foreign.example"), false);
  h.current.activeSessionId = "chat-b";
  h.emit("state.snapshot", { subagents: [] });
  h.emit("subagent.snapshot", child("late-local", "chat-a", "late.example"));
  await h.click(h.ids.get("inspector-toggle"));
  assert.equal(inspector.textContent.includes("late.example"), false);
  assert.equal(inspector.textContent.includes("late-local.txt"), false);
});

test("all sources opens the complete sources tab and preserves attachment removal routing and inert source titles", async () => {
  const h = harness({ documents: [{ id: "document", fileName: "Anleitung.pdf", pageCount: 7, createdAt: "2026-10-05T11:00:00Z" }],
    attachments: [{ id: "attachment", fileName: "Daten.csv", createdAt: "2026-10-05T10:30:00Z" }],
    messages: [message("sources", { toolSteps: [receipt("search", "web.search", { query: "Test" }, { results: [
      { url: "https://one.example/page", title: "<script>never execute</script>" }, { url: "https://two.example/page", title: "Zweite Quelle" }
    ] })] })] });
  await h.click(h.ids.get("inspector-toggle"));
  const inspector = h.ids.get("output-inspector");
  assert.equal(inspector.querySelectorAll(".inspector-source-row").length, 1, "the compact overlay shows the newest source action only");
  await h.click(inspector.querySelector(".inspector-all-sources"));
  const panel = h.ids.get("browser-view-panel");
  assert.equal(panel.hidden, false);
  assert.equal(h.ids.get("conversation-pane").hidden, true);
  assert.equal(h.pane.dataset.view, "sources");
  assert.equal(h.ids.get("session-tabs").children.find(tab => tab.textContent === "Quellen").getAttribute("aria-selected"), "true");
  assert.equal(panel.querySelectorAll(".inspector-source-row").length, 4);
  assert.equal(panel.querySelector("script"), null);
  assert.ok(panel.textContent.includes("<script>never execute</script>"));
  const links = panel.querySelectorAll("a");
  assert.equal(links.length, 2);
  assert.ok(links.every(link => link.rel === "noopener noreferrer" && link.target === "_blank"));
  const removals = h.buttons(panel, "Anhang entfernen");
  await h.click(removals[0]); await h.click(removals[1]);
  assert.deepEqual(plain(h.posts.slice(-2)), [
    { type: "document.remove", payload: { documentId: "document" } }, { type: "attachment.remove", payload: { attachmentId: "attachment" } }
  ]);
  assert.equal(JSON.parse(h.saved.get("assistant.view:mac.tab:chat-a")).view, "sources");
});

test("line totals distinguish actual zero, missing values, binary files and partially captured changes", async () => {
  const h = harness(), counts = value => plain(h.context.missumPanels.changeCounts(value));
  assert.deepEqual(counts({ files: [{ addedLines: 0, removedLines: 0 }] }), { added: 0, removed: 0, partial: false });
  assert.deepEqual(counts({ files: [{ addedLines: 3, removedLines: 2 }, { isBinary: true, addedLines: null, removedLines: null }], isPartial: true }), { added: 3, removed: 2, partial: true });
  assert.deepEqual(counts({ files: [{ isBinary: true }] }), { added: 0, removed: 0, partial: false });
  for (const invalid of [null, undefined, -1, "4", NaN, Number.MAX_SAFE_INTEGER + 1]) {
    assert.equal(counts({ files: [{ addedLines: invalid, removedLines: 0 }] }).added, null);
  }
  h.current.changesSummary = { files: [{ path: "missing.cs", addedLines: null, removedLines: null }], isPartial: true };
  await h.click(h.ids.get("inspector-toggle"));
  const inspector = h.ids.get("output-inspector"), totals = inspector.querySelector(".inspector-change-counts");
  assert.equal(totals.querySelector(".review-added"), null);
  assert.equal(totals.querySelector(".review-removed"), null);
  assert.match(totals.textContent, /verfügbar/i);
  assert.ok(totals.textContent.includes("teilweise"));
  h.current.changesSummary = { files: [{ path: "known.cs", addedLines: 4, removedLines: 2 }], isPartial: true };
  h.emit("coding.changes", h.current.changesSummary);
  assert.equal(inspector.querySelector(".inspector-change-counts .review-added").textContent, "+4");
  assert.equal(inspector.querySelector(".inspector-change-counts .review-removed").textContent, "−2");
  assert.ok(inspector.querySelector(".inspector-change-counts").textContent.includes("teilweise"));
});

test("inspector and review counters match native N0 formatting in the PC application's document language", async () => {
  const h=harness({changesSummary:{files:[{path:"script.py",addedLines:5429,removedLines:1234}],isPartial:true}});
  h.context.document.documentElement={lang:"de-DE"};
  await h.click(h.ids.get("inspector-toggle"));
  const inspector=h.ids.get("output-inspector");
  assert.equal(inspector.querySelector(".review-added").textContent,"+5.429");
  assert.equal(inspector.querySelector(".review-removed").textContent,"−1.234");
  await h.click(inspector.querySelector(".inspector-changes"));
  assert.ok(h.ids.get("browser-view-panel").textContent.includes("+5.429 −1.234"));
  assert.deepEqual(plain(h.context.missumPanels.changeCounts(h.current.changesSummary)),{added:5429,removed:1234,partial:true},"stored receipt values remain numbers");
  h.context.document.documentElement.lang="en-US";
  h.context.missumPanels.render();
  assert.equal(inspector.hidden, true, "review owns a dedicated view without the outputs overlay");
  assert.ok(h.ids.get("browser-view-panel").textContent.includes("+5,429 −1,234"),"language changes refresh the existing view without a new receipt");
  h.context.missumPanels.setView("chat");
  assert.equal(inspector.querySelector(".review-added").textContent,"+5,429");
  assert.equal(inspector.querySelector(".review-removed").textContent,"−1,234");
});

test("the review marks unknown and binary counts and truncated receipts without coloring Git headers as edits", () => {
  const diff = "diff --git a/main.cs b/main.cs\n--- a/main.cs\n+++ b/main.cs\n@@ -1 +1 @@\n-old\n+new\n";
  const h = harness({ changesSummary: { isPartial: true, files: [
    { path: "main.cs", addedLines: null, removedLines: null, diff, diffTruncated: true },
    { path: "picture.png", isBinary: true, addedLines: null, removedLines: null }
  ] } });
  h.context.missumPanels.setView("review");
  const panel = h.ids.get("browser-view-panel"), files = panel.querySelectorAll(".review-file");
  assert.match(panel.textContent, /unvollständig|teilweise/);
  assert.match(files[0].querySelector("summary").textContent, /Zeilenzahlen nicht verfügbar/);
  assert.match(files[1].querySelector("summary").textContent, /Binärdatei/);
  assert.equal(panel.textContent.includes("+?"), false);
  assert.equal(panel.textContent.includes("−?"), false);
  assert.match(files[0].textContent, /Diff ist gekürzt/);
  assert.deepEqual(files[0].querySelectorAll(".review-added").map(line => line.textContent), ["+new\n"]);
  assert.deepEqual(files[0].querySelectorAll(".review-removed").map(line => line.textContent), ["-old\n"]);
  assert.equal(files[0].querySelectorAll(".review-diff-header").length, 4);
});

test("artifact preview, open and download controls use the shared host and render safe client media", async () => {
  const artifact = { id: "plot", fileName: "Ergebnis.png", contentType: "image/png" };
  const h = harness({ messages: [message("artifact", { artifacts: [artifact, artifact, { id: "vision", fileName: "Input.png", metadata: { role: "vision_input" } }] })] });
  await h.click(h.ids.get("inspector-toggle"));
  const inspector = h.ids.get("output-inspector");
  assert.equal(inspector.querySelectorAll(".inspector-artifact").length, 1, "replayed artifact references are deduplicated and vision inputs remain excluded");
  await h.click(h.buttons(inspector, "Vorschau")[0]);
  assert.deepEqual(plain(h.posts.at(-1)), { type: "artifact.preview", payload: { artifactId: "plot" } });
  assert.match(inspector.textContent, /Vorschau wird geladen/);
  h.current.artifactPreviewUrls.set("plot", { url: "/assistant/resources/plot.png" });
  h.emit("artifact.previewReady", { artifactId: "plot", url: "/assistant/resources/plot.png" });
  assert.equal(inspector.querySelector("img").src, "http://192.168.1.2:8080/assistant/resources/plot.png");
  assert.equal(inspector.querySelector("img").alt, "Ergebnis.png");
  await h.click(h.buttons(inspector, "Öffnen")[0]); await h.click(h.buttons(inspector, "Herunterladen")[0]);
  assert.deepEqual(plain(h.posts.slice(-2)), [
    { type: "artifact.open", payload: { artifactId: "plot", sessionId: "chat-a" } },
    { type: "artifact.save", payload: { artifactId: "plot", sessionId: "chat-a" } }
  ]);
  h.current.artifactPreviewUrls.set("plot", { url: "https://foreign.example/plot.png" });
  h.emit("artifact.previewReady", { artifactId: "plot", url: "https://foreign.example/plot.png" });
  assert.equal(inspector.querySelector("img"), null, "foreign preview URLs never become embedded resources");
});

test("catalog snapshots preserve an open model draft and model changes commit only after the dialog is closed", async () => {
  const h = harness(), picker = h.ids.get("model-picker"), overlay = h.ids.get("local-model-overlay");
  const models = [{ id: "A", role: "coding", downloaded: true, name: "Modell A" }, { id: "B", role: "coding", downloaded: true, name: "Modell B" }];
  h.emit("models.snapshot", { selectedModelId: "A", models });
  assert.equal(picker.value, "A");
  overlay.hidden = false; picker.value = "B";
  const before = h.posts.length;
  await picker.dispatch("change");
  assert.equal(h.posts.length, before, "changing a draft selector makes no immediate models.select call");
  h.emit("models.snapshot", { selectedModelId: "A", models: models.map(model => ({ ...model, name: model.name + " aktualisiert" })) });
  assert.equal(picker.value, "B", "refreshing catalog metadata cannot reset the unsaved selection");
  h.emit("state.snapshot", {});
  assert.equal(picker.value, "B", "same-model state updates retain the open draft");
  overlay.hidden = true; h.context.missumPanels.render();
  assert.equal(picker.value, "A", "a closed dialog returns to the authoritative model");
  picker.value = "B"; await picker.dispatch("change");
  assert.deepEqual(plain(h.posts.at(-1)), { type: "models.select", payload: { modelId: "B" } });
});

test("PDF export remains available in the message context menu with the current session and message identity", async () => {
  const currentMessage = message("export-this", { content: "Wichtige Antwort" }), h = harness({ messages: [currentMessage] });
  const article = new TestNode("article"); article.className = "message"; article.dataset.messageId = currentMessage.id;
  article.closest = selector => selector === "article.message" ? article : null;
  let prevented = false;
  for (const listener of h.documentEvents.get("contextmenu")) listener({ target: article, clientX: 120, clientY: 160, preventDefault() { prevented = true; } });
  assert.equal(prevented, true);
  const menu = h.context.document.body.querySelector(".browser-read-menu");
  await h.click(h.buttons(menu, "Nachricht als PDF exportieren")[0]);
  assert.deepEqual(plain(h.posts.at(-1)), { type: "message.exportPdf", payload: { sessionId: "chat-a", messageId: currentMessage.id } });
  assert.equal(h.context.document.body.querySelector(".browser-read-menu"), null);
});
