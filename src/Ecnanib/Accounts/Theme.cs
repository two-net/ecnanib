namespace Ecnanib.Accounts;

/// <summary>
/// How the pages look to a user: dark, light, or as their device is set (FR-THM-1). It is stored
/// with the account by the name of its member, so a member is not renamed without a migration.
/// See docs/themes.md.
/// </summary>
public enum Theme
{
    /// <summary>
    /// As the device is set, and changing when its setting does (FR-THM-2). It is what a new
    /// user has, and a visitor who has chosen nothing.
    /// </summary>
    System,

    /// <summary>Dark, whatever the device is set to.</summary>
    Dark,

    /// <summary>Light, whatever the device is set to.</summary>
    Light,
}
