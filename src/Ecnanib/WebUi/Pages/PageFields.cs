namespace Ecnanib.WebUi.Pages;

/// <summary>
/// What a field of a form says of itself to a screen reader, in one place, so that every form
/// says it the same way (NFR-USA-1). A field that a form refused is marked as wrong, and names
/// what says why before its hint, so that is read out with the field. A page gives the id
/// <c>{field}-error</c> to what says why, and <c>{field}-hint</c> to the hint. See
/// docs/accessibility.md.
/// </summary>
public static class PageFields
{
    /// <summary>
    /// What a field is described by, for <c>aria-describedby</c>: the ids that are given, in
    /// their order, without those that are null. Nothing when none is left, so the attribute is
    /// left out of the page.
    /// </summary>
    /// <param name="ids">The id of what says why the field is wrong, where it is, then the id of its hint.</param>
    public static string? DescribedBy(params string?[] ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var named = string.Join(' ', ids.Where(id => !string.IsNullOrEmpty(id)));

        return named.Length > 0 ? named : null;
    }

    /// <summary>The id of what says why a field is wrong, while it is: <c>{field}-error</c>. Nothing otherwise.</summary>
    /// <param name="field">The id of the field.</param>
    /// <param name="wrong">Whether the form refused the field.</param>
    public static string? ErrorOf(string field, bool wrong) => wrong ? field + "-error" : null;

    /// <summary>What <c>aria-invalid</c> says of a field: <c>true</c> while it is wrong, and nothing otherwise.</summary>
    /// <param name="wrong">Whether the form refused the field.</param>
    public static string? Invalid(bool wrong) => wrong ? "true" : null;
}
