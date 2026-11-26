using System.Text.Json;
using Microsoft.Playwright;

namespace Ecnanib.Tests.Accessibility;

/// <summary>
/// A walk through a page with the keyboard (NFR-USA-1), as a browser makes it: Tab is pressed
/// from where the focus is when the page opens until it has been round. What is held: the
/// focus starts where the page says; every control of the page is a stop, once, in the order
/// of the page; nothing else is a stop; the keyboard gets past every one; and each stop shows
/// the line of the focus, with a contrast of 3 to 1 or more against what is behind it, and is
/// on the screen while it has the focus. See docs/accessibility.md.
/// </summary>
internal static class KeyboardWalk
{
    /// <summary>What a line that tells a control apart needs against what is behind it (WCAG 2.1, 1.4.11).</summary>
    private const double ForALine = 3.0;

    // What an element is called in a finding: its tag, its id, its classes, and a few words of what it says.
    private const string Describe =
        """
        const describe = element => {
            const classes = typeof element.className === "string" && element.className.trim()
                ? "." + element.className.trim().split(/\s+/).join(".")
                : "";
            const says = (element.getAttribute("aria-label") ?? element.textContent ?? "").trim().replace(/\s+/g, " ").slice(0, 40);

            return `<${element.tagName.toLowerCase()}${element.id ? "#" + element.id : ""}${classes}>${says ? ` "${says}"` : ""}`;
        };
        """;

    /// <summary>
    /// The stops that the page is to have, in the order of the page: its controls, and the
    /// named parts that scroll. A control that is not shown is none, nor is one that is turned
    /// off, nor one behind a dialog that is open. Of the choices of one question the keyboard
    /// stops at the one that is chosen, or at the first. It also says where the walk starts.
    /// </summary>
    private const string Stops =
        "(selector) => {" + Describe +
        """
            const modal = document.querySelector("dialog:modal");
            const within = modal ?? document;
            const stops = [...within.querySelectorAll("a[href], button, input, select, textarea, summary, [tabindex]")].filter(element => {
                if (element.tabIndex < 0 || element.disabled || element.closest("[inert]")) {
                    return false;
                }

                if (element instanceof HTMLInputElement && element.type === "hidden") {
                    return false;
                }

                if (!element.checkVisibility({ visibilityProperty: true, contentVisibilityAuto: true })) {
                    return false;
                }

                if (element instanceof HTMLInputElement && element.type === "radio") {
                    const group = [...(element.form ?? document).querySelectorAll("input[type=radio]")].filter(radio => radio.name === element.name);

                    return element === (group.find(radio => radio.checked) ?? group[0]);
                }

                return true;
            });

            window.stopsOfTheWalk = stops;

            // What is to have the focus as the page opens: what asks for it, or what the layout names.
            const asks = [...document.querySelectorAll("[autofocus]")].find(element => element.checkVisibility());
            const named = asks ?? document.querySelector(selector);
            const has = document.activeElement && document.activeElement !== document.body ? document.activeElement : null;

            // The first stop after it, in the order of the page.
            let first = 0;

            if (has) {
                const own = stops.indexOf(has);

                first = own >= 0
                    ? (own + 1) % Math.max(stops.length, 1)
                    : Math.max(0, stops.findIndex(stop => has.compareDocumentPosition(stop) & Node.DOCUMENT_POSITION_FOLLOWING));
            }

            return {
                stops: stops.map(describe),
                first,
                has: has ? describe(has) : null,
                named: named ? describe(named) : null,
                starts: has !== null && has === named,
            };
        }
        """;

    /// <summary>What has the focus after a press of the key, and how it shows.</summary>
    private const string Stop =
        "() => {" + Describe +
        """
            const has = document.activeElement;

            if (!has || has === document.body || has === document.documentElement) {
                return { index: -1 };
            }

            const look = getComputedStyle(has);
            const box = has.getBoundingClientRect();
            const bright = colour => {
                const [red, green, blue] = (colour.match(/[\d.]+/g) ?? ["0", "0", "0"]).slice(0, 3).map(Number).map(part => {
                    part /= 255;

                    return part <= 0.04045 ? part / 12.92 : Math.pow((part + 0.055) / 1.055, 2.4);
                });

                return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
            };

            // What is behind the line, which is drawn around the element: the first of what it
            // stands in that has a colour of its own, or the page, which is white where a
            // browser is told that the device is light.
            let behind = "rgb(255, 255, 255)";

            for (let around = has.parentElement; around; around = around.parentElement) {
                const colour = getComputedStyle(around).backgroundColor;

                if (colour && colour !== "transparent" && !/rgba\(\d+, \d+, \d+, 0\)/.test(colour)) {
                    behind = colour;
                    break;
                }
            }

            const [lighter, darker] = [bright(look.outlineColor), bright(behind)].sort((one, other) => other - one);

            // What has the focus and is none of the stops is said apart from the focus being
            // out of the page: it is a stop that the page is not to have.
            const index = window.stopsOfTheWalk.indexOf(has);

            return {
                index: index < 0 ? -2 : index,
                what: describe(has),
                outlined: look.outlineStyle === "solid" && parseFloat(look.outlineWidth) >= 2,
                contrast: (lighter + 0.05) / (darker + 0.05),
                shown: box.bottom > 0 && box.right > 0 && box.top < innerHeight && box.left < innerWidth,
            };
        }
        """;

    /// <summary>Walks the page that is open, and says what it found: nothing, for a page that keeps to all of it.</summary>
    /// <param name="page">The page, as it is after it opened.</param>
    /// <param name="focusOnLoad">The selector of what takes the focus when a page opens: <c>Screens.FocusOnLoad</c>.</param>
    public static async Task<IReadOnlyList<string>> FindingsAsync(IPage page, string focusOnLoad)
    {
        var findings = new List<string>();
        var start = await page.EvaluateAsync<JsonElement>(Stops, focusOnLoad);
        var stops = start.GetProperty("stops").EnumerateArray().Select(stop => stop.GetString()!).ToList();
        var first = start.GetProperty("first").GetInt32();

        if (!start.GetProperty("starts").GetBoolean())
        {
            findings.Add(
                $"The focus is at {Text(start, "has") ?? "nothing"} when the page opens, and not at {Text(start, "named") ?? "anything that the page names"}.");
        }

        if (stops.Count == 0)
        {
            return findings;
        }

        // Round once, and two presses more: one for what the browser itself has between the
        // last stop and the first, and one to see that the first comes again.
        var reached = new List<int>();

        for (var press = 0; press < stops.Count + 2; press++)
        {
            await page.Keyboard.PressAsync("Tab");

            var stop = await page.EvaluateAsync<JsonElement>(Stop);
            var index = stop.GetProperty("index").GetInt32();

            if (index == -1)
            {
                // Out of the page, to what the browser itself has, and back in with the next press.
                continue;
            }

            var what = Text(stop, "what")!;

            if (index < 0)
            {
                findings.Add($"Tab stopped at {what}, which is no control of the page and no named part that scrolls.");

                continue;
            }

            if (reached.Count < stops.Count && !reached.Contains(index))
            {
                if (!stop.GetProperty("outlined").GetBoolean())
                {
                    findings.Add($"{what} has the focus without the line that says so: two pixels, solid.");
                }
                else if (stop.GetProperty("contrast").GetDouble() < ForALine)
                {
                    findings.Add($"The line around {what} has a contrast of {stop.GetProperty("contrast").GetDouble():0.00} to 1 against what is behind it, and needs {ForALine:0} to 1.");
                }

                if (!stop.GetProperty("shown").GetBoolean())
                {
                    findings.Add($"{what} has the focus and is not on the screen.");
                }
            }

            reached.Add(index);
        }

        // The order of the keyboard is the order of the page, from the first stop after where
        // the focus was, round to the stop before it.
        var expected = Enumerable.Range(0, stops.Count).Select(step => (first + step) % stops.Count).ToList();
        var walked = reached.Take(stops.Count).ToList();

        for (var step = 0; step < expected.Count; step++)
        {
            if (step >= walked.Count)
            {
                findings.Add(
                    $"The keyboard does not get past {stops[walked.Count > 0 ? walked[^1] : expected[0]]}: {stops[expected[step]]} is not reached.");

                break;
            }

            if (walked[step] != expected[step])
            {
                findings.Add(
                    walked.Take(step).Contains(walked[step])
                        ? $"The keyboard does not get past {stops[walked[step]]}: it comes there again before {stops[expected[step]]}."
                        : $"Tab went to {stops[walked[step]]}, and {stops[expected[step]]} comes first in the page.");

                break;
            }
        }

        return findings;
    }

    private static string? Text(JsonElement of, string name) =>
        of.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
