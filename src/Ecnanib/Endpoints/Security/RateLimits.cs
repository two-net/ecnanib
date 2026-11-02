using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;

namespace Ecnanib.Endpoints.Security;

/// <summary>
/// NFR-SEC-07. The global limiter is on by default, so a new endpoint can't forget it. Traffic inside
/// a circuit never reaches it. See docs/architecture.md.
/// </summary>
internal static class RateLimits
{
    /// <summary>The policy for sign-in and anything else that checks a secret over HTTP.</summary>
    public const string Authentication = "authentication";

    private const string UnknownClient = "unknown";
    private const int Ipv6NetworkBytes = 8;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly RateLimitPartition<string> NoLimit = RateLimitPartition.GetNoLimiter(string.Empty);

    public static void Configure(RateLimiterOptions options, RateLimitSettings settings)
    {
        // The default is 503, which request logging records as an Error, so a flood would become
        // a database row per request.
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = WriteRetryAfterAsync;

        // Chained, because a single partition would let signed-in traffic escape the limit per address.
        options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<HttpContext, string>(context =>
                IsExempt(context) ? NoLimit : PerMinute(ClientKey(context.Connection.RemoteIpAddress), settings.RequestsPerMinute)),
            PartitionedRateLimiter.Create<HttpContext, string>(context =>
                !IsExempt(context) && AccountOf(context) is { } account ? PerMinute(account, settings.RequestsPerMinute) : NoLimit));

        options.AddPolicy(Authentication, context =>
            PerMinute(ClientKey(context.Connection.RemoteIpAddress), settings.AuthenticationRequestsPerMinute));
    }

    /// <summary>
    /// The bucket for an address. One IPv6 subscriber holds at least a /64, so taking whole addresses
    /// would give them endless buckets. A dual-stack socket reports IPv4 clients in mapped form.
    /// </summary>
    public static string ClientKey(IPAddress? address)
    {
        if (address is null)
        {
            return UnknownClient;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[Ipv6NetworkBytes..].Clear();

        return new IPAddress(bytes).ToString();
    }

    private static RateLimitPartition<string> PerMinute(string key, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = Window,
        });

    /// <summary>
    /// Throws rather than falling back, because a claim that doesn't match would otherwise leave
    /// every account limited only by address, and nothing would show it.
    /// </summary>
    private static string? AccountOf(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true
            ? context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? throw new InvalidOperationException("A signed-in user has no NameIdentifier claim to rate limit by.")
            : null;

    /// <summary>
    /// Long polling makes a hub request per message on a connection that's already been admitted, so
    /// limiting those would end live sessions. A connection is admitted by negotiating, or by a
    /// WebSocket upgrade, because SignalR accepts one that skips negotiation. The request behind the
    /// error page has already been counted, and a limit here would replace that page with an empty 429.
    /// </summary>
    private static bool IsExempt(HttpContext context)
    {
        if (context.Features.Get<IExceptionHandlerFeature>() is not null)
        {
            return true;
        }

        var metadata = context.GetEndpoint()?.Metadata;

        return metadata?.GetMetadata<HubMetadata>() is not null
            && metadata.GetMetadata<NegotiateMetadata>() is null
            && !context.WebSockets.IsWebSocketRequest;
    }

    private static ValueTask WriteRetryAfterAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        return ValueTask.CompletedTask;
    }
}
