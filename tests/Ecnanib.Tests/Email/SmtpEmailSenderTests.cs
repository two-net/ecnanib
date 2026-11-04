using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Email;

/// <summary>EXT-EML-01, against a real SMTP server. Sending runs as a job, so these wait for it.</summary>
public sealed class SmtpEmailSenderTests(PostgresFixture postgres, MailpitFixture mailpit) : IClassFixture<MailpitFixture>
{
    private const string JobName = "send-email";
    private const string From = "no-reply@example.test";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_message_arrives_as_plain_text_from_the_configured_address()
    {
        await using var app = App(useTls: false);
        app.Start();
        var to = MailpitFixture.NewAddress();

        Sender(app).Send(new EmailMessage { To = to, Subject = "A subject", TextBody = "First line\nhttps://ecnanib.test/x?token=abc" });

        var message = await ArrivalAsync(to);

        Assert.Equal(From, message.From.Address);
        Assert.Equal([to], message.To.Select(mailbox => mailbox.Address));
        Assert.Equal("A subject", message.Subject);
        Assert.Contains("https://ecnanib.test/x?token=abc", message.Text, StringComparison.Ordinal);
        Assert.Empty(message.Html);
        Assert.Equal(["Succeeded"], await RunsAsync(app));
    }

    /// <summary>A reset link must never travel in clear because a server, or something between, dropped STARTTLS.</summary>
    [Fact]
    public async Task With_tls_on_a_server_without_starttls_gets_nothing()
    {
        await using var app = App(useTls: true);
        app.Start();
        var to = MailpitFixture.NewAddress();

        Sender(app).Send(new EmailMessage { To = to, Subject = "Never sent", TextBody = "Never sent" });

        await LoggedApp.UntilAsync(async () => await RunsAsync(app) is [not "Running"]);

        Assert.Equal(["Failed"], await RunsAsync(app));
        Assert.Empty(await mailpit.ReceivedByAsync(to, Cancellation));
    }

    /// <summary>A server's refusal usually repeats the address, and logs and job runs keep neither addresses nor bodies.</summary>
    [Fact]
    public async Task A_refused_recipient_fails_the_run_without_logging_the_address_or_body()
    {
        var connectionString = postgres.NewConnectionString();
        await using var logged = new LoggedApp(connectionString, settings: Settings(useTls: false));
        var to = MailpitFixture.NewAddress("refused.test");
        var body = $"body{Guid.NewGuid():N}";

        Sender(logged.App).Send(new EmailMessage { To = to, Subject = "Refused", TextBody = body });

        await LoggedApp.UntilAsync(async () => (await RunsAsync(connectionString)) is [not "Running"]);

        var details = await Sql.ColumnAsync(connectionString, $"SELECT coalesce(details, '') FROM job_runs WHERE job_name = '{JobName}'", Cancellation);

        await logged.StopAsync();

        var logs = logged.FileText() + await logged.TableTextAsync(Cancellation);

        Assert.Equal(["Failed"], await RunsAsync(connectionString));
        Assert.Contains("refused the message with status", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(to, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(body, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(to, details.Single(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task With_email_off_sending_is_a_mistake_that_throws()
    {
        await using var app = AccountApp.Create(postgres);
        app.Start();

        Assert.Throws<InvalidOperationException>(() =>
            Sender(app).Send(new EmailMessage { To = MailpitFixture.NewAddress(), Subject = "No", TextBody = "No" }));
    }

    private static IEmailSender Sender(AppFactory app) => app.Services.GetRequiredService<IEmailSender>();

    private static Task<List<string>> RunsAsync(AppFactory app) => RunsAsync(app.ConnectionString());

    private static Task<List<string>> RunsAsync(string connectionString) =>
        Sql.ColumnAsync(connectionString, $"SELECT status FROM job_runs WHERE job_name = '{JobName}' ORDER BY id", Cancellation);

    private async Task<ReceivedMessage> ArrivalAsync(string to)
    {
        List<ReceivedMessage> received = [];

        await LoggedApp.UntilAsync(async () => (received = await mailpit.ReceivedByAsync(to, Cancellation)).Count > 0);

        return Assert.Single(received);
    }

    private AppFactory App(bool useTls) => AccountApp.Create(postgres, Settings(useTls));

    private Dictionary<string, string?> Settings(bool useTls) =>
        AccountApp.EmailOn(new()
        {
            ["Email:SmtpHost"] = mailpit.Host,
            ["Email:SmtpPort"] = mailpit.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Email:UseTls"] = useTls ? "true" : "false",
            ["Email:FromAddress"] = From,
        });
}
