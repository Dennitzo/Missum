const assert = require("node:assert/strict");
const test = require("node:test");
const fs = require("node:fs");
const vm = require("node:vm");
const { activeMilliseconds, contrastForeground, fallbackPalette } = require("../../src/Missum.App/Assets/Web/browser-appearance.js");
const sessionId = "00000001-0000-4000-8000-000000000001", messageId = "00000002-0000-4000-8000-000000000002";
const message = extra => ({ id: messageId, sessionId, createdAt: "2026-10-05T10:00:00Z", updatedAt: "2026-10-05T10:20:30Z", status: "completed", ...extra });
const receipt = extra => ({ tool: "assistant.continuation", status: "completed", startedAt: "2026-10-05T10:20:00Z", outputJson: JSON.stringify({ sessionId, messageId, localRunId: "00000003-0000-4000-8000-000000000003", priorActiveMilliseconds: 90000, ...extra }) });

test("native work duration excludes idle gaps between continuation attempts", () => {
  assert.equal(activeMilliseconds(message({ toolSteps: [receipt()] })), 120000);
  assert.equal(activeMilliseconds(message({ status: "streaming", toolSteps: [receipt()] }), Date.parse("2026-10-05T10:20:45Z")), 135000);
});
test("work duration ignores foreign, malformed and decreasing continuation receipts", () => {
  for (const extra of [{ sessionId: messageId }, { messageId: sessionId }, { localRunId: "invalid" }, { priorActiveMilliseconds: -1 }, { priorActiveMilliseconds: 1.5 }])
    assert.equal(activeMilliseconds(message({ toolSteps: [receipt(extra)] })), 1230000);
  assert.equal(activeMilliseconds(message({ toolSteps: [receipt(), { ...receipt({ priorActiveMilliseconds: 1 }), startedAt: "2026-10-05T10:10:00Z" }] })), 120000);
  assert.equal(activeMilliseconds(message({ createdAt: "invalid" })), 0);
});
test("native accent foreground stays readable for light and dark accents", () => {
  assert.equal(contrastForeground("#ffffff"), "#000"); assert.equal(contrastForeground("#000000"), "#fff"); assert.equal(contrastForeground("invalid"), "#fff");
});

function appearanceHarness() {
  const values = new Map(), priorities = new Map();
  const root = { dataset: {}, style: {
    setProperty: (name, value, priority = "") => { values.set(name, value); priorities.set(name, priority); },
    getPropertyValue: name => values.get(name) || "", getPropertyPriority: name => priorities.get(name) || "",
    removeProperty: name => { values.delete(name); priorities.delete(name); }
  } };
  const context = { document: { documentElement: root }, matchMedia: () => ({ matches: false }) };
  vm.runInNewContext(fs.readFileSync(require.resolve("../../src/Missum.App/Assets/Web/browser-appearance.js"), "utf8"), context);
  return { api: context.missumAppearance, root, values, priorities };
}

function contrast(a, b) {
  const luminance = value => {
    const rgb = [1,3,5].map(i => parseInt(value.slice(i,i+2),16) / 255).map(c => c <= .04045 ? c / 12.92 : ((c+.055)/1.055)**2.4);
    return rgb[0]*.2126 + rgb[1]*.7152 + rgb[2]*.0722;
  };
  const x=luminance(a),y=luminance(b); return (Math.max(x,y)+.05)/(Math.min(x,y)+.05);
}
test("light text and readable accent retain contrast without changing the user's accent or dark ink", () => {
  const h=appearanceHarness();
  h.api.preview({theme:"light",accentColor:"#A970FF",backgroundColor:"#181818"},"light");
  assert.equal(h.values.get("--accent"),"#A970FF");
  for (const ink of ["--text","--muted","--accent-readable","--link-readable"])
    for (const paper of ["--bg","--composer","--layer-strong"])
      assert.ok(contrast(h.values.get(ink),h.values.get(paper))>=4.5,`${ink} against ${paper}`);
  h.api.preview({theme:"dark",accentColor:"#A970FF",backgroundColor:"#181818"},"dark");
  assert.equal(h.values.get("--accent-readable"),"#A970FF");
});
test("an older PC's stale white light-mode text cannot make the web interface white on white", () => {
  const h=appearanceHarness();h.api.apply({values:{theme:"light"},resolvedAppearance:{theme:"light",colors:{window:"#FFFFFF",input:"#FFFFFF",layer:"#FFFFFF",layerStrong:"#FFFFFF",text:"#FFFFFF",mutedText:"#EEEEEE"}}});
  assert.ok(contrast(h.values.get("--text"),"#FFFFFF")>=4.5);
  assert.ok(contrast(h.values.get("--muted"),"#FFFFFF")>=4.5);
});

test("fallback palette follows native background weights, alpha and midpoint-to-even rounding", () => {
  const dark = fallbackPalette({ theme: "dark", accentColor: "#B0B0B0", backgroundColor: "#181818" });
  assert.equal(dark.colors.window, "#1f1f1f");
  assert.equal(dark.colors.layer, "#1f1f1fe6");
  assert.equal(dark.colors.layerStrong, "#282828f2");
  assert.equal(dark.colors.input, "#2e2e2e");
  assert.equal(dark.colors.stroke, "#bebebe42");
  assert.equal(dark.colors.accentSubtle, "#B0B0B024");
  const light = fallbackPalette({ theme: "light", backgroundColor: "#181818" });
  assert.equal(light.colors.window, "#e4e1e6"); assert.equal(light.colors.layer, "#f6f6f6f8");
  assert.equal(light.colors.layerStrong, "#e8e6ea");
  assert.equal(fallbackPalette({ theme: "light", backgroundColor: "#050505" }).colors.input, "#e8e8e8", ".NET rounds 232.5 to even 232");
});

test("server captured colors take priority over raw preferences and client OS theme while preserving native brush opacity", () => {
  const h = appearanceHarness();
  h.api.apply({ values: { theme: "system", accentColor: "#111111", backgroundColor: "#181818" }, resolvedTheme: "light",
    resolvedAppearance: { theme: "dark", highContrast: false, colors: {
      window: "#214365", layer: "#314159E6", layerStrong: "#415161F2", input: "#516171", stroke: "#AABBCC42",
      accent: "#A970FF", accentForeground: "#000000", accentSubtle: "#A970FF24", titlebar: "#334455", text: "#FFFFFF", mutedText: "#999999"
    } } });
  assert.equal(h.root.dataset.theme, "dark");
  assert.equal(h.values.get("--bg"), "#214365"); assert.equal(h.values.get("--surface"), "#314159E6");
  assert.equal(h.values.get("--surface-solid"), "#314159"); assert.equal(h.values.get("--layer-strong"), "#415161F2");
  assert.equal(h.values.get("--border"), "#AABBCC42"); assert.equal(h.values.get("--accent-subtle"), "#A970FF24");
  assert.equal(h.values.get("--accent"), "#A970FF"); assert.equal(h.values.get("--titlebar"), "#334455");
  assert.equal(h.values.get("--accent-contrast"), "#000000");
});

test("headless System theme uses the server resolution and default settings rather than browser media queries", () => {
  const h = appearanceHarness();
  h.api.apply({ values: { theme: "system" }, resolvedTheme: "light" });
  assert.equal(h.root.dataset.theme, "light"); assert.equal(h.values.get("--accent"), "#B0B0B0");
  assert.equal(h.values.get("--bg"), "#e4e1e6");
  h.api.apply({ values: { theme: "system" }, resolvedTheme: "dark" });
  assert.equal(h.root.dataset.theme, "dark"); assert.equal(h.values.get("--bg"), "#1f1f1f");
});

test("PC high contrast uses its captured custom colors and foreground instead of Mac system Canvas values", () => {
  const h = appearanceHarness(); const actual = { theme: "high-contrast", highContrast: true, colors: {
    window: "#182838", layer: "#182838", layerStrong: "#182838", input: "#182838", hover: "#182838", pressed: "#182838",
    text: "#F0E0D0", mutedText: "#F0E0D0", stroke: "#F0E0D0", accent: "#AABBCC", accentForeground: "#112233", accentSubtle: "#AABBCC24"
  } };
  h.api.apply({ values: { theme: "dark" }, resolvedAppearance: actual });
  assert.equal(h.root.dataset.theme, "high-contrast"); assert.equal(h.values.get("--text"), "#F0E0D0");
  assert.equal(h.values.get("--accent"), "#AABBCC"); assert.equal(h.priorities.get("--accent"), "important");
  h.api.preview({ theme: "light", accentColor: "#FFFFFF" }, "dark", actual);
  assert.equal(h.values.get("--accent"), "#AABBCC", "native accessibility colors also override temporary settings previews");
});

test("preview and cancellation restore every native palette property, alpha, priority and theme", () => {
  const h = appearanceHarness(); const snapshot = { values: { theme: "dark", accentColor: "#A970FF", backgroundColor: "#6B6872" },
    resolvedAppearance: { theme: "dark", highContrast: false, colors: { window: "#262627", accentSubtle: "#A970FF24", layerStrong: "#373638F2" } } };
  h.api.apply(snapshot); const original = h.api.capture(); const expected = new Map(h.values);
  h.api.preview({ theme: "light", accentColor: "#FFFFFF", backgroundColor: "#D95BA8" }, "dark");
  assert.equal(h.root.dataset.theme, "light"); assert.equal(h.values.get("--accent-contrast"), "#000000");
  assert.notEqual(h.values.get("--composer"), expected.get("--composer"));
  h.api.restore(original);
  assert.equal(h.root.dataset.theme, "dark"); assert.deepEqual(h.values, expected);
  h.api.restore({ theme: "light", properties: {} }); assert.equal(h.values.size, 0);
});

test("invalid server colors cannot inject CSS and native icon colors are exposed consistently", () => {
  const h = appearanceHarness(); h.api.apply({ values: { theme: "dark" }, resolvedAppearance: { theme: "dark", colors: {
    window: "url(https://example.invalid/style)", accent: "red; background:white", iconResearch: "#A07CF6"
  } } });
  assert.equal(h.values.get("--bg"), "#1f1f1f"); assert.equal(h.values.get("--accent"), "#B0B0B0");
  assert.equal(h.values.get("--icon-research"), "#A07CF6"); assert.equal(h.values.get("--icon-web"), "#4c94f2");
});
