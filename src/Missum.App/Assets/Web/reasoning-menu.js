(function () {
  "use strict";
  const names = { none: "Ohne", on: "An", minimal: "Minimal", low: "Niedrig", medium: "Mittel", high: "Hoch", xhigh: "Sehr hoch", max: "Maximum", ultra: "Ultra" };
  const normalized = value => typeof value === "string" ? value.trim().toLowerCase() : "";
  function options(profile) {
    const order = ["none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra", "on"];
    return [...new Set((Array.isArray(profile?.levels) ? profile.levels : []).map(normalized)
      .filter(value => /^[a-z][a-z0-9_-]{0,31}$/.test(value) && value !== "auto"))]
      .sort((a, b) => (order.includes(a) ? order.indexOf(a) : 100) - (order.includes(b) ? order.indexOf(b) : 100))
      .map(value => ({ value, label: names[value] || value }));
  }
  function selection(profile, choices = options(profile)) {
    const supported = value => choices.some(option => option.value === value);
    const preferred = normalized(profile?.selected), modelDefault = normalized(profile?.defaultLevel);
    return supported(preferred) ? preferred : supported(modelDefault) ? modelDefault : choices[0]?.value || null;
  }
  function modelCatalog(snapshot, preferredRole = "general") {
    const list = Array.isArray(snapshot) ? snapshot : snapshot?.models || snapshot?.items || [];
    const unique = new Map();
    for (const item of list) {
      const id = item.id || item.modelId;
      if (!id || item.downloaded === false || item.role && !["general", "coding"].includes(item.role)) continue;
      const prior = unique.get(id);
      if (!prior || item.role === preferredRole && prior.role !== preferredRole) unique.set(id, item);
    }
    return [...unique.values()];
  }
  if (typeof module === "object") module.exports = { options, selection, modelCatalog };
  if (typeof document === "undefined") return;
  globalThis.missumModelCatalog = modelCatalog;
  const byId = id => document.getElementById(id), button = byId("reasoning-button");
  if (!button) return;
  const menu = byId("reasoning-menu"), list = byId("reasoning-options"), detail = byId("reasoning-detail");
  const range = byId("reasoning-range"), picker = byId("model-picker"), overlay = byId("local-model-overlay"), effort = byId("local-model-effort");
  let modelId = "", role = "general", profile = null, pending = null, running = false, draftEffort = null, dialogProfile = null, pendingModelEffort = null, pendingModelRequest = null;
  let pendingKind = null, pendingExpected = null, pendingModelTarget = null, submissionError = null;
  let activeSessionId = null;
  const submissionWaiters = new Set();
  function models() { return [...(picker?.options || picker?.children || [])].filter(item => item.value); }
  function modelName(id) { return models().find(item => item.value === id)?.textContent || String(id || "Modell auswählen").split("/").at(-1).split("~")[0]; }
  function post(type, payload) { return globalThis.missumBridge.post(type, payload); }
  function rejectSubmission(error) {
    submissionError = error instanceof Error ? error : new Error(String(error));
    for (const waiter of submissionWaiters) { globalThis.clearTimeout(waiter.timer); waiter.reject(submissionError); }
    submissionWaiters.clear();
  }
  function finishSubmissions() {
    if (pending || pendingModelRequest || pendingModelEffort) return;
    if (submissionError) { rejectSubmission(submissionError); return; }
    for (const waiter of submissionWaiters) {
      globalThis.clearTimeout(waiter.timer);
      waiter.resolve({ modelId, role, effort: selection(profile) });
    }
    submissionWaiters.clear();
  }
  function beginReasoning(type, payload) {
    pendingKind = type; pendingExpected = type === "reasoning.set" ? payload : null;
    if (type === "reasoning.set") submissionError = null;
    try {
      pending = post(type, payload);
      if (!pending) throw new Error("Die Reasoning-Auswahl konnte nicht an den Server übertragen werden.");
    } catch (error) { pending = null; pendingKind = null; pendingExpected = null; rejectSubmission(error); throw error; }
  }
  function close(focus = false, commit = true) {
    menu.hidden = true; button.setAttribute("aria-expanded", "false");
    const desired = draftEffort; draftEffort = null;
    if (commit && desired && desired !== selection(profile) && !running && !pending) { beginReasoning("reasoning.set", { modelId, role, effort: desired }); render(); }
    if (focus) button.focus();
  }
  function choose(value) { if (!running && !pending && profile?.available) { draftEffort = value; render(); } }
  function render() {
    const choices = options(profile), saved = selection(profile, choices), selected = draftEffort || saved;
    button.disabled = running || Boolean(pending) || Boolean(pendingModelRequest) || (!modelId && !models().length) || (!models().length && (!profile?.available || !choices.length));
    if (running || !modelId) close(false, false);
    button.title = saved ? `Reasoning: ${names[saved] || saved}` : "Modell und Reasoning auswählen";
    button.setAttribute("aria-label", button.title);
    byId("selected-model-label").textContent = modelName(modelId);
    byId("selected-reasoning-label").textContent = saved ? names[saved] || saved : "";
    byId("reasoning-title").textContent = selected ? `Reasoning: ${names[selected] || selected}` : "Reasoning";
    byId("open-model-dialog").textContent = `${modelName(modelId)} ›`;
    detail.textContent = pending ? "Modellinformationen werden geladen …" : profile?.detail || "Das Modell bietet keine wählbaren Reasoning-Stufen.";
    detail.hidden = Boolean(!pending && profile?.available && choices.length);
    if (range.parentElement) range.parentElement.hidden = !choices.length;
    range.disabled = running || Boolean(pending) || !profile?.available;
    range.max = String(Math.max(0, choices.length - 1)); range.value = String(Math.max(0, choices.findIndex(item => item.value === selected)));
    range.setAttribute("aria-valuetext", names[selected] || selected || "Nicht verfügbar");
    range.style.setProperty?.("--reasoning-fill", `${choices.length > 1 ? Number(range.value) / (choices.length - 1) * 100 : 0}%`);
    list.replaceChildren();
    for (const option of choices) {
      const item = document.createElement("button"); item.type = "button"; item.className = "reasoning-option";
      item.setAttribute("role", "menuitemradio"); item.setAttribute("aria-checked", String(option.value === selected)); item.setAttribute("aria-label", option.label);
      item.title = option.label; item.textContent = option.label; item.disabled = running || Boolean(pending) || !profile?.available;
      item.addEventListener("click", () => { if (!item.disabled) choose(option.value); }); list.append(item);
    }
  }
  function refresh() { if (modelId) { beginReasoning("reasoning.get", { modelId, role }); render(); } }
  function commitForSubmission() {
    try { close(false, true); } catch (error) { return Promise.reject(error); }
    if (running && (pending || pendingModelRequest)) return Promise.reject(new Error("Ein Auftrag wurde während der Reasoning-Auswahl gestartet. Prüfe die Eingabe erneut."));
    if (!pending && !pendingModelRequest && !pendingModelEffort)
      return submissionError ? Promise.reject(submissionError) : Promise.resolve({ modelId, role, effort: selection(profile) });
    return new Promise((resolve, reject) => {
      const waiter = { resolve, reject, timer: null };
      submissionWaiters.add(waiter);
      waiter.timer = globalThis.setTimeout(() => {
        rejectSubmission(new Error("Die Serverbestätigung der Modell-/Reasoning-Auswahl fehlt. Deine Eingabe wurde noch nicht abgesendet."));
        pending = null; pendingKind = null; pendingExpected = null; pendingModelRequest = null; pendingModelEffort = null; pendingModelTarget = null;
        profile = null; close(false, false); render();
      }, 30000);
    });
  }
  globalThis.missumReasoningSelection = Object.freeze({ commitForSubmission });
  button.addEventListener("click", event => {
    event.stopPropagation(); if (button.disabled) return;
    if (!menu.hidden) return close();
    byId("tools-menu").hidden = true; byId("tools-button")?.setAttribute("aria-expanded", "false");
    menu.hidden = false; button.setAttribute("aria-expanded", "true"); draftEffort = null; refresh();
  });
  range.addEventListener("input", () => { const option = options(profile)[Number(range.value)]; if (option) choose(option.value); });
  menu.addEventListener("click", event => event.stopPropagation());
  document.addEventListener("click", () => close());
  byId("tools-button")?.addEventListener("click", () => close());
  function dialogChoices(snapshot) {
    dialogProfile = snapshot; effort.replaceChildren();
    for (const item of options(snapshot)) { const option = document.createElement("option"); option.value = item.value; option.textContent = item.label; effort.append(option); }
    effort.value = selection(snapshot) || ""; effort.disabled = !snapshot?.available || !options(snapshot).length;
    byId("local-model-detail").textContent = snapshot?.available ? "" : snapshot?.detail || "Dieses Modell hat keinen wählbaren Denkaufwand.";
  }
  function modelProfile(id) {
    if (id === modelId && profile) return profile;
    const model = models().find(item => item.value === id)?._missumModel;
    const levels = model?.reasoningEfforts || [];
    return { levels, defaultLevel: model?.defaultReasoningEffort, selected: model?.defaultReasoningEffort, available: levels.some(value => normalized(value) !== "auto") };
  }
  function hideModelDialog() { dialogProfile = null; overlay.hidden = true; if (modelId) picker.value = modelId; button.focus(); }
  byId("open-model-dialog").addEventListener("click", () => { close(); overlay.hidden = false; picker.value = modelId; dialogChoices(modelProfile(modelId)); picker.focus(); });
  picker.addEventListener("change", () => {
    if (overlay.hidden) return;
    dialogChoices(modelProfile(picker.value));
  });
  byId("cancel-local-model").addEventListener("click", hideModelDialog);
  byId("apply-local-model").addEventListener("click", () => {
    const selectedModel = picker.value, selectedEffort = effort.value; if (!selectedModel) return;
    if (dialogProfile?.available && options(dialogProfile).some(item => item.value === selectedEffort)) pendingModelEffort = { modelId: selectedModel, role, effort: selectedEffort };
    else pendingModelEffort = null;
    pendingModelTarget = { modelId: selectedModel, role }; submissionError = null;
    try {
      pendingModelRequest = post("models.select", { modelId: selectedModel });
      if (!pendingModelRequest) throw new Error("Die Modellauswahl konnte nicht an den Server übertragen werden.");
    } catch (error) {
      pendingModelRequest = null; pendingModelEffort = null; pendingModelTarget = null; rejectSubmission(error);
    }
    overlay.hidden = true; render(); button.focus();
  });
  overlay.addEventListener("click", event => { if (event.target === overlay) hideModelDialog(); });
  document.addEventListener("keydown", event => {
    if (!overlay.hidden) { if (event.key === "Escape") { event.preventDefault(); hideModelDialog(); } return; }
    if (menu.hidden) return;
    if (event.key === "Escape") { event.preventDefault(); close(true); return; }
    const items = [...list.querySelectorAll("button")].filter(item => !item.disabled); if (!items.length) return;
    const index = items.indexOf(document.activeElement);
    const next = event.key === "ArrowDown" ? (index + 1) % items.length : event.key === "ArrowUp" ? (index - 1 + items.length) % items.length : event.key === "Home" ? 0 : event.key === "End" ? items.length - 1 : -1;
    if (next >= 0 && document.activeElement !== range) { event.preventDefault(); items[next].focus(); }
  });
  globalThis.addEventListener("missum:models-updated", render);
  globalThis.addEventListener("missum:bridge-disconnected", () => { rejectSubmission(new Error("Die Verbindung wurde während der Modell-/Reasoning-Auswahl unterbrochen. Deine Eingabe wurde noch nicht abgesendet.")); pending = null; pendingKind = null; pendingExpected = null; profile = null; draftEffort = null; pendingModelEffort = null; pendingModelRequest = null; pendingModelTarget = null; overlay.hidden = true; close(false, false); render(); });
  globalThis.addEventListener("missum:host-message", event => {
    const message = event.detail, data = message.payload || {};
    if (["state.snapshot", "session.changed"].includes(message.type)) {
      activeSessionId = data.activeSessionId || null;
      running = Boolean(data.isRunning);
      const changed = modelId !== (data.reasoningModelId || "") || role !== (data.reasoningRole || "general");
      const expectedModelTransition = pendingModelTarget && data.reasoningModelId === pendingModelTarget.modelId && (data.reasoningRole || "general") === pendingModelTarget.role;
      if (pendingModelRequest && message.requestId === pendingModelRequest && !expectedModelTransition)
        rejectSubmission(new Error("Der Server hat ein anderes Modell oder eine andere Modellrolle bestätigt. Prüfe die Auswahl erneut."));
      if (changed && submissionWaiters.size && !expectedModelTransition) rejectSubmission(new Error("Modell oder Modellrolle wurden während der Übernahme geändert. Prüfe die Eingabe erneut."));
      modelId = data.reasoningModelId || ""; role = data.reasoningRole || "general";
      if (pendingModelEffort && message.requestId === pendingModelRequest && data.reasoningModelId === pendingModelEffort.modelId && role === pendingModelEffort.role) {
        const desired = pendingModelEffort; pendingModelEffort = null; pendingModelRequest = null; pendingModelTarget = null; profile = null; close(false, false); beginReasoning("reasoning.set", desired);
      } else {
        if (message.requestId === pendingModelRequest) { pendingModelRequest = null; pendingModelEffort = null; pendingModelTarget = null; }
        if (changed) { profile = null; pending = null; pendingKind = null; pendingExpected = null; close(false, false); refresh(); }
        else if (!profile?.available && !pending && !running) refresh();
      }
      if (running) close(false, false); render();
    } else if (message.type === "reasoning.snapshot" && data.modelId === modelId && data.role === role
      && (message.requestId === pending || !message.requestId)) {
      profile = data;
      const ownReply = Boolean(pending && message.requestId === pending);
      if (ownReply) {
        if (pendingKind === "reasoning.set" && normalized(data.selected) !== normalized(pendingExpected?.effort))
          rejectSubmission(new Error("Der Server hat eine andere Reasoning-Auswahl bestätigt. Prüfe die Auswahl erneut."));
        else submissionError = null;
        pending = null; pendingKind = null; pendingExpected = null;
      }
      render(); if (ownReply && !menu.hidden) range.focus();
    } else if (message.type === "host.error" && pendingModelRequest && message.requestId === pendingModelRequest) {
      rejectSubmission(new Error(data.message || "Die Modellauswahl konnte nicht übernommen werden."));
      pendingModelEffort = null; pendingModelRequest = null; pendingModelTarget = null; render();
    } else if (message.type === "host.error" && pending && message.requestId === pending) {
      rejectSubmission(new Error(data.message || "Die Reasoning-Auswahl konnte nicht bestätigt werden."));
      pending = null; pendingKind = null; pendingExpected = null; profile = { ...profile, levels: [], available: false, detail: data.message || "Modellinformationen konnten nicht geladen werden." }; render();
    } else if (message.type === "chat.started" && (!activeSessionId || !data.sessionId || data.sessionId === activeSessionId)) { if (submissionWaiters.size) rejectSubmission(new Error("Ein Auftrag wurde während der Reasoning-Auswahl gestartet. Prüfe die Eingabe erneut.")); running = true; close(false, false); render(); }
    else if (["chat.completed", "chat.failed", "chat.cancelled"].includes(message.type) && (!activeSessionId || !data.sessionId || data.sessionId === activeSessionId)) { running = false; if (modelId && !pending && !profile?.available) refresh(); else render(); }
    finishSubmissions();
  });
  overlay.hidden = true; render();
})();
