// Links in the shell. It's one listener rather than Blazor events because it has to work before a
// circuit connects, and on the error page, which never has one. It runs in the capture phase so that
// it comes before blazor.web.js, which would only scroll to the target and leave focus behind.

// href="#main" counts as this page whatever the path, because <base href="/"> would resolve it to the
// landing page.
const pointsAtThisPage = link => link.getAttribute("href").startsWith("#") || link.pathname === location.pathname;

document.addEventListener("click", event => {
    const link = event.target.closest("a[href]");

    if (!link) {
        return;
    }

    link.closest("[popover]:popover-open")?.hidePopover();

    const target = pointsAtThisPage(link) && link.hash && document.getElementById(link.hash.slice(1));

    if (target) {
        event.preventDefault();
        target.focus();
    }
}, { capture: true });

// The browser's time zone, for pages to show times in (FR-PREF-05) when the user hasn't chosen one. The
// server can't see it otherwise, and a cookie reaches the first render of every page. The name is
// DisplayTime.BrowserTimeZoneCookie. It's written on every load, so it follows a laptop that travels.
const timeZone = Intl.DateTimeFormat().resolvedOptions().timeZone;

if (timeZone) {
    const secure = location.protocol === "https:" ? "; secure" : "";

    document.cookie = `ecnanib.time-zone=${encodeURIComponent(timeZone)}; path=/; max-age=31536000; samesite=lax${secure}`;
}
