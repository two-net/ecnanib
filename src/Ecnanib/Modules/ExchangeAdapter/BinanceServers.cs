using Ecnanib.Modules.ExchangeAdapter.Contracts;

namespace Ecnanib.Modules.ExchangeAdapter;

/// <summary>Every server's state, one each, for the life of the process.</summary>
internal sealed class BinanceServers(BinanceSettings settings, TimeProvider time) : IExchangeUsage
{
    private readonly Dictionary<ExchangeServer, ServerState> _states = settings.BaseUrls.ToDictionary(
        entry => entry.Key,
        entry => new ServerState(entry.Key, entry.Value, settings.WeightHeadroomPercent));

    public ServerState this[ExchangeServer server] => _states[server];

    public IReadOnlyList<ExchangeServerUsage> Current()
    {
        var now = time.GetUtcNow();

        return [.. ExchangeServers.All.Select(server => _states[server].Snapshot(now))];
    }
}
