using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using Ecnanib.Accounts;
using Ecnanib.AiProviderGateway;
using Ecnanib.Credentials;
using Ecnanib.ExchangeGateway;
using Ecnanib.Notifications;
using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Security;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;
using Ecnanib.WebUi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// Stands for the email delivery service: it keeps what it is handed, and fails when a test
/// tells it to.
/// </summary>
internal sealed class RecordingEmail : IEmailAdapter
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    /// <summary>The messages taken, in order.</summary>
    public IReadOnlyList<EmailMessage> Sent => [.. _sent];

    /// <summary>When set, every send fails with it, as when the mail server is away.</summary>
    public Exception? Failure { get; set; }

    /// <summary>When set, it runs as a message is handed over, before the message is taken.</summary>
    public Action<EmailMessage>? Sending { get; set; }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromException(failure);
        }

        Sending?.Invoke(message);

        // As the mail server's client does: a send that was canceled sends nothing.
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        _sent.Enqueue(message);

        return Task.CompletedTask;
    }
}

/// <summary>
/// The application on a database of the test's own, so that a test can say what the tables
/// hold, with the email it sends kept by <see cref="RecordingEmail"/>, and with
/// <see cref="StandInProviders"/> in place of the AI providers and of Binance, which take every
/// key unless a test says otherwise.
/// </summary>
internal sealed class AccountsApplication : IDisposable
{
    /// <summary>A password that the rules accept.</summary>
    public const string Password = "correct horse battery staple";

    /// <summary>The address users reach the application at, as its configuration file says.</summary>
    public const string PublicUrl = "https://ecnanib.example";

    private readonly LoggedApplication _application;

    /// <param name="registrationOpen">What the file says about registration. The file of the other tests has it closed.</param>
    /// <param name="clock">The time the application goes by, when the test sets it.</param>
    /// <param name="database">A database that an earlier application of the test has used.</param>
    /// <param name="settings">Further changes to the configuration file.</param>
    /// <param name="host">Further changes to the host, such as a request handler of the test's own.</param>
    public AccountsApplication(
        bool registrationOpen = true,
        TestClock? clock = null,
        TestDatabase? database = null,
        Func<SettingsJson, SettingsJson>? settings = null,
        Action<IWebHostBuilder>? host = null)
    {
        Database = database ?? PostgresServer.CreateDatabase();

        var file = LoggedApplication.Settings().With("Application:RegistrationOpen", registrationOpen);

        _application = new LoggedApplication(
            settings?.Invoke(file) ?? file,
            builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IEmailAdapter>(Email);

                    // A key is stored only when its provider takes it, and no test reaches one.
                    services.AddSingleton<IAiKeyChecks>(Providers);
                    services.AddSingleton<IExchangeAdapter>(Providers);

                    if (clock is not null)
                    {
                        services.AddSingleton<TimeProvider>(clock);
                    }
                });

                host?.Invoke(builder);
            },
            Database);
    }

    public TestDatabase Database { get; }

    public RecordingEmail Email { get; } = new();

    /// <summary>What stands for the AI providers and for Binance when they are asked about a key.</summary>
    public StandInProviders Providers { get; } = new();

    /// <summary>What the components above Credentials ask about a user's stored keys.</summary>
    public IKeyValidation KeyValidation => _application.Services.GetRequiredService<IKeyValidation>();

    /// <summary>
    /// Runs the job that checks the stored keys, now, as the scheduler does on the hour. A test
    /// runs it itself to see what a run does at a time of its choosing.
    /// </summary>
    public Task CheckKeysAsync(DateTimeOffset scheduledFor) =>
        _application.Services.GetServices<IBackgroundJob>()
            .Single(job => job.Name == "key-validation")
            .RunAsync(new BackgroundJobRun(scheduledFor), TestContext.Current.CancellationToken);

    public IRegistration Registration => _application.Services.GetRequiredService<IRegistration>();

    public IFairUse FairUse => _application.Services.GetRequiredService<IFairUse>();

    public IPasswordReset PasswordReset => _application.Services.GetRequiredService<IPasswordReset>();

    public PasswordHasher Passwords => _application.Services.GetRequiredService<PasswordHasher>();

    public IReauthentication Reauthentication => _application.Services.GetRequiredService<IReauthentication>();

    /// <summary>The database as a component reaches it, for a test that asks it the way a component does.</summary>
    public IDbContextFactory<EcnanibDbContext> Contexts =>
        _application.Services.GetRequiredService<IDbContextFactory<EcnanibDbContext>>();

    /// <summary>What encrypts the secrets that the database holds, with the master keys of the application's file.</summary>
    public StoredSecrets Secrets => _application.Services.GetRequiredService<StoredSecrets>();

    /// <summary>Every document in the version in force: what the form posts when all boxes are ticked.</summary>
    public static IReadOnlyCollection<AcceptedDocument> EveryDocument =>
        [.. LegalDocuments.All.Select(document => new AcceptedDocument(document, LegalDocuments.CurrentVersionOf(document)))];

    public HttpClient CreateClient() => _application.CreateClient();

    /// <summary>A client that does not follow where an answer leads, so that a test sees the answer itself.</summary>
    public HttpClient CreateClientThatStays() => _application.CreateClientThatStays();

    public IReadOnlyList<LoggedEvent> Events() => _application.Events();

    public string LogText() => _application.LogText();

    public Task<RegistrationResult> RegisterAsync(
        string email, string password = Password, IReadOnlyCollection<AcceptedDocument>? accepted = null) =>
        Registration.RegisterAsync(
            new RegistrationRequest(email, accepted ?? EveryDocument), password, AuditOrigin.Unknown, TestContext.Current.CancellationToken);

    /// <summary>
    /// Confirms a verification link, with the password that the link of a repeat registration
    /// asks for. It is asked of the service itself, and not through a page, so it comes from
    /// nowhere that the audit trail could name.
    /// </summary>
    public Task<EmailVerificationOutcome> ConfirmAsync(string token, string? newPassword = null) =>
        Registration.VerifyEmailAsync(token, newPassword, AuditOrigin.Unknown, TestContext.Current.CancellationToken);

    /// <summary>Whether confirming a verification link verified the address.</summary>
    public async Task<bool> VerifyAsync(string token, string? newPassword = null) =>
        await ConfirmAsync(token, newPassword) == EmailVerificationOutcome.Verified;

    public Task<PasswordResetRequestOutcome> RequestResetAsync(string email) =>
        PasswordReset.RequestAsync(email, AuditOrigin.Unknown, TestContext.Current.CancellationToken);

    public Task<PasswordResetOutcome> ResetAsync(string token, string newPassword) =>
        PasswordReset.ResetAsync(token, newPassword, AuditOrigin.Unknown, TestContext.Current.CancellationToken);

    /// <summary>Every row of every table, as the database would hand it to whoever reads it.</summary>
    public string EverythingStored() =>
        string.Join(
            Environment.NewLine,
            Database.Tables().SelectMany(table =>
                Database.Column($"""SELECT row_to_json(stored)::text FROM "{table}" stored""")));

    /// <summary>
    /// Every row that belongs to a user, by the table it is in, as the database would hand it to
    /// whoever reads it. The tables are those of the model that hold rows of users, so a table
    /// that is added later is among them.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> RowsOf(Guid userId)
    {
        using var model = ModelOnly.Context();

        return OwnershipRules.UserOwnedTables(model.Model).ToDictionary(
            table => table,
            table => Database.Column(
                $"""SELECT row_to_json(stored)::text FROM "{table}" stored WHERE user_id = '{userId}' ORDER BY 1"""));
    }

    /// <summary>Registers an address and verifies it with the link it was sent.</summary>
    public async Task<StoredUser> RegisterVerifiedAsync(string email)
    {
        Assert.Equal(RegistrationOutcome.Accepted, (await RegisterAsync(email)).Outcome);
        Assert.True(await VerifyAsync(TokenIn(Email.Sent[^1])));

        return StoredUser.Of(Database, email)!;
    }

    /// <summary>
    /// Registers an address, verifies it, and turns two-factor authentication on for it, as its
    /// user does. Nobody is signed in afterwards, and the clock is a minute on, so that the next
    /// code of the app is one that was not used.
    /// </summary>
    /// <returns>The account, the app that was enrolled, and the recovery codes that were shown.</returns>
    public async Task<(StoredUser User, AuthenticatorApp App, IReadOnlyList<string> RecoveryCodes)> RegisterWithTwoFactorAsync(
        string email, TestClock clock)
    {
        using var client = CreateClient();
        var user = await RegisterVerifiedAsync(email);

        await client.SignInAsync(email);
        var (app, recoveryCodes) = await client.TurnOnTwoFactorAsync(clock);
        await client.SignOutAsync();

        clock.Set(clock.GetUtcNow().AddMinutes(1));

        return (user, app, recoveryCodes);
    }

    /// <summary>The link in a message: a verification link or a reset link.</summary>
    public static Uri LinkIn(EmailMessage message)
    {
        var link = Regex.Match(message.TextBody, @"^https?://\S+$", RegexOptions.Multiline);
        Assert.True(link.Success, $"The message has no link on a line of its own:{Environment.NewLine}{message.TextBody}");

        return new Uri(link.Value);
    }

    /// <summary>The token of the link in a message. Both kinds of link carry it under the same name.</summary>
    public static string TokenIn(EmailMessage message) =>
        HttpUtility.ParseQueryString(LinkIn(message).Query)[EmailVerification.TokenParameter]!;

    public void Dispose() => _application.Dispose();
}

/// <summary>A row of the <c>users</c> table, read by a test directly.</summary>
internal sealed record StoredUser(
    Guid Id,
    string Email,
    string PasswordHash,
    DateTimeOffset RegisteredAt,
    DateTimeOffset? EmailVerifiedAt,
    byte[]? TokenHash,
    DateTimeOffset? TokenExpiresAt,
    bool LinkSetsPassword,
    byte[]? ResetTokenHash,
    DateTimeOffset? ResetExpiresAt)
{
    /// <summary>Every account, oldest first.</summary>
    public static IReadOnlyList<StoredUser> All(TestDatabase database)
    {
        using var connection = database.Open();
        using var command = new NpgsqlCommand(
            """
            SELECT id, email, password_hash, registered_at, email_verified_at,
                   email_verification_token_hash, email_verification_expires_at,
                   email_verification_sets_password, password_reset_token_hash, password_reset_expires_at
            FROM users ORDER BY registered_at, id
            """,
            connection);
        using var reader = command.ExecuteReader();

        var users = new List<StoredUser>();
        while (reader.Read())
        {
            users.Add(new StoredUser(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<byte[]>(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetBoolean(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<byte[]>(8),
                reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9)));
        }

        return users;
    }

    /// <summary>The account of an address, or null when it has none.</summary>
    public static StoredUser? Of(TestDatabase database, string email) =>
        All(database).SingleOrDefault(user => user.Email == email);
}

/// <summary>A row of the <c>sessions</c> table, read by a test directly.</summary>
internal sealed record StoredSession(
    Guid Id,
    Guid UserId,
    byte[] TokenHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActiveAt,
    DateTimeOffset? ReauthenticatedAt)
{
    /// <summary>Every session, oldest first.</summary>
    public static IReadOnlyList<StoredSession> All(TestDatabase database)
    {
        using var connection = database.Open();
        using var command = new NpgsqlCommand(
            "SELECT id, user_id, token_hash, created_at, last_active_at, reauthenticated_at FROM sessions ORDER BY created_at, id",
            connection);
        using var reader = command.ExecuteReader();

        var sessions = new List<StoredSession>();
        while (reader.Read())
        {
            sessions.Add(new StoredSession(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetFieldValue<byte[]>(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return sessions;
    }
}

/// <summary>A row of the <c>legal_acceptances</c> table, read by a test directly.</summary>
internal sealed record StoredAcceptance(Guid UserId, string Document, string Version, DateTimeOffset AcceptedAt)
{
    public static IReadOnlyList<StoredAcceptance> All(TestDatabase database)
    {
        using var connection = database.Open();
        using var command = new NpgsqlCommand(
            "SELECT user_id, document, version, accepted_at FROM legal_acceptances ORDER BY user_id, document", connection);
        using var reader = command.ExecuteReader();

        var acceptances = new List<StoredAcceptance>();
        while (reader.Read())
        {
            acceptances.Add(new StoredAcceptance(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return acceptances;
    }
}

/// <summary>What the <c>users</c> table holds of an account for two-factor authentication, read by a test directly.</summary>
internal sealed record StoredTwoFactor(
    byte[]? Secret, string? SecretKeyId, DateTimeOffset? EnabledAt, long? LastStep, int FailedCodes, DateTimeOffset? LockedUntil)
{
    /// <summary>Whether nothing at all is stored: as for an account that never turned it on.</summary>
    public bool IsEmpty =>
        Secret is null && SecretKeyId is null && EnabledAt is null && LastStep is null && FailedCodes == 0 && LockedUntil is null;

    public static StoredTwoFactor Of(TestDatabase database, string email)
    {
        using var connection = database.Open();
        using var command = new NpgsqlCommand(
            """
            SELECT two_factor_secret, two_factor_secret_key_id, two_factor_enabled_at,
                   two_factor_last_step, two_factor_failed_codes, two_factor_locked_until
            FROM users WHERE email = @email
            """,
            connection);
        command.Parameters.AddWithValue("email", email);
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read(), $"There is no account of {email}.");

        return new StoredTwoFactor(
            reader.IsDBNull(0) ? null : reader.GetFieldValue<byte[]>(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5));
    }
}

/// <summary>A row of the <c>two_factor_recovery_codes</c> table, read by a test directly.</summary>
internal sealed record StoredRecoveryCode(Guid UserId, byte[] CodeHash, DateTimeOffset CreatedAt)
{
    public static IReadOnlyList<StoredRecoveryCode> All(TestDatabase database)
    {
        using var connection = database.Open();
        using var command = new NpgsqlCommand(
            "SELECT user_id, code_hash, created_at FROM two_factor_recovery_codes ORDER BY user_id, code_hash", connection);
        using var reader = command.ExecuteReader();

        var codes = new List<StoredRecoveryCode>();
        while (reader.Read())
        {
            codes.Add(new StoredRecoveryCode(
                reader.GetGuid(0), reader.GetFieldValue<byte[]>(1), reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return codes;
    }

    /// <summary>The hash that the table holds of a code, however the code is written.</summary>
    public static byte[] HashOf(string code) =>
        System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(code.Replace("-", string.Empty)));
}

/// <summary>A row of the <c>pending_sign_ins</c> table, read by a test directly.</summary>
internal sealed record StoredPendingSignIn(Guid Id, Guid UserId, byte[] TokenHash, DateTimeOffset ExpiresAt)
{
    public static IReadOnlyList<StoredPendingSignIn> All(TestDatabase database)
    {
        using var connection = database.Open();
        using var command = new NpgsqlCommand(
            "SELECT id, user_id, token_hash, expires_at FROM pending_sign_ins ORDER BY expires_at, id", connection);
        using var reader = command.ExecuteReader();

        var pending = new List<StoredPendingSignIn>();
        while (reader.Read())
        {
            pending.Add(new StoredPendingSignIn(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetFieldValue<byte[]>(2),
                reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return pending;
    }
}

/// <summary>A row of the <c>audit_events</c> table, read by a test directly.</summary>
internal sealed record StoredAuditEvent(Guid Id, Guid UserId, string Action, string Target, DateTimeOffset OccurredAt, string? Origin)
{
    /// <summary>Every event, in the order they were recorded.</summary>
    public static IReadOnlyList<StoredAuditEvent> All(TestDatabase database)
    {
        using var connection = database.Open();
        using var command = new NpgsqlCommand(
            "SELECT id, user_id, action, target, occurred_at, origin FROM audit_events ORDER BY id", connection);
        using var reader = command.ExecuteReader();

        var events = new List<StoredAuditEvent>();
        while (reader.Read())
        {
            events.Add(new StoredAuditEvent(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return events;
    }

    /// <summary>
    /// The events about two-factor authentication, in the order they were recorded: what a test
    /// about its changes looks at, without the sign-ins and the proofs around them.
    /// </summary>
    public static IReadOnlyList<StoredAuditEvent> OfTwoFactor(TestDatabase database) =>
        [.. All(database).Where(recorded => recorded.Action.StartsWith("TwoFactor", StringComparison.Ordinal))];
}

/// <summary>
/// Stands for the authenticator app on the phone of a user: it holds the key that the page
/// showed, and gives the code of a moment.
/// </summary>
internal sealed class AuthenticatorApp(byte[] key)
{
    private const string Base32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public byte[] Key { get; } = key;

    /// <summary>The key as the page shows it for typing, without its spaces.</summary>
    public string KeyText => Totp.TextOf(Key);

    public string CodeAt(DateTimeOffset time) => Totp.CodeAt(Key, time);

    /// <summary>Six digits that are not the code of a moment, nor of the steps around it.</summary>
    public string WrongCodeAt(DateTimeOffset time)
    {
        var right = new[] { -30, 0, 30 }.Select(seconds => CodeAt(time.AddSeconds(seconds))).ToHashSet();

        return Enumerable.Range(0, 1000).Select(number => (111111 + number).ToString()).First(code => !right.Contains(code));
    }

    /// <summary>The app as a user sets it up: with the key that the page shows as text.</summary>
    public static AuthenticatorApp WithKeyOn(string page)
    {
        var key = Regex.Match(page, "<code class=\"setup-key\">([A-Z2-7 ]+)</code>");
        Assert.True(key.Success, $"The page shows no key to type into an app:{Environment.NewLine}{page}");

        return new AuthenticatorApp(Decode(key.Groups[1].Value.Replace(" ", string.Empty)));
    }

    private static byte[] Decode(string text)
    {
        var bytes = new List<byte>();
        var held = 0;
        var bits = 0;

        foreach (var letter in text)
        {
            held = (held << 5) | Base32.IndexOf(letter);
            bits += 5;

            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)(held >> bits));
            }
        }

        return [.. bytes];
    }
}

/// <summary>What a browser does with the pages: reads one, and posts a form of it.</summary>
internal static class Browser
{
    /// <summary>The page at a path, which must be there.</summary>
    public static async Task<string> PageAsync(this HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode, $"GET {path} answered {(int)response.StatusCode}:{Environment.NewLine}{page}");

        return page;
    }

    /// <summary>Asks for a path and follows where the answer leads, as a browser does with a link.</summary>
    /// <returns>The path and query the browser ends up at, and the page there.</returns>
    public static async Task<(string Address, string Page)> OpenAsync(this HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return (response.RequestMessage!.RequestUri!.PathAndQuery, page);
    }

    /// <summary>
    /// Posts the form with a name on the page at a path, with the token against request forgery
    /// that the page carries, and follows where the answer leads.
    /// </summary>
    /// <returns>The path the browser ends up at, and the page there.</returns>
    public static async Task<(string Path, string Page)> SubmitAsync(
        this HttpClient client, string path, string form, params (string Name, string Value)[] fields)
    {
        var page = await client.PageAsync(path);

        return await client.PostAsync(path, form, AntiforgeryTokenIn(page), fields);
    }

    /// <summary>Posts a form without reading its page first, as something that is not a browser can.</summary>
    public static async Task<(string Path, string Page)> PostAsync(
        this HttpClient client, string path, string form, string antiforgeryToken, params (string Name, string Value)[] fields)
    {
        using var response = await client.SendFormAsync(path, form, antiforgeryToken, fields);
        var answer = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return (response.RequestMessage!.RequestUri!.AbsolutePath, answer);
    }

    /// <summary>Posts a form and returns the answer itself: where it ended up, with its status and headers.</summary>
    public static async Task<HttpResponseMessage> SendFormAsync(
        this HttpClient client, string path, string form, string antiforgeryToken, params (string Name, string Value)[] fields)
    {
        using var content = new FormUrlEncodedContent(
        [
            new("_handler", form),
            new("__RequestVerificationToken", antiforgeryToken),
            .. fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value)),
        ]);

        return await client.PostAsync(path, content, TestContext.Current.CancellationToken);
    }

    /// <summary>Signs in on the sign-in form, and follows where the answer leads.</summary>
    /// <param name="client">The browser.</param>
    /// <param name="email">The address to sign in with.</param>
    /// <param name="password">The password to sign in with.</param>
    /// <param name="page">The address of the form, when it carries where to go afterwards.</param>
    public static Task<(string Path, string Page)> SignInAsync(
        this HttpClient client, string email, string password = AccountsApplication.Password, string page = SessionAuthentication.SignInPath) =>
        client.SubmitAsync(page, "sign-in", ("Email", email), ("Password", password));

    /// <summary>Presses the sign-out button of the start page, and follows where the answer leads.</summary>
    public static async Task<(string Path, string Page)> SignOutAsync(this HttpClient client) =>
        await client.PostAsync(WebUiModule.SignOutPath, "sign-out", AntiforgeryTokenIn(await client.PageAsync("/")));

    /// <summary>The path of the page that turns two-factor authentication on, and says how it stands.</summary>
    public const string TwoFactorPage = "/account/two-factor";

    /// <summary>
    /// Turns two-factor authentication on for the user who is signed in, as a user does: opens
    /// the page, sets an app up with the key on it, and posts the code of the app and the password.
    /// </summary>
    /// <returns>The app, and the recovery codes that the answer showed.</returns>
    public static async Task<(AuthenticatorApp App, IReadOnlyList<string> RecoveryCodes)> TurnOnTwoFactorAsync(
        this HttpClient client, TimeProvider clock, string password = AccountsApplication.Password)
    {
        var page = await client.PageAsync(TwoFactorPage);
        var app = AuthenticatorApp.WithKeyOn(page);

        var (_, answer) = await client.PostAsync(
            TwoFactorPage,
            "turn-on",
            AntiforgeryTokenIn(page),
            ("SealedKey", SealedKeyIn(page)),
            ("Code", app.CodeAt(clock.GetUtcNow())),
            ("Password", password));

        Assert.Contains("Two-factor authentication is on.", answer);

        return (app, RecoveryCodesIn(answer));
    }

    /// <summary>Gives a code on the page that asks for one after the password, and follows where the answer leads.</summary>
    public static Task<(string Path, string Page)> EnterCodeAsync(
        this HttpClient client, string code, string page = SessionAuthentication.SignInCodePath) =>
        client.SubmitAsync(page, "sign-in-code", ("Code", code));

    /// <summary>
    /// Proves again who the signed-in user is, on the page that asks for it before a sensitive
    /// action, and follows where the answer leads.
    /// </summary>
    /// <param name="client">The browser.</param>
    /// <param name="password">The password to give.</param>
    /// <param name="code">The code to give, for an account with two-factor authentication.</param>
    /// <param name="page">The address of the form, when it carries where to go afterwards.</param>
    public static Task<(string Path, string Page)> ConfirmIdentityAsync(
        this HttpClient client,
        string password = AccountsApplication.Password,
        string? code = null,
        string page = SessionAuthentication.ReauthenticationPath) =>
        code is null
            ? client.SubmitAsync(page, "confirm-identity", ("Password", password))
            : client.SubmitAsync(page, "confirm-identity", ("Password", password), ("Code", code));

    /// <summary>
    /// Presses the button of the page that deletes the account of the signed-in user, and
    /// follows where the answer leads. A user who has not just proved who they are is not shown
    /// that page: the form is then posted all the same, as something that is not a browser can.
    /// </summary>
    public static Task<(string Path, string Page)> DeleteAccountAsync(this HttpClient client) =>
        client.SubmitAsync(WebUiModule.DeleteAccountPath, "delete-account");

    /// <summary>The key of the setup that a page carries in its form, as the application sealed it.</summary>
    public static string SealedKeyIn(string page)
    {
        var key = Regex.Match(page, "name=\"SealedKey\" value=\"([^\"]+)\"");
        Assert.True(key.Success, $"The page has no form that turns two-factor authentication on:{Environment.NewLine}{page}");

        return key.Groups[1].Value;
    }

    /// <summary>The recovery codes that a page shows, in order.</summary>
    public static IReadOnlyList<string> RecoveryCodesIn(string page) =>
        [.. Regex.Matches(page, "<li><code>([A-Z2-7]{4}(?:-[A-Z2-7]{4}){3})</code></li>").Select(code => code.Groups[1].Value)];

    /// <summary>The cookies an answer sets, each as the browser is sent it.</summary>
    public static IReadOnlyList<string> CookiesSetBy(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies) ? [.. cookies] : [];

    /// <summary>
    /// A page as a visitor sees it. The framework adds a comment to each page that holds state
    /// for the browser, and each form carries a token against request forgery. Both are
    /// encrypted, and so different every time. So is the nonce of the one script that is
    /// written into a page, which is new for every answer.
    /// </summary>
    public static string AsSeen(string page) =>
        Regex.Replace(
            Regex.Replace(
                Regex.Replace(page, "<!--.*?-->", string.Empty, RegexOptions.Singleline),
                "name=\"__RequestVerificationToken\" value=\"[^\"]+\"",
                string.Empty),
            " nonce=\"[^\"]+\"",
            string.Empty);

    public static string AntiforgeryTokenIn(string page)
    {
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
        Assert.True(token.Success, $"The page has no form with a token against request forgery:{Environment.NewLine}{page}");

        return token.Groups[1].Value;
    }
}
