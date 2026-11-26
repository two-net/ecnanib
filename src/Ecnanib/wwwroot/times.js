// The time zone of the device (NFR-USA-3). The pages write a time in the local time zone of
// their user, and only the device knows which one that is. A form that asks for it has a field
// for it, and this script writes into that field what the browser says its time zone is: the
// form of the sign-in has one. The server keeps what the form says, and writes every time.
//
// The script asks the server nothing, reads no cookie, and stores nothing. It holds no address
// and no text, and writes no time: what a time says is for the server alone. A page works
// without it, and its times are then in UTC. See docs/times.md.

/** What the browser says the time zone of the device is, as the time zone database names it. */
function zone() {
    try {
        return Intl.DateTimeFormat().resolvedOptions().timeZone ?? "";
    } catch {
        // A browser that cannot say says nothing, and the server takes that for UTC.
        return "";
    }
}

/** Writes the time zone into every field of the page that asks for it. */
function say() {
    const said = zone();

    for (const field of document.querySelectorAll("input[data-time-zone]")) {
        field.value = said;
    }
}

// As a form is sent, so that a field which came to the page since is filled too.
document.addEventListener("submit", say, true);

// A page that the browser went to without leaving this one brings its own fields.
globalThis.Blazor?.addEventListener("enhancedload", say);

say();
