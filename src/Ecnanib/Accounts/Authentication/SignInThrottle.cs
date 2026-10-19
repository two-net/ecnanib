using System.Collections.Concurrent;
using Ecnanib.Accounts.Configuration;
using Microsoft.Extensions.Options;

namespace Ecnanib.Accounts.Authentication;

/// <summary>
/// Sign-in rate limit (NFR-SEC-01) with fixed windows in memory, which is enough for the one running instance (C-10,
/// NFR-REL-04); a restart clears it. Two kinds of bucket: a credential bucket per (email, client address) pair holding
/// <c>Accounts:SignInAttemptsPerWindow</c> failures, so one attacker cannot lock a victim out of their own account and its
/// kill switch (FR-EXE-10); and an address bucket per client holding <c>Accounts:SignInAttemptsPerAddress</c> failed
/// sign-ins and registrations, whatever the email. Only failures count; a successful sign-in clears the credential bucket.
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

    /// <summary>How long until a sign-in for this email from this address is allowed again, or null when it is allowed now.</summary>
    public TimeSpan? RetryAfter(string normalizedEmail, string? clientAddress)
    {
        var now = _time.GetUtcNow();
        return Remaining(CredentialKey(normalizedEmail, clientAddress), _options.Value.SignInAttemptsPerWindow, now)
            ?? Remaining(AddressKey(clientAddress), _options.Value.SignInAttemptsPerAddress, now);
    }

    /// <summary>How long until this address may register or sign in again, or null when it is allowed now.</summary>
    public TimeSpan? RetryAfterForAddress(string? clientAddress) =>
        Remaining(AddressKey(clientAddress), _options.Value.SignInAttemptsPerAddress, _time.GetUtcNow());

    public void RecordFailure(string normalizedEmail, string? clientAddress)
    {
        Increment(CredentialKey(normalizedEmail, clientAddress));
        Increment(AddressKey(clientAddress));
    }

    /// <summary>Counts a registration attempt against the address bucket.</summary>
    public void RecordAttempt(string? clientAddress) => Increment(AddressKey(clientAddress));

    public void RecordSuccess(string normalizedEmail, string? clientAddress) =>
        _windows.TryRemove(CredentialKey(normalizedEmail, clientAddress), out _);

    private TimeSpan? Remaining(string key, int limit, DateTimeOffset now)
    {
        if (!_windows.TryGetValue(key, out var window))
            return null;

        var end = window.Start + _options.Value.SignInWindow;
        if (end <= now || window.Count < limit)
            return null;
        return end - now;
    }

    private void Increment(string key)
    {
        var now = _time.GetUtcNow();
        var span = _options.Value.SignInWindow;
        _windows.AddOrUpdate(key, _ => new Window(now, 1), (_, window) => window.Start + span <= now ? new Window(now, 1) : window with { Count = window.Count + 1 });
        Prune(now, span);
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
