(function () {
  "use strict";
  const shell = document.getElementById("app-shell");
  if (!shell || document.getElementById("browser-menubar")) return;
  const byId = id => document.getElementById(id);
  function node(tag, className, text) {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (text !== undefined) element.textContent = text;
    return element;
  }
  function openAssistant() {
    if (globalThis.missumSettings?.close) globalThis.missumSettings.close();
    else globalThis.missumPanels?.setView("chat");
  }
  const bar = node("nav", "browser-menubar");
  bar.id = "browser-menubar";
  bar.setAttribute("aria-label", "Missum-Anwendungsmenü");
  bar.append(node("span", "browser-menubar__brand", "Missum"));
  const menuList = node("div", "browser-menubar__menus");
  menuList.setAttribute("role", "menubar");
  menuList.setAttribute("aria-label", "Anwendung");
  bar.append(menuList);

  const about = node("dialog", "browser-about");
  about.id = "browser-about";
  about.setAttribute("aria-labelledby", "browser-about-title");
  about.setAttribute("aria-describedby", "browser-about-description");
  const title = node("h2", "", "Missum"); title.id = "browser-about-title";
  const description = node("p", "", "Lokaler AI-Arbeitsbereich. ChatGPT, Codex und Claude Science mit den Modellen, Werkzeugen und Daten auf deinem Missum-PC.");
  description.id = "browser-about-description";
  const aboutFooter = node("footer", "browser-about__footer");
  const aboutClose = node("button", "browser-about__close", "Schließen");
  aboutClose.type = "button";
  aboutFooter.append(aboutClose); about.append(title, description, aboutFooter);
  let aboutReturnFocus = null;
  function showAbout() {
    if (about.open) return;
    aboutReturnFocus = document.activeElement;
    about.showModal();
    aboutClose.focus();
  }
  aboutClose.addEventListener("click", () => about.close());
  about.addEventListener("cancel", event => { event.preventDefault(); about.close(); });
  about.addEventListener("close", () => { aboutReturnFocus?.focus(); aboutReturnFocus = null; });
  about.addEventListener("keydown", event => {
    if (event.key === "Tab") { event.preventDefault(); aboutClose.focus(); }
  });
  about.addEventListener("click", event => {
    if (event.target !== about) return;
    const bounds = about.getBoundingClientRect();
    if (event.clientX < bounds.left || event.clientX > bounds.right || event.clientY < bounds.top || event.clientY > bounds.bottom) about.close();
  });

  const definitions = [
    { label: "Datei", items: [
      { label: "AI Assistent", action: openAssistant },
      null,
      { label: "Einstellungen", action: () => byId("open-settings")?.click() },
    ] },
    { label: "Bearbeiten", items: [{ label: "Nachricht schreiben", action: () => { openAssistant(); byId("prompt")?.focus(); } }] },
    { label: "Ansicht", items: [
      { label: "Seitenleiste umschalten", action: () => byId("tabbar-sidebar-toggle")?.click() },
      { label: "Ausgaben und Quellen", action: () => byId("inspector-toggle")?.click() },
    ] },
    { label: "Hilfe", items: [{ label: "Über Missum", action: showAbout }] },
  ];
  const menus = [];
  let activeMenu = -1;
  function setTabStop(index) { menus.forEach((entry, i) => { entry.trigger.tabIndex = i === index ? 0 : -1; }); }
  function closeMenu(restoreFocus = false) {
    const previous = activeMenu;
    activeMenu = -1;
    menus.forEach(entry => { entry.popup.hidden = true; entry.trigger.setAttribute("aria-expanded", "false"); });
    if (restoreFocus && previous >= 0) menus[previous].trigger.focus();
  }
  function openMenu(index, edge = "first") {
    closeMenu(); activeMenu = index; setTabStop(index);
    const entry = menus[index]; entry.popup.hidden = false; entry.trigger.setAttribute("aria-expanded", "true");
    const target = edge === "last" ? entry.items.at(-1) : edge === "trigger" ? entry.trigger : entry.items[0];
    target?.focus();
  }
  definitions.forEach((definition, index) => {
    const group = node("div", "browser-menubar__group"); group.setAttribute("role", "none");
    const trigger = node("button", "browser-menubar__trigger", definition.label);
    trigger.id = `browser-menu-${index}`; trigger.type = "button";
    trigger.setAttribute("role", "menuitem"); trigger.setAttribute("aria-haspopup", "menu");
    trigger.setAttribute("aria-expanded", "false"); trigger.setAttribute("aria-controls", `browser-menu-popup-${index}`);
    trigger.tabIndex = index === 0 ? 0 : -1;
    const popup = node("div", "browser-menubar__popup"); popup.id = `browser-menu-popup-${index}`; popup.hidden = true;
    popup.setAttribute("role", "menu"); popup.setAttribute("aria-labelledby", trigger.id);
    const items = [];
    definition.items.forEach(item => {
      if (!item) { const separator = node("div", "browser-menubar__separator"); separator.setAttribute("role", "separator"); popup.append(separator); return; }
      const button = node("button", "browser-menubar__item", item.label); button.type = "button"; button.tabIndex = -1;
      button.setAttribute("role", "menuitem");
      button.addEventListener("click", () => { closeMenu(true); item.action(); });
      items.push(button); popup.append(button);
    });
    trigger.addEventListener("click", () => { if (activeMenu === index) closeMenu(true); else openMenu(index); });
    trigger.addEventListener("focus", () => setTabStop(index));
    group.addEventListener("pointerenter", () => { if (activeMenu >= 0 && activeMenu !== index) openMenu(index, "trigger"); });
    menus.push({ trigger, popup, items }); group.append(trigger, popup); menuList.append(group);
  });
  document.addEventListener("click", event => { if (!bar.contains(event.target)) closeMenu(); });
  document.addEventListener("focusin", event => { if (!bar.contains(event.target)) closeMenu(); });
  document.addEventListener("keydown", event => {
    if (about.open) return;
    const focused = document.activeElement;
    const triggerIndex = menus.findIndex(entry => entry.trigger === focused);
    if (activeMenu < 0 && triggerIndex < 0) return;
    const isItem = activeMenu >= 0 && menus[activeMenu].items.includes(focused);
    const index = activeMenu >= 0 ? activeMenu : triggerIndex;
    const entry = menus[index];
    if (event.key === "Escape") { event.preventDefault(); closeMenu(true); return; }
    if (event.key === "Tab") { closeMenu(); return; }
    if (event.key === "ArrowLeft" || event.key === "ArrowRight") {
      event.preventDefault();
      const next = (index + (event.key === "ArrowRight" ? 1 : menus.length - 1)) % menus.length;
      if (activeMenu >= 0) openMenu(next, isItem ? "first" : "trigger");
      else { setTabStop(next); menus[next].trigger.focus(); }
    } else if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      if (!isItem) openMenu(index, event.key === "ArrowUp" ? "last" : "first");
      else {
        const current = entry.items.indexOf(focused);
        entry.items[(current + (event.key === "ArrowDown" ? 1 : entry.items.length - 1)) % entry.items.length].focus();
      }
    } else if (event.key === "Home" || event.key === "End") {
      event.preventDefault();
      if (isItem) (event.key === "Home" ? entry.items[0] : entry.items.at(-1)).focus();
      else {
        const next = event.key === "Home" ? 0 : menus.length - 1;
        if (activeMenu >= 0) openMenu(next, "trigger"); else { setTabStop(next); menus[next].trigger.focus(); }
      }
    } else if ((event.key === "Enter" || event.key === " ") && triggerIndex >= 0) {
      event.preventDefault(); openMenu(triggerIndex);
    }
  });
  shell.parentNode.insertBefore(bar, shell);
  document.body.append(about);
  document.body.classList.add("has-browser-menubar");
})();
