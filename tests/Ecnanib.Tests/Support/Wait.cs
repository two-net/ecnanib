namespace Ecnanib.Tests.Support;

public static class Wait
{
    /// <summary>Polls a condition with real time, for code driven by a <see cref="FakeTimeProvider"/> but running on the thread pool.</summary>
    public static async Task UntilAsync(Func<bool> condition, string? what = null, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Condition not met{(what is null ? string.Empty : ": " + what)}.");
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Gives background work a moment and then asserts that nothing changed.</summary>
    public static async Task SettleAsync(int ms = 40) => await Task.Delay(ms, TestContext.Current.CancellationToken);
}
