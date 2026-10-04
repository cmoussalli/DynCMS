// DynCMS admin interop module.
let savedRange = null;

export const rte = {
    init(element, dotnetRef) {
        if (!element || element.__dcInit) return;
        element.__dcInit = true;
        element.addEventListener('input', () => {
            dotnetRef.invokeMethodAsync('OnHtmlChanged', element.innerHTML);
        });
        element.addEventListener('paste', (e) => {
            // Paste as plain text to keep the HTML clean.
            const text = (e.clipboardData || window.clipboardData).getData('text/plain');
            if (!text) return;
            e.preventDefault();
            document.execCommand('insertText', false, text);
        });
    },

    setHtml(element, html) {
        if (element && element.innerHTML !== html) element.innerHTML = html;
    },

    exec(element, command, value) {
        if (!element) return;
        element.focus();
        document.execCommand(command, false, value ?? null);
        element.dispatchEvent(new Event('input'));
    },

    saveSelection() {
        const sel = window.getSelection();
        savedRange = sel && sel.rangeCount > 0 ? sel.getRangeAt(0).cloneRange() : null;
    },

    execWithSelection(element, command, value) {
        if (!element) return;
        element.focus();
        if (savedRange) {
            const sel = window.getSelection();
            sel.removeAllRanges();
            sel.addRange(savedRange);
        }
        document.execCommand(command, false, value ?? null);
        element.dispatchEvent(new Event('input'));
        savedRange = null;
    }
};

export const codeEditor = {
    // Tab indents (Shift+Tab outdents the current line), Ctrl/Cmd+S asks the component to save.
    init(textarea, dotnetRef) {
        if (!textarea || textarea.__dcInit) return;
        textarea.__dcInit = true;
        const changed = () => textarea.dispatchEvent(new Event('input', { bubbles: true }));
        textarea.addEventListener('keydown', (e) => {
            if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') {
                e.preventDefault();
                dotnetRef.invokeMethodAsync('SaveAsync');
                return;
            }
            if (e.key !== 'Tab') return;
            e.preventDefault();
            const { selectionStart: start, selectionEnd: end, value } = textarea;
            if (e.shiftKey || start !== end) {
                const lineStart = value.lastIndexOf('\n', start - 1) + 1;
                const block = value.slice(lineStart, end);
                const lines = block.split('\n');
                const next = e.shiftKey
                    ? lines.map(l => l.replace(/^(  |\t)/, ''))
                    : lines.map(l => '  ' + l);
                const text = next.join('\n');
                textarea.setRangeText(text, lineStart, end, 'select');
            } else {
                textarea.setRangeText('  ', start, end, 'end');
            }
            changed();
        });
        textarea.focus();
    }
};

export function focusElement(element) {
    if (element) element.focus();
}

export function copyText(text) {
    if (navigator.clipboard) return navigator.clipboard.writeText(text);
    return Promise.resolve();
}
