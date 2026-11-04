using System.Net;
using System.Text.RegularExpressions;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Identity;

/// <summary>
/// A running application with its own database and a clock that tests move, shared by a test class.
/// Tests in a class run one at a time, so the clock only ever moves forward.
/// </summary>
public sealed class SharedTwoFactorApp : AppFactory
{
    public SharedTwoFactorApp(PostgresFixture postgres)
        : this(postgres.NewConnectionString(), new TestClock())
    {
    }

    private SharedTwoFactorApp(string connectionString, TestClock clock)
        : base(connectionString, TwoFactorSteps.UseClock(clock), AccountApp.Settings())
    {
        Clock = clock;
        ConnectionString = connectionString;
    }

    public TestClock Clock { get; }

    public string ConnectionString { get; }
}

/// <summary>Setting up two-factor the way a user does, through the page, and reading what it shows.</summary>
internal static partial class TwoFactorSteps
{
    public static Action<IServiceCollection> UseClock(TestClock clock) => services => services.AddSingleton<TimeProvider>(clock);

    /// <summary>Starts setup and returns the key the page shows, without spaces.</summary>
    public static async Task<string> StartAsync(Visitor visitor)
    {
        using var response = await visitor.SubmitAsync(Visitor.TwoFactorPage, Visitor.StartTwoFactorForm);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return KeyIn(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Confirms setup with a code from the next step, and returns the recovery codes the page shows.</summary>
    public static async Task<IReadOnlyList<string>> EnableAsync(Visitor visitor, string key, TestClock clock, string password = Visitor.Password)
    {
        using var response = await visitor.SubmitAsync(
            Visitor.TwoFactorPage,
            Visitor.EnableTwoFactorForm,
            ("Input.Password", password),
            ("Input.TwoFactorCode", clock.NextCode(key)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var codes = RecoveryCodesIn(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(10, codes.Count);

        return codes;
    }

    /// <summary>Registers, signs in and turns two-factor on, leaving that session in <paramref name="visitor"/>.</summary>
    public static async Task<TwoFactorAccount> SignUpWithTwoFactorAsync(Visitor visitor, TestClock clock)
    {
        var email = await visitor.SignUpAsync();
        var key = await StartAsync(visitor);
        var codes = await EnableAsync(visitor, key, clock);

        return new TwoFactorAccount(email, key, codes);
    }

    /// <summary>Both steps of a sign-in, from a visitor with no session yet.</summary>
    public static async Task<HttpResponseMessage> SignInAsync(Visitor visitor, string email, string code)
    {
        using var first = await visitor.SignInAsync(email);

        Assert.Equal(Visitor.SignInCodePage, Visitor.Target(first));

        return await visitor.SubmitCodeAsync(code);
    }

    public static string KeyIn(string page) => Key().Match(page).Groups["key"].Value.Replace(" ", string.Empty, StringComparison.Ordinal);

    public static List<string> RecoveryCodesIn(string page) => [.. RecoveryCode().Matches(page).Select(match => match.Groups["code"].Value)];

    [GeneratedRegex("""class="totp-key"[^>]*>(?<key>[A-Z2-7 ]+)</code>""")]
    private static partial Regex Key();

    [GeneratedRegex("""<li\b[^>]*><code\b[^>]*>(?<code>[a-z2-7]{5}-[a-z2-7]{5})</code></li>""")]
    private static partial Regex RecoveryCode();
}

/// <param name="Key">Base32, as the page showed it, without spaces.</param>
public sealed record TwoFactorAccount(string Email, string Key, IReadOnlyList<string> RecoveryCodes);

/// <summary>What the tests read and change in the two-factor tables, by address.</summary>
internal static class TwoFactorRows
{
    public static Task<long> AuthenticatorsAsync(string connectionString, string email, CancellationToken cancellationToken) =>
        Sql.ScalarAsync<long>(connectionString, $"SELECT count(*) FROM totp_authenticators WHERE {OfUser(email)}", cancellationToken);

    public static Task<Guid> SecretIdAsync(string connectionString, string email, CancellationToken cancellationToken) =>
        Sql.ScalarAsync<Guid>(connectionString, $"SELECT secret_id FROM totp_authenticators WHERE {OfUser(email)}", cancellationToken);

    public static Task<long> SecretsWithIdAsync(string connectionString, Guid secretId, CancellationToken cancellationToken) =>
        Sql.ScalarAsync<long>(connectionString, $"SELECT count(*) FROM secrets WHERE id = '{secretId}'", cancellationToken);

    public static Task<long> RecoveryCodesAsync(string connectionString, string email, CancellationToken cancellationToken) =>
        Sql.ScalarAsync<long>(connectionString, $"SELECT count(*) FROM recovery_codes WHERE {OfUser(email)}", cancellationToken);

    public static Task<int> FailedSignInsAsync(string connectionString, string email, CancellationToken cancellationToken) =>
        Sql.ScalarAsync<int>(connectionString, $"SELECT failed_sign_ins FROM users WHERE normalized_email = '{email.ToLowerInvariant()}'", cancellationToken);

    public static Task ExpireChallengeAsync(string connectionString, string email, CancellationToken cancellationToken) =>
        Sql.ExecuteAsync(connectionString, $"UPDATE sign_in_challenges SET expires_at = now() - interval '1 second' WHERE {OfUser(email)}", cancellationToken);

    private static string OfUser(string email) => $"user_id = (SELECT id FROM users WHERE normalized_email = '{email.ToLowerInvariant()}')";
}
