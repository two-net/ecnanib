using Npgsql;

namespace Ecnanib.Tests.Notifications;

/// <summary>A notification as the table holds it, oldest first.</summary>
internal sealed record NotificationRow(long Id, string Type, string Payload, string Link, bool Read, bool EmailDue, bool Emailed);

/// <summary>Reads and changes notifications directly, so assertions don't rely on the application.</summary>
internal static class NotificationRows
{
    public static async Task<List<NotificationRow>> OfAsync(string connectionString, Guid userId, CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var command = dataSource.CreateCommand(
            """
            SELECT id, type, payload::text, link, read_at IS NOT NULL, email_due, emailed_at IS NOT NULL
            FROM notifications WHERE user_id = @user ORDER BY id
            """);
        command.Parameters.AddWithValue("user", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<NotificationRow>();

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new NotificationRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetBoolean(4),
                reader.GetBoolean(5),
                reader.GetBoolean(6)));
        }

        return rows;
    }

    /// <summary>Moves a user's notifications back in time, as if they'd waited out an outage.</summary>
    public static async Task AgeAsync(string connectionString, Guid userId, TimeSpan age, CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var command = dataSource.CreateCommand("UPDATE notifications SET created_at = created_at - @age WHERE user_id = @user");
        command.Parameters.AddWithValue("age", age);
        command.Parameters.AddWithValue("user", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task SetEmailDueAsync(string connectionString, Guid userId, CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var command = dataSource.CreateCommand("UPDATE notifications SET email_due = true WHERE user_id = @user");
        command.Parameters.AddWithValue("user", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>What a user's email choices hold in the table, or null when they've never chosen.</summary>
    public static async Task<string[]?> EmailChoicesAsync(string connectionString, Guid userId, CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var command = dataSource.CreateCommand("SELECT email_notifications FROM user_preferences WHERE user_id = @user");
        command.Parameters.AddWithValue("user", userId);

        return await command.ExecuteScalarAsync(cancellationToken) as string[];
    }

    /// <summary>Stores choices as a user of another build might have, including names a user can't choose.</summary>
    public static async Task SetEmailChoicesAsync(string connectionString, Guid userId, string[] names, CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO user_preferences (user_id, theme, animation, time_zone, email_notifications, updated_at)
            VALUES (@user, 'System', 'System', NULL, @names, now())
            ON CONFLICT (user_id) DO UPDATE SET email_notifications = excluded.email_notifications
            """);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("names", names);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>
/// Accounts written straight into the table, for tests that need many, or a verified address without
/// following a link. Nobody can sign in to one: its hash matches no password.
/// </summary>
internal static class TestUsers
{
    public static async Task<Guid> AddAsync(string connectionString, string email, bool verified, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO users (id, email, normalized_email, password_hash, email_verified_at, failed_sign_ins, created_at)
            VALUES (@id, @email, lower(@email), 'not a hash', CASE WHEN @verified THEN now() END, 0, now())
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("email", email);
        command.Parameters.AddWithValue("verified", verified);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return id;
    }

    public static async Task VerifyAsync(string connectionString, Guid userId, CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var command = dataSource.CreateCommand("UPDATE users SET email_verified_at = now() WHERE id = @id");
        command.Parameters.AddWithValue("id", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
