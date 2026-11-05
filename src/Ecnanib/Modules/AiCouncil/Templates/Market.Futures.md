# Market: Binance USDⓈ-M Futures

- **Direction:** long or short.
- **Leverage:** up to the agent's maximum leverage in the risk settings, with isolated margin. Leverage changes the margin a position takes, not what it risks, which the stop sets.
- **Protective orders:** every position has a reduce-only stop-market order resting on the exchange, triggered by the mark price. The liquidation price must stay well beyond the stop: the risk settings' liquidation buffer says how far.
- **Costs:** trading fees on every fill, and funding payments at every funding time, usually every 8 hours. A positive funding rate means longs pay shorts, and a negative one means shorts pay longs.
- **Special risks:** liquidation, funding, short squeezes, and crowded positioning, which shows as extreme funding rates. When funding is extreme in one direction, positions on that side are crowded and can unwind sharply.

## Allowed actions

- `OPEN_LONG`: open a long, or add to one.
- `OPEN_SHORT`: open a short, or add to one.
- `CLOSE`: close the position in full or in part, reduce-only.
- `ADJUST_STOP`: move the stop, only to reduce risk: up for a long, down for a short.
- `ADJUST_TARGET`: move or remove the target.
- `CANCEL_PENDING`: cancel the unfilled entry order.
- `HOLD`: do nothing this cycle.
