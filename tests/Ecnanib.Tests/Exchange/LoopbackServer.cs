using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Ecnanib.Tests.Exchange;

/// <summary>
/// A bare HTTP/1.1 server on loopback, for what only the adapter's real handler does, such as following
/// redirects or sending a request again. <paramref name="respond"/> gets each request's head and returns
/// the whole response, or null to close the connection without answering.
/// </summary>
internal sealed class LoopbackServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentQueue<string> _requests = new();
    private readonly Func<string, string?> _respond;

    public LoopbackServer(Func<string, string?> respond)
    {
        _respond = respond;
        _listener.Start();
        _ = AcceptAsync();
    }

    public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    /// <summary>Each request's head: its request line and headers.</summary>
    public IReadOnlyList<string> Requests => [.. _requests];

    public static string Json(string body) =>
        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Stop();
        _stopping.Dispose();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                _ = ServeAsync(client);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Stopped.
        }
    }

    /// <summary>Keeps the connection open between requests, as a real server would, so the client reuses it.</summary>
    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

                while (true)
                {
                    var head = new StringBuilder();
                    var line = await reader.ReadLineAsync(_stopping.Token);

                    if (line is null)
                    {
                        return;
                    }

                    while (!string.IsNullOrEmpty(line))
                    {
                        head.Append(line).Append('\n');
                        line = await reader.ReadLineAsync(_stopping.Token);
                    }

                    _requests.Enqueue(head.ToString());

                    if (_respond(head.ToString()) is not { } response)
                    {
                        return;
                    }

                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stopping.Token);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
            {
                // The client or the test went away.
            }
        }
    }
}
