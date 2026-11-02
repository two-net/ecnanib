using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Ecnanib.Tests.Persistence;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Identity;

/// <summary>
/// A hub client that calls what a browser calls, and can do what a browser wouldn't, such as bringing a
/// secret and a different cookie. It renders nothing, so it can't prove what a page does: only what the
/// hub lets through. Render batches it receives are dropped, which the client logs and survives.
/// </summary>
internal sealed class CircuitClient : IAsyncDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private readonly HubConnection _connection;
    private readonly string _address;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CircuitClient(HubConnection connection, string address)
    {
        _connection = connection;
        _address = address;
        _connection.Closed += _ =>
        {
            _closed.TrySetResult();

            return Task.CompletedTask;
        };
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public bool IsConnected => _connection.State == HubConnectionState.Connected;

    /// <summary>Over long polling on the test host, which needs nothing but the application.</summary>
    public static Task<CircuitClient> ConnectAsync(AppFactory app, string? sessionCookie = null) =>
        ConnectAsync(
            new Uri(app.Server.BaseAddress, "/_blazor"),
            sessionCookie,
            options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
            });

    /// <summary>Over a real WebSocket, as a browser connects. The application has to be on Kestrel.</summary>
    public static Task<CircuitClient> ConnectOverWebSocketAsync(AppFactory app, string? sessionCookie = null) =>
        ConnectAsync(new Uri(KestrelAddress(app), "/_blazor"), sessionCookie, options => options.Transports = HttpTransportType.WebSockets);

    public static Uri KestrelAddress(AppFactory app) =>
        new(Assert.Single(app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses));

    /// <summary>Returns the secret, which is what rejoins it.</summary>
    public async Task<string> StartCircuitAsync() =>
        (await _connection.InvokeAsync<string>("StartCircuit", _address, _address, "[]", string.Empty, Cancellation))!;

    public Task<bool> ConnectCircuitAsync(string secret, string method = "ConnectCircuit") =>
        _connection.InvokeAsync<bool>(method, secret, Cancellation);

    /// <summary>A browser event, as far as the hub can tell. No such method exists, which doesn't matter here.</summary>
    public Task SendUserActionAsync() =>
        _connection.SendAsync("BeginInvokeDotNetFromJS", "1", "Ecnanib", "NoSuchMethod", 0L, "[]", Cancellation);

    public Task SendNavigationAsync() =>
        _connection.SendAsync("OnLocationChanged", _address, null, false, Cancellation);

    public Task SendRenderAcknowledgementAsync() =>
        _connection.SendAsync("OnRenderCompleted", 0L, null, Cancellation);

    /// <summary>
    /// Sent calls don't report back. The hub handles a connection's calls in order, so when this
    /// answers, everything sent before it has been dealt with.
    /// </summary>
    public Task SettleAsync() => _connection.InvokeAsync<bool>("ConnectCircuit", string.Empty, Cancellation);

    public Task UntilClosedAsync() => _closed.Task.WaitAsync(Deadline, Cancellation);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private static async Task<CircuitClient> ConnectAsync(Uri address, string? sessionCookie, Action<HttpConnectionOptions> configure)
    {
        var builder = new HubConnectionBuilder().WithUrl(address, options =>
        {
            configure(options);

            if (sessionCookie is not null)
            {
                options.Headers["Cookie"] = sessionCookie;
            }
        });

        builder.Services.AddSingleton<IHubProtocol, BlazorPackProtocol>();

        var client = new CircuitClient(builder.Build(), new Uri(address, "/").ToString());

        await client._connection.StartAsync(Cancellation);

        return client;
    }

    /// <summary>The hub accepts only this name. On the wire it's MessagePack, as far as these calls go.</summary>
    private sealed class BlazorPackProtocol : IHubProtocol
    {
        private readonly MessagePackHubProtocol _protocol = new();

        public string Name => "blazorpack";

        public int Version => _protocol.Version;

        public TransferFormat TransferFormat => _protocol.TransferFormat;

        public bool TryParseMessage(ref ReadOnlySequence<byte> input, IInvocationBinder binder, [NotNullWhen(true)] out HubMessage? message) =>
            _protocol.TryParseMessage(ref input, binder, out message);

        public void WriteMessage(HubMessage message, IBufferWriter<byte> output) => _protocol.WriteMessage(message, output);

        public ReadOnlyMemory<byte> GetMessageBytes(HubMessage message) => _protocol.GetMessageBytes(message);

        public bool IsVersionSupported(int version) => _protocol.IsVersionSupported(version);
    }
}
