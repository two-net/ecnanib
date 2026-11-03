using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Audit;

/// <summary>
/// FR-AUD-02's address, user agent and correlation ID. A request has them at hand, but a circuit has no
/// request to read them from, and a job acts for nobody even when a request starts it.
/// </summary>
public sealed class ClientOriginTests(PostgresFixture postgres)
{
    /// <summary>Loopback is a trusted proxy, so the application takes the client's address from this.</summary>
    private const string ForwardedAddress = "203.0.113.7";

    private const string UserAgent = "Audit Browser/1.0";

    private static readonly Dictionary<string, string> ClientHeaders = new()
    {
        ["X-Forwarded-For"] = ForwardedAddress,
        ["User-Agent"] = UserAgent,
    };

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_request_is_audited_with_its_client_and_correlation_id()
    {
        await using var app = KestrelApp();
        using var visitor = Visitor.ToAddress(CircuitClient.KestrelAddress(app));
        var email = Visitor.NewEmail();

        foreach (var (name, value) in ClientHeaders)
        {
            visitor.Client.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
        }

        (await visitor.RegisterAsync(email)).Dispose();
        using var response = await visitor.SignInAsync(email);
        var correlationId = Assert.Single(response.Headers.GetValues("X-Correlation-ID"));

        var row = Assert.Single(await AuditRows.WithCorrelationAsync(app.ConnectionString(), correlationId, Cancellation));

        Assert.Equal("SignedIn", row.Action);
        Assert.Equal(ForwardedAddress, row.IpAddress);
        Assert.Equal(UserAgent, row.UserAgent);
    }

    /// <summary>A WebSocket is one long request, which a browser opens with its own headers.</summary>
    [Fact]
    public async Task An_action_on_a_circuit_is_audited_with_the_connections_client()
    {
        await using var app = KestrelApp();
        await using var connection = await CircuitClient.ConnectOverWebSocketAsync(app, headers: ClientHeaders);
        await connection.OpenCircuitAsync();

        var row = Assert.Single(await AuditProbe.RecordFromCircuitAsync(app, connection));

        Assert.Equal(ForwardedAddress, row.IpAddress);
        Assert.Equal(UserAgent, row.UserAgent);
        Assert.NotNull(row.CorrelationId);
    }

    /// <summary>The transport a browser falls back to, which carries each call on a request of its own.</summary>
    [Fact]
    public async Task An_action_on_a_long_polling_circuit_is_audited_with_the_connections_client()
    {
        await using var app = AccountApp.Create(postgres);
        await using var connection = await CircuitClient.ConnectAsync(app, headers: ClientHeaders);
        await connection.OpenCircuitAsync();

        var row = Assert.Single(await AuditProbe.RecordFromCircuitAsync(app, connection));

        // The test host has no peer address, which the framework takes for a local proxy, so it trusts the header.
        Assert.Equal(ForwardedAddress, row.IpAddress);
        Assert.Equal(UserAgent, row.UserAgent);
        Assert.NotNull(row.CorrelationId);
    }

    [Fact]
    public async Task Each_action_on_a_circuit_has_its_own_correlation_id()
    {
        await using var app = AccountApp.Create(postgres);
        await using var connection = await CircuitClient.ConnectAsync(app);
        await connection.OpenCircuitAsync();

        var first = Assert.Single(await AuditProbe.RecordFromCircuitAsync(app, connection));
        var second = Assert.Single(await AuditProbe.RecordFromCircuitAsync(app, connection));

        Assert.NotEqual(first.CorrelationId, second.CorrelationId);
    }

    [Fact]
    public async Task A_job_started_by_a_client_is_audited_without_it()
    {
        await using var app = KestrelApp();
        await using var connection = await CircuitClient.ConnectOverWebSocketAsync(app, headers: ClientHeaders);
        await connection.OpenCircuitAsync();

        var rows = await AuditProbe.RecordFromCircuitAsync(app, connection, alsoInJob: true);

        Assert.Equal(2, rows.Count);
        Assert.Equal((ForwardedAddress, UserAgent), (rows[0].IpAddress, rows[0].UserAgent));
        Assert.Null(rows[1].IpAddress);
        Assert.Null(rows[1].UserAgent);
        Assert.NotNull(rows[1].CorrelationId);
        Assert.NotEqual(rows[0].CorrelationId, rows[1].CorrelationId);
    }

    private AppFactory KestrelApp()
    {
        var app = AccountApp.Create(postgres);

        app.UseKestrel(0);
        app.StartServer();

        return app;
    }
}
