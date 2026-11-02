using System.Net;
using System.Text.Json;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Persistence;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Logging;

/// <summary>
/// NFR-OBS-02. Cycles, AI calls, exchange calls and audit records don't exist yet, so these prove
/// what they'll rely on: the contract, and that the categories they log under inherit it.
/// </summary>
public sealed class CorrelationTests(PostgresFixture postgres)
{
    private const string Header = "X-Correlation-ID";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_request_has_one_id_from_its_response_to_the_log_table()
    {
        var database = postgres.NewConnectionString();
        await using var app = new LoggedApp(database);
        using var client = app.App.CreateClient();

        using var response = await client.GetAsync("/", Cancellation);
        await app.StopAsync();

        var id = IdOf(response);

        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.Equal(id, CorrelationIdOf(RequestEvent(app)));
        Assert.Equal(
            [id],
            await Sql.ColumnAsync(database, "SELECT correlation_id FROM log_events WHERE category = 'Http'", Cancellation));
    }

    [Fact]
    public async Task Each_request_gets_its_own_id()
    {
        await using var app = new LoggedApp(postgres.NewConnectionString());
        using var client = app.App.CreateClient();

        using var first = await client.GetAsync("/", Cancellation);
        using var second = await client.GetAsync("/", Cancellation);

        Assert.NotEqual(IdOf(first), IdOf(second));
    }

    /// <summary>Status pages run the pipeline again, so anything after them would give a request two IDs.</summary>
    [Fact]
    public async Task A_request_keeps_its_id_through_the_status_page()
    {
        await using var app = new LoggedApp(postgres.NewConnectionString());
        using var client = app.App.CreateClient();

        using var response = await client.GetAsync("/nothing-here", Cancellation);
        await app.StopAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(CorrelationIdOf(RequestEvent(app)), IdOf(response));
    }

    /// <summary>With no body, the response only starts once the middleware has returned and its scope has ended.</summary>
    [Fact]
    public async Task A_bodyless_response_still_carries_the_id()
    {
        await using var app = new LoggedApp(
            postgres.NewConnectionString(),
            configureServices: services => services.AddTransient<IStartupFilter>(
                _ => new TerminalMiddleware(context =>
                {
                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                    return Task.CompletedTask;
                })));
        using var client = app.App.CreateClient();

        using var response = await client.GetAsync("/nothing-here", Cancellation);
        await app.StopAsync();

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(CorrelationIdOf(RequestEvent(app)), IdOf(response));
    }

    /// <summary>
    /// The exception handler clears headers and runs the pipeline again, and a failure is when the ID
    /// matters most. Only Production runs it.
    /// </summary>
    [Fact]
    public async Task A_failed_request_keeps_its_id_through_the_error_page()
    {
        await using var app = new LoggedApp(
            postgres.NewConnectionString(),
            configureServices: services => services.AddTransient<IStartupFilter>(
                _ => new TerminalMiddleware(_ => throw new CorrelatedFailure())),
            environment: Environments.Production);
        using var client = app.App.CreatePublicClient();

        using var response = await client.GetAsync("/nothing-here", Cancellation);
        await app.StopAsync();

        var id = IdOf(response);
        var handlerEvent = Assert.Single(app.FileEvents(), entry => CategoryOf(entry).EndsWith("ExceptionHandlerMiddleware", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("The server hit an error", await response.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
        Assert.Equal(id, CorrelationIdOf(RequestEvent(app)));
        Assert.Equal(id, CorrelationIdOf(handlerEvent));
    }

    [Theory]
    [InlineData("client-chosen-value")]
    [InlineData("token=client-chosen-value")]
    [InlineData(null)]
    public async Task A_client_cannot_choose_the_id(string? value)
    {
        var sent = value ?? new string('a', 5_000);

        await using var app = new LoggedApp(postgres.NewConnectionString());
        using var client = app.App.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation(Header, sent);

        using var response = await client.SendAsync(request, Cancellation);
        await app.StopAsync();

        Assert.Matches("^[0-9a-f]{32}$", IdOf(response));
        Assert.DoesNotContain(sent, app.FileText(), StringComparison.Ordinal);
    }

    /// <summary>The adapters can't reference the contract, so this is how their events get linked.</summary>
    [Fact]
    public async Task Everything_logged_during_a_cycle_carries_its_id_including_the_adapter_categories()
    {
        var database = postgres.NewConnectionString();
        await using var app = new LoggedApp(database);
        var correlation = Correlation(app);
        string? id;

        using (correlation.Begin())
        {
            id = correlation.CurrentId;

            await Task.WhenAll(
                Task.Run(() => app.Logger("Exchange").LogInformation("Called the exchange"), Cancellation),
                Task.Run(() => app.Logger("AiCouncil").LogInformation("Called a model"), Cancellation));
        }

        await app.StopAsync();

        Assert.NotNull(id);
        Assert.All(
            app.FileEvents().Where(entry => CategoryOf(entry) is "Exchange" or "AiCouncil"),
            entry => Assert.Equal(id, CorrelationIdOf(entry)));
        Assert.Equal(
            [id, id],
            await Sql.ColumnAsync(
                database,
                "SELECT correlation_id FROM log_events WHERE category IN ('Exchange', 'AiCouncil')",
                Cancellation));
    }

    [Fact]
    public async Task Begin_always_creates_a_new_id_and_restores_the_outer_one()
    {
        await using var app = new LoggedApp(postgres.NewConnectionString());
        var correlation = Correlation(app);
        var logger = app.Logger("Ecnanib.Tests.Nesting");

        Assert.Null(correlation.CurrentId);

        string? outer;
        string? inner;

        using (correlation.Begin())
        {
            outer = correlation.CurrentId;

            using (correlation.Begin())
            {
                inner = correlation.CurrentId;
                logger.LogInformation("Inner");
            }

            Assert.Equal(outer, correlation.CurrentId);
            logger.LogInformation("Outer");
        }

        Assert.Null(correlation.CurrentId);
        logger.LogInformation("Neither");
        await app.StopAsync();

        var events = app.FileEvents().Where(entry => CategoryOf(entry) == "Ecnanib.Tests.Nesting").ToList();

        Assert.NotEqual(outer, inner);
        Assert.Equal(inner, CorrelationIdOf(events[0]));
        Assert.Equal(outer, CorrelationIdOf(events[1]));
        Assert.False(events[2].GetProperty("Properties").TryGetProperty(LogProperties.CorrelationId, out _));
    }

    /// <summary>A circuit's handler can return while OnInitializedAsync carries on, so the ID must outlive the scope.</summary>
    [Fact]
    public async Task Work_that_outlives_its_scope_keeps_the_id()
    {
        await using var app = new LoggedApp(postgres.NewConnectionString());
        var correlation = Correlation(app);
        var finish = new TaskCompletionSource();
        string? id;
        Task<string?> work;

        using (correlation.Begin())
        {
            id = correlation.CurrentId;
            work = Task.Run(async () =>
            {
                await finish.Task;
                app.Logger("Ecnanib.Tests.Outlives").LogInformation("Finished");

                return correlation.CurrentId;
            }, Cancellation);
        }

        finish.SetResult();

        Assert.Equal(id, await work);

        await app.StopAsync();

        Assert.Equal(id, CorrelationIdOf(Assert.Single(app.FileEvents(), entry => CategoryOf(entry) == "Ecnanib.Tests.Outlives")));
    }

    /// <summary>
    /// A real circuit needs a browser, so this runs the registered handlers directly, on the
    /// dispatcher a circuit uses. The context can be null because its constructor is internal and
    /// the handler doesn't read it.
    /// </summary>
    [Fact]
    public async Task Each_action_on_a_circuit_gets_its_own_id()
    {
        await using var app = new LoggedApp(postgres.NewConnectionString());
        var correlation = Correlation(app);
        var dispatcher = Dispatcher.CreateDefault();
        var seen = new List<string?>();

        Func<CircuitInboundActivityContext, Task> activity = _ => dispatcher.InvokeAsync(() =>
        {
            seen.Add(correlation.CurrentId);
            app.Logger("Ecnanib.Tests.Circuit").LogInformation("Handled an action");
        });

        await using var circuit = app.App.Services.CreateAsyncScope();

        foreach (var handler in circuit.ServiceProvider.GetServices<CircuitHandler>())
        {
            activity = handler.CreateInboundActivityHandler(activity);
        }

        await activity(null!);
        await activity(null!);
        await app.StopAsync();

        Assert.Null(correlation.CurrentId);
        Assert.Equal(2, seen.Count);
        Assert.NotNull(seen[0]);
        Assert.NotNull(seen[1]);
        Assert.NotEqual(seen[0], seen[1]);
        Assert.Equal(
            seen,
            app.FileEvents().Where(entry => CategoryOf(entry) == "Ecnanib.Tests.Circuit").Select(CorrelationIdOf));
    }

    private static ICorrelationContext Correlation(LoggedApp app) =>
        app.App.Services.GetRequiredService<ICorrelationContext>();

    private static string IdOf(HttpResponseMessage response) => Assert.Single(response.Headers.GetValues(Header));

    private static JsonElement RequestEvent(LoggedApp app) =>
        Assert.Single(app.FileEvents(), entry => CategoryOf(entry) == "Http");

    private static string CategoryOf(JsonElement entry) =>
        entry.GetProperty("Properties").TryGetProperty("SourceContext", out var category)
            ? category.GetString() ?? string.Empty
            : string.Empty;

    private static string? CorrelationIdOf(JsonElement entry) =>
        entry.GetProperty("Properties").GetProperty(LogProperties.CorrelationId).GetString();

    private sealed class CorrelatedFailure : Exception;
}
