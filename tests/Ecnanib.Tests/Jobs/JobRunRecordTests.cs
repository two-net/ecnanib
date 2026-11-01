using System.Collections.Concurrent;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Jobs;

/// <summary>What the JobRun record holds, and what it must never hold.</summary>
public sealed class JobRunRecordTests(PostgresFixture postgres)
{
    private const string Job = "records";

    private static readonly TimeSpan Unlimited = Timeout.InfiniteTimeSpan;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_run_is_recorded_when_it_starts_and_again_when_it_ends()
    {
        var database = postgres.NewConnectionString();
        var gate = new Gate();

        await using var app = new JobApp(database, new TestJob(
            Job,
            async (context, _) =>
            {
                await gate.PassAsync();
                context.Details = "12 rows";
            },
            interval: TimeSpan.FromHours(1)));

        await gate.UntilReachedAsync();

        Assert.Equal(["Running"], await JobRows.StatusesAsync(database, Job));
        Assert.Equal(1, await CountAsync(database, "started_at IS NOT NULL AND finished_at IS NULL"));

        gate.Open();
        await JobRows.UntilStatusAsync(database, Job, JobStatus.Succeeded);

        Assert.Equal(1, await CountAsync(database, "finished_at >= started_at AND details = '12 rows'"));
    }

    /// <summary>NFR-OBS-02: an operator gets from a row to its logs by this ID.</summary>
    [Fact]
    public async Task Each_run_has_its_own_id_on_its_row_and_its_logs()
    {
        var database = postgres.NewConnectionString();
        var ids = new ConcurrentQueue<string>();

        await using var app = new LoggedApp(
            database,
            configureServices: services => services.AddSingleton<IRecurringJob>(provider =>
            {
                var correlation = provider.GetRequiredService<ICorrelationContext>();

                return new TestJob(Job, (_, _) =>
                {
                    ids.Enqueue(correlation.CurrentId!);
                    return Task.CompletedTask;
                });
            }));

        await JobRows.UntilStatusAsync(database, Job, JobStatus.Succeeded, count: 2);
        await app.StopAsync();

        var rows = await Sql.ColumnAsync(
            database,
            $"SELECT correlation_id FROM job_runs WHERE job_name = '{Job}' AND status = 'Succeeded' ORDER BY id",
            Cancellation);

        var logged = await Sql.ColumnAsync(
            database,
            "SELECT correlation_id FROM log_events WHERE category = 'Ecnanib.Platform.Jobs.JobRunner'",
            Cancellation);

        Assert.Equal(rows.Count, rows.Distinct().Count());
        Assert.Equal(rows, ids.Take(rows.Count));
        Assert.All(rows, id => Assert.Contains(id, logged));
    }

    /// <summary>Work started during a request or a cycle would otherwise share that request's ID.</summary>
    [Fact]
    public async Task A_run_gets_a_new_id_even_when_started_under_another()
    {
        await using var app = new JobApp(postgres.NewConnectionString());
        var correlation = app.App.Services.GetRequiredService<ICorrelationContext>();
        string? inside = null;
        string? outer;

        using (correlation.Begin())
        {
            outer = correlation.CurrentId;

            await app.Runner.RunAsync(Job, Unlimited, (_, _) =>
            {
                inside = correlation.CurrentId;
                return Task.CompletedTask;
            });

            Assert.Equal(outer, correlation.CurrentId);
        }

        Assert.NotNull(inside);
        Assert.NotEqual(outer, inside);
    }

    /// <summary>
    /// job_runs has no SanitizingSink, and administrators read it, who mustn't see prompts that a
    /// provider's error may echo (FR-ADM-01). An unmarked value is the case scrubbing can't catch, so
    /// exception text mustn't reach the row at all. The log still gets it, through the masking there.
    /// </summary>
    [Fact]
    public async Task Exception_text_stays_out_of_the_record_and_goes_to_the_masked_log_under_the_same_id()
    {
        var database = postgres.NewConnectionString();
        var marked = CanaryCases.NewCanary();
        var unmarked = CanaryCases.NewCanary();

        await using var app = new LoggedApp(
            database,
            configureServices: services => services.AddSingleton<IRecurringJob>(new TestJob(
                Job,
                (_, _) => throw new InvalidOperationException($"password={marked} while saying {unmarked}"),
                interval: TimeSpan.FromHours(1))));

        await JobRows.UntilStatusAsync(database, Job, JobStatus.Failed);
        await app.StopAsync();

        var row = await JobRows.TextAsync(database, Job);
        var id = row.Split(' ')[^1];

        var logged = await Sql.ScalarAsync<string>(
            database,
            $"SELECT exception FROM log_events WHERE correlation_id = '{id}' AND exception IS NOT NULL",
            Cancellation);

        Assert.StartsWith("Failed System.InvalidOperationException ", row, StringComparison.Ordinal);
        Assert.DoesNotContain(marked, row, StringComparison.Ordinal);
        Assert.DoesNotContain(unmarked, row, StringComparison.Ordinal);
        Assert.Contains("password=***", logged, StringComparison.Ordinal);
        Assert.DoesNotContain(marked, await app.TableTextAsync(Cancellation), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Details_are_scrubbed_before_they_are_stored()
    {
        var database = postgres.NewConnectionString();
        var canary = CanaryCases.NewCanary();

        await using var app = new JobApp(database);

        await app.Runner.RunAsync(Job, Unlimited, (context, _) =>
        {
            context.Details = $"12 rows, password={canary}";
            return Task.CompletedTask;
        });

        var row = await JobRows.TextAsync(database, Job);

        Assert.Contains("12 rows, password=***", row, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_timeout_of_zero_is_refused_before_anything_runs()
    {
        var database = postgres.NewConnectionString();

        await using var app = new JobApp(database);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => app.Runner.RunAsync(Job, TimeSpan.Zero, (_, _) => Task.CompletedTask));

        Assert.Empty(await JobRows.StatusesAsync(database, Job));
    }

    private static Task<long> CountAsync(string database, string condition) =>
        Sql.ScalarAsync<long>(
            database,
            $"SELECT count(*) FROM job_runs WHERE job_name = '{Job}' AND {condition}",
            Cancellation);
}
