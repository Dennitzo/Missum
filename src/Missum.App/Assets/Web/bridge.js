(function () {
  "use strict";

  const version = 2;
  function storedIdentity(storageName, key) {
    try {
      const storage = globalThis[storageName];
      const saved = storage?.getItem(key);
      if (saved) return saved;
      const created = globalThis.crypto?.randomUUID?.() || `${Date.now()}-${Math.random().toString(16).slice(2)}`;
      storage?.setItem(key, created);
      return created;
    } catch {
      return globalThis.crypto?.randomUUID?.() || `${Date.now()}-${Math.random().toString(16).slice(2)}`;
    }
  }
  const deviceId = storedIdentity("localStorage", "assistant.lan.device-id");
  let tabId = storedIdentity("sessionStorage", "assistant.lan.tab-id");
  let clientId = `${deviceId}.${tabId}`;
  let identityReady = false;
  let identityChannel = null;
  let identityInstance = null;
  let lanTransportGeneration = 0;
  let lanSocket = null;
  let lanEvents = null;
  let lanHttpReady = false;
  let lanTransportWasReady = false;
  let lanHttpSendChain = Promise.resolve();
  let hostEpoch = null;
  let lastRevision = 0;
  const pendingLanMessages = [];
  const unacknowledgedChatCommands = new Map();
  function currentClientEnvelope(serialized) {
    const envelope = JSON.parse(serialized); envelope.clientId = clientId; envelope.tabId = tabId; return JSON.stringify(envelope);
  }
  function abandonSentChat(message) {
    for (const [requestId, command] of unacknowledgedChatCommands) {
      if (!command.sent) continue;
      unacknowledgedChatCommands.delete(requestId);
      receive({ data: { version, type: "host.error", requestId, payload: { message } } });
    }
  }
  function renewTabIdentity() {
    tabId = newRequestId(); clientId = `${deviceId}.${tabId}`;
    try { globalThis.sessionStorage?.setItem("assistant.lan.tab-id", tabId); } catch { /* Page identity remains unique without storage. */ }
    if (!identityReady) return;
    // A delayed collision must not replay an already submitted job as a second client.
    abandonSentChat("Dieser Tab wurde von einem zweiten Tab getrennt. Prüfe den Chat vor einem erneuten Auftrag; eine unbestätigte Eingabe bleibt erhalten.");
    lanTransportGeneration += 1;
    lanSocket?.close(); lanEvents?.close(); lanSocket = null; lanEvents = null; lanHttpReady = false;
    globalThis.dispatchEvent(new CustomEvent("missum:bridge-disconnected"));
    connectLanBridge();
  }
  function claimTabIdentity() {
    if (typeof BroadcastChannel !== "function") return false;
    try { identityChannel = new BroadcastChannel(`missum-assistant-tabs:v2:${deviceId}`); } catch { return false; }
    identityInstance = newRequestId();
    identityChannel.addEventListener("message", event => {
      const claim = event.data;
      if (!claim || claim.tabId !== tabId || claim.instanceId === identityInstance) return;
      if (claim.type === "probe" && (identityReady || identityInstance.localeCompare(claim.instanceId) < 0)) {
        identityChannel.postMessage({ type: "occupied", tabId, instanceId: identityInstance, targetInstanceId: claim.instanceId });
      } else if (claim.type === "occupied" && claim.targetInstanceId === identityInstance) renewTabIdentity();
    });
    globalThis.addEventListener?.("pagehide", () => identityChannel?.close());
    identityChannel.postMessage({ type: "probe", tabId, instanceId: identityInstance });
    return true;
  }
  function markChatRetry() {
    for (const command of unacknowledgedChatCommands.values()) if (command.sent) command.retryNeeded = true;
  }
  function acknowledgeChat(envelope) {
    if (["chat.queued", "chat.started", "chat.completed", "chat.cancelled", "chat.failed", "host.error"].includes(envelope.type)) {
      unacknowledgedChatCommands.delete(envelope.requestId);
    } else if (envelope.type === "action.completed" && envelope.version === 2 && envelope.payload?.duplicate
      && envelope.payload.originalRequestId === envelope.requestId) unacknowledgedChatCommands.delete(envelope.requestId);
    else if (["state.snapshot", "queue.changed"].includes(envelope.type)) {
      const queue = envelope.payload.runQueue || (envelope.type === "queue.changed" ? envelope.payload : null);
      for (const run of [queue?.active, ...(queue?.pending || [])]) if (run?.requestId) unacknowledgedChatCommands.delete(run.requestId);
    }
  }
  function retryChatAfterSnapshot() {
    for (const command of unacknowledgedChatCommands.values()) {
      if (!command.retryNeeded) continue;
      if (lanSocket?.readyState === 1) lanSocket.send(command.serialized);
      else if (lanHttpReady) sendHttp(command.serialized);
      else return;
      command.retryNeeded = false;
    }
  }
  const allowedOutbound = new Set([
    "app.ready", "conversation.refresh", "chat.send", "chat.steer", "chat.cancel", "chat.resume", "session.create", "session.open",
    "models.list", "models.select", "science.presentation.get", "settings.get", "settings.update", "settings.connectionTest",
    "promptTriggers.list", "promptTriggers.apply",
    "backup.create", "backup.restore", "backup.restoreCommit", "backup.restoreCancel", "speech.playbackProgress", "session.groupDeleteEmpty",
    "mode.switch", "action.invoke", "workspace.browse",
    "reasoning.get", "reasoning.set",
    "session.rename", "session.delete", "session.clear", "session.draft", "session.groupCollapse", "session.projectCreate", "session.workspaceCreate", "document.pick", "document.paste", "document.upload",
    "document.remove",
    "memory.list", "memory.create", "memory.update", "memory.pin", "memory.confirm", "memory.delete", "memory.autoCapture",
    "research.list", "research.open", "research.export",
    "message.exportPdf", "message.copy",
    "attachment.remove", "coding.pickWorkspace",
    "artifact.save", "artifact.preview", "artifact.open", "screen.capture", "screenClip.start", "screenClip.stop", "screenClip.cancel",
    "audioCapture.start", "audioCapture.stop", "audioCapture.cancel",
    "microphone.start", "microphone.audio", "microphone.speak", "microphone.stopSpeech", "microphone.toggleSpeechPause", "microphone.stop", "microphone.cancel",
    "liveCaption.start", "liveCaption.stop", "ui.sessionPane", "external.open"
  ]);
  const allowedInbound = new Set([
    "state.snapshot", "conversation.snapshot", "conversation.messageCommitted", "action.completed",
    "models.snapshot", "settings.snapshot", "settings.changed", "settings.conflict", "settings.error", "settings.connectionResult",
    "promptTriggers.snapshot", "promptTriggers.changed",
    "backup.ready", "backup.restoreReady", "backup.restoreDeferred", "backup.restored", "speech.audio", "speech.reset", "speech.complete", "speech.pause",
    "subagent.snapshot", "subagents.snapshot", "science.presentation", "download.ready", "host.capabilities", "backup.cancelled",
    "reasoning.snapshot", "workspace.list",
    "chat.queued", "queue.changed", "chat.started", "chat.delta", "chat.completed", "chat.steer.accepted",
    "chat.cancelled", "chat.failed", "coding.changes", "session.changed", "session.grouped",
    "memory.snapshot", "memory.changed", "research.snapshot", "research.exported", "document.changed", "document.import.started", "document.import.progress", "document.import.completed", "status.changed", "speech.status", "speech.progress", "theme.changed",
    "draft.saved", "caption.changed", "screenClip.changed", "audioCapture.changed", "capture.required", "capture.cancelled",
    "microphone.changed", "microphone.transcript", "artifact.previewReady", "host.error"
  ]);

  function newRequestId() {
    if (globalThis.crypto && typeof globalThis.crypto.randomUUID === "function") {
      return globalThis.crypto.randomUUID();
    }
    const bytes = new Uint8Array(16);
    if (globalThis.crypto?.getRandomValues) globalThis.crypto.getRandomValues(bytes);
    else for (let index = 0; index < bytes.length; index += 1) bytes[index] = Math.floor(Math.random() * 256);
    bytes[6] = (bytes[6] & 15) | 64; bytes[8] = (bytes[8] & 63) | 128;
    const hex = Array.from(bytes, value => value.toString(16).padStart(2, "0")).join("");
    return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
  }

  function isLanBrowser() {
    return !globalThis.chrome?.webview && location.protocol.startsWith("http");
  }

  function browserResourceUrl(value) {
    const raw = String(value || "").trim();
    if (!raw || !isLanBrowser()) return raw;
    let source;
    try { source = new URL(raw, location.href); } catch { return raw; }
    const path = source.pathname.replace(/^\/+/, "");
    const suffix = `${source.search}${source.hash}`;
    if (source.hostname === "assistant-preview.local") {
      return new URL(`preview/${path}${suffix}`, location.href).href;
    }
    if (source.hostname === "assistant.local" && path.startsWith("artifacts/")) {
      return new URL(`${path}${suffix}`, location.href).href;
    }
    if (source.hostname === "assistant-coding-preview.local" && path.startsWith("coding/")) {
      return new URL(`coding-preview/${path.slice("coding/".length)}${suffix}`, location.href).href;
    }
    return raw;
  }

  function fileToBase64(file) {
    return file.arrayBuffer().then(buffer => {
      const bytes = new Uint8Array(buffer);
      let binary = "";
      for (let offset = 0; offset < bytes.length; offset += 0x8000) {
        binary += String.fromCharCode(...bytes.subarray(offset, Math.min(offset + 0x8000, bytes.length)));
      }
      return globalThis.btoa(binary);
    });
  }

  async function uploadFiles(files, sessionId, requestId) {
    const selected = Array.from(files || []).filter(file => file instanceof File);
    if (!sessionId || selected.length === 0) return false;
    if (selected.length > 32) {
      globalThis.alert("Wähle höchstens 32 Dateien gleichzeitig aus.");
      return false;
    }
    const totalBytes = selected.reduce((sum, file) => sum + Number(file.size || 0), 0);
    if (selected.some(file => file.size <= 0 || file.size > 32 * 1024 * 1024)
      || totalBytes > 48 * 1024 * 1024) {
      globalThis.alert("Die Auswahl überschreitet das Upload-Limit von 32 MB je Datei beziehungsweise 48 MB insgesamt.");
      return false;
    }
    try {
      const payloadFiles = await Promise.all(selected.map(async (file, index) => ({
        fileName: browserUploadFileName(file, index),
        contentType: file.type || "application/octet-stream",
        base64: await fileToBase64(file)
      })));
      post("document.upload", { sessionId, files: payloadFiles }, requestId);
      return true;
    } catch {
      globalThis.alert("Die ausgewählten Dateien konnten nicht gelesen werden.");
      return false;
    }
  }

  function browserUploadFileName(file, index) {
    const extensionByType = {
      "image/png": ".png", "image/jpeg": ".jpg", "image/webp": ".webp", "image/gif": ".gif",
      "application/pdf": ".pdf", "text/plain": ".txt", "audio/wav": ".wav", "audio/mpeg": ".mp3",
      "video/mp4": ".mp4"
    };
    const supplied = String(file?.name || "").split(/[\\/]/).pop().trim()
      .replace(/[\\/:*?"<>|\u0000-\u001f]/g, "-").slice(0, 240);
    if (supplied) return supplied;
    const extension = extensionByType[String(file?.type || "").toLocaleLowerCase()] || ".bin";
    return `Zwischenablage-${Date.now()}-${index + 1}${extension}`;
  }

  function pickBrowserFiles(sessionId, requestId) {
    const input = document.createElement("input");
    input.type = "file";
    input.multiple = true;
    input.hidden = true;
    const pickFolder = typeof input.webkitdirectory !== "undefined"
      && globalThis.confirm("Möchtest du einen Ordner auswählen? OK wählt einen Ordner, Abbrechen einzelne Dateien.");
    if (pickFolder) input.webkitdirectory = true;
    const cleanup = () => input.remove();
    input.addEventListener("change", () => {
      void uploadFiles(input.files, sessionId, requestId).finally(cleanup);
    }, { once: true });
    input.addEventListener("cancel", cleanup, { once: true });
    document.body.append(input);
    input.click();
  }

  function copyWithSelection(text) {
    const area = document.createElement("textarea");
    area.value = text;
    area.setAttribute("readonly", "");
    area.style.position = "fixed";
    area.style.opacity = "0";
    document.body.append(area);
    area.select();
    const copied = document.execCommand("copy");
    area.remove();
    if (!copied) globalThis.alert("Der Text konnte nicht in die Zwischenablage kopiert werden.");
    return copied;
  }

  function copyInBrowser(text) {
    const value = String(text || "");
    if (globalThis.navigator?.clipboard?.writeText) {
      void globalThis.navigator.clipboard.writeText(value).catch(() => copyWithSelection(value));
      return;
    }
    copyWithSelection(value);
  }

  function emitLocal(type, payload, requestId) {
    receive({ data: { version, type, requestId: requestId || newRequestId(), payload } });
  }

  async function printInBrowser(messageId) {
    if (!globalThis.missumPrepareBookPdf?.(messageId || null)) return;
    let finished = false;
    const finish = () => {
      if (finished) return;
      finished = true;
      globalThis.missumFinishBookPdf?.();
    };
    globalThis.addEventListener("afterprint", finish, { once: true });
    for (let attempt = 0; attempt < 40 && !globalThis.missumPdfBookReady?.(); attempt += 1) {
      await new Promise(resolve => setTimeout(resolve, 50));
    }
    globalThis.print();
    setTimeout(finish, 60_000);
  }

  function handleBrowserAction(envelope) {
    if (!isLanBrowser()) return false;
    const payload = envelope.payload || {};
    if (["screen.capture", "screenClip.start", "audioCapture.start"].includes(envelope.type)) {
      globalThis.missumApp?.notify("Aufnahme ist über HTTP nicht verfügbar. Wähle eine Datei oder einen Screenshot.");
      pickBrowserFiles(payload.sessionId || globalThis.missumApp?.getState()?.activeSessionId, envelope.requestId);
      return true;
    }
    if (["microphone.start", "microphone.audio", "liveCaption.start"].includes(envelope.type) && !globalThis.isSecureContext) {
      emitLocal("host.error", { message: "Mikrofonaufnahme und Live-Untertitel sind über die HTTP-LAN-Adresse nicht verfügbar. Hänge eine Audio- oder Videodatei an." }, envelope.requestId);
      return true;
    }
    if (envelope.type === "document.pick"
      || (envelope.type === "action.invoke" && payload.actionId === "builtin.workspace/attach-files-and-folders")) {
      pickBrowserFiles(payload.sessionId, envelope.requestId);
      return true;
    }
    if (envelope.type === "message.copy") {
      copyInBrowser(payload.text);
      return true;
    }
    if (envelope.type === "microphone.stopSpeech") {
      globalThis.missumBrowserSpeech?.stop();
      return false;
    }
    if (envelope.type === "microphone.toggleSpeechPause") {
      const paused = globalThis.missumBrowserSpeech?.togglePause();
      if (typeof paused === "boolean") envelope.payload.paused = paused;
      return false;
    }
    if ((envelope.type === "artifact.open" || envelope.type === "artifact.save") && payload.artifactId) {
      const suffix = envelope.type === "artifact.save" ? "?download=1" : "";
      const url = new URL(`artifacts/${encodeURIComponent(payload.artifactId)}${suffix}`, location.href).href;
      if (envelope.type === "artifact.open") globalThis.open(url, "_blank", "noopener,noreferrer");
      else {
        const anchor = document.createElement("a");
        anchor.href = url;
        anchor.download = "";
        document.body.append(anchor);
        anchor.click();
        anchor.remove();
      }
      return true;
    }
    if (envelope.type === "external.open" && payload.url) {
      let url;
      try { url = new URL(String(payload.url)); } catch { return true; }
      if (url.protocol === "http:" || url.protocol === "https:") {
        globalThis.open(url.href, "_blank", "noopener,noreferrer");
      }
      return true;
    }
    return false;
  }

  function post(type, payload, requestId) {
    if (!allowedOutbound.has(type)) {
      throw new Error(`Nicht erlaubter Bridge-Typ: ${type}`);
    }
    const envelope = { version, type, requestId: requestId || newRequestId(), payload: payload || {} };
    if (isLanBrowser()) { envelope.clientId = clientId; envelope.tabId = tabId; }
    if (handleBrowserAction(envelope)) return envelope.requestId;
    if (globalThis.chrome?.webview) {
      globalThis.chrome.webview.postMessage(envelope);
    } else if (location.protocol.startsWith("http")) {
      const serialized = JSON.stringify(envelope);
      if (type === "chat.send" || type === "chat.resume") unacknowledgedChatCommands.set(envelope.requestId, { serialized, sent: Boolean(lanSocket?.readyState === 1 || lanHttpReady), retryNeeded: false });
      if (lanSocket?.readyState === 1) {
        try { lanSocket.send(serialized); }
        catch (error) { unacknowledgedChatCommands.delete(envelope.requestId); throw error; }
      }
      else if (lanHttpReady) sendHttp(serialized);
      else pendingLanMessages.push(serialized);
    } else {
      throw new Error("Die Assistenten-Bridge ist noch nicht verbunden.");
    }
    return envelope.requestId;
  }

  function configureContract(contract) {
    if (!contract || ![1, version].includes(Number(contract.version))) return false;
    const outbound = Array.isArray(contract.clientActions) ? contract.clientActions : [];
    const inbound = Array.isArray(contract.hostEvents) ? contract.hostEvents : [];
    if (!outbound.includes("app.ready") || !inbound.includes("state.snapshot") || !inbound.includes("host.error")) return false;
    allowedOutbound.clear();
    outbound.forEach(type => { if (typeof type === "string" && type) allowedOutbound.add(type); });
    allowedInbound.clear();
    inbound.forEach(type => { if (typeof type === "string" && type) allowedInbound.add(type); });
    return true;
  }

  function receive(event) {
    const envelope = event.data;
    if (!envelope || ![1, version].includes(envelope.version) || typeof envelope.type !== "string"
      || !allowedInbound.has(envelope.type) || typeof envelope.payload !== "object") {
      return;
    }
    if (envelope.clientId && envelope.clientId !== clientId && isLanBrowser()) return;
    if (envelope.hostEpoch && envelope.hostEpoch !== hostEpoch) {
      if (hostEpoch) {
        abandonSentChat("Missum wurde neu gestartet. Prüfe den Chat vor einem erneuten Auftrag; deine noch unbestätigte Eingabe bleibt erhalten.");
      }
      hostEpoch = envelope.hostEpoch;
      lastRevision = 0;
    }
    const revision = Number(envelope.revision);
    if (Number.isFinite(revision) && revision > 0) {
      if (revision <= lastRevision) return;
      lastRevision = revision;
    }
    acknowledgeChat(envelope);
    globalThis.dispatchEvent(new CustomEvent("missum:host-message", { detail: envelope }));
    if (envelope.type === "state.snapshot") retryChatAfterSnapshot();
  }

  if (globalThis.chrome?.webview) {
    globalThis.chrome.webview.addEventListener("message", receive);
  }

  function flushPending(send) {
    while (pendingLanMessages.length > 0) {
      const serialized = currentClientEnvelope(pendingLanMessages.shift());
      const command = unacknowledgedChatCommands.get(JSON.parse(serialized).requestId);
      if (command) { command.sent = true; command.serialized = serialized; }
      send(serialized);
    }
    globalThis.dispatchEvent(new CustomEvent("missum:bridge-ready"));
  }

  function sendHttp(serialized) {
    let requestId = null;
    try { requestId = JSON.parse(serialized)?.requestId || null; } catch { /* Local validation reports below. */ }
    lanHttpSendChain = lanHttpSendChain.then(async () => {
      const response = await fetch(new URL("message", location.href), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: serialized,
        cache: "no-store"
      });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
    }).catch(error => {
      receive({ data: {
        version,
        type: "host.error",
        requestId,
        payload: { message: `Die LAN-Verbindung konnte eine Aktion nicht übertragen (${error?.message || "Netzwerkfehler"}).` }
      } });
    });
    return lanHttpSendChain;
  }

  function readyEnvelope() {
    return JSON.stringify({ version, type: "app.ready", requestId: newRequestId(), payload: {}, clientId, tabId });
  }

  function connectHttpBridge() {
    if (lanEvents || typeof EventSource !== "function" || typeof fetch !== "function") return;
    const generation = lanTransportGeneration;
    const eventUrl = new URL("events", location.href);
    eventUrl.searchParams.set("clientId", clientId);
    lanEvents = new EventSource(eventUrl);
    lanEvents.addEventListener("message", event => {
      if (generation !== lanTransportGeneration) return;
      try { receive({ data: JSON.parse(event.data) }); } catch { /* Reject malformed host data. */ }
    });
    lanEvents.addEventListener("open", () => {
      if (generation !== lanTransportGeneration) return;
      const pendingHasReady = pendingLanMessages.some(serialized => {
        try { return JSON.parse(serialized)?.type === "app.ready"; } catch { return false; }
      });
      lanHttpReady = true;
      if (lanTransportWasReady || !pendingHasReady) sendHttp(readyEnvelope());
      lanTransportWasReady = true;
      flushPending(sendHttp);
    });
    lanEvents.addEventListener("error", () => {
      if (generation !== lanTransportGeneration) return;
      lanHttpReady = false;
      markChatRetry();
      globalThis.dispatchEvent(new CustomEvent("missum:bridge-disconnected"));
    });
  }

  function connectLanBridge() {
  if (isLanBrowser()) {
    const generation = lanTransportGeneration;
    if (typeof WebSocket === "function") {
      const socketProtocol = location.protocol === "https:" ? "wss:" : "ws:";
      try {
        const bridgeUrl = new URL("bridge", `${socketProtocol}//${location.host}${location.pathname}`);
        bridgeUrl.searchParams.set("clientId", clientId);
        lanSocket = new WebSocket(bridgeUrl.href);
      } catch {
        lanSocket = null;
        connectHttpBridge();
      }
      if (!lanSocket) return;
      lanSocket.addEventListener("message", event => {
        if (generation !== lanTransportGeneration) return;
        try { receive({ data: JSON.parse(event.data) }); } catch { /* Reject malformed host data. */ }
      });
      lanSocket.addEventListener("open", () => {
        if (generation !== lanTransportGeneration) return;
        lanTransportWasReady = true;
        flushPending(serialized => lanSocket.send(serialized));
      });
      lanSocket.addEventListener("close", () => {
        if (generation !== lanTransportGeneration) return;
        lanSocket = null;
        markChatRetry();
        globalThis.dispatchEvent(new CustomEvent("missum:bridge-disconnected"));
        connectHttpBridge();
      });
    } else {
      connectHttpBridge();
    }
  }
  }

  globalThis.missumBridge = Object.freeze({
    post,
    newRequestId,
    version,
    get clientId() { return clientId; },
    deviceId,
    get tabId() { return tabId; },
    capabilities: Object.freeze({ microphone: !isLanBrowser() || Boolean(globalThis.isSecureContext && globalThis.navigator?.mediaDevices?.getUserMedia), screen: !isLanBrowser() || Boolean(globalThis.isSecureContext && globalThis.navigator?.mediaDevices?.getDisplayMedia) }),
    isLanBrowser: isLanBrowser(),
    uploadFiles,
    fileToBase64,
    pickFiles: pickBrowserFiles,
    resourceUrl: browserResourceUrl,
    configureContract
  });
  if (isLanBrowser()) {
    if (claimTabIdentity()) setTimeout(() => { identityReady = true; connectLanBridge(); }, 150);
    else { identityReady = true; connectLanBridge(); }
  }
})();
