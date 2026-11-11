using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Ecnanib.Notifications;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecnanib.Tests.Notifications;

/// <summary>
/// Email is delivered through an adapter behind the common interface (NFR-ARC-3). These tests
/// send through the adapter the application registers, over SMTP, to a mail server in a container.
/// </summary>
public class SmtpEmailAdapterTests
{
    private const string AdapterArea = "Ecnanib.Notifications.Adapters.Smtp.SmtpEmailAdapter";

    [Fact]
    public async Task A_message_reaches_the_mail_server_from_the_sender_in_the_configuration_file()
    {
        using var application = ApplicationSendingTo(MailServer.Host, MailServer.SmtpPort, signsIn: false);
        var email = application.Services.GetRequiredService<IEmailAdapter>();
        var to = NewAddress();

        await email.SendAsync(
            new EmailMessage(to, "Confirm your email address — ✓", "First line.\n\nhttps://ecnanib.example/verify-email?token=abc\n"),
            TestContext.Current.CancellationToken);

        var received = Assert.Single(await MailServer.ReceivedByAsync(to));
        Assert.Equal("noreply@ecnanib.example", received.FromAddress);
        Assert.Equal("Ecnanib Test", received.FromName);
        Assert.Equal("Confirm your email address — ✓", received.Subject);
        Assert.Contains("First line.", received.Text);
        Assert.Contains("https://ecnanib.example/verify-email?token=abc", received.Text);

        // Logged with its outcome and duration, and without the address.
        var sent = Assert.Single(application.Events(), written => written.Area == AdapterArea);
        Assert.Equal("Information", sent.Level);
        Assert.Equal("Succeeded", sent.Text("Outcome"));
        Assert.InRange(sent["DurationMs"].GetDouble(), 0, 60_000);
        Assert.DoesNotContain(to, application.LogText());
    }

    [Fact]
    public async Task A_sign_in_is_not_sent_to_a_server_that_offers_no_encryption()
    {
        // The server in the container offers none. The file names a user and a password.
        using var application = ApplicationSendingTo(MailServer.Host, MailServer.SmtpPort, signsIn: true);
        var email = application.Services.GetRequiredService<IEmailAdapter>();
        var to = NewAddress();

        var refused = await Assert.ThrowsAnyAsync<Exception>(() =>
            email.SendAsync(new EmailMessage(to, "Subject", "Body"), TestContext.Current.CancellationToken));

        Assert.Contains("STARTTLS", refused.Message);
        Assert.Empty(await MailServer.ReceivedByAsync(to));

        var failed = Assert.Single(application.Events(), written => written.Area == AdapterArea);
        Assert.Equal("Warning", failed.Level);
        Assert.Equal("Failed", failed.Text("Outcome"));
        Assert.DoesNotContain("email-password", application.LogText());
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_fails_the_send()
    {
        // The port of the mail server's inbox page, which does not speak SMTP, would hang; a
        // port nothing listens on refuses at once.
        using var application = ApplicationSendingTo("127.0.0.1", port: 1, signsIn: false);
        var email = application.Services.GetRequiredService<IEmailAdapter>();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            email.SendAsync(new EmailMessage(NewAddress(), "Subject", "Body"), TestContext.Current.CancellationToken));

        var failed = Assert.Single(application.Events(), written => written.Area == AdapterArea);
        Assert.Equal("Warning", failed.Level);
        Assert.Equal("Failed", failed.Text("Outcome"));
        Assert.NotNull(failed.Exception);
    }

    private static LoggedApplication ApplicationSendingTo(string host, int port, bool signsIn)
    {
        var settings = LoggedApplication.Settings().With("Email:Host", host).With("Email:Port", port);

        return new LoggedApplication(signsIn ? settings : settings.With("Email:User", "").With("Email:Password", ""));
    }

    private static string NewAddress() => $"{Guid.NewGuid():N}@example.com";
}

/// <summary>
/// A mail server for the tests: one container for the whole test process, started the first
/// time a test needs it and removed when the process ends. It takes mail over SMTP without
/// encryption or sign-in, delivers it nowhere, and shows what it took over HTTP.
/// </summary>
internal static class MailServer
{
    // The image that compose.yaml uses for development.
    private const string Image = "axllent/mailpit:v1.33";

    private const int SmtpPortInside = 1025;
    private const int HttpPortInside = 8025;

    private static readonly Lazy<IContainer> Container = new(Start);

    public static string Host => Container.Value.Hostname;

    public static int SmtpPort => Container.Value.GetMappedPublicPort(SmtpPortInside);

    /// <summary>The messages the server has taken for an address.</summary>
    public static async Task<IReadOnlyList<ReceivedMessage>> ReceivedByAsync(string address)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://{Host}:{Container.Value.GetMappedPublicPort(HttpPortInside)}/api/v1/"),
        };

        var found = await client.GetFromJsonAsync<JsonElement>(
            $"search?query={Uri.EscapeDataString($"to:{address}")}", cancellationToken);

        var messages = new List<ReceivedMessage>();
        foreach (var summary in found.GetProperty("messages").EnumerateArray())
        {
            var message = await client.GetFromJsonAsync<JsonElement>(
                $"message/{summary.GetProperty("ID").GetString()}", cancellationToken);

            messages.Add(new ReceivedMessage(
                message.GetProperty("From").GetProperty("Address").GetString()!,
                message.GetProperty("From").GetProperty("Name").GetString()!,
                message.GetProperty("Subject").GetString()!,
                message.GetProperty("Text").GetString()!));
        }

        return messages;
    }

    private static IContainer Start()
    {
        var container = new ContainerBuilder(Image)
            .WithPortBinding(SmtpPortInside, assignRandomHostPort: true)
            .WithPortBinding(HttpPortInside, assignRandomHostPort: true)
            .WithWaitStrategy(DotNet.Testcontainers.Builders.Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPort(HttpPortInside).ForPath("/readyz")))

            // The container library writes to the console, which some tests read.
            .WithLogger(NullLogger.Instance)
            .Build();

        // On a thread of its own: a test's thread must not be what the start waits to come back to.
        Task.Run(() => container.StartAsync()).GetAwaiter().GetResult();

        return container;
    }
}

/// <summary>A message as the mail server took it.</summary>
internal sealed record ReceivedMessage(string FromAddress, string FromName, string Subject, string Text);
