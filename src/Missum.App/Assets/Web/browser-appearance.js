(function () {
  "use strict";
  // Match the native accent foreground selection, including light accent colors.
  function contrastForeground(color) {
    if (!/^#[\da-f]{6}$/i.test(color || "")) return "#fff";
    const linear = value => { const channel = value / 255; return channel <= 0.03928 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4; };
    const channels = [1, 3, 5].map(offset => linear(parseInt(color.slice(offset, offset + 2), 16)));
    const luminance = channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722;
    return (luminance + 0.05) / 0.05 >= 1.05 / (luminance + 0.05) ? "#000" : "#fff";
  }
  function setAccent(color) {
    if (!/^#[\da-f]{6}$/i.test(color || "")) return;
    document.documentElement.style.setProperty("--accent", color);
    document.documentElement.style.setProperty("--accent-contrast", contrastForeground(color));
  }
  const colorKeys = {
    window: ["--bg"], layer: ["--surface", "--sidebar"], layerStrong: ["--layer-strong", "--surface-raised", "--user-bubble"],
    input: ["--composer"], hover: ["--surface-hover"], pressed: ["--surface-pressed"], stroke: ["--border"],
    mutedText: ["--muted"], text: ["--text"], accent: ["--accent"], accentForeground: ["--accent-contrast", "--accent-ink"],
    accentSubtle: ["--accent-subtle"], titlebar: ["--titlebar"], accentReadable: ["--accent-readable"]
  };
  const icons = { Web: "#4c94f2", Research: "#a07cf6", Image: "#e768ab", Audio: "#46bc85", Speech: "#33b8cf",
    Pdf: "#da5052", Document: "#378fea", Plan: "#f19d38", Code: "#9774e1", Folder: "#f19d38", Navigation: "#5b9cf6",
    Link: "#5b9cf6", Add: "#46bc85", Danger: "#da5052", Settings: "#9774e1", Subagent: "#a07cf6" };
  for (const name of Object.keys(icons)) colorKeys[`icon${name}`] = [`--icon-${name.toLowerCase()}`];
  const properties = [...new Set([...Object.values(colorKeys).flat(), "--background-accent", "--surface-solid", "--stroke-color",
    "--link-readable", "--success-readable", "--danger-readable", "--warning-readable"])];
  const validColor = color => /^#[\da-f]{6}(?:[\da-f]{2})?$/i.test(color || "");
  const rgb = color => [1, 3, 5].map(index => parseInt(color.slice(index, index + 2), 16));
  const hex = values => `#${values.map(value => value.toString(16).padStart(2, "0")).join("")}`;
  // .NET Math.Round uses midpoint-to-even; CSS color-mix has fractional channels.
  function roundNative(value) {
    const lower = Math.floor(value), fraction = value - lower;
    return Math.abs(fraction - 0.5) < 1e-10 ? lower + (lower % 2) : Math.round(value);
  }
  function mix(base, tint, weight, alpha = 255) {
    const source = rgb(base), target = rgb(tint);
    const channels = source.map((value, index) => roundNative(value * (1 - weight) + target[index] * weight));
    if (alpha !== 255) channels.push(alpha);
    return hex(channels);
  }
  function composite(foreground, background) {
    const alpha = foreground.length === 9 ? parseInt(foreground.slice(7), 16) / 255 : 1;
    const source = rgb(foreground), target = rgb(background);
    return hex(source.map((value, index) => roundNative(value * alpha + target[index] * (1 - alpha))));
  }
  function relativeLuminance(color) {
    const linear = rgb(color).map(value => { const channel = value / 255; return channel <= .04045 ? channel / 12.92 : ((channel + .055) / 1.055) ** 2.4; });
    return linear[0] * .2126 + linear[1] * .7152 + linear[2] * .0722;
  }
  function readableColor(color, backgrounds, minimum = 4.5) {
    const luminances = backgrounds.map(relativeLuminance);
    const ratio = (first, second) => (Math.max(first, second) + .05) / (Math.min(first, second) + .05);
    const accepts = candidate => luminances.every(background => ratio(relativeLuminance(candidate), background) >= minimum);
    if (accepts(color)) return color;
    const destination = Math.min(...luminances.map(background => ratio(0, background))) >= Math.min(...luminances.map(background => ratio(1, background))) ? "#000000" : "#ffffff";
    let low = 0, high = 1;
    for (let attempt = 0; attempt < 24; attempt++) {
      const middle = (low + high) / 2;
      if (accepts(mix(color, destination, middle))) high = middle; else low = middle;
    }
    return mix(color, destination, high);
  }
  function applyReadableColors(palette) {
    const colors = palette.colors;
    const background = colors.window, surfaces = [background, colors.layer.slice(0,7), colors.layerStrong.slice(0,7), colors.input, colors.hover, colors.pressed];
    if (!validColor(colors.accentReadable)) colors.accentReadable = palette.theme === "light" ? readableColor(colors.accent, surfaces) : colors.accent;
    const root = document.documentElement;
    root.style.setProperty("--accent-readable", colors.accentReadable, palette.highContrast ? "important" : "");
    for (const [name, color] of Object.entries({ link: colors.iconLink, success: "#087a52", danger: "#c62843", warning: "#c77c16" }))
      root.style.setProperty(`--${name}-readable`, palette.theme === "light" ? readableColor(color, surfaces) : color);
  }
  function correctLightText(palette) {
    if (palette.theme !== "light") return;
    const colors = palette.colors, background = colors.window;
    const surfaces = [background, composite(colors.layer, background), composite(colors.layerStrong, background), colors.input,
      composite(colors.accentSubtle, background)];
    for (const name of ["text", "mutedText"]) {
      const readable = surfaces.every(surface => {
        const ink = relativeLuminance(composite(colors[name], surface)), paper = relativeLuminance(surface);
        return (Math.max(ink, paper) + .05) / (Math.min(ink, paper) + .05) >= 4.5;
      });
      if (!readable) colors[name] = readableColor(colors[name], surfaces);
    }
  }
  function fallbackPalette(values = {}, resolvedTheme = "dark") {
    const theme = String(values.theme || "dark").toLowerCase() === "system" ? String(resolvedTheme || "dark").toLowerCase() : String(values.theme || "dark").toLowerCase();
    const accent = /^#[\da-f]{6}$/i.test(values.accentColor || "") ? values.accentColor : "#B0B0B0";
    const background = /^#[\da-f]{6}$/i.test(values.backgroundColor || "") ? values.backgroundColor : "#181818";
    const light = theme === "light";
    const colors = {
      window: mix(light ? "#f3f0f5" : "#202020", background, light ? .07 : .08),
      layer: mix(light ? "#ffffff" : "#202020", background, light ? .04 : .12, light ? 248 : 230),
      layerStrong: mix(light ? "#faf8fc" : "#2b2b2b", background, light ? .08 : .18, light ? 255 : 242),
      input: mix(light ? "#ffffff" : "#333333", background, light ? .09 : .20),
      hover: mix(light ? "#f5f1f7" : "#303030", background, light ? .13 : .23),
      pressed: mix(light ? "#eee9f1" : "#383838", background, light ? .18 : .28),
      stroke: mix(light ? "#302a38" : "#ffffff", background, light ? .22 : .28, light ? 82 : 66),
      mutedText: light ? "#51465d" : "#999999", text: light ? "#000000e4" : "#ffffff",
      accent, accentForeground: contrastForeground(accent) === "#000" ? "#000000" : "#ffffff",
      accentSubtle: `${accent}${roundNative(255 * .14).toString(16).padStart(2, "0")}`
    };
    if (theme === "high-contrast") {
      for (const key of ["window", "layer", "layerStrong", "input", "hover", "pressed"]) colors[key] = "#000000";
      for (const key of ["stroke", "mutedText", "text"]) colors[key] = "#ffffff";
      colors.accent = "#ffff00"; colors.accentForeground = "#000000"; colors.accentSubtle = "#ffff0024";
    }
    colors.titlebar = composite(colors.accentSubtle, colors.window);
    for (const [name, value] of Object.entries(icons)) colors[`icon${name}`] = value;
    return { theme, highContrast: theme === "high-contrast", colors, background };
  }
  function writePalette(palette) {
    correctLightText(palette);
    const root = document.documentElement;
    root.dataset.theme = palette.theme;
    root.style.colorScheme = palette.highContrast ? "light dark" : palette.theme;
    for (const [key, names] of Object.entries(colorKeys)) {
      if (!validColor(palette.colors[key])) continue;
      for (const name of names) root.style.setProperty(name, palette.colors[key], palette.highContrast ? "important" : "");
    }
    root.style.setProperty("--surface-solid", palette.colors.layer.slice(0, 7), palette.highContrast ? "important" : "");
    root.style.setProperty("--stroke-color", palette.colors.stroke.slice(0, 7), palette.highContrast ? "important" : "");
    root.style.setProperty("--background-accent", palette.background, palette.highContrast ? "important" : "");
    applyReadableColors(palette);
  }
  function apply(snapshot = {}) {
    const actual = snapshot.resolvedAppearance;
    const resolvedTheme = actual?.highContrast ? "high-contrast" : actual?.theme || snapshot.resolvedTheme || "dark";
    const palette = fallbackPalette({ ...snapshot.values, theme: actual?.theme || snapshot.values?.theme }, resolvedTheme);
    if (actual?.highContrast) { palette.theme = "high-contrast"; palette.highContrast = true; }
    for (const [key, value] of Object.entries(actual?.colors || {})) if (validColor(value) && key in colorKeys) palette.colors[key] = value;
    if (!validColor(actual?.colors?.titlebar)) palette.colors.titlebar = composite(palette.colors.accentSubtle, palette.colors.window);
    writePalette(palette);
    return palette;
  }
  function preview(values, resolvedTheme, actual) {
    if (actual?.highContrast) return apply({ values, resolvedTheme, resolvedAppearance: actual });
    const palette = fallbackPalette(values, resolvedTheme); writePalette(palette); return palette;
  }
  function capture() {
    const root = document.documentElement;
    return { theme: root.dataset.theme, colorScheme: root.style.colorScheme, properties: Object.fromEntries(properties.map(name => [name,
      { value: root.style.getPropertyValue(name), priority: root.style.getPropertyPriority(name) }])) };
  }
  function restore(saved) {
    if (!saved) return;
    const root = document.documentElement; root.dataset.theme = saved.theme || "dark"; root.style.colorScheme = saved.colorScheme || "";
    for (const name of properties) {
      const item = saved.properties?.[name];
      if (item?.value) root.style.setProperty(name, item.value, item.priority || ""); else root.style.removeProperty(name);
    }
  }
  function activeMilliseconds(message, now = Date.now()) {
    const created = Date.parse(message.createdAt || "");
    if (!Number.isFinite(created)) return 0;
    const active = ["pending", "streaming"].includes(String(message.status).toLowerCase());
    const finished = Date.parse(message.updatedAt || "");
    const end = active ? now : Number.isFinite(finished) ? finished : created;
    let start = created, prior = 0;
    const guid = value => /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(value || "") && !/^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(value);
    for (const step of message.toolSteps || []) {
      if (step.tool !== "assistant.continuation" || !["running", "completed"].includes(step.status)) continue;
      const receiptStart = Date.parse(step.startedAt || "");
      if (!Number.isFinite(receiptStart) || receiptStart < start) continue;
      let receipt; try { receipt = JSON.parse(step.outputJson || ""); } catch { continue; }
      if (!guid(receipt?.localRunId) || !guid(receipt?.sessionId) || !guid(receipt?.messageId)
        || String(receipt.sessionId).toLowerCase() !== String(message.sessionId).toLowerCase()
        || String(receipt.messageId).toLowerCase() !== String(message.id).toLowerCase()
        || !Number.isSafeInteger(receipt.priorActiveMilliseconds) || receipt.priorActiveMilliseconds < 0) continue;
      start = receiptStart; prior = receipt.priorActiveMilliseconds;
    }
    return prior + Math.max(0, end - start);
  }
  const api = { setAccent, contrastForeground, activeMilliseconds, apply, preview, capture, restore, fallbackPalette };
  globalThis.missumAppearance = Object.freeze(api);
  if (typeof module === "object") module.exports = api;
})();
