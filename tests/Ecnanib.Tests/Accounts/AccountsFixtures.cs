using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Web;
using Ecnanib.Accounts;
using Ecnanib.Notifications;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;
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

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromException(failure);
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
    public AccountsApplication(
        bool registrationOpen = true,
        TestClock? clock = null,
        TestDatabase? database = null,
        Func<SettingsJson, SettingsJson>? settings = null)
    {
        Database = database ?? PostgresServer.CreateDatabase();

        var file = LoggedApplication.Settings().With("Application:RegistrationOpen", registrationOpen);

        _application = new LoggedApplication(
            settings?.Invoke(file) ?? file,
            host => host.ConfigureServices(services =>
            {
                services.AddSingleton<IEmailAdapter>(Email);

                if (clock is not null)
                {
                    services.AddSingleton<TimeProvider>(clock);
                }
            }),
            Database);
    }

    public TestDatabase Database { get; }

    public RecordingEmail Email { get; } = new();

    public IRegistration Registration => _application.Services.GetRequiredService<IRegistration>();

    public IFairUse FairUse => _application.Services.GetRequiredService<IFairUse>();

    public PasswordHasher Passwords => _application.Services.GetRequiredService<PasswordHasher>();

    /// <summary>Every document in the version in force: what the form posts when all boxes are ticked.</summary>
    public static IReadOnlyCollection<AcceptedDocument> EveryDocument =>
        [.. LegalDocuments.All.Select(document => new AcceptedDocument(document, LegalDocuments.CurrentVersionOf(document)))];

    public HttpClient CreateClient() => _application.CreateClient();

    public IReadOnlyList<LoggedEvent> Events() => _application.Events();

    public string LogText() => _application.LogText();

    public Task<RegistrationResult> RegisterAsync(
        string email, string password = Password, IReadOnlyCollection<AcceptedDocument>? accepted = null) =>
        Registration.RegisterAsync(
            new RegistrationRequest(email, accepted ?? EveryDocument), password, TestContext.Current.CancellationToken);

    public Task<bool> VerifyAsync(string token) =>
        Registration.VerifyEmailAsync(token, TestContext.Current.CancellationToken);

    /// <summary>Registers an address and verifies it with the link it was sent.</summary>
    public async Task<StoredUser> RegisterVerifiedAsync(string email)
    {
        Assert.Equal(RegistrationOutcome.Accepted, (await RegisterAsync(email)).Outcome);
        Assert.True(await VerifyAsync(TokenIn(Email.Sent[^1])));

        return StoredUser.Of(Database, email)!;
    }

    /// <summary>The verification link in a message.</summary>
    public static Uri LinkIn(EmailMessage message)
    {
        var link = Regex.Match(message.TextBody, @"^https?://\S+$", RegexOptions.Multiline);
        Assert.True(link.Success, $"The message has no link on a line of its own:{Environment.NewLine}{message.TextBody}");

        return new Uri(link.Value);
    }

    /// <summary>The token of the verification link in a message.</summary>
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
    DateTimeOffset? TokenExpiresAt)
{
    /// <summary>Every account, oldest first.</summary>
    public static IReadOnlyList<StoredUser> All(TestDatabase database)
    {
        using var connection = database.Open();
        using var command = new NpgsqlCommand(
            """
            SELECT id, email, password_hash, registered_at, email_verified_at,
                   email_verification_token_hash, email_verification_expires_at
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
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return users;
    }

    /// <summary>The account of an address, or null when it has none.</summary>
    public static StoredUser? Of(TestDatabase database, string email) =>
        All(database).SingleOrDefault(user => user.Email == email);
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
        using var content = new FormUrlEncodedContent(
        [
            new("_handler", form),
            new("__RequestVerificationToken", antiforgeryToken),
            .. fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value)),
        ]);

        using var response = await client.PostAsync(path, content, TestContext.Current.CancellationToken);
        var answer = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return (response.RequestMessage!.RequestUri!.AbsolutePath, answer);
    }

    public static string AntiforgeryTokenIn(string page)
    {
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
        Assert.True(token.Success, $"The page has no form with a token against request forgery:{Environment.NewLine}{page}");

        return token.Groups[1].Value;
    }
}
