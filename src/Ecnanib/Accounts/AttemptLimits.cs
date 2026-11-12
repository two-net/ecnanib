using Ecnanib.Platform.Settings;

namespace Ecnanib.Accounts;

/// <summary>What the limits say to one attempt.</summary>
internal enum Attempt
{
    /// <summary>It may go on, and it is counted.</summary>
    Allowed,

    /// <summary>The client has tried as often as one client may. The attempt is not counted.</summary>
    TooManyFromClient,

    /// <summary>The email address has been tried as often as one address may be. The attempt is not counted.</summary>
    TooManyForAddress,

    /// <summary>The password of the account has been tried as often as it may be. The attempt is not counted.</summary>
    TooManyForAccount,
}

/// <summary>
/// How often signing in, registering, and asking for a password reset may be tried (NFR-SEC-4):
/// so many times from one client, and so many times for one email address, within a period. The
/// numbers are those of the <c>Limits</c> section of the configuration file. See "Limits on
/// attempts" in docs/accounts.md.
/// </summary>
/// <remarks>
/// <para>
/// Whatever takes one of these attempts asks here first, before it looks anything up, hashes a
/// password, or sends an email. An attempt that is refused does none of that.
/// </para>
/// <para>
/// An address is counted as it is typed, whether or not it has an account, so being refused does
/// not tell whether it has one.
/// </para>
/// <para>
/// The counts are kept in memory. They start again when the application does, and each running
/// instance keeps its own. Nothing of them is stored: the address of a client, and an email
/// address that may have no account, are not kept about anyone (NFR-SEC-9).
/// </para>
/// </remarks>
internal sealed class AttemptLimits(LimitsSettings limits, TimeProvider clock)
{
    private readonly AttemptWindow _signIns = new(limits.SignIn.Period);
    private readonly AttemptWindow _registrations = new(limits.Registration.Period);
    private readonly AttemptWindow _passwordResets = new(limits.PasswordReset.Period);

    // Of the account of a browser that is signed in. Its own count: someone who tries the
    // sign-in form for an address does not use up what its owner has here.
    private readonly AttemptWindow _passwordsOfAccounts = new(limits.SignIn.Period);

    /// <summary>One try of a password for an address, on the sign-in form.</summary>
    public Attempt SignIn(AuditOrigin origin, string address) => Take(_signIns, limits.SignIn, origin, address);

    /// <summary>One registration of an address.</summary>
    public Attempt Registration(AuditOrigin origin, string address) =>
        Take(_registrations, limits.Registration, origin, address);

    /// <summary>One request for a password reset link for an address.</summary>
    public Attempt PasswordReset(AuditOrigin origin, string address) =>
        Take(_passwordResets, limits.PasswordReset, origin, address);

    /// <summary>
    /// One try of the password of an account by a browser that is signed in to it: before a
    /// sensitive action, or to turn two-factor authentication on. It may be tried as often as
    /// the password of one address on the sign-in form.
    /// </summary>
    public Attempt PasswordOf(Guid userId)
    {
        var refused = _passwordsOfAccounts.Take(clock.GetUtcNow(), [new Allowance($"account:{userId}", limits.SignIn.PerAddress)]);

        return refused is null ? Attempt.Allowed : Attempt.TooManyForAccount;
    }

    /// <summary>
    /// The limit that refused an attempt, as the log says it. It names no client and no address:
    /// the logs hold neither.
    /// </summary>
    public static string Describe(Attempt attempt) => attempt switch
    {
        Attempt.TooManyFromClient => "from one client",
        Attempt.TooManyForAddress => "for one address",
        Attempt.TooManyForAccount => "for one account",
        _ => throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "An attempt that is allowed was refused by no limit."),
    };

    private Attempt Take(AttemptWindow window, AttemptLimitSettings limit, AuditOrigin origin, string address)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(address);

        // Both or neither: an attempt that the client is refused does not use up what the
        // address has, and the other way round.
        var refused = window.Take(
            clock.GetUtcNow(),
            [new Allowance($"client:{origin.Client}", limit.PerClient), new Allowance($"address:{address}", limit.PerAddress)]);

        return refused switch
        {
            null => Attempt.Allowed,
            0 => Attempt.TooManyFromClient,
            _ => Attempt.TooManyForAddress,
        };
    }
}

/// <summary>What is counted, and how many attempts it has within the period.</summary>
internal readonly record struct Allowance(string Key, int Attempts);

/// <summary>
/// The attempts of a period, by what they are counted for. Something has room for one more
/// attempt while it has made fewer than it is allowed within the period that ends now. So it is
/// refused from its last allowed attempt until its oldest one is a period old.
/// </summary>
internal sealed class AttemptWindow(TimeSpan period)
{
    private readonly Lock _lock = new();

    // When each attempt was made, oldest first.
    private readonly Dictionary<string, Queue<long>> _attempts = new(StringComparer.Ordinal);

    private long _sweptAt;

    /// <summary>
    /// Counts one attempt for each of the allowances, when every one of them has room for it.
    /// Otherwise it counts nothing: being refused does not make the wait longer.
    /// </summary>
    /// <returns>Null when the attempt was counted, or the position of the first allowance that has no room.</returns>
    public int? Take(DateTimeOffset now, ReadOnlySpan<Allowance> allowances)
    {
        var moment = now.UtcTicks;
        var expired = moment - period.Ticks;

        lock (_lock)
        {
            Sweep(moment, expired);

            for (var position = 0; position < allowances.Length; position++)
            {
                if (_attempts.TryGetValue(allowances[position].Key, out var made))
                {
                    Forget(made, expired);

                    if (made.Count >= allowances[position].Attempts)
                    {
                        return position;
                    }
                }
            }

            foreach (var allowance in allowances)
            {
                if (!_attempts.TryGetValue(allowance.Key, out var made))
                {
                    made = new Queue<long>();
                    _attempts[allowance.Key] = made;
                }

                made.Enqueue(moment);
            }

            return null;
        }
    }

    /// <summary>
    /// Drops what no longer counts, once in a period, so that what was tried once and never
    /// again does not stay in memory.
    /// </summary>
    private void Sweep(long moment, long expired)
    {
        // Also when the clock was set back, so that the next sweep is not put off.
        if (moment >= _sweptAt && moment - _sweptAt < period.Ticks)
        {
            return;
        }

        _sweptAt = moment;

        foreach (var (key, made) in _attempts)
        {
            Forget(made, expired);

            if (made.Count == 0)
            {
                _attempts.Remove(key);
            }
        }
    }

    private static void Forget(Queue<long> made, long expired)
    {
        while (made.TryPeek(out var oldest) && oldest <= expired)
        {
            made.Dequeue();
        }
    }
}
