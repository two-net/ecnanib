using System.Diagnostics;
using Ecnanib.Modules.Identity.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Identity;

/// <summary>NFR-SEC-02.</summary>
public sealed class PasswordHasherTests(SharedAccountApp app) : IClassFixture<SharedAccountApp>
{
    /// <summary>
    /// Made by another implementation, argon2-cffi, so these prove that the parameters mean what the
    /// constants say, and that a hash doesn't depend on this one library.
    /// </summary>
    private const string CurrentParameters = "$argon2id$v=19$m=65536,t=3,p=1$ZWNuYW5pYi12ZWN0b3ItMQ$rLeZ2a0JtxFlV+PeAxhGp9+Vcz9JHDoMskglPfjW4WI";

    /// <summary>Weaker parameters and a non-ASCII password, so each hash has to be read for its own settings.</summary>
    private const string OtherParameters = "$argon2id$v=19$m=19456,t=2,p=1$ZWNuYW5pYi12ZWN0b3ItMg$/+0zdN4+vPYXQCE/rmo1dwc+p8ue3aLu3k8cmE92vho";

    private IPasswordHasher Hasher => app.Services.GetRequiredService<IPasswordHasher>();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_hash_from_another_implementation_verifies()
    {
        Assert.True(await Hasher.VerifyAsync("correct horse battery staple", CurrentParameters, Cancellation));
        Assert.False(await Hasher.VerifyAsync("correct horse battery stapler", CurrentParameters, Cancellation));
    }

    [Fact]
    public async Task A_hash_is_read_with_its_own_parameters()
    {
        Assert.True(await Hasher.VerifyAsync("pässwörd ✓ 🔐", OtherParameters, Cancellation));
        Assert.False(await Hasher.VerifyAsync("password", OtherParameters, Cancellation));
    }

    [Fact]
    public async Task A_new_hash_uses_the_current_parameters_and_verifies()
    {
        var hash = await Hasher.HashAsync("a password to hash", Cancellation);

        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=1$", hash, StringComparison.Ordinal);
        Assert.True(await Hasher.VerifyAsync("a password to hash", hash, Cancellation));
        Assert.False(await Hasher.VerifyAsync("a password to hasj", hash, Cancellation));
    }

    [Fact]
    public async Task Two_hashes_of_one_password_differ()
    {
        var first = await Hasher.HashAsync("a password to hash", Cancellation);
        var second = await Hasher.HashAsync("a password to hash", Cancellation);

        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// LockoutTests fakes this, so nothing else would notice if it stopped working, and refusing an unknown
    /// address quickly would show that it's unknown. A real hash takes far longer than this floor.
    /// </summary>
    [Fact]
    public async Task Spending_does_the_work_of_a_hash()
    {
        var clock = Stopwatch.StartNew();

        await Hasher.SpendAsync("a password to hash", Cancellation);

        Assert.True(clock.ElapsedMilliseconds >= 10, $"Took {clock.ElapsedMilliseconds} ms.");
    }

    /// <summary>A hash that can't be read has been damaged, and treating that as a wrong password would hide it.</summary>
    [Fact]
    public async Task A_hash_in_another_format_fails_loudly()
    {
        await Assert.ThrowsAsync<FormatException>(() => Hasher.VerifyAsync("anything", "$2a$10$notargon", Cancellation));
    }
}
