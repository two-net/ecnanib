using System.Text.RegularExpressions;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Tests.Identity;

/// <summary>FR-AUTH-04. A fake hasher counts the work done, which says more than timing and doesn't flake.</summary>
public sealed partial class LockoutTests(PostgresFixture postgres)
{
    private const int Limit = 5;
    private const string Wrong = "the wrong password here";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>Checking the count and then hashing would let every parallel guess through a slow hash.</summary>
    [Fact]
    public async Task Parallel_guesses_get_no_more_checks_than_the_limit()
    {
        var hasher = new CountingHasher();
        await using var app = App(hasher);
        var email = await RegisterAsync(app);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Accounts(app).SignInAsync(email, Wrong, Visitor.AnyClient, Cancellation)));

        Assert.All(results, Assert.Null);
        Assert.Equal(Limit, hasher.Verifications);
        Assert.Equal(20 - Limit, hasher.Spent);
    }

    [Fact]
    public async Task The_right_password_is_refused_during_a_lock()
    {
        await using var app = App(new CountingHasher());
        var email = await RegisterAsync(app);

        await FailAsync(app, email, Limit);

        Assert.Null(await Accounts(app).SignInAsync(email, Visitor.Password, Visitor.AnyClient, Cancellation));
    }

    [Fact]
    public async Task A_lock_ends_on_its_own()
    {
        await using var app = App(new CountingHasher());
        var email = await RegisterAsync(app);
        var connectionString = app.ConnectionString();

        await FailAsync(app, email, Limit);
        await Sql.ExecuteAsync(connectionString, "UPDATE users SET locked_until = now() - interval '1 second'", Cancellation);

        Assert.NotNull(await Accounts(app).SignInAsync(email, Visitor.Password, Visitor.AnyClient, Cancellation));
        Assert.Equal(1, await Sql.ScalarAsync<long>(connectionString, "SELECT count(*) FROM users WHERE locked_until IS NULL AND failed_sign_ins = 0", Cancellation));
    }

    /// <summary>
    /// One lock can't be stretched. This doesn't stop someone locking an account again when it ends,
    /// which is the accepted cost of FR-AUTH-04: see Lockout in docs/architecture.md.
    /// </summary>
    [Fact]
    public async Task More_attempts_dont_extend_a_lock()
    {
        await using var app = App(new CountingHasher());
        var email = await RegisterAsync(app);
        var connectionString = app.ConnectionString();

        await FailAsync(app, email, Limit);
        var before = await Sql.ScalarAsync<DateTime>(connectionString, "SELECT locked_until FROM users", Cancellation);

        await FailAsync(app, email, 3);

        Assert.Equal(before, await Sql.ScalarAsync<DateTime>(connectionString, "SELECT locked_until FROM users", Cancellation));
    }

    [Fact]
    public async Task A_good_sign_in_clears_the_count()
    {
        await using var app = App(new CountingHasher());
        var email = await RegisterAsync(app);

        await FailAsync(app, email, Limit - 1);
        Assert.NotNull(await Accounts(app).SignInAsync(email, Visitor.Password, Visitor.AnyClient, Cancellation));

        await FailAsync(app, email, Limit - 1);
        Assert.NotNull(await Accounts(app).SignInAsync(email, Visitor.Password, Visitor.AnyClient, Cancellation));
    }

    [Fact]
    public async Task The_limit_and_the_lock_come_from_configuration()
    {
        await using var app = App(new CountingHasher(), new() { ["Security:MaxFailedSignIns"] = "2", ["Security:LockoutMinutes"] = "90" });
        var email = await RegisterAsync(app);

        await FailAsync(app, email, 2);

        var minutes = await Sql.ScalarAsync<double>(
            app.ConnectionString(),
            "SELECT (extract(epoch FROM locked_until - now()) / 60)::float8 FROM users",
            Cancellation);

        // Past 90 is possible, because the lock uses this host's clock and now() the database's.
        Assert.InRange(minutes, 89, 91);
        Assert.Null(await Accounts(app).SignInAsync(email, Visitor.Password, Visitor.AnyClient, Cancellation));
    }

    /// <summary>The answer and the work must both match, or either one would show which addresses exist.</summary>
    [Fact]
    public async Task Every_refusal_looks_the_same()
    {
        var hasher = new CountingHasher();
        await using var app = App(hasher);
        using var visitor = Visitor.To(app);
        var known = await RegisterAsync(app);
        var locked = await RegisterAsync(app);

        await FailAsync(app, locked, Limit);
        hasher.Reset();

        var wrong = await ReadRefusalAsync(visitor, known, Wrong);
        var checks = hasher.Total;
        var unknown = await ReadRefusalAsync(visitor, Visitor.NewEmail(), Wrong);
        var whileLocked = await ReadRefusalAsync(visitor, locked, Visitor.Password);

        Assert.Equal(1, checks);
        Assert.Equal(3, hasher.Total);
        Assert.Equal(wrong, unknown);
        Assert.Equal(wrong, whileLocked);
    }

    private static IAccounts Accounts(AppFactory app) => app.Services.GetRequiredService<IAccounts>();

    private static async Task<string> RegisterAsync(AppFactory app)
    {
        using var visitor = Visitor.To(app);
        var email = Visitor.NewEmail();

        (await visitor.RegisterAsync(email)).Dispose();

        return email;
    }

    private static async Task FailAsync(AppFactory app, string email, int times)
    {
        for (var attempt = 0; attempt < times; attempt++)
        {
            await Accounts(app).SignInAsync(email, Wrong, Visitor.AnyClient, Cancellation);
        }
    }

    /// <summary>The page with what legitimately differs taken out: the per-request values, and the address typed in.</summary>
    private static async Task<string> ReadRefusalAsync(Visitor visitor, string email, string password)
    {
        using var response = await visitor.SignInAsync(email, password);
        var page = await response.Content.ReadAsStringAsync(Cancellation);

        return $"{(int)response.StatusCode} {PerRequest().Replace(page, string.Empty).Replace(email, string.Empty, StringComparison.Ordinal)}";
    }

    private AppFactory App(CountingHasher hasher, Dictionary<string, string?>? settings = null) =>
        AccountApp.Create(
            postgres,
            settings,
            services =>
            {
                services.RemoveAll<IPasswordHasher>();
                services.AddSingleton<IPasswordHasher>(hasher);
            });

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"[^\"]*\"|nonce=\"[^\"]*\"|<!--Blazor-Server-Component-State:[^>]*-->")]
    private static partial Regex PerRequest();

    private sealed class CountingHasher : IPasswordHasher
    {
        private int _verifications;
        private int _spent;

        public int Verifications => _verifications;

        public int Spent => _spent;

        public int Total => _verifications + _spent;

        public void Reset()
        {
            _verifications = 0;
            _spent = 0;
        }

        public Task<string> HashAsync(string password, CancellationToken cancellationToken) => Task.FromResult(password);

        public async Task<bool> VerifyAsync(string password, string hash, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _verifications);

            // A real hash is slow, and a guess that's still hashing must not let the next one through.
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);

            return password == hash;
        }

        public Task SpendAsync(string password, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _spent);

            return Task.CompletedTask;
        }
    }
}
