using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Web;
using Ecnanib.Accounts;
using Ecnanib.Notifications;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;
using Ecnanib.WebUi;
using Microsoft.AspNetCore.Hosting;
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
/// hold, with the email it sends kept by <see cref="RecordingEmail"/>.
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

    public IRegistration Registration => _application.Services.GetRequiredService<IRegistration>();

    public IFairUse FairUse => _application.Services.GetRequiredService<IFairUse>();

    public IPasswordReset PasswordReset => _application.Services.GetRequiredService<IPasswordReset>();

    public PasswordHasher Passwords => _application.Services.GetRequiredService<PasswordHasher>();

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
            new RegistrationRequest(email, accepted ?? EveryDocument), password, TestContext.Current.CancellationToken);

    /// <summary>Confirms a verification link, with the password that the link of a repeat registration asks for.</summary>
    public Task<EmailVerificationOutcome> ConfirmAsync(string token, string? newPassword = null) =>
        Registration.VerifyEmailAsync(token, newPassword, TestContext.Current.CancellationToken);

    /// <summary>Whether confirming a verification link verified the address.</summary>
    public async Task<bool> VerifyAsync(string token, string? newPassword = null) =>
        await ConfirmAsync(token, newPassword) == EmailVerificationOutcome.Verified;

    public Task<PasswordResetRequestOutcome> RequestResetAsync(string email) =>
        PasswordReset.RequestAsync(email, TestContext.Current.CancellationToken);

    public Task<PasswordResetOutcome> ResetAsync(string token, string newPassword) =>
        PasswordReset.ResetAsync(token, newPassword, TestContext.Current.CancellationToken);

    /// <summary>Registers an address and verifies it with the link it was sent.</summary>
    public async Task<StoredUser> RegisterVerifiedAsync(string email)
    {
        Assert.Equal(RegistrationOutcome.Accepted, (await RegisterAsync(email)).Outcome);
        Assert.True(await VerifyAsync(TokenIn(Email.Sent[^1])));

        return StoredUser.Of(Database, email)!;
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
internal sealed record StoredSession(Guid Id, Guid UserId, byte[] TokenHash, DateTimeOffset CreatedAt, DateTimeOffset LastActiveAt)
{
    /// <summary>Every session, oldest first.</summary>
    public static IReadOnlyList<StoredSession> All(TestDatabase database)
    {
        using var connection = database.Open();
        using var command = new NpgsqlCommand(
            "SELECT id, user_id, token_hash, created_at, last_active_at FROM sessions ORDER BY created_at, id", connection);
        using var reader = command.ExecuteReader();

        var sessions = new List<StoredSession>();
        while (reader.Read())
        {
            sessions.Add(new StoredSession(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetFieldValue<byte[]>(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetFieldValue<DateTimeOffset>(4)));
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

    /// <summary>The cookies an answer sets, each as the browser is sent it.</summary>
    public static IReadOnlyList<string> CookiesSetBy(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies) ? [.. cookies] : [];

    /// <summary>
    /// A page as a visitor sees it. The framework adds a comment to each page that holds state
    /// for the browser, and each form carries a token against request forgery. Both are
    /// encrypted, and so different every time.
    /// </summary>
    public static string AsSeen(string page) =>
        Regex.Replace(
            Regex.Replace(page, "<!--.*?-->", string.Empty, RegexOptions.Singleline),
            "name=\"__RequestVerificationToken\" value=\"[^\"]+\"",
            string.Empty);

    public static string AntiforgeryTokenIn(string page)
    {
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
        Assert.True(token.Success, $"The page has no form with a token against request forgery:{Environment.NewLine}{page}");

        return token.Groups[1].Value;
    }
}
