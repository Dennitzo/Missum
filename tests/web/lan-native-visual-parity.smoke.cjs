// Independent temporary headless browser QA. No existing user browser is used.
// Run only after the coordinator confirms the actual published LAN host.
const { chromium } = require("playwright");
const assert = require("node:assert/strict");
const fs = require("node:fs/promises");
const path = require("node:path");
const { createHash } = require("node:crypto");

const colorKeys = {
  window:["--bg"],layer:["--surface","--sidebar"],layerStrong:["--layer-strong","--surface-raised","--user-bubble"],
  input:["--composer"],hover:["--surface-hover"],pressed:["--surface-pressed"],stroke:["--border"],mutedText:["--muted"],text:["--text"],
  accent:["--accent"],accentForeground:["--accent-contrast","--accent-ink"],accentSubtle:["--accent-subtle"],titlebar:["--titlebar"]
};
for (const name of ["Web","Research","Image","Audio","Speech","Pdf","Document","Plan","Code","Folder","Navigation","Link","Add","Danger","Settings","Subagent"]) colorKeys[`icon${name}`]=[`--icon-${name.toLowerCase()}`];
const previewText = (value,maximum) => { const text=String(value || "").replace(/\s+/gu," ").trim(); return text.length<=maximum ? text : text.slice(0,maximum-1)+"…"; };
const forbidden = new Set(["chat.send","chat.resume","chat.steer","models.select","reasoning.set","settings.update","promptTriggers.apply","session.create","session.projectCreate","session.delete","microphone.speak","backup.create","backup.restore","backup.restoreCommit"]);

async function main() {
  const target=process.env.MISSUM_LAN_URL || "http://192.168.0.67:8080/assistant/";
  const sessionId=process.env.MISSUM_QA_SESSION || "2da26ffe-8205-46f1-ba7d-421ed01530db";
  const output=path.resolve(process.env.MISSUM_QA_OUTPUT || "artifacts/validation/lan-browser/parity-20261006/web-final");
  const nativeReference=path.resolve("artifacts/validation/lan-browser/parity-20261006/native-reference.jpg");
  await fs.mkdir(output,{recursive:true});
  const browser=await chromium.launch({executablePath:"C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",headless:true,chromiumSandbox:true});
  const sent=[],pageErrors=[],checks=[];
  let page;
  const check=(name,data={})=>{checks.push({name,...data});console.log(JSON.stringify({check:name,...data}));};
  try {
    const context=await browser.newContext({viewport:{width:1266,height:913},colorScheme:"light",reducedMotion:"reduce"});
    page=await context.newPage();
    await page.addInitScript(()=>{window.__nativeVisualEvents=[];addEventListener("missum:host-message",event=>window.__nativeVisualEvents.push(event.detail));});
    page.on("pageerror",error=>pageErrors.push(error.message));
    page.on("websocket",socket=>socket.on("framesent",frame=>{try{sent.push(JSON.parse(String(frame.payload)));}catch{}}));
    page.on("request",request=>{if(request.method()==="POST" && new URL(request.url()).pathname.endsWith("/message")){try{sent.push(JSON.parse(request.postData()));}catch{}}});
    const loaded=await page.goto(target,{waitUntil:"domcontentloaded"}); assert.equal(loaded.status(),200);
    await page.waitForFunction(()=>globalThis.missumApp?.getState()?.activeSessionId);
    await page.evaluate(id=>missumBridge.post("session.open",{sessionId:id}),sessionId);
    await page.waitForFunction(id=>missumApp.getState().activeSessionId===id,sessionId);
    await page.waitForFunction(()=>document.querySelectorAll("#message-list article.message").length>=4);
    const transcript=await page.evaluate(()=>missumApp.getState().messages);
    const messages=[...transcript].sort((a,b)=>Date.parse(a.createdAt)-Date.parse(b.createdAt));
    const prompts=messages.filter(message=>message.role==="user"); assert.equal(prompts.length,2);
    const children=await page.evaluate(id=>{
      const found=new Map();for(const event of __nativeVisualEvents){const rows=event.payload?.subagents || (event.type==="subagent.snapshot"?[event.payload.subagent || event.payload]:[]);for(const child of rows)if(child.sessionId===id)found.set(child.agentId || child.runId,child);}return [...found.values()];
    },sessionId);
    assert.equal(children.length,7); assert.ok(children.every(child=>Number.isInteger(child.planetIndex) && child.planetIndex>=0));
    const tabs=page.locator("#session-tabs [role=tab]");
    assert.equal(await tabs.count(),3); assert.deepEqual((await tabs.allTextContents()).slice(1),["Publikation","Simulation"]);
    await page.locator("#message-scroll").evaluate(element=>{element.scrollTop=element.scrollHeight;});
    const markers=page.locator(".browser-prompt-timeline-marker"); await markers.first().waitFor(); assert.equal(await markers.count(),2);
    await page.mouse.move(0,0);
    await page.waitForFunction(()=>{const widths=[...document.querySelectorAll(".browser-prompt-timeline-marker__line")].map(line=>Math.round(line.getBoundingClientRect().width)).sort((a,b)=>a-b);return JSON.stringify(widths)==="[7,20]";});
    await page.screenshot({path:path.join(output,"assistant-initial.png"),fullPage:true});
    check("initial-native-tabs-timeline",{tabs:await tabs.allTextContents(),lines:await markers.count(),children:children.length});
    await page.waitForTimeout(500); // Allow the app's scheduled prompt resize and font layout to settle.
    const layout=await page.evaluate(()=>{const rect=selector=>{const r=document.querySelector(selector).getBoundingClientRect();return {x:r.x,y:r.y,width:r.width,height:r.height};},style=getComputedStyle(document.getElementById("prompt")),composer=getComputedStyle(document.querySelector(".composer"));return {prompt:rect("#prompt"),composer:rect(".composer"),toolbar:rect(".composer-toolbar"),toolbarMargin:getComputedStyle(document.querySelector(".composer-toolbar")).marginTop,promptCss:{height:style.height,minHeight:style.minHeight,maxHeight:style.maxHeight,paddingTop:style.paddingTop,paddingBottom:style.paddingBottom,lineHeight:style.lineHeight,inlineHeight:document.getElementById("prompt").style.height,valueLength:document.getElementById("prompt").value.length},composerCss:{paddingTop:composer.paddingTop,paddingBottom:composer.paddingBottom,borderTop:composer.borderTopWidth,borderBottom:composer.borderBottomWidth},selectedSession:rect(".session-item.active"),selectedLabel:rect(".session-item.active .session-item__title")};});
    assert.ok(Math.abs(layout.selectedSession.x-11)<1);assert.ok(Math.abs(layout.selectedLabel.x-47)<1);
    check("native-layout-composer-measurement",{...layout,matchesNativeEmptyHeight:layout.prompt.height===48 && layout.composer.height===114});assert.equal(layout.toolbarMargin,"9px");assert.equal(layout.promptCss.valueLength,0);assert.equal(layout.prompt.height,48);assert.equal(layout.composer.height,114);
    const receipts=await page.locator("#message-list .subagent-lifecycle").evaluateAll(rows=>rows.map(row=>({stepId:row.dataset.stepId,caption:row.querySelector(".subagent-lifecycle__caption")?.textContent,planetIndex:Number(row.querySelector(".subagent-planet")?.dataset.planetIndex),disabled:row.disabled})));
    assert.ok(receipts.length>0,"The recorded parent conversation has native child lifecycle receipts.");
    assert.ok(receipts.every(receipt=>["hat die Arbeit begonnen","hat die Arbeit beendet"].includes(receipt.caption) && children.some(child=>child.planetIndex===receipt.planetIndex) && receipt.disabled===false));
    check("native-child-lifecycle-rows",{receipts});

    const requestId=await page.evaluate(()=>missumBridge.post("settings.get",{}));
    await page.waitForFunction(id=>__nativeVisualEvents.some(event=>event.type==="settings.snapshot" && event.requestId===id),requestId);
    const settings=await page.evaluate(id=>__nativeVisualEvents.find(event=>event.type==="settings.snapshot" && event.requestId===id).payload,requestId);
    assert.ok(settings.resolvedAppearance?.colors);
    const expected={};for(const [key,names] of Object.entries(colorKeys)){assert.match(settings.resolvedAppearance.colors[key] || "",/^#[\da-f]{6}(?:[\da-f]{2})?$/i,`Native palette includes ${key}`);for(const name of names)expected[name]=settings.resolvedAppearance.colors[key].toLowerCase();}
    await page.waitForFunction(expected=>Object.entries(expected).every(([key,value])=>getComputedStyle(document.documentElement).getPropertyValue(key).trim().toLowerCase()===value),expected);
    const palette=await page.evaluate(keys=>({theme:document.documentElement.dataset.theme,prefersDark:matchMedia("(prefers-color-scheme:dark)").matches,colors:Object.fromEntries(keys.map(key=>[key,getComputedStyle(document.documentElement).getPropertyValue(key).trim()]))}),Object.keys(expected));
    assert.equal(palette.prefersDark,false);assert.equal(palette.theme,settings.resolvedAppearance.highContrast?"high-contrast":String(settings.resolvedAppearance.theme).toLowerCase());
    check("pc-native-palette-with-light-browser-preference",{revision:settings.revision,expected,actual:palette});

    await page.locator("#inspector-toggle").click(); await page.locator("#output-inspector").waitFor({state:"visible"});
    const inspector=page.locator("#output-inspector"),summary=inspector.locator(".inspector-subagent-summary");
    const bounds=await inspector.boundingBox();assert.equal(bounds.width,304);
    const inspectorLayout=await inspector.evaluate(root=>[...root.children].map(child=>({className:child.className,height:child.getBoundingClientRect().height,lineHeight:getComputedStyle(child).lineHeight,marginTop:getComputedStyle(child).marginTop,marginBottom:getComputedStyle(child).marginBottom,children:[...child.children].map(item=>({tag:item.tagName,className:item.className,height:item.getBoundingClientRect().height,lineHeight:getComputedStyle(item).lineHeight}))})));
    check("native-inspector-height-measurement",{bounds,matchesNativeHeight:bounds.height===336,inspectorLayout});
    assert.equal(bounds.height,336);
    assert.equal(await summary.getAttribute("aria-label"),"7 fertig");assert.equal(await summary.locator(".subagent-planet").count(),4);
    assert.equal(await inspector.locator(".subagent-overview-row").count(),0);
    const indices=await summary.locator(".subagent-planet").evaluateAll(planets=>planets.map(planet=>Number(planet.dataset.planetIndex)));
    assert.ok(indices.every(index=>children.some(child=>child.planetIndex===index)));
    assert.equal(await inspector.locator(".inspector-copy-path svg").count(),1);
    assert.equal(await inspector.locator(".inspector-copy-path").evaluate(button=>getComputedStyle(button).borderTopWidth),"0px");
    const chain=await inspector.locator(".inspector-all-sources path").getAttribute("d");assert.ok(chain.includes("a3 3"));
    const inspectorIcon=await page.locator("#inspector-toggle path").getAttribute("d");assert.equal(inspectorIcon,"M14 3h7v7M21 3 10 14M11 5H4v15h15v-7");
    const toolGlyphs=await page.locator("#message-list .coding-step__icon").evaluateAll(icons=>icons.map(icon=>({key:icon.dataset.iconKey,width:icon.getBoundingClientRect().width,color:getComputedStyle(icon).color,path:icon.querySelector("path")?.getAttribute("d")})));
    const webGlyph=toolGlyphs.find(icon=>icon.key==="web");assert.ok(webGlyph && webGlyph.width===14 && webGlyph.path.includes("M1 8h14"));
    check("native-corrected-icon-shapes",{chain,inspectorIcon,toolGlyphs});
    await page.screenshot({path:path.join(output,"outputs-compact.png"),fullPage:true});
    check("compact-native-outputs",{bounds,planetIndices:indices,label:"7 fertig"});
    await summary.click(); await page.locator(".subagent-overview-entry").first().waitFor();
    assert.equal(await page.locator(".subagent-overview-entry").count(),7);assert.equal(await tabs.count(),4);
    assert.equal(await page.locator("#prompt-navigation").isVisible(),false);
    await page.screenshot({path:path.join(output,"subagent-overview.png"),fullPage:true});
    await page.locator(".subagent-overview-entry").first().click();
    await page.waitForFunction(()=>document.querySelector(".chat-pane").dataset.view==="subagent");
    assert.equal(await tabs.count(),5);assert.equal(await page.locator('#session-tabs [data-view="subagent"] [role=tab]').count(),1);
    await page.screenshot({path:path.join(output,"subagent-selected.png"),fullPage:true});
    await page.locator('#session-tabs [data-view="subagent"] .session-view-tab__close').click();assert.equal(await tabs.count(),4);
    await page.locator('#session-tabs [data-view="subagents"] .session-view-tab__close').click();assert.equal(await tabs.count(),3);
    await inspector.getByRole("button",{name:"Ausgaben schließen",exact:true}).click();
    check("lazy-subagent-tabs",{overviewRows:7,openedChildren:1,finalTabs:3});

    const firstMarker=page.locator(`.browser-prompt-timeline-marker[data-prompt-id="${prompts[0].id}"]`);
    await firstMarker.click();await page.waitForFunction(()=>document.getElementById("message-scroll").scrollTop<2);
    await page.mouse.move(0,0);await firstMarker.hover();
    const preview=page.locator(".browser-prompt-timeline-preview:visible");await preview.waitFor();assert.equal((await preview.boundingBox()).width,326);
    const firstAnswer=messages.slice(messages.findIndex(message=>message.id===prompts[0].id)+1).find(message=>message.role==="assistant");
    assert.equal(await preview.locator(".browser-prompt-timeline-preview__prompt").textContent(),`1) ${previewText(prompts[0].content,118)}`);
    assert.equal(await preview.locator(".browser-prompt-timeline-preview__answer").textContent(),previewText(firstAnswer.content,190));
    assert.equal(await preview.locator(".browser-prompt-timeline-preview__bookmark").count(),1);
    await page.screenshot({path:path.join(output,"timeline-hover-preview.png"),fullPage:true});
    await page.mouse.move(0,0);await firstMarker.focus();await firstMarker.press("ArrowDown");
    await preview.waitFor();assert.equal(await preview.locator(".browser-prompt-timeline-preview__prompt").textContent(),`2) ${previewText(prompts[1].content,118)}`);
    await page.screenshot({path:path.join(output,"timeline-keyboard-preview.png"),fullPage:true});
    await page.locator(":focus").press("Escape");assert.equal(await preview.count(),0);
    await page.locator("#session-tabs [role=tab]").filter({hasText:"Publikation"}).click();
    assert.equal(await page.locator("#prompt-navigation").isVisible(),false);assert.equal(await preview.count(),0);
    await page.locator('#session-tabs [data-view="chat"]').click();await firstMarker.waitFor({state:"visible"});
    check("native-prompt-timeline-navigation",{promptIds:prompts.map(message=>message.id),idleWidths:[7,20],previewWidth:326,clickTop:true,keyboard:true,scopeCleanup:true});

    const goldenPath=path.resolve(process.env.MISSUM_NATIVE_PLANET_GOLDEN || "artifacts/portable/win-x64.native-planet-palette-validation.json");
    let golden;try{golden=JSON.parse(await fs.readFile(goldenPath,"utf8"));}catch(error){if(process.env.MISSUM_NATIVE_PLANET_GOLDEN)throw error;}
    if(golden?.signatures?.length===128){
      const sourceResponse=await page.request.get(new URL("browser-panels.js",target).href);assert.equal(sourceResponse.status(),200);const source=await sourceResponse.text();
      const extract=name=>{const start=source.indexOf(`  function ${name}(`);assert.ok(start>=0);const ending=source.slice(start).match(/\r?\n {2}\}(?:\r?\n|$)/);assert.ok(ending);return source.slice(start,start+ending.index+ending[0].length);};
      const gallery=await context.newPage();await gallery.setViewportSize({width:768,height:532});await gallery.setContent('<style>body{width:768px;margin:0;background:#1e1e1e;color:#dcdcdc;font:10px "Segoe UI Variable Text","Segoe UI"}.tiles{display:grid;grid-template-columns:repeat(16,48px)}.tile{height:62px;display:flex;flex-direction:column;align-items:center;gap:2px}.tile span{opacity:.65}h1{height:36px;box-sizing:border-box;line-height:20px;margin:0;font-size:14px;padding:8px 12px;font-weight:400}</style><h1>Planetenpalette · 14 / 28 DIP · 128 Beispiele</h1><div class="tiles"></div>');
      await gallery.addScriptTag({content:`let planetSequence=0;${extract("svgNode")}${extract("planet")}globalThis.renderPlanet=planet;`});
      const signatures=await gallery.evaluate(()=>{const signatures=[];for(let index=0;index<128;index++){const tile=document.createElement("div");tile.className="tile";const small=renderPlanet({agentId:`golden-${index}`,planetIndex:index},14),large=renderPlanet({agentId:`golden-${index}`,planetIndex:index},28),label=document.createElement("span");label.textContent=String(index);signatures.push(small.dataset.planetSignature);tile.append(small,large,label);document.querySelector(".tiles").append(tile);}return signatures;});
      assert.deepEqual(signatures,golden.signatures);assert.equal(new Set(signatures).size,128);
      await gallery.screenshot({path:path.join(output,"svg-native-planet-contact-sheet.png"),fullPage:true});await gallery.close();
      check("native-128-planet-signature-parity",{samples:128,golden:goldenPath,publishedSourceSha256:createHash("sha256").update(source).digest("hex")});
    }else check("native-128-planet-signature-parity",{skipped:true,reason:"The current native smoke has not exported its 128 signatures."});

    await page.evaluate(()=>document.fonts.load("16px Selawik"));assert.equal(await page.evaluate(()=>document.fonts.check("16px Selawik")),true);
    await page.addStyleTag({content:':root,button,input,textarea,select,.browser-prompt-timeline-preview{font-family:Selawik !important}'});
    await page.locator("#inspector-toggle").click();await page.screenshot({path:path.join(output,"selawik-fallback-outputs.png"),fullPage:true});
    check("bundled-selawik-fallback",{forcedOnlyInOwnQaClient:true,fontLoaded:true});
    const hostErrors=await page.evaluate(()=>__nativeVisualEvents.filter(event=>event.type==="host.error"));
    assert.equal(hostErrors.length,0);assert.equal(pageErrors.length,0);assert.ok(sent.some(command=>command.type==="session.open" && command.payload?.sessionId===sessionId));
    assert.equal(sent.filter(command=>forbidden.has(command.type)).length,0);
    const result={target,sessionId,nativeReference,checks,sent,hostErrors,pageErrors};await fs.writeFile(path.join(output,"inspection.json"),JSON.stringify(result,null,2));
    for(const name of ["failure.png","failure.json"])await fs.rm(path.join(output,name),{force:true});
  }catch(error){if(page && !page.isClosed()){await page.screenshot({path:path.join(output,"failure.png"),fullPage:true});await fs.writeFile(path.join(output,"failure.json"),JSON.stringify({error:error.message,checks,sent,pageErrors,events:await page.evaluate(()=>window.__nativeVisualEvents || [])},null,2));}throw error;
  }finally{await browser.close();}
}
main().catch(error=>{console.error(error);process.exitCode=1;});
