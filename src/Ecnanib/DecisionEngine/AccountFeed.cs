using Ecnanib.Credentials;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Tracks the changes of orders and positions through the account event feed of the exchange,
/// where it has one (FR-BIN-7): it has the gateway follow the accounts in which an agent holds
/// something, and reconciles an agent at once when the feed says that something changed in its
/// account. See "The account event feed" in docs/orders.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>An event is a signal, and no record.</b> The gateway says that something of a symbol
/// changed in an account, and nothing of what. The agent that holds something there is
/// reconciled, which asks the exchange about its orders and about what its account holds, and
/// writes what the exchange answers: see <see cref="IPlanOrders.ReconcileAsync"/>. So the feed
/// decides when the exchange is asked, and never what the records say. What it misses is found
/// by the reconciliation that runs on a schedule.
/// </para>
/// <para>
/// <b>A stored key is asked for when the gateway needs one</b>, to begin following an account
/// or to renew its feed, handed over with that call, and kept nowhere (FR-KEY-9). It is the
/// third type that is handed the Binance credential of a user, beside what places the orders
/// and what watches the positions: a test pins the three. It is handed nothing of the orders of
/// the gateway.
/// </para>
/// <para>
/// An account is followed while an agent holds something in it: an open position, or an order
/// that is not over. The gateway is told every time that the account is still wanted, and closes
/// a feed that nobody asked for in a while.
/// </para>
/// </remarks>
internal sealed class AccountFeed(
    IExchangeAccountFeed feed,
    IKeyHandOver keys,
    IKeyValidation keyChecks,
    AccountReconciliation reconciliation,
    StoredOrders stored,
    ILogger<AccountFeed> logger)
{
    // How many agents are reconciled at the same time for what the feed said.
    private const int AtOnce = 4;

    /// <summary>Whether there is an account to follow: one in which an agent holds something, on a market that has a feed in its environment.</summary>
    public async Task<bool> HasAccountsAsync(CancellationToken cancellationToken) =>
        (await stored.AccountsHoldingAsync(cancellationToken)).Any(account => feed.IsAvailable(account.Market, account.Mode));

    /// <summary>
    /// Has the gateway follow every account in which an agent holds something, where the
    /// exchange has a feed: says that each is still wanted, and hands the key of its user over
    /// for one that is not followed yet, or whose feed is to be renewed.
    /// </summary>
    /// <returns>How many accounts are followed after that.</returns>
    public async Task<int> FollowAsync(CancellationToken cancellationToken)
    {
        var accounts = (await stored.AccountsHoldingAsync(cancellationToken))
            .Where(account => feed.IsAvailable(account.Market, account.Mode))
            .ToList();

        // Each is said to be still wanted first: opening a feed may take a while, and no feed
        // that is followed is closed meanwhile for want of a word.
        foreach (var account in accounts)
        {
            feed.Keep(account.UserId, account.Market, account.Mode);
        }

        var followed = 0;

        // The markets, each in an environment, where the gateway opens no more for now: the
        // limit on requests has no room, or the address of the feed does not answer. The rest
        // of the accounts there wait for the next run, and no key is handed over for them.
        var closed = new HashSet<(Market Market, TradingMode Mode)>();

        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var state = feed.StateOf(account.UserId, account.Market, account.Mode);

            if (state == ExchangeFeedState.Followed)
            {
                followed++;

                continue;
            }

            // A feed whose renewal is due is followed still, whatever becomes of the renewal,
            // until the exchange ends it.
            if (state == ExchangeFeedState.RenewalDue)
            {
                followed++;
            }
            else if (closed.Contains((account.Market, account.Mode)))
            {
                continue;
            }

            try
            {
                var key = await keys.ExchangeKeyAsync(account.UserId, account.Mode, cancellationToken);

                if (key.Outcome != KeyHandOverOutcome.Handed)
                {
                    // Without a key nothing is asked: the account is reconciled on the schedule, as far as it can be.
                    continue;
                }

                var outcome = await feed.FollowAsync(
                    account.UserId, account.Market, account.Mode, key.ApiKey!, key.SecretKey!, cancellationToken);

                switch (outcome)
                {
                    case ExchangeFeedOutcome.Following when state == ExchangeFeedState.NotFollowed:
                        followed++;
                        break;

                    case ExchangeFeedOutcome.NotNow or ExchangeFeedOutcome.Failed:
                        closed.Add((account.Market, account.Mode));
                        break;

                    case ExchangeFeedOutcome.KeyRefused:
                        await RecheckKeyAsync(account);
                        break;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Said once, with the account. The others go on, and the next run tries again.
                logger.LogError(
                    error,
                    "The account event feed of user {UserId} on {Market} {TradingMode} could not be followed",
                    account.UserId,
                    account.Market,
                    account.Mode);
            }
        }

        return followed;
    }

    /// <summary>
    /// Takes what the feeds said since they were last asked, waits for a change when there is
    /// none, and reconciles the agents that hold something where something changed: in the
    /// symbol of a change, or in the whole account where the change names none.
    /// </summary>
    /// <param name="wait">How long it waits for a change when there is none.</param>
    /// <param name="cancellationToken">Cancels the wait, and what is not begun yet.</param>
    /// <returns>How many changes were said, and how many agents were reconciled for them.</returns>
    public async Task<(int Changes, int Agents)> TakeAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        var changes = await feed.TakeAsync(wait, cancellationToken);

        if (changes.Count == 0)
        {
            return (0, 0);
        }

        // An agent is reconciled once, however many changes are about what it holds.
        var agents = new SortedSet<Guid>();

        foreach (var change in changes)
        {
            agents.UnionWith(await stored.HoldingAsync(change.UserId, change.Mode, change.Market, change.Symbol, cancellationToken));
        }

        await Parallel.ForEachAsync(
            agents,
            new ParallelOptions { MaxDegreeOfParallelism = AtOnce, CancellationToken = cancellationToken },
            async (agentId, stopping) =>
            {
                try
                {
                    await reconciliation.ReconcileAsync(agentId, stopping);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // Said once, with the agent. The others go on, and the schedule reconciles it.
                    logger.LogError(error, "Agent {AgentId} could not be reconciled with the exchange after a change of its account", agentId);
                }
            });

        return (changes.Count, agents.Count);
    }

    /// <summary>
    /// Has the credential of a user checked again, after the exchange answered as it answers a
    /// wrong credential: the check decides whether it is (FR-KEY-3).
    /// </summary>
    private async Task RecheckKeyAsync(HeldAccount account)
    {
        try
        {
            await keyChecks.RevalidateExchangeKeyAsync(account.UserId, account.Mode, CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(error, "The Binance key of user {UserId} for {TradingMode} could not be checked again", account.UserId, account.Mode);
        }
    }
}
