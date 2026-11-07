namespace Ecnanib.Components.Live;

/// <summary>
/// A component's re-read after live events (FR-UI-05). A burst of events, such as a keeper pass's, becomes one read,
/// and an event during a read starts one more after it, so the last read always sees the last change and two never
/// overlap. It runs on the component's renderer, through the <c>InvokeAsync</c> it's given, so it needs no lock. A read
/// that throws is logged as a Warning and the component keeps what it showed, because a throw from an event handler
/// would end the circuit. See Live updates in docs/architecture.md.
/// </summary>
/// <param name="invokeAsync">The component's <c>InvokeAsync</c>.</param>
/// <param name="refresh">Reads again and calls <c>StateHasChanged</c>.</param>
public sealed class LiveRefresh(Func<Func<Task>, Task> invokeAsync, Func<Task> refresh, ILogger logger)
{
    private bool _running;
    private bool _again;

    /// <summary>From any thread. It returns at once.</summary>
    public void Request() => _ = RequestAsync();

    /// <summary>Completes once the read it asked for, or one after it, has run. For a caller that wants to wait.</summary>
    public async Task RequestAsync()
    {
        try
        {
            await invokeAsync(RunAsync);
        }
        catch (Exception exception)
        {
            // A disposed renderer refuses the call, which is the page closing.
            logger.LogDebug(exception, "A live read couldn't start");
        }
    }

    private async Task RunAsync()
    {
        if (_running)
        {
            _again = true;
            return;
        }

        _running = true;

        try
        {
            do
            {
                _again = false;

                try
                {
                    await refresh();
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "A live read failed, so the page keeps what it showed until the next change");
                }
            }
            while (_again);
        }
        finally
        {
            _running = false;
        }
    }
}
