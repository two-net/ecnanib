using Ecnanib.Agents;
using Ecnanib.Experience;
using Ecnanib.Platform.Logging;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Has the lessons of the closed positions written (FR-EXP-6): for each lesson that is due, it
/// gathers what the position was and how it ended, has the model of the agent's Risk Manager
/// write the lesson, and gives the row what the model wrote, or says why it has nothing. See
/// "Writing a lesson" in docs/experience.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>A lesson is due once its position has its result.</b> What records the result leaves the
/// row, in the same transaction: see <see cref="PositionResults"/>. So nothing is asked here
/// about which positions are closed, and a position whose result was recorded before lessons
/// were written has none.
/// </para>
/// <para>
/// <b>The model is the one that the Risk Manager of the agent has now,</b> and the key is the
/// one of the agent's user now: the lesson is written after the position, by whoever reviews the
/// next plan. The model that held the role when the position was opened may no longer be the
/// one that the user gave it.
/// </para>
/// <para>
/// <b>It works across users.</b> A job has no user who is signed in: each row says whose it is,
/// and everything that is read for a lesson is read for that user, and written to that row
/// alone. See "What is not asked for by owner" in docs/ownership.md.
/// </para>
/// <para>
/// <b>A lesson is taken before its model is called,</b> which moves when it is due an hour on.
/// A run that is stopped while the model is called leaves the lesson for a later run, and a
/// model is not called for the same lesson again at once. A lesson is completed once: its row
/// keeps the first outcome that it is given.
/// </para>
/// <para>
/// <b>One lesson at a time, and none is started late in a run.</b> A call may take as long as
/// its provider is given, and may be made once more: a run starts no lesson after
/// <see cref="StartsWithin"/>, so that it is over before the next one is due. What is left
/// waits for that run.
/// </para>
/// <para>
/// It places no order and reads nothing of the exchange: what it needs of a position is stored.
/// </para>
/// </remarks>
internal sealed class LessonWriting(
    IDbContextFactory<EcnanibDbContext> contexts,
    IDueLessons lessons,
    IAgentTeams teams,
    LessonRuns runs,
    TimeProvider clock,
    ILogger<LessonWriting> logger)
{
    /// <summary>How many lessons a run takes at most: the ones that are due the longest.</summary>
    public const int Most = 50;

    /// <summary>How long after its start a run still starts a lesson. The rest wait for the next run.</summary>
    public static readonly TimeSpan StartsWithin = TimeSpan.FromMinutes(2);

    /// <summary>Has the lessons written that are due, each on its own, and says how many were written and how many ended without one.</summary>
    /// <param name="cancellationToken">Stops the run. What it completed by then stays.</param>
    public async Task<LessonsRun> WriteDueAsync(CancellationToken cancellationToken)
    {
        var began = clock.GetUtcNow();
        var (written, unwritten) = (0, 0);

        foreach (var due in await lessons.DueAsync(Most, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // By the clock, as a job is due by it: what is left waits for the next run.
            if (clock.GetUtcNow() - began >= StartsWithin)
            {
                break;
            }

            // Every event of a lesson says whose agent it is for.
            using var scope = logger.BeginCorrelation(new Correlation(UserId: due.UserId.ToString(), AgentId: due.AgentId.ToString()));

            try
            {
                switch (await WriteAsync(due, cancellationToken))
                {
                    case true:
                        written++;
                        break;

                    case false:
                        unwritten++;
                        break;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Said once, with the position. The others go on, and a later run tries again.
                logger.LogError(error, "The lesson of the position {PositionId} could not be written", due.PositionId);
            }
        }

        return new LessonsRun(written, unwritten);
    }

    /// <summary>Has one lesson written.</summary>
    /// <returns>True when it was written, false when its row was ended without one, and null when nothing was completed.</returns>
    private async Task<bool?> WriteAsync(DueLesson due, CancellationToken cancellationToken)
    {
        if (await PartsAsync(due, cancellationToken) is not { } parts
            || await teams.FindAsync(due.UserId, due.AgentId, cancellationToken) is not { } team)
        {
            // The position or its agent went meanwhile, and the row of the lesson with it.
            return null;
        }

        if (team.Roles.SingleOrDefault(role => role.Role == Role.RiskManager) is not { } riskManager)
        {
            return await EndAsync(due, parts, LessonOutcome.NoModel, cancellationToken);
        }

        // Taken before anything is called: only one that takes it calls, and a run that is
        // stopped from here on leaves the lesson for an hour.
        if (!await lessons.ClaimAsync(due.PositionId, cancellationToken))
        {
            return null;
        }

        var run = await runs.RunAsync(due.UserId, due.AgentId, riskManager, parts, cancellationToken);

        if (run is not { Outcome: LessonOutcome.Written, Draft: { } draft, TemplateId: { } templateId })
        {
            return await EndAsync(due, parts, run.Outcome, cancellationToken);
        }

        if (!await lessons.WriteAsync(due.PositionId, draft.Words, riskManager.ModelId, templateId, cancellationToken))
        {
            // The row went, or was completed, while the model was called.
            return null;
        }

        logger.LogInformation(
            "The lesson of the position of agent {AgentId} in {Symbol} was written", due.AgentId, parts.Position.Symbol);

        return true;
    }

    /// <summary>Ends a lesson with nothing written, and says why as a warning.</summary>
    private async Task<bool?> EndAsync(DueLesson due, LessonParts parts, LessonOutcome why, CancellationToken cancellationToken)
    {
        if (!await lessons.EndAsync(due.PositionId, why, cancellationToken))
        {
            return null;
        }

        logger.LogWarning(
            "The lesson of the position of agent {AgentId} in {Symbol} was not written: {LessonOutcome}",
            due.AgentId,
            parts.Position.Symbol,
            why);

        return false;
    }

    /// <summary>
    /// What the call of a lesson is given, from the rows of its position in one snapshot: the
    /// position with its result, and what the team answered in the cycle that opened it. Null
    /// when the position is not there, is not closed, or has no result.
    /// </summary>
    private async Task<LessonParts?> PartsAsync(DueLesson due, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var snapshot = await context.BeginSnapshotAsync(cancellationToken);

        // Asked for by the owner that the row of the lesson names, like everything of a user.
        var position = await context.Set<Position>()
            .AsNoTracking()
            .OwnedBy(due.UserId)
            .SingleOrDefaultAsync(stored => stored.Id == due.PositionId, cancellationToken);

        if (position is not { ClosedAt: not null, ExitReason: { } exitReason }
            || await context.Set<PositionResult>()
                .AsNoTracking()
                .OwnedBy(due.UserId)
                .SingleOrDefaultAsync(stored => stored.PositionId == due.PositionId, cancellationToken) is not { } result)
        {
            return null;
        }

        // What the position was opened with is what its entry keeps: its stop-loss is moved
        // afterwards. A position that no entry of the agent opened says what it holds.
        var entry = await context.Set<Order>()
            .AsNoTracking()
            .OwnedBy(due.UserId)
            .Where(order => order.PositionId == due.PositionId && order.Purpose == OrderPurpose.Entry)
            .OrderBy(order => order.Id)
            .Select(order => new { order.CycleId, order.AveragePrice, order.StopLoss, order.TakeProfit })
            .FirstOrDefaultAsync(cancellationToken);

        // The plan that opened it and the review of that plan, as the record of that cycle keeps
        // them: the valid answer of each role, and the latest where there is more than one. An
        // answer is kept as one line, and only when it holds nothing that a prompt never holds.
        var answered = entry?.CycleId is { } cycleId
            ? await context.Set<RoleOutput>()
                .AsNoTracking()
                .OwnedBy(due.UserId)
                .Where(output => output.CycleId == cycleId
                    && output.Validation == RoleValidation.Valid
                    && output.Result != null
                    && (output.Role == Role.Strategist || output.Role == Role.RiskManager))
                .OrderBy(output => output.Attempt)
                .Select(output => new { output.Role, output.Result })
                .ToListAsync(cancellationToken)
            : [];

        return new LessonParts(
            new ClosedPositionContext(
                position.Market,
                position.Timeframe,
                position.Symbol,
                position.QuoteAsset,
                position.Side,
                entry?.AveragePrice ?? position.EntryPrice,
                entry?.StopLoss ?? position.StopLoss,
                entry is null ? position.TakeProfit : entry.TakeProfit,
                exitReason,
                result.HoldingSeconds,
                result.ResultInR,
                result.NetResult,
                result.Fees,
                result.Funding,
                result.Complete),
            answered.LastOrDefault(output => output.Role == Role.Strategist)?.Result,
            answered.LastOrDefault(output => output.Role == Role.RiskManager)?.Result);
    }
}

/// <summary>What a run completed.</summary>
/// <param name="Written">How many lessons were written.</param>
/// <param name="Unwritten">How many closed positions were left without a lesson, each with why.</param>
internal sealed record LessonsRun(int Written, int Unwritten);
