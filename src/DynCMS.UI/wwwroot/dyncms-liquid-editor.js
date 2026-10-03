// DynCMS template editor.
//
// A textarea stays the thing you type into (so selection, undo, spellcheck and mobile keyboards keep
// working); a <pre> behind it shows the same text highlighted, and a gutter to the left numbers the lines.
// On top of that: auto-indent, bracket and tag completion, comment toggling, Ctrl+S and a completion
// popup fed with the variables, filters, tags and partials the back office knows about.

const INDENT = "  ";

const editors = new WeakMap();

const KEYWORDS = new Set([
    "if", "elsif", "else", "endif", "unless", "endunless", "case", "when", "endcase",
    "for", "endfor", "break", "continue", "cycle", "tablerow", "endtablerow",
    "assign", "capture", "endcapture", "increment", "decrement",
    "include", "render", "comment", "endcomment", "raw", "endraw", "liquid", "echo",
    "in", "and", "or", "not", "contains", "with", "as", "empty", "blank", "nil", "null", "true", "false", "limit", "offset", "reversed"
]);

// ---- escaping ---------------------------------------------------------------------------------

const escapeHtml = (s) => s.replace(/[&<>]/g, (c) => (c === "&" ? "&amp;" : c === "<" ? "&lt;" : "&gt;"));
const span = (cls, text) => `<span class="dc-t-${cls}">${escapeHtml(text)}</span>`;

// ---- highlighting -----------------------------------------------------------------------------

// Liquid comments, output, tags, then HTML comments, doctypes and elements. Quoted attribute values are
// consumed as a unit so a ">" inside them does not end the element early.
const ROOT = /(\{%-?\s*comment[\s\S]*?endcomment\s*-?%\})|(\{\{[\s\S]*?\}\})|(\{%[\s\S]*?%\})|(<!--[\s\S]*?-->)|(<![^>]*>)|(<\/?[A-Za-z][-\w:]*(?:"[^"]*"|'[^']*'|\{\{[\s\S]*?\}\}|\{%[\s\S]*?%\}|[^>'"])*\/?>)/g;

function highlight(source) {
    let out = "";
    let last = 0;
    ROOT.lastIndex = 0;
    let m;
    while ((m = ROOT.exec(source)) !== null) {
        if (m.index > last) out += escapeHtml(source.slice(last, m.index));
        if (m[1]) out += span("comment", m[1]);
        else if (m[2]) out += liquid(m[2], "output");
        else if (m[3]) out += liquid(m[3], "tag");
        else if (m[4] || m[5]) out += span("comment", m[4] || m[5]);
        else if (m[6]) out += element(m[6]);
        last = m.index + m[0].length;
    }
    out += escapeHtml(source.slice(last));
    return out;
}

// {{ ... }} and {% ... %}: delimiters, keywords, strings, numbers and filters.
function liquid(text, kind) {
    const open = text.startsWith("{{") ? (text.startsWith("{{-") ? 3 : 2) : (text.startsWith("{%-") ? 3 : 2);
    const closeLen = text.endsWith("-}}") || text.endsWith("-%}") ? 3 : 2;
    const head = text.slice(0, open);
    const tail = text.slice(text.length - closeLen);
    const body = text.slice(open, text.length - closeLen);

    let out = span("delim", head);
    const parts = /('[^']*'|"[^"]*"|\|\s*[a-zA-Z_][\w]*|[a-zA-Z_][\w.]*|\d+(?:\.\d+)?|[^\s'"|\w]+|\s+)/g;
    let m;
    while ((m = parts.exec(body)) !== null) {
        const token = m[0];
        if (/^\s+$/.test(token)) out += escapeHtml(token);
        else if (token[0] === "'" || token[0] === '"') out += span("string", token);
        else if (token[0] === "|") out += span("delim", "|") + span("filter", token.slice(1));
        else if (/^\d/.test(token)) out += span("number", token);
        else if (KEYWORDS.has(token)) out += span("keyword", token);
        else if (token.includes(".")) {
            const dot = token.indexOf(".");
            out += span("variable", token.slice(0, dot)) + span("delim", ".") + span("member", token.slice(dot + 1));
        }
        else if (/^[a-zA-Z_]/.test(token)) out += span(kind === "tag" ? "keyword" : "variable", token);
        else out += span("delim", token);
    }
    return `<span class="dc-t-${kind}">${out}${span("delim", tail)}</span>`;
}

// <div class="x" data-y="{{ z }}">
function element(text) {
    const name = /^<\/?\s*([-\w:]+)/.exec(text);
    if (!name) return escapeHtml(text);

    let out = span("delim", text.slice(0, name[0].length - name[1].length)) + span("element", name[1]);
    const rest = text.slice(name[0].length);
    const parts = /([-\w:@.]+)(\s*=\s*)("[^"]*"|'[^']*'|[^\s>]+)?|(\{\{[\s\S]*?\}\}|\{%[\s\S]*?%\})|(\s+)|([\s\S])/g;
    let m;
    while ((m = parts.exec(rest)) !== null) {
        if (m[1]) {
            out += span("attr", m[1]) + span("delim", m[2]);
            if (m[3]) out += inlineValue(m[3]);
        }
        else if (m[4]) out += liquid(m[4], m[4][1] === "{" ? "output" : "tag");
        else if (m[5]) out += escapeHtml(m[5]);
        else out += span("delim", m[6]);
    }
    return out;
}

// An attribute value, which often contains Liquid: class="card {{ content.alias }}".
function inlineValue(value) {
    const inner = /(\{\{[\s\S]*?\}\}|\{%[\s\S]*?%\})/g;
    let out = "";
    let last = 0;
    let m;
    while ((m = inner.exec(value)) !== null) {
        if (m.index > last) out += span("string", value.slice(last, m.index));
        out += liquid(m[0], m[0][1] === "{" ? "output" : "tag");
        last = m.index + m[0].length;
    }
    out += span("string", value.slice(last));
    return out;
}

// ---- completion context -----------------------------------------------------------------------

// What the caret is in the middle of, so only sensible completions are offered.
function contextAt(value, caret) {
    const before = value.slice(0, caret);
    const openOutput = before.lastIndexOf("{{");
    const openTag = before.lastIndexOf("{%");
    const closeOutput = before.lastIndexOf("}}");
    const closeTag = before.lastIndexOf("%}");

    const inOutput = openOutput > closeOutput && openOutput > openTag;
    const inTag = openTag > closeTag && openTag > openOutput;

    if (!inOutput && !inTag) {
        // Outside a tag, only an opening brace asks for help — otherwise the popup would fight with prose.
        const brace = /\{[%{]?[\w]*$/.exec(before);
        return { kind: "markup", prefix: brace ? brace[0] : "" };
    }

    const inner = before.slice((inOutput ? openOutput : openTag) + 2);

    if (inTag) {
        const partial = /^\s*(?:render|include)\s+(['"])([^'"]*)$/.exec(inner);
        if (partial) return { kind: "partial", prefix: partial[2] };
        const first = /^-?\s*([a-zA-Z_][\w]*)?$/.exec(inner);
        if (first) return { kind: "tag", prefix: first[1] || "" };
    }

    const filter = /\|\s*([a-zA-Z_][\w]*)?$/.exec(inner);
    if (filter) return { kind: "filter", prefix: filter[1] || "" };

    // Dotted paths complete as a whole ("content.bo" -> "content.bodyText").
    const word = /([a-zA-Z_][\w.]*)?$/.exec(inner);
    return { kind: inTag ? "expression" : "output", prefix: (word && word[1]) || "" };
}

function matches(items, context) {
    const prefix = (context.prefix || "").toLowerCase();
    const wanted = {
        markup: ["snippet"],
        tag: ["tag"],
        partial: ["partial"],
        filter: ["filter"],
        output: ["variable", "property"],
        expression: ["variable", "property", "keyword"]
    }[context.kind] || ["variable"];

    return items
        .filter((i) => wanted.includes(i.kind))
        .filter((i) => !prefix || i.label.toLowerCase().includes(prefix))
        .sort((a, b) => {
            const as = a.label.toLowerCase().startsWith(prefix) ? 0 : 1;
            const bs = b.label.toLowerCase().startsWith(prefix) ? 0 : 1;
            return as - bs || a.label.localeCompare(b.label);
        })
        .slice(0, 60);
}

// ---- editor -----------------------------------------------------------------------------------

class LiquidEditor {
    constructor(root, dotnet, options) {
        this.root = root;
        this.dotnet = dotnet;
        this.options = options || {};
        this.items = this.options.completions || [];
        this.textarea = root.querySelector("textarea");
        this.pre = root.querySelector(".dc-ed-highlight");
        this.gutter = root.querySelector(".dc-ed-gutter");
        this.popup = root.querySelector(".dc-ed-popup");
        this.selected = 0;
        this.visible = [];
        this.charWidth = 8;
        this.lineHeight = 21;

        if (typeof this.options.value === "string") this.textarea.value = this.options.value;

        this.onInput = () => { this.render(); this.notify(); this.maybeComplete(); };
        this.onScroll = () => this.syncScroll();
        this.onKeyDown = (e) => this.keydown(e);
        this.onBlur = () => setTimeout(() => this.closePopup(), 150);
        this.onClick = () => this.closePopup();

        this.textarea.addEventListener("input", this.onInput);
        this.textarea.addEventListener("scroll", this.onScroll);
        this.textarea.addEventListener("keydown", this.onKeyDown);
        this.textarea.addEventListener("blur", this.onBlur);
        this.textarea.addEventListener("click", this.onClick);

        this.measure();
        this.render();
    }

    measure() {
        const probe = document.createElement("span");
        probe.textContent = "0".repeat(20);
        probe.style.cssText = "position:absolute;visibility:hidden;white-space:pre";
        const style = getComputedStyle(this.textarea);
        probe.style.font = style.font;
        probe.style.letterSpacing = style.letterSpacing;
        this.root.appendChild(probe);
        this.charWidth = probe.getBoundingClientRect().width / 20 || 8;
        this.root.removeChild(probe);
        this.lineHeight = parseFloat(style.lineHeight) || 21;
        this.padLeft = parseFloat(style.paddingLeft) || 0;
        this.padTop = parseFloat(style.paddingTop) || 0;
    }

    // ---- painting ----

    render() {
        const value = this.textarea.value;
        this.pre.innerHTML = highlight(value) + "\n";
        const lines = value.split("\n").length;
        if (this.gutter.childElementCount !== lines) {
            let html = "";
            for (let i = 1; i <= lines; i++) html += `<span>${i}</span>`;
            this.gutter.innerHTML = html;
        }
        this.markActiveLine();
        this.syncScroll();
    }

    markActiveLine() {
        const line = this.textarea.value.slice(0, this.textarea.selectionStart).split("\n").length - 1;
        if (this.activeLine === line) return;
        const previous = this.gutter.children[this.activeLine];
        if (previous) previous.classList.remove("dc-active");
        const current = this.gutter.children[line];
        if (current) current.classList.add("dc-active");
        this.activeLine = line;
    }

    syncScroll() {
        this.pre.scrollTop = this.textarea.scrollTop;
        this.pre.scrollLeft = this.textarea.scrollLeft;
        this.gutter.scrollTop = this.textarea.scrollTop;
    }

    notify() {
        clearTimeout(this.timer);
        this.timer = setTimeout(() => {
            if (this.dotnet) this.dotnet.invokeMethodAsync("OnSourceChanged", this.textarea.value).catch(() => { });
        }, 250);
    }

    flush() {
        clearTimeout(this.timer);
        if (this.dotnet) return this.dotnet.invokeMethodAsync("OnSourceChanged", this.textarea.value).catch(() => { });
        return Promise.resolve();
    }

    // ---- editing ----

    replace(start, end, text, caret) {
        this.textarea.setRangeText(text, start, end, "end");
        if (typeof caret === "number") this.textarea.selectionStart = this.textarea.selectionEnd = caret;
        this.textarea.dispatchEvent(new Event("input", { bubbles: true }));
    }

    lineAt(position) {
        const value = this.textarea.value;
        const start = value.lastIndexOf("\n", position - 1) + 1;
        const end = value.indexOf("\n", position);
        return { start, end: end === -1 ? value.length : end, text: value.slice(start, end === -1 ? value.length : end) };
    }

    keydown(e) {
        if (this.popupOpen()) {
            if (e.key === "ArrowDown") { e.preventDefault(); this.move(1); return; }
            if (e.key === "ArrowUp") { e.preventDefault(); this.move(-1); return; }
            if (e.key === "Enter" || e.key === "Tab") { e.preventDefault(); this.accept(); return; }
            if (e.key === "Escape") { e.preventDefault(); this.closePopup(); return; }
        }

        const ctrl = e.ctrlKey || e.metaKey;

        if (ctrl && (e.key === "s" || e.key === "S")) {
            e.preventDefault();
            this.flush().then(() => this.dotnet && this.dotnet.invokeMethodAsync("OnSaveShortcut").catch(() => { }));
            return;
        }
        if (ctrl && e.key === " ") { e.preventDefault(); this.maybeComplete(true); return; }
        if (ctrl && (e.key === "/" || e.key === "7")) { e.preventDefault(); this.toggleComment(); return; }
        if (e.key === "Tab") { e.preventDefault(); this.indent(e.shiftKey); return; }
        if (e.key === "Enter") { this.newline(e); return; }
        if (e.key === "{" || e.key === "%" || e.key === "'" || e.key === '"') { this.pair(e); return; }
        if (e.key === "Escape") this.closePopup();

        // Caret moves without editing still update the active line.
        setTimeout(() => this.markActiveLine(), 0);
    }

    indent(outdent) {
        const { selectionStart: start, selectionEnd: end, value } = this.textarea;
        const first = value.lastIndexOf("\n", start - 1) + 1;

        if (start !== end || outdent) {
            const last = value.indexOf("\n", end);
            const stop = last === -1 ? value.length : last;
            const block = value.slice(first, stop);
            const changed = outdent
                ? block.replace(/^[ \t]{1,2}/gm, (m) => (m === "\t" ? "" : m.slice(0, m.length - Math.min(2, m.length))))
                : block.replace(/^/gm, INDENT);
            this.replace(first, stop, changed);
            this.textarea.selectionStart = first;
            this.textarea.selectionEnd = first + changed.length;
            return;
        }

        this.replace(start, end, INDENT);
    }

    // Enter keeps the current indentation, and goes one level deeper after an opening tag or element.
    newline(e) {
        const { selectionStart: start, selectionEnd: end, value } = this.textarea;
        if (start !== end) return;

        const line = this.lineAt(start);
        const before = line.text.slice(0, start - line.start);
        const indent = (/^[ \t]*/.exec(before) || [""])[0];

        const opens = /\{%-?\s*(if|unless|for|case|capture|tablerow|comment)\b/.test(before) || /<([a-zA-Z][-\w:]*)(?:\s[^>]*)?>\s*$/.test(before);
        const closesNext = /^\s*(\{%-?\s*(end\w+|else|elsif|when)\b|<\/)/.test(value.slice(start));

        e.preventDefault();
        if (opens && closesNext) {
            this.replace(start, end, `\n${indent}${INDENT}\n${indent}`, start + 1 + indent.length + INDENT.length);
        } else {
            this.replace(start, end, `\n${indent}${opens ? INDENT : ""}`);
        }
    }

    // {{ → {{  }}, {% → {%  %}, quotes wrap the selection or close themselves.
    pair(e) {
        const { selectionStart: start, selectionEnd: end, value } = this.textarea;

        if (e.key === "{" && value.slice(start - 1, start) === "{" && !value.startsWith("{", start)) {
            e.preventDefault();
            this.replace(start, end, "{  }}", start + 2);
            return;
        }
        if (e.key === "%" && value.slice(start - 1, start) === "{") {
            e.preventDefault();
            this.replace(start, end, "%  %}", start + 2);
            return;
        }
        if ((e.key === "'" || e.key === '"')) {
            if (start !== end) {
                e.preventDefault();
                const selected = value.slice(start, end);
                this.replace(start, end, e.key + selected + e.key);
                this.textarea.selectionStart = start + 1;
                this.textarea.selectionEnd = end + 1;
                return;
            }
            if (value[start] === e.key) { e.preventDefault(); this.textarea.selectionStart = this.textarea.selectionEnd = start + 1; return; }
            const prev = value[start - 1] || "";
            if (/[\s({[=,|:]/.test(prev) || prev === "") {
                e.preventDefault();
                this.replace(start, end, e.key + e.key, start + 1);
            }
        }
    }

    toggleComment() {
        const { selectionStart: start, selectionEnd: end, value } = this.textarea;
        const first = value.lastIndexOf("\n", start - 1) + 1;
        const lastBreak = value.indexOf("\n", end);
        const stop = lastBreak === -1 ? value.length : lastBreak;
        const block = value.slice(first, stop);

        const commented = /^\s*\{%-?\s*comment\s*-?%\}[\s\S]*\{%-?\s*endcomment\s*-?%\}\s*$/.test(block);
        const indent = (/^[ \t]*/.exec(block) || [""])[0];

        const changed = commented
            ? block.replace(/\{%-?\s*comment\s*-?%\}\s?/, "").replace(/\s?\{%-?\s*endcomment\s*-?%\}/, "")
            : `${indent}{% comment %}${block.slice(indent.length)}{% endcomment %}`;

        this.replace(first, stop, changed);
        this.textarea.selectionStart = first;
        this.textarea.selectionEnd = first + changed.length;
    }

    insert(text) {
        const { selectionStart: start, selectionEnd: end } = this.textarea;
        const caret = text.indexOf("$0");
        const clean = caret === -1 ? text : text.replace("$0", "");
        this.replace(start, end, clean, caret === -1 ? start + clean.length : start + caret);
        this.textarea.focus();
    }

    setValue(text) {
        this.textarea.value = text ?? "";
        this.render();
        this.flush();
    }

    // ---- completions ----

    popupOpen() { return this.popup.classList.contains("dc-open"); }

    closePopup() { this.popup.classList.remove("dc-open"); this.visible = []; }

    maybeComplete(force) {
        const caret = this.textarea.selectionStart;
        if (caret !== this.textarea.selectionEnd) return this.closePopup();

        this.context = contextAt(this.textarea.value, caret);
        if (this.context.kind === "markup" && !force && !this.context.prefix) return this.closePopup();

        const found = matches(this.items, this.context);
        if (found.length === 0) return this.closePopup();
        if (!force && found.length === 1 && found[0].label === this.context.prefix) return this.closePopup();

        this.visible = found;
        this.selected = 0;
        this.paintPopup();
    }

    paintPopup() {
        this.popup.innerHTML = this.visible
            .map((item, i) => `<button type="button" class="dc-ed-item${i === this.selected ? " dc-active" : ""}" data-i="${i}">` +
                `<span class="dc-ed-kind dc-ed-kind-${item.kind}">${escapeHtml(shortKind(item.kind))}</span>` +
                `<span class="dc-ed-label">${escapeHtml(item.label)}</span>` +
                (item.detail ? `<span class="dc-ed-detail">${escapeHtml(item.detail)}</span>` : "") +
                `</button>`)
            .join("");

        for (const button of this.popup.querySelectorAll(".dc-ed-item")) {
            button.addEventListener("mousedown", (e) => {
                e.preventDefault();
                this.selected = Number(button.dataset.i);
                this.accept();
            });
        }

        const caret = this.textarea.selectionStart;
        const upto = this.textarea.value.slice(0, caret);
        const line = upto.split("\n").length - 1;
        const column = caret - (upto.lastIndexOf("\n") + 1);

        const x = this.padLeft + column * this.charWidth - this.textarea.scrollLeft;
        const y = this.padTop + (line + 1) * this.lineHeight - this.textarea.scrollTop;
        const height = this.textarea.clientHeight;

        this.popup.classList.add("dc-open");
        const width = this.popup.offsetWidth || 320;
        this.popup.style.left = `${Math.max(8, Math.min(x, this.textarea.clientWidth - width - 8))}px`;
        if (y > height - 180 && y > 220) {
            this.popup.style.top = "auto";
            this.popup.style.bottom = `${height - y + this.lineHeight}px`;
        } else {
            this.popup.style.bottom = "auto";
            this.popup.style.top = `${y}px`;
        }
    }

    move(delta) {
        this.selected = (this.selected + delta + this.visible.length) % this.visible.length;
        this.paintPopup();
        const active = this.popup.querySelector(".dc-ed-item.dc-active");
        if (active) active.scrollIntoView({ block: "nearest" });
    }

    accept() {
        const item = this.visible[this.selected];
        if (!item) return this.closePopup();

        const caret = this.textarea.selectionStart;
        const prefix = (this.context && this.context.prefix) || "";
        const start = caret - prefix.length;
        const text = item.insert || item.label;
        const offset = text.indexOf("$0");

        this.closePopup();
        this.replace(start, caret, offset === -1 ? text : text.replace("$0", ""), offset === -1 ? start + text.length : start + offset);
        this.textarea.focus();
    }

    setCompletions(items) { this.items = items || []; }

    dispose() {
        clearTimeout(this.timer);
        this.textarea.removeEventListener("input", this.onInput);
        this.textarea.removeEventListener("scroll", this.onScroll);
        this.textarea.removeEventListener("keydown", this.onKeyDown);
        this.textarea.removeEventListener("blur", this.onBlur);
        this.textarea.removeEventListener("click", this.onClick);
    }
}

function shortKind(kind) {
    return { variable: "var", property: "prop", member: "prop", filter: "filter", tag: "tag", snippet: "snip", partial: "partial", keyword: "kw" }[kind] || kind;
}

// ---- interop ----------------------------------------------------------------------------------

export function init(root, dotnet, options) {
    if (!root) return;
    dispose(root);
    editors.set(root, new LiquidEditor(root, dotnet, options));
}

export function setValue(root, text) { editors.get(root)?.setValue(text); }
export function insert(root, text) { editors.get(root)?.insert(text); }
export function setCompletions(root, items) { editors.get(root)?.setCompletions(items); }
export function focus(root) { editors.get(root)?.textarea.focus(); }
export function getValue(root) { return editors.get(root)?.textarea.value ?? ""; }

export function dispose(root) {
    const editor = editors.get(root);
    if (editor) { editor.dispose(); editors.delete(root); }
}

// Writes rendered HTML into the preview iframe without a network round trip. The stylesheets of the
// surrounding page come along, so the preview is styled the way the site is.
export function writePreview(frame, html) {
    if (!frame) return;
    const styles = [...document.querySelectorAll('link[rel="stylesheet"]')]
        .map((l) => `<link rel="stylesheet" href="${l.getAttribute("href")}">`)
        .join("");
    frame.srcdoc =
        `<!DOCTYPE html><html><head><meta charset="utf-8"><base href="/" target="_blank">${styles}` +
        `<style>body{margin:0;padding:0;background:#fff}</style></head><body>${html || ""}</body></html>`;
}
