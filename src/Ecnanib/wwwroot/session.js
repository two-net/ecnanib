// The notice before a session ends (NFR-USA-1, NFR-SEC-1). A session ends after a period of
// inactivity. A page of a user says how many seconds its session has left, and how many seconds
// before the end its user is to be told: this script waits until then, asks the server how long
// is left by now, and shows the dialog of the page that says so. The dialog has a button that
// says its user is still there, which is a request of theirs and so starts the period anew.
//
// The script holds no address, no text, and no time of day: the page brings where to ask and
// where to answer, and the server says what the dialog says. It reads no cookie and stores
// nothing. A page works without it, and its session then ends as it always did. A page of a
// visitor has no notice, and nothing is done for it. See docs/accounts.md.

const holder = () => document.getElementById("session-notice");
const dialog = () => document.getElementById("session-warning");
const sentence = () => document.getElementById("session-warning-text");
const failure = () => document.getElementById("session-warning-failed");

// How long after a question that got no answer the server is asked again.
const again = 15_000;

// When the session ends, as far as this page knows, and how long before that its user is told.
let ends = 0;
let lead = 0;
let waiting = 0;

/** Waits for so long, and then asks the server. Only one wait is under way at a time. */
function wait(time) {
    clearTimeout(waiting);
    waiting = setTimeout(look, Math.max(0, time));
}

/** Reads what the page says of its session, and waits until its user is to be told. */
function plan() {
    const notice = holder();

    if (!notice) {
        clearTimeout(waiting);

        return;
    }

    const left = Number(notice.getAttribute("data-left")) * 1000;

    lead = Number(notice.getAttribute("data-warn")) * 1000;
    ends = Date.now() + left;

    // A dialog that is open has asked already, and asks again at the end.
    if (!dialog()?.open) {
        wait(left - lead);
    }
}

/** What the server said of the session: how many seconds are left, and what the dialog says. */
function heard(answer) {
    const left = Number(answer?.left) * 1000;

    if (!(left > 0)) {
        // The session has ended. The page is asked for anew, which leads to the page to sign
        // in on, and from there back here.
        location.replace(location.href);

        return;
    }

    ends = Date.now() + left;

    if (left > lead) {
        // Its user was there since, here or in another window: nothing is to be said yet.
        if (dialog()?.open) {
            dialog().close();
        }

        wait(left - lead);

        return;
    }

    show(answer.says, false);

    // And once more when the session is to have ended, a moment after it.
    wait(left + 1000);
}

/** Shows the dialog, with what the server said, or with what the page brought when it said nothing. */
function show(says, unreached) {
    const notice = dialog();

    if (!notice) {
        return;
    }

    if (says) {
        sentence()?.replaceChildren(says);
    }

    failure()?.toggleAttribute("hidden", !unreached);

    if (!notice.open) {
        notice.showModal();
    }
}

/** The server gave no answer: the dialog says so, and nothing is said of the session itself. */
function unheard() {
    show(null, true);
    wait(again);
}

/** Asks the server how long the session has left. Asking is no activity of the user. */
async function look() {
    const notice = holder();

    if (!notice) {
        return;
    }

    try {
        const answer = await fetch(notice.getAttribute("data-ask"), {
            headers: { Accept: "application/json" },
            cache: "no-store",
            credentials: "same-origin",
        });

        if (!answer.ok) {
            throw new Error(String(answer.status));
        }

        heard(await answer.json());
    } catch {
        unheard();
    }
}

/** Says that the user is still there: the form of the button is sent to where the button says. */
async function stay(button) {
    try {
        const answer = await fetch(button.formAction, {
            method: "POST",
            body: new URLSearchParams(new FormData(button.form)),
            headers: { Accept: "application/json" },
            cache: "no-store",
            credentials: "same-origin",
        });

        if (!answer.ok) {
            // The page is older than its session, or of another user by now: the server is
            // asked what is left, and says what to do.
            await look();

            return;
        }

        heard(await answer.json());
    } catch {
        unheard();
    }
}

/** The first button of the dialog: the one that says its user is still there. */
const staying = () => dialog()?.querySelector("button[formaction]");

// The button that stays is answered here, without leaving the page. The one that signs out
// sends its form as the button of the bar does.
document.addEventListener("submit", event => {
    const button = event.submitter;

    if (button && button === staying()) {
        event.preventDefault();
        stay(button);
    }
});

// The key that closes a dialog says the same as the button that stays: a dialog that closed
// and left the session to end would have said nothing.
document.addEventListener("cancel", event => {
    const button = staying();

    if (event.target === dialog() && button) {
        event.preventDefault();
        stay(button);
    }
}, true);

/** A device that slept, or a window that was not shown, may be past the moment by now. */
function woke() {
    if (holder() && document.visibilityState === "visible" && Date.now() >= ends - lead) {
        look();
    }
}

document.addEventListener("visibilitychange", woke);
addEventListener("pageshow", woke);
addEventListener("online", woke);

// A page that the browser went to without leaving this one says anew how long is left, and so
// does a page that asked for itself again.
globalThis.Blazor?.addEventListener("enhancedload", plan);

plan();
