using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Ecnanib.ExchangeGateway.Adapters.Binance;

/// <summary>
/// One connection to an account event feed of Binance (FR-BIN-7): a WebSocket, on which
/// <see cref="BinanceFeed"/> reads the events of one account. It is the one type of the gateway
/// that opens a WebSocket. See "The account event feed" in docs/exchange.md.
/// </summary>
/// <remarks>
/// <para>
/// It sends one thing at most: the request that subscribes to the events of an account, on the
/// Spot market. After that it only receives. It has no way to send anything else, so the feed
/// is no second way to an order beside the calls of the orders (NFR-SEC-6, FR-BIN-9).
/// </para>
/// <para>
/// What happens on the network leaves here as how the connection ended, and never as an error of
/// the network. Its address may hold the key of a stream, so nothing here logs it.
/// </para>
/// </remarks>
internal sealed class BinanceFeedConnection : IDisposable
{
    // The longest message that is read. An event of an account is a few hundred bytes: a
    // message that is longer than this is not one, and is not kept in memory to find out.
    private const int LongestMessage = 256 * 1024;

    private readonly ClientWebSocket _socket;
    private bool _subscribed;

    private BinanceFeedConnection(ClientWebSocket socket) => _socket = socket;

    /// <summary>Why the connection ended, once <see cref="ReceiveAsync"/> has given nothing.</summary>
    public BinanceFeedEnd Ended { get; private set; } = BinanceFeedEnd.Closed;

    /// <summary>
    /// Opens a connection to an address, within a time limit.
    /// </summary>
    /// <param name="address">The address, which may hold the key of a stream and is in no log.</param>
    /// <param name="within">How long Binance may take.</param>
    /// <param name="clock">The clock that the limit runs on.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <returns>The connection, or none with the status that Binance refused the attempt with, where it answered with one.</returns>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public static async Task<(BinanceFeedConnection? Connection, int? Status)> OpenAsync(
        Uri address, TimeSpan within, TimeProvider clock, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();

        // The status of an attempt that Binance refuses says whether it has had too many.
        socket.Options.CollectHttpResponseDetails = true;

        using var limit = new CancellationTokenSource(within, clock);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);

        try
        {
            await socket.ConnectAsync(address, either.Token);

            return (new BinanceFeedConnection(socket), null);
        }
        catch (Exception error) when (error is WebSocketException or IOException or OperationCanceledException or InvalidOperationException)
        {
            var status = socket.HttpStatusCode != 0 ? (int?)(int)socket.HttpStatusCode : null;

            socket.Dispose();
            cancellationToken.ThrowIfCancellationRequested();

            return (null, status);
        }
    }

    /// <summary>
    /// Sends the one request of a connection: the one that subscribes to the events of an
    /// account. A connection sends nothing else, and nothing a second time.
    /// </summary>
    /// <returns>Whether it was sent.</returns>
    /// <exception cref="InvalidOperationException">The connection has sent its request already.</exception>
    public async Task<bool> SubscribeAsync(string request, CancellationToken cancellationToken)
    {
        if (_subscribed)
        {
            throw new InvalidOperationException("A connection of the account event feed sends one request, which subscribes, and nothing else.");
        }

        _subscribed = true;

        try
        {
            await _socket.SendAsync(Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

            return true;
        }
        catch (Exception error) when (error is WebSocketException or IOException or InvalidOperationException)
        {
            Ended = BinanceFeedEnd.Broken;

            return false;
        }
    }

    /// <summary>
    /// Receives the next message as the JSON that Binance sends, or nothing when the connection
    /// has ended: <see cref="Ended"/> then says why.
    /// </summary>
    /// <returns>The message. It is <see cref="JsonValueKind.Undefined"/> for one that is no JSON, or is longer than a message is.</returns>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public async Task<JsonElement?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];
        using var message = new MemoryStream();
        var tooLong = false;

        try
        {
            while (true)
            {
                var received = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);

                if (received.MessageType == WebSocketMessageType.Close)
                {
                    Ended = BinanceFeedEnd.Closed;

                    return null;
                }

                if (message.Length + received.Count > LongestMessage)
                {
                    tooLong = true;
                }
                else
                {
                    message.Write(buffer, 0, received.Count);
                }

                if (received.EndOfMessage)
                {
                    break;
                }
            }
        }
        catch (Exception error) when (error is WebSocketException or IOException or InvalidOperationException)
        {
            Ended = BinanceFeedEnd.Broken;

            return null;
        }

        if (tooLong)
        {
            return default(JsonElement);
        }

        try
        {
            using var read = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));

            return read.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default(JsonElement);
        }
    }

    public void Dispose()
    {
        // Without a goodbye: whoever ends a connection does not wait for Binance to agree.
        _socket.Abort();
        _socket.Dispose();
    }
}

/// <summary>Why a connection of the account event feed ended.</summary>
internal enum BinanceFeedEnd
{
    /// <summary>Binance closed it, as it does with every connection after a day.</summary>
    Closed,

    /// <summary>The connection broke.</summary>
    Broken,

    /// <summary>Binance said that the stream of the account has ended: its key expired, or the subscription was terminated.</summary>
    Expired,

    /// <summary>Nobody said for a while that the feed is still wanted.</summary>
    NotKept,

    /// <summary>The application stops.</summary>
    Stopped,
}
