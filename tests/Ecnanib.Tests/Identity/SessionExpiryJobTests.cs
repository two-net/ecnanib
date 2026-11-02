using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Identity;

/// <summary>Moves silent tabs along and keeps the table small. Nothing depends on it for a session to stop working.</summary>
public sealed class SessionExpiryJobTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_job_deletes_sessions_past_either_timeout_and_no_others(bool idle)
    {
        await using var app = App();
        using var expired = Visitor.To(app);
        using var live = Visitor.To(app);
        var connectionString = app.ConnectionString();
        var expiredEmail = await expired.SignUpAsync();
        var liveEmail = await live.SignUpAsync();
        var session = Assert.Single(await SessionRows.OfAsync(connectionString, expiredEmail, Cancellation));

        await ExpireAsync(connectionString, session, idle);

        await RunAsync(app);

        Assert.Empty(await SessionRows.OfAsync(connectionString, expiredEmail, Cancellation));
        Assert.Single(await SessionRows.OfAsync(connectionString, liveEmail, Cancellation));
    }

    /// <summary>A tab nobody is touching sends nothing to the hub, so this is what moves it along.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_job_closes_a_silent_tab(bool idle)
    {
        await using var app = App();
        using var visitor = Visitor.To(app);
        var connectionString = app.ConnectionString();
        var email = await visitor.SignUpAsync();
        var session = Assert.Single(await SessionRows.OfAsync(connectionString, email, Cancellation));

        await using var tab = await CircuitClient.ConnectAsync(app, visitor.SessionCookie);
        await tab.StartCircuitAsync();
        await ExpireAsync(connectionString, session, idle);

        await RunAsync(app);

        await tab.UntilClosedAsync();
    }

    /// <summary>Details go in job_runs, which keeps them after an account has gone, so a count is all they hold.</summary>
    [Fact]
    public async Task The_job_records_how_many_it_deleted()
    {
        await using var app = App();
        using var visitor = Visitor.To(app);
        var connectionString = app.ConnectionString();
        var session = Assert.Single(await SessionRows.OfAsync(connectionString, await visitor.SignUpAsync(), Cancellation));

        await SessionRows.SetExpiredAsync(connectionString, session, Cancellation);
        await RunAsync(app);

        Assert.Contains(
            "1 sessions",
            await Sql.ColumnAsync(connectionString, "SELECT details FROM job_runs WHERE job_name = 'test-session-expiry' AND details IS NOT NULL", Cancellation));
    }

    private static Task ExpireAsync(string connectionString, Guid session, bool idle) =>
        idle
            ? SessionRows.SetLastSeenAsync(connectionString, session, "61 minutes", Cancellation)
            : SessionRows.SetExpiredAsync(connectionString, session, Cancellation);

    /// <summary>A name of its own, so that this doesn't move the schedule the real loop reads.</summary>
    private static async Task RunAsync(AppFactory app)
    {
        var job = app.Services.GetServices<IRecurringJob>().Single(candidate => candidate.Name == "session-expiry");
        var status = await app.Services.GetRequiredService<IJobRunner>().RunAsync("test-session-expiry", TimeSpan.FromSeconds(30), job.RunAsync);

        Assert.Equal(JobStatus.Succeeded, status);
    }

    private AppFactory App() => AccountApp.Create(postgres, realJobs: true);
}
