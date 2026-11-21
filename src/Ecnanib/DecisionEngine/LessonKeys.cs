using Ecnanib.Experience;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// The key from the lessons of Experience to the position that each is of:
/// <c>lessons (position_id, user_id)</c> points at <c>positions (id, user_id)</c>. See "The data"
/// in docs/experience.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is declared here, and not beside the entity.</b> A key between two components is
/// configured in the one that may reference the other, and Experience may not use the Decision
/// Engine. So the table has its own configuration, in Experience, for everything but its keys to
/// the Decision Engine, and this one. See "The model" in docs/persistence.md.
/// </para>
/// <para>
/// <b>It is named for the position, so that it is applied after the configuration of the
/// position:</b> see <see cref="PositionFundingConfiguration"/>.
/// </para>
/// </remarks>
internal sealed class PositionLessonConfiguration : IEntityTypeConfiguration<Lesson>
{
    public void Configure(EntityTypeBuilder<Lesson> builder)
    {
        // The lesson of a position goes with it, which goes with its agent, and with the account
        // when that is deleted. The key carries the owner along, so the lesson of a position is
        // its user's (NFR-DAT-9).
        builder.HasOne<Position>()
            .WithMany()
            .HasForeignKey(lesson => new { lesson.PositionId, lesson.UserId })
            .HasPrincipalKey(position => new { position.Id, position.UserId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>
/// The key from a lesson to the version of the lesson instructions that it was written from
/// (FR-PRM-6): <c>lessons (template_id)</c> points at <c>prompt_templates (id)</c>. See
/// "The lesson of a closed position" in docs/prompts.md.
/// </summary>
/// <remarks>
/// It is declared here for what <see cref="PositionLessonConfiguration"/> says, and is named for
/// the template, so that it is applied after the configuration of the templates.
/// </remarks>
internal sealed class PromptTemplateLessonConfiguration : IEntityTypeConfiguration<Lesson>
{
    public void Configure(EntityTypeBuilder<Lesson> builder)
    {
        // A version is a row that is never changed and never deleted, as every seeded row that
        // something points at: so a lesson says for good what its model was told.
        builder.HasOne<PromptTemplate>()
            .WithMany()
            .HasForeignKey(lesson => lesson.TemplateId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
