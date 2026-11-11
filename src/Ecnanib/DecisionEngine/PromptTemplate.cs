using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.DecisionEngine;

/// <summary>What a prompt template is for: a role, a market, a timeframe, or the trading profile (FR-PRM-1).</summary>
public enum PromptTemplateKind
{
    Role,
    Market,
    Timeframe,
    Profile,
}

/// <summary>
/// One version of a prompt template: the instructions for one role, or the module for one
/// market, one timeframe, or the trading profile (SRS 4.6). Templates are managed by the system
/// and seeded through migrations (FR-PRM-7, NFR-DAT-10).
/// </summary>
/// <param name="id">Assigned by the seed, and never by the database.</param>
/// <param name="kind">What the template is for.</param>
/// <param name="subject">
/// Which one of its kind: the role, the market, the timeframe, or the profile, such as
/// <c>MarketAnalyst</c>, <c>Spot</c>, <c>15m</c>, or <c>Aggressive</c>.
/// </param>
/// <param name="version">Counts up from 1 for each kind and subject.</param>
/// <param name="text">The template itself.</param>
/// <param name="isActive">Whether this is the version in use. Each kind and subject has at most one.</param>
public sealed class PromptTemplate(
    int id, PromptTemplateKind kind, string subject, int version, string text, bool isActive)
{
    public int Id { get; } = id;

    public PromptTemplateKind Kind { get; } = kind;

    public string Subject { get; } = subject;

    public int Version { get; } = version;

    public string Text { get; } = text;

    public bool IsActive { get; } = isActive;
}

internal sealed class PromptTemplateConfiguration : IEntityTypeConfiguration<PromptTemplate>
{
    public void Configure(EntityTypeBuilder<PromptTemplate> builder)
    {
        builder.ToTable("prompt_templates");
        builder.HasKey(template => template.Id);

        // Rows come only from migrations, which name each id, so the database generates none.
        builder.Property(template => template.Id).ValueGeneratedNever();
        builder.Property(template => template.Kind);
        builder.Property(template => template.Subject);
        builder.Property(template => template.Version);
        builder.Property(template => template.Text);
        builder.Property(template => template.IsActive);

        builder.HasIndex(template => new { template.Kind, template.Subject, template.Version }).IsUnique();

        // At most one version in use for each kind and subject.
        builder.HasIndex(template => new { template.Kind, template.Subject }).IsUnique().HasFilter("is_active");

        builder.HasData(FirstVersions.All);
    }

    /// <summary>
    /// Version 1 of every template, with the outline the specification gives for it: the role's
    /// job and structured result (SRS 4.5), the timeframe and market modules (SRS 4.6), and the
    /// Aggressive profile (FR-PRM-5). The full wording follows as later versions.
    /// </summary>
    /// <remarks>
    /// Each text is one line. A text that spans lines in this file would change with the line
    /// endings of a checkout, and with it the model.
    /// </remarks>
    private static class FirstVersions
    {
        public static PromptTemplate[] All =>
        [
            Of(1, PromptTemplateKind.Role, nameof(Role.MarketAnalyst),
                "Reads price, volume, and indicators, and on Futures also funding and open interest. " +
                "Structured result: Bias (bullish, bearish, or neutral), key price levels, volatility read."),
            Of(2, PromptTemplateKind.Role, nameof(Role.SentimentAnalyst),
                "Reads news and social context for the symbol and the wider market. " +
                "Structured result: Bias, notable events, confidence, and whether live data was available."),
            Of(3, PromptTemplateKind.Role, nameof(Role.Strategist),
                "Combines both reports, recalled lessons, and the current position into a trade plan. " +
                "Structured result: Action, entry type and price, stop-loss, take-profit, risk fraction, rationale."),
            Of(4, PromptTemplateKind.Role, nameof(Role.RiskManager),
                "Reviews the plan independently. " +
                "Structured result: Approve, reduce, or veto, with reasons."),

            Of(5, PromptTemplateKind.Market, nameof(Market.Spot),
                "Long-only. Actions are open long, hold, close, and adjust protection. " +
                "No leverage and no shorting; a bearish view means staying in the quote currency."),
            Of(6, PromptTemplateKind.Market, nameof(Market.Futures),
                "Long and short. Adds open short and reverse. " +
                "Covers leverage within the cap, isolated margin, liquidation distance, " +
                "funding cost over the expected holding period, and reduce-only exits."),

            Of(7, PromptTemplateKind.Timeframe, "15m",
                "Style: Intraday momentum and pullbacks, with close attention to fees and spread. " +
                "Context: 15m candles, with 1H and 4H context. " +
                "Expected holding period: Minutes to hours."),
            Of(8, PromptTemplateKind.Timeframe, "1H",
                "Style: Intraday to short swing. " +
                "Context: 1H candles, with 4H and 1D context. " +
                "Expected holding period: Hours to days."),
            Of(9, PromptTemplateKind.Timeframe, "4H",
                "Style: Swing. " +
                "Context: 4H candles, with 1D and 1W context. " +
                "Expected holding period: Days to weeks."),
            Of(10, PromptTemplateKind.Timeframe, "1D",
                "Style: Position. " +
                "Context: 1D candles, with 1W context. " +
                "Expected holding period: Weeks to months."),

            Of(11, PromptTemplateKind.Profile, "Aggressive",
                "Look for opportunities actively, act on moderate-conviction setups, use the full risk allowed, " +
                "and trade both directions on Futures, while always defining a stop-loss and staying within the stated limits."),
        ];

        private static PromptTemplate Of(int id, PromptTemplateKind kind, string subject, string text) =>
            new(id, kind, subject, version: 1, text, isActive: true);
    }
}
