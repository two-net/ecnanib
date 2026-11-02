using System.Reflection;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Tests.Persistence;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Identity;

/// <summary>
/// FR-AUTH-07 on a circuit, which HTTP can't see. The issue's point is that a revoked user mustn't keep
/// working over the open connection, and a client that isn't a browser ignores anything advisory, so
/// these talk to the real hub and check what it lets through. See Sessions in docs/architecture.md.
/// </summary>
public sealed class CircuitAccessTests(SharedAccountApp app) : IClassFixture<SharedAccountApp>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private ISessions Sessions => app.Services.GetRequiredService<ISessions>();

    public static TheoryData<string, Type?> HubMethods => new()
    {
        { "StartCircuit", typeof(ValueTask<string>) },
        { "ResumeCircuit", typeof(ValueTask<string>) },
        { "ConnectCircuit", typeof(ValueTask<bool>) },
        { "BeginInvokeDotNetFromJS", null },
        { "OnLocationChanged", null },
        { "OnRenderCompleted", null },
    };

    /// <summary>
    /// These names and types aren't public, and the filter matches on them. Without this, a framework
    /// upgrade that renamed one would quietly stop the check it guards. If this fails, read
    /// SessionHubFilter against the new ComponentHub.
    /// </summary>
    [Theory]
    [MemberData(nameof(HubMethods))]
    public void The_hub_still_has_the_methods_the_filter_names(string name, Type? returns)
    {
        var hub = typeof(ServerComponentsEndpointOptions).Assembly.GetType("Microsoft.AspNetCore.Components.Server.ComponentHub");

        Assert.NotNull(hub);

        var method = hub.GetMethod(name, BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(method);

        if (returns is not null)
        {
            Assert.Equal(returns, method.ReturnType);
        }
    }

    /// <summary>A secret is the first argument, and SessionHubFilter reads it from there.</summary>
    [Fact]
    public void A_circuit_is_rejoined_with_its_secret_alone()
    {
        var hub = typeof(ServerComponentsEndpointOptions).Assembly.GetType("Microsoft.AspNetCore.Components.Server.ComponentHub");

        var parameter = Assert.Single(hub!.GetMethod("ConnectCircuit")!.GetParameters());

        Assert.Equal(typeof(string), parameter.ParameterType);
    }

    [Fact]
    public async Task Revoking_a_session_closes_its_connection_and_no_other()
    {
        using var here = Visitor.To(app);
        var email = await here.SignUpAsync();
        using var there = await SignInAgainAsync(email);
        var sessions = await SessionsAsync(email);

        await using var connection = await CircuitClient.ConnectAsync(app, here.SessionCookie);
        await using var other = await CircuitClient.ConnectAsync(app, there.SessionCookie);
        await connection.StartCircuitAsync();
        await other.StartCircuitAsync();

        Assert.True(await Sessions.RevokeAsync(await UserIdAsync(email), sessions[0], Cancellation));

        await connection.UntilClosedAsync();
        Assert.True(other.IsConnected);
    }

    /// <summary>Every tab of a browser shares a cookie, so this is what signing out has to reach.</summary>
    [Fact]
    public async Task Signing_out_closes_the_other_tabs()
    {
        using var visitor = Visitor.To(app);
        await visitor.SignUpAsync();

        await using var firstTab = await CircuitClient.ConnectAsync(app, visitor.SessionCookie);
        await using var secondTab = await CircuitClient.ConnectAsync(app, visitor.SessionCookie);

        (await visitor.SignOutAsync()).Dispose();

        await firstTab.UntilClosedAsync();
        await secondTab.UntilClosedAsync();
    }

    [Fact]
    public async Task Changing_the_password_closes_other_sessions_and_keeps_this_one()
    {
        using var here = Visitor.To(app);
        using var there = await SignInAgainAsync(await here.SignUpAsync());

        await using var thisDevice = await CircuitClient.ConnectAsync(app, here.SessionCookie);
        await using var otherDevice = await CircuitClient.ConnectAsync(app, there.SessionCookie);
        await thisDevice.StartCircuitAsync();

        (await here.ChangePasswordAsync(Visitor.Password, "a new password of some length")).Dispose();

        await otherDevice.UntilClosedAsync();
        await thisDevice.SettleAsync();
        Assert.True(thisDevice.IsConnected);
    }

    [Fact]
    public async Task A_circuit_can_be_rejoined_by_its_own_session()
    {
        using var visitor = Visitor.To(app);
        await visitor.SignUpAsync();

        await using var first = await CircuitClient.ConnectAsync(app, visitor.SessionCookie);
        var secret = await first.StartCircuitAsync();

        await using var second = await CircuitClient.ConnectAsync(app, visitor.SessionCookie);

        Assert.True(await second.ConnectCircuitAsync(secret));
    }

    /// <summary>
    /// What the client does straight after a revocation, and what stops one that kept the secret. It
    /// also stops one that withholds render acknowledgements, because that needs a connection too.
    /// </summary>
    [Fact]
    public async Task A_circuit_cannot_be_rejoined_after_its_session_ends()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();

        await using var connection = await CircuitClient.ConnectAsync(app, visitor.SessionCookie);
        var secret = await connection.StartCircuitAsync();

        Assert.True(await Sessions.RevokeAsync(await UserIdAsync(email), Assert.Single(await SessionsAsync(email)), Cancellation));

        await connection.UntilClosedAsync();

        await using var withTheDeadCookie = await CircuitClient.ConnectAsync(app, visitor.SessionCookie);
        await using var withNoCookie = await CircuitClient.ConnectAsync(app);

        Assert.False(await withTheDeadCookie.ConnectCircuitAsync(secret));
        Assert.False(await withNoCookie.ConnectCircuitAsync(secret));
    }

    /// <summary>
    /// The framework would allow this, and hand the circuit the new user without re-initializing its
    /// components, so the page would keep the first user's data and the IDs it captured.
    /// </summary>
    [Fact]
    public async Task Someone_elses_session_cannot_take_over_a_circuit()
    {
        using var victim = Visitor.To(app);
        using var attacker = Visitor.To(app);
        await victim.SignUpAsync();
        await attacker.SignUpAsync();

        await using var theirs = await CircuitClient.ConnectAsync(app, victim.SessionCookie);
        var secret = await theirs.StartCircuitAsync();

        await using var mine = await CircuitClient.ConnectAsync(app, attacker.SessionCookie);

        Assert.False(await mine.ConnectCircuitAsync(secret));
    }

    /// <summary>
    /// A connection with no session would otherwise start a circuit, and one with a session would give
    /// it an identity. Revoking that session couldn't then reach the first connection.
    /// </summary>
    [Fact]
    public async Task A_session_cannot_join_a_circuit_started_by_a_visitor()
    {
        using var visitor = Visitor.To(app);
        await visitor.SignUpAsync();

        await using var anonymous = await CircuitClient.ConnectAsync(app);
        var secret = await anonymous.StartCircuitAsync();

        await using var signedIn = await CircuitClient.ConnectAsync(app, visitor.SessionCookie);

        Assert.False(await signedIn.ConnectCircuitAsync(secret));
    }

    /// <summary>Nothing tracks a visitor's circuit, so nobody without a session can rejoin anything. The client reloads.</summary>
    [Fact]
    public async Task A_visitor_reloads_rather_than_rejoins()
    {
        await using var first = await CircuitClient.ConnectAsync(app);
        var secret = await first.StartCircuitAsync();

        await using var second = await CircuitClient.ConnectAsync(app);

        Assert.False(await second.ConnectCircuitAsync(secret));
    }

    /// <summary>SignalR matches a method's name in any case, so a filter comparing what the client sent would miss this.</summary>
    [Theory]
    [InlineData("connectcircuit")]
    [InlineData("CONNECTCIRCUIT")]
    public async Task A_method_name_in_another_case_gets_the_same_refusal(string method)
    {
        using var victim = Visitor.To(app);
        await victim.SignUpAsync();

        await using var theirs = await CircuitClient.ConnectAsync(app, victim.SessionCookie);
        var secret = await theirs.StartCircuitAsync();

        await using var mine = await CircuitClient.ConnectAsync(app);

        Assert.False(await mine.ConnectCircuitAsync(secret, method));
    }

    [Fact]
    public async Task A_user_action_on_a_circuit_keeps_the_session_alive()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var session = Assert.Single(await SessionsAsync(email));

        await using var connection = await CircuitClient.ConnectAsync(app, visitor.SessionCookie);
        await connection.StartCircuitAsync();
        await SessionRows.SetLastSeenAsync(app.ConnectionString, session, "30 minutes", Cancellation);

        await connection.SendUserActionAsync();
        await connection.SettleAsync();

        Assert.True(await SessionRows.WasJustSeenAsync(app.ConnectionString, session, Cancellation));
    }

    [Fact]
    public async Task Moving_to_another_page_keeps_the_session_alive()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var session = Assert.Single(await SessionsAsync(email));

        await using var connection = await CircuitClient.ConnectAsync(app, visitor.SessionCookie);
        await connection.StartCircuitAsync();
        await SessionRows.SetLastSeenAsync(app.ConnectionString, session, "30 minutes", Cancellation);

        await connection.SendNavigationAsync();
        await connection.SettleAsync();

        Assert.True(await SessionRows.WasJustSeenAsync(app.ConnectionString, session, Cancellation));
    }

    /// <summary>A live page acknowledges every render, so counting those would mean it never went idle.</summary>
    [Fact]
    public async Task A_render_acknowledgement_is_not_activity()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var session = Assert.Single(await SessionsAsync(email));

        await using var connection = await CircuitClient.ConnectAsync(app, visitor.SessionCookie);
        await connection.StartCircuitAsync();
        await SessionRows.SetLastSeenAsync(app.ConnectionString, session, "30 minutes", Cancellation);

        await connection.SendRenderAcknowledgementAsync();
        await connection.SettleAsync();

        Assert.InRange(await SessionRows.SecondsSinceSeenAsync(app.ConnectionString, session, Cancellation), 1790, 1810);
    }

    /// <summary>The first thing someone does after a timeout is what has to fail, rather than the next job run.</summary>
    [Fact]
    public async Task A_user_action_on_an_expired_session_closes_the_connection()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var session = Assert.Single(await SessionsAsync(email));

        await using var connection = await CircuitClient.ConnectAsync(app, visitor.SessionCookie);
        var secret = await connection.StartCircuitAsync();
        await SessionRows.SetExpiredAsync(app.ConnectionString, session, Cancellation);

        await connection.SendUserActionAsync();

        await connection.UntilClosedAsync();

        await using var again = await CircuitClient.ConnectAsync(app);

        Assert.False(await again.ConnectCircuitAsync(secret));
    }

    /// <summary>Long polling makes a request per poll, and counting those would keep an idle tab alive.</summary>
    [Fact]
    public async Task A_hub_request_is_not_activity()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var session = Assert.Single(await SessionsAsync(email));

        await SessionRows.SetLastSeenAsync(app.ConnectionString, session, "30 minutes", Cancellation);

        await using var connection = await CircuitClient.ConnectAsync(app, visitor.SessionCookie);
        await connection.StartCircuitAsync();

        Assert.InRange(await SessionRows.SecondsSinceSeenAsync(app.ConnectionString, session, Cancellation), 1790, 1810);
    }

    /// <summary>Hub requests are still checked, or a dead cookie could open a connection as its user.</summary>
    [Fact]
    public async Task A_dead_session_opens_a_connection_as_a_visitor()
    {
        using var visitor = Visitor.To(app);
        var email = await visitor.SignUpAsync();
        var session = Assert.Single(await SessionsAsync(email));

        await SessionRows.SetExpiredAsync(app.ConnectionString, session, Cancellation);

        await using var connection = await CircuitClient.ConnectAsync(app, visitor.SessionCookie);
        var secret = await connection.StartCircuitAsync();

        await using var again = await CircuitClient.ConnectAsync(app, visitor.SessionCookie);

        Assert.False(await again.ConnectCircuitAsync(secret));
    }

    private async Task<Visitor> SignInAgainAsync(string email)
    {
        var visitor = Visitor.To(app);

        (await visitor.SignInAsync(email)).Dispose();

        return visitor;
    }

    private Task<List<Guid>> SessionsAsync(string email) => SessionRows.OfAsync(app.ConnectionString, email, Cancellation);

    private Task<Guid> UserIdAsync(string email) => AccountRows.IdAsync(app.ConnectionString, email, Cancellation);
}

/// <summary>On Kestrel, because a test host can't carry a real WebSocket, which is how a browser connects.</summary>
public sealed class CircuitAccessOverWebSocketTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Revoking_a_session_closes_its_websocket()
    {
        await using var app = AccountApp.Create(postgres);

        app.UseKestrel(0);
        app.StartServer();

        using var visitor = Visitor.ToAddress(CircuitClient.KestrelAddress(app));
        var email = await visitor.SignUpAsync();

        await using var connection = await CircuitClient.ConnectOverWebSocketAsync(app, visitor.SessionCookie);
        await connection.StartCircuitAsync();

        var sessions = app.Services.GetRequiredService<ISessions>();
        var userId = await AccountRows.IdAsync(app.ConnectionString(), email, Cancellation);
        var session = Assert.Single(await sessions.ListAsync(userId, Cancellation));

        Assert.True(await sessions.RevokeAsync(userId, session.Id, Cancellation));

        await connection.UntilClosedAsync();
    }
}
