namespace Ecnanib.Modules.ExchangeAdapter.Contracts;

/// <summary>
/// Told when Binance bans this server's address from one of its servers with HTTP 418 (EXT-BIN-04).
/// The adapter can't use Notifications, so Execution implements this and notifies administrators. It's
/// called once per new or longer ban, on the request that found it, so it must return at once. Nothing
/// keeps a ban across a restart: the next 418 reports it again.
/// </summary>
public interface IExchangeBanObserver
{
    void OnBanned(ExchangeBan ban);
}

/// <param name="Until">When requests to the server resume.</param>
public sealed record ExchangeBan(ExchangeServer Server, DateTimeOffset Until);
