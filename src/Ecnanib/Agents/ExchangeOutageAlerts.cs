using Ecnanib.ExchangeGateway;
using Ecnanib.Notifications;
using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Agents;

/// <summary>
/// Tells the users whose agents run on a market in an environment that the exchange is out
/// there, and that it answers again (NFR-REL-4): "a Binance outage pauses entries and raises an
/// alert". See "A Binance outage" in docs/reliability.md.
/// </summary>
/// <remarks>
/// <para>
/// The gateway says where the exchange is out, and pauses the new entries there: it may tell no
/// user, and knows of none. Agents knows whose agents run where, so it is what tells.
/// </para>
/// <para>
/// <b>An outage is told of once it has lasted for as long as the configuration file says,</b>
/// so that a connection which broke for a moment is told to nobody. Each user who has an agent
/// that runs there at that moment is told once, however many agents they have there. When the
/// exchange answers again, the users whose agents run there then are told that as well.
/// </para>
/// <para>
/// What was told is kept in memory, for this process, as the gateway keeps what it knows of
/// the outage. An application that starts during an outage finds it anew, and tells of it
/// again once it has lasted, and one that starts after an outage tells nobody that it is over.
/// </para>
/// </remarks>
internal sealed class ExchangeOutageAlerts(
    IExchangeOutages outages,
    IDbContextFactory<EcnanibDbContext> contexts,
    INotifications notifications,
    BinanceSettings settings,
    TimeProvider clock,
    ILogger<ExchangeOutageAlerts> logger)
{
    // The states in which the cycles of an agent run, and so in which it would open a position.
    private static readonly AgentState[] States = [.. Enum.GetValues<AgentState>().Where(AgentStates.RunsCycles)];

    // Where the users were told of an outage that is not yet told to be over. Only the job
    // touches it, and a job does not run twice at once.
    private readonly HashSet<(Market Market, TradingMode Mode)> _told = [];

    /// <summary>
    /// Tells of the outages that have lasted long enough and were not told of, and of those that
    /// are over and were. A user who could not be told is said in the log, and the others are.
    /// </summary>
    /// <param name="cancellationToken">Stops the telling. What was told by then stays told.</param>
    public async Task TellAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var lastsFor = TimeSpan.FromSeconds(settings.OutageAlertSeconds);

        foreach (var answers in outages.Answers())
        {
            var where = (answers.Market, answers.Environment);

            if (answers.OutageSince is { } since)
            {
                if (_told.Contains(where) || now - since < lastsFor)
                {
                    continue;
                }

                var told = await TellUsersAsync(
                    answers,
                    $"Binance has not answered on {answers.Market} ({answers.Environment}) since {since.UtcDateTime:HH:mm} UTC. Your agents " +
                    "there open no new position until it answers again. Open positions stay as they are, with their protective " +
                    "orders, which rest at Binance. There is nothing for you to do: you are told when Binance answers again.",
                    cancellationToken);

                _told.Add(where);

                // Said when someone was told: a market on which no agent runs has nobody to tell.
                if (told > 0)
                {
                    logger.LogInformation(
                        "{UserCount} users whose agents run on {Market} {Environment} were told that Binance is out there",
                        told,
                        answers.Market.ToString(),
                        answers.Environment.ToString());
                }
            }
            else if (_told.Contains(where))
            {
                var told = await TellUsersAsync(
                    answers,
                    $"Binance answers again on {answers.Market} ({answers.Environment}). Your agents there open new positions again, " +
                    "from the next close of a candle on.",
                    cancellationToken);

                _told.Remove(where);

                if (told > 0)
                {
                    logger.LogInformation(
                        "{UserCount} users whose agents run on {Market} {Environment} were told that Binance answers again there",
                        told,
                        answers.Market.ToString(),
                        answers.Environment.ToString());
                }
            }
        }
    }

    /// <summary>
    /// Tells each user who has an agent that runs on a market in an environment now, once, and
    /// says how many were told.
    /// </summary>
    private async Task<int> TellUsersAsync(ExchangeAnswers answers, string content, CancellationToken cancellationToken)
    {
        List<Guid> users;

        await using (var context = await contexts.CreateDbContextAsync(cancellationToken))
        {
            // Across users: a job has no user who is signed in, and takes the owner from the row.
            users = await context.Set<Agent>()
                .AsNoTracking()
                .Where(agent => States.Contains(agent.State) && agent.Market == answers.Market && agent.Mode == answers.Environment)
                .Select(agent => agent.UserId)
                .Distinct()
                .OrderBy(userId => userId)
                .ToListAsync(cancellationToken);
        }

        var told = 0;

        foreach (var userId in users)
        {
            try
            {
                // About no agent, and about the environment: a user reads it for all their agents there.
                await notifications.RaiseAsync(
                    userId, NotificationType.ExchangeOutage, agentId: null, answers.Environment, content, cancellationToken);

                told++;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // The others are told all the same. This one sees it on the page of an agent,
                // where a cycle says why it placed no entry.
                logger.LogError(
                    error,
                    "The user {UserId} could not be told of the outage of Binance on {Market} {Environment}",
                    userId,
                    answers.Market.ToString(),
                    answers.Environment.ToString());
            }
        }

        return told;
    }
}
