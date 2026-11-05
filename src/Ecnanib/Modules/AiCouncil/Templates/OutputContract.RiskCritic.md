## Output contract: risk verdict

Answer with one JSON object and nothing else: no text before or after it, and no code fence. It has exactly these fields:

- `verdict`: `"approve"`, `"modify"` or `"reject"`.
- `modifications`: for `"modify"`, an object with `stop_price`, `target_prices` and `risk_percent`, each `null` (or empty for the targets) when you don't change it. Every change must reduce risk: a tighter stop, a nearer target or a lower risk percent. `null` for `"approve"` and `"reject"`.
- `concerns`: the problems you found, each a short sentence. Empty if there are none.
- `checklist`: an object with `trend_alignment`, `reward_to_risk`, `event_risk`, `funding` and `reflections`, each an object with `result` (`"pass"`, `"fail"` or `"not_applicable"`) and `note` (one sentence). `funding` is `"not_applicable"` on Spot.
- `rationale`: why this verdict, in a few sentences.
