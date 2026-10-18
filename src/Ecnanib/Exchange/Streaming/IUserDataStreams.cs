using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Exchange.Streaming;

/// <summary>Owns one user-data stream session per (key, market), shared by all subscribers (EX-03).</summary>
internal interface IUserDataStreams
{
    IDisposable Subscribe(ExchangeKeyHandle key, MarketType market, Func<UserDataEvent, CancellationToken, ValueTask> handler);

    StreamStatus GetStatus(string keyId, MarketType market);

    IReadOnlyList<StreamStatus> GetStatuses();

    /// <summary>Closes every session of a key (key deleted or rotated, FR-KEY-06).</summary>
    Task StopSessionsAsync(string keyId, CancellationToken ct);
}
