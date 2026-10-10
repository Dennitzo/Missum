const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");
const { TestNode } = require("./test-dom.cjs");

const source = fs.readFileSync(path.resolve(__dirname, "../../src/Missum.App/Assets/Web/app.js"), "utf8");
function functionSource(name) {
  const start = source.indexOf(`  function ${name}(`);
  const ending = source.slice(start).match(/\r?\n {2}\}(?:\r?\n|$)/);
  assert.ok(start >= 0 && ending, `production ${name} exists`);
  return source.slice(start, start + ending.index + ending[0].length);
}

class MediaNode extends TestNode {
  load() { this.loadCount = (this.loadCount || 0) + 1; }
  get src() { return this.getAttribute("src") || ""; }
  set src(value) { this.setAttribute("src", value); }
}

function harness({ isLan = true } = {}) {
  const posts = [];
  const state = { artifactPreviewUrls: new Map(), artifactPreviewPending: new Set(),
    chatMode: "general", activeSessionId: "session", codingActivity: new Map(), messageRunStatus: new Map(), messages: [] };
  const body = new MediaNode("body");
  const context = vm.createContext({ state, post: (type, payload) => posts.push({ type, payload }),
    document: { createElement: tag => new MediaNode(tag), createTextNode: text => new MediaNode("#text", text),
      createDocumentFragment: () => new MediaNode("#fragment"), querySelectorAll: selector => body.querySelectorAll(selector) },
    missumBridge: { isLanBrowser: isLan,
      resourceUrl: value => String(value || "").replace("https://assistant.local/", "http://pc:8090/assistant/") } });
  for (const name of ["resourceUrl", "isVisibleArtifact", "formatBytes", "resolveImageArtifactAnchors", "createArtifactList", "handleHostMessage"])
    vm.runInContext(functionSource(name), context, { filename: `app.js:${name}` });
  const render = artifacts => { const list = context.createArtifactList(artifacts); body.append(list); return list; };
  const preview = payload => context.handleHostMessage({ detail: { type: "artifact.previewReady", payload } });
  const renderMessage = (message, previous = null) => {
    if (!context.createMessage) {
      Object.assign(context, { timeLabel: () => "12:00", createToolIcon: () => new MediaNode("svg"),
        codingToolLabel: name => name, codingStepState: value => String(value || "completed").toLowerCase(),
        codingToolSummary: () => null, enhanceCodingCodeBlocks: () => {}, annotateReadableSpeechBlocks: () => {},
        createMessageFooter: () => new MediaNode("footer") });
      for (const name of ["sanitizeVisibleMessageContent", "isTerminalMessageStatus", "statusLabel", "codingPreviewHtml",
        "normalizeCodingStep", "mergeCodingToolSteps", "createCodingActivity", "createMessage"])
        vm.runInContext(functionSource(name), context, { filename: `app.js:${name}` });
      for (const script of ["markdown.js", "coding-timeline.js"])
        vm.runInContext(fs.readFileSync(path.resolve(__dirname, "../../src/Missum.App/Assets/Web", script), "utf8"), context);
    }
    const next = context.createMessage(message, previous);
    const article = previous ? context.missumCodingTimeline.reconcile(previous, next) : next;
    if (!previous) body.append(article);
    return article;
  };
  return { state, posts, render, preview, renderMessage };
}

const imageArtifact = (extra = {}) => ({ id: "image", fileName: "Original.png", contentType: "image/png", length: 50000,
  url: "https://assistant.local/artifacts/image", downloadUrl: "https://assistant.local/artifacts/image?download=1", ...extra });

test("original image URL renders inline and a late thumbnail cannot replace its source", async () => {
  const h = harness();
  h.state.artifactPreviewUrls.set("image", { url: "data:image/png;base64,old-thumbnail" });
  const list = h.render([imageArtifact()]);
  const card = list.querySelector(".artifact-card--image"), image = card.querySelector("img");
  assert.ok(list.classList.contains("message-artifacts--images"));
  assert.equal(image.src, "http://pc:8090/assistant/artifacts/image");
  assert.equal(image.dataset.originalUrl, image.src);
  assert.equal(image.loading, "lazy");
  assert.equal(h.posts.filter(item => item.type === "artifact.preview").length, 0);
  h.preview({ artifactId: "image", url: "data:image/png;base64,new-thumbnail" });
  assert.equal(image.src, "http://pc:8090/assistant/artifacts/image");
  assert.match(card.querySelector(".artifact-card__open-image").title, /herunterladen$/);
  await card.querySelector(".artifact-card__open-image").dispatch("click");
  assert.equal(h.posts.at(-1).type, "artifact.open");
  assert.equal(h.posts.at(-1).payload.artifactId, "image");
  await card.querySelector(".artifact-card__open").dispatch("click");
  assert.equal(h.posts.at(-1).type, "artifact.open");
});

test("legacy images request one preview and can upgrade to an additive original URL", () => {
  const h = harness({ isLan: false });
  const first = h.render([imageArtifact({ url: undefined })]);
  h.render([imageArtifact({ url: undefined })]);
  assert.equal(h.posts.filter(item => item.type === "artifact.preview").length, 1);
  h.preview({ artifactId: "image", url: "data:image/png;base64,legacy-thumbnail" });
  assert.equal(first.querySelector("img").src, "data:image/png;base64,legacy-thumbnail");
  h.preview({ artifactId: "image", originalUrl: "https://assistant.local/artifacts/image" });
  assert.equal(first.querySelector("img").src, "http://pc:8090/assistant/artifacts/image");
  h.preview({ artifactId: "image", url: "data:image/png;base64,late-thumbnail" });
  assert.equal(first.querySelector("img").src, "http://pc:8090/assistant/artifacts/image");
  const restored = h.render([imageArtifact({ url: undefined })]);
  assert.equal(restored.querySelector("img").src, "http://pc:8090/assistant/artifacts/image");
  assert.match(restored.querySelector(".artifact-card__open-image").title, /im Standardprogramm öffnen$/);
});

test("image rendering preserves audio/video previews and PDF actions while filtering vision inputs", async () => {
  const h = harness();
  h.state.artifactPreviewUrls.set("audio", { url: "/assistant/preview/audio.wav" });
  h.state.artifactPreviewUrls.set("video", { url: "/assistant/preview/video.mp4", posterUrl: "/assistant/preview/poster.jpg" });
  const list = h.render([imageArtifact(), imageArtifact({ id: "private", metadata: { role: "VISION_INPUT" } }),
    { id: "audio", fileName: "Audio.wav", contentType: "audio/wav", url: "/original-audio" },
    { id: "video", fileName: "Video.mp4", contentType: "video/mp4", url: "/original-video" },
    { id: "pdf", fileName: "Dokument.pdf", contentType: "application/pdf", url: "/original-pdf" }]);
  assert.equal(list.querySelectorAll(".artifact-card").length, 4);
  assert.equal(list.querySelectorAll("img").length, 1);
  const audio = list.querySelector("audio"), video = list.querySelector("video");
  assert.equal(audio.src, "/assistant/preview/audio.wav");
  assert.equal(video.src, "/assistant/preview/video.mp4");
  assert.equal(video.poster, "/assistant/preview/poster.jpg");
  assert.equal(audio.controls, true); assert.equal(video.controls, true);
  h.preview({ artifactId: "audio", url: "/assistant/preview/audio-new.wav", originalUrl: "/unused-original-audio" });
  assert.equal(audio.src, "/assistant/preview/audio-new.wav");
  const pdf = list.querySelector('[data-artifact-id="pdf"]');
  assert.equal(pdf.querySelector("img"), null);
  await pdf.querySelector(".artifact-card__open").dispatch("click");
  assert.equal(h.posts.at(-1).type, "artifact.open"); assert.equal(h.posts.at(-1).payload.artifactId, "pdf");
  assert.equal(h.posts.filter(item => item.type === "artifact.preview").length, 0);
});

const imageStep = (extra = {}) => ({ id: "server-create", tool: "image.generate", status: "completed",
  contentOffset: "Vor dem Bild.\n\n".length, inputJson: "{}", outputJson: "{}", ...extra });
const imageMessage = (extra = {}) => ({ id: "answer", sessionId: "session", role: "assistant", status: "completed",
  content: "Vor dem Bild.\n\nNach dem Bild.", toolSteps: [imageStep()], artifacts: [imageArtifact({ stepId: "create" })], ...extra });

test("created images stay after their tool receipt and before later narration during streaming and reload", () => {
  const h = harness();
  const first = h.renderMessage(imageMessage({ status: "streaming", content: "Vor dem Bild.\n\n" }));
  const card = first.querySelector(".artifact-card--image");
  const timeline = first.querySelector(".coding-timeline");
  const producingStep = timeline.querySelector('[data-step-id="server-create"]');
  assert.equal(card.parentElement.parentElement, producingStep);
  assert.equal(producingStep.querySelector("details").hasAttribute("open"), false);
  const grown = imageMessage({ content: "Vor dem Bild.\n\nNach dem Bild.\n\nWeiterer Text.",
    toolSteps: [imageStep(), imageStep({ id: "later-tool", tool: "web.search", contentOffset: 29 })] });
  const updated = h.renderMessage(grown, first);
  assert.equal(updated.querySelector(".artifact-card--image"), card);
  assert.equal(updated.querySelectorAll(".artifact-card--image").length, 1);
  const parts = updated.querySelector(".coding-timeline").children;
  const index = parts.findIndex(part => part.dataset.stepId === "server-create");
  assert.ok(parts[index - 1].textContent.includes("Vor dem Bild."));
  assert.ok(parts[index + 1].textContent.includes("Nach dem Bild."));
  const reloaded = harness().renderMessage(JSON.parse(JSON.stringify(grown)));
  assert.equal(reloaded.querySelectorAll('[data-step-id="server-create"] .artifact-card--image').length, 1);
  assert.equal(reloaded.querySelectorAll(".artifact-card--image").length, 1);
});

test("original recovery inherits the thumbnail's creation position and removes duplicate image cards", () => {
  const thumbnail = imageArtifact({ id: "thumb", stepId: "server-create", metadata: { role: "thumbnail", sourceUploadId: "upload" } });
  const original = imageArtifact({ id: "original", stepId: undefined,
    metadata: { role: "original", replacesArtifactId: "thumb", sourceUploadId: "upload" } });
  const message = imageMessage({ artifacts: [thumbnail, original, original] });
  for (const h of [harness(), harness()]) {
    const article = h.renderMessage(message);
    const cards = article.querySelectorAll(".artifact-card--image");
    assert.equal(cards.length, 1);
    assert.equal(cards[0].dataset.artifactId, "original");
    assert.equal(cards[0].parentElement.parentElement.dataset.stepId, "server-create");
  }
  assert.equal(original.stepId, undefined, "rendering must not mutate stored artifact metadata");
});

test("a live image without stepId uses recorded upload provenance and keeps unbound artifacts at the footer", () => {
  const h = harness();
  const liveStep = imageStep({ id: "inspect", tool: "media.inspect", inputJson: '{"uploadId":"wrong-requested"}',
    outputJson: '{"kind":"image","resolvedUploadId":"actual-upload"}' });
  h.state.codingActivity.set("answer", [{ ...liveStep, kind: "tool" }]);
  const bound = imageArtifact({ id: "bound", stepId: undefined, metadata: { role: "original", sourceUploadId: "actual-upload" } });
  const unbound = imageArtifact({ id: "unbound", stepId: undefined });
  const pdf = { id: "pdf", fileName: "Report.pdf", contentType: "application/pdf" };
  const article = h.renderMessage(imageMessage({ toolSteps: [], artifacts: [bound, unbound, pdf], status: "streaming" }));
  assert.equal(article.querySelectorAll('[data-step-id="inspect"] .artifact-card--image').length, 1);
  assert.equal(article.querySelectorAll(".artifact-card--image").length, 2);
  assert.equal(article.querySelector('[data-artifact-id="unbound"]').parentElement.parentElement.className, "message-body");
  assert.equal(article.querySelector('[data-artifact-id="pdf"]').parentElement.parentElement.className, "message-body");
});

test("a live image receipt remains before a later persisted tool at its creation offset", () => {
  const h = harness();
  const live = imageStep({ id: "live-image", kind: "tool" });
  h.state.codingActivity.set("answer", [live]);
  const message = imageMessage({ artifacts: [imageArtifact({ stepId: "live-image" })],
    content: "Vor dem Bild.\n\nNach dem Bild.\n\nWeiterer Text.",
    toolSteps: [imageStep({ id: "persisted-later", tool: "web.search", contentOffset: 29 })] });
  const article = h.renderMessage(message);
  const children = article.querySelector(".coding-timeline").children;
  const imageIndex = children.findIndex(child => child.dataset.stepId === "live-image");
  const laterIndex = children.findIndex(child => child.dataset.stepId === "persisted-later");
  assert.ok(imageIndex >= 0 && imageIndex < laterIndex);
  assert.ok(children[imageIndex - 1].textContent.includes("Vor dem Bild."));
  assert.ok(children[imageIndex + 1].textContent.includes("Nach dem Bild."));
  assert.equal(article.querySelectorAll(".artifact-card--image").length, 1);
});
