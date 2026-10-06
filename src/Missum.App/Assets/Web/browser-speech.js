(function () {
  "use strict";
  if (!globalThis.missumBridge?.isLanBrowser) return;
  let playbackId = null;
  let nextSequence = 0;
  let current = null;
  let paused = false;
  let complete = false;
  let generation = 0;
  let feedbackSequence = 0;
  let lastPosition = -1;
  let advanceTimer = null;
  const queued = new Map();
  const rejectedPlaybacks = new Set();
  function controls(active = Boolean(playbackId)) {
    globalThis.dispatchEvent(new CustomEvent("missum:host-message", { detail: {
      version: 2, type: "microphone.changed", payload: {
        isRecording: false, isBusy: active, isSpeaking: active, canPauseSpeech: active,
        isSpeechPaused: active && paused, status: active ? (paused ? "Pausiert" : "Spricht") : "Inaktiv", provider: "browser-f5"
      }
    } }));
  }

  function feedback(state) {
    if (!current || !playbackId) return;
    const position = Number(current.audio.currentTime) || 0;
    globalThis.missumBridge.post("speech.playbackProgress", {
      playbackId, sequence: current.payload.sequence, positionSeconds: position,
      currentTime: position, state, eventSequence: ++feedbackSequence
    });
  }

  function cleanup(entry) {
    if (!entry) return;
    entry.audio.pause();
    entry.audio.removeAttribute("src");
    entry.audio.load();
  }

  function reset(payload) {
    generation += 1;
    clearTimeout(advanceTimer);
    advanceTimer = null;
    cleanup(current);
    for (const entry of queued.values()) cleanup(entry);
    current = null;
    queued.clear();
    paused = false;
    complete = false;
    playbackId = payload?.playbackId || null;
    nextSequence = Number(payload?.firstSequence) || 0;
    feedbackSequence = 0;
    lastPosition = -1;
    showBlocked(false);
    controls();
  }

  function showBlocked(blocked) {
    const button = document.getElementById("browser-audio-resume");
    if (button) button.hidden = !blocked;
  }

  async function playCurrent() {
    if (!current || paused) return;
    const owner = current;
    const epoch = generation;
    try {
      await owner.audio.play();
      if (epoch !== generation || owner !== current) { owner.audio.pause(); return; }
      showBlocked(false);
      feedback("playing");
    } catch (error) {
      if (epoch !== generation || owner !== current) return;
      paused = true;
      showBlocked(true);
      controls();
      feedback(error?.name === "NotAllowedError" ? "blocked" : "failed");
    }
  }

  function advance() {
    if (current || paused || advanceTimer) return;
    const entry = queued.get(nextSequence);
    if (!entry) {
      if (complete && queued.size === 0) showBlocked(false);
      return;
    }
    queued.delete(nextSequence);
    current = entry;
    lastPosition = -1;
    void playCurrent();
  }

  function enqueue(payload) {
    if (!payload?.playbackId || rejectedPlaybacks.has(payload.playbackId)) return;
    if (playbackId !== payload.playbackId) {
      // Only a reset begins playback. Delayed packets cannot revive a stopped queue.
      return;
    }
    const sequence = Number(payload.sequence);
    if (!Number.isInteger(sequence) || sequence < nextSequence || queued.has(sequence)
      || current?.payload.sequence === sequence) return;
    const url = globalThis.missumBridge.resourceUrl(payload.url);
    if (!url) return;
    const audio = new Audio();
    audio.preload = "auto";
    audio.src = url;
    const entry = { payload, audio };
    const epoch = generation;
    audio.addEventListener("timeupdate", () => {
      if (epoch !== generation || entry !== current) return;
      const position = Number(audio.currentTime) || 0;
      if (Math.abs(position - lastPosition) < 0.12) return;
      lastPosition = position;
      feedback(paused ? "paused" : "playing");
    });
    audio.addEventListener("ended", () => {
      if (epoch !== generation || entry !== current) return;
      feedback("ended");
      current = null;
      nextSequence = sequence + 1;
      cleanup(entry);
      const pause = Math.min(5000, Math.max(0, Number(payload.pauseAfterMilliseconds) || 0));
      if (pause) advanceTimer = setTimeout(() => { advanceTimer = null; advance(); }, pause);
      else advance();
    });
    audio.addEventListener("error", () => {
      if (epoch !== generation || entry !== current) return;
      paused = true;
      feedback("failed");
      showBlocked(true);
      controls();
    });
    queued.set(sequence, entry);
    audio.load();
    advance();
  }

  function setPaused(value) {
    paused = Boolean(value);
    if (paused) { current?.audio.pause(); feedback("paused"); }
    else { showBlocked(false); if (current) void playCurrent(); else advance(); }
    controls();
    return paused;
  }

  function stop() {
    if (playbackId) {
      rejectedPlaybacks.add(playbackId);
      if (rejectedPlaybacks.size > 64) rejectedPlaybacks.delete(rejectedPlaybacks.values().next().value);
    }
    reset(null);
  }

  globalThis.addEventListener("missum:host-message", event => {
    const { type, payload } = event.detail;
    if (type === "speech.reset") {
      if (!payload?.playbackId) stop();
      else if (!rejectedPlaybacks.has(payload.playbackId)) reset(payload);
    } else if (type === "speech.audio") enqueue(payload);
    else if (type === "speech.pause" && (!payload?.playbackId || payload.playbackId === playbackId)) setPaused(payload.paused);
    else if (type === "speech.complete" && payload?.playbackId === playbackId) { complete = true; advance(); }
    else if (type === "speech.status") controls(Boolean(payload?.active));
  });
  globalThis.addEventListener("missum:bridge-disconnected", stop);
  document.getElementById("browser-audio-resume")?.addEventListener("click", () => setPaused(false));
  globalThis.missumBrowserSpeech = Object.freeze({ stop, togglePause: () => setPaused(!paused) });
})();
