using Ecnanib.Kernel;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Secrets;

/// <summary>Stores and reads secrets through the contract, and the rows behind them through SQL.</summary>
internal static class SecretApp
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public static string ConnectionString(AppFactory app) =>
        app.Services.GetRequiredService<IConfiguration>().GetConnectionString("Default")!;

    public static async Task<Guid> AddAsync(AppFactory app, string value)
    {
        var contextFactory = app.Services.GetRequiredService<IDbContextFactory<EcnanibDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync(Cancellation);
        using var secret = SecretValue.FromString(value);

        var id = app.Services.GetRequiredService<ISecretStore>().Add(context, secret);
        await context.SaveChangesAsync(Cancellation);

        return id;
    }

    public static async Task<string> RevealAsync(AppFactory app, Guid id)
    {
        using var secret = await app.Services.GetRequiredService<ISecretStore>().RevealAsync(id, Cancellation);

        return secret.Reveal();
    }

    public static Task<MasterKeyProgress> ProgressAsync(AppFactory app) =>
        app.Services.GetRequiredService<IMasterKeyRotation>().GetProgressAsync(Cancellation);

    public static Task<string> KeyIdAsync(string database, Guid id) =>
        Sql.ScalarAsync<string>(database, $"SELECT key_id FROM secrets WHERE id = '{id}'", Cancellation);

    public static Task<string> CiphertextHexAsync(string database, Guid id) =>
        Sql.ScalarAsync<string>(database, $"SELECT encode(ciphertext, 'hex') FROM secrets WHERE id = '{id}'", Cancellation);

    /// <summary>Flips one bit in the middle of the stored bytes, past the header.</summary>
    public static Task CorruptAsync(string database, Guid id) =>
        Sql.ExecuteAsync(
            database,
            $"UPDATE secrets SET ciphertext = set_byte(ciphertext, 30, get_byte(ciphertext, 30) # 1) WHERE id = '{id}'",
            Cancellation);
}

/// <summary>Never runs anything, so a test can see the state before re-encryption.</summary>
internal sealed class NeverRuns : IJobRunner
{
    public Task<JobStatus> RunAsync(string name, TimeSpan timeout, Func<JobContext, CancellationToken, Task> work) =>
        Task.FromResult(JobStatus.NotStarted);
}
