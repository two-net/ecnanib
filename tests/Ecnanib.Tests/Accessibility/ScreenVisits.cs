using AngleSharp.Dom;

namespace Ecnanib.Tests.Accessibility;

/// <summary>
/// A browser at a page, as far as reaching a state of a screen takes one: it opens an address,
/// answers a form of the page that is open, and says what is there. A client over HTTP is one,
/// and so is a browser that a test drives, so both reach a state by the same steps.
/// </summary>
internal interface IVisit
{
    /// <summary>The path and query of the page that is open.</summary>
    string Address { get; }

    /// <summary>Opens an address, and follows where the answer leads.</summary>
    Task OpenAsync(string address);

    /// <summary>
    /// Answers a form of the page that is open, as its user does: with everything that the form
    /// holds, the given fields in place of its own, and the press of one button. A field that
    /// names a button of the form presses that one. Otherwise the button that brings nothing of
    /// its own is pressed, which is the one that does what the form is for, or the first there is.
    /// What a browser would hold back for its user, such as a required field that is empty, is
    /// sent all the same: the states of a form that was refused are what the server says of it.
    /// It follows where the answer leads.
    /// </summary>
    /// <param name="form">The name of the form, which the form carries in its <c>_handler</c> field.</param>
    /// <param name="fields">What is entered.</param>
    Task SubmitAsync(string form, params (string Name, string Value)[] fields);

    /// <summary>The markup of the page that is open.</summary>
    Task<string> MarkupAsync();

    /// <summary>How many elements of the page that is open a selector matches.</summary>
    Task<int> CountAsync(string selector);
}

/// <summary>
/// What a visit could not do, as a sentence: the page that is open has no such form. A state
/// that is not reached for it says so, and the states after it are still gone through.
/// </summary>
internal sealed class VisitFailedException(string message) : Exception(message);

/// <summary>
/// A visit that reads: a client over HTTP with the cookies of one browser, which runs no script
/// and paints nothing. It posts a form as a browser would post it.
/// </summary>
internal sealed class ReadVisit(HttpClient client) : IVisit
{
    /// <summary>The name of the field that a form carries its name in, as the framework writes it.</summary>
    private const string Handler = "_handler";

    private string _markup = string.Empty;
    private IDocument? _page;

    public string Address { get; private set; } = "/";

    public async Task OpenAsync(string address)
    {
        using var answer = await client.GetAsync(address, TestContext.Current.CancellationToken);

        await ReadAsync(answer);
    }

    public async Task SubmitAsync(string form, params (string Name, string Value)[] fields)
    {
        var page = _page ?? throw new VisitFailedException($"No page is open, so there is no form \"{form}\" to answer.");

        var posted = page.QuerySelectorAll("form").FirstOrDefault(candidate =>
            candidate.QuerySelector($"input[name='{Handler}']")?.GetAttribute("value") == form)
            ?? throw new VisitFailedException($"The page at {Address} has no form called \"{form}\".");

        var buttons = posted.QuerySelectorAll("button, input[type='submit']").Where(IsSubmit).ToList();

        // The button that a field names, or the one that does what the form is for.
        var pressed = buttons.FirstOrDefault(button => fields.Any(field =>
                button.GetAttribute("name") == field.Name && (button.GetAttribute("value") ?? string.Empty) == field.Value))
            ?? buttons.FirstOrDefault(button => !button.HasAttribute("name"))
            ?? buttons.FirstOrDefault();

        var brought = Held(posted);

        foreach (var (name, value) in fields)
        {
            if (pressed is not null && pressed.GetAttribute("name") == name && (pressed.GetAttribute("value") ?? string.Empty) == value)
            {
                continue;
            }

            // In the place of what the form holds under the name, or after everything else.
            var at = brought.FindIndex(held => held.Key == name);
            brought.RemoveAll(held => held.Key == name);
            brought.Insert(at < 0 ? brought.Count : at, new(name, value));
        }

        if (pressed?.GetAttribute("name") is { Length: > 0 } pressedName)
        {
            brought.Add(new(pressedName, pressed.GetAttribute("value") ?? string.Empty));
        }

        using var content = new FormUrlEncodedContent(brought);
        using var answer = await client.PostAsync(
            Target(pressed?.GetAttribute("formaction") ?? posted.GetAttribute("action")), content, TestContext.Current.CancellationToken);

        await ReadAsync(answer);
    }

    public Task<string> MarkupAsync() => Task.FromResult(_markup);

    public Task<int> CountAsync(string selector) => Task.FromResult(_page?.QuerySelectorAll(selector).Length ?? 0);

    private async Task ReadAsync(HttpResponseMessage answer)
    {
        _markup = await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        _page = AccessibleMarkup.Read(_markup);

        // Where the browser ended up, after whatever the answer led to.
        Address = answer.RequestMessage!.RequestUri!.PathAndQuery;
    }

    /// <summary>Where a form is posted to: the address that it names, or the one of the page that it is on.</summary>
    private string Target(string? action) =>
        string.IsNullOrEmpty(action) ? Address
        : action.StartsWith('/') ? action
        : $"/{action}";

    /// <summary>Whether a button sends its form when it is pressed, which a button does unless it says otherwise.</summary>
    private static bool IsSubmit(IElement button) =>
        !button.HasAttribute("disabled")
        && (button.GetAttribute("type") ?? "submit").Equals("submit", StringComparison.OrdinalIgnoreCase);

    /// <summary>What a form holds, as a browser sends it: each control that has a name and a value to send, in the order of the page.</summary>
    private static List<KeyValuePair<string, string>> Held(IElement form)
    {
        var held = new List<KeyValuePair<string, string>>();

        foreach (var control in form.QuerySelectorAll("input, select, textarea"))
        {
            if (control.GetAttribute("name") is not { Length: > 0 } name || control.HasAttribute("disabled"))
            {
                continue;
            }

            switch (control.LocalName)
            {
                case "select":
                    var options = control.QuerySelectorAll("option");

                    // The option that is chosen, or the first, which a browser shows as chosen.
                    if ((options.FirstOrDefault(option => option.HasAttribute("selected")) ?? options.FirstOrDefault()) is { } chosen)
                    {
                        held.Add(new(name, chosen.GetAttribute("value") ?? chosen.TextContent));
                    }

                    break;

                case "textarea":
                    held.Add(new(name, control.TextContent));
                    break;

                default:
                    switch ((control.GetAttribute("type") ?? "text").ToLowerInvariant())
                    {
                        // A button brings its name only when it is the one that is pressed.
                        case "submit" or "button" or "reset" or "image" or "file":
                            break;

                        case "checkbox" or "radio":
                            if (control.HasAttribute("checked"))
                            {
                                held.Add(new(name, control.GetAttribute("value") ?? "on"));
                            }

                            break;

                        default:
                            held.Add(new(name, control.GetAttribute("value") ?? string.Empty));
                            break;
                    }

                    break;
            }
        }

        return held;
    }
}

/// <summary>What leads a visit to a state of a screen.</summary>
internal static class ScreenStates
{
    /// <summary>
    /// Leads a visit to a state, by the steps that the list says: it opens the page, or opens an
    /// address and answers a form there. The visit is of the person that the state is of.
    /// </summary>
    /// <returns>Null when the visit is at the state, or the sentence that says why it is not.</returns>
    public static async Task<string?> ReachAsync(ScreenState state, AccessibilityWorld world, IVisit visit)
    {
        var path = world.PathOf(state);
        var listed = $"The state \"{state.State}\" of {state.Route}, as {state.As}, is listed at {path}";

        try
        {
            if (state.How is Reach.Post answered)
            {
                await visit.OpenAsync(world.AddressOf(answered.From ?? path));
                await visit.SubmitAsync(answered.Form, await world.EnteredAsync(answered.Fields, visit));
            }
            else
            {
                await visit.OpenAsync(path);
            }
        }
        catch (VisitFailedException failed)
        {
            return $"{listed}, and was not reached. {failed.Message}";
        }

        // A page that is not for its browser sends it elsewhere: to the sign-in, or to the page
        // that asks who it is.
        if (visit.Address != path)
        {
            return $"{listed}, and the browser ended up at {visit.Address}.";
        }

        if (await visit.CountAsync(state.Shows) == 0)
        {
            return $"{listed}, and the page there has nothing that matches {state.Shows}, which is what the state shows.";
        }

        return null;
    }
}
