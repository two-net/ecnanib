using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Accounts;

/// <summary>
/// A browser that is signed in to an account, as a row of the <c>sessions</c> table (FR-ACC-2).
/// The browser holds the token in a cookie, and the row holds its hash. See docs/accounts.md.
/// </summary>
/// <param name="id">Assigned by the application when the session starts.</param>
/// <param name="userId">The account the browser is signed in to.</param>
/// <param name="tokenHash">The SHA-256 hash of the token in the cookie.</param>
/// <param name="createdAt">When the user signed in.</param>
/// <param name="lastActiveAt">
/// When the session was last used, to within the time between two writes. The session ends when
/// this is longer ago than the configured period of inactivity (NFR-SEC-1).
/// </param>
public sealed class Session(Guid id, Guid userId, byte[] tokenHash, DateTimeOffset createdAt, DateTimeOffset lastActiveAt)
{
    public Guid Id { get; } = id;

    public Guid UserId { get; } = userId;

    public byte[] TokenHash { get; } = tokenHash;

    public DateTimeOffset CreatedAt { get; } = createdAt;

    public DateTimeOffset LastActiveAt { get; } = lastActiveAt;
}

internal sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.ToTable("sessions");
        builder.HasKey(session => session.Id);

        // The application assigns the id, so a row can be written without asking the database for one.
        builder.Property(session => session.Id).ValueGeneratedNever();
        builder.Property(session => session.UserId);
        builder.Property(session => session.TokenHash);
        builder.Property(session => session.CreatedAt);
        builder.Property(session => session.LastActiveAt);

        // A session is looked up by the hash of its token, on every signed-in request.
        builder.HasIndex(session => session.TokenHash).IsUnique();

        // The sessions belong to the account and go with it when it is deleted. The key also
        // gives the index that ending every session of an account uses.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(session => session.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
