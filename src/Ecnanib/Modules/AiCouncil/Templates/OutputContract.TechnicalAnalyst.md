## Output contract: technical assessment

Answer with one JSON object and nothing else: no text before or after it, and no code fence. It has exactly these fields:

- `trend`: the trend on the agent's timeframe, `"up"`, `"down"` or `"range"`.
- `htf_trend`: the trend on the higher timeframe, `"up"`, `"down"` or `"range"`.
- `support_levels`: the key support prices, as numbers, nearest to the current price first. Empty if there are none.
- `resistance_levels`: the key resistance prices, as numbers, nearest to the current price first. Empty if there are none.
- `signals`: the signals you see, each an object with `name` (such as `"ema20 above ema50"`), `direction` (`"bullish"`, `"bearish"` or `"neutral"`) and `strength` (0 to 1).
- `volatility_regime`: `"low"`, `"normal"` or `"high"`.
- `setup_present`: `true` if there is a setup worth trading under the timeframe's guidance, otherwise `false`.
- `confidence`: how sure you are of this assessment, from 0 to 1.
- `summary`: what matters most, in at most 600 characters.
