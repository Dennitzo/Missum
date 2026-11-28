// Missum Webdesign overlay. Injected by the "Webseite" tab into every page it shows; it is never
// written into project files. It draws markings and badges in a closed shadow root and talks to
// Missum only through chrome.webview.postMessage. Page scripts can see this object, so messages
// only ever create drafts in Missum, never actions.
(() => {
  if (window.top !== window || window.__missum) return;
  const NONCE = "__MISSUM_NONCE__";
  const ACCENT = "#7c5cff", DONE = "#2ea043";
  const post = (type, data) => {
    try { window.chrome.webview.postMessage(JSON.stringify(Object.assign({}, data || {}, { nonce: NONCE, type, url: location.href }))); }
    catch (_) { /* outside Missum */ }
  };

  let host = null, root = null, highlight = null, hoverLabel = null, badges = null, regionBox = null, popover = null, aiPointer = null;
  let mode = "none", annotations = [], previews = new Map(), drag = null, hoverEl = null, raf = 0;

  function ensure() {
    if (host && host.isConnected) return;
    host = document.createElement("missum-overlay");
    host.setAttribute("aria-hidden", "true");
    host.style.cssText = "all:initial;position:fixed;inset:0;z-index:2147483647;pointer-events:none;display:block;";
    root = host.attachShadow({ mode: "closed" });
    root.innerHTML = `<style>
      :host{all:initial}
      .hl{position:fixed;border:2px solid ${ACCENT};background:rgba(124,92,255,.10);border-radius:3px;pointer-events:none;display:none;box-sizing:border-box;transition:all .05s}
      .lbl{position:fixed;background:${ACCENT};color:#fff;font:600 11px/1.6 "Segoe UI",system-ui,sans-serif;padding:0 6px;border-radius:4px;pointer-events:none;display:none;white-space:nowrap}
      .badge{position:fixed;min-width:22px;height:22px;border-radius:11px;background:${ACCENT};color:#fff;font:700 12px/22px "Segoe UI",system-ui,sans-serif;text-align:center;padding:0 5px;box-sizing:border-box;box-shadow:0 2px 6px rgba(0,0,0,.35);pointer-events:auto;cursor:pointer;transform:translate(-50%,-50%)}
      .badge.done{background:${DONE}}
      .badge.pin{border-radius:11px 11px 11px 2px}
      .frame{position:fixed;border:2px solid ${ACCENT};border-radius:4px;pointer-events:none;box-sizing:border-box}
      .frame.region{border-style:dashed;background:rgba(124,92,255,.06)}
      .frame.done{border-color:${DONE};background:transparent}
      .drag{position:fixed;border:2px dashed ${ACCENT};background:rgba(124,92,255,.12);pointer-events:none;display:none;box-sizing:border-box}
      .pop{position:fixed;width:280px;background:#1f1c33;color:#ecebf5;border:1px solid #4b4570;border-radius:10px;padding:10px;box-shadow:0 8px 24px rgba(0,0,0,.45);font:13px/1.4 "Segoe UI",system-ui,sans-serif;pointer-events:auto}
      .pop textarea{width:100%;box-sizing:border-box;height:72px;resize:vertical;background:#15132a;color:inherit;border:1px solid #4b4570;border-radius:6px;padding:6px;font:inherit}
      .pop .row{display:flex;gap:8px;justify-content:flex-end;margin-top:8px}
      .pop button{font:600 12px "Segoe UI",system-ui,sans-serif;border-radius:6px;border:1px solid #4b4570;background:#2a2645;color:inherit;padding:5px 10px;cursor:pointer}
      .pop button.primary{background:${ACCENT};border-color:${ACCENT};color:#fff}
      .ai{position:fixed;width:18px;height:18px;border-radius:50%;border:3px solid #ff9f1a;background:rgba(255,159,26,.25);transform:translate(-50%,-50%);pointer-events:none;display:none}
      .gz{position:fixed;border:1.5px solid ${ACCENT};box-sizing:border-box;pointer-events:auto;cursor:move;background:rgba(124,92,255,.04)}
      .gz .h{position:absolute;width:12px;height:12px;border-radius:3px;background:#fff;border:2px solid ${ACCENT};box-sizing:border-box}
      .gz .nw{left:-7px;top:-7px;cursor:nwse-resize}.gz .ne{right:-7px;top:-7px;cursor:nesw-resize}
      .gz .sw{left:-7px;bottom:-7px;cursor:nesw-resize}.gz .se{right:-7px;bottom:-7px;cursor:nwse-resize}
      .gz .rot{left:50%;top:-34px;margin-left:-7px;border-radius:50%;cursor:grab}
      .gz .stem{position:absolute;left:50%;top:-22px;width:1.5px;height:20px;background:${ACCENT}}
      .gz .rad{left:10px;top:10px;width:10px;height:10px;border-radius:50%;background:${ACCENT};border-color:#fff;cursor:ew-resize}
      .gz .tag{position:absolute;left:0;bottom:-24px;background:${ACCENT};color:#fff;font:600 11px/18px "Segoe UI",system-ui,sans-serif;padding:0 6px;border-radius:4px;white-space:nowrap;pointer-events:none}
      .pulse{position:fixed;border:3px solid ${DONE};border-radius:6px;pointer-events:none;animation:p 1.2s ease-out 2;box-sizing:border-box}
      @keyframes p{0%{opacity:1;transform:scale(1)}100%{opacity:0;transform:scale(1.08)}}
    </style>
    <div class="hl"></div><div class="lbl"></div><div class="badges"></div><div class="drag"></div><div class="ai"></div>`;
    highlight = root.querySelector(".hl");
    hoverLabel = root.querySelector(".lbl");
    badges = root.querySelector(".badges");
    regionBox = root.querySelector(".drag");
    aiPointer = root.querySelector(".ai");
    (document.documentElement || document).appendChild(host);
  }

  const isOverlay = el => el === host;
  function elementAt(x, y) {
    let el = document.elementFromPoint(x, y);
    if (!el || isOverlay(el)) return null;
    return el;
  }

  function cssEscape(value) { return window.CSS && CSS.escape ? CSS.escape(value) : value.replace(/[^a-zA-Z0-9_-]/g, "\\$&"); }
  function unique(selector) { try { return document.querySelectorAll(selector).length === 1; } catch (_) { return false; } }
  function selectorFor(el) {
    if (!(el instanceof Element)) return "";
    if (el.id && unique("#" + cssEscape(el.id))) return "#" + cssEscape(el.id);
    const parts = [];
    let node = el;
    while (node && node.nodeType === 1 && node !== document.documentElement && parts.length < 7) {
      let part = node.tagName.toLowerCase();
      if (node.id) { part += "#" + cssEscape(node.id); parts.unshift(part); if (unique(parts.join(" > "))) break; node = node.parentElement; continue; }
      const classes = [...node.classList].filter(c => !/^(is-|has-|js-)/.test(c)).slice(0, 2);
      if (classes.length) part += "." + classes.map(cssEscape).join(".");
      const parent = node.parentElement;
      if (parent) {
        const same = [...parent.children].filter(c => c.tagName === node.tagName);
        if (same.length > 1) part += `:nth-of-type(${same.indexOf(node) + 1})`;
      }
      parts.unshift(part);
      if (unique(parts.join(" > "))) break;
      node = parent;
    }
    return parts.join(" > ");
  }

  const STYLE_KEYS = ["color", "backgroundColor", "fontSize", "fontWeight", "fontFamily", "lineHeight", "textAlign", "letterSpacing",
    "marginTop", "marginRight", "marginBottom", "marginLeft", "paddingTop", "paddingRight", "paddingBottom", "paddingLeft",
    "borderRadius", "borderWidth", "borderColor", "width", "height", "display", "gap", "boxShadow", "opacity", "transform"];
  function styles(el) {
    const computed = getComputedStyle(el), result = {};
    for (const key of STYLE_KEYS) result[key] = computed[key];
    return result;
  }
  function label(el) {
    const name = el.getAttribute("aria-label") || el.getAttribute("alt") || el.getAttribute("title") || el.getAttribute("placeholder")
      || (el.value && typeof el.value === "string" ? el.value : "") || el.innerText || el.textContent || "";
    return name.replace(/\s+/g, " ").trim().slice(0, 160);
  }
  function role(el) {
    const tag = el.tagName.toLowerCase();
    return el.getAttribute("role") || ({ a: el.hasAttribute("href") ? "link" : "", button: "button", select: "combobox", textarea: "textbox",
      summary: "button", img: "img", nav: "navigation", h1: "heading", h2: "heading", h3: "heading" })[tag]
      || (tag === "input" ? ({ checkbox: "checkbox", radio: "radio", submit: "button", button: "button", range: "slider" })[el.type] || "textbox" : "");
  }
  function describe(el) {
    const rect = el.getBoundingClientRect();
    return {
      selector: selectorFor(el), tag: el.tagName.toLowerCase(), id: el.id || "", classes: [...el.classList].slice(0, 6),
      text: label(el), role: role(el), inputType: el.type || "", href: el.getAttribute("href") || "",
      disabled: !!el.disabled, password: el.type === "password", file: el.type === "file",
      box: { x: rect.left + scrollX, y: rect.top + scrollY, width: rect.width, height: rect.height },
      view: { x: rect.left, y: rect.top, width: rect.width, height: rect.height },
      viewport: { width: innerWidth, height: innerHeight }, page: location.pathname, styles: styles(el),
    };
  }

  function showHighlight(el) {
    ensure();
    if (!el) { highlight.style.display = hoverLabel.style.display = "none"; return; }
    const r = el.getBoundingClientRect();
    Object.assign(highlight.style, { display: "block", left: r.left + "px", top: r.top + "px", width: r.width + "px", height: r.height + "px" });
    const classes = [...el.classList].slice(0, 2).map(c => "." + c).join("");
    hoverLabel.textContent = el.tagName.toLowerCase() + classes + "  " + Math.round(r.width) + "×" + Math.round(r.height);
    Object.assign(hoverLabel.style, { display: "block", left: Math.max(2, r.left) + "px", top: Math.max(2, r.top - 20) + "px" });
  }

  function closePopover() { if (popover) { popover.remove(); popover = null; } }
  function askComment(x, y, title, done) {
    ensure(); closePopover();
    popover = document.createElement("div");
    popover.className = "pop";
    popover.innerHTML = `<div style="font-weight:600;margin-bottom:6px"></div><textarea placeholder="Was soll sich hier ändern?"></textarea>
      <div class="row"><button data-a="cancel">Abbrechen</button><button class="primary" data-a="ok">Hinzufügen</button></div>`;
    popover.firstElementChild.textContent = title;
    const left = Math.min(Math.max(8, x + 12), innerWidth - 296), top = Math.min(Math.max(8, y + 12), innerHeight - 160);
    Object.assign(popover.style, { left: left + "px", top: top + "px" });
    root.appendChild(popover);
    const area = popover.querySelector("textarea");
    const submit = () => { const text = area.value.trim(); closePopover(); if (text) done(text); };
    popover.addEventListener("click", e => { const a = e.target.dataset && e.target.dataset.a; if (a === "ok") submit(); if (a === "cancel") closePopover(); e.stopPropagation(); });
    popover.addEventListener("mousedown", e => e.stopPropagation());
    area.addEventListener("keydown", e => { if (e.key === "Enter" && (e.ctrlKey || !e.shiftKey)) { e.preventDefault(); submit(); } if (e.key === "Escape") closePopover(); e.stopPropagation(); });
    setTimeout(() => area.focus(), 0);
  }

  // Fine-tuning gizmo: drag the frame to move, the corners to scale, the top knob to rotate,
  // the inner dot to round the corners. Changes are preview styles only, never written to files.
  let gizmo = null, tuneEl = null, tune = null, gdrag = null;
  // The shadow root is closed, so composedPath() ends at the host. Hit-test inside it instead.
  const shadowHit = (e, node) => {
    if (!node || e.target !== host) return false;
    if (typeof e.clientX !== "number") return true; // keyboard events from a focused popover field
    const hit = root.elementFromPoint(e.clientX, e.clientY);
    return !!hit && node.contains(hit);
  };
  const insideGizmo = e => shadowHit(e, gizmo);
  const insidePopover = e => shadowHit(e, popover) || insideGizmo(e);
  function startTune(el) {
    // Only one element is fine-tuned: choosing another one returns the previous to its original look.
    if (tuneEl && tuneEl !== el) resetPreview();
    endTune(false);
    tuneEl = el;
    const cs = getComputedStyle(el), r = el.getBoundingClientRect();
    if (!previews.has(el)) previews.set(el, el.getAttribute("style"));
    const radius = parseFloat(cs.borderTopLeftRadius) || 0;
    tune = { dx: 0, dy: 0, rot: 0, w: r.width, h: r.height, w0: r.width, h0: r.height, radius, radius0: radius,
      base: cs.transform === "none" ? "" : cs.transform };
    ensure();
    gizmo = document.createElement("div");
    gizmo.className = "gz";
    gizmo.innerHTML = '<div class="stem"></div><div class="h rot" data-g="rot" title="Drehen"></div><div class="h nw" data-g="nw"></div><div class="h ne" data-g="ne"></div>'
      + '<div class="h sw" data-g="sw"></div><div class="h se" data-g="se" title="Skalieren"></div><div class="h rad" data-g="rad" title="Ecken abrunden"></div><div class="tag"></div>';
    gizmo.addEventListener("mousedown", e => {
      if (e.button !== 0) return;
      e.preventDefault(); e.stopPropagation();
      const r2 = tuneEl.getBoundingClientRect();
      gdrag = { type: e.target.dataset && e.target.dataset.g || "move", x0: e.clientX, y0: e.clientY, start: Object.assign({}, tune),
        cx: r2.left + r2.width / 2, cy: r2.top + r2.height / 2 };
    });
    root.appendChild(gizmo);
    drawGizmo();
  }
  function applyTune() {
    if (!tuneEl || !tune) return;
    const t = (tune.base ? tune.base + " " : "") + `translate(${Math.round(tune.dx)}px, ${Math.round(tune.dy)}px) rotate(${Math.round(tune.rot)}deg)`;
    const plain = tune.dx === 0 && tune.dy === 0 && tune.rot === 0;
    if (plain) tuneEl.style.removeProperty("transform"); else tuneEl.style.setProperty("transform", t, "important");
    const size = (name, now, before) => {
      if (Math.abs(now - before) > 0.5) tuneEl.style.setProperty(name, Math.round(now) + "px", "important");
      else tuneEl.style.removeProperty(name);
    };
    size("width", tune.w, tune.w0);
    size("height", tune.h, tune.h0);
    size("border-radius", tune.radius, tune.radius0);
    drawGizmo();
    schedule();
  }
  function tuneReport() {
    return { dx: Math.round(tune.dx), dy: Math.round(tune.dy), rotate: Math.round(tune.rot), width: Math.round(tune.w), height: Math.round(tune.h),
      width0: Math.round(tune.w0), height0: Math.round(tune.h0), radius: Math.round(tune.radius), radius0: Math.round(tune.radius0) };
  }
  function drawGizmo() {
    if (!gizmo || !tuneEl) return;
    const r = tuneEl.getBoundingClientRect();
    Object.assign(gizmo.style, { left: r.left + "px", top: r.top + "px", width: r.width + "px", height: r.height + "px" });
    gizmo.querySelector(".tag").textContent = `${Math.round(tune.w)}×${Math.round(tune.h)}` + (tune.rot ? ` · ${Math.round(tune.rot)}°` : "")
      + (tune.dx || tune.dy ? ` · ${Math.round(tune.dx)}/${Math.round(tune.dy)}` : "");
  }
  function moveGizmo(e) {
    if (!gdrag) return false;
    const s = gdrag.start, dx = e.clientX - gdrag.x0, dy = e.clientY - gdrag.y0;
    const g = gdrag.type;
    if (g === "move") { tune.dx = s.dx + dx; tune.dy = s.dy + dy; }
    else if (g === "rot") {
      let angle = Math.atan2(e.clientY - gdrag.cy, e.clientX - gdrag.cx) * 180 / Math.PI + 90;
      if (angle > 180) angle -= 360;
      tune.rot = e.shiftKey ? Math.round(angle / 15) * 15 : angle;
    }
    else if (g === "rad") tune.radius = Math.max(0, Math.min(Math.min(tune.w, tune.h) / 2, s.radius + dx));
    else {
      const sx = g.includes("e") ? 1 : -1, sy = g.includes("s") ? 1 : -1;
      let w = Math.max(8, s.w + sx * dx), h = Math.max(8, s.h + sy * dy);
      if (e.shiftKey) { const k = Math.max(w / s.w, h / s.h); w = s.w * k; h = s.h * k; }
      tune.w = w; tune.h = h;
    }
    applyTune();
    return true;
  }
  function endTune(report) {
    if (gizmo) { gizmo.remove(); gizmo = null; }
    if (report && tune) post("tune-change", tuneReport());
    tuneEl = null; tune = null; gdrag = null;
  }
  function onMove(e) {
    if (moveGizmo(e)) { e.preventDefault(); return; }
    if (mode === "none" || insidePopover(e)) return;
    if (mode === "region" && drag) {
      const x = Math.min(drag.x, e.clientX), y = Math.min(drag.y, e.clientY);
      Object.assign(regionBox.style, { display: "block", left: x + "px", top: y + "px", width: Math.abs(e.clientX - drag.x) + "px", height: Math.abs(e.clientY - drag.y) + "px" });
      return;
    }
    if (mode === "mark" || mode === "pin" || mode === "tune") { hoverEl = elementAt(e.clientX, e.clientY); showHighlight(hoverEl); }
  }
  function swallow(e) { e.preventDefault(); e.stopPropagation(); e.stopImmediatePropagation(); }
  function onDown(e) {
    if (mode === "none" || insidePopover(e) || e.button !== 0) return;
    swallow(e);
    if (mode === "region") { closePopover(); drag = { x: e.clientX, y: e.clientY }; }
  }
  function onUp(e) {
    if (gdrag) { gdrag = null; e.preventDefault(); e.stopPropagation(); if (tune) post("tune-change", tuneReport()); return; }
    if (mode === "none" || insidePopover(e) || e.button !== 0) return;
    swallow(e);
    if (mode === "region" && drag) {
      const box = { x: Math.min(drag.x, e.clientX), y: Math.min(drag.y, e.clientY), width: Math.abs(e.clientX - drag.x), height: Math.abs(e.clientY - drag.y) };
      drag = null;
      if (box.width < 8 || box.height < 8) { regionBox.style.display = "none"; return; }
      const center = elementAt(box.x + box.width / 2, box.y + box.height / 2);
      askComment(box.x + box.width, box.y, "Bereich kommentieren", text => {
        regionBox.style.display = "none";
        post("region", { comment: text, box: { x: box.x + scrollX, y: box.y + scrollY, width: box.width, height: box.height },
          view: box, viewport: { width: innerWidth, height: innerHeight }, page: location.pathname, element: center ? describe(center) : null });
      });
    }
  }
  function onClick(e) {
    if (mode === "none" || insidePopover(e)) return;
    swallow(e);
    const el = elementAt(e.clientX, e.clientY);
    if (!el) return;
    if (mode === "mark") {
      const info = describe(el);
      showHighlight(el);
      askComment(e.clientX, e.clientY, "Was soll sich an " + info.tag + (info.text ? " „" + info.text.slice(0, 30) + "“" : "") + " ändern?", text =>
        post("mark", Object.assign(info, { comment: text })));
    }
    else if (mode === "tune") { showHighlight(null); startTune(el); post("tune-select", describe(el)); }
    else if (mode === "pin") {
      const info = describe(el);
      askComment(e.clientX, e.clientY, "Kommentar zu " + info.tag + (info.text ? " „" + info.text.slice(0, 30) + "“" : ""), text =>
        post("pin", Object.assign(info, { comment: text, point: { x: e.clientX + scrollX, y: e.clientY + scrollY } })));
    }
  }
  function onKey(e) {
    if (e.key === "Escape" && mode !== "none" && !popover) { setMode("none"); post("mode", { mode: "none" }); }
  }
  for (const [type, handler] of [["mousemove", onMove], ["mousedown", onDown], ["mouseup", onUp], ["click", onClick], ["keydown", onKey]])
    window.addEventListener(type, handler, true);
  for (const type of ["dblclick", "contextmenu", "auxclick", "submit"])
    window.addEventListener(type, e => { if (mode !== "none" && !insidePopover(e)) swallow(e); }, true);

  function setMode(next) {
    ensure();
    mode = ["mark", "pin", "region", "tune"].includes(next) ? next : "none";
    document.documentElement.style.cursor = mode === "none" ? "" : (mode === "region" ? "crosshair" : "pointer");
    if (mode === "none") { showHighlight(null); closePopover(); regionBox.style.display = "none"; drag = null; }
    if (mode !== "tune") endTune(false);
  }

  function resolveRect(item) {
    if (item.page && item.page !== location.pathname) return null;
    if (item.selector) {
      try { const el = document.querySelector(item.selector); if (el) { const r = el.getBoundingClientRect(); if (r.width || r.height) return r; } } catch (_) { }
    }
    if (item.box) return { left: item.box.x - scrollX, top: item.box.y - scrollY, width: item.box.width, height: item.box.height };
    return null;
  }
  function draw() {
    raf = 0;
    if (!badges) return;
    badges.textContent = "";
    for (const item of annotations) {
      const r = resolveRect(item);
      if (!r) continue;
      const done = item.status === "done";
      if (item.kind === "region" || item.kind === "mark" || item.kind === "tune") {
        const frame = document.createElement("div");
        frame.className = "frame" + (item.kind === "region" ? " region" : "") + (done ? " done" : "");
        Object.assign(frame.style, { left: r.left + "px", top: r.top + "px", width: r.width + "px", height: r.height + "px", opacity: done ? ".6" : "1" });
        badges.appendChild(frame);
      }
      const badge = document.createElement("div");
      badge.className = "badge" + (done ? " done" : "") + (item.kind === "pin" ? " pin" : "");
      badge.textContent = (done ? "✓" : "") + item.number;
      badge.title = (item.comment || item.text || "").slice(0, 200);
      const px = item.kind === "pin" && item.point ? item.point.x - scrollX : r.left;
      const py = item.kind === "pin" && item.point ? item.point.y - scrollY : r.top;
      Object.assign(badge.style, { left: Math.max(12, px) + "px", top: Math.max(12, py) + "px" });
      badge.addEventListener("click", ev => { ev.stopPropagation(); post("badge", { number: item.number }); });
      badges.appendChild(badge);
    }
  }
  const schedule = () => { if (!raf) raf = requestAnimationFrame(() => { draw(); drawGizmo(); }); };
  addEventListener("scroll", schedule, true);
  addEventListener("resize", schedule);

  function flash(number) {
    const item = annotations.find(a => a.number === number);
    if (!item) return false;
    let el = null;
    try { el = item.selector ? document.querySelector(item.selector) : null; } catch (_) { }
    if (el) el.scrollIntoView({ behavior: "smooth", block: "center" });
    else if (item.box) scrollTo({ top: Math.max(0, item.box.y - innerHeight / 3), behavior: "smooth" });
    setTimeout(() => {
      const r = resolveRect(item);
      if (!r) return;
      ensure();
      const pulse = document.createElement("div");
      pulse.className = "pulse";
      Object.assign(pulse.style, { left: r.left - 4 + "px", top: r.top - 4 + "px", width: r.width + 8 + "px", height: r.height + 8 + "px" });
      root.appendChild(pulse);
      setTimeout(() => pulse.remove(), 2600);
    }, 450);
    return true;
  }

  function preview(selector, changes) {
    let el = null;
    try { el = document.querySelector(selector); } catch (_) { return false; }
    if (!el) return false;
    if (!previews.has(el)) previews.set(el, el.getAttribute("style"));
    for (const [property, value] of Object.entries(changes || {})) {
      const name = property.replace(/[A-Z]/g, m => "-" + m.toLowerCase());
      if (value === null || value === "") el.style.removeProperty(name); else el.style.setProperty(name, String(value), "important");
    }
    schedule();
    return true;
  }
  function resetPreview() {
    if (tune) { tune.dx = 0; tune.dy = 0; tune.rot = 0; tune.w = tune.w0; tune.h = tune.h0; tune.radius = tune.radius0; }
    for (const [el, original] of previews) { if (original === null) el.removeAttribute("style"); else el.setAttribute("style", original); }
    previews.clear();
    if (tuneEl && !previews.has(tuneEl)) previews.set(tuneEl, tuneEl.getAttribute("style"));
    schedule();
  }

  function interactive() {
    const result = [];
    const nodes = document.querySelectorAll("a[href],button,input,select,textarea,summary,label,[role=button],[role=link],[role=tab],[role=menuitem],[onclick],[tabindex]:not([tabindex='-1'])");
    for (const el of nodes) {
      if (result.length >= 120) break;
      const r = el.getBoundingClientRect();
      if (r.width < 2 || r.height < 2 || r.bottom < 0 || r.right < 0 || r.top > innerHeight || r.left > innerWidth) continue;
      const style = getComputedStyle(el);
      if (style.visibility === "hidden" || style.display === "none" || Number(style.opacity) === 0) continue;
      result.push({ role: role(el) || el.tagName.toLowerCase(), name: label(el).slice(0, 80), selector: selectorFor(el),
        x: Math.round(r.left), y: Math.round(r.top), width: Math.round(r.width), height: Math.round(r.height),
        disabled: !!el.disabled, inputType: el.type || undefined, tag: el.tagName.toLowerCase(),
        href: el.tagName === "A" ? (el.getAttribute("href") || undefined) : undefined,
        value: "value" in el && typeof el.value === "string" && el.tagName !== "BUTTON"
          ? (el.type === "password" ? (el.value ? "••••" : "") : el.value.slice(0, 80)) : undefined,
        checked: el.type === "checkbox" || el.type === "radio" ? !!el.checked : undefined });
    }
    return result;
  }

  function pointer(x, y) {
    ensure();
    Object.assign(aiPointer.style, { display: "block", left: x + "px", top: y + "px" });
    clearTimeout(pointer.timer);
    pointer.timer = setTimeout(() => { aiPointer.style.display = "none"; }, 1800);
  }

  function reloadCss() {
    for (const link of document.querySelectorAll('link[rel="stylesheet"]')) {
      const url = new URL(link.href, location.href); url.searchParams.set("missum", Date.now()); link.href = url.toString();
    }
  }

  window.__missum = Object.freeze({
    tuneSet(values) { if (!tune) return null; Object.assign(tune, values || {}); applyTune(); post("tune-change", tuneReport()); return tuneReport(); },
    tuneEnd() { endTune(false); },
    tuneRestore(state) {
      if (!tune) return null;
      const s = state || {};
      tune.dx = s.dx || 0; tune.dy = s.dy || 0; tune.rot = s.rot || 0;
      tune.w = s.w == null ? tune.w0 : s.w; tune.h = s.h == null ? tune.h0 : s.h; tune.radius = s.radius == null ? tune.radius0 : s.radius;
      applyTune();
      return tuneReport();
    },
    setMode, flash, preview, resetPreview, reloadCss, pointer,
    render(list) { annotations = Array.isArray(list) ? list : []; ensure(); schedule(); },
    inspect(selector) { let el = null; try { el = document.querySelector(selector); } catch (e) { return { error: String(e.message || e) }; } return el ? describe(el) : null; },
    elementAt(x, y) { const el = elementAt(x, y); return el ? describe(el) : null; },
    elements: interactive,
    active() { const el = document.activeElement; return el && el !== document.body ? describe(el) : null; },
    state() { return { url: location.href, scrollX, scrollY, width: innerWidth, height: innerHeight, title: document.title }; },
  });
  const ready = () => { ensure(); post("ready", { title: document.title }); };
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", ready, { once: true }); else ready();
})();
