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
