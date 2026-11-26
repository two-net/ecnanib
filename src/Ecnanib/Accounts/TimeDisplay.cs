namespace Ecnanib.Accounts;

/// <summary>
/// The time zone that the pages write a time in for a user: the one of the device that they
/// signed in on, or UTC (NFR-USA-3). It is stored with the account by the name of its member,
/// so a member is not renamed without a migration. See docs/times.md.
/// </summary>
/// <remarks>
/// The account says which of the two, and not which time zone: that is of the device, which
/// says it when its user signs in, and is kept on the device. So a user who signs in at home
/// and on a journey reads the time of where they are, and one who chose UTC reads UTC on both.
/// </remarks>
public enum TimeDisplay
{
    /// <summary>
    /// In the time zone of the device, as it said it when its user signed in. It is what a new
    /// user has. A device that has said none is read UTC.
    /// </summary>
    Device,

    /// <summary>In UTC, whatever the device says: the time that the exchange and the candles go by.</summary>
    Utc,
}
