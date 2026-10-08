(function () {
  "use strict";

  const mathRenderCache = new Map();
  const maxMathRenderCacheEntries = 600;

  function copyText(text) {
    if (globalThis.missumBridge && typeof globalThis.missumBridge.post === "function") {
      globalThis.missumBridge.post("message.copy", { text });
      return Promise.resolve();
    }
    if (globalThis.navigator?.clipboard?.writeText) {
      return globalThis.navigator.clipboard.writeText(text);
    }
    return Promise.reject(new Error("Clipboard unavailable"));
  }

  function katexStrictMode(errorCode) {
    return errorCode === "unicodeTextInMathMode" ? "ignore" : "error";
  }

  function normalizeEscapedLatex(tex) {
    const source = String(tex || "");
    // Match NativeMathRenderer: decode retained JSON escaping only when every
    // control word is escaped. A valid matrix row break may be immediately
    // followed by a letter (B_p\\\\B_\\phi); its whitespace is not significant.
    let escapedCommands = false;
    for (let index = 0; index < source.length; index += 1) {
      if (source[index] !== "\\") continue;
      const start = index;
      while (source[index] === "\\") index += 1;
      const slashes = index - start;
      if (index >= source.length || !/[A-Za-z]/.test(source[index])) continue;
      if (slashes % 2 === 1) return source;
      if (slashes === 2 && index + 1 < source.length && /[A-Za-z]/.test(source[index + 1])) escapedCommands = true;
    }
    if (!escapedCommands) return source;

    let normalized = "";
    for (let index = 0; index < source.length; index += 1) {
      if (source[index] !== "\\") {
        normalized += source[index];
        continue;
      }
      const start = index;
      while (source[index] === "\\") index += 1;
      const slashes = index - start;
      const commandFollows = index < source.length && /[A-Za-z,;!:{}]/.test(source[index]);
      normalized += "\\".repeat(slashes >= 4 && slashes % 2 === 0 ? slashes / 2 : slashes === 2 && commandFollows ? 1 : slashes);
      index -= 1;
    }
    return normalized;
  }

  function normalizedMathParts(rawMath, renderLatex) {
    const source = String(rawMath || "").trim();
    let display = false;
    let tex = source;
    if (source.startsWith("\\[") && source.endsWith("\\]")) {
      display = true;
      tex = source.slice(2, -2).trim();
    } else if (source.startsWith("\\(") && source.endsWith("\\)")) {
      tex = source.slice(2, -2).trim();
    } else if (source.startsWith("$$") && source.endsWith("$$") && source.length > 4) {
      display = true;
      tex = source.slice(2, -2).trim();
    } else if (source.startsWith("$") && source.endsWith("$") && source.length > 2) {
      tex = source.slice(1, -1).trim();
    }

    tex = normalizeEscapedLatex(renderLatex ?? tex)
      .replace(/\u00a0/g, "~")
      .replace(/[\u2009\u202f]/g, "\\,")
      .replace(/\\text\{([^{}]*[\u00b7\u22c5][^{}]*)\}/g, (_match, body) => (
        body.split(/[\u00b7\u22c5]/).map(part => `\\text{${part}}`).join("\\cdot")
      ))
      .replace(/[\u00b7\u22c5]/g, "\\cdot");
    return { source, tex, display };
  }

  function cachedKatexResult(tex, displayMode) {
    if (!globalThis.katex || typeof globalThis.katex.renderToString !== "function") return { html: "", error: "Der Formelrenderer ist nicht verfügbar." };
    const key = `${displayMode ? "display" : "inline"}\n${tex}`;
    if (mathRenderCache.has(key)) return mathRenderCache.get(key);

    let html = "";
    let error = "";
    try {
      html = globalThis.katex.renderToString(tex, {
        displayMode,
        throwOnError: true,
        output: "html",
        strict: katexStrictMode,
        trust: false
      });
    } catch (exception) {
      error = String(exception?.message || "Die Formel konnte nicht dargestellt werden.").slice(0, 500);
    }
    const result = { html, error };
    mathRenderCache.set(key, result);
    if (mathRenderCache.size > maxMathRenderCacheEntries) {
      mathRenderCache.delete(mathRenderCache.keys().next().value);
    }
    return result;
  }

  function createSelectableMathNode(rawMath, renderLatex) {
    const { source, tex, display } = normalizedMathParts(rawMath, renderLatex);
    const wrapper = document.createElement("span");
    wrapper.className = `math-selectable${display ? " display" : ""}`;
    wrapper.setAttribute("aria-label", `LaTeX: ${source}`);
    wrapper.title = "LaTeX kopieren";

    const rendered = document.createElement("span");
    rendered.className = "math-render";
    rendered.setAttribute("aria-hidden", "true");
    const katexResult = cachedKatexResult(tex, display);

    const sourceText = document.createElement("span");
    sourceText.className = "math-source-text";
    sourceText.textContent = source;

    if (katexResult.html) {
      rendered.innerHTML = katexResult.html;
      rendered.dataset.mathTypeset = "true";
      wrapper.append(rendered, sourceText);
    } else {
      sourceText.classList.add("fallback");
      wrapper.classList.add("invalid");
      wrapper.dataset.mathError = katexResult.error;
      wrapper.title += `\n${katexResult.error}`;
      wrapper.append(sourceText);
    }

    wrapper.addEventListener("click", event => {
      const selection = typeof globalThis.getSelection === "function"
        ? String(globalThis.getSelection() || "")
        : "";
      if (selection) return;
      event.preventDefault();
      event.stopPropagation();
      copyText(source).then(() => {
        wrapper.classList.add("copied");
        globalThis.setTimeout(() => wrapper.classList.remove("copied"), 900);
      }).catch(() => {
        wrapper.title = "Kopieren fehlgeschlagen";
        globalThis.setTimeout(() => { wrapper.title = "LaTeX kopieren"; }, 1200);
      });
    });
    return wrapper;
  }

  function isInlineDollarMath(rawMath) {
    const source = String(rawMath || "");
    if (!source.startsWith("$") || !source.endsWith("$") || source.startsWith("$$") || source.length <= 2) {
      return false;
    }
    const body = source.slice(1, -1);
    return body.trim() === body && body.length > 0;
  }

  function isEscapedDelimiter(text, index) {
    let slashCount = 0;
    for (let cursor = index - 1; cursor >= 0 && text[cursor] === "\\"; cursor -= 1) slashCount += 1;
    return slashCount % 2 === 1;
  }

  function closingDelimiterIndex(text, startIndex, delimiter, sameLine) {
    let cursor = startIndex;
    while (cursor < text.length) {
      const found = text.indexOf(delimiter, cursor);
      if (found < 0 || (sameLine && text.slice(startIndex, found).includes("\n"))) return -1;
      if (!isEscapedDelimiter(text, found)) return found;
      cursor = found + delimiter.length;
    }
    return -1;
  }

  // Presentation-only counterpart of NativeLooseMath. Older persisted replies
  // often contain scientific notation without TeX delimiters. Recover only the
  // same bounded, unambiguous fragments; their original text remains selectable.
  const looseLetter = value => /^\p{L}$/u.test(value || "");
  const looseDigit = value => /^\p{Nd}$/u.test(value || "");
  const looseLetterOrDigit = value => looseLetter(value) || looseDigit(value);
  const looseAsciiDigit = value => /^[0-9]$/.test(value || "");
  const looseUppercase = value => /^\p{Lu}+$/u.test(value || "");
  const looseLabels = new Set(["std", "eff", "max", "min", "tot", "eq", "crit"]);
  const looseSuperscripts = Object.freeze({ "⁰": "0", "¹": "1", "²": "2", "³": "3", "⁴": "4", "⁵": "5", "⁶": "6", "⁷": "7", "⁸": "8", "⁹": "9", "⁻": "-", "⁺": "+" });

  function looseScientificSymbol(text) {
    if (text.length === 1 && looseLetter(text[0])) return true;
    if (text.length === 2 && ["d", "Δ"].includes(text[0]) && looseLetter(text[1])) return true;
    if (text.length < 3 || !looseLetter(text[0]) || text[1] !== "_") return false;
    const label = text.slice(2);
    return label.length === 1 || looseLabels.has(label) || looseUppercase(label);
  }

  function looseLiteralEnd(text, start) {
    if (text.startsWith("](", start)) {
      let end = start + 2, depth = 1;
      while (end < text.length && !/[\r\n]/.test(text[end]) && depth > 0) {
        if (text[end] === "(") depth += 1;
        if (text[end] === ")") depth -= 1;
        end += 1;
      }
      return end;
    }
    if (start > 0 && !/\s/.test(text[start - 1]) && !"([<\"':".includes(text[start - 1])) return start;
    if (!looseLetterOrDigit(text[start]) && !"/\\".includes(text[start])
      && !text.startsWith("./", start) && !text.startsWith("../", start)) return start;
    let end = start;
    const limit = Math.min(text.length, start + 4096);
    while (end < limit && !/\s/.test(text[end]) && !"`|>".includes(text[end])) end += 1;
    const token = text.slice(start, end), slash = token.search(/[\\/]/), dot = token.lastIndexOf(".");
    const identifier = value => /^[\p{L}\p{Nd}_.-]+$/u.test(value);
    const isPath = token.startsWith("./") || token.startsWith("../") || token.startsWith("/")
      || token.startsWith("\\\\") || /^[A-Za-z]:[\\/]/.test(token)
      || slash > 0 && !/[=^(]/.test(token.slice(0, slash)) && identifier(token.slice(0, slash))
        && (slash === 1 || !looseScientificSymbol(token.slice(0, slash)));
    const host = token.split(/[/?#]/, 1)[0], hostDot = host.lastIndexOf(".");
    const isDomain = hostDot > 0 && host.length - hostDot >= 3 && host.length - hostDot <= 25
      && /^[A-Za-z0-9.-]+$/.test(host.slice(0, hostDot)) && /^[A-Za-z]+$/.test(host.slice(hostDot + 1));
    const isUrl = /^(?:https?:\/\/|www\.)/i.test(token) || token.includes("@") || isDomain;
    const equal = token.indexOf("=");
    const isAssignment = equal > 0 && identifier(token.slice(0, equal)) && !looseScientificSymbol(token.slice(0, equal));
    const isFile = dot > 0 && dot + 1 < token.length && !/[=()]/.test(token.slice(0, dot))
      && /^(?:cs|py|tex|md|json|xml|yaml|yml|pdf|png|jpg|txt|exe|dll)$/i.test(token.slice(dot + 1).replace(/[,;)\]"'.]+$/g, ""));
    if (!isPath && !isUrl && !isFile && !isAssignment) return start;
    while (end < text.length && !/\s/.test(text[end]) && !"`|>".includes(text[end])) end += 1;
    return end;
  }

  function looseMathAt(text, start) {
    if (start > 0 && (looseLetterOrDigit(text[start - 1]) || "_\\".includes(text[start - 1]))) return null;
    if (!looseLetterOrDigit(text[start]) && text[start] !== "(") return null;
    const limit = Math.min(text.length, start + 1024);
    let position = start, depth = 0, evidence = false, incomplete = false;
    const space = () => { while (position < limit && /[ \t]/.test(text[position])) position += 1; };
    const nested = read => {
      depth += 1;
      if (depth > 16) { incomplete = true; depth -= 1; return null; }
      const value = read(); depth -= 1; return value;
    };
    function script(subscript) {
      if (position >= limit) return null;
      const braced = text[position] === "{";
      if (braced) position += 1;
      const begin = position;
      if (!subscript && /[+−-]/.test(text[position] || "")) position += 1;
      if (!braced && !subscript) {
        if (looseAsciiDigit(text[position])) while (position < limit && looseAsciiDigit(text[position])) position += 1;
        else if (position < limit && looseLetter(text[position])) position += 1;
      } else while (position < limit && looseLetterOrDigit(text[position])) position += 1;
      if (position === begin || position - begin > 8) return null;
      const value = text.slice(begin, position).replace(/−/g, "-");
      if (["+", "-"].includes(value)) return null;
      if (braced) { if (position >= limit || text[position] !== "}") return null; position += 1; }
      return value;
    }
    function atom() {
      space();
      if (position >= limit || /[\r\n]/.test(text[position])) return null;
      const character = text[position];
      if (/[+−-]/.test(character)) { position += 1; const value = nested(atom); return value === null ? null : (character === "+" ? "+" : "-") + value; }
      if (character === "(") {
        position += 1;
        const value = nested(expression); space();
        if (value === null || position >= limit || text[position] !== ")") { incomplete = true; return null; }
        position += 1; return `(${value})`;
      }
      if (looseAsciiDigit(character)) {
        const begin = position++;
        while (position < limit && looseAsciiDigit(text[position])) position += 1;
        if (position + 1 < limit && /[.,]/.test(text[position]) && looseAsciiDigit(text[position + 1])) {
          position += 1; while (position < limit && looseAsciiDigit(text[position])) position += 1;
        }
        let value = text.slice(begin, position).replace(/,/g, "{,}");
        if (position < limit && /[eE]/.test(text[position])) {
          const exponentStart = position + 1;
          let exponentEnd = exponentStart;
          if (/[+-]/.test(text[exponentEnd] || "")) exponentEnd += 1;
          const digitsStart = exponentEnd;
          while (exponentEnd < limit && looseAsciiDigit(text[exponentEnd])) exponentEnd += 1;
          if (exponentEnd > digitsStart) { position = exponentEnd; evidence = true; value += `\\cdot 10^{${text.slice(exponentStart, exponentEnd)}}`; }
        }
        return value;
      }
      if (!looseLetter(character)) return null;
      const begin = position++;
      while (position < limit && looseLetter(text[position])) position += 1;
      const value = text.slice(begin, position);
      if (["ln", "log", "sin", "cos", "tan", "exp"].includes(value)) {
        evidence = true; const argument = nested(power);
        if (argument === null) { incomplete = true; return null; }
        return `\\${value} ${argument}`;
      }
      if (value.length === 2 && value[0] === "Δ") { evidence = true; return value; }
      if (value.length === 1 || value.length === 2 && value[0] === "d" || value === "mc") return value;
      position = begin; return null;
    }
    function power() {
      let value = atom();
      if (value === null) return null;
      let subscript = false, exponent = false;
      while (position < limit) {
        const marker = text[position];
        if (marker === "_" || marker === "^") {
          if (marker === "_" ? subscript : exponent) { incomplete = true; return null; }
          if (marker === "_") subscript = true; else exponent = true;
          position += 1;
          const content = script(marker === "_");
          if (content === null) { incomplete = true; return null; }
          if (marker === "^" || content.length === 1 || looseUppercase(content) || looseLabels.has(content)) evidence = true;
          value += `${marker}{${content}}`;
        } else if (looseSuperscripts[marker] !== undefined) {
          if (exponent) { incomplete = true; return null; }
          exponent = true;
          let content = "";
          while (position < limit && looseSuperscripts[text[position]] !== undefined) content += looseSuperscripts[text[position++]];
          if (!/[0-9]/.test(content)) { incomplete = true; return null; }
          evidence = true; value += `^{${content}}`;
        } else break;
      }
      return value;
    }
    function product() {
      const first = power(); if (first === null) return null;
      let value = first;
      while (position < limit) {
        const beforeSpace = position; space();
        if (position >= limit || text.startsWith("**", position)) break;
        const character = text[position];
        if ("*·×/".includes(character)) {
          position += 1;
          if (character === "·" || character === "×" || character === "/" && (first.startsWith("d") || text.startsWith("dt", position))) evidence = true;
          const right = power(); if (right === null) { incomplete = true; return null; }
          value += (character === "/" ? "/" : "\\cdot ") + right;
        } else if (beforeSpace === position && (looseLetter(character) || character === "(")
          && (looseDigit(text[beforeSpace - 1]) || text[beforeSpace - 1] === ")" || character === "(" && looseScientificSymbol(first))
          || position > beforeSpace && looseScientificSymbol(first) && scriptedSymbolAhead()) {
          const right = power(); if (right === null) break; value += " " + right;
        } else break;
      }
      return value;
    }
    function scriptedSymbolAhead() {
      if (position >= limit || !looseLetter(text[position])) return false;
      let marker = position + 1;
      if (["d", "Δ"].includes(text[position]) && marker < limit && looseLetter(text[marker])) marker += 1;
      return marker < limit && (["_", "^"].includes(text[marker]) || looseSuperscripts[text[marker]] !== undefined);
    }
    function sum() {
      let value = product(); if (value === null) return null;
      while (position < limit) {
        space(); if (position >= limit || !"+-−".includes(text[position])) break;
        const operation = text[position++] === "+" ? "+" : "-";
        const right = product(); if (right === null) { incomplete = true; return null; }
        value += operation + right;
      }
      return value;
    }
    function expression() {
      let value = sum(); if (value === null) return null;
      const relations = { "=": "=", "~": "\\sim", "≈": "\\approx", "≠": "\\ne", "≤": "\\le", "≥": "\\ge", "∝": "\\propto" };
      while (position < limit) {
        space(); const relation = position < limit && relations[text[position]];
        if (!relation) break;
        position += 1; const right = sum();
        if (right === null) { incomplete = true; return null; }
        value += ` ${relation} ${right}`;
      }
      return value;
    }
    const latex = expression();
    if (!evidence) return null;
    if (latex === null || incomplete || position >= limit && limit < text.length && !/[\r\n]/.test(text[limit])) {
      let blockedUntil = start;
      while (blockedUntil < text.length && !/[\r\n]/.test(text[blockedUntil])) blockedUntil += 1;
      return { blockedUntil };
    }
    let end = position;
    while (end > start && /[ \t]/.test(text[end - 1])) end -= 1;
    if (end <= start || end < text.length && (looseLetterOrDigit(text[end]) || "_^\\".includes(text[end])
      || text[end] === "." && end + 1 < text.length && looseLetter(text[end + 1]))) return null;
    return {end,latex};
  }

  function protectMarkdownSegments(text) {
    const source = String(text || "");
    const segments = [];
    let protectedText = "";
    let index = 0;
    let legacyBlockedUntil = 0;

    const appendProtected = (value, kind, renderLatex, codeDelimiterLength = 1) => {
      const token = `\uE000MISSUM_MATH_${segments.length.toString(36).toUpperCase()}\uE001`;
      segments.push({ token, source: value, kind, renderLatex, codeDelimiterLength });
      protectedText += token;
    };

    while (index < source.length) {
      if (source[index] === "`") {
        let length = 1;
        while (source[index + length] === "`") length += 1;
        let close = source.indexOf("`".repeat(length), index + length);
        while (close >= 0 && (source[close - 1] === "`" || source[close + length] === "`"))
          close = source.indexOf("`".repeat(length), close + length);
        if (close >= index + length) {
          appendProtected(source.slice(index, close + length), "code", undefined, length);
          index = close + length;
          continue;
        }
        appendProtected(source.slice(index), "literal");
        break;
      }

      const literalEnd = looseLiteralEnd(source, index);
      if (literalEnd > index) { protectedText += source.slice(index, literalEnd); index = literalEnd; continue; }

      let open = "";
      let close = "";
      let sameLine = false;
      if (source.startsWith("\\[", index)) {
        open = "\\[";
        close = "\\]";
      } else if (source.startsWith("\\(", index)) {
        open = "\\(";
        close = "\\)";
        sameLine = true;
      } else if (source.startsWith("$$", index) && !isEscapedDelimiter(source, index)) {
        open = "$$";
        close = "$$";
      } else if (source[index] === "$" && source[index + 1] !== "$" && !isEscapedDelimiter(source, index)) {
        open = "$";
        close = "$";
        sameLine = true;
      }

      if (open) {
        const closeIndex = closingDelimiterIndex(source, index + open.length, close, sameLine);
        if (closeIndex >= 0) {
          const end = closeIndex + close.length;
          const candidate = source.slice(index, end);
          if (open !== "$" || isInlineDollarMath(candidate)) {
            appendProtected(candidate, "math");
            index = end;
            continue;
          }
        } else {
          const lineEnd = source.indexOf("\n", index);
          const end = lineEnd < 0 ? source.length : lineEnd;
          appendProtected(source.slice(index, end), "literal");
          index = end;
          continue;
        }
      }

      if (!open && index >= legacyBlockedUntil) {
        const legacy = looseMathAt(source, index);
        if (legacy?.end > index) {
          appendProtected(source.slice(index, legacy.end), "math", legacy.latex);
          index = legacy.end; continue;
        }
        if (legacy?.blockedUntil) legacyBlockedUntil = legacy.blockedUntil;
      }

      protectedText += source[index];
      index += 1;
    }
    return { text: protectedText, segments };
  }

  function restoreSegments(text, segments) {
    let restored = String(text || "");
    for (const segment of segments || []) restored = restored.split(segment.token).join(segment.source);
    return restored;
  }

  function safeExternalUrl(value) {
    if (typeof value !== "string" || value.length > 2048) return null;
    try {
      const parsed = new URL(value);
      return parsed.protocol === "http:" || parsed.protocol === "https:" ? parsed.href : null;
    } catch {
      return null;
    }
  }

  function appendExternalLink(parent, url, label = url) {
    const safeUrl = safeExternalUrl(url);
    if (!safeUrl) {
      parent.append(document.createTextNode(label));
      return;
    }
    const link = document.createElement("a");
    link.href = safeUrl;
    link.textContent = label;
    link.rel = "noopener noreferrer";
    link.addEventListener("click", event => {
      event.preventDefault();
      globalThis.missumBridge?.post("external.open", { url: safeUrl });
    });
    parent.append(link);
  }

  function appendExternalImage(parent, url, label) {
    const safeUrl = safeExternalUrl(url);
    if (!safeUrl || !safeUrl.startsWith("https://")) {
      parent.append(document.createTextNode(label));
      return;
    }
    const image = document.createElement("img");
    image.src = safeUrl;
    image.alt = label;
    image.loading = "lazy";
    image.decoding = "async";
    image.referrerPolicy = "no-referrer";
    image.className = "message-external-image";
    image.addEventListener("error", () => image.replaceWith(document.createTextNode(`${label} (Bild nicht verfügbar)`)));
    parent.append(image);
  }

  function appendInline(parent, text) {
    const protectedSource = protectMarkdownSegments(String(text || ""));
    const segmentByToken = new Map(protectedSource.segments.map(segment => [segment.token, segment]));
    const pattern = /(\uE000MISSUM_MATH_[0-9A-Z]+\uE001)|\*\*(.+?)\*\*|\*([^*\n]{1,400})\*|<(sub|sup)>([^<>\r\n]+)<\/\4>|!\[([^\]\r\n]{1,500})\]\((https?:\/\/[^\s<>)]+)\)|\[([^\]\r\n]{1,500})\]\((https?:\/\/[^\s<>)]+)\)|(https?:\/\/[^\s<]+)/gi;
    let cursor = 0;
    let match = pattern.exec(protectedSource.text);
    while (match) {
      if (match.index > cursor) parent.append(document.createTextNode(protectedSource.text.slice(cursor, match.index)));
      if (match[1]) {
        const segment = segmentByToken.get(match[1]);
        if (!segment || segment.kind === "literal") {
          parent.append(document.createTextNode(segment?.source || match[1]));
        } else if (segment.kind === "code") {
          const code = document.createElement("code");
          code.textContent = segment.source.slice(segment.codeDelimiterLength, -segment.codeDelimiterLength);
          parent.append(code);
        } else {
          parent.append(createSelectableMathNode(segment.source, segment.renderLatex));
        }
      } else if (match[2] !== undefined) {
        const strong = document.createElement("strong");
        appendInline(strong, restoreSegments(match[2], protectedSource.segments));
        parent.append(strong);
      } else if (match[3] !== undefined) {
        const emphasis = document.createElement("em");
        appendInline(emphasis, restoreSegments(match[3], protectedSource.segments));
        parent.append(emphasis);
      } else if (match[4]) {
        const semantic = document.createElement(match[4].toLowerCase());
        semantic.textContent = restoreSegments(match[5], protectedSource.segments);
        parent.append(semantic);
      } else if (match[6] && match[7]) {
        appendExternalImage(parent, match[7], restoreSegments(match[6], protectedSource.segments));
      } else if (match[8] && match[9]) {
        appendExternalLink(parent, match[9], restoreSegments(match[8], protectedSource.segments));
      } else if (match[10]) {
        appendExternalLink(parent, match[10]);
      }
      cursor = match.index + match[0].length;
      match = pattern.exec(protectedSource.text);
    }
    if (cursor < protectedSource.text.length) {
      parent.append(document.createTextNode(protectedSource.text.slice(cursor)));
    }
  }

  function collectDisplayMathBlock(lines, startIndex) {
    const first = String(lines[startIndex] || "").trim();
    const open = first.startsWith("\\[") ? "\\[" : (first.startsWith("$$") ? "$$" : "");
    if (!open) return null;
    const close = open === "\\[" ? "\\]" : "$$";
    const parts = [];
    let index = startIndex;
    while (index < lines.length) {
      const line = String(lines[index] || "");
      const searchStart = index === startIndex ? line.indexOf(open) + open.length : 0;
      const closeIndex = closingDelimiterIndex(line, searchStart, close, false);
      parts.push(line);
      if (closeIndex >= 0) {
        if (line.slice(closeIndex + close.length).trim()) return null;
        return { math: parts.join("\n"), nextIndex: index + 1 };
      }
      index += 1;
    }
    return null;
  }

  function isTableSeparator(line) {
    const cells = splitTableRow(line);
    return cells.length > 0 && cells.every(cell => /^:?-{3,}:?$/.test(cell));
  }

  function splitTableRow(line) {
    const protectedSource = protectMarkdownSegments(String(line || ""));
    const trimmed = protectedSource.text.trim().replace(/^\||\|$/g, "");
    if (!trimmed.includes("|")) return [];
    return trimmed.split("|").map(cell => restoreSegments(cell.trim(), protectedSource.segments));
  }

  function escapeMarkdownTableCell(value) {
    return String(value ?? "").replace(/\|/g, "\\|").replace(/\n+/g, " ").trim();
  }

  function canonicalMarkdownTableHeader(value) {
    return String(value || "")
      .toLocaleLowerCase("de-DE")
      .replace(/ä/g, "ae")
      .replace(/ö/g, "oe")
      .replace(/ü/g, "ue")
      .normalize("NFD")
      .replace(/[\u0300-\u036f]/g, "")
      .replace(/\s+/g, " ")
      .trim();
  }

  function markdownTableHeaderMatches(actual, expected) {
    const normalizedActual = canonicalMarkdownTableHeader(actual);
    const normalizedExpected = canonicalMarkdownTableHeader(expected);
    return normalizedActual === normalizedExpected || normalizedActual.startsWith(`${normalizedExpected} (`);
  }

  function splitPossibleCadHandleSuffix(value) {
    const match = String(value || "").trim().match(/^(.+\S)\s+([0-9a-f]*[a-f][0-9a-f]*|[0-9a-f]{3,})$/i);
    return match ? [match[1].trim(), match[2].trim()] : null;
  }

  function splitGluedCadObjectRows(cells, columnCount) {
    const output = [];
    for (const cell of cells) {
      if (columnCount > 0 && (output.length + 1) % columnCount === 0) {
        const split = splitPossibleCadHandleSuffix(cell);
        if (split) {
          output.push(split[0], split[1]);
          continue;
        }
      }
      output.push(cell);
    }
    return output;
  }

  function normalizeFlatCadObjectTableLine(line) {
    const source = String(line || "").trim();
    if (!/^Handle\s*\|\s*Typ\s*\|\s*Layer\s*\|/i.test(source)) return "";

    const expectedHeaders = ["Handle", "Typ", "Layer", "Breite X", "Tiefe Y", "Höhe Z", "Länge", "Fläche", "Volumen"];
    const cells = source.split("|").map(cell => cell.trim());
    if (cells[0] === "") cells.shift();
    if (cells[cells.length - 1] === "") cells.pop();
    if (cells.length <= expectedHeaders.length) return "";

    const headers = [];
    for (let index = 0; index < expectedHeaders.length - 1; index += 1) {
      if (!markdownTableHeaderMatches(cells[index], expectedHeaders[index])) return "";
      headers.push(cells[index]);
    }
    const lastHeader = String(cells[expectedHeaders.length - 1] || "").match(/^(Volumen(?:\s*\([^)]*\))?)(?:\s+(.+))?$/i);
    if (!lastHeader) return "";
    headers.push(lastHeader[1].trim());

    let data = cells.slice(expectedHeaders.length);
    if (lastHeader[2]) data.unshift(lastHeader[2].trim());
    data = splitGluedCadObjectRows(data, headers.length);
    if (data.length < headers.length || data.length % headers.length !== 0) return "";

    const rows = [];
    for (let index = 0; index < data.length; index += headers.length) rows.push(data.slice(index, index + headers.length));
    return [
      `| ${headers.map(escapeMarkdownTableCell).join(" | ")} |`,
      `| ${headers.map(() => "---").join(" | ")} |`,
      ...rows.map(row => `| ${row.map(escapeMarkdownTableCell).join(" | ")} |`)
    ].join("\n");
  }

  function splitLooseTableRow(line) {
    const value = String(line || "").trim();
    if (!value
      || value.startsWith("|")
      || /^#{1,6}\s+/.test(value)
      || /^>\s?/.test(value)
      || /^\s*[-*\u2022]\s+/.test(value)
      || /^\s*\d+[.)]\s+/.test(value)
      || isTableSeparator(value)) return null;
    const cells = value.includes("\t") ? value.split(/\t+/) : value.split(/\s{2,}/);
    const normalized = cells.map(cell => cell.trim()).filter(Boolean);
    return normalized.length >= 2 ? normalized : null;
  }

  function normalizePipeTablesWithoutSeparator(lines) {
    const output = [];
    for (let index = 0; index < lines.length;) {
      const first = splitTableRow(lines[index]);
      if (!first.length || isTableSeparator(lines[index])) {
        output.push(lines[index]);
        index += 1;
        continue;
      }

      const block = [lines[index]];
      let next = index + 1;
      while (next < lines.length) {
        const cells = splitTableRow(lines[next]);
        if (!cells.length || cells.length !== first.length) break;
        block.push(lines[next]);
        next += 1;
      }

      const contentRows = block.filter(line => !isTableSeparator(line));
      if (contentRows.length < 2) {
        output.push(lines[index]);
        index += 1;
        continue;
      }

      const separator = block.find(line => isTableSeparator(line))
        || `| ${first.map(() => "---").join(" | ")} |`;
      output.push(contentRows[0], separator, ...contentRows.slice(1));
      index = next;
    }
    return output;
  }

  function normalizeLooseMarkdownTables(text) {
    const pipeNormalized = normalizePipeTablesWithoutSeparator(String(text || "").replace(/\r\n/g, "\n").split("\n"));
    const output = [];
    for (let index = 0; index < pipeNormalized.length;) {
      const flatCadTable = normalizeFlatCadObjectTableLine(pipeNormalized[index]);
      if (flatCadTable) {
        output.push(...flatCadTable.split("\n"));
        index += 1;
        continue;
      }

      const first = splitLooseTableRow(pipeNormalized[index]);
      if (!first) {
        output.push(pipeNormalized[index]);
        index += 1;
        continue;
      }

      const rows = [first];
      let next = index + 1;
      while (next < pipeNormalized.length) {
        const cells = splitLooseTableRow(pipeNormalized[next]);
        if (!cells || cells.length !== first.length) break;
        rows.push(cells);
        next += 1;
      }
      if (rows.length < 2) {
        output.push(pipeNormalized[index]);
        index += 1;
        continue;
      }
      output.push(`| ${rows[0].map(escapeMarkdownTableCell).join(" | ")} |`);
      output.push(`| ${rows[0].map(() => "---").join(" | ")} |`);
      for (const row of rows.slice(1)) output.push(`| ${row.map(escapeMarkdownTableCell).join(" | ")} |`);
      index = next;
    }
    return output.join("\n");
  }

  function normalizeMarkdownStructure(text) {
    const protectedSource = protectMarkdownSegments(String(text || ""));
    const prose = protectedSource.text.replace(/<\s*br\s*\/?\s*>/gi, "\n");
    return restoreSegments(normalizeLooseMarkdownTables(prose), protectedSource.segments);
  }

  function openingCodeFence(line) {
    const match = /^( {0,3})(`{3,}|~{3,})(.*)$/.exec(line);
    if (!match || (match[2][0] === "`" && match[3].includes("`"))) return null;
    return { marker: match[2][0], length: match[2].length, indent: match[1].length, language: match[3].trim() };
  }

  function closesCodeFence(line, fence) {
    const match = /^ {0,3}(`{3,}|~{3,})[ \t]*$/.exec(line);
    return Boolean(match && match[1][0] === fence.marker && match[1].length >= fence.length);
  }

  function normalizeMarkdownOutsideCodeFences(text) {
    const lines = String(text || "").split("\n");
    const output = [];
    let markdown = [];
    let fence = null;
    const flushMarkdown = () => {
      if (!markdown.length) return;
      output.push(...normalizeMarkdownStructure(markdown.join("\n")).split("\n"));
      markdown = [];
    };
    for (const line of lines) {
      if (fence) {
        output.push(line);
        if (closesCodeFence(line, fence)) fence = null;
      } else {
        const opening = openingCodeFence(line);
        if (opening) {
          flushMarkdown();
          output.push(line);
          fence = opening;
        } else markdown.push(line);
      }
    }
    flushMarkdown();
    return output.join("\n");
  }

  function createCodeBlock(language, content) {
    const block = document.createElement("div");
    block.className = "code-block";
    const header = document.createElement("div");
    header.className = "code-header";
    header.append(document.createTextNode(language || "Code"));
    const copy = document.createElement("button");
    copy.type = "button";
    copy.textContent = "Kopieren";
    copy.addEventListener("click", () => copyText(content));
    header.append(copy);
    const pre = document.createElement("pre");
    const code = document.createElement("code");
    code.textContent = content;
    globalThis.missumCodeHighlight?.appendTo(code, content, language);
    pre.append(code);
    block.append(header, pre);
    return block;
  }

  function appendParagraph(root, lines) {
    if (!lines.length) return;
    const paragraph = document.createElement("p");
    appendInline(paragraph, lines.join("\n").trim());
    root.append(paragraph);
    lines.length = 0;
  }

  function render(markdown) {
    const root = document.createDocumentFragment();
    const normalized = normalizeMarkdownOutsideCodeFences(
      String(markdown || "").replace(/\r\n?/g, "\n")
    );
    const lines = normalized.split("\n");
    const paragraph = [];
    let index = 0;

    while (index < lines.length) {
      const line = lines[index];
      const displayMath = collectDisplayMathBlock(lines, index);
      if (displayMath) {
        appendParagraph(root, paragraph);
        root.append(createSelectableMathNode(displayMath.math));
        index = displayMath.nextIndex;
        continue;
      }

      const openingFence = openingCodeFence(line);
      if (openingFence) {
        appendParagraph(root, paragraph);
        const language = openingFence.language;
        const content = [];
        index += 1;
        while (index < lines.length && !closesCodeFence(lines[index], openingFence)) {
          const codeLine = lines[index++];
          // Up to three leading spaces belong to the Markdown fence. Remove
          // only that prefix; relative program indentation remains unchanged.
          const indent = Math.min(openingFence.indent, /^ */.exec(codeLine)[0].length);
          content.push(codeLine.slice(indent));
        }
        if (index < lines.length) index += 1;
        root.append(createCodeBlock(language, content.join("\n")));
        continue;
      }

      const headerCells = splitTableRow(line);
      if (headerCells.length && index + 1 < lines.length && isTableSeparator(lines[index + 1])) {
        appendParagraph(root, paragraph);
        const table = document.createElement("table");
        const head = document.createElement("thead");
        const headRow = document.createElement("tr");
        for (const cellText of headerCells) {
          const cell = document.createElement("th");
          appendInline(cell, cellText);
          headRow.append(cell);
        }
        head.append(headRow);
        table.append(head);
        const body = document.createElement("tbody");
        index += 2;
        while (index < lines.length) {
          const cells = splitTableRow(lines[index]);
          if (!cells.length) break;
          const row = document.createElement("tr");
          for (let cellIndex = 0; cellIndex < headerCells.length; cellIndex += 1) {
            const cell = document.createElement("td");
            appendInline(cell, cells[cellIndex] || "");
            row.append(cell);
          }
          body.append(row);
          index += 1;
        }
        table.append(body);
        const wrap = document.createElement("div");
        wrap.className = "table-wrap";
        wrap.append(table);
        root.append(wrap);
        continue;
      }

      const headingMatch = line.match(/^(#{1,4})\s+(.+)$/);
      if (headingMatch) {
        appendParagraph(root, paragraph);
        const heading = document.createElement(`h${headingMatch[1].length}`);
        appendInline(heading, headingMatch[2]);
        root.append(heading);
        index += 1;
        continue;
      }

      const unorderedMatch = line.match(/^\s*[-*\u2022]\s+(.+)$/);
      if (unorderedMatch) {
        appendParagraph(root, paragraph);
        const list = document.createElement("ul");
        while (index < lines.length) {
          const itemMatch = lines[index].match(/^\s*[-*\u2022]\s+(.+)$/);
          if (!itemMatch) break;
          const item = document.createElement("li");
          appendInline(item, itemMatch[1]);
          list.append(item);
          index += 1;
        }
        root.append(list);
        continue;
      }

      const orderedMatch = line.match(/^\s*(\d+)[.)]\s+(.+)$/);
      if (orderedMatch) {
        appendParagraph(root, paragraph);
        const list = document.createElement("ol");
        list.start = Number(orderedMatch[1]);
        while (index < lines.length) {
          const itemMatch = lines[index].match(/^\s*\d+[.)]\s+(.+)$/);
          if (!itemMatch) break;
          const item = document.createElement("li");
          appendInline(item, itemMatch[1]);
          list.append(item);
          index += 1;
        }
        root.append(list);
        continue;
      }

      if (/^>\s?/.test(line)) {
        appendParagraph(root, paragraph);
        const quoteLines = [];
        while (index < lines.length && /^>\s?/.test(lines[index])) quoteLines.push(lines[index++].replace(/^>\s?/, ""));
        const quote = document.createElement("blockquote");
        appendInline(quote, quoteLines.join("\n"));
        root.append(quote);
        continue;
      }

      if (!line.trim()) {
        appendParagraph(root, paragraph);
        index += 1;
        continue;
      }
      paragraph.push(line);
      index += 1;
    }

    appendParagraph(root, paragraph);
    return root;
  }

  globalThis.missumMarkdown = Object.freeze({ render });
})();
