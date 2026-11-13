# AI providers

Every call to a model goes through the AI Provider Gateway: one adapter each for Claude, Gemini, GPT, and Grok, behind a common interface (SRS 3.1, NFR-ARC-3). A call takes prompt text and a result schema, and returns the structured result with its token usage and its latency. The prompt is the same for every provider. Only how the structured result is asked for is a provider's own (FR-PRM-10).

The code is in `src/Ecnanib/AiProviderGateway`. The rules below are enforced by tests in `tests/Ecnanib.Tests/AiProviderGateway`, `tests/Ecnanib.Tests/AiProviderRouterTests.cs`, and `tests/Ecnanib.Tests/Architecture`.

How a key is checked with its provider is in [credentials.md](credentials.md). How a call is logged is in [logging.md](logging.md). How a provider is added is in [architecture.md](architecture.md).

## The call

```csharp
public interface IAiProviderGateway
{
    IReadOnlyCollection<AiProviderCode> Providers { get; }

    Task<AiCallResult> CompleteAsync(
        AiProviderCode provider, AiCallRequest request, string apiKey, CancellationToken cancellationToken);
}

public sealed record AiCallRequest(string Model, string Prompt, string ResultSchema);

public sealed record AiCallResult(AiCallOutcome Outcome, string? ResultJson, AiTokenUsage? Usage, TimeSpan Latency);

public readonly record struct AiTokenUsage(int InputTokens, int OutputTokens);
```

| Rule | Detail |
|---|---|
| **Callers name the provider by its code and never branch on it.** | `AiProviderCode` is an open set of codes, and not an enum. The four adapters are private to the gateway. What calls a model does the same for each of them. |
| **The prompt is sent as it is** (FR-PRM-10). | It is the one message of the user in every provider's request. An adapter adds no instruction to it, and sends no system prompt, no sampling parameter, and no setting for how much a model reasons. |
| The result schema is a JSON Schema, as text. | An adapter puts it where its provider takes a schema, unchanged. See "Result schemas" for what all four take. |
| **The call is made once.** | The gateway does not call again after a failure. Whether a call is made again is its caller's to decide (FR-TEAM-9). |
| **The model that was asked for answers, or none.** | A provider may offer to hand a refused request to another model. The gateway does not take that: the system never puts another model in the place of the one that was chosen (FR-CAT-8), and experience is measured for each model. |
| The key comes with the call. | See "The key". |

`CompleteAsync` ends with an error in three cases, none of which is a provider's doing: the caller cancelled, the provider has no adapter or no entry in the configuration file, or the call has no model, no prompt, no key, or a schema that is not a JSON object. Everything else is an outcome.

## Outcomes

A call that gives no result is not an error. Its result says how it ended, so that a caller, which may name no type of the network, can tell what to do next.

| Outcome | When | What it is for |
|---|---|---|
| `Succeeded` | The model finished its answer, and the answer is a JSON object. `ResultJson` holds it, as the model wrote it. | The result. |
| `InvalidResult` | The provider answered without a result that can be used: the model refused, the answer was cut short, the prompt was blocked, or the text is not a JSON object. | The call may be made again (FR-TEAM-9). |
| `TimedOut` | The call took longer than the provider's time limit. | The call may be made again (FR-TEAM-9). |
| `AuthenticationFailed` | The provider answered as it answers a wrong key. | The key is checked again, with `IKeyValidation.RevalidateAiKeyAsync` (FR-KEY-3). The outcome does not say that the key is wrong: the check decides that. |
| `ModelNotFound` | The provider's own error says that it has no such model. | The agent needs its user (FR-CAT-8). |
| `Failed` | Anything else: no connection, a limit on requests, a fault of the provider, a request that it would not take, or an answer that is not what the provider sends. | Counted where repeated failures are (NFR-REL-4). |

- **`ResultJson` is there when the call succeeded, and never otherwise.** What a model wrote before it was cut short is not handed on.
- **The gateway does not check a result against its schema.** It asks the provider for one that matches, and takes a finished answer that is a JSON object. Whether the object matches the schema, and whether its values are in range, is for what validates the result (FR-TEAM-6, NFR-SEC-7).
- **Only a provider's own word is a missing model.** A 404 from a wrong address in the configuration file, or from something between the application and the provider, is `Failed`. It must not send a user to look after a model that is there.
- **`Usage` is null when the provider did not count.** Null is unknown, and not none: a call that timed out may still be charged for. An `InvalidResult` carries its usage, because the tokens were spent.

## What each provider is sent, and how its answer reads

| | Claude | Gemini | GPT | Grok |
|---|---|---|---|---|
| Call | `POST /v1/messages` | `POST /v1beta/models/{model}:generateContent` | `POST /v1/responses` | `POST /v1/responses` |
| Key | `x-api-key` | `x-goog-api-key` | Bearer token | Bearer token |
| Prompt | `messages[0].content` | `contents[0].parts[0].text` | `input` | `input` |
| Schema | `output_config.format.schema` | `generationConfig.responseJsonSchema`, with `responseMimeType: application/json` | `text.format.schema`, with `strict: true` | The same |
| Also sent | `max_tokens: 16000` | Nothing | `store: false` | `store: false` |
| Text of the answer | `content[]` blocks of type `text` | `candidates[0].content.parts[]`, without the parts marked `thought` | `output[]` messages, `content[]` parts of type `output_text` | The same |
| Finished | `stop_reason: end_turn` | `finishReason: STOP` | `status: completed`, and no part of type `refusal` | The same |
| A wrong key | 401 | 401, or 400 with the reason `API_KEY_INVALID` | 401 | 401, or 400 with a message about the API key |
| No such model | 404 with `not_found_error` | 404 with the status `NOT_FOUND` | The error code `model_not_found` | 404 with an error of Grok's |

- **Any other way of ending is no result**, one that this code does not know included: `max_tokens`, `refusal`, `MAX_TOKENS`, `SAFETY`, `incomplete`, and the rest are `InvalidResult`.
- **`max_tokens` is the one thing sent that is not asked for.** Claude requires it. It is a constant of the adapter, with room for a model that reasons before it answers. The other providers are sent no limit.
- **`store: false`** keeps GPT and Grok from keeping the prompt and the answer, which both do for thirty days unless told otherwise (NFR-SEC-8).
- **The model of Gemini is part of the address.** The adapter takes it with or without the `models/` of the list of models, and escapes it, so an id cannot say where the call goes.
- GPT and Grok share one format, in `Adapters/ResponsesFormat.cs`. What they answer an error with differs, and each adapter says that for itself.
- What a provider has in common between the check of a key and a call to a model, the header of the key and the answer to a wrong one, is in `Adapters/<Provider>/<Provider>Api.cs`.

## Usage

The tokens of a call are counted the same way for every provider, so that they can be added up and priced: the input is every token of the prompt, and the output is every token that is charged as output.

| | Input | Output |
|---|---|---|
| Claude | `input_tokens`, with `cache_creation_input_tokens` and `cache_read_input_tokens` | `output_tokens` |
| Gemini | `promptTokenCount` | `candidatesTokenCount` and `thoughtsTokenCount` |
| GPT, Grok | `input_tokens` | `total_tokens` less `input_tokens`, or `output_tokens` where there is no total |

- Claude counts what it read from a cache, and what it wrote to one, apart from the rest of the input.
- Gemini counts what the model reasoned apart from its answer, and charges both as output.
- GPT counts what the model reasoned inside `output_tokens`, and Grok's examples count it beside. What the total holds beyond the input is right for both.

## The time limit

`AiProviders:<Provider>:TimeoutSeconds` in the configuration file, from 1 to 600, is how long a call to a model of that provider may take, and `AiProviders:<Provider>:BaseUrl` is where it goes. See [configuration.md](configuration.md).

- **It is the only limit on the call.** It covers the sending and the reading of the answer. The client that the gateway makes its calls with has no limit of its own: a client of the host's factory would give up after 100 seconds.
- A call that runs into it ends as `TimedOut`.
- The latency of a result is measured around the same span: from the request to the answer, or to the failure.
- A model that reasons for long needs a limit to match. The file's 60 seconds is a starting point.

## The key

A user's key for the provider is given with each call (FR-KEY-1).

| Rule | Detail |
|---|---|
| **The gateway keeps no key.** | An adapter puts the key into a header of the one request, and holds it nowhere else: not in a field, and not in a header that a client sends by default. The adapters are the same objects for every user. |
| **The gateway fetches no key.** | It may use neither Credentials nor the service's own `CatalogKey`: the architecture rules and `NoSharedKeyTests` hold it to that. What decrypts a stored key hands it over for the one call (FR-KEY-9). |
| A key is never in an address. | Gemini would also take it there. An address is logged, so it travels in the header. |
| A key is in no log event and no error. | The log of outbound calls leaves headers out. An error of the gateway names the provider, and not what it was given. |
| **What a provider writes stays in the gateway.** | A provider's error can repeat a part of the key, and an answer can hold anything. Neither reaches a result, an error, or a log event above the debug level. The gateway logs how an answer ended in a word of its own, and the provider's word for it only when that is one short word, such as `max_tokens`. |

At the debug level the log of outbound calls writes the bodies of requests and answers, the prompt and a provider's error included. It masks what has the shape of a key: see "Masking" in [logging.md](logging.md).

## Result schemas

A schema is sent to the provider as it is, so a schema that is meant for any of them stays inside what all four take:

- The root is an object. Every object lists all of its properties in `required`, and sets `additionalProperties` to `false`.
- The types are `string`, `number`, `integer`, `boolean`, `array`, `object`, and `null`, with `enum`.
- No `minimum`, `maximum`, `multipleOf`, `minLength`, or `maxLength`: Claude refuses a schema that has them. What validates the result checks ranges.
- No schema that refers to itself.

A provider that does not take a schema answers the call with an error, which is `Failed`.

## Checking against the real providers

The tests answer in a provider's place, with what its documentation says it sends. `LiveProviderTests` asks the providers themselves. It is skipped unless a key and a model are given for a provider, in the environment of the run, so the suite never reaches a provider by itself and no key is stored:

```
ECNANIB_LIVE_CLAUDE_KEY=... ECNANIB_LIVE_CLAUDE_MODEL=... \
  dotnet test --solution Ecnanib.slnx --filter-class "*LiveProviderTests"
```

The variables are `ECNANIB_LIVE_<PROVIDER>_KEY` and `ECNANIB_LIVE_<PROVIDER>_MODEL`, with `CLAUDE`, `GEMINI`, `GPT`, or `GROK`. Give them for one provider or for several. For each provider that has both, three calls are made, at the addresses of the tracked configuration file:

| Call | What it must end as |
|---|---|
| A small prompt with a schema, to the model that was given. | `Succeeded`, with a result of the schema and a usage. |
| The same with a key that is none. It costs nothing. | `AuthenticationFailed` |
| The same to a model that does not exist. | `ModelNotFound` |

The tests read the variables. The application reads none (NFR-CFG-2).

Run it when an adapter is added or changed, and when a provider changes its API. Three things rest on documentation alone until a run has shown them:

- The names of the usage counts of GPT, which are taken from Grok's documentation of the same format.
- What Grok answers a wrong key and a missing model with. Grok does not document the body of its errors.
- That Gemini takes a schema as `responseJsonSchema` with `generateContent`. Its documentation now leads with a newer way of calling a model.

## Not built yet

- What hands a stored key to a call, and has the key checked again when the call ends as `AuthenticationFailed` (FR-KEY-9, FR-KEY-3). It comes with what calls the gateway for an agent.
- Validating a result against its schema, and calling again after a timeout or an invalid result (FR-TEAM-6, FR-TEAM-9).
- Listing a provider's models, for the catalog (FR-CAT-1).
- Live search for the Sentiment Analyst (FR-CYC-3).
- Breakers for repeated failures of a provider or a key (NFR-REL-4).
- Recording the usage of each call, for the reports (FR-TEAM-8, FR-RPT-3).
