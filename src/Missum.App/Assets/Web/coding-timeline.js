(() => {
  "use strict";

  const terminalStates = new Set(["completed", "failed", "denied", "cancelled", "interrupted", "steered"]);
  const statusLabels = { completed: "Abgeschlossen", running: "Läuft", pending: "Wartet",
    failed: "Fehlgeschlagen", denied: "Abgelehnt", cancelled: "Abgebrochen", interrupted: "Nicht abgeschlossen", steered: "Umgeleitet" };

  function node(tag, className, text) {
    const result = document.createElement(tag);
    if (className) result.className = className;
    if (text !== undefined) result.textContent = String(text);
    return result;
  }

  function json(value) {
    if (value && typeof value === "object") return value;
    try { return value ? JSON.parse(value) : null; } catch { return null; }
  }

  // Unified patches are data, never Markdown or HTML. Keep metadata and no-newline
  // markers, and advance each side independently so line numbers remain truthful.
  function parseUnifiedDiff(value) {
    const files = [];
    let file = null, oldLine = null, newLine = null, inHunk = false, oldRemaining = 0, newRemaining = 0;
    const begin = path => {
      file = { path, lines: [], added: 0, removed: 0, rawText: "" };
      files.push(file);
      oldLine = newLine = null;
      inHunk = false;
    };
    const source = String(value || "");
    const lines = source.split("\n");
    if (lines.at(-1) === "") lines.pop();
    for (const [lineIndex, rawLine] of lines.entries()) {
      const text = rawLine.endsWith("\r") ? rawLine.slice(0, -1) : rawLine;
      const rawText = rawLine + (lineIndex < lines.length - 1 || source.endsWith("\n") ? "\n" : "");
      if (/^\.{3}\[Ausgabe gekürzt\]\.\.\.$/.test(text)) {
        inHunk = false; oldLine = newLine = null;
      }
      if (text.startsWith("diff --git ")) {
        const paths = /^diff --git ("(?:[^"\\]|\\.)*"|\S+) ("(?:[^"\\]|\\.)*"|\S+)$/.exec(text);
        begin(paths ? gitPathLabel(paths[2]) : text.slice(11));
        file.rawText += rawText;
        file.lines.push({ kind: "meta", text, oldLine: null, newLine: null });
        continue;
      }
      if (!file || text.startsWith("--- ") && inHunk && oldRemaining === 0 && newRemaining === 0) begin("Änderung");
      file.rawText += rawText;
      if (text.startsWith("--- ") && !inHunk) {
        file.path = gitPathLabel(text.slice(4));
      }
      if (text.startsWith("+++ ") && !inHunk && text.slice(4) !== "/dev/null") {
        file.path = gitPathLabel(text.slice(4));
      }
      const hunk = /^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@/.exec(text);
      if (hunk) {
        oldLine = Number(hunk[1]); newLine = Number(hunk[3]); inHunk = true;
        oldRemaining = Number(hunk[2] ?? 1); newRemaining = Number(hunk[4] ?? 1);
        file.lines.push({ kind: "hunk", text, oldLine: null, newLine: null });
      } else if (inHunk && text.startsWith("+")) {
        file.added++;
        newRemaining--;
        file.lines.push({ kind: "added", text, oldLine: null, newLine: newLine++ });
      } else if (inHunk && text.startsWith("-")) {
        file.removed++;
        oldRemaining--;
        file.lines.push({ kind: "removed", text, oldLine: oldLine++, newLine: null });
      } else if (inHunk && text.startsWith(" ")) {
        oldRemaining--; newRemaining--;
        file.lines.push({ kind: "context", text, oldLine: oldLine++, newLine: newLine++ });
      } else {
        file.lines.push({ kind: "meta", text, oldLine: null, newLine: null });
      }
    }
    return files;
  }

  function gitPathLabel(path) {
    if (path.startsWith('"') && path.endsWith('"')) {
      const escaped = path.slice(1, -1).replace(/%/g, "%25").replace(/\\([0-7]{3}|[tnr"\\])/g, (_, escape) => {
        const value = /^[0-7]{3}$/.test(escape) ? parseInt(escape, 8) : ({ t: 9, n: 10, r: 13, '"': 34, "\\": 92 })[escape];
        return `%${value.toString(16).padStart(2, "0")}`;
      });
      try { path = decodeURIComponent(escaped); } catch { path = escaped; }
    }
    return path.replace(/^[ab]\//, "");
  }

  function copyButton(text, label = "Kopieren") {
    const button = node("button", "coding-copy", label);
    button.type = "button";
    button.setAttribute("aria-label", label);
    button.addEventListener("click", async () => {
      try {
        if (globalThis.missumBridge?.post) globalThis.missumBridge.post("message.copy", { text });
        else await navigator.clipboard.writeText(text);
        button.textContent = "Kopiert";
      } catch { button.textContent = "Kopieren fehlgeschlagen"; }
    });
    return button;
  }

  function outputBlock(label, text, kind = "output") {
    const block = node("div", `coding-output coding-output--${kind}`);
    block.dataset.timelineKey = label;
    const header = node("div", "coding-output__header");
    header.append(node("span", "", label), copyButton(String(text)));
    block.append(header, node("pre", "coding-output__text", text));
    return block;
  }

  function diffView(text, label) {
    const view = node("div", "coding-diffs");
    view.dataset.timelineKey = label;
    for (const [index, file] of parseUnifiedDiff(text).entries()) {
      const section = node("section", "coding-diff");
      section.dataset.timelineKey = `diff-${index}-${file.path}`;
      const header = node("div", "coding-diff__header");
      const identity = node("div", "coding-diff__identity");
      identity.append(node("span", "coding-diff__label", label), node("strong", "coding-diff__path", file.path));
      const counts = node("span", "coding-diff__counts");
      counts.setAttribute("aria-label", `${file.added} hinzugefügte, ${file.removed} entfernte Zeilen`);
      counts.append(node("span", "coding-diff__added", `+${file.added}`), node("span", "coding-diff__removed", `−${file.removed}`));
      header.append(identity, counts, copyButton(file.rawText, "Diff kopieren"));
      const lines = node("div", "coding-diff__lines");
      for (const [lineIndex, line] of file.lines.entries()) {
        const row = node("div", `coding-diff__line coding-diff__line--${line.kind}`);
        row.dataset.timelineKey = `line-${lineIndex}`;
        const before = node("span", "coding-diff__number", line.oldLine ?? "");
        const after = node("span", "coding-diff__number", line.newLine ?? "");
        before.setAttribute("aria-label", line.oldLine == null ? "" : `Vorher Zeile ${line.oldLine}`);
        after.setAttribute("aria-label", line.newLine == null ? "" : `Nachher Zeile ${line.newLine}`);
        row.append(before, after, node("code", "coding-diff__code", line.text));
        lines.append(row);
      }
      section.append(header, lines);
      view.append(section);
    }
    return view;
  }

  function describe(tool, input) {
    const target = input?.path || input?.workingDirectory || ".";
    const query = input?.query;
    return ({
      "coding.list": `Dateien in ${target} erfassen.`,
      "coding.read": `Den aktuellen Inhalt von ${target} lesen.`,
      "coding.search": `Im Projekt nach ${query ? `„${query}“` : "passenden Codeabschnitten"} suchen.`,
      "coding.write": `Den vorgesehenen Dateiinhalt für ${target} prüfen und speichern.`,
      "coding.edit": `Die passenden Stellen in ${target} prüfen und ändern.`,
      "coding.command": `Den angezeigten Befehl in ${target} ausführen und die Ausgabe prüfen.`,
      "coding.gitDiff": `Die Git-Änderungen in ${target} prüfen.`,
      "coding.undo": `Die getätigten Änderungen in ${target} zurücknehmen.`,
      "coding.searchHistory": `Frühere Nachrichten nach ${query ? `„${query}“` : "relevantem Kontext"} durchsuchen.`,
      "coding.searchKnowledge": `Dokumentwissen nach ${query ? `„${query}“` : "relevanten Informationen"} durchsuchen.`,
      "coding.renderHtml": "Eine isolierte HTML-Vorschau vorbereiten.",
      "web.search": "Passende Quellen im Web suchen.",
      "web.fetch": "Die ausgewählte Quelle lesen.",
      "web.deepResearch": "Quellen suchen, lesen und für die Recherche auswerten."
    })[tool] || "Das Werkzeug ausführen und sein Ergebnis prüfen.";
  }

  function valueBody(value, key, options, depth) {
    // Process streams and source code are exact text, even when they happen to
    // contain valid JSON. Decode JSON only for ordinary structured data fields.
    if (typeof value === "string" && ["stdout", "stderr"].includes(key)) return outputBlock(key === "stdout" ? "Standardausgabe" : "Fehlerausgabe", value, key === "stdout" ? "terminal" : "stderr");
    if (typeof value === "string" && ["code", "source"].includes(key) && options.sourceLanguage) {
      const card = outputBlock(key === "source" && options.sourceLanguage === "lean" ? "Lean-Beweis" : "Code", value, "source");
      const code = node("code", `language-${options.sourceLanguage}`, value);
      globalThis.missumCodeHighlight?.appendTo(code, value, options.sourceLanguage);
      card.querySelector("pre").replaceChildren(code); return card;
    }
    if (typeof value === "string" && /^[\[{]/.test(value.trimStart())) {
      const parsed = json(value);
      if (parsed && typeof parsed === "object") return valueBody(parsed, key, options, depth);
    }
    if (value && typeof value === "object") {
      if (depth >= 12) return outputBlock("Daten", JSON.stringify(value, null, 2), "json");
      if (!Array.isArray(value)) return fields(value, "coding-facts coding-facts--nested", options, depth + 1);
      if (!value.length) return node("span", "coding-note", "Keine Einträge.");
      if (value.every(item => item == null || ["string", "number", "boolean"].includes(typeof item) && !String(item).includes("\n"))) {
        const values = node("span", "coding-value-list");
        for (const [index, item] of value.entries()) {
          if (index) values.append(node("span", "coding-value-separator", ", "));
          values.append(node("span", "coding-value", item == null ? "Nicht verfügbar" : typeof item === "boolean" ? item ? "Ja" : "Nein" : String(item)));
        }
        return values;
      }
      const list = node("ol", "coding-value-items");
      for (const [index, item] of value.entries()) {
        const row = node("li", "coding-value-item"); row.dataset.timelineKey = `value-${index}`;
        row.append(valueBody(item, key, options, depth + 1)); list.append(row);
      }
      return list;
    }
    if (typeof value === "string" && ["answer", "summary", "result", "contentMarkdown", "statement", "task", "description", "explanation"].includes(key) && options.renderMarkdown) {
      const body = node("div", "coding-value-markdown"); body.append(options.renderMarkdown(value)); options.enhanceCodeBlocks?.(body); return body;
    }
    return node("span", "coding-value-text", value == null ? "Nicht verfügbar" : typeof value === "boolean" ? value ? "Ja" : "Nein" : String(value));
  }
  function toolIcon(tool) {
    const key = ["subagent","subagent.completed","subagent.spawn","subagent.wait"].includes(tool)?"subagent":tool==="assistant.continuation"?"success":tool==="coding.updatePlan"?"plan":tool==="speech.translate"?"translate":/^research\.code\.|^coding\./i.test(tool)?"code":/^math\.|^research\.|^assistant\.(reasoning|progress)/i.test(tool)?"research":/^web\./i.test(tool)?"web":/audiobook/i.test(tool)?"audiobook":/caption/i.test(tool)?"captions":/^speech\.|transcription/i.test(tool)?"speech":/image/i.test(tool)?"image":/audio/i.test(tool)?"audio":/video/i.test(tool)?"video":/pdf/i.test(tool)?"pdf":/document/i.test(tool)?"document":"tool";
    const create = tag => document.createElementNS?document.createElementNS("http://www.w3.org/2000/svg",tag):document.createElement(tag);
    const svg=create("svg");for(const [name,value] of Object.entries({viewBox:"0 0 16 16",fill:"none",stroke:"currentColor","stroke-width":"1.2","stroke-linejoin":"round","aria-hidden":"true"}))svg.setAttribute(name,value);
    const paths={web:"M8 1a7 7 0 1 0 0 14 7 7 0 0 0 0-14ZM1 8h14M8 1c-5 4-5 10 0 14m0-14c5 4 5 10 0 14",research:"M11 11l4 4M12.5 6.5a5.5 5.5 0 1 1-11 0 5.5 5.5 0 0 1 11 0",code:"M6 1H4C2 1 4 7 1 8c3 1 1 7 3 7h2M10 1h2c2 0 0 6 3 7-3 1-1 7-3 7h-2",plan:"M1 3l2 2 3-4M1 8l2 2 3-4M1 13l2 2 3-4M8 3h7M8 8h7M8 13h7",success:"M4 2l10 6-10 6Z",image:"M1 2h14v12H1ZM2 12l4-5 3 3 2-2 3 4M10 5h1",speech:"M1 6h3l4-4v12l-4-4H1ZM11 5c3 2 3 4 0 6M13 3c4 3 4 7 0 10",document:"M3 1h6l4 4v10H3ZM9 1v4h4M5 8h6M5 11h6",pdf:"M3 1h6l4 4v10H3ZM9 1v4h4M5 10h6M8 6c-2 6-4 7-4 6s7-3 8 0-4-2-4-6",captions:"M1 2h14v9H5l-3 3v-3H1ZM4 5h8M4 8h6",subagent:"M8 1a2 2 0 1 0 0 4 2 2 0 0 0 0-4ZM4 14V9c0-4 8-4 8 0v5M1 13V8M15 13V8",tool:"M4 1v4L1 8l3 3v4h8v-4l3-3-3-3V1Z"};
    const path=create("path");path.setAttribute("d",paths[key] || paths.speech);svg.append(path);return {key,svg};
  }

  function fields(values, className = "coding-facts", options = {}, depth = 0) {
    const list = node("dl", className);
    const labels = { path: "Pfad", sha256: "Prüfsumme", originalSha256: "Prüfsumme vorher", expectedSha256: "Geprüfte Ausgangsversion",
      bytes: "Bytes", created: "Neue Datei", totalLines: "Zeilen", startLine: "Ab Zeile", maximumLines: "Zeilenlimit",
      nextLine: "Fortsetzung ab Zeile", query: "Suche", searchedFiles: "Durchsuchte Dateien", maximumResults: "Trefferlimit",
      maximumEntries: "Dateilimit", ignoredDirectories: "Ausgenommene Ordner", partial: "Unvollständige Ausgabe",
      truncated: "Ausgabe gekürzt", diffTruncated: "Diff gekürzt", errorCode: "Fehler", message: "Hinweis", note: "Hinweis",
      progressError: "Übertragungsfehler", stdout: "Standardausgabe", stderr: "Fehlerausgabe", exitCode: "Exitcode",
      elapsedMilliseconds: "Laufzeit (ms)", toolStatus: "Status", addedLines: "Hinzugefügte Zeilen", removedLines: "Entfernte Zeilen",
      operation: "Rechenoperation", left: "Linke Werte", right: "Rechte Werte", result: "Ergebnis", variables: "Variablen", expression: "Ausdruck", expressions: "Ausdrücke",
      workingDirectory: "Arbeitsordner", executable: "Programm", arguments: "Argumente", timeoutSeconds: "Zeitlimit (s)",
      title: "Titel", url: "Adresse", summary: "Zusammenfassung", answer: "Antwort", description: "Beschreibung", contentMarkdown: "Inhalt", statement: "Aussage", task: "Aufgabe" };
    for (const [key, value] of Object.entries(values || {})) {
      if (value === undefined || value === null) continue;
      // These transport flags duplicate the visible execution status. The exact
      // input and receipt remain available through the step's data-copy action.
      if (["success", "toolStatus", "phase", "applied"].includes(key)) continue;
      if (["partial", "truncated", "diffTruncated", "created"].includes(key) && value === false) continue;
      const fact = node("div", "coding-fact");
      fact.dataset.timelineKey = `field-${key}`;
      if (value && typeof value === "object" || typeof value === "string" && (value.includes("\n") || /^[\[{]/.test(value.trimStart()) || ["answer", "summary", "result", "contentMarkdown", "statement", "task", "description", "explanation", "source", "code", "stdout", "stderr"].includes(key))) fact.classList.add("coding-fact--wide");
      if (value && typeof value === "object" && (!Array.isArray(value) || value.some(item => item && typeof item === "object"))
        || typeof value === "string" && (/^[\[{]/.test(value.trimStart()) || ["answer", "summary", "result", "contentMarkdown", "statement", "task", "description", "explanation", "source", "code", "stdout", "stderr"].includes(key))) fact.classList.add("coding-fact--structured");
      const content = node("dd", ""); content.append(valueBody(value, key, options, depth));
      fact.append(node("dt", "", labels[key] || key), content);
      list.append(fact);
    }
    return list;
  }

  function renderData(value, options = {}) {
    if (typeof value === "string") {
      const parsed = /^[\[{]/.test(value.trimStart()) ? json(value) : null;
      return parsed && typeof parsed === "object" ? renderData(parsed, options) : outputBlock("Ergebnis", value);
    }
    if (!value || typeof value !== "object" || Array.isArray(value)) return valueBody(value, "result", options, 0);
    const result = node("div", "coding-data");
    const rest = { ...value };
    for (const key of ["entries", "matches", "results", "sources", "citations"]) {
      if (!Array.isArray(value[key])) continue;
      const list = node("div", "coding-result-list");
      list.dataset.timelineKey = key;
      for (const [index, item] of value[key].entries()) {
        const row = node("div", "coding-result");
        row.dataset.timelineKey = `${key}-${index}`;
        if (!item || typeof item !== "object") row.append(node("span", "", item));
        else {
          const title = item.path || item.title || item.url || item.name;
          const details = { ...item };
          if (title) {
            const label = item.line ? `${title}:${item.line}` : title;
            let link = null;
            try { if (/^https?:$/.test(new URL(item.url).protocol)) link = node("a", "coding-result__title", label); } catch { /* Plain text for other targets. */ }
            if (link) { link.href = item.url; link.target = "_blank"; link.rel = "noopener noreferrer"; }
            row.append(link || node("strong", "coding-result__title", label));
            for (const key of ["path", "title", "name"]) if (details[key] === title) delete details[key];
            if (item.line) delete details.line;
          }
          for (const key of ["text", "snippet", "content"]) {
            if (typeof details[key] !== "string") continue;
            row.append(node("pre", "coding-result__text", details[key]));
            delete details[key];
          }
          if (Object.keys(details).length) row.append(fields(details, "coding-facts", options));
        }
        list.append(row);
      }
      if (!value[key].length) list.append(node("p", "coding-note", "Keine Treffer."));
      result.append(list);
      delete rest[key];
    }
    for (const key of ["content", "text", "answer", "summary"]) {
      if (typeof rest[key] !== "string") continue;
      if (["answer", "summary"].includes(key)) result.append(fields({ [key]: rest[key] }, "coding-facts", options));
      else result.append(outputBlock("Inhalt", rest[key]));
      delete rest[key];
    }
    if (Object.keys(rest).length) result.append(fields(rest, "coding-facts", options));
    return result;
  }

  function processOutput(output, running) {
    const body = node("div", "coding-process-output");
    if (output.stdout) body.append(outputBlock("Standardausgabe", output.stdout, "terminal"));
    if (output.stderr) body.append(outputBlock("Fehlerausgabe", output.stderr, "stderr"));
    if (!output.stdout && !output.stderr) body.append(node("p", "coding-note", running ? "Warte auf die erste Ausgabe …" : "Der Prozess hat keine Textausgabe geliefert."));
    const facts = {};
    if (output.exitCode != null) facts.Exitcode = output.exitCode;
    if (output.elapsedMilliseconds != null) facts.Laufzeit = `${(output.elapsedMilliseconds / 1000).toLocaleString("de-DE", { maximumFractionDigits: 1 })} s`;
    if (output.timedOut) facts.Status = "Zeitlimit erreicht";
    if (output.truncated) facts.Hinweis = "Die Prozessausgabe wurde an der Erfassungsgrenze gekürzt.";
    if (Object.keys(facts).length) body.append(fields(facts));
    return body;
  }

  function stepBody(step, status, options) {
    const body = node("div", "coding-step__detail");
    const input = json(step.inputJson), output = json(step.outputJson);
    const running = !terminalStates.has(status);
    const hasInput = input !== null, hasOutput = output !== null;
    if (!hasInput && !hasOutput) {
      // Historic messages did not store structured input/output or text positions.
      // Their original details remain readable without inventing an execution log.
      if (step.detail) {
        body.append(options.renderMarkdown(step.detail));
        options.enhanceCodeBlocks?.(body);
      }
      return body;
    }
    options = { ...options, sourceLanguage: step.tool === "math.formalProof" ? "lean" : step.tool === "research.code.write" ? "python" : step.tool === "coding.renderHtml" ? "html" : options.sourceLanguage };
    const part = (label, key) => {
      const section = node("section", `coding-receipt-section coding-receipt-section--${key}`);
      section.dataset.timelineKey = `receipt-${key}`; section.setAttribute("aria-label", label);
      section.append(node("h3", "coding-receipt-heading", label));
      const content = node("div", "coding-receipt-body"); section.append(content); body.append(section); return content;
    };
    const parameters = hasInput ? part("Parameter", "input") : null;
    const result = part("Ergebnis", "output");
    if (step.tool === "coding.command") {
      if (hasInput && input && typeof input === "object" && !Array.isArray(input)) {
        const quote = value => /[\s"']/.test(String(value)) ? `'${String(value).replace(/'/g, "''")}'` : String(value);
        const command = [input.executable, ...(Array.isArray(input.arguments) ? input.arguments : input.arguments == null ? [] : [input.arguments])].filter(value => value != null).map(quote).join(" ");
        parameters.append(outputBlock("Befehl", command, "command"));
        parameters.append(fields({ Arbeitsordner: input.workingDirectory || ".", ...(input.timeoutSeconds ? { Zeitlimit: `${input.timeoutSeconds} s` } : {}) }, "coding-facts", options));
        const extra = Object.fromEntries(Object.entries(input).filter(([key]) => !["executable", "arguments", "workingDirectory", "timeoutSeconds"].includes(key)));
        if (Object.keys(extra).length) parameters.append(fields(extra, "coding-facts", options));
      } else if (hasInput) parameters.append(valueBody(input, "parameters", options, 0));
      if (hasOutput && output && typeof output === "object" && !Array.isArray(output)) {
        result.append(processOutput(output, running));
        const other = Object.fromEntries(Object.entries(output).filter(([key]) => !["stdout", "stderr", "exitCode", "elapsedMilliseconds", "timedOut", "truncated", "success"].includes(key)));
        if (Object.keys(other).length) result.append(fields(other, "coding-facts", options));
      } else if (hasOutput) result.append(renderData(output, options));
      else result.append(node("p", "coding-note", "Prozess wird gestartet …"));
      return body;
    }
    if (hasInput && input && typeof input === "object" && !Array.isArray(input)) {
      const inputFields = { ...input };
      // The full content is displayed once in the actual patch/preview below.
      if (["coding.write", "coding.edit"].includes(step.tool)) {
        for (const key of ["content", "oldText", "newText", "edits"]) delete inputFields[key];
      }
      if (Object.keys(inputFields).length) parameters.append(fields(inputFields, "coding-facts coding-facts--input", options));
      if (["coding.write", "coding.edit"].includes(step.tool) && !output?.diff) {
        if (typeof input.content === "string") parameters.append(outputBlock("Angeforderter Dateiinhalt", input.content));
        const edits = input.edits || (input.oldText != null ? [input] : []);
        for (const [index, edit] of edits.entries()) {
          parameters.append(outputBlock(`Ersetzung ${index + 1} · bisher`, edit.oldText || ""));
          parameters.append(outputBlock(`Ersetzung ${index + 1} · vorgesehen`, edit.newText || ""));
        }
      }
    } else if (hasInput) parameters.append(valueBody(input, "parameters", options, 0));
    if (output && (typeof output.diff === "string" || output.diff?.stdout != null)) {
      const diff = typeof output.diff === "string" ? output.diff : output.diff.stdout;
      const label = step.tool === "coding.gitDiff" ? "Git-Diff" : output.applied === true || status === "completed" && output.success !== false
        ? "Angewendete Änderung" : "Vorbereitete Änderung · noch nicht angewendet";
      if (diff) result.append(diffView(diff, label));
      else result.append(node("p", "coding-note", output.diffTruncated ? "Der Diff überschreitet die Erfassungsgrenze." : "Keine Textänderungen."));
      if (diff && (output.diffTruncated || output.diff?.truncated)) result.append(node("p", "coding-note", step.tool === "coding.gitDiff"
        ? "Der Git-Diff ist ein gekürzter Auszug. An Auslassungen werden keine Zeilennummern behauptet."
        : "Der Diff ist gekürzt; angezeigt werden vollständig erfasste Änderungsblöcke."));
      if (output.stagedDiff?.stdout) result.append(diffView(output.stagedDiff.stdout, "Vorgemerkte Änderungen"));
      const remaining = { ...output };
      delete remaining.diff; delete remaining.stagedDiff;
      if (remaining.stdout === diff) delete remaining.stdout;
      if (remaining.stderr) { result.append(outputBlock("Git-Fehlerausgabe", remaining.stderr, "stderr")); delete remaining.stderr; }
      if (!output.diffTruncated) { delete remaining.addedLines; delete remaining.removedLines; }
      if (typeof output.diff === "object") {
        const { stdout, ...diagnostics } = output.diff;
        if (diagnostics.stderr || diagnostics.exitCode || diagnostics.truncated) result.append(fields(diagnostics, "coding-facts", options));
      }
      if (output.stagedDiff && (output.stagedDiff.stderr || output.stagedDiff.exitCode || output.stagedDiff.truncated)) {
        const { stdout, ...diagnostics } = output.stagedDiff;
        result.append(fields(diagnostics, "coding-facts", options));
      }
      if (remaining.status && typeof remaining.status === "object") {
        result.append(outputBlock("Git-Status", remaining.status.stdout || "Keine Einträge."));
        const { stdout, ...diagnostics } = remaining.status;
        if (diagnostics.stderr || diagnostics.exitCode || diagnostics.truncated) result.append(fields(diagnostics, "coding-facts", options));
        delete remaining.status;
      }
      if (Object.keys(remaining).length) result.append(fields(remaining, "coding-facts", options));
    } else if (hasOutput) result.append(renderData(output, options));
    else if (running) result.append(node("p", "coding-note", ["coding.edit", "coding.write"].includes(step.tool)
      ? "Datei wird geprüft; der Diff erscheint vor dem Speichern." : "Ausführung läuft …"));
    else result.append(node("p", "coding-note", "Kein Ergebnis verfügbar."));
    if (output?.error || output?.errorCode) body.classList.add("coding-step__detail--error");
    return body;
  }

  function stepSummary(step, status) {
    const input = json(step.inputJson), output = json(step.outputJson);
    const compact = value => String(value ?? "").replace(/\s+/g, " ").trim();
    const shorten = (value, maximum) => {
      const text = compact(value);
      return text.length > maximum ? `${text.slice(0, maximum - 1)}…` : text;
    };
    const lastLine = value => String(value || "").trim().split(/\r?\n/).filter(line => line.trim()).at(-1) || "";
    let target = input?.path || input?.url || input?.query || input?.title || "";
    let progress = "";
    if (step.tool === "coding.command") {
      target = [input?.executable, ...(input?.arguments || [])].filter(value => value != null).join(" ");
      const stdout = lastLine(output?.stdout), stderr = lastLine(output?.stderr);
      const lineLimit = stdout && stderr ? 50 : 120;
      progress = [output?.exitCode != null ? `Exitcode ${output.exitCode}` : "",
        stdout ? `stdout: ${shorten(stdout, lineLimit)}` : "",
        stderr ? `stderr: ${shorten(stderr, lineLimit)}` : "",
        !stdout && !stderr ? output?.error || output?.errorCode || "" : ""].filter(Boolean).join(" · ");    } else if (output?.error || output?.errorCode) {
      progress = output.error || output.errorCode;
    } else if (Array.isArray(output?.entries)) {
      progress = `${output.entries.length} ${output.entries.length === 1 ? "Eintrag" : "Einträge"}`;
    } else if (Array.isArray(output?.matches) || Array.isArray(output?.results)) {
      progress = `${(output.matches || output.results).length} Treffer`;
    } else if (Array.isArray(output?.sources)) {
      progress = `${output.sources.length} ${output.sources.length === 1 ? "Quelle" : "Quellen"}`;
    } else if (["coding.write", "coding.edit", "coding.gitDiff"].includes(step.tool)) {
      const counts = [Number.isFinite(output?.addedLines) ? `+${output.addedLines}` : "",
        Number.isFinite(output?.removedLines) ? `−${output.removedLines}` : ""].filter(Boolean).join(" / ");
      const applied = step.tool !== "coding.gitDiff" && output && (output.applied === true || status === "completed" && output.success !== false);
      progress = [counts, applied ? "Gespeichert" : ""].filter(Boolean).join(" · ");
    } else if (Number.isFinite(output?.totalLines)) {
      progress = `${output.totalLines} ${output.totalLines === 1 ? "Zeile" : "Zeilen"}`;
    } else if (step.tool === "coding.renderHtml" && status === "completed" && step.previewHtml) {
      progress = "Vorschau verfügbar";
    }
    if (output?.truncated || output?.diffTruncated) progress = [progress, "Ausgabe gekürzt"].filter(Boolean).join(" · ");
    // Only this compact header is shortened; the disclosure keeps all captured data.
    return [shorten(target, 100), shorten(progress, 160)].filter(Boolean).join(" · ");
  }

  function stepContent(step, status, messageId, options) {
    const content = node("div", "coding-step__content");
    let copy;
    if (step.inputJson || step.outputJson) {
      copy = copyButton(JSON.stringify({ tool: step.tool, status, explanation: step.explanation,
        input: json(step.inputJson), output: json(step.outputJson) }, null, 2), "Daten kopieren");
      copy.classList.add("coding-step__copy");
    }
    const input = json(step.inputJson);
    const explanation = step.explanation && !/^Missum soll (?:coding|web)\./.test(step.explanation)
      ? step.explanation : input ? describe(step.tool, input) : "";
    if (explanation && explanation !== step.label) content.append(node("p", "coding-step__explanation", explanation));
    content.append(stepBody(step, status, options));
    if (step.tool === "coding.renderHtml" && status === "completed" && step.previewHtml) {
      const preview = node("button", "coding-preview-button", "HTML-Vorschau öffnen");
      preview.type = "button";
      preview.addEventListener("click", () => options.onPreview?.(messageId, String(step.id)));
      content.append(preview);
    }
    if (copy) content.append(copy);
    return content;
  }

  function syncDisclosure(disclosure) {
    const content = disclosure.querySelector(".coding-step__content");
    if (!disclosure.hasAttribute("open")) content?.remove();
    else if (!content && disclosure._codingCreateContent) disclosure.append(disclosure._codingCreateContent());
  }

  function disclosureToggled(event) {
    syncDisclosure(event.currentTarget || event.target);
  }

  function displayStepStatus(step, message) {
    const status = String(step.status || "running").toLowerCase();
    const output = json(step.outputJson);
    if (status === "interrupted" && (output?.state === "steered" || output?.generation?.state === "steered")) return "steered";
    if (!terminalStates.has(status) && terminalStates.has(String(message.status).toLowerCase())) {
      return message.status === "steered" ? "steered" : "interrupted";
    }
    return status;
  }

  function renderStep(step, index, message, options, previous, stepArtifacts) {
    const status = displayStepStatus(step, message);
    const nativeSummary = options.toolSummary?.(step);
    const summary = nativeSummary == null ? stepSummary(step, status) : String(nativeSummary);
    const section = node("section", `coding-step coding-step--${status}`);
    section.dataset.stepId = String(step.id);
    section.dataset.timelineKey = `tool-${step.id}`;
    // Artifacts can arrive after the tool result. Include their metadata so an
    // unchanged receipt cannot keep a stale (or missing) preview in the DOM.
    const signature = [step.tool, step.label, status, index, step.inputJson, step.outputJson, step.detail, step.explanation,
      step.previewHtml, summary, JSON.stringify(stepArtifacts || [])];
    section._codingSignature = signature;
    if (previous?._codingSignature?.every((value, i) => value === signature[i])) {
      section._codingKeep = previous;
      return section;
    }
    const label = step.label || step.tool;
    section.setAttribute("aria-label", `Schritt ${index + 1}: ${label}`);
    const disclosure = node("details", "coding-step__disclosure");
    const previousDisclosure = previous?.querySelector(".coding-step__disclosure");
    if (previousDisclosure ? previousDisclosure.hasAttribute("open")
      : options.codingToolStepsExpanded === true) {
      disclosure.setAttribute("open", "");
    }
    const title = node("summary", "coding-step__title");
    const number = node("span", "coding-step__icon");
    const glyph=toolIcon(step.tool);number.dataset.iconKey=glyph.key;number.append(glyph.svg);
    number.setAttribute("aria-hidden", "true");
    const name = node("strong", "coding-step__name", label);
    name.title = step.tool;
    const state = node("span", "coding-step__status", !terminalStates.has(status) ? "…" : ["failed", "denied", "cancelled", "interrupted"].includes(status) ? statusLabels[status] : "");
    state.setAttribute("role", "status");
    state.setAttribute("aria-live", "polite");
    const chevron = node("span", "coding-step__chevron", "›");
    chevron.setAttribute("aria-hidden", "true");
    title.append(number, name, state, chevron);
    const mutation = ["coding.write", "coding.edit", "coding.undo"].includes(step.tool);
    const input = json(step.inputJson), output = json(step.outputJson);
    const path = String(output?.path || input?.path || "");
    const basename = path.replace(/\\/g, "/").split("/").at(-1);
    const counts = node("span", "coding-step__counts");
    if (mutation && Number.isFinite(output?.addedLines)) counts.append(node("span", "review-added", `+${output.addedLines}`));
    if (mutation && Number.isFinite(output?.removedLines)) counts.append(node("span", "review-removed", `−${output.removedLines}`));
    if (counts.children.length) title.insertBefore(counts, chevron);
    disclosure.append(title);
    const updateHeader = (target = disclosure) => { target.querySelector(".coding-step__name").textContent = mutation && !target.hasAttribute("open") && basename ? basename : [label, summary].filter(Boolean).join(" · "); };
    disclosure._codingUpdateHeader = updateHeader;
    disclosure.addEventListener("toggle", event => { const target = event.currentTarget || event.target; target._codingUpdateHeader?.(target); }); updateHeader();
    // Retain the receipt, not thousands of hidden patch/text DOM nodes. Avoid
    // capturing previousTimeline here, which would retain older render trees.
    const contentOptions = { renderMarkdown: options.renderMarkdown, enhanceCodeBlocks: options.enhanceCodeBlocks,
      onPreview: options.onPreview };
    const messageId = String(message.id);
    disclosure._codingCreateContent = () => stepContent(step, status, messageId, contentOptions);
    disclosure.addEventListener("toggle", disclosureToggled);
    syncDisclosure(disclosure);
    section.append(disclosure);
    // Keep images visible even when verbose tool arguments/results are closed.
    // They remain part of this step, before all subsequent narration and tools.
    if (Array.isArray(stepArtifacts) && stepArtifacts.length && options.createArtifacts) {
      const artifacts = options.createArtifacts(stepArtifacts);
      artifacts.classList.add("coding-step__artifacts");
      section.append(artifacts);
    }
    return section;
  }

  const thinkingStates = new Map();
  const tokenNumber = value => Number.isSafeInteger(value) && value >= 0 ? value : null;
  const eventTime = value => { const time = typeof value === "number" ? value : Date.parse(value); return Number.isFinite(time) ? time : null; };
  const formatTokens = value => Math.max(0, Math.round(value)).toLocaleString("de-DE");

  function nativeThinking(message, steps, options) {
    const latest = steps.findLast(step => step.tool === "assistant.reasoning"
      && (!step.agentId || step.agentId === (options.reasoningAgentId || "")));
    const key = `${message.sessionId || options.sessionId || ""}:${message.id}`;
    const active = !terminalStates.has(String(message.status).toLowerCase());
    if (!active) {
      const ended = thinkingStates.get(key);
      if (ended?.timer != null) clearTimeout(ended.timer);
      thinkingStates.delete(key); return { enabled: true, show: false, latest };
    }
    const persisted = steps.findLast(step => step.tool === "assistant.progress" && json(step.outputJson)?.generation?.state);
    const generation = persisted ? json(persisted.outputJson).generation : null;
    const reasoningData = latest ? json(latest.outputJson) : null;
    const supplied = options.liveStatus;
    const live = supplied?.generationState ? supplied : generation ? {
      generationState: generation.state, generatedTokens: generation.generatedTokens,
      generationUpdatedAt: generation.updatedAt || persisted.updatedAt,
      processedPromptTokens: generation.processedPromptTokens ?? generation.inputTokens,
      totalPromptTokens: generation.promptTokens, promptProgress: generation.promptProgress,
      contextUsed: generation.contextUsed, runId: generation.runId,
    } : tokenNumber(reasoningData?.generatedTokens) !== null ? {
      generationState: "tokenProgress", generatedTokens: reasoningData.generatedTokens,
      generationUpdatedAt: latest.updatedAt, contextUsed: reasoningData.contextUsed,
      processedPromptTokens: reasoningData.processedPromptTokens, runId: reasoningData.runId,
    } : null;
    if (!live || live.messageId && String(live.messageId) !== String(message.id)) return { enabled: false, latest };
    const runId = String(live.runId || options.runId || message.runId || "");
    const now = typeof options.now === "function" ? options.now() : Number.isFinite(options.now) ? options.now : Date.now();
    let state = thinkingStates.get(key);
    if (!state || state.runId !== runId) {
      if (state?.timer != null) clearTimeout(state.timer);
      state = { runId, latestAt: null, tokenAt: null, contentAt: null, consumedAt: null, content: null,
        reasoningId: null, reasoning: null, generated: 0, generating: false, shown: false, phase: null,
        processed: null, total: null, fraction: null, phaseAt: null, contextUsed: null, timer: null, slot: null, label: null };
      thinkingStates.set(key, state);
      if (thinkingStates.size > 256) {
        const first = thinkingStates.keys().next().value, old = thinkingStates.get(first);
        if (old.timer != null) clearTimeout(old.timer);
        thinkingStates.delete(first);
      }
    }
    const at = eventTime(live.generationUpdatedAt), phase = live.generationState;
    if (!(at !== null && state.latestAt !== null && at < state.latestAt)) {
      if (at !== null) state.latestAt = at;
      if (Number.isFinite(live.contextUsed) && live.contextUsed >= 0) state.contextUsed = live.contextUsed;
      if (phase && !["codingWaiting", "reasoningDelta", "contentDelta"].includes(phase)) {
        const samePhase = phase === state.phase;
        const processed = tokenNumber(live.processedPromptTokens), total = tokenNumber(live.totalPromptTokens);
        const freshCounts = processed !== null || total !== null;
        state.processed = processed ?? (samePhase ? state.processed : null);
        state.total = total ?? (samePhase ? state.total : null);
        state.fraction = Number.isFinite(live.promptProgress) ? Math.max(0, Math.min(1, live.promptProgress)) : samePhase && !freshCounts ? state.fraction : null;
        state.phase = phase; if (at !== null) state.phaseAt = at;
      }
      if (!["codingWaiting", "reasoningDelta", "contentDelta"].includes(phase)) {
        const generated = tokenNumber(live.generatedTokens);
        if (phase === "tokenProgress" && generated > 0 && at !== null) {
          if (!(state.consumedAt !== null && at <= state.consumedAt) && generated > state.generated) {
            state.tokenAt = at; state.generated = generated; state.generating = true;
          }
        } else {
          state.generating = false; state.tokenAt = null;
          if (["generationStarted", "generationRetry"].includes(phase)) state.generated = 0;
        }
      }
    }
    if (latest && ["running", "pending"].includes(String(latest.status).toLowerCase()) && String(latest.detail || "").trim()) {
      const reasoningAt = eventTime(latest.updatedAt);
      if (reasoningAt !== null) {
        if (state.reasoningId !== latest.id || state.reasoning !== latest.detail) {
          state.reasoningId = latest.id; state.reasoning = latest.detail;
          if (state.consumedAt === null || reasoningAt > state.consumedAt) { state.tokenAt = reasoningAt; state.generating = true; }
        }
        if (state.phaseAt === null || reasoningAt >= state.phaseAt) { state.phase = "reasoning"; state.phaseAt = reasoningAt; }
      }
    }
    let content = String(options.sanitizeText ? options.sanitizeText(message.content || "") : message.content || "").trimEnd();
    for (const step of steps) if (step.tool === "assistant.narration" && String(step.detail || "").trim()) content += `\0${step.id}\0${String(step.detail).trimEnd()}`;
    if (state.content !== content) {
      const first = state.content === null; state.content = content;
      if (content) {
        state.contentAt = now;
        if (!first || state.shown) {
          state.shown = false; state.generating = false; state.tokenAt = null;
          state.consumedAt = Math.max(state.latestAt ?? now, now);
          if (state.phaseAt === null || now >= state.phaseAt) { state.phase = "content"; state.phaseAt = now; }
        }
      }
    }
    const visible = options.visible !== false && document.visibilityState !== "hidden"
      && document.getElementById?.("conversation-pane")?.hidden !== true;
    const blocked = Boolean(message.liveStatus) || steps.some(step => ["running", "pending"].includes(String(step.status).toLowerCase())
      && !["assistant.reasoning", "assistant.progress", "assistant.narration", "assistant.steering", "subagent", "subagent.completed"].includes(step.tool)
      && !(options.createSubagentReceipt && acceptedSubagentManagement(step, steps.filter(item => item.tool === "subagent"))));
    const processing = state.phase === "promptProcessing";
    let show = processing ? visible && !blocked : visible && state.shown;
    if (!processing && visible && !state.shown && !blocked && state.generating && state.tokenAt !== null
      && now - state.tokenAt >= 0 && now - state.tokenAt < 8000 && (state.contentAt === null || now - state.contentAt >= 750)) {
      state.shown = true; show = true;
    }
    const slot = Math.floor(now / 1000);
    if (show && (state.slot !== slot || !state.label || processing !== state.wasProcessing)) {
      if (processing) {
        const fraction = state.fraction ?? (state.total > 0 && state.processed !== null ? Math.max(0, Math.min(1, state.processed / state.total)) : null);
        state.label = "Kontext wird verarbeitet" + (fraction !== null ? ` · ${Math.round(fraction * 100)} %` : "")
          + (state.processed !== null ? ` · ${formatTokens(state.processed)} Eingangstoken` : "");
      } else {
        const tokens = state.contextUsed ?? (tokenNumber(live.processedPromptTokens) !== null ? live.processedPromptTokens + state.generated : state.generated);
        state.label = `Denke nach · ${formatTokens(tokens)} Token`;
      }
      state.explanation = processing ? (state.processed !== null
        ? state.total !== null ? `${formatTokens(state.processed)} von ${formatTokens(state.total)} Eingangstoken verarbeitet. ` : `${formatTokens(state.processed)} Eingangstoken verarbeitet. ` : "")
        + "Der Denkprozess erscheint, sobald das Modell mit der Generierung beginnt." : null;
      state.slot = slot; state.wasProcessing = processing;
    }
    if (state.timer != null) { clearTimeout(state.timer); state.timer = null; }
    if (visible && options.onThinkingChanged && (show || !blocked && state.generating && state.tokenAt !== null && now - state.tokenAt < 8000)) {
      // Visibility follows the native 80ms poll; the label still changes only on
      // its once-per-second tick. Token events themselves never add counts.
      const waiting = !show && (state.contentAt !== null && now - state.contentAt < 750 || state.tokenAt > now);
      const delay = waiting ? 80 : Math.max(1, 1000 - now % 1000);
      state.timer = setTimeout(() => {
        state.timer = null;
        if (thinkingStates.get(key) === state) options.onThinkingChanged();
      }, delay);
    }
    return { enabled: true, show, processing, label: state.label, explanation: state.explanation, latest };
  }

  function renderReasoning(step, message, options, previous) {
    const status = displayStepStatus(step, message);
    const active = !terminalStates.has(status);
    const input = json(step.inputJson);
    const round = Number.isSafeInteger(input?.round) && input.round > 0 ? input.round : null;
    const compaction = input?.phase === "compaction";
    const text = String(step.detail || "");
    const section = node("section", `coding-reasoning coding-reasoning--${status}`);
    section.dataset.stepId = String(step.id);
    section.dataset.timelineKey = `tool-${step.id}`;
    // Model reasoning is a separate display channel, never answer narration or
    // a real execution. In particular, it must not enter read-aloud selection.
    section.dataset.speechExclude = "true";
    section.setAttribute("aria-label", ["Denkprozess", round ? `Runde ${round}` : "", compaction ? "Kontextverdichtung" : ""].filter(Boolean).join(" · "));
    const label = options.reasoningLabel || (compaction ? "Kontextverdichtung" : "Denkprozess");
    const signature = [status, round, text, compaction, Boolean(options.liveThinking), options.processingExplanation];
    section._codingSignature = signature;
    if (previous?._codingSignature?.every((value, i) => value === signature[i])) {
      if (options.liveThinking) {
        const name = previous.querySelector(".coding-reasoning__name");
        if (name.textContent !== label) name.textContent = label;
        if (previous.getAttribute("aria-label") !== label) previous.setAttribute("aria-label", label);
      }
      section._codingKeep = previous;
      return section;
    }

    const disclosure = node("details", "coding-step__disclosure coding-reasoning__disclosure");
    const previousDisclosure = previous?.querySelector(".coding-reasoning__disclosure");
    if (previousDisclosure?.hasAttribute("open")) disclosure.setAttribute("open", "");
    const title = node("summary", "coding-reasoning__title");
    const indicator = node("span", "coding-reasoning__indicator coding-step__icon");
    const glyph=toolIcon("assistant.reasoning");indicator.dataset.iconKey=glyph.key;indicator.append(glyph.svg);
    indicator.setAttribute("aria-hidden", "true");
    const name = node("strong", "coding-reasoning__name", label);
    title.append(indicator, name);
    const state = node("span", "coding-reasoning__status", active && !options.liveThinking ? "…" : ["failed", "cancelled", "interrupted"].includes(status) ? statusLabels[status] : "");
    state.setAttribute("role", "status");
    state.setAttribute("aria-live", "polite");
    const chevron = node("span", "coding-step__chevron", "›");
    chevron.setAttribute("aria-hidden", "true");
    title.append(state, chevron);
    // Only the closed preview is shortened. The expanded text retains every
    // received paragraph and code block, without an inner scroll container.
    disclosure.append(title);
    const renderMarkdown = options.renderMarkdown, enhanceCodeBlocks = options.enhanceCodeBlocks;
    disclosure._codingCreateContent = () => {
      const content = node("div", "coding-step__content coding-reasoning__content");
      const body = node("div", "message-content coding-reasoning__body");
      body.setAttribute("aria-live", "off");
      if (options.processingExplanation) body.append(node("p", "coding-note", options.processingExplanation));
      else if (text.trim()) {
        body.append(renderMarkdown(text));
        enhanceCodeBlocks?.(body);
      } else body.append(node("p", "coding-note", options.liveThinking ? "Noch kein Denkprozess vom Modell übermittelt." : active ? "Gedanken werden empfangen …" : "Kein Denktext empfangen."));
      content.append(body);
      return content;
    };
    disclosure.addEventListener("toggle", disclosureToggled);
    syncDisclosure(disclosure);
    section.append(disclosure);
    if (options.liveThinking) { section.classList.add("coding-live-thinking"); section.setAttribute("aria-label", label); }
    return section;
  }

  function render(message, steps, options) {
    const timeline = node("div", "coding-timeline");
    timeline.dataset.timelineKey = "timeline";
    const content = String(message.content || "");
    const previousByKey = new Map(options.previousTimeline
      ? [...options.previousTimeline.children].map(item => [item.dataset.timelineKey, item]) : []);
    const thinking = nativeThinking(message, steps, options);
    // Media artifacts bound to a tool step render inside that step so later
    // narration and actions stay below the preview instead of the message end.
    const stepArtifacts = new Map();
    const seenArtifacts = new Set();
    for (const artifact of Array.isArray(message.artifacts) ? message.artifacts : []) {
      if (String(artifact?.metadata?.role || "").toLowerCase() === "vision_input") continue;
      if (!artifact?.stepId || seenArtifacts.has(String(artifact.id))) continue;
      seenArtifacts.add(String(artifact.id));
      const list = stepArtifacts.get(String(artifact.stepId)) || [];
      list.push(artifact);
      stepArtifacts.set(String(artifact.stepId), list);
    }
    let offset = 0;
    function narration(text, key, streaming = false) {
      const visible = options.sanitizeText ? options.sanitizeText(text) : text;
      if (!visible.trim()) return;
      const block = node("div", "message-content coding-narration");
      block.dataset.timelineKey = key;
      block._codingSignature = [visible, streaming, terminalStates.has(String(message.status).toLowerCase())];
      const previous = previousByKey.get(key);
      if (previous?._codingSignature?.every((value, i) => value === block._codingSignature[i])) {
        block._codingKeep = previous;
        timeline.append(block);
        return;
      }
      block.append(options.renderMarkdown(visible));
      options.enhanceCodeBlocks?.(block);
      timeline.append(block);
    }
    let executionIndex = 0;
    const subagentReceipts = steps.filter(step => step.tool === "subagent");
    steps.filter(step => step.kind !== "phase").forEach(step => {
      if (Number.isInteger(step.contentOffset)) {
        const end = Math.max(offset, Math.min(content.length, step.contentOffset));
        narration(content.slice(offset, end), `text-before-${step.id}`);
        offset = end;
      }
      const previous = previousByKey.get(`tool-${step.id}`);
      if (options.createSubagentReceipt && acceptedSubagentManagement(step, subagentReceipts)) return;
      if (["subagent", "subagent.completed"].includes(step.tool) && options.createSubagentReceipt) {
        const receipt = options.createSubagentReceipt(step);
        if (receipt) {
          receipt.dataset.timelineKey = `tool-${step.id}`; receipt.dataset.stepId = String(step.id); receipt.dataset.speechExclude = "true";
          receipt._codingSignature = [step.tool, step.status, step.inputJson, step.outputJson, step.detail, step.agentId];
          if (previous?._codingSignature?.every((value, index) => value === receipt._codingSignature[index])) { previous._refreshSubagentReceipt?.(); receipt._codingKeep = previous; }
          timeline.append(receipt); return;
        }
      }
      const rendered = step.tool === "assistant.steering"
        ? renderSteering(step)
        : ["assistant.narration", "assistant.progress"].includes(step.tool)
        ? renderAgentActivity(step, message, options)
        : step.tool === "assistant.reasoning"
        ? renderReasoning(step, message, options, previous)
        : renderStep(step, executionIndex++, message, options, previous, stepArtifacts.get(String(step.id)));
      if (step === thinking.latest) {
        const history = rendered._codingKeep || rendered;
        if (thinking.show && !thinking.processing) { rendered.setAttribute("hidden", ""); history.setAttribute("hidden", ""); }
        else { rendered.removeAttribute("hidden"); history.removeAttribute("hidden"); }
      }
      timeline.append(rendered);
    });
    const active = !terminalStates.has(String(message.status).toLowerCase());
    narration(content.slice(offset), "text-tail", active && !steps.some(step => !terminalStates.has(String(step.status).toLowerCase())));
    if (thinking.show) {
      const id = `live-thinking:${message.id}:${thinking.latest?.id || "empty"}`;
      timeline.append(renderReasoning({ id, tool: "assistant.reasoning", status: "running", detail: thinking.latest?.detail || "", inputJson: "{}" }, message,
        { ...options, liveThinking: true, reasoningLabel: thinking.label, processingExplanation: thinking.explanation }, previousByKey.get(`tool-${id}`)));
    } else if (active && !thinking.enabled && options.liveStatus?.status) {
      const phase = node("div", "coding-live-phase");
      phase.dataset.timelineKey = "live-phase";
      phase.setAttribute("role", "status");
      phase.setAttribute("aria-live", "polite");
      phase.append(node("span", "message-status-spinner"), node("span", "", options.liveStatus.status));
      if (options.liveStatus.detail) phase.append(node("span", "coding-live-phase__detail", options.liveStatus.detail));
      timeline.append(phase);
    }
    return timeline;
  }

  function acceptedSubagentManagement(step, receipts) {
    if (!["subagent.spawn", "subagent.wait", "subagent.resume"].includes(step.tool) || ["failed", "denied", "cancelled", "interrupted"].includes(step.status)) return false;
    const output = json(step.outputJson), input = json(step.inputJson);
    if (["failed", "denied", "disabled", "unavailable", "rejected"].includes(output?.status) || output?.success === false) return false;
    const agentId = output?.agentId || input?.agentId || step.agentId, runId = output?.runId || input?.runId;
    return receipts.some(receipt => { const accepted = json(receipt.outputJson), id = accepted?.agentId || receipt.agentId; return id && accepted?.runId && (agentId && agentId === id || runId && runId === accepted.runId); });
  }

  function renderAgentActivity(step, message, options) {
    const section = node("section", `agent-activity agent-activity--${step.tool.split(".")[1]}`);
    section.dataset.timelineKey = `tool-${step.id}`;
    section.dataset.stepId = String(step.id);
    const round = json(step.inputJson)?.round;
    if (step.tool === "assistant.narration") {
      if (round) section.append(node("span", "agent-activity__round", `Runde ${round}`));
      const body = node("div", "message-content");
      body.append(options.renderMarkdown(String(step.detail || "")));
      options.enhanceCodeBlocks?.(body);
      section.append(body);
    } else {
      const displayStatus = displayStepStatus(step, message);
      const running = !terminalStates.has(displayStatus);
      const status = node("div", "agent-activity__status");
      status.setAttribute("role", "status");
      if (running) status.append(node("span", "message-status-spinner"));
      status.append(node("span", "", displayStatus === "steered" ? "Umgeleitet" : String(step.detail || (running ? "Arbeitet" : "Abgeschlossen"))));
      section.append(status);
      const data = json(step.outputJson);
      const generation = data?.generation || {}, context = data?.context || {}, measured = data?.metrics?.metrics || data?.metrics || {};
      const inputTokens = data?.metrics?.inputTokens ?? measured.inputTokens ?? generation.inputTokens ?? generation.promptTokens;
      const outputTokens = data?.metrics?.outputTokens ?? measured.outputTokens ?? generation.outputTokens ?? generation.generatedTokens;
      const hasTokens = Number.isFinite(inputTokens) || Number.isFinite(outputTokens);
      const totalTokens = (Number.isFinite(inputTokens) ? inputTokens : 0) + (Number.isFinite(outputTokens) ? outputTokens : 0);
      const metrics = [
        hasTokens ? `${totalTokens.toLocaleString("de-DE")} Token` : "",
        Number.isFinite(context.contextLimit) ? `Kontext: ${(context.estimatedInputTokens ?? inputTokens ?? 0).toLocaleString("de-DE")} / ${context.contextLimit.toLocaleString("de-DE")}` : ""
      ].filter(Boolean);
      if (metrics.length) section.append(node("p", "agent-activity__metrics", metrics.join(" · ")));
    }
    return section;
  }

  function renderSteering(step) {
    const section = node("section", "steering-message");
    section.dataset.timelineKey = `tool-${step.id}`;
    section.dataset.stepId = String(step.id);
    section.dataset.speechExclude = "true";
    const input = json(step.inputJson);
    const label = node("div", "steering-message__label", "Du · Umlenkung");
    const body = node("div", "steering-message__text", String(step.detail || input?.text || ""));
    const status = node("span", "steering-message__status", step.status === "completed" ? "Angewendet"
      : terminalStates.has(String(step.status).toLowerCase()) ? "Nicht angewendet" : "Übernommen · wird angewendet");
    section.append(label, body, status);
    return section;
  }

  // Keep completed rows and unchanged text nodes in place while output grows.
  // This preserves selection and avoids resetting embedded controls on each token.
  function reconcile(current, next) {
    if (next._codingKeep === current) return current;
    if (!current || current.nodeType !== next.nodeType || current.nodeName !== next.nodeName) {
      current?.replaceWith(next);
      return next;
    }
    if (current.nodeType === 3) {
      if (current.nodeValue !== next.nodeValue) {
        if (next.nodeValue.startsWith(current.nodeValue) && typeof current.appendData === "function") current.appendData(next.nodeValue.slice(current.nodeValue.length));
        else current.nodeValue = next.nodeValue;
      }
      return current;
    }
    if (current.nodeType !== 1) return current;
    if (next._codingSignature) current._codingSignature = next._codingSignature;
    // Native disclosure state belongs to the reader. Streaming output and setting
    // changes update the contents without opening or closing an existing step.
    if (current.nodeName === "DETAILS" && current.classList.contains("coding-step__disclosure")) {
      if (current.hasAttribute("open")) next.setAttribute("open", "");
      else next.removeAttribute("open");
      current._codingCreateContent = next._codingCreateContent;
      current._codingUpdateHeader = next._codingUpdateHeader;
      // Reconciliation can also be called without the previous render cache.
      // Build only the reader's actual open state, then reconcile its contents.
      syncDisclosure(next);
    }
    for (const attr of [...current.attributes]) if (!next.hasAttribute(attr.name)) current.removeAttribute(attr.name);
    for (const attr of [...next.attributes]) if (current.getAttribute(attr.name) !== attr.value) current.setAttribute(attr.name, attr.value);
    // Event handlers capture the most recent copy payload and message footer data.
    if (["BUTTON", "A", "AUDIO", "VIDEO", "IFRAME"].includes(current.nodeName)) {
      current.replaceWith(next);
      return next;
    }
    const oldChildren = [...current.childNodes];
    const keyed = new Map(oldChildren.filter(child => child.dataset?.timelineKey).map(child => [child.dataset.timelineKey, child]));
    let index = 0;
    for (const child of [...next.childNodes]) {
      const key = child.dataset?.timelineKey;
      const old = key ? keyed.get(key) : oldChildren[index]?.dataset?.timelineKey ? null : oldChildren[index];
      const retained = old ? reconcile(old, child) : child;
      const at = current.childNodes[index];
      if (retained !== at) current.insertBefore(retained, at || null);
      index++;
    }
    while (current.childNodes.length > index) current.lastChild.remove();
    return current;
  }

  globalThis.missumCodingTimeline = { render, parseUnifiedDiff, reconcile };
})();
