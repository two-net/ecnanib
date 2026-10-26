namespace Ecnanib.Database;

/// <summary>Where the process stands with the database's instance lock (NFR-REL-04).</summary>
public enum InstanceLockState
{
    /// <summary>Not taken yet, or released on shutdown.</summary>
    NotHeld = 1,
    Held = 2,
    /// <summary>The connection holding it was lost; the lock is being taken again.</summary>
    Reacquiring = 3,
    /// <summary>Another instance took the lock while this one re-acquired; this instance is stopping.</summary>
    Lost = 4,
}

/// <summary>The instance lock as the status page shows it (NFR-OBS-01); <see cref="BackendPid"/> is the PostgreSQL backend that holds it.</summary>
public sealed record InstanceLockStatus(InstanceLockState State, DateTimeOffset? HeldSince, int? BackendPid, DateTimeOffset? LastCheckedAt, string? LastError, DateTimeOffset? LastErrorAt)
{
    public static readonly InstanceLockStatus NotHeld = new(InstanceLockState.NotHeld, null, null, null, null, null);
}

public interface IInstanceLockStatus
{
    InstanceLockStatus GetStatus();
}
