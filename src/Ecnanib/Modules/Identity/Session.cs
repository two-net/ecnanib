using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Modules.Identity;

/// <summary>
/// One sign-in (SRS 6.1, FR-AUTH-07). A row means the session is allowed, so revoking one deletes it.
/// It can still be past a timeout until the expiry job runs, and every read checks for that.
/// </summary>
internal sealed class Session
{
    /// <summary>Long enough for an IPv6 address with a scope.</summary>
    public const int IpAddressMaxLength = 64;

    public const int UserAgentMaxLength = 512;

    public Guid Id { get; init; }

    public required Guid UserId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The last request or user action, to within <see cref="IdentitySettings.SessionTouchInterval"/>.</summary>
    public required DateTimeOffset LastSeenAt { get; init; }

    /// <summary>The absolute limit, which activity doesn't move.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Null when the host didn't report one.</summary>
    public string? IpAddress { get; init; }

    /// <summary>As the client sent it, so it's only ever text to show. Null when it sent none.</summary>
    public string? UserAgent { get; init; }
}

internal sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.ToTable("sessions");
        builder.HasKey(session => session.Id);
        builder.Property(session => session.IpAddress).HasMaxLength(Session.IpAddressMaxLength);
        builder.Property(session => session.UserAgent).HasMaxLength(Session.UserAgentMaxLength);
        builder.HasOne<User>().WithMany().HasForeignKey(session => session.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
