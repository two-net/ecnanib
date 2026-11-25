// What moves on the pages (FR-ANI-2 to FR-ANI-6), as far as a stylesheet cannot do it alone.
//
// The stylesheet, app.css, says what moves and for how long, and gives none of it to a device
// that asks for reduced motion or to an account that reduced it (FR-ANI-4). This script says
// when: it names a state on an element, and reads the times that it needs from the
// stylesheet. It holds no look and no time of its own, writes no markup and no style, asks the
// server for nothing, and polls nothing. A page works without it: every value then shows at
// once. See docs/motion.md.
//
// The server marks what a page holds:
//   <data class="figure" value="…">   a number that counts from its old value to its new one
//   data-change="…"                   what an element shows now, such as the state of an agent
//   data-key="…"                      which item of a list an element is
//   data-moves                        a part in which something moves for as long as a state lasts
//
// And this script marks, for the stylesheet:
//   data-changed, data-entered        what changed, and an item that entered a list, once each
//   data-still                        for a moment, to have something move again
//   data-offscreen, data-hidden       a part that is off the screen, and a tab that nobody sees
//   aria-busy on <main>               a page that was asked for and is on its way for long
//
// A page changes in two ways. Its part that follows is written over the connection of the page,
// and the page asks for itself again and the browser changes what differs. Both change the
// elements that are there, in their place, which is what this script watches. The second also
// takes away every attribute that the server did not write, and gives an element that is there
// to whatever comes at its place: so what is shown is told apart by its key, and never by its
// element, and the marks that are to last are put back.

const root = document.documentElement;

/** The time that the stylesheet gives under a name, in milliseconds, or none. */
function timeOf(name) {
    const said = getComputedStyle(root).getPropertyValue(name).trim();
    const time = /^(\d*\.?\d+)(ms|s)$/.exec(said);

    return time ? Number(time[1]) * (time[2] === "s" ? 1000 : 1) : 0;
}

/** An element and what it holds that a selector names. */
function within(element, selector) {
    const found = [...element.querySelectorAll(selector)];

    return element.matches(selector) ? [element, ...found] : found;
}

/** Whether any of an element is where the window shows it. */
function onScreen(element) {
    const box = element.getBoundingClientRect();

    return box.bottom > 0 && box.right > 0 && box.top < innerHeight && box.left < innerWidth;
}

// The marks that the stylesheet plays once.

/** Marks an element, so that the stylesheet plays what the mark stands for from its start. */
function mark(element, name) {
    if (element.hasAttribute(name)) {
        // It is played already: taken away and seen without it, it is played again.
        element.removeAttribute(name);
        element.getBoundingClientRect();
    }

    element.setAttribute(name, "");
}

/** Has what moved when an element was first shown move again. */
function again(element) {
    element.setAttribute("data-still", "");
    element.getBoundingClientRect();
    element.removeAttribute("data-still");
}

// A mark is of no use once what it stands for was played. One that stayed would be played
// again whenever the element is shown anew.
document.addEventListener("animationend", event => {
    if (event.target instanceof Element) {
        event.target.removeAttribute("data-changed");
        event.target.removeAttribute("data-entered");
    }
});

// Figures.

// The figures that count now, each with the text node that it counts in, the text that the
// server wrote there, the value that it shows at the moment, and the frame that it waits for.
const counts = new Map();

// What the server last wrote in a figure: whether it writes a sign is seen from it.
const written = new WeakMap();

/** How many places after the point a number is written with. */
function placesOf(number) {
    const point = number.indexOf(".");

    return point < 0 ? 0 : number.length - point - 1;
}

/** A value on its way, written as the figure writes its numbers. */
function say(value, places, signed) {
    const size = Math.abs(value).toFixed(places);

    return signed && Number(size) !== 0 ? (value > 0 ? "+" : "-") + size : size;
}

/** Stops a figure that counts where it is, and takes what it holds for what the server wrote. */
function settle(figure) {
    const run = counts.get(figure);

    if (run) {
        cancelAnimationFrame(run.frame);
        counts.delete(figure);
    }

    if (figure.firstChild instanceof Text) {
        written.set(figure, figure.firstChild.data);
    }
}

/** Ends a figure that counts, at what the server wrote. */
function end(figure) {
    const run = counts.get(figure);

    if (run) {
        cancelAnimationFrame(run.frame);
        counts.delete(figure);

        if (figure.firstChild === run.node) {
            run.node.data = run.last;
        }
    }
}

/**
 * Counts a figure from the value that it had to the one that it has now. The server has
 * written the new number already: it is what the figure ends at, to the character. The text
 * node is written in its place and never replaced, because the connection of the page writes
 * to the node that it knows.
 */
function count(figure, before) {
    const node = figure.firstChild;
    const running = counts.get(figure);
    const earlier = written.get(figure) ?? "";

    settle(figure);

    if (!(node instanceof Text) || before === null) {
        return;
    }

    const after = figure.getAttribute("value") ?? "";
    const last = node.data;
    const from = running ? running.now : Number(before);
    const to = Number(after);
    const takes = timeOf("--figure-change");

    // A text that writes the sign of its number counts through nothing. One that says a loss
    // in words does not: its number would shrink and grow under a word that changed at once.
    const signed = /^[+-]/.test(last) || /^[+-]/.test(earlier);
    const counted = Number.isFinite(from) && Number.isFinite(to) && from !== to && (signed || from * to >= 0);

    // Nothing is counted where the stylesheet gives no time for it, which is how a device and
    // an account ask for less, nor where nobody would see it (FR-ANI-4, FR-ANI-6).
    if (!takes || !counted || document.hidden || !onScreen(figure)) {
        return;
    }

    const places = Math.max(placesOf(before), placesOf(after));
    const began = performance.now();
    const run = { node, last, now: from, frame: 0 };

    const step = time => {
        const part = Math.min(Math.max((time - began) / takes, 0), 1);

        if (part === 1 || figure.firstChild !== node || !figure.isConnected) {
            end(figure);

            return;
        }

        // Fast at first and slower towards the new value.
        run.now = from + (to - from) * (1 - (1 - part) ** 3);
        node.data = say(run.now, places, signed);
        run.frame = requestAnimationFrame(step);
    };

    counts.set(figure, run);

    // The old value again, before the browser paints the new one that the server wrote.
    node.data = say(from, places, signed);
    run.frame = requestAnimationFrame(step);
}

// Parts in which something moves for as long as a state lasts, and which are off the screen.
const away = new Set();

const seen = new IntersectionObserver(entries => {
    for (const { target, isIntersecting } of entries) {
        if (!target.isConnected) {
            seen.unobserve(target);
            away.delete(target);
        } else if (isIntersecting) {
            away.delete(target);
            target.removeAttribute("data-offscreen");
        } else {
            away.add(target);
            target.setAttribute("data-offscreen", "");
        }
    }
});

/** Puts back the marks that are to last, which the browser took away with a page that it changed. */
function keep() {
    root.toggleAttribute("data-hidden", document.hidden);

    for (const part of away) {
        if (part.isConnected) {
            part.setAttribute("data-offscreen", "");
        } else {
            away.delete(part);
        }
    }
}

// Lists: the keys of the items of each, as they were when it was last looked at.
const keys = new WeakMap();

/** Takes in what a part of the page holds, and names the lists that it has items of. */
function learn(element, lists) {
    for (const item of within(element, "[data-key]")) {
        lists.add(item.parentElement);
    }

    for (const figure of within(element, "data.figure")) {
        settle(figure);
    }

    for (const part of within(element, "[data-moves]")) {
        seen.observe(part);
    }
}

/**
 * Reads what changed on the page. On a page that the browser went to, nothing of it moves:
 * what was there belonged to the page before, so it is only taken in.
 */
function read(records, quiet) {
    const lists = new Set();
    const rekeyed = new Set();
    const valued = new Map();
    const changed = new Map();

    for (const record of records) {
        const target = record.target;

        if (record.type === "childList") {
            for (const node of record.addedNodes) {
                if (node instanceof Element) {
                    learn(node, lists);
                }
            }

            for (const node of record.removedNodes) {
                if (node instanceof Element && node.hasAttribute("data-key")) {
                    lists.add(target);
                }
            }
        } else if (record.attributeName === "data-key") {
            // The element shows another item now. What it holds did not change: it is other.
            rekeyed.add(target);
            lists.add(target.parentElement);
        } else if (record.attributeName === "data-change") {
            if (!changed.has(target)) {
                changed.set(target, record.oldValue);
            }
        } else if (target.matches("data.figure") && !valued.has(target)) {
            valued.set(target, record.oldValue);
        }
    }

    for (const list of lists) {
        if (!list?.isConnected) {
            continue;
        }

        const before = keys.get(list);
        const items = [...list.children].filter(item => item.hasAttribute("data-key"));

        keys.set(list, new Set(items.map(item => item.getAttribute("data-key"))));

        // An item whose key the list did not have entered it. A list that is new had none.
        for (const item of quiet ? [] : items) {
            if (!before?.has(item.getAttribute("data-key"))) {
                mark(item, "data-entered");
            }
        }
    }

    for (const [element, before] of changed) {
        const now = element.getAttribute("data-change");
        const other = rekeyed.has(element.closest("[data-key]"));

        if (!quiet && !other && before !== null && now !== null && now !== before) {
            mark(element, "data-changed");
            again(element);
        }
    }

    for (const [figure, before] of valued) {
        if (quiet || rekeyed.has(figure.closest("[data-key]"))) {
            settle(figure);
        } else {
            count(figure, before);
        }
    }
}

// The address of the page that is shown. While the browser is on its way to another page
// without leaving this one, the address is the new one already and the page is still the old.
const address = () => location.pathname + location.search;
const content = () => document.querySelector("main");

let here = address();
let waiting = 0;

const observer = new MutationObserver(records => read(records, address() !== here));

/** The page that was asked for is there, or will not come: the placeholder goes. */
function arrived() {
    clearTimeout(waiting);
    waiting = 0;
    content()?.removeAttribute("aria-busy");
}

/** The page that was asked for is on its way for long: the placeholder shows, for so long at the most. */
function late() {
    content()?.setAttribute("aria-busy", "true");
    waiting = setTimeout(arrived, timeOf("--loading-longest"));
}

// The page as it is when this script starts: nothing of it moves for having been there.
{
    const lists = new Set();

    learn(root, lists);

    for (const list of lists) {
        keys.set(list, new Set([...list.children].map(item => item.getAttribute("data-key")).filter(key => key !== null)));
    }

    keep();
}

observer.observe(root, {
    subtree: true,
    childList: true,
    attributes: true,
    attributeOldValue: true,
    attributeFilter: ["value", "data-key", "data-change"],
});

document.addEventListener("visibilitychange", () => {
    root.toggleAttribute("data-hidden", document.hidden);

    // A browser draws no frame of a tab in the background, so a figure would stop on its way:
    // it ends at its new value now (FR-ANI-6).
    if (document.hidden) {
        for (const figure of [...counts.keys()]) {
            end(figure);
        }
    }
});

// A page that is shown again from the memory of the browser comes as it was left.
addEventListener("pageshow", arrived);

// The framework says nothing of a page that does not come, as when the device is offline. The
// request that failed is heard here, and the content is given back.
addEventListener("unhandledrejection", arrived);

// What the framework says of going to a page without leaving the one that is shown.
const framework = globalThis.Blazor;

framework?.addEventListener("enhancednavigationstart", () => {
    clearTimeout(waiting);
    waiting = 0;

    // A page that asks for itself again is there already, and shows no placeholder.
    if (address() !== here) {
        waiting = setTimeout(late, timeOf("--loading-after"));
    }
});

framework?.addEventListener("enhancedload", () => {
    // The browser has changed the page just now, and says so before this script is told what
    // changed: so it is read here, and known to be of another page where the address is another.
    const moved = address() !== here;

    read(observer.takeRecords(), moved);
    here = address();
    arrived();

    if (moved) {
        for (const figure of [...counts.keys()]) {
            settle(figure);
        }

        const page = content();

        if (page) {
            again(page);
        }
    }

    keep();
});

framework?.addEventListener("enhancednavigationend", arrived);
