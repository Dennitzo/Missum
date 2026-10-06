(() => {
  "use strict";

  const controllers = new WeakMap();
  let previewSequence = 0;
  const text = message => String(message?.content ?? message?.text ?? "");
  const finite = value => Number.isFinite(Number(value)) ? Number(value) : 0;

  function previewText(value, maximum) {
    if (!Number.isInteger(maximum) || maximum < 2) throw new RangeError("Die Vorschau benötigt mindestens zwei Zeichen.");
    const normalized = String(value ?? "").replace(/\s+/gu, " ").trim();
    return normalized.length <= maximum ? normalized : normalized.slice(0, maximum - 1) + "…";
  }

  // Match NativePromptTimelineState: the last prompt at the bottom, otherwise
  // the last prompt within the viewport's small leading reading threshold.
  function selectActive(prompts, verticalOffset, viewportHeight, scrollableHeight) {
    const ordered = Array.from(prompts || [], item => Array.isArray(item)
      ? { id: item[0], offset: finite(item[1]) } : { id: item.id, offset: finite(item.offset) })
      .sort((left, right) => left.offset - right.offset);
    if (!ordered.length) return null;
    if (scrollableHeight - verticalOffset < 8) return ordered.at(-1).id;
    const threshold = verticalOffset + Math.min(96, viewportHeight * .3);
    return ordered.filter(item => item.offset <= threshold).at(-1)?.id ?? ordered[0].id;
  }

  function node(tag, className, value) {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (value !== undefined) element.textContent = value;
    return element;
  }

  function bookmark() {
    const icon = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    for (const [name, value] of Object.entries({ viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", "stroke-width": "1.6", "aria-hidden": "true" })) icon.setAttribute(name, value);
    icon.classList.add("browser-prompt-timeline-preview__bookmark");
    const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
    path.setAttribute("d", "M5 3h14v18l-7-4-7 4V3Z"); icon.append(path); return icon;
  }

  function current(controller, marker) {
    return !controller.disposed && controller.visible && marker.scopeKey === controller.scopeKey
      && !controller.container.hidden && !marker.button.hidden && Boolean(marker.target)
      && controller.markers.get(marker.id) === marker;
  }

  function paint(controller) {
    for (const marker of controller.markers.values()) {
      const active = marker.id === controller.activeId;
      const expanded = marker.pointer || marker.keyboard;
      marker.button.classList.toggle("browser-prompt-timeline-marker--active", active);
      marker.button.classList.toggle("browser-prompt-timeline-marker--preview", expanded);
      marker.line.style.width = `${expanded ? 22 : active ? 20 : 7}px`;
      if (active) marker.button.setAttribute("aria-current", "location");
      else marker.button.removeAttribute("aria-current");
    }
  }

  function hidePreview(controller, reset = true) {
    controller.preview.hidden = true;
    controller.previewMarker = null;
    controller.prompt.textContent = ""; controller.answer.textContent = "";
    for (const marker of controller.markers.values()) {
      marker.button.removeAttribute("aria-describedby");
      if (reset) { marker.pointer = false; marker.keyboard = false; }
    }
    paint(controller);
  }

  function positionPreview(controller, marker) {
    const bounds = marker.button.getBoundingClientRect();
    const previewBounds = controller.preview.getBoundingClientRect();
    const width = previewBounds.width || 326, height = previewBounds.height || 100;
    const viewportWidth = globalThis.innerWidth || document.documentElement?.clientWidth || 1024;
    const viewportHeight = globalThis.innerHeight || document.documentElement?.clientHeight || 768;
    let left = bounds.right + 8;
    if (left + width > viewportWidth - 8) left = bounds.left - width - 8;
    controller.preview.style.left = `${Math.max(8, Math.min(left, viewportWidth - width - 8))}px`;
    controller.preview.style.top = `${Math.max(8, Math.min(bounds.top - 10, viewportHeight - height - 8))}px`;
  }

  function showPreview(controller, marker) {
    if (!current(controller, marker) || !marker.pointer && !marker.keyboard) return;
    const index = controller.messages.findIndex(message => String(message.id) === marker.id);
    if (index < 0) return;
    const message = controller.messages[index];
    const answer = controller.messages.slice(index + 1).find(item => item.role === "assistant");
    const ordinal = controller.messages.slice(0, index + 1).filter(item => item.role === "user").length;
    controller.prompt.textContent = `${ordinal}) ${previewText(text(message), 118)}`;
    controller.answer.textContent = previewText(text(answer), 190);
    controller.answer.hidden = !controller.answer.textContent;
    controller.previewMarker?.button.removeAttribute("aria-describedby");
    controller.previewMarker = marker;
    marker.button.setAttribute("aria-describedby", controller.preview.id);
    controller.preview.hidden = false;
    paint(controller); positionPreview(controller, marker);
  }

  function updatePreview(controller, marker) {
    if (marker.pointer || marker.keyboard) showPreview(controller, marker);
    else if (controller.previewMarker === marker) hidePreview(controller, false);
    paint(controller);
  }

  function measure(controller) {
    if (!controller.visible || controller.disposed) return;
    const { container, scroller, messageRoot } = controller;
    const bounds = scroller.getBoundingClientRect();
    const parentBounds = (container.offsetParent || container.parentElement)?.getBoundingClientRect() || { top: 0, left: 0 };
    const viewport = scroller.clientHeight || bounds.height || Math.max(0, bounds.bottom - bounds.top);
    const height = Math.max(0, viewport - 44);
    container.style.top = `${bounds.top - parentBounds.top + 20}px`;
    container.style.left = `${bounds.left - parentBounds.left + 20}px`;
    container.style.height = `${height}px`;
    const articles = new Map(Array.from(messageRoot.querySelectorAll("[data-message-id]"), article => [String(article.dataset.messageId), article]));
    const rendered = [];
    for (const id of controller.promptIds) {
      const marker = controller.markers.get(id);
      if (!marker) continue;
      const target = articles.get(marker.id);
      marker.button.hidden = !target;
      marker.target = target || null;
      if (!target) continue;
      marker.offset = Math.max(0, target.getBoundingClientRect().top - bounds.top + finite(scroller.scrollTop) - 20);
      rendered.push(marker);
    }
    const available = Math.max(0, height - 14);
    const stride = Math.min(14, available / Math.max(1, rendered.length - 1));
    const start = (available - stride * (rendered.length - 1)) / 2;
    rendered.forEach((marker, index) => { marker.button.style.top = `${start + index * stride}px`; });
    container.hidden = !rendered.length;
    const scrollable = Math.max(0, finite(scroller.scrollHeight) - viewport);
    controller.activeId = selectActive(rendered, finite(scroller.scrollTop), viewport, scrollable);
    paint(controller);
    if (controller.previewMarker) {
      if (!controller.previewMarker.target) hidePreview(controller);
      else showPreview(controller, controller.previewMarker);
    }
  }

  function schedule(controller) {
    if (controller.frame !== null || controller.disposed) return;
    controller.frame = requestAnimationFrame(() => { controller.frame = null; measure(controller); });
  }

  function jump(controller, marker) {
    if (!current(controller, marker)) return;
    measure(controller);
    if (!marker.target) return;
    hidePreview(controller);
    const maximum = Math.max(0, finite(controller.scroller.scrollHeight) - finite(controller.scroller.clientHeight));
    const top = Math.max(0, Math.min(marker.offset, maximum));
    const behavior = globalThis.matchMedia?.("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth";
    controller.scroller.scrollTo({ top, behavior });
    schedule(controller);
  }

  function createMarker(controller, message, index) {
    const marker = { id: String(message.id), scopeKey: controller.scopeKey, pointer: false, keyboard: false, offset: 0, target: null };
    const button = node("button", "browser-prompt-timeline-marker"); button.type = "button";
    button.dataset.promptId = marker.id;
    const line = node("span", "browser-prompt-timeline-marker__line"); line.setAttribute("aria-hidden", "true");
    button.append(line); marker.button = button; marker.line = line;
    button.addEventListener("click", () => jump(controller, marker));
    button.addEventListener("pointerenter", () => { if (current(controller, marker)) { marker.pointer = true; updatePreview(controller, marker); } });
    button.addEventListener("pointerleave", () => { marker.pointer = false; updatePreview(controller, marker); });
    button.addEventListener("focus", () => {
      // An automatic/programmatic focus transfer is not a request for a preview.
      marker.keyboard = Date.now() - controller.keyboardAt < 250;
      controller.keyboardAt = -Infinity; updatePreview(controller, marker);
    });
    button.addEventListener("blur", () => { marker.keyboard = false; updatePreview(controller, marker); });
    button.addEventListener("keydown", event => {
      if (!current(controller, marker)) return;
      if (event.key === "Escape") { hidePreview(controller); event.preventDefault(); event.stopPropagation(); return; }
      const markers = [...controller.markers.values()].filter(item => !item.button.hidden);
      const position = markers.indexOf(marker);
      const next = event.key === "ArrowDown" || event.key === "ArrowRight" ? markers[(position + 1) % markers.length]
        : event.key === "ArrowUp" || event.key === "ArrowLeft" ? markers[(position - 1 + markers.length) % markers.length]
        : event.key === "Home" ? markers[0] : event.key === "End" ? markers.at(-1) : null;
      if (!next) return;
      event.preventDefault(); controller.keyboardAt = Date.now(); next.button.focus();
      next.keyboard = true; updatePreview(controller, next);
    });
    button.setAttribute("aria-label", `Zu Prompt ${index + 1} springen: ${previewText(text(message), 80)}`);
    return marker;
  }

  function unbind(controller) {
    controller.scroller?.removeEventListener("scroll", controller.scroll);
    controller.resizeObserver?.disconnect(); controller.mutationObserver?.disconnect();
    controller.resizeObserver = controller.mutationObserver = null;
  }

  function bind(controller, scroller, messageRoot) {
    unbind(controller); controller.scroller = scroller; controller.messageRoot = messageRoot;
    scroller.addEventListener("scroll", controller.scroll, { passive: true });
    if (globalThis.ResizeObserver) {
      controller.resizeObserver = new ResizeObserver(() => schedule(controller));
      controller.resizeObserver.observe(scroller); controller.resizeObserver.observe(messageRoot);
    }
    if (globalThis.MutationObserver) {
      controller.mutationObserver = new MutationObserver(() => schedule(controller));
      controller.mutationObserver.observe(messageRoot, { childList: true, subtree: true, characterData: true });
    }
  }

  function createController(container) {
    const preview = node("div", "browser-prompt-timeline-preview"); preview.id = `browser-prompt-preview-${++previewSequence}`;
    preview.setAttribute("role", "tooltip"); preview.hidden = true;
    const content = node("div", "browser-prompt-timeline-preview__text");
    const prompt = node("p", "browser-prompt-timeline-preview__prompt"), answer = node("p", "browser-prompt-timeline-preview__answer");
    content.append(prompt, answer); preview.append(content, bookmark()); document.body.append(preview);
    const controller = { container, preview, prompt, answer, markers: new Map(), messages: [], activeId: null,
      promptIds: [], scopeKey: null, scroller: null, messageRoot: null, visible: false, disposed: false, keyboardAt: -Infinity, frame: null };
    controller.scroll = () => { hidePreview(controller); schedule(controller); };
    controller.resize = () => schedule(controller);
    controller.keydown = event => { if (event.key === "Tab") controller.keyboardAt = Date.now(); };
    controller.pointerdown = () => { controller.keyboardAt = -Infinity; for (const marker of controller.markers.values()) marker.keyboard = false; updatePreview(controller, controller.previewMarker || { pointer: false, keyboard: false }); };
    document.addEventListener("keydown", controller.keydown, true);
    document.addEventListener("pointerdown", controller.pointerdown, true);
    globalThis.addEventListener("resize", controller.resize);
    container.classList.add("browser-prompt-timeline");
    return controller;
  }

  function render({ container, scroller, messageRoot, messages = [], scopeKey, visible = true } = {}) {
    if (!container) return false;
    if (!scroller || !messageRoot) { dispose(container); container.hidden = true; return false; }
    let controller = controllers.get(container);
    if (!controller) { controller = createController(container); controllers.set(container, controller); container.replaceChildren(); }
    const scope = String(scopeKey ?? "");
    if (controller.scopeKey !== scope || controller.scroller !== scroller) {
      hidePreview(controller); controller.markers.clear(); container.replaceChildren(); controller.activeId = null;
      controller.scopeKey = scope; bind(controller, scroller, messageRoot);
    } else if (controller.messageRoot !== messageRoot) bind(controller, scroller, messageRoot);
    controller.visible = Boolean(visible);
    const unique = new Map();
    (Array.isArray(messages) ? messages : []).forEach((message, index) => {
      if (!message || message.id == null) return;
      const id = String(message.id);
      unique.set(id, { message, index: unique.get(id)?.index ?? index, time: Date.parse(message.createdAt) });
    });
    const ordered = [...unique.values()]
      .sort((left, right) => (Number.isFinite(left.time) ? left.time : -Infinity) - (Number.isFinite(right.time) ? right.time : -Infinity) || left.index - right.index)
      .map(item => item.message);
    controller.messages = ordered;
    const prompts = ordered.filter(message => message.role === "user" && text(message).trim());
    controller.promptIds = [...new Set(prompts.map(message => String(message.id)))];
    const ids = new Set(prompts.map(message => String(message.id)));
    for (const [id, marker] of controller.markers) if (!ids.has(id)) {
      if (controller.previewMarker === marker) hidePreview(controller);
      marker.button.remove(); controller.markers.delete(id);
    }
    prompts.forEach((message, index) => {
      const id = String(message.id); let marker = controller.markers.get(id);
      if (!marker) { marker = createMarker(controller, message, index); controller.markers.set(id, marker); }
      marker.button.setAttribute("aria-label", `Zu Prompt ${index + 1} springen: ${previewText(text(message), 80)}`);
      if (container.children[index] !== marker.button) container.insertBefore(marker.button, container.children[index] || null);
    });
    if (!controller.visible || !prompts.length) { hidePreview(controller); container.hidden = true; return true; }
    measure(controller); return true;
  }

  function dispose(container) {
    const controller = controllers.get(container); if (!controller) return;
    controller.disposed = true; hidePreview(controller); unbind(controller);
    if (controller.frame !== null) cancelAnimationFrame(controller.frame);
    document.removeEventListener("keydown", controller.keydown, true); document.removeEventListener("pointerdown", controller.pointerdown, true);
    globalThis.removeEventListener("resize", controller.resize);
    controller.preview.remove(); controller.markers.clear(); controller.messages = [];
    container.replaceChildren(); container.hidden = true; controllers.delete(container);
  }

  globalThis.missumPromptTimeline = Object.freeze({ render, dispose, selectActive, previewText });
})();
