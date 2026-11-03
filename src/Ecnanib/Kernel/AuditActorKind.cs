namespace Ecnanib.Kernel;

/// <summary>Who did what an audit record describes (FR-AUD-02).</summary>
public enum AuditActorKind
{
    User,

    /// <summary>An administrator acting on someone else's account, or on the whole platform.</summary>
    Administrator,

    /// <summary>The application itself, such as a job or a risk limit.</summary>
    System,

    /// <summary>
    /// Someone not signed in. FR-AUD-02 names only the three above, but a failed sign-in proves
    /// nothing about who sent it, so it isn't recorded as the account's user.
    /// </summary>
    Visitor,
}
