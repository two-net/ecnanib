using System.Threading.Channels;
using Ecnanib.Accounts.Abstractions;
using Ecnanib.Agents.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace Ecnanib.Components.Live;

/// <summary>
/// The base of every interactive island that reflects agent events without a reload (FR-UI-04, NFR-PERF-03). It
/// resolves the signed-in user from the authentication state (never from <c>HttpContext</c>, which a circuit does not
/// have), loads once for the prerender, and only once the circuit is interactive subscribes to the user's
/// <see cref="IAgentEvents"/>. Events request a refresh; a bounded channel coalesces bursts into one re-read after a
/// short debounce, and a signal that arrives during a read yields one trailing read, so nothing is missed. The
/// subscription ends when the session is no longer the same user's (NFR-SEC-03) and when the circuit goes away.
/// </summary>
public abstract class LiveComponentBase : ComponentBase, IAsyncDisposable
{
    /// <summary>Bursts (a fill, its protection and the trade update arrive within milliseconds) become one re-read.</summary>
    protected static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private IDisposable? _subscription;
    private Task? _loop;
    private bool _authenticationHooked;
    private ILogger? _logger;

    [CascadingParameter]
    private Task<AuthenticationState> AuthenticationState { get; set; } = default!;

    [Inject]
    private IAgentEvents Events { get; set; } = default!;

    [Inject]
    private AuthenticationStateProvider AuthenticationProvider { get; set; } = default!;

    [Inject]
    private ILoggerFactory LoggerFactory { get; set; } = default!;

    /// <summary>The signed-in user; every read is scoped to it (NFR-SEC-03).</summary>
    protected Guid UserId { get; private set; }

    /// <summary>True once the circuit is interactive and the event subscription is in place.</summary>
    protected bool IsLive { get; private set; }

    /// <summary>Cancelled when the component is disposed.</summary>
    protected CancellationToken Lifetime => _cts.Token;

    protected ILogger Logger => _logger ??= LoggerFactory.CreateLogger(GetType());

    protected override async Task OnInitializedAsync()
    {
        var state = await AuthenticationState;
        UserId = state.User.RequireUserId();
        await LoadAsync(Lifetime);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || !RendererInfo.IsInteractive)
            return;

        _subscription = Events.Subscribe(UserId, HandleAsync);
        AuthenticationProvider.AuthenticationStateChanged += OnAuthenticationStateChanged;
        _authenticationHooked = true;
        _loop = RunRefreshLoopAsync(Lifetime);
        IsLive = true;
        await OnLiveAsync(Lifetime);
        // Whatever changed between the prerender and the circuit's start is picked up by one re-read.
        RequestRefresh();
    }

    /// <summary>Reads everything the island shows into its fields; called for the prerender and after every coalesced burst of events.</summary>
    protected abstract Task LoadAsync(CancellationToken cancellationToken);

    /// <summary>Which of the user's events concern this island (an agent page filters by its agent id).</summary>
    protected virtual bool Accepts(AgentEvent agentEvent) => true;

    /// <summary>What an accepted event does; by default it requests a re-read. Runs on a thread-pool thread: use <see cref="RenderAsync"/> to touch state.</summary>
    protected virtual ValueTask OnEventAsync(AgentEvent agentEvent)
    {
        RequestRefresh();
        return ValueTask.CompletedTask;
    }

    /// <summary>Called once the circuit is interactive, for subscriptions that need a live connection (price feeds).</summary>
    protected virtual Task OnLiveAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected virtual ValueTask DisposeLiveAsync() => ValueTask.CompletedTask;

    /// <summary>Asks for a re-read; several requests in flight collapse into one.</summary>
    protected void RequestRefresh() => _signal.Writer.TryWrite(true);

    /// <summary>Runs <paramref name="update"/> on the renderer's context and renders; for in-memory changes an event brings on its own.</summary>
    protected Task RenderAsync(Action update) => InvokeAsync(() =>
    {
        update();
        StateHasChanged();
    });

    public async ValueTask DisposeAsync()
    {
        StopListening();
        if (_authenticationHooked)
            AuthenticationProvider.AuthenticationStateChanged -= OnAuthenticationStateChanged;
        await _cts.CancelAsync();
        _signal.Writer.TryComplete();
        if (_loop is { } loop)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await DisposeLiveAsync();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    private async ValueTask HandleAsync(AgentEvent agentEvent)
    {
        if (_cts.IsCancellationRequested || !Accepts(agentEvent))
            return;
        try
        {
            await OnEventAsync(agentEvent).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            // Never back into the publisher: a UI failure must not become a warning on the trading side.
            Logger.LogWarning(ex, "Live view {Component} could not handle {Event}", GetType().Name, agentEvent.GetType().Name);
        }
    }

    private async Task RunRefreshLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var requested in _signal.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                _ = requested;
                await Task.Delay(Debounce, cancellationToken).ConfigureAwait(false);
                while (_signal.Reader.TryRead(out var pending))
                {
                    _ = pending;
                }

                try
                {
                    await LoadAsync(cancellationToken).ConfigureAwait(false);
                    await InvokeAsync(StateHasChanged).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (InvalidOperationException ex) when (cancellationToken.IsCancellationRequested)
                {
                    Logger.LogDebug(ex, "Live view {Component} refreshed after its circuit ended", GetType().Name);
                    return;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Live view {Component} could not refresh; the next event tries again", GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnAuthenticationStateChanged(Task<AuthenticationState> task) => _ = CheckAsync(task);

    private async Task CheckAsync(Task<AuthenticationState> task)
    {
        try
        {
            var state = await task.ConfigureAwait(false);
            if (state.User.GetUserId() != UserId)
                StopListening();
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Live view {Component} could not read the authentication state; it stops listening", GetType().Name);
            StopListening();
        }
    }

    private void StopListening()
    {
        _subscription?.Dispose();
        _subscription = null;
        IsLive = false;
    }
}
