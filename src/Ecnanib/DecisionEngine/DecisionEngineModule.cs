using Ecnanib.Agents;
using Ecnanib.Platform.BackgroundWork;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Decision Engine: schedules cycles, composes prompts, runs the team, validates outputs (SRS 3.1).
/// See docs/cycles.md and docs/prompts.md for what of it is built.
/// </summary>
public static class DecisionEngineModule
{
    /// <summary>
    /// Registers the component: what starts a cycle for each running agent when a candle closes
    /// (FR-CYC-1), what builds the data package of a cycle (FR-CYC-2), what runs the roles of
    /// the team, validates their results, and records their calls (FR-TEAM-6 to FR-TEAM-9), what
    /// reads the prompt templates that are in use (FR-PRM-7), what composes the prompt of a role
    /// call from them (FR-PRM-1), and what says whether the model of a role searches live for
    /// its call (FR-CYC-3).
    /// </summary>
    public static IServiceCollection AddDecisionEngine(this IServiceCollection services)
    {
        // What a cycle does once it has started: it builds its data package, runs the four roles
        // of the team, validates what each answered, and records every call (FR-TEAM-6 to
        // FR-TEAM-9). A decision that completes later than the stale limit of the agent's
        // timeframe opens no position (FR-CYC-9). What goes on of its plan is checked by the
        // Risk Guard (FR-RSK-3), and what the Risk Guard accepted is handed to what places the
        // orders.
        services.AddSingleton<IDecisionCycle, TeamCycle>();

        // The orders (SRS 3.2, step 7): what places the entry and the stop-loss of a plan that
        // the Risk Guard accepted, closes a position, and settles the orders of an agent with
        // the exchange. An order is never sent twice (FR-BIN-6), and every open position has a
        // stop-loss that rests on the exchange, or is closed at market (FR-RSK-7). It is the one
        // place that uses the orders of the Exchange Gateway (NFR-SEC-6).
        services.AddSingleton<StoredOrders>();
        services.AddSingleton<IPlanOrders, AgentOrders>();

        // What the fee of an execution is worth in the asset that its symbol is priced in,
        // said as the execution is stored, so that the fee is in every profit and loss figure
        // whatever asset the exchange took it in (FR-BIN-10). It reads prices, and no account.
        services.AddSingleton<FeeValuation>();

        // What the positions and the orders answer to those that waited for them: Agents asks
        // what an agent holds on the exchange and has its position closed, a cycle reads what
        // its agent holds, and what starts a cycle asks whether the orders of its agent are
        // settled, so that a restart places no order twice (NFR-REL-3).
        services.AddSingleton<IAgentExposure, AgentExposure>();

        // The result of an agent, from the rows of its positions, its orders, its executions,
        // and its funding, with what its open position would make now: its equity is its
        // allocated capital plus that (FR-RSK-5, FR-RSK-8).
        services.AddSingleton<AgentResults>();
        services.AddSingleton<IAgentHoldings, StoredHoldings>();
        services.AddSingleton<IReconciliation, OrderReconciliation>();

        // What watches the loss limits of an agent, at every cycle and every reconciliation:
        // when its loss for the UTC day reaches its daily loss limit, or its drawdown from its
        // peak equity reaches its drawdown limit, its position is closed and it is halted
        // (FR-RSK-8). The Risk Guard says when a limit is reached, and Agents halts the agent.
        // It records the equity that it evaluated: the equity of an agent over time.
        services.AddSingleton<ILossLimits, LossLimitWatch>();

        // Settles the orders every minute, for the agents that have something to settle: what
        // a cycle could not finish, and what was under way when the application stopped.
        services.AddBackgroundJob<OrderSettlingJob>();

        // Records the result of every closed position, once all of it is stored: its net profit
        // or loss, its result in R, its fees, its funding, and how long it was held (FR-EXP-1).
        // The fees and the funding are those that the exchange said (FR-BIN-10). It reads the
        // rows alone, and has the equity of the agent recorded after it.
        services.AddSingleton<PositionResults>();
        services.AddBackgroundJob<PositionResultsJob>();

        // Reconciles the agents with the exchange, which is the source of truth, as the
        // background work starts and at the time between reconciliations: what the account of
        // an agent's user holds is held against the records, which are corrected, so what a
        // user does at the exchange by hand is in them (FR-BIN-7, FR-BIN-8). What starts a
        // cycle asks whether its agent was reconciled since this instance took up the work
        // (NFR-REL-3).
        services.AddSingleton<AccountReconciliation>();
        services.AddSingleton<IReconciliation, ReconciledSinceStart>();
        services.AddBackgroundJob<ReconciliationJob>();

        // Follows the account event feed of the exchange, where it has one: an agent is
        // reconciled at once when something changes in the account of its user (FR-BIN-7). An
        // event is a signal: what the records say is what the exchange answers when it is asked.
        services.AddSingleton<AccountFeed>();
        services.AddBackgroundJob<AccountFeedJob>();

        // Watches the positions of the Futures market, as the background work starts and at
        // the time between reconciliations: the margin of every open position is checked, and
        // its user is told when it approaches liquidation (FR-BIN-F5), and what a position
        // received and paid as funding is read and stored (FR-BIN-F4). It only reads the
        // exchange: it places no order, and adds no margin.
        services.AddSingleton<PositionWatch>();
        services.AddBackgroundJob<PositionWatchJob>();

        // What has the Risk Guard check what goes on of a plan: it gathers the facts that the
        // fixed rules are asked of, and keeps the outcome of every rule for the record of the
        // cycle (FR-RSK-4, FR-RSK-9). It calls no model. What keeps the global halt registers
        // an IGlobalHalt, when it is built: until then no halt is on.
        services.AddSingleton<PlanChecks>();

        // The checks of the Risk Guard that were stored for an agent, as its user reads them.
        services.AddSingleton<IRiskCheckRecords, RiskCheckRecords>();

        // One role of a team in a cycle: its prompt, the call of its model with the key of the
        // agent's user, the validation of its answer, and the one call more (FR-TEAM-9).
        services.AddSingleton<RoleRuns>();

        // The data package of a cycle: candles, the context of the higher timeframes, the
        // indicators, which the system computes, and what the agent holds, which what keeps the
        // positions and the orders says.
        services.AddSingleton<IDataPackages, DataPackages>();

        // The candles that a cycle starts with, asked for until they reach its close (FR-CYC-1).
        services.AddSingleton<ClosedCandles>();

        // Starts the cycles at every quarter hour, which the scheduler of the platform runs: it
        // skips the closes that passed while the application was down (FR-CYC-8), and the job
        // queues the cycles of a close with the limits of the configuration file (FR-CYC-11).
        services.AddBackgroundJob<DecisionCyclesJob>();

        // The templates as a prompt takes them and as a user reads them. It only reads: a
        // template arrives with a migration (NFR-DAT-10).
        services.AddSingleton<IPromptTemplates, PromptTemplates>();

        // What a prompt never holds: a secret, an email address, or an account identifier
        // (FR-PRM-9). It knows the secrets of the configuration file from the mask of the log.
        services.AddSingleton<PromptScreen>();

        // The prompt of a role call, from the templates of the agent's market and timeframe and
        // from what the call is given. Every part of it passes the screen. It calls no model.
        services.AddSingleton<IPromptComposition, PromptComposition>();

        // What the model of a role is called with. Only the Sentiment Analyst searches live, and
        // only with a model that the configuration file says can (FR-CYC-3). It makes no call:
        // what runs the roles of a cycle does.
        services.AddSingleton<IRoleCalls, RoleCalls>();

        // What validates the answer of a role: against the fixed schema of the role, against the
        // ranges of its values (FR-TEAM-6, NFR-SEC-7), and, for a plan, against what a plan may
        // do for its agent: the actions of its market, one position, and protection that only
        // tightens (FR-CYC-4 to FR-CYC-7). Only it makes a result.
        services.AddSingleton<IRoleResults, RoleResults>();

        // What records a cycle with the calls of its roles and the checks of the Risk Guard,
        // once, when it ends (FR-TEAM-8, FR-RSK-9).
        services.AddSingleton<ICycleRecords, CycleRecords>();

        return services;
    }
}
