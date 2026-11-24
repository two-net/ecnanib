using Ecnanib.Catalog;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Reads the cycles that were recorded for an agent, each as a complete record: the summary of
/// its data package, every call of a role, the checks of the Risk Guard, its orders, and its
/// outcome (FR-CYC-10). Its user views a cycle as a timeline. See "The timeline" in docs/team.md.
/// </summary>
/// <remarks>
/// It takes the user by id, and not from a request: the Decision Engine may not use Accounts.
/// Whatever serves the request hands over the user who is signed in, and a cycle of anyone else
/// is not found (NFR-SEC-3). It only reads.
/// </remarks>
public interface IRecordedCycles
{
    /// <summary>The latest cycles of an agent of a user, latest close first.</summary>
    /// <param name="userId">The user who is signed in.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<CycleHistory> LatestAsync(Guid userId, Guid agentId, CancellationToken cancellationToken);

    /// <summary>
    /// One cycle of an agent of a user, as a complete record, or null when that user has no such
    /// cycle of that agent: one of another user, and one of another agent, are not found.
    /// </summary>
    /// <param name="userId">The user who is signed in.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="cycleId">The cycle.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<CycleRecord?> FindAsync(Guid userId, Guid agentId, Guid cycleId, CancellationToken cancellationToken);

    /// <summary>
    /// The latest cycles of the agents of a user in one mode, latest close first: the recent
    /// decisions that the dashboard lists for that mode (FR-RPT-1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A cycle is of the mode that its agent was in then,</b> which its row says, and not of
    /// the mode that the agent is in now: what was decided in Demo is never listed under Live
    /// (FR-BIN-11).
    /// </para>
    /// <para>
    /// <b>A test cycle is not among them.</b> Its user ran it to see what the team would
    /// decide, and it placed no orders (FR-CYC-12): it is on the list of the cycles of its
    /// agent, and is no decision that anything followed from.
    /// </para>
    /// </remarks>
    /// <param name="userId">The user who is signed in.</param>
    /// <param name="mode">The mode.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns><see cref="RecentCycle.Shown"/> cycles at the most.</returns>
    Task<IReadOnlyList<RecentCycle>> LatestOfUserAsync(Guid userId, TradingMode mode, CancellationToken cancellationToken);
}

/// <summary>A cycle of one of the agents of a user, as a list of the latest cycles of several agents names it.</summary>
/// <param name="AgentId">The agent whose cycle it is.</param>
/// <param name="Cycle">The cycle.</param>
public sealed record RecentCycle(Guid AgentId, CycleListing Cycle)
{
    /// <summary>How many cycles of a mode the dashboard lists.</summary>
    public const int Shown = 10;
}

/// <summary>The cycles of an agent that a page lists.</summary>
/// <param name="Total">How many cycles are recorded for the agent.</param>
/// <param name="Cycles">The latest of them, latest close first.</param>
public sealed record CycleHistory(int Total, IReadOnlyList<CycleListing> Cycles);

/// <summary>One cycle of an agent, as a list names it.</summary>
/// <param name="Id">The id of the cycle.</param>
/// <param name="ClosedAt">The close of the candle that the cycle was for.</param>
/// <param name="Mode">The mode that the agent was in then, which stays as it was when the agent is switched (FR-BIN-11).</param>
/// <param name="StartedAt">When the cycle started.</param>
/// <param name="EndedAt">When it ended.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Kind">What started it: the close of its candle, or its user, who ran it as a test (FR-CYC-12).</param>
public sealed record CycleListing(
    Guid Id,
    DateTimeOffset ClosedAt,
    TradingMode Mode,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    CycleOutcome Outcome,
    CycleKind Kind = CycleKind.Scheduled);

/// <summary>One cycle, as a complete record (FR-CYC-10).</summary>
/// <param name="Id">The id of the cycle.</param>
/// <param name="ClosedAt">The close of the candle that the cycle was for.</param>
/// <param name="Mode">The mode that the agent was in then (FR-BIN-11).</param>
/// <param name="StartedAt">When the cycle started.</param>
/// <param name="EndedAt">When it ended.</param>
/// <param name="Outcome">How it ended: the decision of its team, or why it made none.</param>
/// <param name="DataPackage">The summary of the data package that its team decided on, or null for a cycle that kept none.</param>
/// <param name="Calls">Every call of a role, in the order the calls were made.</param>
/// <param name="Checks">What the Risk Guard made of what went on of the plan, in the order of the checks. Empty when nothing went on.</param>
/// <param name="Orders">The orders that the cycle placed, in the order they were written, each as it is now. Empty when it placed none.</param>
/// <param name="NoEntry">Why no entry was placed for a plan to open a position that the Risk Guard accepted, or null.</param>
/// <param name="Kind">
/// What started it: the close of its candle, or its user, who ran it as a test. A test cycle
/// placed no orders, whatever its team decided (FR-CYC-12).
/// </param>
public sealed record CycleRecord(
    Guid Id,
    DateTimeOffset ClosedAt,
    TradingMode Mode,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    CycleOutcome Outcome,
    DataPackageSummary? DataPackage,
    IReadOnlyList<RoleCallView> Calls,
    IReadOnlyList<RiskCheckView> Checks,
    IReadOnlyList<CycleOrderView> Orders,
    NoEntryReason? NoEntry,
    CycleKind Kind = CycleKind.Scheduled);

/// <summary>One call of a role in a cycle, as its user reads it (FR-TEAM-8).</summary>
/// <param name="Role">The role that was called.</param>
/// <param name="Attempt">Which call of the role in its cycle: 1, or 2 for the one that was made once more.</param>
/// <param name="ProviderName">The name of the provider of the model, as users read it.</param>
/// <param name="ModelIdentifier">The identifier that the provider gives the model.</param>
/// <param name="ModelName">The name that the provider gives the model for people to read, or null when it gives none.</param>
/// <param name="Templates">
/// The versions of the templates that the prompt of the call was composed from, in the order of
/// the prompt: the instructions of the role, and the modules of the market, the timeframe, and
/// the profile (FR-PRM-6).
/// </param>
/// <param name="CalledAt">When the call was made.</param>
/// <param name="LatencyMs">How long it took, in whole milliseconds.</param>
/// <param name="InputTokens">The tokens of the prompt, as the provider counted them, or null when it did not say.</param>
/// <param name="OutputTokens">The tokens of the answer, or null when the provider did not say.</param>
/// <param name="LiveSearches">How many live searches the provider ran, for a call that offered one. Null for a call that offered none.</param>
/// <param name="Outcome">How the call ended.</param>
/// <param name="Validation">What the validation made of the answer.</param>
/// <param name="Rejection">Why the answer was rejected, as a field of the schema and a rule, or null.</param>
/// <param name="Result">What the model answered, on one line of JSON, or null for a call whose answer is not kept.</param>
public sealed record RoleCallView(
    Role Role,
    int Attempt,
    string ProviderName,
    string ModelIdentifier,
    string? ModelName,
    IReadOnlyList<TemplateVersion> Templates,
    DateTimeOffset CalledAt,
    int LatencyMs,
    int? InputTokens,
    int? OutputTokens,
    int? LiveSearches,
    RoleCallOutcome Outcome,
    RoleValidation Validation,
    string? Rejection,
    string? Result);

/// <summary>A version of a prompt template that a call used.</summary>
/// <param name="Kind">Whether it is the instructions of a role, or the module of a market, a timeframe, or the profile.</param>
/// <param name="Subject">Which one of that kind, such as <c>Strategist</c> or <c>15m</c>.</param>
/// <param name="Version">The version, which is never changed.</param>
public sealed record TemplateVersion(PromptTemplateKind Kind, string Subject, int Version);

/// <summary>An order that a cycle placed, as its user reads it, and as it is now.</summary>
/// <param name="Purpose">What it is for.</param>
/// <param name="Type">The kind of order.</param>
/// <param name="Side">Whether it buys or sells.</param>
/// <param name="Symbol">The symbol that it was placed in.</param>
/// <param name="Price">The price that a stop is triggered at, or the price of an entry at a price. Null for an order at the market.</param>
/// <param name="StopLoss">Of an entry: the stop-loss of the position that it opens.</param>
/// <param name="TakeProfit">Of an entry: the take-profit of that position.</param>
/// <param name="Quantity">How much it is for.</param>
/// <param name="Status">What has become of it.</param>
/// <param name="ExecutedQuantity">How much of it is executed, as far as the exchange has said.</param>
/// <param name="AveragePrice">The average price of what is executed, or null when nothing is.</param>
/// <param name="ClientOrderId">The identifier that the order carries at the exchange.</param>
/// <param name="ExchangeOrderId">The id that the exchange knows it by, or null while it has said none.</param>
/// <param name="CreatedAt">When its row was written, before it was sent.</param>
/// <param name="UpdatedAt">When its status last changed.</param>
public sealed record CycleOrderView(
    OrderPurpose Purpose,
    OrderType Type,
    OrderSide Side,
    string Symbol,
    decimal? Price,
    decimal? StopLoss,
    decimal? TakeProfit,
    decimal Quantity,
    OrderStatus Status,
    decimal ExecutedQuantity,
    decimal? AveragePrice,
    string ClientOrderId,
    string? ExchangeOrderId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Reads the recorded cycles of an agent, of the user that it is asked for.</summary>
internal sealed class RecordedCycles(IDbContextFactory<EcnanibDbContext> contexts, ICatalogModels models) : IRecordedCycles
{
    /// <summary>How many cycles a page lists.</summary>
    public const int Shown = 50;

    public async Task<CycleHistory> LatestAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // Asked for by owner: a cycle of another user's agent is not found.
        var ofAgent = context.Set<DecisionCycle>().AsNoTracking().OwnedBy(userId).Where(cycle => cycle.AgentId == agentId);

        var total = await ofAgent.CountAsync(cancellationToken);
        var latest = await ofAgent
            .OrderByDescending(cycle => cycle.ClosedAt)
            .ThenByDescending(cycle => cycle.StartedAt)
            .Take(Shown)
            .Select(cycle => new CycleListing(cycle.Id, cycle.ClosedAt, cycle.Mode, cycle.StartedAt, cycle.EndedAt, cycle.Outcome, cycle.Kind))
            .ToListAsync(cancellationToken);

        return new CycleHistory(total, latest);
    }

    public async Task<IReadOnlyList<RecentCycle>> LatestOfUserAsync(Guid userId, TradingMode mode, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // Asked for by owner, and by the mode that each cycle says: an index of the cycles of a
        // user in a mode, in the order of their closes, gives the latest of them.
        var latest = await context.Set<DecisionCycle>()
            .AsNoTracking()
            .OwnedBy(userId)
            .Where(cycle => cycle.Mode == mode && cycle.Kind == CycleKind.Scheduled)
            .OrderByDescending(cycle => cycle.ClosedAt)
            .ThenByDescending(cycle => cycle.StartedAt)
            .Take(RecentCycle.Shown)
            .Select(cycle => new { cycle.AgentId, cycle.Id, cycle.ClosedAt, cycle.Mode, cycle.StartedAt, cycle.EndedAt, cycle.Outcome, cycle.Kind })
            .ToListAsync(cancellationToken);

        return
        [
            .. latest.Select(cycle => new RecentCycle(
                cycle.AgentId,
                new CycleListing(cycle.Id, cycle.ClosedAt, cycle.Mode, cycle.StartedAt, cycle.EndedAt, cycle.Outcome, cycle.Kind))),
        ];
    }

    public async Task<CycleRecord?> FindAsync(Guid userId, Guid agentId, Guid cycleId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // The parts of a record are rows of four tables, and an order of the cycle may be
        // written to while they are read: they are read as they were at one moment.
        await using var snapshot = await context.BeginSnapshotAsync(cancellationToken);

        // Everything is asked for by owner. A cycle of another user is not found, and neither
        // is one of another agent of the same user: the page of an agent shows its own cycles.
        var cycle = await context.Set<DecisionCycle>()
            .AsNoTracking()
            .OwnedBy(userId)
            .SingleOrDefaultAsync(stored => stored.Id == cycleId && stored.AgentId == agentId, cancellationToken);

        if (cycle is null)
        {
            return null;
        }

        var outputs = await context.Set<RoleOutput>()
            .AsNoTracking()
            .OwnedBy(userId)
            .Where(output => output.CycleId == cycleId)
            .ToListAsync(cancellationToken);

        var checks = await context.Set<RiskCheck>()
            .AsNoTracking()
            .OwnedBy(userId)
            .Where(check => check.CycleId == cycleId)
            .OrderBy(check => check.Sequence)
            .ToListAsync(cancellationToken);

        // An order names its cycle by the id alone, so it is asked for by its agent as well.
        var orders = await context.Set<Order>()
            .AsNoTracking()
            .OwnedBy(userId)
            .Where(order => order.CycleId == cycleId && order.AgentId == agentId)
            .OrderBy(order => order.CreatedAt)
            .ThenBy(order => order.Id)
            .ToListAsync(cancellationToken);

        // The versions of the templates that the calls used. A version is never changed, and
        // its text is not read here: the page of the prompts shows what an agent uses now.
        var templateIds = outputs
            .SelectMany(output => new[] { output.RoleTemplateId, output.MarketTemplateId, output.TimeframeTemplateId, output.ProfileTemplateId })
            .Distinct()
            .ToList();
        var templates = await context.Set<PromptTemplate>()
            .AsNoTracking()
            .Where(template => templateIds.Contains(template.Id))
            .Select(template => new { template.Id, template.Kind, template.Subject, template.Version })
            .ToDictionaryAsync(template => template.Id, template => new TemplateVersion(template.Kind, template.Subject, template.Version), cancellationToken);

        var guarded = await RiskCheckViews.OfAsync(
            context, userId, [.. checks.Select(check => (check, cycle.ClosedAt, cycle.Mode, cycle.Kind))], cancellationToken);

        // A model of the catalog is never deleted, so a call finds the one that it was made
        // with, and its provider, for good.
        var called = new Dictionary<Guid, CatalogModel?>();

        foreach (var modelId in outputs.Select(output => output.ModelId).Distinct())
        {
            called[modelId] = await models.FindAsync(modelId, cancellationToken);
        }

        return new CycleRecord(
            cycle.Id,
            cycle.ClosedAt,
            cycle.Mode,
            cycle.StartedAt,
            cycle.EndedAt,
            cycle.Outcome,
            DataPackageSummary.Read(cycle.DataPackage),
            [
                // In the order the calls were made. The two analysts are called at the same
                // moment: they stand in the order of the roles.
                .. outputs
                    .OrderBy(output => output.CalledAt)
                    .ThenBy(output => output.Role)
                    .ThenBy(output => output.Attempt)
                    .Select(output => new RoleCallView(
                        output.Role,
                        output.Attempt,
                        called[output.ModelId]?.ProviderName ?? UnknownProvider,
                        called[output.ModelId]?.Identifier ?? UnknownModel,
                        called[output.ModelId]?.DisplayName,
                        [
                            .. new[] { output.RoleTemplateId, output.MarketTemplateId, output.TimeframeTemplateId, output.ProfileTemplateId }
                                .Where(templates.ContainsKey)
                                .Select(id => templates[id]),
                        ],
                        output.CalledAt,
                        output.LatencyMs,
                        output.InputTokens,
                        output.OutputTokens,
                        output.LiveSearches,
                        output.Outcome,
                        output.Validation,
                        output.Rejection,
                        output.Result)),
            ],
            guarded,
            [
                .. orders.Select(order => new CycleOrderView(
                    order.Purpose,
                    order.Type,
                    order.Side,
                    order.Symbol,
                    order.Price,
                    order.StopLoss,
                    order.TakeProfit,
                    order.Quantity,
                    order.Status,
                    order.ExecutedQuantity,
                    order.AveragePrice,
                    order.ClientOrderId,
                    order.ExchangeOrderId,
                    order.CreatedAt,
                    order.UpdatedAt)),
            ],
            cycle.NoEntry,
            cycle.Kind);
    }

    // A call points at its model with a key that keeps the model from being deleted, so these
    // are words for a row that nothing of the application wrote.
    private const string UnknownProvider = "a provider that is not in the catalog";
    private const string UnknownModel = "a model that is not in the catalog";
}
