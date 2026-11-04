using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Ecnanib.Tests.Email;

/// <summary>
/// A real SMTP server that keeps what it receives, with an API to read it. It has no TLS, so it also
/// stands for a server that doesn't offer STARTTLS. It accepts mail only for <see cref="AcceptedDomain"/>,
/// so a test can make it refuse a recipient.
/// </summary>
public sealed class MailpitFixture : IAsyncLifetime
{
    public const string AcceptedDomain = "accepted.test";

    private const int SmtpPort = 1025;
    private const int ApiPort = 8025;

    private readonly IContainer _container = new ContainerBuilder("axllent/mailpit:v1.27")
        .WithPortBinding(SmtpPort, assignRandomHostPort: true)
        .WithPortBinding(ApiPort, assignRandomHostPort: true)
        .WithCommand("--smtp-allowed-recipients", $@"@{AcceptedDomain.Replace(".", @"\.", StringComparison.Ordinal)}$")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(ApiPort).ForPath("/api/v1/info")))
        .Build();

    private HttpClient? _api;

    public string Host => _container.Hostname;

    public int Port => _container.GetMappedPublicPort(SmtpPort);

    public static string NewAddress(string domain = AcceptedDomain) => $"{Guid.NewGuid():N}@{domain}";

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        _api = new HttpClient { BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(ApiPort)}") };
    }

    public async ValueTask DisposeAsync()
    {
        _api?.Dispose();
        await _container.DisposeAsync();
    }

    /// <summary>Every message the server holds for <paramref name="address"/>, with its text.</summary>
    public async Task<List<ReceivedMessage>> ReceivedByAsync(string address, CancellationToken cancellationToken)
    {
        var api = _api ?? throw new InvalidOperationException("The server hasn't started.");
        var list = await api.GetFromJsonAsync<MessageList>("/api/v1/messages?limit=500", cancellationToken);
        var received = new List<ReceivedMessage>();

        foreach (var summary in list!.Messages.Where(message => message.To.Any(to => to.Address == address)))
        {
            received.Add((await api.GetFromJsonAsync<ReceivedMessage>($"/api/v1/message/{summary.Id}", cancellationToken))!);
        }

        return received;
    }

    private sealed record MessageList([property: JsonPropertyName("messages")] List<MessageSummary> Messages);

    private sealed record MessageSummary(
        [property: JsonPropertyName("ID")] string Id,
        [property: JsonPropertyName("To")] List<Mailbox> To);
}

public sealed record Mailbox([property: JsonPropertyName("Address")] string Address);

public sealed record ReceivedMessage(
    [property: JsonPropertyName("From")] Mailbox From,
    [property: JsonPropertyName("To")] List<Mailbox> To,
    [property: JsonPropertyName("Subject")] string Subject,
    [property: JsonPropertyName("Text")] string Text,
    [property: JsonPropertyName("HTML")] string Html);
