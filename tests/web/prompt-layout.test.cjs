const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

const source = fs.readFileSync(path.resolve(__dirname, "../../src/Missum.App/Assets/Web/app.js"), "utf8");
function harness() {
  const layout = { paneHeight: 800, overhead: 112, contentHeight: 58, minimum: 58 };
  const prompt = { value: "", clientWidth: 790, scrollTop: 0, style: { height: "58px" },
    getBoundingClientRect() { return { height: parseFloat(this.style.height) }; },
    get scrollHeight() { return layout.contentHeight; } };
  const elements = { prompt, chatPane: { getBoundingClientRect: () => ({ top: 20, bottom: layout.paneHeight + 20, height: layout.paneHeight }) },
    composerRegion: { getBoundingClientRect: () => ({ height: parseFloat(prompt.style.height) + layout.overhead }) } };
  const frames = [];
  const context = vm.createContext({ elements, innerHeight: 1000, getComputedStyle: () => ({ minHeight: `${layout.minimum}px` }),
    renderComposerAction() {},
    requestAnimationFrame: fn => { frames.push(fn); return frames.length; } });
  vm.runInContext("let promptResizeFrame = 0;", context);
  for (const name of ["resizePrompt", "schedulePromptResize", "setPromptValue"]) {
    const start = source.indexOf(`  function ${name}(`);
    vm.runInContext(source.slice(start, source.indexOf("\n  function ", start + 1)), context);
  }
  return { context, layout, prompt, frames };
}

test("draft grows to show all lines, then scrolls only within the available window", () => {
  const { context, layout, prompt } = harness();
  context.resizePrompt();
  assert.equal(prompt.style.height, "58px");
  prompt.value = "A draft with several lines";
  layout.contentHeight = 420;
  context.resizePrompt();
  assert.equal(prompt.style.height, "420px");
  assert.equal(prompt.style.overflowY, "hidden");
  layout.contentHeight = 1400;
  prompt.scrollTop = 200;
  context.resizePrompt();
  assert.equal(prompt.style.height, "676px");
  assert.equal(prompt.style.overflowY, "auto");
  assert.equal(prompt.scrollTop, 200, "resizing a long draft retains its reading position");
  layout.contentHeight = 58;
  context.resizePrompt();
  assert.equal(prompt.style.height, "58px");
  assert.equal(prompt.style.overflowY, "hidden");
  assert.equal(prompt.scrollTop, 0);
});

test("window resize, wrapped controls and the visual viewport reserve actual usable space", () => {
  const { context, layout, prompt } = harness();
  prompt.value = "A long draft";
  layout.contentHeight = 1600;
  context.resizePrompt();
  layout.paneHeight = 500;
  layout.overhead = 148;
  context.resizePrompt();
  assert.equal(prompt.style.height, "340px");
  context.visualViewport = { offsetTop: 0, height: 430 };
  context.resizePrompt();
  assert.equal(prompt.style.height, "250px");
  layout.paneHeight = 1000;
  delete context.visualViewport;
  layout.overhead = 112;
  context.resizePrompt();
  assert.equal(prompt.style.height, "856px");
});

test("an empty native composer ignores a stale intrinsic textarea height after changing views", () => {
  const { context, layout, prompt } = harness();
  layout.minimum = 48;
  layout.contentHeight = 58;
  context.resizePrompt();
  assert.equal(prompt.style.height, "48px");
  prompt.value = "A real multiline draft";
  layout.contentHeight = 280;
  context.resizePrompt();
  assert.equal(prompt.style.height, "280px");
  context.setPromptValue("");
  layout.contentHeight = 58;
  context.resizePrompt();
  assert.equal(prompt.style.height, "48px");
});

test("restoring and clearing programmatic drafts coalesces layout work and respects new wrapping", () => {
  const { context, layout, prompt, frames } = harness();
  context.setPromptValue("A restored multiline draft");
  context.schedulePromptResize();
  assert.equal(frames.length, 1);
  layout.contentHeight = 300;
  frames.shift()();
  assert.equal(prompt.value, "A restored multiline draft");
  assert.equal(prompt.style.height, "300px");
  context.setPromptValue("");
  layout.contentHeight = 58;
  frames.shift()();
  assert.equal(prompt.style.height, "58px");
});

const webRoot = path.resolve(__dirname, "../../src/Missum.App/Assets/Web");
const baseCss = fs.readFileSync(path.join(webRoot, "styles.css"), "utf8");
const browserCss = fs.readFileSync(path.join(webRoot, "browser-panels.css"), "utf8");

function simpleCascade(tag, id, classes) {
  const values = new Map(); let order = 0;
  for (const css of [baseCss, browserCss]) {
    for (const rule of css.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
      for (const raw of rule[1].split(",")) {
        const selector = raw.trim();
        if (!/^(?:[a-z][\w-]*)?(?:[.#][\w-]+)*$/i.test(selector)) continue;
        const expectedTag = selector.match(/^[a-z][\w-]*/i)?.[0];
        if (expectedTag && expectedTag !== tag) continue;
        if ([...selector.matchAll(/#([\w-]+)/g)].some(match => match[1] !== id)) continue;
        if ([...selector.matchAll(/\.([\w-]+)/g)].some(match => !classes.includes(match[1]))) continue;
        const specificity = (selector.match(/#/g) || []).length * 100 + (selector.match(/\./g) || []).length * 10 + Boolean(expectedTag);
        for (const declaration of rule[2].split(";")) {
          const colon = declaration.indexOf(":"); if (colon < 0) continue;
          const name = declaration.slice(0, colon).trim(), value = declaration.slice(colon + 1).trim();
          const previous = values.get(name);
          if (!previous || specificity > previous.specificity || specificity === previous.specificity && order >= previous.order)
            values.set(name, { value, specificity, order });
        }
        order++;
      }
    }
  }
  return Object.fromEntries([...values].map(([name, entry]) => [name, entry.value]));
}

function browserRule(selector) {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const matches = [...browserCss.matchAll(new RegExp(`^${escaped} \\{([^}]+)\\}`, "gm"))];
  assert.ok(matches.length, `browser rule ${selector} exists`);
  return Object.fromEntries(matches.flatMap(match => match[1].split(";").filter(value => value.includes(":"))
    .map(value => { const colon = value.indexOf(":"); return [value.slice(0, colon).trim(), value.slice(colon + 1).trim()]; })));
}

test("native footer circles override the inherited fifty pixel flex basis as well as their visible dimensions", () => {
  assert.match(baseCss, /\.send-button, \.microphone-button\s*\{[^}]*flex:\s*0 0 50px;/);
  for (const [id, className] of [["send", "send-button"], ["microphone", "microphone-button"]]) {
    const resolved = simpleCascade("button", id, [className]);
    assert.equal(resolved.flex, "0 0 36px", `${id} reserves exactly its native circle width in the flex row`);
    for (const property of ["width", "min-width", "max-width", "height", "min-height", "max-height"]) assert.equal(resolved[property], "36px", `${id} ${property}`);
    assert.equal(resolved.padding, "0");
  }
  const stopped = simpleCascade("button", "send", ["send-button", "send-button--stop"]);
  assert.equal(stopped.flex, "0 0 36px"); assert.equal(stopped.width, "36px");
});

test("a long model and reasoning label can shrink through every nested flex item without pushing send outside", () => {
  const submit = browserRule(".composer-submit"), anchor = browserRule(".composer-submit > .reasoning-menu-anchor");
  assert.equal(submit.flex, "0 1 auto"); assert.equal(submit["min-width"], "0"); assert.match(submit["max-width"], /100%/);
  assert.equal(anchor.flex, "0 1 auto"); assert.equal(anchor["min-width"], "0"); assert.match(anchor["max-width"], /100%/);
  const pill = browserRule(".model-reasoning-button"); assert.equal(pill["min-width"], "0"); assert.match(pill["max-width"], /100%/);
  for (const id of ["selected-model-label", "selected-reasoning-label"]) {
    const span = simpleCascade("span", id, []); assert.equal(span["min-width"], "0"); assert.match(span.flex, /^\d+ 1 auto$/);
    assert.equal(span.overflow, "hidden"); assert.equal(span["text-overflow"], "ellipsis");
  }
  assert.equal(browserRule(".model-reasoning-button > svg").flex, "0 0 10px");
});

test("footer containment preserves native composer padding and reserves the plus button without wrapping or changing order", () => {
  const composer = simpleCascade("div", "", ["composer"]), toolbar = browserRule(".composer-toolbar"), tools = browserRule(".composer-tools");
  assert.equal(composer.padding, "10px 10px 9px 12px");
  assert.equal(toolbar.height, "36px"); assert.equal(toolbar["margin-top"], "9px");
  assert.equal(tools["flex-wrap"], "nowrap"); assert.equal(tools["min-width"], "46px");
  assert.equal(browserRule(".composer-submit > .context-meter").flex, "0 0 24px");
  assert.equal(browserRule(".conversation-pane, .app-shell.science-mode .conversation-pane")["grid-template-columns"], "minmax(0, 1fr)",
    "the composer cannot widen an implicit grid track beyond a narrow viewport");
  const html = fs.readFileSync(path.join(webRoot, "index.html"), "utf8");
  const ids = ["tools-button", "active-tool-chips", "context-meter", "reasoning-button", "microphone", "send"].map(id => html.indexOf(`id="${id}"`));
  assert.ok(ids.every(index => index >= 0)); assert.deepEqual(ids, [...ids].sort((left, right) => left - right));
});
