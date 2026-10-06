const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const { TestNode } = require("./test-dom.cjs");
const source = fs.readFileSync(require.resolve("../../src/Missum.App/Assets/Web/browser-menubar.js"), "utf8");

function harness() {
  const documentEvents = new Map(), calls = [];
  let document;
  function emit(type, event) { for (const handler of documentEvents.get(type) || []) handler(event); }
  function create(tag) {
    const node = new TestNode(tag);
    node.focus = () => {
      document.activeElement = node;
      for (const handler of node.listeners.get("focus") || []) handler({ target: node });
      emit("focusin", { target: node });
    };
    node.click = () => node.dispatch("click");
    node.contains = target => { for (let value = target; value; value = value.parentNode) if (value === node) return true; return false; };
    node.getBoundingClientRect = () => ({ left: 10, top: 10, right: 100, bottom: 100 });
    return node;
  }
  const body = create("body");
  document = {
    body, activeElement: null, createElement: create,
    getElementById: id => [body, ...body.querySelectorAll("*")].find(node => node.id === id) || null,
    addEventListener: (type, callback) => { if (!documentEvents.has(type)) documentEvents.set(type, []); documentEvents.get(type).push(callback); },
  };
  const add = (tag, id) => { const item = create(tag); item.id = id; body.append(item); return item; };
  const shell = add("main", "app-shell");
  const settingsHook = add("button", "open-settings"); settingsHook.hidden = true;
  settingsHook.addEventListener("click", () => calls.push("settings.open"));
  const sidebar = add("button", "tabbar-sidebar-toggle"); sidebar.addEventListener("click", () => calls.push("sidebar.toggle"));
  const inspector = add("button", "inspector-toggle"); inspector.addEventListener("click", () => calls.push("inspector.toggle"));
  const prompt = add("textarea", "prompt"); prompt.value = "Draft survives navigation";
  const context = { document, missumSettings: { close: () => calls.push("settings.close/chat.open") },
    missumPanels: { setView: view => calls.push(`view.${view}`) } };
  vm.runInNewContext(source, context);
  const bar = document.getElementById("browser-menubar");
  const triggers = bar.querySelectorAll(".browser-menubar__trigger");
  const popups = bar.querySelectorAll(".browser-menubar__popup");
  const items = index => popups[index].querySelectorAll(".browser-menubar__item");
  const key = value => {
    let prevented = false;
    emit("keydown", { key: value, target: document.activeElement, preventDefault() { prevented = true; } });
    return prevented;
  };
  const click = async target => { await target.click(); emit("click", { target }); };
  return { document, bar, shell, triggers, popups, items, calls, prompt, key, click, emit, context,
    about: document.getElementById("browser-about") };
}

test("application bar exposes native menu names and routes settings through the retained hidden hook", async () => {
  const h = harness();
  assert.equal(h.document.body.children[0], h.bar);
  assert.equal(h.document.body.children[1], h.shell);
  assert.equal(h.document.body.classList.contains("has-browser-menubar"), true);
  assert.deepEqual(h.triggers.map(item => item.textContent), ["Datei", "Bearbeiten", "Ansicht", "Hilfe"]);
  assert.deepEqual(h.items(0).map(item => item.textContent), ["AI Assistent", "Einstellungen"]);
  assert.equal(h.bar.querySelectorAll('[role="separator"]').length, 1);
  assert.equal(h.bar.textContent.includes("Beenden"), false);
  await h.click(h.triggers[0]);
  await h.click(h.items(0)[1]);
  assert.deepEqual(h.calls, ["settings.open"]);
  assert.equal(h.popups[0].hidden, true);
  assert.equal(h.document.activeElement, h.triggers[0]);
});

test("compose returns through the existing settings close route and focuses the preserved draft", async () => {
  const h = harness();
  await h.click(h.triggers[1]); await h.click(h.items(1)[0]);
  assert.deepEqual(h.calls, ["settings.close/chat.open"]);
  assert.equal(h.document.activeElement, h.prompt);
  assert.equal(h.prompt.value, "Draft survives navigation");
  assert.equal(h.popups.every(item => item.hidden), true);
});

test("assistant and view actions use existing routes without introducing host commands", async () => {
  const h = harness();
  await h.click(h.triggers[0]); await h.click(h.items(0)[0]);
  await h.click(h.triggers[2]); await h.click(h.items(2)[0]);
  await h.click(h.triggers[2]); await h.click(h.items(2)[1]);
  assert.deepEqual(h.calls, ["settings.close/chat.open", "sidebar.toggle", "inspector.toggle"]);
  delete h.context.missumSettings;
  await h.click(h.triggers[0]); await h.click(h.items(0)[0]);
  assert.equal(h.calls.at(-1), "view.chat", "panels remain usable before settings initialization");
});

test("arrows, Home, End and Escape navigate menus and restore focus to the invoking trigger", () => {
  const h = harness(); h.triggers[0].focus();
  assert.equal(h.key("ArrowRight"), true); assert.equal(h.document.activeElement, h.triggers[1]);
  h.key("ArrowRight"); h.key("ArrowDown");
  assert.equal(h.document.activeElement, h.items(2)[0]);
  assert.equal(h.triggers[2].getAttribute("aria-expanded"), "true");
  h.key("ArrowDown"); assert.equal(h.document.activeElement, h.items(2)[1]);
  h.key("ArrowDown"); assert.equal(h.document.activeElement, h.items(2)[0]);
  h.key("End"); assert.equal(h.document.activeElement, h.items(2)[1]);
  h.key("Home"); assert.equal(h.document.activeElement, h.items(2)[0]);
  h.key("ArrowLeft"); assert.equal(h.document.activeElement, h.items(1)[0]);
  assert.equal(h.popups[2].hidden, true);
  h.key("Escape"); assert.equal(h.document.activeElement, h.triggers[1]);
  assert.equal(h.popups.every(item => item.hidden), true);
  assert.deepEqual(h.triggers.map(item => item.tabIndex), [-1, 0, -1, -1]);
  h.key("Home"); h.key("ArrowUp"); assert.equal(h.document.activeElement, h.items(0)[1]);
});

test("pointer menu switching, Tab and outside focus dismiss menus without stealing focus", async () => {
  const h = harness();
  await h.click(h.triggers[0]);
  await h.triggers[2].parentNode.dispatch("pointerenter");
  assert.equal(h.popups[0].hidden, true); assert.equal(h.popups[2].hidden, false);
  assert.equal(h.document.activeElement, h.triggers[2]);
  assert.equal(h.key("Tab"), false); assert.equal(h.popups[2].hidden, true);
  await h.click(h.triggers[0]); h.prompt.focus();
  assert.equal(h.popups[0].hidden, true); assert.equal(h.document.activeElement, h.prompt);
  await h.click(h.triggers[0]); h.emit("click", { target: h.prompt });
  assert.equal(h.popups[0].hidden, true);
});

test("about uses a modal named dialog, contains keyboard focus and returns to Help on dismissal", async () => {
  const h = harness();
  await h.click(h.triggers[3]); await h.click(h.items(3)[0]);
  const close = h.about.querySelector("button");
  assert.equal(h.about.open, true);
  assert.equal(h.about.getAttribute("aria-labelledby"), "browser-about-title");
  assert.equal(h.document.getElementById("browser-about-title").textContent, "Missum");
  assert.equal(h.document.activeElement, close);
  let prevented = false;
  for (const handler of h.about.listeners.get("keydown")) handler({ key: "Tab", shiftKey: true, preventDefault() { prevented = true; } });
  assert.equal(prevented, true); assert.equal(h.document.activeElement, close);
  assert.equal(h.key("ArrowRight"), false, "menubar navigation must not escape a modal dialog");
  await h.about.dispatch("cancel");
  assert.equal(h.about.open, false); assert.equal(h.document.activeElement, h.triggers[3]);
  await h.click(h.triggers[3]); await h.click(h.items(3)[0]); await close.click();
  assert.equal(h.about.open, false); assert.equal(h.document.activeElement, h.triggers[3]);
});

test("reinitializing the module does not duplicate the application bar or its dialog", () => {
  const h = harness(); vm.runInNewContext(source, h.context);
  assert.equal(h.document.body.querySelectorAll(".browser-menubar").length, 1);
  assert.equal(h.document.body.querySelectorAll(".browser-about").length, 1);
});
