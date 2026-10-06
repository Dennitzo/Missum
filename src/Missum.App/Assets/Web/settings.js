(function () {
  "use strict";
  const page = document.getElementById("settings-page");
  if (!page) return;
  const editableFields = ["missumAiServerUrl", "isAutomaticSpeechEnabled", "liveCaptionLanguage", "theme", "language", "accentColor", "backgroundColor", "codingToolStepsExpanded"];
  const accents = [["Grau", "#B0B0B0"], ["Lila", "#A970FF"], ["Violett", "#7C5CFC"], ["Blau", "#4C8DFF"], ["Türkis", "#25B7A6"], ["Grün", "#8FBD45"], ["Orange", "#F4B860"], ["Pink", "#D95BA8"]];
  const backgrounds = [["Missum", "#181818"], ["Standard", "#6B6872"], ["Grau", "#858A94"], ["Dunkel", "#34313B"], ["Schwarz", "#000000"], ...accents.slice(1)];
  const state = { snapshot: null, values: {}, triggers: [], selected: new Set(), deleted: [], actions: [], dirty: false, saving: null, search: "", category: "", sort: "phrase", descending: false, conflict: null };
  const controls = new Map();
  let statusNode, triggerBody, actionPicker, newPhrase, countNode;
  let savedAppearance = null;

  function node(tag, className, text) {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (text !== undefined) element.textContent = text;
    return element;
  }
  function post(type, payload) { return globalThis.missumBridge.post(type, payload); }
  function status(text, error = false) { statusNode.textContent = text; statusNode.classList.toggle("settings-status--error", error); }
  function clearPendingSave() { state.saving = null; document.getElementById("settings-save").disabled = false; }
  function button(label, action, className = "secondary") {
    const element = node("button", className, label); element.type = "button"; element.addEventListener("click", action); return element;
  }
  function option(value, label) { const item = node("option", "", label); item.value = String(value); return item; }
  function card(title, detail) {
    const element = node("section", "settings-card"); element.append(node("h2", "", title));
    if (detail) element.append(node("p", "settings-note", detail)); page.append(element); return element;
  }
  function field(parent, key, label, choices) {
    const wrapper = node("label", "settings-field"); wrapper.append(node("span", "", label));
    const input = node(choices ? "select" : "input"); input.id = `settings-${key}`;
    if (choices) for (const [value, title] of choices) input.append(option(value, title));
    else { input.type = "text"; input.autocomplete = "off"; }
    input.addEventListener("input", () => { state.values[key] = input.value; state.dirty = true; preview(); });
    wrapper.append(input); parent.append(wrapper); controls.set(key, input); return input;
  }
  function toggle(parent, key, label) {
    const wrapper = node("label", "settings-toggle"); const input = node("input"); input.type = "checkbox";
    input.addEventListener("change", () => { state.values[key] = input.checked; state.dirty = true; });
    wrapper.append(input, node("span", "", label)); parent.append(wrapper); controls.set(key, input);
  }
  function colorPalette(parent, key, title, colors) {
    const section = node("fieldset", "settings-colors"); section.append(node("legend", "", title));
    for (const [name, value] of colors) {
      const item = button(name, () => { state.values[key] = value; state.dirty = true; renderColorSelection(); preview(); }, "settings-color");
      const swatch = node("i"); swatch.style.backgroundColor = value; item.prepend(swatch); item.dataset.color = value; item.dataset.setting = key;
      section.append(item);
    }
    parent.append(section);
  }
  function renderColorSelection() {
    for (const element of page.querySelectorAll("[data-color]")) element.setAttribute("aria-pressed", String(String(state.values[element.dataset.setting]).toUpperCase() === element.dataset.color.toUpperCase()));
  }
  function preview() {
    if (page.hidden) return;
    if (globalThis.missumAppearance?.apply) {
      const changed = ["theme", "accentColor", "backgroundColor"].some(key => state.values[key] !== state.snapshot?.values?.[key]);
      if (!changed && state.snapshot) globalThis.missumAppearance.apply(state.snapshot);
      else globalThis.missumAppearance.preview(state.values, state.snapshot?.resolvedTheme || "dark", state.snapshot?.resolvedAppearance);
      return;
    }
    const root = document.documentElement;
    root.dataset.theme = String(state.values.theme).toLowerCase() === "system" ? String(state.snapshot?.resolvedTheme || "dark").toLowerCase() : String(state.values.theme || "dark").toLowerCase();
    if (/^#[0-9a-f]{6}$/i.test(state.values.accentColor || "")) { root.style.setProperty("--accent", state.values.accentColor); globalThis.missumAppearance?.setAccent(state.values.accentColor); }
    if (/^#[0-9a-f]{6}$/i.test(state.values.backgroundColor || "")) root.style.setProperty("--background-accent", state.values.backgroundColor);
  }
  function captureAppearance() {
    if (globalThis.missumAppearance?.capture) return { captured: globalThis.missumAppearance.capture() };
    const root = document.documentElement;
    return { theme: root.dataset.theme, accent: root.style.getPropertyValue("--accent"), background: root.style.getPropertyValue("--background-accent") };
  }
  function restoreAppearance() {
    if (!savedAppearance) return;
    if (globalThis.missumAppearance?.apply && savedAppearance.snapshot) { globalThis.missumAppearance.apply(savedAppearance.snapshot); return; }
    if (globalThis.missumAppearance?.restore && savedAppearance.captured) { globalThis.missumAppearance.restore(savedAppearance.captured); return; }
    const root = document.documentElement;
    root.dataset.theme = savedAppearance.theme || "dark";
    for (const [name, value] of [["--accent", savedAppearance.accent], ["--background-accent", savedAppearance.background]]) {
      if (value) root.style.setProperty(name, value); else root.style.removeProperty(name);
    }
    globalThis.missumAppearance?.setAccent(savedAppearance.accent);
  }
  function applySnapshot(snapshot, force = false) {
    if (!snapshot?.values) return;
    if (state.dirty && !force) {
      savedAppearance = { snapshot };
      if (Number(snapshot.revision) === Number(state.snapshot?.revision)) {
        state.snapshot = { ...state.snapshot, resolvedTheme: snapshot.resolvedTheme, resolvedAppearance: snapshot.resolvedAppearance };
        preview(); return;
      }
      state.conflict = snapshot;
      status("Einstellungen wurden auf einem anderen Gerät geändert. Dein Entwurf bleibt erhalten. Lade den aktuellen Stand oder speichere erneut nach Prüfung.", true);
      return;
    }
    state.snapshot = snapshot;
    state.values = Object.fromEntries(editableFields.map(key => [key, snapshot.values[key]]));
    state.triggers = (snapshot.triggers || []).map(row => ({ ...row }));
    state.actions = snapshot.triggerActions || [];
    state.deleted = []; state.selected.clear(); state.dirty = false; state.conflict = null;
    for (const [key, input] of controls) { if (input.type === "checkbox") input.checked = Boolean(state.values[key]); else input.value = state.values[key] ?? ""; }
    actionPicker.replaceChildren(...state.actions.map(action => option(action.value, action.label)));
    const filter = document.getElementById("settings-trigger-category"); const value = filter.value;
    filter.replaceChildren(option("", "Alle Kategorien"), ...state.actions.map(action => option(action.value, action.label))); filter.value = value;
    renderColorSelection(); renderTriggers();
    const root = document.documentElement;
    root.lang = state.values.language || "de-DE";
    savedAppearance = globalThis.missumAppearance?.apply ? { snapshot }
      : { theme: String(state.values.theme).toLowerCase() === "system" ? String(snapshot.resolvedTheme || root.dataset.theme || "dark").toLowerCase() : String(state.values.theme || "Dark").toLowerCase(), accent: state.values.accentColor, background: state.values.backgroundColor };
    restoreAppearance(); preview();
  }
  function buildUpdate() {
    const values = {};
    for (const key of editableFields) if (state.values[key] !== state.snapshot?.values?.[key]) values[key] = state.values[key];
    const original = new Map((state.snapshot?.triggers || []).map(row => [String(row.id), row]));
    const triggers = state.triggers.filter(row => {
      const old = original.get(String(row.id));
      return !old || ["action", "phrase", "description", "isEnabled"].some(key => old[key] !== row[key]);
    }).map(row => ({ id: row.id, revision: row.revision || 0, action: row.action, extensionActionId: row.extensionActionId || null, phrase: row.phrase, description: row.description, isEnabled: Boolean(row.isEnabled) }));
    return { expectedRevision: state.snapshot.revision, values, triggers, deletedTriggers: state.deleted.map(row => ({ id: row.id, revision: row.revision })) };
  }
  function save() {
    if (!state.snapshot || state.saving) return;
    if (state.conflict) { status("Der Serverstand ist neuer. „Aktuellen Stand laden“ verwirft deinen Entwurf nach Bestätigung. Danach kannst du die Änderungen erneut übernehmen.", true); return; }
    state.saving = post("settings.update", buildUpdate());
    status("Einstellungen werden gespeichert …");
    document.getElementById("settings-save").disabled = true;
  }
  function renderTriggers() {
    const needle = state.search.trim().toLocaleLowerCase();
    const rows = state.triggers.filter(row => (!state.category || String(row.action) === state.category)
      && (!needle || [row.phrase, row.description, state.actions.find(action => String(action.value) === String(row.action))?.label].some(value => String(value || "").toLocaleLowerCase().includes(needle))));
    rows.sort((a, b) => String(a[state.sort] ?? "").localeCompare(String(b[state.sort] ?? ""), "de", { numeric: true }) * (state.descending ? -1 : 1));
    triggerBody.replaceChildren();
    for (const row of rows) {
      const tr = node("tr"); const selection = node("input"); selection.type = "checkbox"; selection.checked = state.selected.has(row.id); selection.setAttribute("aria-label", `Trigger ${row.phrase} auswählen`);
      selection.addEventListener("change", () => { if (selection.checked) state.selected.add(row.id); else state.selected.delete(row.id); updateSelection(); });
      const selectCell = node("td"); selectCell.append(selection); tr.append(selectCell);
      for (const key of ["phrase", "description", "action", "isEnabled"]) {
        const td = node("td"); const input = node(key === "action" ? "select" : "input");
        input.setAttribute("aria-label", `${key === "phrase" ? "Triggerphrase" : key === "description" ? "Beschreibung" : key === "action" ? "Kategorie" : "Aktiv"}: ${row.phrase}`);
        if (key === "action") {
          for (const action of state.actions) input.append(option(action.value, action.label)); input.value = String(row.action);
        } else if (key === "isEnabled") { input.type = "checkbox"; input.checked = Boolean(row.isEnabled); }
        else { input.type = "text"; input.value = row[key] || ""; input.maxLength = key === "phrase" ? 160 : 500; }
        const updateDraft = () => { row[key] = key === "isEnabled" ? input.checked : key === "action" ? (state.actions.find(action => String(action.value) === input.value)?.value ?? input.value) : input.value; if (key === "action") row.extensionActionId = state.actions.find(action => String(action.value) === input.value)?.extensionActionId || null; state.dirty = true; };
        if (key === "phrase" || key === "description") input.addEventListener("input", updateDraft);
        input.addEventListener("change", updateDraft);
        td.append(input); tr.append(td);
      }
      triggerBody.append(tr);
    }
    countNode.textContent = `${rows.length} von ${state.triggers.length} Triggern`;
    updateSelection();
  }
  function updateSelection() {
    const button = document.getElementById("settings-delete-triggers"); button.disabled = state.selected.size === 0; button.textContent = `Ausgewählte löschen (${state.selected.size})`;
  }
  function reload() {
    if (state.dirty && !globalThis.confirm("Deinen Einstellungsentwurf verwerfen und den aktuellen Serverstand laden?")) return;
    clearPendingSave(); state.dirty = false; state.conflict = null; post("settings.get", {}); status("Aktueller Stand wird geladen …");
  }
  function addTrigger() {
    const phrase = newPhrase.value.trim();
    if (!phrase) { status("Bitte eine Triggerphrase eingeben.", true); newPhrase.focus(); return; }
    const action = state.actions.find(item => String(item.value) === actionPicker.value)?.value;
    if (action === undefined) { status("Bitte eine Kategorie auswählen.", true); return; }
    state.triggers.push({ id: globalThis.crypto?.randomUUID?.() || "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, value => { const random = Math.floor(Math.random() * 16); return (value === "x" ? random : (random & 3) | 8).toString(16); }), revision: 0, action, extensionActionId: state.actions.find(item => item.value === action)?.extensionActionId || null, phrase, description: "", isEnabled: true });
    newPhrase.value = ""; state.dirty = true; renderTriggers();
  }
  async function restoreBackup() {
    const input = node("input"); input.type = "file"; input.accept = ".missumbackup"; input.hidden = true;
    input.addEventListener("change", async () => {
      const file = input.files?.[0]; if (!file) return;
      if (file.size <= 0 || file.size > 64 * 1024 * 1024) { status("Die Backup-Datei muss zwischen 1 Byte und 64 MB groß sein.", true); input.remove(); return; }
      status("Backup wird hochgeladen und geprüft …");
      try { post("backup.restore", { fileName: file.name, base64: await globalThis.missumBridge.fileToBase64(file) }); }
      catch (error) { status(`Backup konnte nicht gelesen werden: ${error.message}`, true); }
      finally { input.remove(); }
    }, { once: true });
    input.addEventListener("cancel", () => input.remove(), { once: true }); document.body.append(input); input.click();
  }
  function open() {
    if (page.hidden || !savedAppearance) savedAppearance = captureAppearance();
    if (globalThis.location && location.hash !== "#settings") location.hash = "settings";
    globalThis.missumPanels?.setView("settings");
    page.hidden = false; post("settings.get", {}); if (state.snapshot) preview();
  }
  function close() { page.hidden = true; restoreAppearance(); if (globalThis.location?.hash === "#settings") location.hash = ""; globalThis.missumPanels?.setView("chat"); }

  const header = node("header", "settings-heading"); const identity = node("div"); identity.append(node("h1", "", "Einstellungen"), node("p", "settings-note", "Missum auf diesem PC konfigurieren"));
  const actions = node("div", "settings-actions"); const saveButton = button("Speichern", save, "primary"); saveButton.id = "settings-save";
  actions.append(button("Zum Assistenten", close), button("Aktuellen Stand laden", reload), saveButton); header.append(identity, actions); page.append(header);
  statusNode = node("p", "settings-status"); statusNode.setAttribute("role", "status"); page.append(statusNode);
  const gateway = card("Missum AI Server", "Gatewayadresse und Verbindung beziehen sich auf den Windows-PC, auf dem Missum läuft.");
  field(gateway, "missumAiServerUrl", "Gatewayadresse");
  gateway.append(button("Verbindung testen und Fähigkeiten laden", () => { post("settings.connectionTest", { missumAiServerUrl: state.values.missumAiServerUrl }); status("Verbindung wird vom PC geprüft …"); }));
  const capabilities = node("pre", "settings-capabilities"); capabilities.id = "settings-capabilities"; gateway.append(capabilities);
  const speech = card("Sprache", "Vorlesen verwendet Supertonic-3 F5. Auf diesem Browser erfolgt die Wiedergabe; das Modell läuft auf dem PC.");
  toggle(speech, "isAutomaticSpeechEnabled", "Antworten automatisch vorlesen"); field(speech, "liveCaptionLanguage", "Untertitelsprache", [["de", "Deutsch"], ["en", "Englisch"], ["auto", "Automatisch"]]);
  speech.append(node("p", "settings-note", "Mikrofonaufnahme und Bildschirmfreigabe stehen über eine HTTP-LAN-Adresse nicht zur Verfügung. Dateien und Screenshots kannst du im Chat anhängen."));
  const appearance = card("Darstellung", "Die Vorschau gilt bis zum Speichern nur auf diesem Gerät. System folgt der Windows-Einstellung des Servers.");
  const appearanceGrid = node("div", "settings-field-grid"); appearance.append(appearanceGrid);
  field(appearanceGrid, "theme", "App-Theme", [["system", "Windows-Systemeinstellung"], ["light", "Hell"], ["dark", "Dunkel"]]);
  field(appearanceGrid, "language", "Sprache", [["de-DE", "Deutsch"], ["en-US", "English"]]);
  toggle(appearance, "codingToolStepsExpanded", "Werkzeugschritte automatisch aufklappen");
  colorPalette(appearance, "accentColor", "Akzentfarbe", accents); colorPalette(appearance, "backgroundColor", "Hintergrundfarbe", backgrounds);
  const backups = card("Sicherung", "Chats, Projekte, Dateien, Trigger und Einstellungen in einer .missumbackup-Datei sichern. Backups sind nicht verschlüsselt.");
  const backupActions = node("div", "settings-actions"); backupActions.append(button("Backup herunterladen", () => { post("backup.create", {}); status("Backup wird auf dem PC erstellt …"); }, "primary"), button("Backup wiederherstellen", restoreBackup)); backups.append(backupActions);
  const triggers = card("Prompt-Trigger", "Aktive Phrasen routen die Anfrage zum zugeordneten Dienst. Bearbeitungen werden mit „Speichern“ übernommen.");
  const addRow = node("div", "settings-trigger-add"); actionPicker = node("select"); actionPicker.setAttribute("aria-label", "Kategorie für neuen Trigger"); newPhrase = node("input"); newPhrase.placeholder = "Neue Triggerphrase"; newPhrase.maxLength = 160; newPhrase.setAttribute("aria-label", "Neue Triggerphrase"); addRow.append(actionPicker, newPhrase, button("Zeile hinzufügen", addTrigger)); triggers.append(addRow);
  const filters = node("div", "settings-trigger-filters"); const search = node("input"); search.type = "search"; search.placeholder = "Tabelle durchsuchen …"; search.setAttribute("aria-label", "Prompt-Trigger durchsuchen"); search.addEventListener("input", () => { state.search = search.value; renderTriggers(); });
  const filter = node("select"); filter.id = "settings-trigger-category"; filter.setAttribute("aria-label", "Prompt-Trigger nach Kategorie filtern"); filter.append(option("", "Alle Kategorien")); filter.addEventListener("change", () => { state.category = filter.value; renderTriggers(); });
  const remove = button("Ausgewählte löschen (0)", () => {
    state.deleted.push(...state.triggers.filter(row => state.selected.has(row.id) && (state.snapshot?.triggers || []).some(old => old.id === row.id)));
    state.triggers = state.triggers.filter(row => !state.selected.has(row.id)); state.selected.clear(); state.dirty = true; renderTriggers();
  }); remove.id = "settings-delete-triggers"; remove.disabled = true; filters.append(search, filter, remove); triggers.append(filters);
  const tableWrapper = node("div", "settings-table-scroll"); const table = node("table", "settings-trigger-table"); const head = node("thead"); const row = node("tr");
  row.append(node("th", "", "Auswahl"));
  for (const [key, label] of [["phrase", "Triggerphrase"], ["description", "Beschreibung"], ["action", "Kategorie"], ["isEnabled", "Aktiv"]]) {
    const th = node("th"); th.scope = "col"; th.append(button(label, () => { state.descending = state.sort === key ? !state.descending : false; state.sort = key; renderTriggers(); }, "settings-sort")); row.append(th);
  }
  head.append(row); triggerBody = node("tbody"); table.append(head, triggerBody); tableWrapper.append(table); triggers.append(tableWrapper); countNode = node("p", "settings-note"); triggers.append(countNode);
  document.getElementById("open-settings")?.addEventListener("click", open);
  globalThis.addEventListener("missum:bridge-disconnected", () => {
    if (!state.saving) return;
    clearPendingSave();
    status("Die Speicherbestätigung wurde unterbrochen. Dein Entwurf bleibt erhalten. Lade nach der Wiederverbindung den aktuellen Serverstand, um die Speicherung zu prüfen.", true);
  });
  globalThis.addEventListener("missum:host-message", event => {
    const { type, payload, requestId } = event.detail;
    if (type === "settings.snapshot" || type === "settings.changed") {
      const ownSave = Boolean(state.saving && state.saving === requestId);
      if (ownSave) clearPendingSave();
      applySnapshot(payload, ownSave); if (ownSave) status("Einstellungen gespeichert.");
    } else if (type === "settings.conflict") {
      clearPendingSave();
      state.conflict = payload.current || payload.snapshot || payload;
      if (state.conflict?.values && globalThis.missumAppearance?.apply) savedAppearance = { snapshot: state.conflict };
      status(payload.message || "Der Serverstand wurde zwischenzeitlich geändert. Dein Entwurf bleibt erhalten.", true);
    } else if (type === "settings.connectionResult") {
      status(payload.message || payload.connectionStatus || (payload.isReady ? "Verbindung bereit." : "Verbindung nicht bereit."), Boolean(payload.error || payload.isReady === false));
      capabilities.textContent = JSON.stringify(payload.capabilities || payload.status || payload, null, 2);
    } else if (type === "backup.ready") {
      const anchor = node("a"); anchor.href = globalThis.missumBridge.resourceUrl(payload.url); anchor.download = payload.fileName || "Missum.missumbackup"; document.body.append(anchor); anchor.click(); anchor.remove(); status("Backup steht als Download bereit.");
    } else if (type === "backup.restoreReady") {
      if (globalThis.confirm(`Geprüftes Backup „${payload.fileName || "Missum"}“ wiederherstellen? Dies ersetzt die Daten auf dem PC. Missum erstellt eine Sicherheitskopie und startet anschließend neu.`)) { post("backup.restoreCommit", { restoreId: payload.restoreId }); status("Wiederherstellung wird ausgeführt. Der Browser verbindet sich nach dem Neustart erneut."); }
      else { post("backup.restoreCancel", { restoreId: payload.restoreId }); status("Wiederherstellung abgebrochen."); }
    } else if (type === "backup.restoreDeferred") status(payload.message || "Wiederherstellung wartet auf den Abschluss aktiver Aufträge.");
    else if (type === "backup.restored") status("Backup wiederhergestellt. Missum startet neu …");
    else if (type === "host.error" || type === "settings.error") { if (state.saving === requestId) clearPendingSave(); if (!page.hidden) status(payload.message || "Aktion fehlgeschlagen.", true); }
  });
  globalThis.missumSettings = Object.freeze({ open, close, leave: () => { page.hidden = true; restoreAppearance(); if (globalThis.location?.hash === "#settings") location.hash = ""; }, isPreviewing: () => !page.hidden, buildUpdate, applySnapshot });
  if (globalThis.location?.hash === "#settings") open();
})();
