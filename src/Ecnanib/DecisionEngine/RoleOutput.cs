using Ecnanib.Catalog;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// How one call of a role ended. A member is stored by its name, so renaming one changes stored
/// data and needs a migration.
/// </summary>
/// <remarks>
/// The first six are the outcomes of the gateway, by the same names: see "Outcomes" in
/// docs/ai-providers.md. The last two are the pipeline's own.
/// </remarks>
public enum RoleCallOutcome
{
    /// <summary>The model answered with a JSON object. Whether it is valid is the validation's to say.</summary>
    Succeeded,

    /// <summary>The model gave no JSON object: a refusal, an answer that was cut short, or text that is none.</summary>
    InvalidResult,

    /// <summary>The provider did not answer within its time limit.</summary>
    TimedOut,

    /// <summary>The provider refused the key.</summary>
    AuthenticationFailed,

    /// <summary>The provider has no such model.</summary>
    ModelNotFound,

    /// <summary>The call failed otherwise: no connection, a limit of the provider, or a fault of it.</summary>
    Failed,

    /// <summary>No call was made: the user has no key for the provider that can be used.</summary>
    NoKey,

    /// <summary>The call was under way when the cycle was stopped.</summary>
    Canceled,
}

/// <summary>
/// What the validation made of the answer of a call (FR-TEAM-8). A member is stored by its name.
/// </summary>
public enum RoleValidation
{
    /// <summary>The answer is inside the schema of the role and the ranges of its values.</summary>
    Valid,

    /// <summary>The answer is outside the schema or a range, and was rejected (NFR-SEC-7).</summary>
    Rejected,

    /// <summary>The call gave no answer to validate.</summary>
    NoResult,
}

/// <summary>
/// One call of a role, as a row of the <c>role_outputs</c> table (FR-TEAM-8): the model and so
/// its provider, the versions of the prompt templates, the latency, the token usage, the
/// structured result, and the validation outcome. A call that is made once more is a row of its
/// own. See "The record of a cycle" in docs/team.md.
/// </summary>
/// <remarks>
/// <para>
/// The row names the model, and not its provider: a model is of one provider, and is never
/// deleted (FR-CAT-5). It names the four templates that the prompt of the call was composed
/// from, each in the version that was in use: a version is never changed, so the row says for
/// good what the model was told (FR-PRM-6).
/// </para>
/// <para>
/// It holds no prompt and no key. The result is what the model answered, which was sent nothing
/// that says who the user is (FR-PRM-9).
/// </para>
/// <para>
/// It carries its owner along to the cycle that it hangs on, as the cycle does to its agent:
/// <c>(cycle_id, user_id)</c> points at <c>decision_cycles (id, user_id)</c>. See docs/ownership.md.
/// </para>
/// </remarks>
/// <param name="id">Assigned by the application.</param>
/// <param name="cycleId">The cycle that the call was made in.</param>
/// <param name="userId">The user whose agent the cycle was of.</param>
/// <param name="role">The role that was called.</param>
/// <param name="attempt">Which call of the role in its cycle: 1, or 2 for the one that was made once more (FR-TEAM-9).</param>
/// <param name="modelId">The model of the catalog that was called, which says its provider.</param>
/// <param name="roleTemplateId">The version of the instructions of the role that the prompt was composed from.</param>
/// <param name="marketTemplateId">The version of the market module.</param>
/// <param name="timeframeTemplateId">The version of the timeframe module.</param>
/// <param name="profileTemplateId">The version of the profile module.</param>
/// <param name="calledAt">When the call was made.</param>
/// <param name="latencyMs">How long the call took, in whole milliseconds.</param>
/// <param name="inputTokens">The tokens of the prompt, as the provider counted them, or null when it did not say.</param>
/// <param name="outputTokens">The tokens of the answer, as the provider counted them, or null when it did not say.</param>
/// <param name="liveSearches">
/// How many live searches the provider ran for the call, for a call that offered the model a
/// search (FR-CYC-3). Null for a call that offered none, and when the provider did not say.
/// </param>
/// <param name="outcome">How the call ended.</param>
/// <param name="validation">What the validation made of the answer.</param>
/// <param name="rejection">
/// Why the answer was rejected, as <see cref="RoleRejection"/> writes it: a field of the fixed
/// schema and the rule that it broke. Null for an answer that was not rejected.
/// </param>
/// <param name="result">
/// What the model answered, on one line of JSON, with the rationale that is part of it. Null for
/// a call that gave no answer, and for an answer that is not kept.
/// </param>
public sealed class RoleOutput(
    Guid id,
    Guid cycleId,
    Guid userId,
    Role role,
    int attempt,
    Guid modelId,
    int roleTemplateId,
    int marketTemplateId,
    int timeframeTemplateId,
    int profileTemplateId,
    DateTimeOffset calledAt,
    int latencyMs,
    int? inputTokens,
    int? outputTokens,
    int? liveSearches,
    RoleCallOutcome outcome,
    RoleValidation validation,
    string? rejection,
    string? result) : IUserOwned
{
    public Guid Id { get; } = id;

    public Guid CycleId { get; } = cycleId;

    public Guid UserId { get; } = userId;

    public Role Role { get; } = role;

    public int Attempt { get; } = attempt;

    public Guid ModelId { get; } = modelId;

    public int RoleTemplateId { get; } = roleTemplateId;

    public int MarketTemplateId { get; } = marketTemplateId;

    public int TimeframeTemplateId { get; } = timeframeTemplateId;

    public int ProfileTemplateId { get; } = profileTemplateId;

    public DateTimeOffset CalledAt { get; } = calledAt;

    public int LatencyMs { get; } = latencyMs;

    public int? InputTokens { get; } = inputTokens;

    public int? OutputTokens { get; } = outputTokens;

    public int? LiveSearches { get; } = liveSearches;

    public RoleCallOutcome Outcome { get; } = outcome;

    public RoleValidation Validation { get; } = validation;

    public string? Rejection { get; } = rejection;

    public string? Result { get; } = result;
}

internal sealed class RoleOutputConfiguration : IEntityTypeConfiguration<RoleOutput>
{
    public void Configure(EntityTypeBuilder<RoleOutput> builder)
    {
        builder.ToTable("role_outputs");
        builder.HasKey(output => output.Id);

        // The application assigns the id, so a row can be written without asking the database for one.
        builder.Property(output => output.Id).ValueGeneratedNever();
        builder.Property(output => output.CycleId);
        builder.Property(output => output.UserId);
        builder.Property(output => output.Role);
        builder.Property(output => output.Attempt);
        builder.Property(output => output.ModelId);
        builder.Property(output => output.RoleTemplateId);
        builder.Property(output => output.MarketTemplateId);
        builder.Property(output => output.TimeframeTemplateId);
        builder.Property(output => output.ProfileTemplateId);
        builder.Property(output => output.CalledAt);
        builder.Property(output => output.LatencyMs);
        builder.Property(output => output.InputTokens);
        builder.Property(output => output.OutputTokens);
        builder.Property(output => output.LiveSearches);
        builder.Property(output => output.Outcome);
        builder.Property(output => output.Validation);
        builder.Property(output => output.Rejection);
        builder.Property(output => output.Result);

        // A role is called once in a cycle, and once more at most (FR-TEAM-9): each call is a
        // row. It is also the index of the key below, which finds the calls of a cycle.
        builder.HasIndex(output => new { output.CycleId, output.UserId, output.Role, output.Attempt }).IsUnique();

        // The calls of a user over a time, which is what the usage of the user's keys is added
        // up from (FR-RPT-3, FR-KEY-11). The index carries what is added up, so the database
        // can count the calls of a period from it, without the rows, which hold what the
        // models answered.
        builder.HasIndex(output => new { output.UserId, output.CalledAt })
            .IncludeProperties(output => new
            {
                output.CycleId,
                output.ModelId,
                output.Outcome,
                output.InputTokens,
                output.OutputTokens,
                output.LiveSearches,
            });

        // The calls of every user by when they were made, the oldest first: what the job that
        // deletes the calls that are kept no longer reads, once for every batch (NFR-DAT-5).
        // No other index starts with the time, and that job has no owner to ask by.
        builder.HasIndex(output => output.CalledAt);

        // The calls of a cycle go with it, and so with its agent and with the account. The key
        // carries the owner along, so the call of a cycle is its user's (NFR-DAT-9).
        builder.HasOne<DecisionCycle>()
            .WithMany()
            .HasForeignKey(output => new { output.CycleId, output.UserId })
            .HasPrincipalKey(cycle => new { cycle.Id, cycle.UserId })
            .OnDelete(DeleteBehavior.Cascade);

        // A role is a seeded row, which is never deleted while a row points at it.
        builder.HasOne<RoleRow>()
            .WithMany()
            .HasForeignKey(output => output.Role)
            .OnDelete(DeleteBehavior.Restrict);

        // A model of the catalog is never deleted (FR-CAT-5), so the row finds the model that
        // was called, and its provider, for good.
        builder.HasOne<AiModel>()
            .WithMany()
            .HasForeignKey(output => output.ModelId)
            .OnDelete(DeleteBehavior.Restrict);

        // The versions of the templates that the call used (FR-PRM-6). A version is a seeded
        // row that is never changed and never deleted.
        builder.HasOne<PromptTemplate>()
            .WithMany()
            .HasForeignKey(output => output.RoleTemplateId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PromptTemplate>()
            .WithMany()
            .HasForeignKey(output => output.MarketTemplateId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PromptTemplate>()
            .WithMany()
            .HasForeignKey(output => output.TimeframeTemplateId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PromptTemplate>()
            .WithMany()
            .HasForeignKey(output => output.ProfileTemplateId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
