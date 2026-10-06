(function (global) {
  "use strict";
  const requestTimeout = 30000;
  let dialog, title, server, sidebar, breadcrumbs, pathInput, searchInput, list, summary, status, up, select, more;
  let state = null;
  let connected = true;
  let requestCounter = 0;

  function node(tag, className, text) {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (text !== undefined) element.textContent = text;
    return element;
  }
  function button(label, action, className = "server-workspace-button") {
    const element = node("button", className, label); element.type = "button";
    element.addEventListener("click", action); return element;
  }
  function folderIcon() {
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    for (const [key, value] of Object.entries({ viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", "stroke-width": "1.6", "aria-hidden": "true" })) svg.setAttribute(key, value);
    const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
    path.setAttribute("d", "M3 6V4h6l2 2h10v14H3Zm0 2h18"); svg.append(path); return svg;
  }
  function cleanPath(value) { return typeof value === "string" ? value.trim() : ""; }
  function pathName(value) { return value.replace(/[\\/]+$/, "").split(/[\\/]/).at(-1) || value; }
  function normalizedPath(value) { return cleanPath(value).replace(/[\\/]+$/, "").toLocaleLowerCase(); }
  function message(text, error = false) {
    status.textContent = text;
    status.classList.toggle("server-workspace-status--error", error);
    status.setAttribute("role", error ? "alert" : "status");
  }
  function clearRequest() {
    if (!state?.pending) return;
    global.clearTimeout(state.pending.timer);
    state.pending = null;
  }
  function busyControls() {
    const pending = Boolean(state?.pending || state?.searchTimer);
    const candidate = state?.selected || (state?.pending?.selectOnSuccess ? state.pending.path : null) || state?.listing?.path;
    list.setAttribute("aria-busy", String(pending));
    select.disabled = !state?.validated || !connected || pending || !candidate
      || normalizedPath(pathInput.value) !== normalizedPath(state.listing?.path);
    select.textContent = state?.selected || state?.pending?.selectOnSuccess ? `„${pathName(candidate)}“ auswählen` : "Ordner auswählen";
    select.title = candidate ? `Ordner auswählen: ${candidate}` : "Öffne oder markiere einen Ordner auf dem Server.";
    for (const row of list.querySelectorAll(".server-workspace-folder")) row.disabled = pending || !connected;
    more.disabled = pending || !connected;
    up.disabled = !state?.listing?.path || !connected;
    searchInput.disabled = !state?.listing?.path;
    summary.textContent = candidate ? `Ordner: ${candidate}` : "Öffne oder markiere einen erreichbaren Ordner auf dem Server-PC.";
  }
  function finish(path) {
    if (!state) return;
    const previous = state;
    clearRequest(); global.clearTimeout(previous.searchTimer);
    state = null;
    dialog.close();
    if (previous.returnFocus?.isConnected !== false) previous.returnFocus?.focus();
    previous.resolve(path);
  }
  function choose() {
    if (!state || select.disabled || !state.validated) return;
    if (state.selected) { navigate(state.selected, { selectOnSuccess: true }); return; }
    if (!state.listing?.path) return;
    finish(state.listing.path);
  }
  function navigate(path, options = {}) {
    if (!state) return;
    global.clearTimeout(state.searchTimer); state.searchTimer = null;
    if (!options.append && !options.filterOnly) searchInput.value = "";
    const requestedPath = cleanPath(path) || null;
    if (!options.preservePathText) {
      pathInput.value = requestedPath || "";
      state.pathEditVersion += 1;
    }
    clearRequest();
    state.selected = null;
    if (!connected) { state.validated = false; message("Die Verbindung zum Server ist unterbrochen. Dein eingegebener Pfad bleibt erhalten.", true); busyControls(); return; }
    const requestId = global.missumBridge?.newRequestId?.() || global.crypto?.randomUUID?.() || `workspace-${Date.now()}-${++requestCounter}`;
    const pending = { requestId, path: requestedPath, filter: searchInput.value.slice(0, 256),
      offset: options.append ? state.nextOffset : 0, append: Boolean(options.append),
      preservePathText: Boolean(options.preservePathText), selectOnSuccess: Boolean(options.selectOnSuccess),
      editVersion: state.pathEditVersion, timer: null };
    state.pending = pending;
    message(options.append ? "Weitere Ordner werden geladen …" : "Ordner auf dem Server werden geladen …");
    busyControls();
    pending.timer = global.setTimeout(() => {
      if (state?.pending !== pending) return;
      state.pending = null; state.validated = false;
      message("Der Server hat nicht rechtzeitig geantwortet. Dein eingegebener Pfad bleibt erhalten; versuche „Öffnen“ erneut.", true);
      busyControls();
    }, requestTimeout);
    try {
      if (!global.missumBridge?.post) throw new Error("Die Verbindung zum Missum-Server ist noch nicht bereit.");
      global.missumBridge.post("workspace.browse", { path: pending.path, filter: pending.filter, offset: pending.offset }, requestId);
    } catch (error) {
      if (state?.pending !== pending) return;
      clearRequest(); state.validated = false;
      message(error?.message || "Der Ordner konnte nicht geladen werden.", true); busyControls();
    }
  }
  function rootOrParent() {
    if (!state?.listing?.path) return;
    navigate(state.listing.parentPath || null);
  }
  function sideEntry(entry, className) {
    const item = button("", () => navigate(entry.path), className);
    item.title = entry.path; item.append(folderIcon(), node("span", "server-workspace-place-name", entry.name || pathName(entry.path)));
    if (normalizedPath(entry.path) === normalizedPath(state.listing?.path)) item.classList.add("active");
    return item;
  }
  function renderSidebar() {
    sidebar.replaceChildren();
    const computer = button("Dieser PC", () => navigate(null), "server-workspace-place");
    computer.classList.toggle("active", !state.listing?.path); sidebar.append(computer);
    sidebar.append(node("h3", "server-workspace-side-title", "Laufwerke"));
    for (const entry of state.listing?.roots || []) sidebar.append(sideEntry(entry, "server-workspace-place"));
    if (state.recent.length) {
      sidebar.append(node("h3", "server-workspace-side-title", "Zuletzt verwendete Projekte"));
      for (const entry of state.recent) sidebar.append(sideEntry(entry, "server-workspace-place server-workspace-recent"));
    }
  }
  function renderBreadcrumbs() {
    breadcrumbs.replaceChildren(button("Dieser PC", () => navigate(null), "server-workspace-crumb"));
    for (const entry of state.listing?.breadcrumbs || []) {
      breadcrumbs.append(node("span", "server-workspace-crumb-divider", "›"));
      const crumb = button(entry.name, () => navigate(entry.path), "server-workspace-crumb");
      crumb.title = entry.path; breadcrumbs.append(crumb);
    }
  }
  function renderListing() {
    const listing = state.listing;
    server.textContent = listing?.serverName ? `Server: ${listing.serverName}` : "Ordner auf dem Missum-Server";
    renderSidebar(); renderBreadcrumbs(); list.replaceChildren();
    const entries = listing?.path ? state.directories : listing?.roots || [];
    for (const entry of entries) {
      const row = button("", () => {
        if (row.disabled || !state) return;
        state.selected = entry.path;
        for (const sibling of list.querySelectorAll(".server-workspace-folder")) {
          sibling.classList.toggle("selected", sibling === row);
          sibling.setAttribute("aria-pressed", String(sibling === row));
        }
        busyControls();
      }, "server-workspace-folder");
      row.dataset.path = entry.path; row.title = entry.path;
      row.setAttribute("aria-label", entry.name); row.setAttribute("aria-pressed", "false");
      row.append(folderIcon(), node("span", "server-workspace-folder-name", entry.name));
      row.addEventListener("dblclick", () => { if (!row.disabled) navigate(entry.path); });
      row.addEventListener("keydown", event => {
        if (row.disabled) return;
        if (event.key === "Enter" || event.key === "ArrowRight") {
          event.preventDefault(); navigate(entry.path); return;
        }
        const folders = list.querySelectorAll(".server-workspace-folder"); const index = Array.from(folders).indexOf(row);
        const target = event.key === "ArrowDown" ? folders[(index + 1) % folders.length]
          : event.key === "ArrowUp" ? folders[(index - 1 + folders.length) % folders.length]
          : event.key === "Home" ? folders[0] : event.key === "End" ? folders[folders.length - 1] : null;
        if (target) { event.preventDefault(); target.focus(); }
      });
      list.append(row);
    }
    if (!entries.length) list.append(node("p", "server-workspace-empty",
      !listing?.path ? "Keine erreichbaren Laufwerke gefunden." : searchInput.value ? "Keine passenden Unterordner." : "Keine Unterordner."));
    more.hidden = !listing?.hasMore;
    more.textContent = listing?.hasMore ? `Weitere Ordner laden (${state.directories.length} von ${listing.totalDirectories})` : "Weitere Ordner laden";
    busyControls();
  }
  function receive(event) {
    const envelope = event.detail;
    if (!state?.pending || envelope?.requestId !== state.pending.requestId) return;
    if (envelope.type === "host.error") {
      clearRequest(); state.validated = false;
      message(envelope.payload?.message || "Der Ordner ist nicht erreichbar oder der Zugriff wurde verweigert.", true);
      busyControls(); return;
    }
    if (envelope.type !== "workspace.list") return;
    const payload = envelope.payload;
    const validEntries = entries => Array.isArray(entries) && entries.every(entry => entry
      && typeof entry.path === "string" && typeof entry.name === "string");
    if (!payload || !validEntries(payload.roots) || !validEntries(payload.directories)
      || !validEntries(payload.breadcrumbs) || (payload.path !== null && typeof payload.path !== "string")) {
      clearRequest(); state.validated = false; message("Der Server hat eine ungültige Ordnerliste gesendet. Öffne den Pfad erneut.", true); busyControls(); return;
    }
    const pending = state.pending;
    clearRequest();
    if (pending.selectOnSuccess && (!payload.path || normalizedPath(payload.path) !== normalizedPath(pending.path))) {
      state.validated = false; busyControls();
      message("Der Server hat einen anderen Ordner bestätigt. Wähle den gewünschten Ordner erneut.", true); return;
    }
    state.listing = payload;
    const all = pending.append ? [...state.directories, ...payload.directories] : payload.directories;
    state.directories = [...new Map(all.filter(entry => entry && typeof entry.path === "string" && typeof entry.name === "string")
      .map(entry => [normalizedPath(entry.path), entry])).values()];
    state.nextOffset = Number(payload.offset || 0) + payload.directories.length;
    state.validated = true;
    state.selected = null;
    if (!pending.preservePathText && state.pathEditVersion === pending.editVersion) pathInput.value = payload.path || "";
    const restoreFocus = [list, breadcrumbs, sidebar].some(container => Array.from(container.querySelectorAll("button")).includes(document.activeElement));
    renderListing();
    if (restoreFocus) (list.querySelector(".server-workspace-folder") || pathInput).focus();
    if (pending.selectOnSuccess) {
      if (state.pathEditVersion !== pending.editVersion) {
        message("Der Pfad wurde während der Prüfung geändert. Öffne oder wähle den gewünschten Ordner erneut."); return;
      }
      finish(payload.path); return;
    }
    message(payload.path ? `${Number(payload.totalDirectories) || 0} Unterordner · Doppelklick öffnet einen Ordner.` : "Wähle ein Laufwerk oder einen zuletzt verwendeten Projektordner.");
  }
  function disconnected() {
    connected = false;
    if (!state) return;
    clearRequest(); state.validated = false; global.clearTimeout(state.searchTimer); state.searchTimer = null;
    message("Die Verbindung zum Server ist unterbrochen. Dein eingegebener Pfad bleibt erhalten.", true); busyControls();
  }
  function reconnected() {
    connected = true;
    if (!state) return;
    message("Verbindung wiederhergestellt. Öffne den Pfad erneut, um den Ordner zu prüfen."); busyControls();
  }
  function createDialog() {
    dialog = node("dialog", "server-workspace-dialog"); dialog.id = "server-workspace-overlay";
    dialog.setAttribute("aria-labelledby", "server-workspace-title");
    dialog.setAttribute("aria-describedby", "server-workspace-server");
    const header = node("header", "server-workspace-header");
    const copy = node("div"); title = node("h2", "", "Projektordner auf dem PC auswählen"); title.id = "server-workspace-title";
    server = node("p", "", "Ordner auf dem Missum-Server"); server.id = "server-workspace-server";
    copy.append(title, server);
    const cancelTop = button("×", () => finish(null), "server-workspace-close"); cancelTop.setAttribute("aria-label", "Ordnerauswahl schließen");
    header.append(copy, cancelTop);
    const body = node("div", "server-workspace-body"); sidebar = node("nav", "server-workspace-sidebar"); sidebar.setAttribute("aria-label", "Laufwerke und Projekte auf dem Server");
    const main = node("div", "server-workspace-main");
    const navigation = node("div", "server-workspace-navigation");
    up = button("↑", rootOrParent); up.setAttribute("aria-label", "Übergeordneter Ordner");
    breadcrumbs = node("nav", "server-workspace-breadcrumbs"); breadcrumbs.setAttribute("aria-label", "Ordnerpfad"); navigation.append(up, breadcrumbs);
    const pathbar = node("div", "server-workspace-pathbar");
    pathInput = node("input", "server-workspace-path"); pathInput.id = "server-workspace-path"; pathInput.type = "text"; pathInput.autocomplete = "off";
    pathInput.maxLength = 4096; pathInput.placeholder = "Vollständiger Ordnerpfad auf dem PC";
    pathInput.setAttribute("aria-label", "Ordnerpfad auf dem Server-PC");
    pathInput.addEventListener("input", () => { if (!state) return; state.pathEditVersion += 1; state.selected = null; busyControls(); });
    pathInput.addEventListener("keydown", event => { if (event.key === "Enter") { event.preventDefault(); navigate(pathInput.value); } });
    pathbar.append(pathInput, button("Öffnen", () => navigate(pathInput.value)));
    searchInput = node("input", "server-workspace-search"); searchInput.id = "server-workspace-search"; searchInput.type = "search"; searchInput.maxLength = 256;
    searchInput.placeholder = "Unterordner durchsuchen"; searchInput.setAttribute("aria-label", "Unterordner durchsuchen");
    searchInput.addEventListener("input", () => {
      if (!state?.listing?.path) return;
      // Selection cannot use an earlier unfiltered listing while the query changes.
      clearRequest(); global.clearTimeout(state.searchTimer); state.validated = false;
      state.searchTimer = global.setTimeout(() => navigate(state?.listing?.path, { filterOnly: true, preservePathText: true }), 250);
      busyControls();
    });
    list = node("div", "server-workspace-list"); list.id = "server-workspace-list"; list.setAttribute("aria-label", "Ordner auf dem Server");
    more = button("Weitere Ordner laden", () => navigate(state?.listing?.path, { append: true, preservePathText: true })); more.hidden = true;
    status = node("p", "server-workspace-status"); status.id = "server-workspace-status"; status.setAttribute("aria-live", "polite");
    main.append(navigation, pathbar, searchInput, list, more, status); body.append(sidebar, main);
    const footer = node("footer", "server-workspace-footer"); summary = node("p", "server-workspace-summary");
    const actions = node("div", "server-workspace-actions");
    select = button("Ordner auswählen", choose, "server-workspace-button server-workspace-primary"); select.id = "server-workspace-select";
    select.setAttribute("aria-label", "Ordner auswählen");
    actions.append(button("Abbrechen", () => finish(null)), select); footer.append(summary, actions);
    dialog.append(header, body, footer); document.body.append(dialog);
    dialog.addEventListener("cancel", event => { event.preventDefault(); finish(null); });
    dialog.addEventListener("click", event => {
      if (event.target !== dialog) return;
      const bounds = dialog.getBoundingClientRect();
      if (event.clientX < bounds.left || event.clientX > bounds.right || event.clientY < bounds.top || event.clientY > bounds.bottom) finish(null);
    });
    dialog.addEventListener("keydown", event => {
      if (!state) return;
      if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); finish(null); return; }
      if (event.altKey && event.key === "ArrowUp") { event.preventDefault(); rootOrParent(); return; }
      if (event.key !== "Tab") return;
      const focusable = Array.from(dialog.querySelectorAll("button,input")).filter(element => !element.disabled && !element.hidden);
      const first = focusable[0], last = focusable.at(-1);
      if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus(); }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus(); }
    });
    global.addEventListener("missum:host-message", receive);
  }
  function open(options = {}) {
    if (state) return state.promise;
    if (!dialog) createDialog();
    const seen = new Set();
    const recent = (Array.isArray(options.recentPaths) ? options.recentPaths : []).map(value => {
      const path = cleanPath(typeof value === "string" ? value : value?.path || value?.workspacePath);
      return { path, name: typeof value === "object" && value?.name ? String(value.name) : pathName(path) };
    }).filter(entry => entry.path && !seen.has(normalizedPath(entry.path)) && seen.add(normalizedPath(entry.path))).slice(0, 30);
    let resolve;
    const promise = new Promise(done => { resolve = done; });
    state = { promise, resolve, recent, returnFocus: document.activeElement, listing: null, pending: null,
      directories: [], nextOffset: 0, selected: null, validated: false, pathEditVersion: 0, searchTimer: null };
    searchInput.value = ""; pathInput.value = ""; more.hidden = true; renderListing();
    dialog.showModal();
    navigate(cleanPath(options.initialPath) || recent[0]?.path || null);
    pathInput.focus();
    return promise;
  }
  global.addEventListener("missum:bridge-disconnected", disconnected);
  global.addEventListener("missum:bridge-ready", reconnected);
  global.missumWorkspace = Object.freeze({ open });
})(globalThis);
