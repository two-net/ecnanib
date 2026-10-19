# Exchange module

Binance Spot and USDⓈ-M Futures gateway (SRS §3.1.2, EX-01..06). Other modules depend only on
`Ecnanib.Exchange.Abstractions` and `AddExchange(...)`; Binance.Net types never leave this folder.

| Need | Use |
|---|---|
| Public data without keys (symbols, filters, klines, price, mark price) | `IExchangeClientFactory.GetPublicData(env, market)` → `IPublicMarketData` |
| Authenticated spot account and orders, OCO exits | `IExchangeClientFactory.GetSpotClient(ExchangeKeyHandle)` → `ISpotAccountClient` |
| Authenticated futures account, positions, leverage, margin type, position mode, orders | `IExchangeClientFactory.GetFuturesClient(ExchangeKeyHandle)` → `IFuturesAccountClient` |
| Live closed candles, prices, mark prices with reconnect and polling fallback | `IMarketStreams.SubscribeCandles / SubscribePrice / SubscribeMarkPrice` |
| Order, fill, balance and position events per key, with snapshot polling fallback | `IExchangeAccountClient.SubscribeUserData(...)` |
| Key deleted or rotated | `IExchangeClientFactory.ReleaseAsync(keyId)` then obtain clients again |
| Health for the status page | `IExchangeHealth` |

Rules the module enforces:

- Environment is fixed per key (`ExchangeKeyHandle.Environment`); endpoints come from the `Exchange` section of `ecnanib.json`.
  Testnet maps to the Spot testnet and the USDⓈ-M **demo** environment.
- Rate limits: Binance.Net's client-side gate waits on the published limits; 429/418 become `ExchangeRateLimitedException`
  with `RetryAfter`. Reads retry with jittered backoff; order placement and cancellation are never retried.
- Secrets: `ExchangeCredentials.ApiSecret` is internal and only `BinanceClientRegistry` reads it. Logs and exception
  messages carry the key id and the masked key only. Library log categories are capped at Warning in `ecnanib.json`.
- Futures orders always use one-way mode (`positionSide=BOTH`).

Tests: `dotnet test` runs offline. Live checks are opt-in through `tests/.exchange-keys.json` (git-ignored):

```json
{ "publicProduction": true,
  "spotTestnet": { "apiKey": "...", "apiSecret": "..." },
  "futuresTestnet": { "apiKey": "...", "apiSecret": "..." } }
```

Run them with `dotnet test -- --filter-trait "Category=Live"`; the default run excludes nothing but every live test
skips when its section is missing. Use `-- --filter-not-trait "Category=Live"` to exclude them explicitly.
