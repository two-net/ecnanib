using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Ecnanib.Tests.Support;

/// <summary>One structural accessibility defect in a rendered page; <see cref="ToString"/> is one line, so a list of them reads as a report.</summary>
internal sealed record AccessibilityViolation(string Rule, string Criterion, string Where, string Excerpt)
{
    public override string ToString() => $"{Rule} ({Criterion}) at {Where}: {Excerpt}";
}

/// <summary>
/// The structural part of WCAG 2.1 AA over a server-rendered page (NFR-USE-01): the document's language and title, the
/// landmarks and the skip link, the heading outline, unique ids, a name for every control, button, link, table, header
/// cell, image, inline SVG and dialog, resolving ARIA references, valid roles and values, live regions that do not nest,
/// nothing focusable behind aria-hidden, no meta refresh. Colour contrast, reflow and motion are stylesheet matters and
/// live in <c>DesignContractTests</c>. Rule ids are axe-core's where one exists.
/// </summary>
internal static class AccessibilityChecks
{
    private static readonly HashSet<string> Roles = new(StringComparer.Ordinal)
    {
        "alert", "alertdialog", "application", "article", "banner", "blockquote", "button", "caption", "cell", "checkbox", "code", "columnheader",
        "combobox", "comment", "complementary", "contentinfo", "definition", "deletion", "dialog", "directory", "document", "emphasis", "feed",
        "figure", "form", "generic", "grid", "gridcell", "group", "heading", "img", "insertion", "link", "list", "listbox", "listitem", "log",
        "main", "mark", "marquee", "math", "menu", "menubar", "menuitem", "menuitemcheckbox", "menuitemradio", "meter", "navigation", "none",
        "note", "option", "paragraph", "presentation", "progressbar", "radio", "radiogroup", "region", "row", "rowgroup", "rowheader",
        "scrollbar", "search", "searchbox", "separator", "slider", "spinbutton", "status", "strong", "subscript", "suggestion", "superscript",
        "switch", "tab", "table", "tablist", "tabpanel", "term", "textbox", "time", "timer", "toolbar", "tooltip", "tree", "treegrid", "treeitem",
    };

    private static readonly HashSet<string> LiveRoles = new(StringComparer.Ordinal) { "status", "alert", "log", "marquee", "timer" };

    private static readonly HashSet<string> NamelessInputTypes = new(StringComparer.OrdinalIgnoreCase) { "hidden", "submit", "button", "reset", "image" };

    private static readonly HashSet<string> GenericRoles = new(StringComparer.Ordinal) { "presentation", "none", "generic" };

    private const string LabelableSelector = "input:not([type=hidden]), select, textarea, button, meter, output, progress";

    private static readonly Regex Language = new("^[a-z]{2,3}(-[A-Za-z0-9]+)*$", RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static IHtmlDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    public static IReadOnlyList<AccessibilityViolation> Check(string html) => Check(Parse(html));

    public static IReadOnlyList<AccessibilityViolation> Check(IHtmlDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var violations = new List<AccessibilityViolation>();
        void Add(string rule, string criterion, IElement? element, string? detail = null) =>
            violations.Add(new AccessibilityViolation(rule, criterion, element is null ? "document" : Where(element), detail ?? Excerpt(element)));

        var root = document.DocumentElement;
        if (root.GetAttribute("lang") is not { } lang || !Language.IsMatch(lang))
            Add("html-lang", "3.1.1", null, $"<html lang=\"{root.GetAttribute("lang")}\">");
        if (string.IsNullOrWhiteSpace(document.Title))
            Add("document-title", "2.4.2", null, "no <title>");
        foreach (var meta in document.QuerySelectorAll("meta[name=viewport]"))
        {
            var content = meta.GetAttribute("content") ?? "";
            if (Regex.IsMatch(content, @"user-scalable\s*=\s*(no|0)", RegexOptions.IgnoreCase) || Regex.IsMatch(content, @"maximum-scale\s*=\s*(0|1)(\.\d+)?\b", RegexOptions.IgnoreCase))
                Add("meta-viewport", "1.4.4", meta);
        }

        foreach (var meta in document.QuerySelectorAll("meta[http-equiv]"))
        {
            if (string.Equals(meta.GetAttribute("http-equiv"), "refresh", StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(meta.GetAttribute("content") ?? "", @"^\s*0\s*(;|$)"))
                Add("meta-refresh", "2.2.1", meta);
        }

        var mains = document.QuerySelectorAll("main, [role=main]");
        if (mains.Length != 1)
            Add("landmark-main-once", "1.3.1", mains.FirstOrDefault(), $"{mains.Length} main landmarks");
        foreach (var anchor in document.QuerySelectorAll("a[href^='#']").Where(a => a.GetAttribute("href")!.Length > 1))
        {
            // The skip link and every other in-page link lead somewhere.
            if (document.GetElementById(anchor.GetAttribute("href")![1..]) is null)
                Add("skip-link-target", "2.4.1", anchor);
        }

        var headings = document.QuerySelectorAll("h1, h2, h3, h4, h5, h6").Where(h => !Hidden(h)).ToList();
        var h1 = headings.Count(h => h.LocalName == "h1");
        if (h1 != 1)
            Add("one-h1", "1.3.1", headings.FirstOrDefault(h => h.LocalName == "h1"), $"{h1} h1 elements");
        var previous = 0;
        foreach (var heading in headings)
        {
            var level = heading.LocalName[1] - '0';
            if (previous == 0 ? level != 1 : level > previous + 1)
                Add("heading-order", "1.3.1", heading, $"h{level} after h{previous}: {Excerpt(heading)}");
            previous = level;
        }

        foreach (var group in document.QuerySelectorAll("[id]").GroupBy(e => e.Id!, StringComparer.Ordinal).Where(g => g.Count() > 1))
            Add("duplicate-id", "4.1.1", group.First(), $"id \"{group.Key}\" used {group.Count()} times");

        foreach (var control in document.QuerySelectorAll("input, select, textarea"))
        {
            if (control is IHtmlInputElement input && NamelessInputTypes.Contains(input.Type))
                continue;
            if (AccessibleName(control) is null)
                Add("control-name", "1.3.1, 4.1.2", control);
        }

        foreach (var button in document.QuerySelectorAll("button, [role=button], summary, input[type=submit], input[type=button], input[type=reset], input[type=image]"))
        {
            if (AccessibleName(button) is null)
                Add("button-name", "4.1.2", button);
        }

        foreach (var link in document.QuerySelectorAll("a[href], [role=link]"))
        {
            if (AccessibleName(link) is null)
                Add("link-name", "2.4.4, 4.1.2", link);
        }

        foreach (var element in document.QuerySelectorAll("[aria-labelledby], [aria-describedby], [aria-controls], [aria-owns], [aria-errormessage], label[for], td[headers], th[headers]"))
        {
            foreach (var attribute in new[] { "aria-labelledby", "aria-describedby", "aria-controls", "aria-owns", "aria-errormessage", "for", "headers" })
            {
                if (element.GetAttribute(attribute) is not { } value || (attribute is "for" && element.LocalName != "label") || (attribute is "headers" && element.LocalName is not ("td" or "th")))
                    continue;
                foreach (var id in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var target = document.GetElementById(id);
                    if (target is null)
                        Add("aria-refs", "1.3.1, 4.1.2", element, $"{attribute} names a missing id \"{id}\": {Excerpt(element)}");
                    else if (attribute == "aria-labelledby" && TextOf(target).Length == 0 && target.GetAttribute("aria-label") is null)
                        Add("aria-refs", "4.1.2", element, $"aria-labelledby names an empty element \"{id}\": {Excerpt(element)}");
                }
            }
        }

        foreach (var element in document.QuerySelectorAll("div[aria-label], div[aria-labelledby], span[aria-label], span[aria-labelledby]"))
        {
            if (element.GetAttribute("role") is not { } role || GenericRoles.Contains(role))
                Add("aria-label-on-generic", "4.1.2", element);
        }

        foreach (var element in document.QuerySelectorAll("[role]"))
        {
            foreach (var role in element.GetAttribute("role")!.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Roles.Contains(role))
                    Add("role-valid", "4.1.2", element, $"role \"{role}\": {Excerpt(element)}");
            }
        }

        foreach (var (attribute, allowed) in new[] { ("aria-hidden", new[] { "true", "false" }), ("aria-live", new[] { "off", "polite", "assertive" }), ("aria-current", new[] { "page", "step", "location", "date", "time", "true", "false" }) })
        {
            foreach (var element in document.QuerySelectorAll($"[{attribute}]"))
            {
                if (!allowed.Contains(element.GetAttribute(attribute), StringComparer.Ordinal))
                    Add("aria-attr-values", "4.1.2", element, $"{attribute}=\"{element.GetAttribute(attribute)}\": {Excerpt(element)}");
            }
        }

        foreach (var region in document.QuerySelectorAll("[aria-live], [role]").Where(IsLiveRegion))
        {
            for (var ancestor = region.ParentElement; ancestor is not null; ancestor = ancestor.ParentElement)
            {
                if (IsLiveRegion(ancestor))
                {
                    Add("aria-live-nested", "4.1.3", region, $"inside {Where(ancestor)}: {Excerpt(region)}");
                    break;
                }
            }
        }

        foreach (var hidden in document.QuerySelectorAll("[aria-hidden=true]"))
        {
            if (hidden.Descendants<IElement>().FirstOrDefault(IsFocusable) is { } focusable)
                Add("aria-hidden-focus", "4.1.2", hidden, $"focusable {Excerpt(focusable)} inside {Excerpt(hidden)}");
        }

        foreach (var image in document.QuerySelectorAll("img"))
        {
            if (!image.HasAttribute("alt"))
                Add("img-alt", "1.1.1", image);
        }

        foreach (var svg in document.QuerySelectorAll("svg"))
        {
            var named = svg.GetAttribute("role") == "img" && (svg.Children.Any(c => c.LocalName == "title" && TextOf(c).Length > 0) || !string.IsNullOrWhiteSpace(svg.GetAttribute("aria-label")) || svg.HasAttribute("aria-labelledby"));
            if (svg.GetAttribute("aria-hidden") != "true" && !named)
                Add("svg-name", "1.1.1", svg);
        }

        foreach (var table in document.QuerySelectorAll("table"))
        {
            if (GenericRoles.Contains(table.GetAttribute("role") ?? ""))
                continue;
            var caption = table.Children.FirstOrDefault(c => c.LocalName == "caption");
            if ((caption is null || TextOf(caption).Length == 0) && string.IsNullOrWhiteSpace(table.GetAttribute("aria-label")) && !table.HasAttribute("aria-labelledby"))
                Add("table-name", "1.3.1", table);
        }

        foreach (var header in document.QuerySelectorAll("th"))
        {
            if (TextOf(header).Length == 0 && string.IsNullOrWhiteSpace(header.GetAttribute("aria-label")) && !GenericRoles.Contains(header.GetAttribute("role") ?? ""))
                Add("th-has-text", "1.3.1", header, $"empty header cell in {Where(header.Closest("table") ?? header)}");
        }

        foreach (var fieldset in document.QuerySelectorAll("fieldset"))
        {
            if (!fieldset.Children.Any(c => c.LocalName == "legend" && TextOf(c).Length > 0))
                Add("fieldset-legend", "1.3.1", fieldset);
        }

        foreach (var dialog in document.QuerySelectorAll("dialog, [role=dialog], [role=alertdialog]"))
        {
            if (string.IsNullOrWhiteSpace(dialog.GetAttribute("aria-label")) && (dialog.GetAttribute("aria-labelledby") is not { } ids || ids.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(id => document.GetElementById(id) is not { } t || TextOf(t).Length == 0)))
                Add("dialog-name", "4.1.2", dialog);
        }

        foreach (var frame in document.QuerySelectorAll("iframe"))
        {
            if (string.IsNullOrWhiteSpace(frame.GetAttribute("title")))
                Add("iframe-title", "4.1.2", frame);
        }

        foreach (var element in document.QuerySelectorAll("[tabindex]"))
        {
            if (int.TryParse(element.GetAttribute("tabindex"), out var index) && index > 0)
                Add("no-positive-tabindex", "2.4.3", element);
        }

        if (document.GetElementById("blazor-error-ui") is { } bar)
        {
            if (bar.GetAttribute("role") != "alert")
                Add("blazor-error-ui-dismiss", "4.1.3", bar, "the error bar is not role=\"alert\"");
            foreach (var dismiss in bar.QuerySelectorAll(".dismiss"))
            {
                if (dismiss.LocalName != "button" || AccessibleName(dismiss) is null)
                    Add("blazor-error-ui-dismiss", "2.1.1, 4.1.2", dismiss);
            }

            foreach (var reload in bar.QuerySelectorAll(".reload"))
            {
                if (!(reload.LocalName == "button" || (reload.LocalName == "a" && reload.HasAttribute("href"))) || AccessibleName(reload) is null)
                    Add("blazor-error-ui-dismiss", "2.1.1, 4.1.2", reload);
            }
        }

        return violations;
    }

    /// <summary>
    /// The accessible name as a browser computes it, in order: aria-labelledby, aria-label, the native name (a label's text
    /// for a control, the content for a button or link, value or alt for input buttons), then title. A placeholder never counts.
    /// </summary>
    public static string? AccessibleName(IElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        var document = element.Owner!;
        if (element.GetAttribute("aria-labelledby") is { } ids)
        {
            var text = string.Join(' ', ids.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(id => document.GetElementById(id) is { } t ? t.GetAttribute("aria-label") ?? TextOf(t) : "")).Trim();
            if (text.Length > 0)
                return text;
        }

        if (!string.IsNullOrWhiteSpace(element.GetAttribute("aria-label")))
            return element.GetAttribute("aria-label")!.Trim();

        string? native = element switch
        {
            IHtmlInputElement { Type: "submit" or "button" or "reset" } input => input.GetAttribute("value"),
            IHtmlInputElement { Type: "image" } input => input.GetAttribute("alt"),
            IHtmlInputElement or IHtmlSelectElement or IHtmlTextAreaElement => LabelFor(element) is { } label ? TextOf(label) : null,
            _ => TextOf(element),
        };
        if (!string.IsNullOrWhiteSpace(native))
            return native.Trim();
        return string.IsNullOrWhiteSpace(element.GetAttribute("title")) ? null : element.GetAttribute("title")!.Trim();
    }

    /// <summary>The label of a control: <c>label[for]</c> naming its id, or the nearest ancestor label whose first labelable descendant it is.</summary>
    private static IElement? LabelFor(IElement control)
    {
        var document = control.Owner!;
        if (!string.IsNullOrEmpty(control.Id) && document.QuerySelectorAll("label[for]").FirstOrDefault(l => l.GetAttribute("for") == control.Id) is { } explicitLabel)
            return explicitLabel;
        if (control.Closest("label") is not { } label)
            return null;
        if (label.HasAttribute("for"))
            return label.GetAttribute("for") == control.Id ? label : null;
        return ReferenceEquals(label.QuerySelector(LabelableSelector), control) ? label : null;
    }

    /// <summary>The text assistive technology reads: descendants' text with aria-hidden parts left out, images by their alt, SVGs by their title.</summary>
    private static string TextOf(INode node)
    {
        var text = new System.Text.StringBuilder();
        void Walk(INode current)
        {
            foreach (var child in current.ChildNodes)
            {
                switch (child)
                {
                    case IText t:
                        text.Append(t.Data);
                        break;
                    case IElement e when e.GetAttribute("aria-hidden") == "true":
                        break;
                    case IElement { LocalName: "img" } e:
                        text.Append(e.GetAttribute("alt"));
                        break;
                    case IElement { LocalName: "svg" } e:
                        text.Append(e.Children.FirstOrDefault(c => c.LocalName == "title")?.TextContent);
                        break;
                    case IElement e:
                        if (!string.IsNullOrWhiteSpace(e.GetAttribute("aria-label")))
                            text.Append(e.GetAttribute("aria-label"));
                        else
                            Walk(e);
                        break;
                }
            }
        }

        Walk(node);
        return Whitespace.Replace(text.ToString(), " ").Trim();
    }

    private static bool Hidden(IElement element) => element.Closest("[aria-hidden=true]") is not null;

    private static bool IsLiveRegion(IElement element) =>
        (element.GetAttribute("aria-live") is { } live && live != "off") || (element.GetAttribute("role") is { } role && LiveRoles.Contains(role));

    /// <summary>Reachable by the keyboard: natively focusable or carrying a tabindex, unless disabled or taken out of the tab order.</summary>
    private static bool IsFocusable(IElement element)
    {
        if (element.HasAttribute("disabled") || element.GetAttribute("tabindex") == "-1")
            return false;
        if (element.HasAttribute("tabindex") || element.HasAttribute("contenteditable"))
            return true;
        return element.LocalName switch
        {
            "a" or "area" => element.HasAttribute("href"),
            "input" => !string.Equals(element.GetAttribute("type"), "hidden", StringComparison.OrdinalIgnoreCase),
            "button" or "select" or "textarea" or "summary" or "iframe" => true,
            _ => false,
        };
    }

    /// <summary>A short CSS-like path: the element and up to four ancestors, each by id or by tag and position.</summary>
    private static string Where(IElement element)
    {
        var parts = new List<string>();
        for (var current = element; current is not null && parts.Count < 5 && current.LocalName != "html"; current = current.ParentElement)
        {
            if (!string.IsNullOrEmpty(current.Id))
            {
                parts.Add($"{current.LocalName}#{current.Id}");
                break;
            }

            var siblings = current.ParentElement?.Children.Where(c => c.LocalName == current.LocalName).ToList();
            parts.Add(siblings is { Count: > 1 } ? $"{current.LocalName}:nth-of-type({siblings.IndexOf(current) + 1})" : current.LocalName);
        }

        parts.Reverse();
        return string.Join(" > ", parts);
    }

    private static string Excerpt(IElement? element)
    {
        if (element is null)
            return "";
        var html = Whitespace.Replace(element.OuterHtml, " ").Trim();
        return html.Length <= 160 ? html : html[..160] + "…";
    }
}
