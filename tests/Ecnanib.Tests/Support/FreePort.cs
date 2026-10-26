using System.Net;
using System.Net.Sockets;

namespace Ecnanib.Tests.Support;

internal static class FreePort
{
    /// <summary>
    /// A loopback port nothing listens on right now. The small race with another process is accepted: a collision shows
    /// up as the application's own "Cannot listen on" line in the captured output.
    /// </summary>
    public static int Next()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
