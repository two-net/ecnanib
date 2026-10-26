using System.Collections.Concurrent;
using Ecnanib.Accounts.Configuration;
using Microsoft.Extensions.Options;

namespace Ecnanib.Accounts.Authentication;

/// <summary>
/// Sign-in rate limit (NFR-SEC-01) with fixed windows in memory, which is enough for the one running instance (C-10,
/// NFR-REL-04); a restart clears it. Two kinds of bucket: a credential bucket per (email, client address) pair holding
/// <c>Accounts:SignInAttemptsPerWindow</c> failures, so one attacker cannot lock a victim out of their own account and its
/// kill switch (FR-EXE-10); and an address bucket per client holding <c>Accounts:SignInAttemptsPerAddress</c> failed
/// sign-ins and registrations, whatever the email. An attempt is counted before the password is checked and refunded
/// when it succeeds or when it is refused, so only failures stay counted and a burst of parallel attempts is cut at the
/// limit instead of all passing the check before the first failure is recorded. The client address is the connection's,
/// or the real client's behind a trusted proxy (<c>Server:TrustedProxies</c>).
/// </summary>
internal sealed class SignInThrottle
{
    private const int MaxKeyLength = 300;
    private const string UnknownAddress = "unknown";

    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly IOptions<AccountsOptions> _options;
    private readonly TimeProvider _time;

    public SignInThrottle(IOptions<AccountsOptions> options, TimeProvider time)
    {
        _options = options;
        _time = time;
    }

    internal int BucketCount => _windows.Count;

    /// <summary>
    /// Counts a sign-in attempt for this email from this address against both buckets. Null means the attempt may
    /// proceed (call <see cref="RecordSuccess"/> if it succeeds); otherwise the attempt is refused, not counted, and the
    /// value is how long until one is allowed again.
    /// </summary>
    public TimeSpan? TryBegin(string normalizedEmail, string? clientAddress)
    {
        var now = _time.GetUtcNow();
        var span = _options.Value.SignInWindow;
        var credentialKey = CredentialKey(normalizedEmail, clientAddress);
        var addressKey = AddressKey(clientAddress);
        var credential = Increment(credentialKey, now, span);
        var address = Increment(addressKey, now, span);
        Prune(now, span);

        var blocked = Blocked(credential, _options.Value.SignInAttemptsPerWindow, now, span)
            ?? Blocked(address, _options.Value.SignInAttemptsPerAddress, now, span);
        if (blocked is not null)
        {
            Refund(credentialKey);
            Refund(addressKey);
        }

        return blocked;
    }

    /// <summary>
    /// Counts a registration attempt against the address bucket, whether or not it goes on to succeed. Null means it
    /// may proceed; otherwise it is refused, not counted, and the value is how long until the address may try again.
    /// </summary>
    public TimeSpan? TryBeginForAddress(string? clientAddress)
    {
        var now = _time.GetUtcNow();
        var span = _options.Value.SignInWindow;
        var addressKey = AddressKey(clientAddress);
        var address = Increment(addressKey, now, span);
        Prune(now, span);

        var blocked = Blocked(address, _options.Value.SignInAttemptsPerAddress, now, span);
        if (blocked is not null)
            Refund(addressKey);
        return blocked;
    }

    /// <summary>A successful attempt forgets the credential bucket and gives the attempt back to the address bucket, so only failures stay counted.</summary>
    public void RecordSuccess(string normalizedEmail, string? clientAddress)
    {
        _windows.TryRemove(CredentialKey(normalizedEmail, clientAddress), out _);
        Refund(AddressKey(clientAddress));
    }

    private static TimeSpan? Blocked(Window window, int limit, DateTimeOffset now, TimeSpan span) =>
        window.Count > limit ? window.Start + span - now : null;

    private Window Increment(string key, DateTimeOffset now, TimeSpan span) =>
        _windows.AddOrUpdate(key, _ => new Window(now, 1), (_, window) => window.Start + span <= now ? new Window(now, 1) : window with { Count = window.Count + 1 });

    private void Refund(string key)
    {
        while (_windows.TryGetValue(key, out var window) && window.Count > 0)
        {
            if (_windows.TryUpdate(key, window with { Count = window.Count - 1 }, window))
                return;
        }
    }

    /// <summary>Keys are attacker-chosen strings; expired buckets are dropped on every write so memory stays bounded.</summary>
    private void Prune(DateTimeOffset now, TimeSpan span)
    {
        foreach (var (key, window) in _windows)
        {
            if (window.Start + span <= now)
                _windows.TryRemove(KeyValuePair.Create(key, window));
        }
    }

    private static string CredentialKey(string normalizedEmail, string? clientAddress) =>
        Truncate("cred:" + normalizedEmail + "|" + (clientAddress ?? UnknownAddress));

    private static string AddressKey(string? clientAddress) => Truncate("addr:" + (clientAddress ?? UnknownAddress));

    private static string Truncate(string key) => key.Length <= MaxKeyLength ? key : key[..MaxKeyLength];

    private readonly record struct Window(DateTimeOffset Start, int Count);
}
