namespace Ecnanib.Accounts;

/// <summary>
/// How much of the pages moves for a user: as their device asks, or reduced (FR-ANI-4). It is
/// stored with the account by the name of its member, so a member is not renamed without a
/// migration. See docs/motion.md.
/// </summary>
/// <remarks>
/// There is no member that has the pages move whatever the device asks: a device that asks for
/// reduced motion is never overruled.
/// </remarks>
public enum Motion
{
    /// <summary>
    /// As the device asks: the pages move, unless the device asks for reduced motion. It is what
    /// a new user has, and a visitor.
    /// </summary>
    System,

    /// <summary>Reduced, whatever the device asks: a change shows at once, and nothing moves by itself.</summary>
    Reduced,
}
