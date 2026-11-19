using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.ExchangeGateway;

/// <summary>
/// Stands for the account event feeds of Binance: a WebSocket server on this machine, at an
/// address of its own, which takes the connections that the gateway opens, keeps what each was
/// sent, and says on each what the test has it say. No connection leaves the machine.
/// </summary>
/// <remarks>
/// On the Spot market Binance's feed is its WebSocket API, where a request is answered under its
/// id: this answers every request as taken, unless the test says otherwise.
/// </remarks>
internal sealed class FeedServer : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private readonly WebApplication _server;
    private readonly Channel<FeedSession> _sessions = Channel.CreateUnbounded<FeedSession>();
    private readonly List<FeedSession> _all = [];

    public FeedServer()
    {
        var builder = WebApplication.CreateSlimBuilder();

        // A port that the machine gives, on this machine alone, and nothing on the console,
        // which some tests read.
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _server = builder.Build();
        _server.UseWebSockets();
        _server.Run(ServeAsync);
        _server.StartAsync().GetAwaiter().GetResult();

        Address = new Uri("ws" + _server.Urls.Single()["http".Length..]);
    }

    /// <summary>The address of the server, as a setting of the configuration file takes it.</summary>
    public Uri Address { get; }

    /// <summary>The address of something under it.</summary>
    public string At(string path) => $"{Address.AbsoluteUri.TrimEnd('/')}/{path.TrimStart('/')}";

    /// <summary>The status that an attempt to connect is refused with, when the test says that it is.</summary>
    public int? Refuses { get; set; }

    /// <summary>
    /// What a request that a connection sends is answered with, or null for no answer. Unless a
    /// test says otherwise, a request is answered as one that Binance's WebSocket API took.
    /// </summary>
    public Func<JsonElement, string?> Answers { get; set; } = Taken;

    /// <summary>Every connection that the server took, in the order they came.</summary>
    public IReadOnlyList<FeedSession> Sessions
    {
        get
        {
            lock (_all)
            {
                return [.. _all];
            }
        }
    }

    /// <summary>The answer of Binance's WebSocket API to a request that it took.</summary>
    public static string Taken(JsonElement request) =>
        $$"""{ "id": "{{request.GetProperty("id").GetString()}}", "status": 200, "result": { "subscriptionId": 0 }, "rateLimits": [] }""";

    /// <summary>The answer of Binance's WebSocket API to a request that it refused, with a status and one of its errors.</summary>
    public static Func<JsonElement, string?> Refused(int status, int code) => request =>
        $$"""{ "id": "{{request.GetProperty("id").GetString()}}", "status": {{status}}, "error": { "code": {{code}}, "msg": "Refused." } }""";

    /// <summary>The next connection that the server takes, or took and that no test has had yet.</summary>
    public async Task<FeedSession> NextAsync()
    {
        using var patience = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        patience.CancelAfter(Patience);

        return await _sessions.Reader.ReadAsync(patience.Token);
    }

    public void Dispose()
    {
        foreach (var session in Sessions)
        {
            session.Break();
        }

        _server.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        _server.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private async Task ServeAsync(HttpContext context)
    {
        if (Refuses is { } status)
        {
            context.Response.StatusCode = status;

            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;

            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var session = new FeedSession($"{context.Request.Path}{context.Request.QueryString}", socket, request => Answers(request));

        lock (_all)
        {
            _all.Add(session);
        }

        await _sessions.Writer.WriteAsync(session, context.RequestAborted);
        await session.ServeAsync(context.RequestAborted);
    }
}

/// <summary>One connection that a <see cref="FeedServer"/> took.</summary>
internal sealed class FeedSession(string path, WebSocket socket, Func<JsonElement, string?> answers)
{
    private readonly List<string> _received = [];
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The path that the connection was opened at, with its query.</summary>
    public string Path => path;

    /// <summary>Everything that the connection was sent, in the order it came.</summary>
    public IReadOnlyList<string> Received
    {
        get
        {
            lock (_received)
            {
                return [.. _received];
            }
        }
    }

    /// <summary>Ends when the connection has: the other side closed it or let it go, or the test did.</summary>
    public Task Ended => _ended.Task;

    /// <summary>Says something on the connection, as Binance says an event.</summary>
    public async Task SayAsync(string json)
    {
        await _sending.WaitAsync(TestContext.Current.CancellationToken);

        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, TestContext.Current.CancellationToken);
        }
        finally
        {
            _sending.Release();
        }
    }

    /// <summary>Closes the connection, as Binance does with every connection after a day.</summary>
    public async Task CloseAsync()
    {
        await _sending.WaitAsync(TestContext.Current.CancellationToken);

        try
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Bye.", TestContext.Current.CancellationToken);
        }
        finally
        {
            _sending.Release();
        }
    }

    /// <summary>Breaks the connection off, as a network does.</summary>
    public void Break() => socket.Abort();

    public async Task ServeAsync(CancellationToken stopping)
    {
        var buffer = new byte[16 * 1024];

        try
        {
            while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult received;

                do
                {
                    received = await socket.ReceiveAsync(buffer, stopping);
                    message.Write(buffer, 0, received.Count);
                }
                while (!received.EndOfMessage);

                if (received.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                var text = Encoding.UTF8.GetString(message.ToArray());

                lock (_received)
                {
                    _received.Add(text);
                }

                using var request = JsonDocument.Parse(text);

                if (answers(request.RootElement) is { } answer)
                {
                    await SayAsync(answer);
                }
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or IOException)
        {
            // The other side let the connection go.
        }
        finally
        {
            _ended.TrySetResult();
        }
    }
}
