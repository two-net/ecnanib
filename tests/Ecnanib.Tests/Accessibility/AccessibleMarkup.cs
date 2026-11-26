using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Ecnanib.Tests.Accessibility;

/// <summary>
/// What holds the markup of a page to the rules of <see cref="AccessibilityRules.Markup"/>: one
/// check for each rule, over the page as a browser reads it into elements. It runs no script
/// and paints nothing, so it says nothing of colours, of sizes, or of where the focus goes: a
/// browser that a test drives says those.
/// </summary>
/// <remarks>
/// The name of an element is what a screen reader calls it, by a part of the rules of browsers
/// that these pages keep to: what <c>aria-labelledby</c> names, then <c>aria-label</c>, then
/// the label of a field, then what the element says itself, without what is hidden from a
/// screen reader, and the <c>alt</c> of a picture. A <c>title</c> and a <c>placeholder</c>
/// never count: neither is shown to everyone.
/// </remarks>
internal static partial class AccessibleMarkup
{
    // The roles of what a user works: an element that has one is a control to a screen reader.
    private static readonly IReadOnlySet<string> ControlRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "button", "checkbox", "combobox", "gridcell", "link", "listbox", "menu", "menubar", "menuitem", "menuitemcheckbox",
        "menuitemradio", "option", "radio", "scrollbar", "searchbox", "slider", "spinbutton", "switch", "tab", "tablist",
        "textbox", "tree", "treegrid", "treeitem",
    };

    // The elements that stand in a line of text: what they say is part of the words around them.
    private static readonly IReadOnlySet<string> InLine = new HashSet<string>(StringComparer.Ordinal)
    {
        "a", "abbr", "b", "code", "data", "em", "i", "kbd", "mark", "small", "span", "strong", "sub", "sup", "time", "u",
    };

    private static readonly string[] Counted = ["two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"];

    private static readonly IReadOnlyDictionary<string, Func<IDocument, IEnumerable<string>>> Checks =
        new Dictionary<string, Func<IDocument, IEnumerable<string>>>
        {
            ["language"] = Language,
            ["title"] = Title,
            ["one heading"] = OneHeading,
            ["heading order"] = HeadingOrder,
            ["ids"] = Ids,
            ["references"] = References,
            ["field names"] = FieldNames,
            ["groups"] = Groups,
            ["link and button names"] = LinkAndButtonNames,
            ["label in name"] = LabelInName,
            ["native controls"] = NativeControls,
            ["tables"] = Tables,
            ["regions and landmarks"] = RegionsAndLandmarks,
            ["pictures"] = Pictures,
            ["wrong fields"] = WrongFields,
            ["focus on load"] = FocusOnLoad,
            ["messages"] = Messages,
            ["new tabs"] = NewTabs,
            ["no title attribute"] = NoTitleAttribute,
            ["input purpose"] = InputPurpose,
            ["dialogs"] = Dialogs,
            ["details"] = Details,
            ["hidden parts"] = HiddenParts,
            ["viewport"] = Viewport,
        };

    /// <summary>The rules that there is a check for, by what each is called.</summary>
    public static IReadOnlyCollection<string> Checked => [.. Checks.Keys];

    /// <summary>A page as a browser reads its markup: its elements.</summary>
    public static IDocument Read(string markup) => new HtmlParser().ParseDocument(markup);

    /// <summary>
    /// What on a page breaks a rule, each as the end of a sentence that starts with the page:
    /// what is wrong, and what to do about it.
    /// </summary>
    public static IReadOnlyList<string> FindingsOn(IDocument page, MarkupRule rule) =>
        Checks.TryGetValue(rule.Id, out var check)
            ? [.. check(page).Distinct(StringComparer.Ordinal)]
            : throw new ArgumentException($"There is no check for the rule \"{rule.Id}\". Write one in AccessibleMarkup.", nameof(rule));

    /// <summary>What on a page breaks a rule, each as a sentence about the page at a path.</summary>
    public static IReadOnlyList<string> FindingsOn(string path, IDocument page, MarkupRule rule) =>
        [.. FindingsOn(page, rule).Select(finding => $"The page at {path} {finding}")];

    /// <summary>Fails for a page that breaks a rule, with everything that it breaks.</summary>
    /// <param name="path">The path of the page, which what it fails with names.</param>
    /// <param name="page">The page.</param>
    public static void AssertIsAccessible(string path, IDocument page)
    {
        var findings = AccessibilityRules.Markup.SelectMany(rule => FindingsOn(path, page, rule)).ToList();

        Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings));
    }

    private static IEnumerable<string> Language(IDocument page)
    {
        if (string.IsNullOrWhiteSpace(page.DocumentElement?.GetAttribute("lang")))
        {
            yield return "does not say which language it is in. Give its first element lang.";
        }
    }

    private static IEnumerable<string> Title(IDocument page)
    {
        var titles = page.QuerySelectorAll("head > title");

        if (titles.All(title => Said(title).Length == 0))
        {
            yield return "has no title. Give it a PageTitle that says what it is.";
        }
        else if (titles.Length > 1)
        {
            yield return $"has {titles.Length} titles. A page has one.";
        }
    }

    private static IEnumerable<string> OneHeading(IDocument page)
    {
        var first = page.QuerySelectorAll("h1");

        if (first.Length != 1)
        {
            yield return $"has {first.Length} first headings. A page has one h1.";
        }
        else if (first[0].Closest("main") is null)
        {
            yield return "has its h1 outside main. The first heading of a page is the heading of its content.";
        }
    }

    private static IEnumerable<string> HeadingOrder(IDocument page)
    {
        var level = 0;

        foreach (var heading in page.QuerySelectorAll("h1, h2, h3, h4, h5, h6"))
        {
            var next = heading.LocalName[1] - '0';

            if (next > level + 1)
            {
                yield return level == 0
                    ? $"starts with \"{Short(Said(heading))}\" of level {next}. The first heading of a page is its h1."
                    : $"goes from level {level} to \"{Short(Said(heading))}\" of level {next}. Give it level {level + 1}.";
            }

            level = next;
        }
    }

    private static IEnumerable<string> Ids(IDocument page) =>
        page.QuerySelectorAll("[id]")
            .GroupBy(element => element.Id, StringComparer.Ordinal)
            .Where(sharing => sharing.Count() > 1)
            .Select(sharing => $"has the id \"{sharing.Key}\" {sharing.Count()} times.");

    private static IEnumerable<string> References(IDocument page)
    {
        var ids = page.QuerySelectorAll("[id]").Select(element => element.Id!).ToHashSet(StringComparer.Ordinal);

        (string Attribute, string Carriers)[] naming =
        [
            ("for", "label[for]"),
            ("aria-labelledby", "[aria-labelledby]"),
            ("aria-describedby", "[aria-describedby]"),
            ("aria-controls", "[aria-controls]"),
            ("form", "button[form], input[form]"),
        ];

        foreach (var (attribute, carriers) in naming)
        {
            foreach (var id in page.QuerySelectorAll(carriers).SelectMany(element => Words(element.GetAttribute(attribute))))
            {
                if (!ids.Contains(id))
                {
                    yield return $"names \"{id}\" in {attribute}, and no element has that id.";
                }
            }
        }
    }

    private static IEnumerable<string> FieldNames(IDocument page) =>
        Fields(page)
            .Where(field => NameOf(field).Length == 0)
            .Select(field => $"has the field {Called(field)} without a label.");

    private static IEnumerable<string> Groups(IDocument page)
    {
        var choices = page.QuerySelectorAll("input").Where(input => TypeOf(input) is "radio" or "checkbox").ToList();

        // A radio button is always one of a group. A checkbox is one where another has its name.
        var boxes = choices
            .Where(choice => TypeOf(choice) == "checkbox")
            .GroupBy(choice => (Form: choice.Closest("form"), Name: choice.GetAttribute("name")))
            .Where(sharing => sharing.Key.Name is not null && sharing.Count() > 1)
            .SelectMany(sharing => sharing);

        return choices
            .Where(choice => TypeOf(choice) == "radio")
            .Concat(boxes)
            .Where(choice => choice.Closest("fieldset") is not { } group
                || group.Children.FirstOrDefault(child => child.LocalName == "legend") is not { } legend
                || Said(legend).Length == 0)
            .Select(choice => $"has the choice {Called(choice)} outside a fieldset with a legend.");
    }

    private static IEnumerable<string> LinkAndButtonNames(IDocument page) =>
        page.QuerySelectorAll("a[href], button, summary, input")
            .Where(element => element.LocalName != "input" || TypeOf(element) is "submit" or "button" or "reset")
            .Where(element => NameOf(element).Length == 0)
            .Select(element => $"has {Tag(element)}, which says nothing.");

    private static IEnumerable<string> LabelInName(IDocument page)
    {
        foreach (var element in page.QuerySelectorAll("[aria-label]"))
        {
            // What is shown of a control: what it says itself, or the label of a field.
            var shown = IsField(element) ? LabelOf(element)
                : IsControl(element) || HasControlRole(element) ? Said(element)
                : string.Empty;

            var label = Spaced(element.GetAttribute("aria-label"));

            if (shown.Length > 0 && !label.Contains(shown, StringComparison.OrdinalIgnoreCase))
            {
                yield return $"calls {Tag(element)} \"{label}\" and shows \"{Short(shown)}\".";
            }
        }
    }

    private static IEnumerable<string> NativeControls(IDocument page)
    {
        foreach (var element in page.QuerySelectorAll("[role]").Where(element => HasControlRole(element) && !IsControl(element)))
        {
            yield return $"gives {Tag(element)} the role of a control, and it is none. Use a button, a link, or a field of the browser.";
        }

        foreach (var link in page.QuerySelectorAll("a").Where(link => !link.HasAttribute("href")))
        {
            yield return $"has {Tag(link)}, a link that leads nowhere. Give it an address, or make it a button.";
        }

        foreach (var element in page.QuerySelectorAll("[tabindex]"))
        {
            if (!int.TryParse(element.GetAttribute("tabindex"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                continue;
            }

            if (index > 0)
            {
                yield return $"gives {Tag(element)} a place of its own in the order of the keyboard. The order of the keyboard is the order of the page.";
            }
            else if (index == 0 && !IsControl(element) && !(RoleOf(element) == "region" && NameOf(element).Length > 0))
            {
                yield return $"makes {Tag(element)} a stop of the keyboard, and it is neither a control nor a named region that scrolls.";
            }
            else if (index < 0 && !TakesTheFocus(element))
            {
                yield return $"lets the focus be put on {Tag(element)}, and it is neither main, a heading, nor a message of a form.";
            }
        }
    }

    private static IEnumerable<string> Tables(IDocument page)
    {
        foreach (var table in page.QuerySelectorAll("table"))
        {
            var name = NameOf(table);

            if (name.Length == 0)
            {
                yield return "has a table without a name. Give it a caption, or name it with aria-labelledby.";
            }

            var called = name.Length == 0 ? "a table without a name" : $"the table \"{Short(name)}\"";
            var rows = table.QuerySelectorAll("tr").Where(row => row.Closest("table") == table).ToList();

            if (rows.SelectMany(row => row.Children).FirstOrDefault(cell => cell.LocalName == "th" && !cell.HasAttribute("scope")) is { } heading)
            {
                yield return $"has {called} with the heading cell \"{Short(Said(heading))}\", which does not say what it heads. Give it scope.";
            }

            // A row of the body, but for one that is a single cell across the table, which says that there is nothing.
            var unnamed = rows
                .Where(row => row.ParentElement?.LocalName is not ("thead" or "tfoot"))
                .Where(row => !(row.Children.Length == 1 && row.Children[0].LocalName == "td" && row.Children[0].HasAttribute("colspan")))
                .Any(row => !row.Children.Any(cell => cell.LocalName == "th" && cell.GetAttribute("scope") == "row"));

            if (unnamed)
            {
                yield return $"has {called} with a row that no cell names. Make the cell that says what a row is of a th with scope=\"row\".";
            }
        }
    }

    private static IEnumerable<string> RegionsAndLandmarks(IDocument page)
    {
        var top = page.Body?.Children.ToList() ?? [];

        (string Parts, int Count)[] parts =
        [
            ("main elements", page.QuerySelectorAll("main").Length),
            ("header elements at its top level", top.Count(element => element.LocalName == "header")),
            ("footer elements at its top level", top.Count(element => element.LocalName == "footer")),
        ];

        foreach (var (what, count) in parts.Where(part => part.Count != 1))
        {
            yield return $"has {count} {what}. A page has one.";
        }

        var landmarks = page.QuerySelectorAll("nav, [role]")
            .Select(element => (Element: element, Kind: element.LocalName == "nav" ? "navigation" : RoleOf(element)))
            .Where(landmark => landmark.Kind is "navigation" or "region")
            .Select(landmark => (landmark.Element, landmark.Kind, Name: NameOf(landmark.Element)))
            .ToList();

        foreach (var (element, kind, _) in landmarks.Where(landmark => landmark.Name.Length == 0))
        {
            yield return $"has a {kind} without a name: {Tag(element)}. Name it with aria-label or aria-labelledby.";
        }

        foreach (var sharing in landmarks
            .Where(landmark => landmark.Name.Length > 0)
            .GroupBy(landmark => (landmark.Kind, landmark.Name))
            .Where(sharing => sharing.Count() > 1))
        {
            yield return $"has {Number(sharing.Count())} {sharing.Key.Kind}s called \"{Short(sharing.Key.Name)}\".";
        }
    }

    private static IEnumerable<string> Pictures(IDocument page)
    {
        // A drawing, but for one that is part of another.
        foreach (var drawing in page.QuerySelectorAll("svg").Where(drawing => drawing.ParentElement?.Closest("svg") is null))
        {
            if (!IsHidden(drawing) && !(RoleOf(drawing) == "img" && NameOf(drawing).Length > 0))
            {
                yield return $"has a picture that says nothing and is not hidden: {Tag(drawing)}. Give it role=\"img\" and a name, or aria-hidden=\"true\".";
            }
        }

        foreach (var image in page.QuerySelectorAll("img").Where(image => !image.HasAttribute("alt") && !IsHidden(image)))
        {
            yield return $"has a picture that says nothing and is not hidden: {Tag(image)}. Give it alt.";
        }
    }

    private static IEnumerable<string> WrongFields(IDocument page)
    {
        foreach (var field in page.QuerySelectorAll("[aria-invalid='true']"))
        {
            if (!Named(field, "aria-describedby").Any(message => message.ClassList.Contains("field-error") && Said(message).Length > 0))
            {
                yield return
                    $"marks {Called(field)} as wrong, and nothing it is described by says what is wrong. " +
                    "Give the message an id, and name it in aria-describedby.";
            }
        }

        var describing = page.QuerySelectorAll("input, select, textarea, fieldset")
            .SelectMany(field => Words(field.GetAttribute("aria-describedby")))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var message in page.QuerySelectorAll(".field-error").Where(message => message.Id is not { Length: > 0 } id || !describing.Contains(id)))
        {
            yield return
                $"says \"{Short(Said(message))}\" of a field, and no field is described by it. " +
                "Give the message an id, and name it in the aria-describedby of its field.";
        }
    }

    private static IEnumerable<string> FocusOnLoad(IDocument page)
    {
        var asking = page.QuerySelectorAll("[autofocus]");

        if (asking.Length > 1)
        {
            yield return $"has {asking.Length} elements that ask for the focus. One does.";
        }

        foreach (var inside in asking.Where(element => element.Closest("dialog") is not null))
        {
            yield return
                $"asks for the focus inside a dialog, with {Tag(inside)}. That keeps the page from putting the focus " +
                "where it starts, and a dialog that opens puts it on its first control by itself.";
        }

        if (asking.Length > 0 && page.QuerySelector("main .form-error, main .form-done") is not null)
        {
            yield return
                $"asks for the focus with {Tag(asking[0])}, beside a message of its form. The message takes the focus: " +
                "leave autofocus off where a form says something as a whole.";
        }
    }

    private static IEnumerable<string> Messages(IDocument page)
    {
        foreach (var message in page.QuerySelectorAll(".form-error").Where(message => RoleOf(message) != "alert"))
        {
            yield return $"has the message \"{Short(Said(message))}\" of a form, which is not said as it appears. Give it role=\"alert\".";
        }

        foreach (var message in page.QuerySelectorAll(".form-done").Where(message => RoleOf(message) != "status"))
        {
            yield return $"has the answer \"{Short(Said(message))}\" of a form, which is not said as it appears. Give it role=\"status\".";
        }

        // A warning that answers a form takes the focus, and is an alert. One that stands on the
        // page as it opens is a note: nothing happened that it would be said for.
        foreach (var warning in page.QuerySelectorAll(".form-warning"))
        {
            var takesTheFocus = warning.HasAttribute("autofocus") || warning.HasAttribute("tabindex");

            if (!(RoleOf(warning) == "alert" || (RoleOf(warning) == "note" && !takesTheFocus)))
            {
                yield return
                    $"has the warning \"{Short(Said(warning))}\" of a form, which does not say what it is. " +
                    "Give it role=\"alert\" where it answers the form, and role=\"note\" where it stands on the page as it opens.";
            }
        }
    }

    private static IEnumerable<string> NewTabs(IDocument page) =>
        page.QuerySelectorAll("a[target='_blank']")
            .Where(link => !Said(link).Contains(AccessibilityRules.NewTab, StringComparison.OrdinalIgnoreCase))
            .Select(link => $"opens {Tag(link)} in a new tab and does not say so. Say \"({AccessibilityRules.NewTab})\" in the text of the link.");

    private static IEnumerable<string> NoTitleAttribute(IDocument page) =>
        page.QuerySelectorAll("[title]")
            .Select(element => $"says something in a title, which a keyboard does not show. It is on {Tag(element)}: say it in the text.");

    private static IEnumerable<string> InputPurpose(IDocument page)
    {
        foreach (var field in page.QuerySelectorAll("input"))
        {
            var says = Words(field.GetAttribute("autocomplete")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var name = field.GetAttribute("name") ?? string.Empty;

            var saysIt = TypeOf(field) switch
            {
                "email" => says.Contains("email") || says.Contains("username"),
                "password" => says.Contains("current-password")
                    || says.Contains("new-password")
                    || (says.Contains("off") && AccessibilityRules.NotOfTheUser.ContainsKey(name)),
                _ => name != "Code" || says.Contains("one-time-code"),
            };

            if (!saysIt)
            {
                yield return $"does not say what {Called(field)} is for in autocomplete.";
            }
        }
    }

    private static IEnumerable<string> Dialogs(IDocument page) =>
        page.QuerySelectorAll("dialog")
            .Where(dialog => NameOf(dialog).Length == 0)
            .Select(dialog => $"has a dialog without a name: {Tag(dialog)}. Name it with aria-label or aria-labelledby.");

    private static IEnumerable<string> Details(IDocument page) =>
        page.QuerySelectorAll("details")
            .Where(part => part.FirstElementChild is not { LocalName: "summary" } summary || Said(summary).Length == 0)
            .Select(_ => "has a part that opens and does not say what it is. Give it a summary first, which says it.");

    private static IEnumerable<string> HiddenParts(IDocument page) =>
        page.QuerySelectorAll("[aria-hidden='true']")
            .SelectMany(hidden => hidden.QuerySelectorAll("*").Prepend(hidden))
            .Where(IsStop)
            .Select(element => $"hides {Tag(element)} from a screen reader and leaves it a stop of the keyboard.");

    private static IEnumerable<string> Viewport(IDocument page)
    {
        foreach (var viewport in page.QuerySelectorAll("meta[name='viewport']"))
        {
            var says = (viewport.GetAttribute("content") ?? string.Empty)
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
                .Where(part => part.Length == 2)
                .ToDictionary(part => part[0], part => part[1], StringComparer.OrdinalIgnoreCase);

            var fixedSize = says.TryGetValue("user-scalable", out var scalable) && scalable is "no" or "0";
            var capped = says.TryGetValue("maximum-scale", out var most)
                && double.TryParse(most, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale)
                && scale < 2;

            if (fixedSize || capped)
            {
                yield return "keeps its user from enlarging it. Take user-scalable and maximum-scale out of its viewport.";
            }
        }
    }

    /// <summary>The fields of a page: what a user enters something in, or chooses with.</summary>
    private static IEnumerable<IElement> Fields(IDocument page) => page.QuerySelectorAll("input, select, textarea").Where(IsField);

    private static bool IsField(IElement element) =>
        element.LocalName is "select" or "textarea"
        || (element.LocalName == "input" && TypeOf(element) is not ("hidden" or "submit" or "button" or "reset" or "image"));

    /// <summary>Whether an element is a control of the browser: one that the keyboard reaches and works by itself.</summary>
    private static bool IsControl(IElement element) => element.LocalName switch
    {
        "a" => element.HasAttribute("href"),
        "input" => TypeOf(element) != "hidden",
        "button" or "select" or "textarea" or "summary" => true,
        _ => false,
    };

    private static bool HasControlRole(IElement element) => ControlRoles.Contains(RoleOf(element));

    /// <summary>Whether an element is one that a page puts the focus on, and that the keyboard does not stop at.</summary>
    private static bool TakesTheFocus(IElement element) =>
        element.LocalName is "main" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6"
        || element.ClassList.Contains("form-error")
        || element.ClassList.Contains("form-done")
        || element.ClassList.Contains("form-warning");

    /// <summary>Whether the keyboard stops at an element.</summary>
    private static bool IsStop(IElement element)
    {
        var placed = int.TryParse(element.GetAttribute("tabindex"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index);

        return placed ? index >= 0 : IsControl(element) && !element.HasAttribute("disabled");
    }

    /// <summary>Whether an element is hidden from a screen reader, by itself or by what it is in.</summary>
    private static bool IsHidden(IElement element) => element.Closest("[aria-hidden='true']") is not null;

    private static string TypeOf(IElement input) => (input.GetAttribute("type") ?? "text").ToLowerInvariant();

    private static string RoleOf(IElement element) => (element.GetAttribute("role") ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// What a screen reader calls an element: see the remarks of the class. What an element
    /// says itself is its name where it is a link, a button, or a summary. A part of a page,
    /// such as a navigation, a region, or a dialog, has a name only where it is given one.
    /// </summary>
    private static string NameOf(IElement element)
    {
        if (Spaced(string.Join(' ', Named(element, "aria-labelledby").Select(Said))) is { Length: > 0 } labelledBy)
        {
            return labelledBy;
        }

        if (Spaced(element.GetAttribute("aria-label")) is { Length: > 0 } label)
        {
            return label;
        }

        return element.LocalName switch
        {
            "img" => Spaced(element.GetAttribute("alt")),
            "table" => element.Children.FirstOrDefault(child => child.LocalName == "caption") is { } caption ? Said(caption) : string.Empty,
            "fieldset" => element.Children.FirstOrDefault(child => child.LocalName == "legend") is { } legend ? Said(legend) : string.Empty,
            "input" when TypeOf(element) is "submit" or "button" or "reset" => Spaced(element.GetAttribute("value")),
            "a" or "button" or "summary" => Said(element),
            _ when IsField(element) => LabelOf(element),
            _ => string.Empty,
        };
    }

    /// <summary>What the label of a field says: the one that names the field, or the one that the field is in.</summary>
    private static string LabelOf(IElement field)
    {
        var naming = field.Id is { Length: > 0 } id
            ? field.Owner?.QuerySelectorAll("label[for]").FirstOrDefault(label => label.GetAttribute("for") == id)
            : null;

        return (naming ?? field.Closest("label")) is { } found ? Said(found) : string.Empty;
    }

    /// <summary>The elements that an attribute of an element names by their ids, in the order that it names them.</summary>
    private static IEnumerable<IElement> Named(IElement element, string attribute) =>
        Words(element.GetAttribute(attribute))
            .Select(id => element.Owner?.GetElementById(id))
            .OfType<IElement>();

    private static IEnumerable<string> Words(string? value) =>
        (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>What an element says, as a screen reader reads it: its text, without what is hidden from a screen reader.</summary>
    private static string Said(INode node) => Spaced(Says(node));

    private static string Says(INode node)
    {
        if (node is IText text)
        {
            return text.Data;
        }

        if (node is not IElement element || element.GetAttribute("aria-hidden") == "true")
        {
            return string.Empty;
        }

        switch (element.LocalName)
        {
            case "script" or "style" or "template" or "input" or "select" or "textarea":
                return string.Empty;

            case "img":
                return $" {element.GetAttribute("alt")} ";

            case "svg":
                return RoleOf(element) == "img" ? $" {element.GetAttribute("aria-label")} " : string.Empty;
        }

        var said = string.Concat(element.ChildNodes.Select(Says));

        return InLine.Contains(element.LocalName) ? said : $" {said} ";
    }

    /// <summary>How a failure names a field: by the name that its form brings it under, by its id, or as it is written.</summary>
    private static string Called(IElement field) =>
        field.GetAttribute("name") is { Length: > 0 } name ? name
        : field.Id is { Length: > 0 } id ? id
        : Tag(field);

    /// <summary>An element as it is written, without what it holds, and with what is long cut short.</summary>
    private static string Tag(IElement element)
    {
        var attributes = element.Attributes.Select(attribute =>
            attribute.Value.Length == 0 ? $" {attribute.Name}" : $" {attribute.Name}=\"{Short(attribute.Value, 60)}\"");

        return $"<{element.LocalName}{string.Concat(attributes)}>";
    }

    private static string Short(string text, int length = 80) => text.Length <= length ? text : $"{text[..length]}…";

    private static string Number(int count) => count is >= 2 and <= 10 ? Counted[count - 2] : count.ToString(CultureInfo.InvariantCulture);

    private static string Spaced(string? text) => Space().Replace(text ?? string.Empty, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();
}
