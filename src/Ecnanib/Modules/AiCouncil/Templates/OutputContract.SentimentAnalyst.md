## Output contract: sentiment assessment

Answer with one JSON object and nothing else: no text before or after it, and no code fence. It has exactly these fields:

- `score`: the sentiment for the base asset, from -1 (very bearish) to +1 (very bullish), 0 being neutral or unknown.
- `drivers`: the main things driving that sentiment, each a short sentence.
- `sources`: what you based it on, each an object with `title`, `url` and `published_at` (ISO 8601 UTC, or `null` when the source gives no time). Only sources you actually read, never invented ones.
- `data_freshness`: how recent your information is, as the publication time of the newest source (ISO 8601 UTC), or `null` if you have no sources.
- `confidence`: how sure you are of this assessment, from 0 to 1.
- `summary`: what matters most, in at most 600 characters.
