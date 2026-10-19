using Ecnanib.Accounts.Authentication;
using Ecnanib.Accounts.Configuration;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Accounts.Authentication;

/// <summary>NFR-SEC-01: sign-in is rate-limited per (email, address) and per address, with fixed windows that expire.</summary>
public class SignInThrottleTests
{
    private const string Email = "TRADER@EXAMPLE.COM";
    private const string Address = "10.0.0.1";

    [Fact]
    public void AllowsTheConfiguredAttemptsAndBlocksTheNext()
    {
        var (throttle, _) = Build(attempts: 3);

        throttle.RecordFailure(Email, Address);
        throttle.RecordFailure(Email, Address);
        Assert.Null(throttle.RetryAfter(Email, Address));

        throttle.RecordFailure(Email, Address);
        Assert.Equal(TimeSpan.FromMinutes(15), throttle.RetryAfter(Email, Address));
    }

    [Fact]
    public void ForgetsFailuresAfterTheWindow()
    {
        var (throttle, time) = Build(attempts: 3);
        for (var i = 0; i < 3; i++)
            throttle.RecordFailure(Email, Address);

        time.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(1), throttle.RetryAfter(Email, Address));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(throttle.RetryAfter(Email, Address));
    }

    [Fact]
    public void KeysTheCredentialBucketByEmailAndAddressTogether()
    {
        var (throttle, _) = Build(attempts: 3);
        for (var i = 0; i < 3; i++)
            throttle.RecordFailure(Email, Address);

        Assert.NotNull(throttle.RetryAfter(Email, Address));
        Assert.Null(throttle.RetryAfter(Email, "10.0.0.2"));
        Assert.Null(throttle.RetryAfter("OTHER@EXAMPLE.COM", Address));
    }

    [Fact]
    public void BlocksAnAddressAfterItsOwnLimitWhateverTheEmail()
    {
        var (throttle, _) = Build(attempts: 3, perAddress: 5);
        for (var i = 0; i < 5; i++)
            throttle.RecordFailure($"USER{i}@EXAMPLE.COM", Address);

        Assert.NotNull(throttle.RetryAfter("FRESH@EXAMPLE.COM", Address));
        Assert.NotNull(throttle.RetryAfterForAddress(Address));
        Assert.Null(throttle.RetryAfterForAddress("10.0.0.2"));
        Assert.Null(throttle.RetryAfter("FRESH@EXAMPLE.COM", "10.0.0.2"));
    }

    [Fact]
    public void RegistrationsCountAgainstTheAddressOnly()
    {
        var (throttle, _) = Build(attempts: 3, perAddress: 5);
        for (var i = 0; i < 5; i++)
            throttle.RecordAttempt(Address);

        Assert.NotNull(throttle.RetryAfterForAddress(Address));
        Assert.Null(throttle.RetryAfterForAddress("10.0.0.2"));
        Assert.Null(throttle.RetryAfter(Email, "10.0.0.2"));
    }

    [Fact]
    public void SuccessClearsTheCredentialBucketButNotTheAddressBucket()
    {
        var (throttle, _) = Build(attempts: 3, perAddress: 4);
        for (var i = 0; i < 3; i++)
            throttle.RecordFailure(Email, Address);
        Assert.NotNull(throttle.RetryAfter(Email, Address));

        throttle.RecordSuccess(Email, Address);
        Assert.Null(throttle.RetryAfter(Email, Address));

        throttle.RecordFailure(Email, Address);
        Assert.NotNull(throttle.RetryAfterForAddress(Address));
    }

    [Fact]
    public void ANullAddressSharesOneBucket()
    {
        var (throttle, _) = Build(attempts: 3);
        for (var i = 0; i < 3; i++)
            throttle.RecordFailure(Email, null);

        Assert.NotNull(throttle.RetryAfter(Email, null));
        Assert.Null(throttle.RetryAfter(Email, Address));
    }

    [Fact]
    public void PrunesExpiredBucketsSoMemoryStaysBounded()
    {
        var (throttle, time) = Build();
        for (var i = 0; i < 1000; i++)
            throttle.RecordFailure($"USER{i}@EXAMPLE.COM", Address);
        Assert.Equal(1001, throttle.BucketCount);

        time.Advance(TimeSpan.FromMinutes(16));
        throttle.RecordFailure("LAST@EXAMPLE.COM", "10.0.0.9");

        Assert.Equal(2, throttle.BucketCount);
    }

    private static (SignInThrottle Throttle, FakeTimeProvider Time) Build(int attempts = 10, int perAddress = 100)
    {
        var time = new FakeTimeProvider();
        var options = Options.Create(new AccountsOptions { SignInAttemptsPerWindow = attempts, SignInAttemptsPerAddress = perAddress, SignInWindowMinutes = 15 });
        return (new SignInThrottle(options, time), time);
    }
}
