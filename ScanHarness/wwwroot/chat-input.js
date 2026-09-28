// Chat input behaviour for <textarea data-send-on-enter="<send button id>">:
//   Enter        sends (clicks the send button, so it does nothing while that button is disabled)
//   Shift+Enter  inserts a new line (the browser's default)
// Enter while an input method is composing (e.g. Japanese) confirms the text instead of sending.
// The browser inserts the newline before a server-side handler could stop it, so this runs here.
// Listeners are on the document, so they keep working when Blazor re-renders the textarea.
(function () {
    const maxRows = 6;

    function isChatInput(element) {
        return element instanceof HTMLTextAreaElement && element.hasAttribute("data-send-on-enter");
    }

    // Grow with the content up to maxRows lines, then scroll (for browsers without field-sizing).
    function resize(textarea) {
        if (CSS.supports("field-sizing", "content")) {
            return;
        }

        const style = getComputedStyle(textarea);
        const lineHeight = parseFloat(style.lineHeight) || 20;
        const chrome = parseFloat(style.paddingTop) + parseFloat(style.paddingBottom) +
            parseFloat(style.borderTopWidth) + parseFloat(style.borderBottomWidth);
        textarea.style.height = "auto";
        textarea.style.height = Math.min(textarea.scrollHeight, lineHeight * maxRows + chrome) + "px";
    }

    document.addEventListener("keydown", event => {
        const textarea = event.target;
        if (!isChatInput(textarea) || event.key !== "Enter" || event.shiftKey || event.isComposing
            || event.ctrlKey || event.altKey || event.metaKey) {
            return;
        }

        event.preventDefault();
        const send = document.getElementById(textarea.getAttribute("data-send-on-enter"));
        if (send && !send.disabled) {
            send.click();
            // The server clears the text once it has the message; shrink back when it does.
            setTimeout(() => resize(textarea), 250);
        }
    });

    document.addEventListener("input", event => {
        if (isChatInput(event.target)) {
            resize(event.target);
        }
    });
})();
