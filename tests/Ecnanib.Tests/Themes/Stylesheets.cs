using System.Text.RegularExpressions;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Animation;
using Ecnanib.Tests.Logging;

namespace Ecnanib.Tests.Themes;

/// <summary>
/// The stylesheets that a page names, as the application serves them, for the tests that hold
/// them to their rules: <see cref="ThemeStyleTests"/> and <see cref="MotionStyleTests"/>. No
/// test drives a browser, so what is held are the rules as they are written.
/// </summary>
internal static partial class Stylesheets
{
    /// <summary>The stylesheet of the pages, as the application serves it.</summary>
    public static async Task<string> OfThePagesAsync() =>
        (await AllAsync()).Single(sheet => sheet.Styles.Contains(":root", StringComparison.Ordinal)).Styles;

    /// <summary>Every stylesheet that a page names, as the application serves it.</summary>
    public static async Task<IReadOnlyList<(string Name, string Styles)>> AllAsync()
    {
        // The application as it is, on the database that the tests share: a stylesheet is a
        // file, and nothing of it is stored.
        using var application = new LoggedApplication(LoggedApplication.Settings());
        using var client = application.CreateClient();

        var page = await client.PageAsync("/");
        var names = Stylesheet().Matches(page).Select(link => link.Groups[1].Value).ToList();

        // The stylesheet of the pages, and the one of the styles that are written beside a component.
        Assert.Equal(2, names.Count);

        var sheets = new List<(string, string)>();
        foreach (var name in names)
        {
            sheets.Add((name, await client.GetStringAsync(name, TestContext.Current.CancellationToken)));
        }

        return sheets;
    }

    /// <summary>A stylesheet without what it says about itself.</summary>
    public static string WithoutComments(string styles) => Comment().Replace(styles, string.Empty);

    /// <summary>The selector of the rule that a place in a stylesheet is in.</summary>
    public static string RuleOf(string styles, int index)
    {
        var opened = styles.LastIndexOf('{', index);
        var before = styles.LastIndexOfAny(['{', '}', ';'], opened - 1);

        return styles[(before + 1)..opened].Trim();
    }

    [GeneratedRegex("<link rel=\"stylesheet\" href=\"([^\"]*)\"")]
    private static partial Regex Stylesheet();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comment();
}
