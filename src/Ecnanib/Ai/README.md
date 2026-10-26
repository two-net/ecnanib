# AI provider module

Anthropic (Claude), Google (Gemini), OpenAI (GPT) and xAI (Grok) over their REST APIs (SRS §3.1.3, AI-01..04). Other
modules depend only on `Ecnanib.Ai.Abstractions`, `AiOptions` (the model-sync module reads the sync interval and the
miss threshold) and `AddAiProviders(...)`; wire types never leave this folder. Every
call carries the key of the user who owns the agent (C-05, AI-03); the system holds none (C-11, FR-CFG-03).

| Need | Use |
|---|---|
| Validate a provider key, or list its models for the catalog (FR-KEY-03, FR-SYNC-01) | `IAiClientFactory.GetClient(provider).ListModelsAsync(key, ct)` → `AiModelInfo` (id, display name, token limits, raw `Capabilities`) |
| Role prompt: system and user content in, text back (AI-02) | `CompleteAsync(key, new AiRequest(model, system, user), ct)` → `AiCompletion.Text` |
| Structured output (AI-02, FR-AI-07) | `new AiRequest(..., OutputSchema: schema)` → `Text` is the JSON document when `IsStructured`; validate it yourself |
| Tokens and latency per call (AI-04, FR-AI-14, NFR-COST-01) | `AiCompletion.Usage`, `AiCompletion.Latency` |
| Model rejected at call time (FR-SYNC-04) | `AiRequestException` with `Kind == AiErrorKind.UnknownModel` and `Model` set |
| Invalid key (FR-KEY-07) | `AiAuthException` |
| Rate limit or outage (FR-AI-13) | retried with jittered backoff inside the call, then `AiRateLimitedException` (`RetryAfter`) or `AiUnavailableException` |

Rules the module enforces:

- A key travels only in its own provider's auth header to that provider's configured base URL, which must be a host
  under the provider's own domain (`anthropic.com`, `googleapis.com`, `openai.com`, `x.ai`; startup validation refuses
  anything else, NFR-SEC-04); a handle for another provider is rejected before any request (FR-KEY-05).
- `AiCredentials.ApiKey` is internal and read only by each client's `ApplyAuth`. Logs and exception messages carry the
  key id and the masked key; provider error text is scrubbed of the key value; `HttpClient` logging is capped at
  Warning in `ecnanib.json` and the auth headers are redacted (FR-KEY-04, FR-LOG-05).
- Requests carry the model, the system and user content, the output-token cap and the optional schema, nothing else:
  no temperature, thinking or provider-specific switches, and no model identifiers in code (FR-SYNC-05).
- Output tokens are capped by `Providers:MaxOutputTokens`; a request may ask for fewer, never more (NFR-COST-01): the
  smaller of the two goes out as Claude's `max_tokens`, the Responses API's `max_output_tokens` and Gemini's
  `maxOutputTokens`. Reasoning tokens count against the cap on GPT, Grok and Gemini and come back as
  `Usage.ReasoningTokens`, a part of the output tokens.
- Each attempt times out after `Providers:CallTimeoutSeconds`; the caller's cancellation token is the deadline
  (FR-AI-13). Only transient failures (network, timeout, rate limit, overload, 5xx) are retried.
- Every attempt is logged with provider, operation, purpose, model, status, latency and tokens, never prompt content
  (FR-LOG-04).
- Refusals, safety blocks and truncation are results (`AiCompletion.FinishReason`), not exceptions, so the usage of
  every answer is recorded (AI-04); a response the provider marks failed, or one with no candidate and no block reason,
  is an exception and carries none. Only `Stop` means the text is complete.
- Base URLs include the API version path (`Providers:<Provider>:BaseUrl`); Claude's `anthropic-version` header comes
  from `Providers:Claude:ApiVersion`. OpenAI and xAI are called through the Responses API, Gemini through
  `generateContent`.

Structured output: the module passes the schema through unchanged. A schema every provider accepts has a root
`object`, `additionalProperties: false` on every object, every property listed in `required` (optional values as
`"type": ["string", "null"]`), and uses only `type`, `properties`, `required`, `items`, `enum` and `description`.
Claude (`output_config.format`) and GPT (`text.format` with `strict: true`) require that strict form; Grok accepts a
narrower JSON Schema subset than OpenAI; Gemini (`responseJsonSchema`) accepts a broader one. Check each provider's
current documentation when writing the Appendix C schema.

Tests: `dotnet test` runs offline. Live checks are opt-in through `tests/.ai-keys.json` (git-ignored):

```json
{ "claude": { "apiKey": "...", "model": "<model-id>" },
  "gemini": { "apiKey": "...", "model": "<model-id>" },
  "gpt":    { "apiKey": "...", "model": "<model-id>" },
  "grok":   { "apiKey": "...", "model": "<model-id>" } }
```

`model` names the model the completion checks use (none is hard-coded, FR-SYNC-05). Run them with
`dotnet test -- --filter-trait "Category=Live"`; every live test skips when its section is missing. Use
`-- --filter-not-trait "Category=Live"` to exclude them explicitly.
