(function (global) {
  "use strict";
  // NativeAssistantPage's dirty render timer and once-per-second header tick.
  const native = Object.freeze({ renderMilliseconds: 80, labelMilliseconds: 1000, draftMilliseconds: 500,
    selectionScrollMilliseconds: 30, chipHoverMilliseconds: 100, timelineHoverInMilliseconds: 120,
    timelineHoverOutMilliseconds: 150, sliderFeedbackMilliseconds: 160, researchPollMilliseconds: 6000 });

  function create({ render, getScope, intervalMs = native.renderMilliseconds } = {}) {
    if (typeof render !== "function") throw new TypeError("render callback is required");
    const interval = Number.isFinite(intervalMs) && intervalMs > 0 ? intervalMs : native.renderMilliseconds;
    let scope = String(typeof getScope === "function" ? getScope() ?? "" : "");
    let timer = null, pending = null, generation = 0, disposed = false;
    const currentScope = () => String(typeof getScope === "function" ? getScope() ?? "" : scope);
    function cancel() {
      generation += 1;
      if (timer !== null) global.clearTimeout(timer);
      timer = null; pending = null;
    }
    function reset(scopeKey = currentScope()) {
      cancel(); scope = String(scopeKey ?? "");
    }
    function admit(options = {}) {
      if (disposed) return null;
      const authoritative = currentScope();
      const requested = String(options.scopeKey ?? authoritative);
      // A delayed packet from a previously visible prompt may not reset the new
      // prompt's scheduler or paint its terminal state into the current view.
      if (typeof getScope === "function" && requested !== authoritative) return null;
      if (requested !== scope) reset(requested);
      return { scopeKey: requested, follow: Boolean(options.follow), reason: options.reason ?? null };
    }
    function drain() {
      if (disposed || !pending) return false;
      const next = pending;
      if (next.scopeKey !== currentScope()) { reset(); return false; }
      cancel();
      // State is read by the caller now, after every packet has been merged.
      // Clear pending before rendering so a reentrant layout update is retained.
      render(next);
      return true;
    }
    function request(options = {}) {
      const next = admit(options);
      if (!next) return false;
      pending = { ...next, follow: next.follow || Boolean(pending?.follow) };
      if (options.immediate) return drain();
      if (timer !== null) return true;
      const scheduledGeneration = generation;
      timer = global.setTimeout(() => {
        if (disposed || scheduledGeneration !== generation) return;
        timer = null; drain();
      }, interval);
      return true;
    }
    function flush(options = {}) { return request({ ...options, immediate: true }); }
    function dispose() { cancel(); disposed = true; }
    return Object.freeze({ request, flush, reset, dispose });
  }
  const api = Object.freeze({ create, native });
  global.missumRenderTiming = api;
  if (typeof module === "object") module.exports = api;
})(globalThis);
