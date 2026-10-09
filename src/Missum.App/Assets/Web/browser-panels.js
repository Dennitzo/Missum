(function () {
  "use strict";
  const byId = id => document.getElementById(id);
  const panel = byId("browser-view-panel");
  const tabs = byId("session-tabs");
  const inspector = byId("output-inspector");
  if (!panel || !tabs || !globalThis.missumApp) return;
  let view = globalThis.location?.hash === "#settings" ? "settings" : "chat";
  let ownerSession = null;
  const inspectorDevice = globalThis.missumBridge?.deviceId || String(globalThis.missumBridge?.clientId || "browser").replace(/\.[^.]+$/, "");
  const inspectorPreferenceKey = `assistant.outputs-visible:v1:${inspectorDevice}`;
  let inspectorOpen = false;
  try { inspectorOpen = globalThis.localStorage?.getItem(inspectorPreferenceKey) === "1"; }
  catch { /* Keep the default when private browser storage is unavailable. */ }
  let childId = null;
  let presentation = null;
  const presentations = new Map();
  const children = new Map();
  let renderTimer = null;
  let tabSignature = "";
  let panelSignature = "";
  let inspectorSignature = "";
  const sourceTabs = new Set();
  const overviewTabs = new Set();
  const reviewTabs = new Set();
  const childTabs = new Map();
  let observationOrder = 0;
  let planetSequence = 0;
  let simulationPane = null;
  function node(tag, className, text) {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (text !== undefined) element.textContent = String(text);
    return element;
  }
  function button(label, handler, className) {
    const element = node("button", className || "secondary", label); element.type = "button"; element.addEventListener("click", handler); return element;
  }
  function state() { return globalThis.missumApp.getState(); }
  function post(type, payload) { return globalThis.missumBridge.post(type, payload); }
  function formattedCount(value) { return new Intl.NumberFormat(document.documentElement?.lang || "de", { maximumFractionDigits: 0 }).format(value); }
  function tabState(sessionId = state().activeSessionId) {
    if (!childTabs.has(sessionId)) {
      let saved;
      try { saved = JSON.parse(sessionStorage.getItem(`assistant.child-tabs:${globalThis.missumBridge.clientId}:${sessionId}`) || "null"); } catch { /* Optional client storage. */ }
      childTabs.set(sessionId, { open: new Set(saved?.open || []), closed: new Set(saved?.closed || []) });
    }
    return childTabs.get(sessionId);
  }
  function saveTabs(sessionId = state().activeSessionId) {
    const local = tabState(sessionId);
    try { sessionStorage.setItem(`assistant.child-tabs:${globalThis.missumBridge.clientId}:${sessionId}`, JSON.stringify({ open: [...local.open], closed: [...local.closed] })); } catch { /* Optional client storage. */ }
  }
  function childRunning(child) { return !/^(completed|failed|denied|cancelled|interrupted|disabled|unavailable|rejected)$/i.test(child.status || "") && (child.isRunning === true || /^(running|pending|queued|waiting|waitingforclient)$/i.test(child.status || "")); }
  function childTitle(child) { return child.title || child.name || child.taskName || "Subagent"; }
  function childCreated(child) { return Date.parse(child.attemptStartedAt || child.createdAt || child.startedAt || child.messages?.find(message => message.role === "user")?.createdAt || "") || 0; }
  function sessionChildren() { return [...children.values()].filter(child => child.sessionId === state().activeSessionId).sort((a, b) => childCreated(b) - childCreated(a) || (b._observationOrder || 0) - (a._observationOrder || 0)); }
  function childStatus(child) { return ({ completed: "fertig", failed: "Fehlgeschlagen", denied: "Fehlgeschlagen", cancelled: "Abgebrochen", interrupted: "Unterbrochen", disabled: "Nicht verfügbar", unavailable: "Nicht verfügbar" })[String(child.status).toLowerCase()] || "arbeitet"; }
  function observeChild(child) {
    const id = child.agentId || child.runId; if (!id) return;
    const previous = children.get(id), resumed = previous && previous.runId !== child.runId;
    if (previous && (resumed ? (previous.previousRunIds || []).includes(child.runId) || childCreated(child) < childCreated(previous) : Number(child.projectionRevision || 0) < Number(previous.projectionRevision || 0))) return;
    const next = { ...child, _observationOrder: previous && !resumed ? previous._observationOrder : ++observationOrder };
    if (resumed) next.previousRunIds = [...new Set([...(previous.previousRunIds || []), previous.runId, ...(child.previousRunIds || [])])];
    children.set(id, next);
    if (next.sessionId && childRunning(next)) { const local = tabState(next.sessionId); if (!local.closed.has(id)) local.open.add(id); saveTabs(next.sessionId); }
    refreshSubagentReceipts();
  }
  function syncChildren(list) {
    const present = new Set(list.map(child => child.agentId || child.runId));
    for (const child of list) observeChild(child);
    for (const id of children.keys()) if (!present.has(id)) children.delete(id);
    refreshSubagentReceipts();
  }
  function refreshSubagentReceipts() { for(const row of document.querySelectorAll(".subagent-lifecycle"))row._refreshSubagentReceipt?.(); }
  function createSubagentReceipt(step) {
    if(!["subagent","subagent.completed"].includes(step.tool))return null;
    const parse=value=>{try{return typeof value==="string"?JSON.parse(value):value;}catch{return null;}}, output=parse(step.outputJson), input=parse(step.inputJson);
    const agentId=output?.agentId || input?.agentId || step.agentId || "", runId=output?.runId || input?.runId || "", owner=state().activeSessionId;
    const assigned=input?.task || step.detail || output?.title || "Subagent";
    const title=value=>{let line=String(value || "Subagent").split("\n").find(line=>line.trim())?.trim().replace(/\s+/g," ") || "Subagent"; const end=/[.!?](?=\s|$)/g;let match;while((match=end.exec(line)))if(match.index>=12){line=line.slice(0,match.index);break;}return line.length<=100?line.replace(/[.!?]+$/,"") || "Subagent":line.slice(0,100).replace(/\s+\S*$/,"")+"…";};
    const row=button("",()=>{const child=findChild();if(child && state().activeSessionId===owner)setView("subagent",child.agentId || child.runId);},"subagent-lifecycle");
    row.dataset.speechExclude="true";row.dataset.agentId=agentId;row.dataset.runId=runId;
    const avatar=node("span","subagent-lifecycle__avatar"), task=node("span","subagent-lifecycle__task"), caption=node("span","subagent-lifecycle__caption");row.append(avatar,task,caption);
    let appearanceChosen=false, legacyCompletion=false, selectedTitle="", selectedPlanet=null;
    function findChild(){if(state().activeSessionId!==owner)return null;const direct=children.get(agentId);return direct?.sessionId===owner?direct:[...children.values()].find(child=>child.sessionId===owner && runId && child.runId===runId);}
    row._refreshSubagentReceipt=()=>{
      const child=findChild(), sameAttempt=child && (!runId || child.runId===runId), metadata=sameAttempt?child:output;
      if(!appearanceChosen && (child || typeof output?.resultDelivered==="boolean" || output?.status)) {legacyCompletion=step.tool!=="subagent.completed" && typeof metadata?.resultDelivered!=="boolean" && String(metadata?.status || step.status).toLowerCase()==="completed";appearanceChosen=true;}
      if(!selectedTitle)selectedTitle=title(sameAttempt?childTitle(child):output?.title || assigned);task.textContent=selectedTitle;
      caption.textContent=step.tool==="subagent.completed" || legacyCompletion?"hat die Arbeit beendet":"hat die Arbeit begonnen";
      const identity=child || {agentId,runId}, index=identity.planetIndex;
      if(!avatar.firstChild || selectedPlanet!==index){avatar.replaceChildren(planet(identity,14));selectedPlanet=index;}
      row.disabled=!child;row.setAttribute("aria-label",`${selectedTitle} ${caption.textContent}`);row.title=`${row.getAttribute("aria-label")}\n\n${assigned}\n\nSubagent öffnen`;
    };
    row._refreshSubagentReceipt();return row;
  }
  function svgNode(tag, attributes, parent) {
    const element = document.createElementNS ? document.createElementNS("http://www.w3.org/2000/svg", tag) : document.createElement(tag);
    for (const [key, value] of Object.entries(attributes || {})) element.setAttribute(key, String(value));
    if (parent) parent.append(element); return element;
  }
  function icon(key) {
    const element = svgNode("svg", { viewBox: "0 0 16 16", "aria-hidden": "true", focusable: "false", class: `session-tab-icon session-tab-icon--${key}` });
    const paths = { chat: "M2 2h12v9H7l-4 3v-3H2Z", publication: "M8 1a6 6 0 1 0 0 12 6 6 0 0 0 0-12ZM2 7h12M8 1c-4 3-4 9 0 12m0-12c4 3 4 9 0 12", simulation: "M2 3h12v10H2ZM5 10l2-3 2 2 3-4", sources: "M8 1a7 7 0 1 0 0 14 7 7 0 0 0 0-14ZM1 8h14M8 1c-5 4-5 10 0 14m0-14c5 4 5 10 0 14", review: "M3 1h6l4 4v10H3ZM9 1v4h4M5 8h6M5 11h6", copy: "M6 5h8v10H6ZM10 5V1H2v10h4", close: "M3 3l10 10M13 3 3 13", link: "M6.5 5.5 8 4a3 3 0 0 1 4.2 4.2l-2 2a3 3 0 0 1-4.2 0M9.5 10.5 8 12a3 3 0 0 1-4.2-4.2l2-2a3 3 0 0 1 4.2 0" };
    svgNode("path", { d: paths[key] || paths.chat, fill: "none", stroke: "currentColor", "stroke-width": key === "close" ? 1 : 1.2, "stroke-linejoin": "round" }, element); return element;
  }
  function planet(child, size = 14) {
    // NativePlanetPalette + NativeSubagentAvatar, using the PC's persisted index.
    let index = child.planetIndex;
    if (!Number.isInteger(index) || index < 0 || index > 2147483647) { let hash = 2166136261; const id=String(child.agentId || child.runId || ""); for(let position=0;position<id.length;position++)hash=Math.imul(hash^id.charCodeAt(position),16777619)>>>0; index = hash & 2147483647; }
    const families = [[76,159,239],[237,126,152],[224,169,65],[83,195,161],[165,128,235],[230,116,65],[84,189,218],[205,139,207],[137,184,77],[218,179,124],[111,132,235],[229,100,124],[98,200,191],[183,174,236],[197,183,76],[140,171,203]];
    const round = value => value % 1 === .5 ? Math.floor(value) + Math.floor(value) % 2 : Math.round(value);
    const mix = (first, second, amount) => first.map((value, position) => round(value + (second[position] - value) * amount));
    const color = value => `rgb(${value.join(",")})`, mixed = ((index & 4095) * 1193 + 417) & 4095, key = Math.floor(index / 4096) * 16 + (mixed >> 8);
    let body = families[key];
    if (!body) { const reserved = families.filter(value => value[0] >= 128).map(value => ((value[0] - 128) << 16) | (value[1] << 8) | value[2]).sort((a,b) => a-b), capacity = 8388608 - reserved.length; let multiplier = 7919; const gcd = (a,b) => { while (b) [a,b] = [b,a%b]; return a; }; while (gcd(multiplier, capacity) !== 1) multiplier += 2; let rank = ((key - 16) * multiplier) % capacity; for (const value of reserved) { if (rank < value) break; rank++; } body = [128+(rank>>16),(rank>>8)&255,rank&255]; }
    const light = mix(body,[255,250,240],.42), dark = mix(body,[18,25,49],.47), accent = mix(body,[231,245,250],.65), surface = mixed & 7, orbit = (mixed >> 3) & 7, satellite = (mixed >> 6) & 3;
    const root = svgNode("svg", { viewBox: "0 0 32 32", width: size, height: size, "aria-hidden": "true", focusable: "false", class: "subagent-planet", "data-planet-index": index, "data-planet-signature": `${body.map(value => value.toString(16).padStart(2,"0")).join("").toUpperCase()}:${surface}:${orbit}:${satellite}` });
    const uid = `missum-planet-${++planetSequence}`, defs = svgNode("defs", {}, root), gradient = svgNode("linearGradient", { id: `${uid}-gradient`, x1: 0, y1: 0, x2: 1, y2: 1 }, defs);
    for (const [offset,value] of [[0,light],[.48,body],[1,dark]]) svgNode("stop", { offset, "stop-color": color(value) }, gradient);
    const clip = svgNode("clipPath", { id: `${uid}-clip` }, defs); svgNode("circle", { cx:16,cy:16,r:10.8 },clip);
    const ellipse = (parent,x,y,w,h,fill,extra={}) => svgNode("ellipse", { cx:x,cy:y,rx:w/2,ry:h/2,fill:color(fill),...extra },parent);
    const polygon = (parent,fill,points) => svgNode("polygon", { points,fill:color(fill) },parent);
    const line = (parent,stroke,width,points) => svgNode("polyline", { points,fill:"none",stroke:color(stroke),"stroke-width":width,"stroke-linejoin":"round" },parent);
    const curve = (parent,stroke,width,data,extra={}) => svgNode("path", { d:data,fill:"none",stroke:color(stroke),"stroke-width":width,...extra },parent);
    const drawOrbit = front => {
      const angle = ({2:-32,3:32,4:-12,5:64,6:-21,7:17})[orbit] || 0, h = ({4:4,5:8,7:24})[orbit] || 10, w = orbit===7 ? 29 : 29.8, stroke = mix(accent,body,front?.12:.6), extra = { transform:`rotate(${angle} 16 16)`, ...(orbit===7 ? {"stroke-dasharray":"1.1 1.5"} : {}) };
      if (!front) ellipse(root,16,16,w,h,stroke,{fill:"none",stroke:color(stroke),"stroke-width":orbit===6?2.8:1.9,...extra});
      else { const data = `M${16-w/2} 16C${16-w/2} ${16+h*.69} ${16+w/2} ${16+h*.69} ${16+w/2} 16`; curve(root,stroke,orbit===6?2.8:1.9,data,extra); if(orbit===6)curve(root,dark,.8,data,{transform:extra.transform}); }
    };
    if (orbit) drawOrbit(false);
    ellipse(root,16,16,22,22,body,{fill:`url(#${uid}-gradient)`});
    const land = svgNode("g", { "clip-path":`url(#${uid}-clip)` },root);
    if (surface===0) { const green=mix(body,[164,226,165],.62); polygon(land,green,"8,6 15,7 13,11 16,15 12,18 8,14");polygon(land,green,"20,16 25,15 25,22 19,25 18,21");ellipse(land,20,9,4.2,2.7,green); }
    else if(surface===1) {for(let band=0;band<4;band++)curve(land,band%2===0?accent:dark,2.4,`M4 ${9+band*4.3}C12 ${5+band*4.3} 20 ${13+band*4.3} 28 ${9+band*4.3}`);ellipse(land,21,18,5.2,2.4,dark);}
    else if(surface===2) {for(const [x,y,s] of [[11,11,5.5],[20,16,7],[12,23,4.8],[22,8,3.1]]){ellipse(land,x,y,s,s,dark);ellipse(land,x-.6,y-.7,s-1.6,s-1.6,mix(body,light,.2));}}
    else if(surface===3) {const lava=mix(accent,[255,172,79],.58);line(land,lava,2,"8,5 13,12 10,18 15,27");line(land,lava,1.8,"24,6 19,13 23,20 20,27");line(land,lava,1.5,"11,17 19,14 25,17");}
    else if(surface===4) {polygon(land,accent,"4,4 28,4 24,10 19,8 16,12 10,9");polygon(land,accent,"4,28 28,28 23,22 18,25 12,21 8,24");line(land,accent,1.3,"8,15 13,17 17,14");line(land,dark,1.1,"19,12 18,18 23,20");}
    else if(surface===5) {curve(land,accent,3.2,"M6 17C8 3 27 4 25 18");curve(land,dark,3.1,"M25 18C21 31 6 25 10 15");curve(land,accent,2.2,"M10 15C14 9 23 13 17 20");}
    else if(surface===6) for(let ridge=0;ridge<4;ridge++)line(land,ridge%2===0?accent:dark,2.3,`${3+ridge*5},29 ${7+ridge*5},16 ${16+ridge*5},3`);
    else {polygon(land,accent,"5,7 16,5 14,15 6,19");polygon(land,dark,"16,5 27,11 22,18 14,15");polygon(land,mix(accent,body,.5),"14,15 22,18 19,27 9,24");line(land,accent,1.1,"5,20 14,15 20,28");}
    ellipse(root,11.7,9.9,3.4,1.8,light,{opacity:.6}); if(orbit)drawOrbit(true);
    if(satellite===1){ellipse(root,28.1,4.1,4.2,4.2,accent);ellipse(root,28.7,4.6,1.5,1.5,dark);}
    else if(satellite===2){ellipse(root,3.4,27.5,3.3,3.3,light);ellipse(root,6.8,30,2,2,accent);}
    else if(satellite===3){line(root,accent,1.5,"28.5,1.7 28.5,7");line(root,accent,1.5,"25.8,4.3 31.1,4.3");ellipse(root,3.1,27.7,2.8,2.8,light);}
    return root;
  }
  function url(value, local = false) {
    try {
      const target = new URL(globalThis.missumBridge.resourceUrl(value), location.href);
      if (!value || !["http:", "https:"].includes(target.protocol)) return null;
      if (local && globalThis.missumBridge.isLanBrowser && target.origin !== location.origin) return null;
      return target.href;
    } catch { return null; }
  }
  function link(title, value) {
    const target = url(value); if (!target) return node("span", "", title);
    const element = node("a", "browser-resource-link", title); element.href = target; element.target = "_blank"; element.rel = "noopener noreferrer"; return element;
  }
  function setView(next, agentId) {
    if (next === "research") next = "chat";
    if (view === "settings" && next !== "settings") globalThis.missumSettings?.leave();
    if (next === "subagent" && agentId) { const local = tabState(); local.open.add(agentId); local.closed.delete(agentId); saveTabs(); }
    if (next === "subagents") overviewTabs.add(state().activeSessionId);
    if (next === "sources") sourceTabs.add(state().activeSessionId);
    if (next === "review") reviewTabs.add(state().activeSessionId);
    view = next; childId = agentId || null; panelSignature = "";
    try { if (next !== "settings") sessionStorage.setItem(`assistant.view:${globalThis.missumBridge.clientId}:${state().activeSessionId}`, JSON.stringify({ view: next, childId })); } catch { /* Storage is optional. */ }
    byId("settings-page").hidden = next !== "settings";
    if (["publication", "simulation"].includes(next) && state().scientificResearch?.selectedProjectId) post("science.presentation.get", { sessionId: state().activeSessionId, projectId: state().scientificResearch.selectedProjectId });
    render();
  }
  function renderTabs() {
    const current = state(); const session = current.sessions.find(item => item.id === current.activeSessionId);
    const local = tabState();
    const signature=JSON.stringify([current.activeSessionId,current.chatMode,session?.title,view,childId,[...local.open],sessionChildren().filter(child => local.open.has(child.agentId || child.runId)).map(child => [child.agentId || child.runId,childTitle(child),child.status,child.planetIndex]),sourceTabs.has(current.activeSessionId),overviewTabs.has(current.activeSessionId),reviewTabs.has(current.activeSessionId),current.changesSummary?.files?.length,document.documentElement?.lang]);
    if(signature===tabSignature)return; tabSignature=signature;
    const previousOffset = tabs.scrollLeft || 0;
    tabs.replaceChildren();
    const add = (label, key, glyph, action, close, selected = view === key) => {
      const element = button("", action || (() => setView(key)), close ? "session-view-tab__select" : "session-view-tab");
      element.append(glyph, node("span", "session-view-tab__label", label)); element.setAttribute("role", "tab"); element.setAttribute("aria-selected", String(selected)); element.title = label;
      element.dataset.view = key;
      if (close) { const container = node("div", "session-view-tab session-view-tab--closable"); container.dataset.view = key; container.setAttribute("aria-selected", String(selected)); const dismiss = button("", close, "session-view-tab__close"); dismiss.append(icon("close")); dismiss.setAttribute("aria-label", `${key === "subagent" ? "Subagent" : label}-Tab schließen${key === "subagent" ? `: ${label.replace(/^Subagent · /, "")}` : ""}`); container.append(element,dismiss); tabs.append(container); }
      else tabs.append(element);
      return element;
    };
    const chatIcon = icon("chat"); chatIcon.dataset.mode = current.chatMode;
    if(current.activeSessionId)add(session?.title || "Neue Sitzung", "chat", chatIcon);
    if (current.activeSessionId && current.chatMode === "claudescience") { add("Publikation", "publication", icon("publication")); add("Simulation", "simulation", icon("simulation")); }
    for (const child of children.values()) {
      if (child.sessionId !== current.activeSessionId) continue;
      const id = child.agentId || child.runId;
      if (!local.open.has(id)) continue;
      const childTab=add(`Subagent · ${childTitle(child)}`, "subagent", planet(child,13), () => setView("subagent",id), () => { local.open.delete(id); local.closed.add(id); saveTabs(); if(view === "subagent" && childId === id)setView("chat"); else render(); }, view === "subagent" && childId === id); childTab.title=`${childTitle(child)}\n${childStatus(child)}`;
    }
    const closeView = (key,collection) => { collection.delete(current.activeSessionId); if(view===key)setView("chat"); else render(); };
    if(sourceTabs.has(current.activeSessionId))add("Quellen","sources",icon("sources"),null,() => closeView("sources",sourceTabs));
    if(overviewTabs.has(current.activeSessionId))add("Subagenten","subagents",planet({agentId:"subagent-overview"},13),null,() => closeView("subagents",overviewTabs));
    if(reviewTabs.has(current.activeSessionId)) { const count=current.changesSummary?.files?.length || 0; add(`Änderungen · ${formattedCount(count)} ${count===1?"Datei":"Dateien"}`,"review",icon("review"),null,() => closeView("review",reviewTabs)); }
    if (view === "settings") add("Einstellungen", "settings", icon("review"), () => globalThis.missumSettings?.open());
    tabs.scrollLeft = previousOffset;
  }
  function collectSources(messages) {
    const found = new Map();
    function collect(value, depth) {
      if (depth > 20 || found.size >= 150 || !value) return;
      if (typeof value === "string") {
        if (value.trimStart().startsWith("{") || value.trimStart().startsWith("[")) { try { collect(JSON.parse(value), depth + 1); } catch { /* Ordinary text. */ } }
      } else if (Array.isArray(value)) for (const child of value) collect(child, depth + 1);
      else if (typeof value === "object") {
        const title = value.title || value.pageTitle || value.name;
        for (const [key, child] of Object.entries(value)) {
          if (/url|href/i.test(key) && !/thumbnail|image|icon/i.test(key) && typeof child === "string") {
            const target = /^https?:\/\//i.test(child) ? url(child) : null; if (target) found.set(target, title || found.get(target) || new URL(target).hostname);
          } else collect(child, depth + 1);
        }
      }
    }
    for (const message of messages) { collect(message.sources, 0); collect(message.toolSteps, 0); }
    return Array.from(found, ([href, title]) => ({ href, title }));
  }
  function section(title, detail) {
    const element = node("section", "browser-panel-card"); element.append(node("h2", "", title)); if (detail) element.append(node("p", "browser-muted", detail)); return element;
  }
  function sourceActions(current, messages) {
    const actions = new Map();
    for (const message of messages) {
      for (const step of message.toolSteps || []) {
        if (!/^(web|research)\./.test(step.tool || "")) continue;
        const sources = collectSources([{ toolSteps: [step] }]); if (!sources.length) continue;
        let input; try { input = JSON.parse(step.inputJson || "{}"); } catch { input = {}; }
        const label = step.tool === "web.search" ? "Websuche" : step.tool === "web.fetch" ? "Webseite" : "Recherche";
        actions.set(`step:${step.id}`, { id: `step:${step.id}`, title: input.query ? `${label}: ${input.query}` : label, sources, time: step.updatedAt || step.startedAt || message.createdAt });
      }
      if (message.sources?.length) { const sources = collectSources([{ sources: message.sources }]); if (sources.length) actions.set(`message:${message.id}`, { id: `message:${message.id}`, title: "Quellen", sources, time: message.createdAt }); }
    }
    const known = new Set([...actions.values()].flatMap(action => action.sources.map(source => source.href)));
    for (const work of current.scientificResearch?.detail?.works || []) {
      const raw = work.url || work.canonicalUrl, href = /^https?:\/\//i.test(raw || "") ? url(raw) : null; if (!href || known.has(href)) continue;
      known.add(href); actions.set(`work:${href}`, { id: `work:${href}`, title: work.title || new URL(href).hostname, sources: [{ href, title: work.title || new URL(href).hostname }], time: work.updatedAt });
    }
    for (const item of [...current.documents || [], ...current.attachments || []]) {
      const id = `attachment:${item.id}`;
      actions.set(id, { id, title: item.fileName || item.name || "Anhang", attachment: item, document: "pageCount" in item || (current.documents || []).some(document => document.id === item.id), sources: [], time: item.createdAt });
    }
    const titles = new Map();
    for (const action of actions.values()) for (const source of action.sources) if (source.title !== new URL(source.href).hostname) titles.set(source.href, source.title);
    for (const work of current.scientificResearch?.detail?.works || []) { const raw = work.url || work.canonicalUrl, href = /^https?:\/\//i.test(raw || "") ? url(raw) : null; if (href && work.title) titles.set(href, work.title); }
    for (const action of actions.values()) for (const source of action.sources) if (titles.has(source.href)) source.title = titles.get(source.href);
    for (const action of actions.values()) if (action.title === "Webseite") { const title = action.sources.find(source => titles.has(source.href))?.title; if (title) for (const source of action.sources) if (!titles.has(source.href)) source.title = title; }
    return [...actions.values()].sort((a, b) => (Date.parse(b.time) || 0) - (Date.parse(a.time) || 0));
  }
  function sourceRow(action, full = false) {
    if (action.attachment) {
      const row = node("div", "inspector-source-row inspector-source-row--attachment"), fileIcon=node("span", "inspector-icon inspector-icon--file");
      const extension=String(action.title).split(".").at(-1).toLowerCase(); fileIcon.dataset.fileType=extension==="pdf"?"pdf":/^(avif|bmp|gif|ico|jpeg|jpg|png|svg|tif|tiff|webp)$/.test(extension)?"image":/^(aac|aiff|flac|m4a|mp3|ogg|opus|wav|wma)$/.test(extension)?"audio":/^(avi|m4v|mkv|mov|mp4|mpeg|mpg|webm)$/.test(extension)?"video":/^(c|cpp|cs|css|go|h|hpp|html|ipynb|java|js|json|jsx|lean|ps1|psm1|py|r|rs|sh|sql|tex|toml|ts|tsx|xaml|xml|yaml|yml)$/.test(extension)?"code":"document";
      row.append(fileIcon, node("span", "inspector-source-title", action.title)); row.title = action.title;
      if (full) { const remove = button("Anhang entfernen", () => post(action.document ? "document.remove" : "attachment.remove", action.document ? { documentId: action.attachment.id } : { attachmentId: action.attachment.id }), "source-remove"); row.append(remove); }
      return row;
    }
    const source = action.sources[0], row = link(source.title, source.href); row.className = "inspector-source-row"; row.replaceChildren(node("span", "inspector-icon inspector-icon--web"));
    const labels = node("span", "inspector-source-labels"); labels.append(node("span", "inspector-source-title", source.title), node("span", "inspector-source-url", source.href)); row.append(labels); row.title = `${source.title}\n${source.href}`; return row;
  }
  function changeCounts(summary) {
    const files = summary?.files || [], textFiles = files.filter(file => !file.isBinary);
    return { added: textFiles.every(file => Number.isSafeInteger(file.addedLines) && file.addedLines >= 0) ? textFiles.reduce((sum, file) => sum + file.addedLines, 0) : null,
      removed: textFiles.every(file => Number.isSafeInteger(file.removedLines) && file.removedLines >= 0) ? textFiles.reduce((sum, file) => sum + file.removedLines, 0) : null, partial: summary?.isPartial === true };
  }
  function renderInspector() {
    const supportsInspector = view === "chat" || view === "subagent";
    const visible = supportsInspector && inspectorOpen;
    const toggle = byId("inspector-toggle");
    inspector.hidden = !visible;
    if (toggle) {
      toggle.hidden = !supportsInspector;
      toggle.setAttribute("aria-expanded", String(visible));
      toggle.setAttribute("aria-label", visible ? "Ausgaben schließen" : "Ausgaben einblenden");
      toggle.title = visible ? "Ausgaben schließen" : "Ausgaben einblenden";
    }
    if (inspector.hidden) return;
    const current = state(), child = children.get(childId), localChildren = [...children.values()].filter(item => item.sessionId === current.activeSessionId);
    const messages = view === "subagent" ? child?.messages || [] : [...current.messages, ...localChildren.flatMap(item => item.messages || [])];
    const sources = sourceActions(current, messages);
    const signature = JSON.stringify([current.activeSessionId, current.workspacePath, current.changesSummary, sources, localChildren.map(item => [item.agentId || item.runId, item.title || item.name, item.status, item.isRunning, item.planetIndex, childCreated(item), item._observationOrder]),document.documentElement?.lang]);
    if (inspectorSignature === signature) return; inspectorSignature = signature;
    const header = node("header", "inspector-heading"), close = button("×", toggleInspector, "icon-button"); close.setAttribute("aria-label", "Ausgaben schließen"); header.append(node("span", "", "Ausgaben"), close); inspector.replaceChildren(header);
    const workspace = node("div", "inspector-workspace"); workspace.append(node("span", "inspector-icon inspector-icon--folder"), node("span", "inspector-workspace-label", current.workspacePath?.replace(/[\\/]+$/, "").split(/[\\/]/).at(-1) || "Kein Projekt ausgewählt")); workspace.title = current.workspacePath || "Kein Projekt ausgewählt";
    if (current.workspacePath) { const copyPath = button("", () => post("message.copy", { text: current.workspacePath }), "inspector-copy-path"); copyPath.append(icon("copy")); copyPath.title = "Projektpfad kopieren"; copyPath.setAttribute("aria-label", "Projektpfad kopieren"); workspace.append(copyPath); } inspector.append(workspace);
    const changes = button("", () => setView("review"), "inspector-changes"); changes.append(node("span", "inspector-icon inspector-icon--changes"), node("span", "", "Änderungen"));
    const counts = changeCounts(current.changesSummary), totals = node("span", "inspector-change-counts");
    if (counts.added !== null && counts.removed !== null) totals.append(node("span", "review-added", `+${formattedCount(counts.added)}`), node("span", "review-removed", `−${formattedCount(counts.removed)}`)); else totals.append(node("span", "browser-muted", "Nicht verfügbar"));
    if (counts.partial) totals.append(node("span", "browser-muted", "teilweise")); changes.append(totals); changes.title = current.changesSummary?.notice || "Git-Diff anzeigen"; changes.setAttribute("aria-label", `Änderungen anzeigen${counts.partial ? " · Übersicht unvollständig" : ""}`); inspector.append(changes);
    if (localChildren.length) {
      const ordered = sessionChildren(), working = ordered.filter(childRunning), inactive = ordered.length-working.length;
      const caption = working.length ? `${working.length} ${working.length===1?"arbeitet":"arbeiten"}` : `${inactive} fertig`;
      const agents = node("section", "inspector-section inspector-subagents"), summary = button("", () => setView("subagents"), "inspector-subagent-summary"); agents.append(node("h2", "", "Subagenten"));
      const avatars = node("span", "inspector-subagent-planets"); for(const child of (working.length ? working : ordered).slice(0,4))avatars.append(planet(child));
      summary.append(avatars,node("span","inspector-subagent-caption",caption));
      if(working.length && inactive)summary.append(node("span","inspector-subagent-finished",`${inactive} fertig`));
      summary.classList.toggle("is-working", working.length>0); summary.setAttribute("aria-label", `${caption}${working.length && inactive ? ` · ${inactive} fertig` : ""}`);
      summary.title = `${summary.getAttribute("aria-label")}\nSubagentenübersicht öffnen${ordered.filter(child => !childRunning(child) && child.status !== "completed").map(child => `\n${childStatus(child)}`).join("")}`;
      agents.append(summary); inspector.append(agents);
    }
    const sourceSection = node("section", "inspector-section inspector-sources"); sourceSection.append(node("h2", "", "Quellen"));
    if (sources.length) {
      sourceSection.append(sourceRow(sources[0]));
      const all = button("", () => setView("sources"), "inspector-all-sources");
      all.append(icon("link"), node("span", "", "Alle anzeigen"));
      all.setAttribute("aria-label", "Alle Quellen anzeigen");
      sourceSection.append(all);
    }
    else sourceSection.append(node("p", "inspector-empty", "Noch keine Quellen"));
    inspector.append(sourceSection);
  }
  function toggleInspector() {
    if (view !== "chat" && view !== "subagent") return;
    inspectorOpen = !inspectorOpen;
    try { globalThis.localStorage?.setItem(inspectorPreferenceKey, inspectorOpen ? "1" : "0"); }
    catch { /* The current tab remains usable without persistent browser storage. */ }
    if (inspectorOpen) inspectorSignature = "";
    renderInspector();
  }
  function renderNavigation() {
    const current = state();
    const child = view === "subagent" ? children.get(childId) : null;
    globalThis.missumPromptTimeline?.render({
      container: byId("prompt-navigation"),
      scroller: child ? panel : byId("message-scroll"),
      messageRoot: child ? panel.querySelector(".subagent-messages") : byId("message-list"),
      messages: child ? child.messages || [] : current.messages || [],
      scopeKey: `${current.activeSessionId}:${child ? `subagent:${childId}` : "chat"}`,
      visible: view === "chat" || Boolean(child),
    });
  }
  function renderScience() {
    const current = state(); const data = presentation || {}; const publication = data.publication; const simulation = data.simulation;
    if (view === "publication") {
      const publicationView = node("section", "browser-publication-view");
      if (data.publicationError) publicationView.append(node("p", "browser-panel-error publication-error", data.publicationError));
      const pdf = url(publication?.pdfUrl || publication?.url, true);
      if (pdf) {
        const frame = node("iframe", "publication-frame"); frame.title = "Wissenschaftliche Publikation als PDF";
        frame.src = pdf.split("#", 1)[0] + "#view=FitH";
        publicationView.append(frame);
      } else publicationView.append(node("p", "browser-empty-state publication-empty-state", current.scientificResearch.selectedProjectId ? "Die wissenschaftliche Publikation wird erstellt. Der Arbeitsablauf bleibt im Chat sichtbar." : "Stelle im Chat eine Forschungsfrage."));
      panel.append(publicationView);
      return;
    }
    renderSimulation(current, simulation, data.simulationError);
  }

  function simulationDisclosure(label, resource) {
    if (!resource) return null;
    const disclosure = node("details", "simulation-source"); disclosure.append(node("summary", "", label));
    const text = node("pre", "", "Inhalt wird geladen …"); disclosure.append(text); let loaded = false;
    disclosure.addEventListener("toggle", async () => {
      if (!disclosure.open || loaded) return;
      try {
        const response = await fetch(resource, { credentials: "same-origin" });
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        const code = await response.text();
        text.textContent = code.slice(0, 100000) + (code.length > 100000 ? "\nVorschau auf 100.000 Zeichen begrenzt." : ""); loaded = true;
      } catch (error) { text.textContent = `Inhalt konnte nicht geladen werden: ${error.message}`; }
    });
    return disclosure;
  }

  function createSimulationCard(key, artifact, resource, interactive) {
    const item = node("section", "browser-panel-card simulation-card"); item.dataset.simulationId = key;
    const heading = node("h2", "", artifact.title || artifact.fileName || "Simulation");
    const provenance = node("p", "browser-muted");
    const toolbar = node("div", "simulation-toolbar");
    const viewport = node("div", "simulation-viewport");
    const media = node(interactive ? "iframe" : "img", interactive ? "simulation-frame" : "simulation-image");
    if (interactive) { media.title = artifact.title || "Interaktive Simulation"; media.setAttribute("sandbox", "allow-scripts"); }
    else media.alt = artifact.title || artifact.fileName || "Simulationsergebnis";
    if (resource) media.src = resource;
    const imageSurface = interactive ? null : node("div", "simulation-image-surface");
    if (imageSurface) { imageSurface.append(media); viewport.append(imageSurface); } else viewport.append(media);
    const sources = node("div", "simulation-sources");
    const entry = { item, heading, provenance, viewport, media, imageSurface, sources, scale: 1, resource, interactive, available: true };
    entry.open = node("a", "secondary simulation-open", "Öffnen");
    if (interactive) { entry.open.target = "_blank"; entry.open.rel = "noopener noreferrer"; }
    const zoom = scale => {
      entry.scale = Math.max(.5, Math.min(2, scale)); reset.textContent = `${Math.round(entry.scale * 100)} %`;
      if (interactive) { media.style.zoom = String(entry.scale); media.style.width = `${100 / entry.scale}%`; media.style.height = `${680 / entry.scale}px`; }
      else imageSurface.style.transform = `scale(${entry.scale})`;
    };
    const reset = button("100 %", () => zoom(1)); reset.setAttribute("aria-label", "Zoom auf 100 Prozent zurücksetzen");
    const smaller = button("−", () => zoom(entry.scale - .25)); smaller.setAttribute("aria-label", "Verkleinern");
    const larger = button("+", () => zoom(entry.scale + .25)); larger.setAttribute("aria-label", "Vergrößern");
    const reload = button("Neu laden", () => {
      if (!entry.resource) return;
      if (interactive) media.src = entry.resource;
      else { const fresh = new URL(entry.resource); fresh.searchParams.set("reload", String(Date.now())); media.src = fresh.href; }
    });
    const toggle = disclosure => { if (!disclosure) return; disclosure.open = !disclosure.open; if (disclosure.open) disclosure.scrollIntoView?.({ block: "nearest" }); };
    entry.sourceButton = button("Quellcode", () => toggle(entry.source));
    entry.dataButton = button("Daten", () => toggle(entry.data));
    toolbar.append(entry.open, reload, smaller, reset, larger, entry.sourceButton, entry.dataButton);
    item.append(heading, provenance, toolbar, viewport, sources);
    return entry;
  }

  function renderSimulation(current, simulation, error) {
    const research = current.scientificResearch;
    const paneKey = `${current.activeSessionId}:${research.selectedProjectId || ""}`;
    if (simulationPane?.key !== paneKey) {
      const root = section("Simulation", "Simulationen und Python-Analysen mit Daten und Quellcode");
      root.classList.add("science-presentation-card", "simulation-pane");
      const toolbar = node("div", "browser-panel-toolbar");
      toolbar.append(button("Aktualisieren", () => { post("research.list", { sessionId: state().activeSessionId }); if (state().scientificResearch.selectedProjectId) post("research.open", { sessionId: state().activeSessionId, projectId: state().scientificResearch.selectedProjectId }); }));
      const picker = node("select", "science-project-picker"); picker.setAttribute("aria-label", "Forschungsvorhaben auswählen");
      picker.addEventListener("change", () => post("research.open", { sessionId: state().activeSessionId, projectId: picker.value })); toolbar.append(picker);
      const notice = node("p", "browser-panel-error"), detail = node("p", "browser-muted");
      const artifacts = node("div", "simulation-artifacts"), empty = node("p", "browser-empty-state");
      root.append(toolbar, notice, detail, artifacts, empty);
      simulationPane = { key: paneKey, root, picker, notice, detail, artifacts, empty, cards: new Map() };
    }
    const pane = simulationPane;
    if (pane.root.parentNode !== panel) panel.replaceChildren(pane.root);
    pane.picker.replaceChildren();
    for (const project of research.projects || []) { const option = node("option", "", project.interpretedQuestion || project.originalQuestion || project.id); option.value = project.id; pane.picker.append(option); }
    pane.picker.value = research.selectedProjectId || ""; pane.picker.hidden = (research.projects || []).length <= 1;
    pane.notice.textContent = error || (simulation?.status === "failed" ? simulation.detail : ""); pane.notice.hidden = !pane.notice.textContent;
    pane.detail.textContent = simulation?.detail || ""; pane.detail.hidden = !pane.detail.textContent;
    const artifacts = simulation?.artifacts || [], available = new Set();
    for (const artifact of artifacts) {
      const key = String(artifact.id || artifact.url || artifact.previewUrl), resource = url(artifact.url || artifact.previewUrl, true);
      const interactive = /html|interactive/i.test(`${artifact.kind} ${artifact.contentType} ${artifact.fileName}`);
      available.add(key);
      let entry = pane.cards.get(key);
      if (!entry || entry.interactive !== interactive) {
        entry?.item.remove(); entry = createSimulationCard(key, artifact, resource, interactive); pane.cards.set(key, entry); pane.artifacts.append(entry.item);
      }
      // Status/revision polling keeps the real frame, animation and disclosure
      // state. Only changed content or an explicit reload replaces its document.
      if (entry.resource !== resource || entry.hash !== artifact.sha256) { if (resource) entry.media.src = resource; entry.resource = resource; entry.hash = artifact.sha256; }
      if (resource) {
        if (interactive) entry.open.href = resource;
        else {
          const download = new URL(resource); download.searchParams.set("download", "1"); entry.open.href = download.href;
          const extension = /jpe?g/i.test(artifact.contentType || "") ? ".jpg" : ".png";
          const filename = String(artifact.fileName || `${artifact.title || "Simulation"}${extension}`).replace(/[<>:"/\\|?*\u0000-\u001f]/g, "-");
          entry.open.download = filename;
        }
      } else entry.open.removeAttribute("href");
      entry.open.setAttribute("aria-label", interactive ? "HTML-Simulation im Browser öffnen" : "Abbildung herunterladen und anschließend lokal öffnen");
      entry.heading.textContent = artifact.title || artifact.fileName || "Simulation";
      if (interactive) entry.media.title = entry.heading.textContent; else entry.media.alt = entry.heading.textContent;
      entry.provenance.textContent = typeof artifact.provenance === "string" ? artifact.provenance : artifact.provenance ? JSON.stringify(artifact.provenance) : "";
      entry.provenance.hidden = !entry.provenance.textContent;
      const sourceUrl = interactive ? resource : url(artifact.sourceUrl || artifact.scriptUrl, true), dataUrl = url(artifact.dataUrl, true);
      const sourcesKey = JSON.stringify([sourceUrl, dataUrl, artifact.sha256]);
      if (entry.sourcesKey !== sourcesKey) {
        entry.sources.replaceChildren();
        entry.source = simulationDisclosure(interactive ? "HTML-/JavaScript-Code" : "Python-Code", sourceUrl);
        entry.data = simulationDisclosure("Daten", dataUrl);
        if (entry.source) entry.sources.append(entry.source); if (entry.data) entry.sources.append(entry.data);
        entry.sourcesKey = sourcesKey;
      }
      entry.sourceButton.disabled = !entry.source; entry.dataButton.disabled = !entry.data;
    }
    for (const [key, entry] of pane.cards) entry.available = available.has(key);
    for (const entry of pane.cards.values()) entry.item.hidden = !entry.available;
    pane.empty.hidden = artifacts.length > 0;
    pane.empty.textContent = simulation?.status === "running" ? "Simulation läuft …" : "Ergebnisse, Plots, Daten und Quellcode erscheinen nach einer Analyse hier.";
  }
  function renderSubagent() {
    const child = children.get(childId); if (!child) { setView("chat"); return; }
    const heading = section(child.name || child.title || "Subagent", `${child.status || "Bereit"} · ${child.model || child.modelId || ""}`); panel.append(heading);
    const messages = node("div", "subagent-messages");
    for (const message of child.messages || []) {
      const article = node("article", `message ${message.role === "user" ? "user" : "assistant"}`); article.dataset.messageId = String(message.id);
      const content = node("div", "message-content markdown-body");
      if (globalThis.missumMarkdown?.render) content.append(globalThis.missumMarkdown.render(message.content || message.text || ""));
      if (!content.childNodes.length) content.textContent = message.content || message.text || "";
      article.append(content); messages.append(article);
    }
    panel.append(messages);
    if (!(child.messages || []).length) panel.append(node("p", "browser-empty-state", child.isRunning ? "Subagent arbeitet …" : "Noch keine Nachrichten vorhanden."));
  }
  function renderReview() {
    const summary = state().changesSummary; const card = section("Änderungen auf dem PC", summary?.notice);
    if (summary?.isPartial) card.append(node("p", "browser-panel-error", "Die Änderungsübersicht ist unvollständig."));
    for (const file of summary?.files || []) {
      const counts = changeCounts({ files: [file] }), label = file.isBinary ? "Binärdatei" : counts.added === null || counts.removed === null ? "Zeilenzahlen nicht verfügbar" : `+${formattedCount(counts.added)} −${formattedCount(counts.removed)}`;
      const item = node("details", "review-file"); item.append(node("summary", "", `${file.path}  ${label}`));
      const pre = node("pre", "review-diff");
      for (const line of String(file.diff || "Kein Text-Diff verfügbar.").split("\n")) pre.append(node("span", /^(?:\+\+\+|---|@@|diff |index )/.test(line) ? "review-diff-header" : line.startsWith("+") ? "review-added" : line.startsWith("-") ? "review-removed" : "", `${line}\n`));
      if (file.diffTruncated) item.append(node("p", "browser-panel-error", "Dieser Diff ist gekürzt."));
      item.append(pre, button("Diff kopieren", () => post("message.copy", { text: file.diff || "" }))); card.append(item);
    }
    if (!(summary?.files || []).length) card.append(node("p", "browser-muted", "Noch keine Dateiänderungen erfasst.")); panel.append(card);
  }
  function renderSubagentOverview() {
    const body = node("section", "browser-subagent-overview"); body.append(node("h1", "", "Subagenten"));
    const list = sessionChildren();
    for(const child of list) {
      const status = childStatus(child), entry = button("", () => setView("subagent",child.agentId || child.runId), "subagent-overview-entry");
      entry.append(planet(child,16)); const labels=node("span","subagent-overview-labels"); labels.append(node("span","subagent-overview-title",childTitle(child)),node("span","subagent-overview-status",status)); entry.append(labels);
      entry.setAttribute("aria-label",`${childTitle(child)} · ${status}`); entry.title=`${childTitle(child)}\n${status}`; body.append(entry);
    }
    if(!list.length)body.append(node("p","browser-muted","Noch keine Subagenten in dieser Sitzung")); panel.append(body);
  }
  function renderSources() {
    const current = state(), agents = [...children.values()].filter(item => item.sessionId === current.activeSessionId);
    const sources = sourceActions(current, [...current.messages, ...agents.flatMap(item => item.messages || [])]);
    const body = node("section", "browser-sources"); body.append(node("h1", "", "Quellen"));
    for (const action of sources) {
      const group = node("section", "browser-source-group"); group.append(node("h2", "", action.title));
      if (action.attachment) group.append(sourceRow(action, true)); else for (const source of action.sources) group.append(sourceRow({ ...action, sources: [source] }));
      body.append(group);
    }
    if (!sources.length) body.append(node("p", "browser-muted", "Noch keine Quellen")); panel.append(body);
  }
  function renderPanel() {
    const research = state().scientificResearch;
    // Progress metadata must not recreate a PDF frame and reset the reader's page.
    const displayed = view === "publication" ? [presentation?.publication, presentation?.publicationError]
      : view === "simulation" ? [presentation?.simulation, presentation?.simulationError] : null;
    const signature = JSON.stringify([view, childId, displayed, research?.selectedProjectId,
      (research?.projects || []).map(project => [project.id, project.interpretedQuestion || project.originalQuestion]),
      view === "review" && state().changesSummary, view === "review" && document.documentElement?.lang, view === "subagent" && children.get(childId),
      view === "subagents" && sessionChildren().map(child => [child.agentId, childTitle(child),child.status,child.planetIndex,childCreated(child),child._observationOrder]),
      view === "sources" && sourceActions(state(), [...state().messages, ...[...children.values()].filter(item => item.sessionId === state().activeSessionId).flatMap(item => item.messages || [])])]);
    if (signature === panelSignature) return; panelSignature = signature;
    if (view === "simulation") { renderScience(); return; }
    panel.replaceChildren();
    if (view === "publication" || view === "simulation") renderScience(); else if (view === "subagent") renderSubagent(); else if(view === "subagents")renderSubagentOverview(); else if (view === "review") renderReview(); else if (view === "sources") renderSources();
  }
  function render() {
    const current = state();
    if (ownerSession !== current.activeSessionId) { ownerSession = current.activeSessionId; if (view !== "settings") view = "chat"; childId = null; presentation = null; panelSignature = ""; }
    byId("conversation-pane").hidden = view !== "chat";
    byId("science-workbench").hidden = true;
    panel.hidden = !["publication", "simulation", "subagent", "subagents", "review", "sources"].includes(view);
    document.querySelector(".chat-pane").dataset.view = view;
    const picker = byId("model-picker");
    const selected = current.selectedModelId || current.model;
    if (byId("local-model-overlay").hidden && selected && [...picker.options].some(option => option.value === selected)) picker.value = selected;
    if (view === "subagent") picker.disabled = true;
    else picker.disabled = picker.options.length === 0 || (picker.options.length === 1 && !picker.options[0].value);
    renderTabs(); if (!panel.hidden) renderPanel(); renderNavigation(); renderInspector();
  }
  function scheduleRender() { if (renderTimer) return; renderTimer = setTimeout(() => { renderTimer = null; render(); }, globalThis.missumRenderTiming?.native?.renderMilliseconds || 80); }
  function models(snapshot) {
    const draftModel = !byId("local-model-overlay").hidden ? byId("model-picker").value : null;
    const picker = byId("model-picker"); const list = globalThis.missumModelCatalog(snapshot, state().chatMode === "coding" ? "coding" : "general"); const selected = snapshot.selectedModelId || snapshot.selectedModel || snapshot.modelId || state().selectedModelId || state().model || "";
    picker.replaceChildren();
    for (const model of list) { const option = node("option", "", model.displayName || model.name || model.id || model.modelId); option.value = model.id || model.modelId; option._missumModel = model; picker.append(option); }
    if (!list.length) { const option = node("option", "", selected || "Modell auswählen"); option.value = selected; picker.append(option); }
    picker.value = draftModel && [...picker.options].some(option => option.value === draftModel) ? draftModel : selected; picker.disabled = list.length === 0;
    globalThis.dispatchEvent(new CustomEvent("missum:models-updated"));
  }
  globalThis.addEventListener("missum:host-message", event => {
    const { type, payload } = event.detail;
    if (["state.snapshot", "session.changed", "document.changed"].includes(type)) {
      if (ownerSession !== state().activeSessionId) {
        ownerSession = state().activeSessionId; if (view !== "settings") view = "chat"; presentation = null; children.clear();
        try {
          const restored = JSON.parse(sessionStorage.getItem(`assistant.view:${globalThis.missumBridge.clientId}:${ownerSession}`) || "null");
          if (view !== "settings" && restored && ["chat", "publication", "simulation", "review", "subagent", "subagents", "sources"].includes(restored.view)) {
            view = restored.view; childId = restored.childId;
            if (view === "sources") sourceTabs.add(ownerSession);
            if (view === "subagents") overviewTabs.add(ownerSession);
            if (view === "review") reviewTabs.add(ownerSession);
            if (view === "subagent" && childId) {tabState(ownerSession).open.add(childId); tabState(ownerSession).closed.delete(childId); saveTabs(ownerSession);}
          }
        } catch { /* Ignore invalid saved view. */ }
      }
      if (Array.isArray(payload.subagents)) syncChildren(payload.subagents);
      if (payload.sciencePresentation) {
        const incoming = payload.sciencePresentation;
        const selected = state().scientificResearch?.selectedProjectId || state().scientificResearch?.detail?.project?.id;
        if (incoming.projectId) presentations.set(incoming.projectId, incoming);
        if (incoming.projectId && incoming.projectId === selected) presentation = incoming;
      }
      if (payload.models) models(payload.models);
    } else if (type === "subagent.snapshot") {
      observeChild(payload.subagent || payload);
    }
    else if (type === "subagents.snapshot") syncChildren(payload.subagents || []);
    else if (type === "science.presentation" && (!payload.sessionId || payload.sessionId === state().activeSessionId)) {
      const incoming = payload.presentation || payload;
      const projectId = incoming.projectId || payload.projectId;
      const selected = state().scientificResearch?.selectedProjectId || state().scientificResearch?.detail?.project?.id;
      const previous = presentations.get(projectId);
      if (projectId && (!previous || !incoming.revision || Number(incoming.revision) >= Number(previous.revision || 0))) {
        presentations.set(projectId, incoming.status === "preparing" && previous?.publication ? { ...previous, status: incoming.status } : incoming);
      }
      if (projectId && projectId === selected) presentation = presentations.get(projectId);
    }
    else if (type === "research.snapshot") {
      const selected = state().scientificResearch?.selectedProjectId || state().scientificResearch?.detail?.project?.id;
      presentation = presentations.get(selected) || null;
    }
    else if (type === "models.snapshot") models(payload);
    else if (type === "download.ready") {
      const anchor = node("a"); anchor.href = url(payload.url, true); anchor.download = payload.fileName || "Missum.pdf"; document.body.append(anchor); anchor.click(); anchor.remove();
    }
    scheduleRender();
  });
  byId("model-picker")?.addEventListener("change", event => { if (byId("local-model-overlay").hidden) post("models.select", { modelId: event.target.value }); });
  byId("tabbar-sidebar-toggle")?.addEventListener("click", () => globalThis.missumApp.toggleSidebar());
  byId("inspector-toggle")?.addEventListener("click", toggleInspector);
  let readMenu = null;
  function closeReadMenu() { readMenu?.remove(); readMenu = null; }
  if (globalThis.missumBridge.isLanBrowser) {
    document.addEventListener("contextmenu", event => {
      closeReadMenu(); const target = globalThis.missumGetReadFromContextTarget?.();
      const article = event.target.closest("article.message");
      const message = (state().messages || []).find(item => String(item.id) === article?.dataset.messageId);
      if (!target && !message) return;
      event.preventDefault();
      readMenu = node("div", "browser-read-menu"); readMenu.setAttribute("role", "menu");
      const action = (label, fn) => { const item = button(label, () => { fn(); closeReadMenu(); }); item.setAttribute("role", "menuitem"); readMenu.append(item); return item; };
      if (target) action("Ab hier vorlesen", () => post("microphone.speak", { ...target, startAnchor: { kind: target.kind, blockIndex: target.blockIndex } }));
      if (message) {
        action("Nachricht kopieren", () => post("message.copy", { text: message.content || "" }));
        if (String(message.content || "").trim() && !["pending", "streaming"].includes(String(message.status))) action("Nachricht vorlesen", () => post("microphone.speak", { sessionId: state().activeSessionId, messageId: message.id, text: message.content }));
        action("Nachricht als PDF exportieren", () => post("message.exportPdf", { sessionId: state().activeSessionId, messageId: message.id }));
      }
      const speechSource = message && globalThis.missumApp?.isMessageSpeechActive?.(message.id, message.sessionId || state().activeSessionId);
      if (speechSource) {
        action(state().microphone.isSpeechPaused ? "Vorlesen fortsetzen" : "Vorlesen pausieren", () => post("microphone.toggleSpeechPause", {})).disabled = !state().microphone.canPauseSpeech;
        action("Vorlesen beenden", () => post("microphone.stopSpeech", {}));
      }
      readMenu.style.left = `${Math.min(event.clientX, window.innerWidth - 225)}px`; readMenu.style.top = `${Math.min(event.clientY, window.innerHeight - 220)}px`; document.body.append(readMenu); readMenu.querySelector("button")?.focus();
    });
    document.addEventListener("click", closeReadMenu);
    document.addEventListener("keydown", event => { if (event.key === "Escape") closeReadMenu(); });
  }
  byId("browser-host-label").textContent = globalThis.missumBridge.isLanBrowser ? `Server: ${location.hostname} · Werkzeuge laufen auf dem PC` : "Werkzeuge laufen auf diesem PC";
  globalThis.missumPanels = Object.freeze({ setView, render, collectSources, sourceActions, changeCounts, createSubagentReceipt });
  globalThis.addEventListener("hashchange", () => { if (location.hash === "#settings") globalThis.missumSettings?.open(); else if (view === "settings") globalThis.missumSettings?.close(); });
  post("models.list", {});
  render();
})();
