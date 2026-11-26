using Microsoft.Playwright;

namespace Ecnanib.Tests.Accessibility;

/// <summary>
/// A visit that a browser makes: a page of Chromium, which runs the scripts of a page, paints
/// it, and gives the focus where the page says. It reaches a state of a screen by the steps
/// that <see cref="ReadVisit"/> takes over HTTP, so one list leads both through the same states.
/// </summary>
internal sealed class DrivenVisit(IPage page) : IVisit
{
    // How long a page is given to arrive, and to do what it does by itself as it opens.
    private const float Arrives = 15_000;
    private const float Settles = 3_000;

    /// <summary>
    /// Fills the form with a name as its user would, and keeps the button that is to be pressed.
    /// The browser is told not to hold the form back: a state of a form that was refused is what
    /// the server says of it. A field that the form has no control for is brought all the same,
    /// as a client that posts it would bring it.
    /// </summary>
    /// <returns>Nothing when the form is ready, or what is wrong.</returns>
    private const string Prepare =
        """
        ({ form, fields }) => {
            const posted = [...document.querySelectorAll("form")]
                .find(candidate => candidate.querySelector("input[name='_handler']")?.value === form);

            if (!posted) {
                return "missing";
            }

            posted.noValidate = true;

            const buttons = [...posted.querySelectorAll("button, input[type='submit']")]
                .filter(button => !button.disabled && (button.getAttribute("type") ?? "submit").toLowerCase() === "submit");
            const names = (button, [name, value]) => button.getAttribute("name") === name && (button.getAttribute("value") ?? "") === value;
            const pressed = buttons.find(button => fields.some(field => names(button, field)))
                ?? buttons.find(button => !button.hasAttribute("name"))
                ?? buttons[0]
                ?? null;

            for (const field of fields) {
                const [name, value] = field;

                if (pressed && names(pressed, field)) {
                    continue;
                }

                const controls = [...posted.elements]
                    .filter(control => control.name === name && !(control instanceof HTMLButtonElement) && control.type !== "submit");
                let entered = false;

                for (const control of controls) {
                    if (control instanceof HTMLInputElement && (control.type === "checkbox" || control.type === "radio")) {
                        control.checked = control.value === value;
                        entered ||= control.checked;
                    } else if (control instanceof HTMLSelectElement) {
                        if (![...control.options].some(option => option.value === value)) {
                            control.add(new Option(value, value));
                        }

                        control.value = value;
                        entered = true;
                    } else {
                        control.value = value;
                        entered = true;
                    }
                }

                if (!entered && value !== "") {
                    const brought = document.createElement("input");

                    brought.type = "hidden";
                    brought.name = name;
                    brought.value = value;
                    posted.append(brought);
                }
            }

            window.answeredByTheVisit = { posted, pressed };

            return null;
        }
        """;

    /// <summary>Presses the button that was kept, or sends the form where it has none.</summary>
    private const string Press =
        """
        () => {
            const { posted, pressed } = window.answeredByTheVisit;

            if (pressed) {
                pressed.click();
            } else {
                posted.requestSubmit();
            }
        }
        """;

    /// <summary>The page of the browser, for what a check does with the state that the visit reached.</summary>
    public IPage Page => page;

    public string Address => new Uri(page.Url).PathAndQuery;

    public async Task OpenAsync(string address)
    {
        await page.GotoAsync(address, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = Arrives });
        await SettleAsync();
    }

    public async Task SubmitAsync(string form, params (string Name, string Value)[] fields)
    {
        var wrong = await page.EvaluateAsync<string?>(
            Prepare, new { form, fields = fields.Select(field => new[] { field.Name, field.Value }).ToArray() });

        if (wrong is not null)
        {
            throw new VisitFailedException($"The page at {Address} has no form called \"{form}\".");
        }

        // The answer is a page of its own, at the same address or where the answer leads: the
        // browser says when it has come to it.
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void Arrived(object? sender, IFrame frame)
        {
            if (frame == page.MainFrame)
            {
                arrived.TrySetResult();
            }
        }

        page.FrameNavigated += Arrived;

        try
        {
            await page.EvaluateAsync(Press);
            await arrived.Task.WaitAsync(TimeSpan.FromMilliseconds(Arrives), TestContext.Current.CancellationToken);
            await page.WaitForLoadStateAsync(LoadState.Load, new PageWaitForLoadStateOptions { Timeout = Arrives });
        }
        finally
        {
            page.FrameNavigated -= Arrived;
        }

        await SettleAsync();
    }

    public Task<string> MarkupAsync() => page.ContentAsync();

    public Task<int> CountAsync(string selector) => page.Locator(selector).CountAsync();

    /// <summary>
    /// Waits for what a page does by itself as it opens: it gives the focus to its heading or to
    /// what a form was answered with, and a page that follows makes its connection. A page that
    /// does neither in time is gone on with: the checks say what is missing.
    /// </summary>
    private async Task SettleAsync()
    {
        try
        {
            await page.WaitForFunctionAsync(
                "() => document.activeElement && document.activeElement !== document.body", null, new PageWaitForFunctionOptions { Timeout = Settles });
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = Settles });
        }
        catch (TimeoutException)
        {
        }
    }
}
