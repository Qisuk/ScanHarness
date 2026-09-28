// Theme picker: "dark" (default), "light", or "system" (follows the OS setting).
// Loaded synchronously in <head> so the saved theme is applied before the first paint,
// with no flash of the wrong theme. The choice is kept per browser in localStorage.
(function () {
    const storageKey = "sh-theme";
    const choices = ["dark", "light", "system"];
    const defaultChoice = "dark";
    const prefersDark = window.matchMedia("(prefers-color-scheme: dark)");

    function savedChoice() {
        try {
            const value = localStorage.getItem(storageKey);
            return choices.includes(value) ? value : defaultChoice;
        } catch {
            // Storage can be blocked (private mode, site data disabled); fall back to the default.
            return defaultChoice;
        }
    }

    function save(choice) {
        try {
            localStorage.setItem(storageKey, choice);
        } catch {
            // Not persisted; the theme still changes for this page.
        }
    }

    // Sets data-theme (our tokens) and data-bs-theme (Bootstrap's built-in dark mode) on <html>,
    // and syncs every picker on the page to the saved choice.
    function apply() {
        const choice = savedChoice();
        const theme = choice === "system" ? (prefersDark.matches ? "dark" : "light") : choice;
        const root = document.documentElement;
        root.dataset.theme = theme;
        root.dataset.bsTheme = theme;

        document.querySelectorAll("[data-theme-picker]").forEach(picker => {
            picker.value = choice;
        });
    }

    apply();

    // Delegated so it keeps working when Blazor re-renders the picker.
    document.addEventListener("change", event => {
        if (event.target.matches("[data-theme-picker]") && choices.includes(event.target.value)) {
            save(event.target.value);
            apply();
        }
    });

    prefersDark.addEventListener("change", () => {
        if (savedChoice() === "system") {
            apply();
        }
    });

    // Sync the picker once the body exists, and again after Blazor's enhanced navigation
    // swaps the page content.
    document.addEventListener("DOMContentLoaded", apply);
    window.addEventListener("load", () => window.Blazor?.addEventListener?.("enhancedload", apply));
})();
