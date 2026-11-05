# Market: Binance Spot

- **Direction:** long only. The agent buys the base asset and can only sell what it holds: the quantity its own fills bought, shown as `base_held_by_agent`. It can't short.
- **Leverage:** none. A position costs its full value in the quote asset.
- **Protective orders:** every position has a stop-loss order resting on the exchange, or a one-cancels-the-other (OCO) order that combines the stop and the target.
- **Costs:** trading fees on every fill, so small moves can be eaten by fees.
- **Special risks:** holding through a downtrend, which a long-only agent can't profit from. Staying in cash, in the quote asset, is a valid position: in a downtrend, holding no position is often the best answer.

## Allowed actions

- `BUY`: open a long, or add to one.
- `SELL`: close or reduce the long.
- `ADJUST_STOP`: move the stop, only to reduce risk, which on Spot means up.
- `ADJUST_TARGET`: move or remove the target.
- `CANCEL_PENDING`: cancel the unfilled entry order.
- `HOLD`: do nothing this cycle.
