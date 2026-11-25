using Ecnanib.Accounts;

namespace Ecnanib.WebUi.Animation;

/// <summary>
/// How much of a page moves: as the device asks, or reduced (FR-ANI-4). The server decides it
/// for every page that it answers with, and writes it into the first element of the page beside
/// the theme, so the stylesheet gives nothing that moves to a page that says "reduced". What the
/// device asks is for the stylesheet to see, and the server is never told it. See docs/motion.md.
/// </summary>
public static class PageMotion
{
    /// <summary>
    /// How much moves on the page that a request is answered with. For a user who is signed in
    /// it is what their account says, on every device. A visitor has chosen nothing, and nothing
    /// is stored for one: their pages move as their device asks.
    /// </summary>
    public static Motion Of(HttpContext request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SignedInUser.MotionOf(request.User) ?? Motion.System;
    }

    /// <summary>
    /// What a page says of it to the stylesheet and to the script, as <c>data-motion</c> on its
    /// first element: <c>reduced</c>. A page that moves as the device asks says nothing, and the
    /// stylesheet follows the device.
    /// </summary>
    public static string? AttributeOf(Motion motion) => motion == Motion.Reduced ? WordOf(motion) : null;

    /// <summary>A choice as a form and a page spell it: <c>system</c> or <c>reduced</c>.</summary>
    public static string WordOf(Motion motion) => motion switch
    {
        Motion.Reduced => "reduced",
        _ => "system",
    };

    /// <summary>The choice that a word names, or null for anything that is no such word.</summary>
    public static Motion? OfWord(string? word) => word switch
    {
        "system" => Motion.System,
        "reduced" => Motion.Reduced,
        _ => null,
    };
}
