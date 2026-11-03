using Ecnanib.Kernel;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ecnanib.Tests.Audit;

/// <summary>FR-AUD-01 and FR-AUD-02: the write contract, what a record holds, and that it stays as written.</summary>
public sealed class AuditTrailTests(SharedApp app) : IClassFixture<SharedApp>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private IAuditTrail Trail => app.Services.GetRequiredService<IAuditTrail>();

    private IDbContextFactory<EcnanibDbContext> Contexts => app.Services.GetRequiredService<IDbContextFactory<EcnanibDbContext>>();

    private string ConnectionString => app.Services.GetRequiredService<IConfiguration>()["ConnectionStrings:Default"]!;

    [Fact]
    public async Task A_saved_record_holds_every_field_FR_AUD_02_asks_for()
    {
        var userId = Guid.NewGuid();
        var administratorId = Guid.NewGuid();
        var correlation = app.Services.GetRequiredService<ICorrelationContext>();
        string? correlationId;

        using (correlation.Begin())
        {
            correlationId = correlation.CurrentId;

            await AddAsync(new AuditEntry
            {
                Action = AuditAction.SessionRevoked,
                ActorKind = AuditActorKind.Administrator,
                ActorId = administratorId,
                UserId = userId,
                TargetType = "Session",
                TargetId = "a-session",
                Changes = [new AuditChange("Status", "Active", "Revoked")],
            });
        }

        var row = Assert.Single(await AuditRows.OfUserAsync(ConnectionString, userId, Cancellation));
        var record = Assert.Single(await Trail.ListForUserAsync(userId, before: null, count: 10, Cancellation));

        Assert.InRange(row.Timestamp, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
        Assert.Equal(("Administrator", administratorId), (row.ActorKind, row.ActorId));
        Assert.Equal("SessionRevoked", row.Action);
        Assert.Equal(("Session", "a-session"), (row.TargetType, row.TargetId));
        Assert.NotNull(correlationId);
        Assert.Equal(correlationId, row.CorrelationId);
        Assert.Equal([new AuditChange("Status", "Active", "Revoked")], record.Changes);

        // Outside a request or circuit action there's no client. ClientOriginTests covers the rest.
        Assert.Null(row.IpAddress);
        Assert.Null(row.UserAgent);
    }

    [Fact]
    public async Task Nothing_is_recorded_unless_the_callers_change_commits()
    {
        var userId = Guid.NewGuid();

        await using (var unsaved = await Contexts.CreateDbContextAsync(Cancellation))
        {
            Trail.Add(unsaved, AuditEntry.ByUser(AuditAction.PasswordChanged, userId));
        }

        await using (var context = await Contexts.CreateDbContextAsync(Cancellation))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(Cancellation);

            Trail.Add(context, AuditEntry.ByUser(AuditAction.PasswordChanged, userId));
            await context.SaveChangesAsync(Cancellation);
            await transaction.RollbackAsync(Cancellation);
        }

        Assert.Empty(await AuditRows.OfUserAsync(ConnectionString, userId, Cancellation));
    }

    /// <summary>The table has no SanitizingSink, so the trail masks with the logging rules (AC-03).</summary>
    [Fact]
    public async Task A_field_named_like_a_secret_keeps_no_value()
    {
        var userId = Guid.NewGuid();
        var canary = CanaryCases.NewCanary();

        await AddAsync(AuditEntry.ByUser(AuditAction.PasswordChanged, userId) with
        {
            Changes =
            [
                .. CanaryCases.SecretNames.Select(name => new AuditChange(name, canary, canary)),
                new AuditChange("ApiKey", null, canary),
            ],
        });

        var row = Assert.Single(await AuditRows.OfUserAsync(ConnectionString, userId, Cancellation));
        var changes = Assert.Single(await Trail.ListForUserAsync(userId, before: null, count: 10, Cancellation)).Changes;

        Assert.DoesNotContain(canary, row.Summary, StringComparison.Ordinal);
        Assert.Equal(CanaryCases.SecretNames.Count + 1, changes.Count);
        Assert.All(changes.Take(changes.Count - 1), change => Assert.Equal(("***", "***"), (change.Before, change.After)));
        Assert.Equal(new AuditChange("ApiKey", null, "***"), changes[^1]);
    }

    [Fact]
    public async Task A_value_that_looks_like_a_secret_is_scrubbed_whatever_its_field()
    {
        var userId = Guid.NewGuid();
        var canary = CanaryCases.NewCanary();
        var databasePassword = new NpgsqlConnectionStringBuilder(ConnectionString).Password!;

        await AddAsync(AuditEntry.ByUser(AuditAction.PasswordChanged, userId) with
        {
            Changes =
            [
                .. CanaryCases.SecretShapes.Select(shape => new AuditChange($"Note{shape.Name}", null, shape.Render(canary))),
                new AuditChange("Note", $"reached with {databasePassword}", "unchanged"),
            ],
        });

        var row = Assert.Single(await AuditRows.OfUserAsync(ConnectionString, userId, Cancellation));

        Assert.DoesNotContain(canary, row.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(databasePassword, row.Summary, StringComparison.Ordinal);
        Assert.Contains("unchanged", row.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_record_without_changes_has_no_summary()
    {
        var userId = Guid.NewGuid();

        await AddAsync(AuditEntry.ByUser(AuditAction.SignedIn, userId));

        Assert.Null(Assert.Single(await AuditRows.OfUserAsync(ConnectionString, userId, Cancellation)).Summary);
    }

    [Fact]
    public async Task A_users_records_come_newest_first_in_pages()
    {
        var userId = Guid.NewGuid();

        foreach (var action in new[] { AuditAction.SignedIn, AuditAction.PasswordChanged, AuditAction.SignedOut })
        {
            await AddAsync(AuditEntry.ByUser(action, userId));
        }

        await AddAsync(AuditEntry.ByUser(AuditAction.SignedIn, Guid.NewGuid()));

        var first = await Trail.ListForUserAsync(userId, before: null, count: 2, Cancellation);
        var rest = await Trail.ListForUserAsync(userId, first[^1].Id, count: 2, Cancellation);

        Assert.Equal([AuditAction.SignedOut, AuditAction.PasswordChanged], first.Select(record => record.Action));
        Assert.Equal([AuditAction.SignedIn], rest.Select(record => record.Action));
        Assert.All(first.Concat(rest), record => Assert.Equal(userId, record.UserId));
    }

    /// <summary>The boundary tests can't see raw SQL, so the table enforces this itself, even for no rows.</summary>
    [Theory]
    [InlineData("UPDATE audit_events SET action = 'SignedIn'")]
    [InlineData("UPDATE audit_events SET action = 'SignedIn' WHERE false")]
    [InlineData("TRUNCATE audit_events")]
    public async Task The_table_refuses_to_change_a_record(string sql)
    {
        var userId = Guid.NewGuid();

        await AddAsync(AuditEntry.ByUser(AuditAction.SignedIn, userId));

        var refusal = await Assert.ThrowsAsync<PostgresException>(() => Sql.ExecuteAsync(ConnectionString, sql, Cancellation));

        Assert.Contains("append-only", refusal.MessageText, StringComparison.Ordinal);
        Assert.Equal("SignedIn", Assert.Single(await AuditRows.OfUserAsync(ConnectionString, userId, Cancellation)).Action);
    }

    /// <summary>Retention (#50) deletes expired records.</summary>
    [Fact]
    public async Task The_table_lets_a_record_be_deleted()
    {
        var userId = Guid.NewGuid();

        await AddAsync(AuditEntry.ByUser(AuditAction.SignedIn, userId));
        await Sql.ExecuteAsync(ConnectionString, $"DELETE FROM audit_events WHERE user_id = '{userId}'", Cancellation);

        Assert.Empty(await AuditRows.OfUserAsync(ConnectionString, userId, Cancellation));
    }

    private async Task AddAsync(AuditEntry entry)
    {
        await using var context = await Contexts.CreateDbContextAsync(Cancellation);

        Trail.Add(context, entry);
        await context.SaveChangesAsync(Cancellation);
    }
}
