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

export function focusElement(element) {
    if (element) element.focus();
}

export function copyText(text) {
    if (navigator.clipboard) return navigator.clipboard.writeText(text);
    return Promise.resolve();
}
