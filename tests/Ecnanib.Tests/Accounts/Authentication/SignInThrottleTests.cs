using Ecnanib.Accounts.Authentication;
using Ecnanib.Accounts.Configuration;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Accounts.Authentication;

/// <summary>NFR-SEC-01: sign-in is rate-limited per (email, address) and per address, with fixed windows that expire; an attempt is counted before it is checked.</summary>
public class SignInThrottleTests
{
    private const string Email = "TRADER@EXAMPLE.COM";
    private const string Address = "10.0.0.1";

    [Fact]
    public void AllowsTheConfiguredAttemptsAndBlocksTheNext()
    {
        var (throttle, _) = Build(attempts: 3);

        Assert.Null(throttle.TryBegin(Email, Address));
        Assert.Null(throttle.TryBegin(Email, Address));
        Assert.Null(throttle.TryBegin(Email, Address));

        Assert.Equal(TimeSpan.FromMinutes(15), throttle.TryBegin(Email, Address));
    }

    [Fact]
    public void ForgetsFailuresAfterTheWindow()
    {
        var (throttle, time) = Build(attempts: 3);
        for (var i = 0; i < 3; i++)
            throttle.TryBegin(Email, Address);

        time.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(1), throttle.TryBegin(Email, Address));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(throttle.TryBegin(Email, Address));
    }

    [Fact]
    public void ARefusedAttemptIsNotCounted()
    {
        var (throttle, time) = Build(attempts: 3, perAddress: 4);
        for (var i = 0; i < 3; i++)
            throttle.TryBegin(Email, Address);
        for (var i = 0; i < 10; i++)
            Assert.NotNull(throttle.TryBegin(Email, Address));

        // The refusals did not count against the address: one more failure from another email is still allowed.
        Assert.Null(throttle.TryBegin("OTHER@EXAMPLE.COM", Address));
        Assert.NotNull(throttle.TryBegin("THIRD@EXAMPLE.COM", Address));

        time.Advance(TimeSpan.FromMinutes(15));
        Assert.Null(throttle.TryBegin(Email, Address));
    }

    [Fact]
    public void KeysTheCredentialBucketByEmailAndAddressTogether()
    {
        var (throttle, _) = Build(attempts: 3);
        for (var i = 0; i < 3; i++)
            throttle.TryBegin(Email, Address);

        Assert.NotNull(throttle.TryBegin(Email, Address));
        Assert.Null(throttle.TryBegin(Email, "10.0.0.2"));
        Assert.Null(throttle.TryBegin("OTHER@EXAMPLE.COM", Address));
    }

    [Fact]
    public void BlocksAnAddressAfterItsOwnLimitWhateverTheEmail()
    {
        var (throttle, _) = Build(attempts: 3, perAddress: 5);
        for (var i = 0; i < 5; i++)
            Assert.Null(throttle.TryBegin($"USER{i}@EXAMPLE.COM", Address));

        Assert.NotNull(throttle.TryBegin("FRESH@EXAMPLE.COM", Address));
        Assert.NotNull(throttle.TryBeginForAddress(Address));
        Assert.Null(throttle.TryBeginForAddress("10.0.0.2"));
        Assert.Null(throttle.TryBegin("FRESH@EXAMPLE.COM", "10.0.0.2"));
    }

    [Fact]
    public void RegistrationsCountAgainstTheAddressOnly()
    {
        var (throttle, _) = Build(attempts: 3, perAddress: 5);
        for (var i = 0; i < 5; i++)
            Assert.Null(throttle.TryBeginForAddress(Address));

        Assert.NotNull(throttle.TryBeginForAddress(Address));
        Assert.Null(throttle.TryBeginForAddress("10.0.0.2"));
        Assert.Null(throttle.TryBegin(Email, "10.0.0.2"));
    }

    [Fact]
    public void SuccessClearsTheCredentialBucketAndRefundsTheAddressBucket()
    {
        var (throttle, _) = Build(attempts: 3, perAddress: 4);
        throttle.TryBegin(Email, Address);
        throttle.TryBegin(Email, Address);
        Assert.Null(throttle.TryBegin(Email, Address));
        throttle.RecordSuccess(Email, Address);

        // Two failures remain on the address; the credential bucket starts over.
        Assert.Null(throttle.TryBegin(Email, Address));
        Assert.Null(throttle.TryBegin(Email, Address));
        Assert.NotNull(throttle.TryBegin(Email, Address));
        Assert.NotNull(throttle.TryBeginForAddress(Address));
        Assert.NotNull(throttle.TryBegin("OTHER@EXAMPLE.COM", Address));
    }

    [Fact]
    public void ANullAddressSharesOneBucket()
    {
        var (throttle, _) = Build(attempts: 3);
        for (var i = 0; i < 3; i++)
            throttle.TryBegin(Email, null);

        Assert.NotNull(throttle.TryBegin(Email, null));
        Assert.Null(throttle.TryBegin(Email, Address));
    }

    [Fact]
    public void ABurstOfParallelAttemptsIsCutAtTheLimit()
    {
        var (throttle, _) = Build(attempts: 3);
        var allowed = 0;

        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ =>
        {
            if (throttle.TryBegin(Email, Address) is null)
                Interlocked.Increment(ref allowed);
        });

        Assert.Equal(3, allowed);
    }

    [Fact]
    public void PrunesExpiredBucketsSoMemoryStaysBounded()
    {
        var (throttle, time) = Build(perAddress: 2000);
        for (var i = 0; i < 1000; i++)
            throttle.TryBegin($"USER{i}@EXAMPLE.COM", Address);
        Assert.Equal(1001, throttle.BucketCount);

        time.Advance(TimeSpan.FromMinutes(16));
        throttle.TryBegin("LAST@EXAMPLE.COM", "10.0.0.9");

        Assert.Equal(2, throttle.BucketCount);
    }

    private static (SignInThrottle Throttle, FakeTimeProvider Time) Build(int attempts = 10, int perAddress = 100)
    {
        var time = new FakeTimeProvider();
        var options = Options.Create(new AccountsOptions { SignInAttemptsPerWindow = attempts, SignInAttemptsPerAddress = perAddress, SignInWindowMinutes = 15 });
        return (new SignInThrottle(options, time), time);
    }
}
