using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Security;

/// <summary>
/// Stands in for the network. A test host has no connection, so RemoteIpAddress is null, and a null
/// peer skips the check on forwarded headers that these tests exist to prove.
/// </summary>
internal static class TestNetwork
{
    private const string PeerHeader = "X-Test-Peer";
    private const string AccountHeader = "X-Test-Account";

    /// <summary>Lets a request choose its peer address and signed-in account.</summary>
    public static void AddTestNetwork(this IServiceCollection services) =>
        services.AddTransient<IStartupFilter>(_ => new OuterMiddleware(context =>
        {
            if (context.Request.Headers[PeerHeader].FirstOrDefault() is { } peer)
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            }

            if (context.Request.Headers[AccountHeader].FirstOrDefault() is { } account)
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, account)], "Test"));
            }
        }));

    public static async Task<HttpResponseMessage> SendFromAsync(
        this HttpClient client,
        string path,
        string? peer = null,
        string? account = null,
        string? forwardedFor = null,
        string? forwardedProto = null,
        HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);

        Add(PeerHeader, peer);
        Add(AccountHeader, account);
        Add("X-Forwarded-For", forwardedFor);
        Add("X-Forwarded-Proto", forwardedProto);

        return await client.SendAsync(request, TestContext.Current.CancellationToken);

        void Add(string name, string? value)
        {
            if (value is not null)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }
    }
}
