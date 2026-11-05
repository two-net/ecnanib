## Output contract: trade proposal

Answer with one JSON object and nothing else: no text before or after it, and no code fence. It has exactly these fields. A field that doesn't apply to the action is `null`, or empty for a list.

- `action`: one of the actions the market overlay allows, written exactly as it lists them, such as `"HOLD"`.
- `side`: `"long"` or `"short"`.
- `entry_type`: `"market"` or `"limit"`, for an entry.
- `entry_price`: the limit price for a limit entry. `null` for a market entry.
- `stop_price`: the protective stop for an entry, or the new stop for `ADJUST_STOP`.
- `target_prices`: the take-profit prices, nearest first, for an entry or for `ADJUST_TARGET`.
- `risk_percent`: the risk you ask for, as a percentage of the capital allocation, no more than the risk settings' maximum risk per trade.
- `limit_ttl_candles`: how many candles of the agent's timeframe an unfilled limit entry may wait before it's cancelled.
- `invalidation`: the condition that would prove the idea wrong.
- `rationale`: why this action, in a few sentences.
- `confidence`: how sure you are, from 0 to 1.
