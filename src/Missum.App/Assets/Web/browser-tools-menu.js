(function (global) {
  "use strict";

  const controllers = new WeakMap();
  const ids = Object.freeze({
    attach: "builtin.workspace/attach-files-and-folders",
    plan: "builtin.coding/plan-mode",
    document: "builtin.documents/create",
    research: "builtin.web/deep-research",
    pdf: "builtin.documents/export-chat-pdf",
    captions: "builtin.speech/live-captions"
  });
  const colors = Object.freeze({
    attachment: "#5b9cf6", web: "#4c94f2", research: "#a07cf6",
    image: "#e768ab", video: "#e768ab", audio: "#46bc85",
    speech: "#33b8cf", captions: "#33b8cf", translate: "#33b8cf",
    document: "#378fea", pdf: "#da5052", plan: "#f19d38",
    audiobook: "#f19d38", folder: "#f19d38", code: "#9774e1",
    extension: "#9774e1"
  });
  const paths = Object.freeze({
    attachment: ["M8 12.5 15.3 5.2a3 3 0 0 1 4.2 4.2l-9.2 9.2a5 5 0 0 1-7.1-7.1L12.4 2.3", "m6 14.5 8-8a1 1 0 0 1 1.4 1.4l-8 8"],
    folder: ["M3 6h6l2 2h10v11H3Z", "M3 6V4h6l2 2"],
    web: ["M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20", "M2 12h20", "M12 2c6 5 6 15 0 20-6-5-6-15 0-20"],
    research: ["M10.5 3a7.5 7.5 0 1 0 0 15 7.5 7.5 0 0 0 0-15", "m16 16 5 5"],
    image: ["M3 3h18v18H3Z", "m3 17 6-6 4 4 3-3 5 5", "M16 6h.01"],
    video: ["M3 5h13v14H3Z", "m16 10 5-3v10l-5-3"],
    audio: ["M12 3v11", "M12 3h8v4h-8", "M8 14a4 4 0 1 0 4 4v-4Z"],
    speech: ["M3 9h4l5-5v16l-5-5H3Z", "M16 8a6 6 0 0 1 0 8", "M19 5a10 10 0 0 1 0 14"],
    captions: ["M3 4h18v13H9l-5 4v-4H3Z", "M6 8h5m3 0h4M6 12h12"],
    translate: ["M3 5h12M9 2v3M5 5c1 6 6 9 10 10M13 5c-1 6-6 9-10 10", "m14 21 4-10 4 10m-6-4h4"],
    document: ["M5 2h9l5 5v15H5Z", "M14 2v6h5M8 12h8M8 16h8"],
    pdf: ["M5 2h9l5 5v15H5Z", "M14 2v6h5M8 12v6m0-6h2v3H8m5-3v6h2m-2-6h2v6m3-6h2m-2 0v6m0-3h2"],
    plan: ["m3 5 2 2 3-4m-5 9 2 2 3-4m-5 9 2 2 3-4", "M11 5h10M11 12h10M11 19h10"],
    audiobook: ["M3 4h8l1 2 1-2h8v16h-8l-1 1-1-1H3Z", "M12 6v15M6 8h3m6 0h3M6 12h3m6 0h3"],
    code: ["m8 5-6 7 6 7m8-14 6 7-6 7m-2-16-4 18"],
    extension: ["M9 3h6l1 4 4 1v8l-4 1-1 4H9l-1-4-4-1V8l4-1Z", "M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6"],
    back: ["m14 5-7 7 7 7"]
  });
  function modeName(value) {
    const name = String(value || "general").toLowerCase();
    return name === "codex" ? "coding" : name === "science" ? "claudescience" : name;
  }
  function selectable(descriptor) {
    return Boolean(descriptor.toolAction || descriptor.toolToggle
      || String(descriptor.selectionBehavior).toLowerCase() === "toggle"
      || /^(selectabletool|1)$/i.test(String(descriptor.actionKind)));
  }
  function iconKey(descriptor) {
    const key = String(descriptor.iconKey || "extension").toLowerCase();
    return String(descriptor.actionId || "").includes("audiobook") ? "audiobook" : key;
  }
  // Match NativeAssistantPage.ShowComposerTools: special rows first, extension
  // tools, built-in tools, then supported immediate actions. No group headings.
  function buildEntries(descriptors, mode) {
    const currentMode = modeName(mode);
    const seen = new Set();
    const available = (Array.isArray(descriptors) ? descriptors : []).filter(item => {
      if (!item?.actionId || !item.displayName || seen.has(item.actionId)) return false;
      seen.add(item.actionId);
      return !item.supportedChatModes?.length || item.supportedChatModes.some(value => modeName(value) === currentMode);
    });
    const tools = available.filter(selectable);
    const entries = [{ kind: "files", label: "Dateien und Ordner", description: "", icon: "attachment" }];
    if (currentMode === "claudescience") entries.push({
      kind: "publication", label: "Publikation öffnen", description: "Vorhaben, Quellen und Ergebnisse", icon: "research"
    });
    const plan = tools.find(item => iconKey(item) === "plan");
    if (plan) entries.push({ kind: "action", descriptor: plan, label: "Planmodus", description: "Planmodus einschalten", icon: "plan" });
    const document = tools.find(item => iconKey(item) === "document");
    if (document) entries.push({ kind: "action", descriptor: document, label: "Dokumente erstellen", description: "Word, PDF, Tabellen und Präsentationen", icon: "document" });
    const normal = tools.filter(item => !["plan", "document"].includes(iconKey(item)) && item.actionId !== ids.research);
    for (const item of normal.filter(item => !item.actionId.startsWith("builtin."))) add(item);
    for (const item of normal.filter(item => item.actionId.startsWith("builtin."))) add(item);
    for (const item of available.filter(item => !selectable(item) && item.actionId !== ids.attach
      && (!item.actionId.startsWith("builtin.") || [ids.pdf, ids.captions].includes(item.actionId)))) add(item);
    return entries;
    function add(descriptor) {
      entries.push({ kind: "action", descriptor, label: descriptor.displayName, description: descriptor.description || "", icon: iconKey(descriptor) });
    }
  }
  function browserCaptureAlternative(descriptor, isLan) {
    if (!isLan || !descriptor) return null;
    const identity = `${descriptor.actionId || ""} ${descriptor.captureAction || ""}`;
    if (/live.?caption|captions/i.test(identity)) return "Audio-/Videodatei anhängen; Live-Aufnahme über HTTP nicht verfügbar.";
    if (/screen[.-]?(capture|record)|screenshare|desktop[.-]?capture/i.test(identity)) return "Screenshot oder Video anhängen; Bildschirmaufnahme über HTTP nicht verfügbar.";
    if (/microphone|speech[.-]?listen|voice[.-]?(capture|control)|audio[.-]?record/i.test(identity)) return "Audiodatei anhängen; Mikrofonaufnahme über HTTP nicht verfügbar.";
    return null;
  }
  function node(tag, className, text) {
    const result = document.createElement(tag);
    if (className) result.className = className;
    if (text !== undefined) result.textContent = text;
    return result;
  }
  function icon(key) {
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("viewBox", "0 0 24 24");
    svg.setAttribute("fill", "none");
    svg.setAttribute("stroke", "currentColor");
    svg.setAttribute("stroke-width", "1.65");
    svg.setAttribute("stroke-linecap", "round");
    svg.setAttribute("stroke-linejoin", "round");
    svg.setAttribute("aria-hidden", "true");
    svg.setAttribute("class", "native-tools-icon");
    svg.style.color = colors[key] || colors.extension;
    for (const value of paths[key] || paths.extension) {
      const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
      path.setAttribute("d", value); svg.append(path);
    }
    return svg;
  }
  function createController(options) {
    const controller = { options, view: "main", buttons: [], rows: [], scope: "", heading: null };
    options.menu.classList.add("native-tools-menu");
    options.content.classList.add("native-tools-content");
    controller.heading = options.menu.querySelector(".tools-menu__title");
    if (!controller.heading) {
      controller.heading = node("div", "tools-menu__title");
      options.menu.insertBefore(controller.heading, options.content);
    }
    options.menu.addEventListener("keydown", event => handleKey(controller, event), true);
    const reposition = () => { if (!controller.options.menu.hidden) position(controller); };
    global.addEventListener?.("resize", reposition);
    global.addEventListener?.("scroll", reposition, true);
    return controller;
  }
  function close(controller, restoreFocus = false) {
    const { menu, trigger } = controller.options;
    menu.hidden = true;
    trigger?.setAttribute("aria-expanded", "false");
    if (restoreFocus) trigger?.focus();
    if (controller.view !== "main") { controller.view = "main"; renderView(controller); }
  }
  function position(controller) {
    const { menu, composer } = controller.options;
    if (!composer?.getBoundingClientRect) return;
    const bounds = composer.getBoundingClientRect();
    const viewportWidth = Number(global.innerWidth) || document.documentElement?.clientWidth || 1024;
    const viewportHeight = Number(global.innerHeight) || document.documentElement?.clientHeight || 768;
    const composerWidth = bounds.width || bounds.right - bounds.left;
    const width = Math.min(Math.max(280, controller.view === "files" ? Math.min(360, composerWidth) : composerWidth), viewportWidth - 16);
    const left = Math.min(Math.max(8, bounds.left), Math.max(8, viewportWidth - width - 8));
    const maxHeight = Math.max(80, Math.min(320, bounds.top - 12));
    menu.style.width = `${width}px`;
    menu.style.left = `${left}px`;
    menu.style.bottom = `${Math.max(8, viewportHeight - bounds.top + 4)}px`;
    menu.style.setProperty("--native-tools-max-height", `${maxHeight}px`);
  }
  function reason(controller, entry) {
    return browserCaptureAlternative(entry.descriptor, controller.options.isLan)
      || (typeof controller.options.disabledReason === "function"
        ? controller.options.disabledReason(entry.descriptor)
        : entry.descriptor?.disabledReason) || "";
  }
  function refresh(controller) {
    for (const { button, entry, detail, status } of controller.rows) {
      if (!entry.descriptor) continue;
      const disabledReason = reason(controller, entry);
      const alternative = browserCaptureAlternative(entry.descriptor, controller.options.isLan);
      const active = Boolean(controller.options.isActive?.(entry.descriptor));
      button.disabled = Boolean(disabledReason);
      button.title = disabledReason || entry.description || entry.label;
      button.setAttribute("aria-disabled", String(button.disabled));
      button.classList.toggle("active", active);
      if (selectable(entry.descriptor)) button.setAttribute("aria-checked", String(active));
      detail.textContent = alternative || entry.description;
      status.hidden = !active;
    }
  }
  function renderView(controller) {
    const { options } = controller;
    const wasFocused = controller.buttons.find(button => button === document.activeElement)?.dataset.nativeToolsKey;
    options.content.replaceChildren();
    controller.buttons = []; controller.rows = [];
    options.menu.dataset.toolsView = controller.view;
    options.menu.setAttribute("aria-label", controller.view === "files" ? "Dateien und Ordner" : "Hinzufügen");
    controller.heading.replaceChildren();
    if (controller.view === "files") {
      const back = node("button", "native-tools-back");
      back.type = "button"; back.setAttribute("aria-label", "Zurück zu Hinzufügen");
      back.append(icon("back"));
      back.addEventListener("click", () => showMain(controller, true));
      controller.heading.append(back, node("span", "", "Dateien und Ordner"));
    } else controller.heading.textContent = "Hinzufügen";
    const entries = controller.view === "files" ? [
      { kind: "upload", label: "Dateien hinzufügen", description: "", icon: "attachment" },
      { kind: "workspace", label: "Projektordner auswählen", description: "", icon: "folder" }
    ] : buildEntries(options.descriptors, options.mode);
    for (const entry of entries) {
      const button = node("button", "service-option native-tools-row");
      button.type = "button";
      button.tabIndex = -1;
      button.dataset.nativeToolsKey = entry.descriptor?.actionId || entry.kind;
      button.setAttribute("role", entry.descriptor && selectable(entry.descriptor) ? "menuitemcheckbox" : "menuitem");
      button.setAttribute("aria-label", entry.label);
      if (entry.kind === "files") button.setAttribute("aria-haspopup", "menu");
      if (entry.descriptor) {
        button._actionDescriptor = entry.descriptor;
        button.dataset.actionId = entry.descriptor.actionId;
        if (entry.descriptor.toolAction) button.dataset.toolAction = entry.descriptor.toolAction;
        if (entry.descriptor.toolToggle) button.dataset.toolToggle = entry.descriptor.toolToggle;
      }
      const label = node("span", "native-tools-label", entry.label);
      const detail = node("span", "native-tools-description", entry.description);
      const status = node("span", "action-menu__status native-tools-status", "✓"); status.hidden = true;
      button.append(icon(entry.icon), label, detail, status);
      button.title = entry.description || entry.label;
      button.addEventListener("click", () => {
        if (button.disabled || (entry.descriptor && reason(controller, entry))) return;
        if (entry.kind === "files") {
          controller.view = "files"; renderView(controller); position(controller); controller.buttons[0]?.focus();
          return;
        }
        close(controller);
        if (entry.kind === "upload") options.openFiles?.();
        else if (entry.kind === "workspace") options.openWorkspace?.();
        else if (entry.kind === "publication") options.openPublication?.();
        else options.invoke?.(entry.descriptor);
      });
      controller.buttons.push(button);
      controller.rows.push({ button, entry, detail, status });
      options.content.append(button);
    }
    refresh(controller);
    if (!options.menu.hidden && wasFocused) controller.buttons.find(button => button.dataset.nativeToolsKey === wasFocused)?.focus();
    position(controller);
  }
  function showMain(controller, restoreFocus) {
    controller.view = "main"; renderView(controller); position(controller);
    if (restoreFocus) controller.buttons[0]?.focus();
  }
  function handleKey(controller, event) {
    if (controller.options.menu.hidden) return;
    if (event.key === "Tab") { close(controller); return; }
    const enabled = controller.buttons.filter(button => !button.disabled);
    const index = enabled.indexOf(document.activeElement);
    let target = null;
    if (event.key === "ArrowDown") target = enabled[(index + 1) % enabled.length];
    else if (event.key === "ArrowUp") target = enabled[(index - 1 + enabled.length) % enabled.length];
    else if (event.key === "Home") target = enabled[0];
    else if (event.key === "End") target = enabled.at(-1);
    else if ((event.key === "ArrowLeft" || event.key === "Escape") && controller.view === "files") showMain(controller, true);
    else if (event.key === "Escape") close(controller, true);
    else if (event.key === "ArrowRight" && document.activeElement?.dataset.nativeToolsKey === "files") {
      controller.view = "files"; renderView(controller); position(controller); controller.buttons[0]?.focus();
    } else return;
    event.preventDefault(); event.stopPropagation(); event.stopImmediatePropagation?.();
    target?.focus();
  }
  function render(options) {
    if (!options?.menu || !options.content) return false;
    let controller = controllers.get(options.menu);
    if (!controller) { controller = createController(options); controllers.set(options.menu, controller); }
    controller.options = options;
    const scope = `${modeName(options.mode)}:${options.sessionId || ""}`;
    if (controller.scope && controller.scope !== scope) close(controller);
    controller.scope = scope;
    renderView(controller);
    return true;
  }
  function setOpen(menu, open) {
    const controller = controllers.get(menu);
    if (!controller) return false;
    if (!open) close(controller);
    else {
      menu.hidden = false;
      controller.options.trigger?.setAttribute("aria-expanded", "true");
      refresh(controller); position(controller);
    }
    return true;
  }
  function update(menu) {
    const controller = controllers.get(menu);
    if (!controller) return false;
    refresh(controller); position(controller);
    return true;
  }
  global.missumToolsMenu = Object.freeze({ render, setOpen, update, buildEntries });
})(globalThis);
