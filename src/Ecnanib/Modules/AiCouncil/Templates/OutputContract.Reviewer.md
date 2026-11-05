## Output contract: reflection

Answer with one JSON object and nothing else: no text before or after it, and no code fence. It has exactly these fields:

- `setup_summary`: the setup that was traded and why, in a few sentences.
- `outcome`: an object with `r_multiple` and `pnl`, copied from the trade's record. Don't compute your own.
- `what_worked`: what went right, each a short sentence. Empty if nothing did.
- `what_failed`: what went wrong, each a short sentence. Empty if nothing did.
- `lesson`: one sentence that a future decision on this agent can act on.
- `tags`: a few short lowercase labels for the setup and the lesson, such as `"breakout"` or `"late-entry"`.
