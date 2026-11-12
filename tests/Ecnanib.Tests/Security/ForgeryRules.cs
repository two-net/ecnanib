using Ecnanib.WebUi;

namespace Ecnanib.Tests.Security;

/// <summary>
/// Which endpoints take a request that changes something without the framework's check of the
/// token against request forgery (NFR-SEC-4), in one place. This file is the source of truth:
/// <c>docs/security.md</c> explains the rule, and <see cref="RequestForgeryTests"/> enforces it.
/// </summary>
/// <remarks>
/// Every page has the check: the framework refuses a form that is posted to a page without the
/// token of that page. An endpoint that is not a page has it only when it says so. To list one
/// here is to decide, where it shows, that it is safe without.
/// </remarks>
internal static class ForgeryRules
{
    /// <summary>The methods that only read, which need no token.</summary>
    public static readonly IReadOnlySet<string> ReadingMethods =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS", "TRACE" };

    /// <summary>
    /// The routes that take other methods without the framework's check, each with why that is safe.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> WithoutTheCheck = new Dictionary<string, string>
    {
        [WebUiModule.SignOutPath] =
            "It checks the token itself, for a request that is signed in. A request that is not signed in has nothing " +
            "to protect, and its page may be older than its session. SignInPageTests shows both.",

        ["/_blazor"] = Circuit,
        ["/_blazor/negotiate"] = Circuit,
        ["/_blazor/disconnect/"] = Circuit,
        ["/_blazor/initializers/"] = Circuit,
    };

    private const string Circuit =
        "The framework's connection of an interactive page. A connection that another site opens does not carry the " +
        "session cookie, which is SameSite=Lax, so it is a visitor's and reaches nothing of a user's.";
}
