# Legal and compliance

Three things are asked for before the service opens to the public. Qualified counsel reviews whether operating an automated trading service needs a licence or a registration in the target jurisdictions, and any restriction by country that follows is applied (NFR-LEG-3). The use of each AI provider's API and of Binance's API keeps to their terms, their names are used only to identify them, with no suggestion of endorsement, and an official logo is used only where the terms of its brand allow it (NFR-LEG-4). And the name of the product is cleared for trademark conflicts (NFR-LEG-5). They treat two risks of the specification: regulation (R4), and the name, which is "Binance" reversed (R8).

**Most of this is the work of people, and most of it is not done.** No code reviews a licence or clears a name. This document is the record: what is done, what is open, and whose it is. Three parts of it are code: a list of countries that a registration is held to, a sentence at the foot of every page, and a rule that reads every text and every picture of the application. A fourth is a reading: the published terms of Binance and of the four AI providers were read against what the code does, on 28 November 2026, by engineering and not by counsel. **It found points that are open**, and some of them go to how the service is built: see "The terms of the APIs".

The codes of the countries are `Countries` in `src/Ecnanib/Shared`, the check is in `src/Ecnanib/Accounts/Registration.cs`, the form and `CountryNames` are in `src/Ecnanib/WebUi/Pages/Account`, and the sentence is `Screens.Independence` in `src/Ecnanib/WebUi/Layout`. The rules below are enforced by `CountryRestrictionTests` and `CountryRestrictionPageTests` in `tests/Ecnanib.Tests/Accounts`, by `LayoutPageTests`, and by `BrandRules` with `BrandRuleTests` in `tests/Ecnanib.Tests/Legal`.

The risk disclosure (NFR-LEG-1), and the Terms of Service and the Privacy Policy, which are drafts, are in "Legal documents" in [accounts.md](accounts.md). That the service never takes custody of funds and refuses keys that can withdraw (NFR-LEG-2) is in [credentials.md](credentials.md) and [exchange.md](exchange.md).

## Before public launch

This is the record. A row is changed in the commit that acts on its outcome.

| What | Whose it is | State on 28 November 2026 |
|---|---|---|
| Name the target jurisdictions: the countries that the service is offered to. | The owner | **Not done.** The specification names none. |
| Review whether operating the service needs a licence or a registration in each of them (NFR-LEG-3, R4). | Qualified counsel | **Not done.** |
| Apply the restrictions by country that the review asks for. | The owner, in the configuration file of the deployment | **None is set.** The list is there to be filled: see "Restrictions by country". |
| Settle what the reading of the terms found (NFR-LEG-4). | Counsel, and the owner for what changes the product | **Open.** Read by engineering, not by counsel. Findings T1 to T9 and B1 to B6 stand: see "The terms of the APIs". |
| Review the wording that was written from the specification: the sentence at the foot of every page, and the risk disclosure. | Counsel | **Not done.** |
| Write the Terms of Service and the Privacy Policy. | The owner, with counsel | **Not done.** Both are drafts at version `0.1`. What they must pass on from the terms of others is finding T7. |
| Clear the name of the product for trademark conflicts (NFR-LEG-5, R8). | The owner, with counsel | **Not done.** See "The product name". |

| Rule | Detail |
|---|---|
| **Registration stays closed until the record is complete.** | A deployment that the public can reach keeps `Application:RegistrationOpen` at `false` until no row above says "Not done" or "Open". No code holds a deployment to that: it is the owner's to keep. |
| **An outcome is written here, and not the advice.** | Who reviewed, when, for which jurisdictions, and what follows from it. The opinion of counsel itself stays out of the repository. |
| **What a review changes is changed where it lives.** | A country that is restricted is added to the file of the deployment. A word of a legal text that changes gives the text a new version: see "Legal documents" in [accounts.md](accounts.md). A risk of the specification that is treated is said so in `SRS.md`, where R4 and R8 stand as they were. |
| **The reading has a date, and is repeated.** | The parties change their terms, as the specification says of the providers (R5). Two of the documents that were read were less than three weeks old. The reading is repeated before the service opens, and when a party announces new terms. |

## What counsel is asked

What bears on a licence or a registration, as the service is built:

| Fact | Where it is held |
|---|---|
| The service holds no funds. They stay in the user's own account at Binance, and the service acts through a key that the user gives it. | [credentials.md](credentials.md) |
| A key that can withdraw is refused, and the service has no call that withdraws, deposits, or transfers. | `NoFundMovementTests`, [exchange.md](exchange.md) |
| It places orders for the user without anyone approving each one: Spot, and perpetual Futures with leverage up to a cap. What decides is the answer of AI models, inside limits that the models cannot move. | [cycles.md](cycles.md), [risk-guard.md](risk-guard.md) |
| It trades one aggressive profile. A new agent trades in Binance's test environment, and its user switches it to real funds after proving who they are and accepting the risks. | [agents.md](agents.md) |
| It is free: no fee, no subscription, no commission from the exchange, and no broker or referral id on any order. | [exchange.md](exchange.md) |
| It says that nothing in it is investment advice, and shows each user what their agents did and why. | "The risk disclosure" in [accounts.md](accounts.md) |
| It shows signed-in users the results of all users together by provider and by model, with no user in them. | "The platform-wide board" in [experience.md](experience.md) |
| It does not know where a user is. It asks which country a visitor lives in only while a country is restricted, and does not keep the answer. | "Restrictions by country" below |

The questions that the reading leaves for counsel are with each finding below. Four are asked first, because the answer to each could change what is built:

1. Whether unattended orders are a use that the usage policies of the providers allow (T4).
2. Whether a service may run on keys that its users hand over, under each party's terms (T3, B1).
3. Whether Binance's test environment may be what Demo trades in (B2).
4. Whether the name can be kept (B5, "The product name").

## Restrictions by country

Restrictions by country are applied at registration (NFR-LEG-3). **None is set.** Which countries are restricted follows the review by counsel, and the terms of the five parties: the service is not to be offered where Binance, or a provider that an agent needs, is not (T7, B4).

| Rule | Detail |
|---|---|
| **The configuration file names the countries that the service is not offered in.** | `Application:RestrictedCountries`: each by its code, the two capital letters of ISO 3166-1, and `XK` for Kosovo. The tracked file names none. See [configuration.md](configuration.md). |
| **A code that names no country stops the application from starting.** | `UK` is none: the United Kingdom is `GB`. Neither is a code in small letters. A restriction that restricted nobody would look like one, so the file is refused, and the report names the key. `SettingsFileTests`. |
| **While the file names a country, the form that registers asks which country the visitor lives in,** | and nobody who says one of those is registered. With an empty list the form does not ask. See "The country a visitor lives in" in [accounts.md](accounts.md). |
| **The answer is not kept.** | It is held against the list and then it is gone: no column, no log event, no email, and no audit event holds it. Personal data stays the email address, the password hash, and preferences (NFR-SEC-9). |
| **A change to the list takes effect at the next start.** | As every key of the file but the log levels. |

**What follows from asking once and keeping nothing** is decided, and is for counsel to weigh:

- **It is what the visitor says.** Nothing looks up where a request comes from, and a visitor who says another country is registered.
- **Nothing shows afterwards what anyone said.** The service cannot show that a user declared where they live.
- **A country that is restricted later reaches nobody who registered before.** The accounts that exist say nothing of a country. An operator can disable an account that they know of (FR-OPS-4), and nothing finds one.
- **It is one list for the whole service.** A country in which one market is not available, such as Futures, cannot be restricted for that market alone.

If counsel asks for more than this, it is built then: see "Not built yet".

## The terms of the APIs

**This is an engineering reading, and no legal opinion.** The terms that each party publishes were read on 28 November 2026 and held against what the code does. A finding says where the words of the terms and the service do not fit, as an engineer reads them. Whether a clause binds, whom it binds, and what follows is for counsel.

| Rule | Detail |
|---|---|
| **Each finding rests on words that were read that day,** | with the document, its address, and the date that it carries. What was read through a copy, and what could not be read, is said. |
| **The four that weigh most were read twice.** | The quotes under T1, T2, and T4 of Google's grounding terms, of OpenAI's web search guide, and of the usage policies of OpenAI and of Anthropic were fetched a second time, apart from the first reading, and stand word for word. The quotes of Binance under B1, B2, B4, and B5 were looked up again in the text of each document. |
| **A finding that needs a change to the product is not fixed in passing.** | It is open until the owner decides. Where a decision of the specification bears on it, the finding names it. |

### What was read

| Party | Documents, with the date that each carries |
|---|---|
| Anthropic (Claude) | Commercial Terms of Service, `anthropic.com/legal/commercial-terms`, effective 17 June 2025. Usage Policy, `anthropic.com/legal/aup`, effective 12 November 2026. Supported Regions Policy, `anthropic.com/supported-countries`, effective 12 November 2026. Trademark Guidelines, `anthropic.com/legal/trademark-guidelines`, effective 1 August 2024. Service Specific Terms, effective 31 August 2026. The page of the web search tool in the documentation, and the help article on keeping API keys safe, of 19 October 2026. |
| Google (Gemini) | Gemini API Additional Terms of Service, `ai.google.dev/gemini-api/terms`, which now leads to `aistudio.google.com/docs/terms`, effective 23 March 2026. Google APIs Terms of Service, `developers.google.com/terms`, last modified 9 November 2021. Generative AI Prohibited Use Policy, last modified 17 December 2024. The pages of grounding with Google Search, of the API keys, of the available regions, and of the prices in the documentation. Google's brand guidance and trademark guidelines. **Not read:** the brand guidelines of Gemini itself, which ask for a sign-in. |
| OpenAI (GPT) | Services Agreement, `openai.com/policies/services-agreement`, effective 1 January 2026. Service Terms, updated 13 November 2026. Usage Policies, `openai.com/policies/usage-policies`, effective 20 November 2026. Terms of Use, effective 1 January 2026. The guide of the web search tool. The help article on API key safety. Brand guidelines, `openai.com/brand`. The list of supported countries. |
| xAI (Grok) | Terms of Service for enterprise, `x.ai/legal/terms-of-service-enterprise`, last updated 27 October 2026. Acceptable Use Policy, effective 14 August 2026. Brand Guidelines, of 14 February 2025. The pages of the citations, of the search on X, and of security in the documentation at `docs.x.ai`. **Read through a copy:** the pages under `x.ai/legal` refused the request, so their texts are those that the Open Terms Archive recorded in October 2026. A change after 11 November 2026 is not excluded. The documents that were read name the company by another name than xAI; which name stands is to be read on the pages themselves. **Not found:** terms for what the search on X returns, and a list of the countries of the API. |
| Binance | Terms of Use, `binance.com/en/terms`, effective 27 October 2026. API Keys Product Terms, version 2.0, in force since 9 July 2026. Prohibited Use Policy, and List of Prohibited Countries, both of 5 January 2026. Demo Trading Terms, version 1.0, in force since 5 January 2026, and the terms of the testnets, of 24 February 2025. OMS Toolkit Product Terms, version 2.0, in force since 8 May 2026, which bind only a company that joins that programme. Exchange Rules and Exchange Procedures, which carry no date. The general information of the Spot and of the Futures API in the documentation at `developers.binance.com`, and the help articles on API keys. **How they were read:** the legal pages hand their text out as documents behind a check for browsers, so each was opened in a browser and its text taken from the document. **Not read:** Binance's style guide, which asks for a password. **Not found:** a policy on its trademarks for those who are not its partners. |

### What the reading found about the AI providers

| # | What does not fit | Whose words | What the service does | What it is waiting for |
|---|---|---|---|---|
| T1 | **What a live search found is shown without its sources.** | Google: "will only display the Grounded Results with the associated Search Suggestion(s) to the end user who submitted the prompt". OpenAI: "When your interface displays web results or information from them, you must show inline citations as prominent, clickable links", in documentation that its Service Terms have a customer keep to. Anthropic, in its documentation: "When displaying API outputs directly to end users, citations must be included to the original source". xAI: no such duty was found. | The Sentiment Analyst searches live with the tool of its provider (FR-CYC-3). Its answer is kept as text for 90 days and shown to the owner of the agent. The sources, the citations, and Google's search suggestions are not kept and not shown: see [ai-providers.md](ai-providers.md). | **A change to the product,** and the decision that is open in the specification as Q10: whether the Sentiment Analyst searches with its provider at all, or is given a news feed. To keep searching: the sources are stored with the answer and shown with it. |
| T2 | **What Gemini grounds with Google Search is used beyond what its terms allow.** | Google: "You will not, and will not allow your end user or any third party to, cache, frame, syndicate, resell, analyze, train on, or otherwise learn from Grounded Results or Search Suggestions". Storing the text is allowed for the history of the user who asked, and resubmitting it only to Google to refine the answer. Grounding is for "an application that is owned and operated by you", and the prompt is one "from the end user". | The grounded answer is handed to the Strategist and the Risk Manager, which may be models of another provider, and is kept for 90 days. The prompt is written by the service, and the key is its user's. | **A change to the product:** Gemini without live search, or with what it finds kept apart and shown as Google asks. It goes with T1 and Q10. The price list says that grounding needs a paid key. |
| T3 | **The keys are their users', handed to the service.** | OpenAI: a customer will not "buy, sell, or transfer API keys from, to, or with a third party", and its help article says "The sharing of API keys is against the Terms of Use." Google: "Developer credentials (such as passwords, keys, and client IDs) are intended to be used by you". xAI: its terms are for business customers, and allow access by others "solely on Customer's behalf". Anthropic: no clause forbids it; its help article advises against it and says what it gives away. Google, Anthropic, and xAI each say that the API is not for consumer use, or is for business users. | Each user makes a key in their own account with the provider and gives it to the service, which keeps it encrypted and calls the provider with it for that user's agents alone (FR-KEY-1, constraint C8). | **Counsel:** whether this is the sharing or the transfer that the terms speak of, who the customer is, and whether the operator needs an account, an approval, or terms of its own with each provider. It is a constraint of the brief, so the answer is the owner's to act on. |
| T4 | **No person reviews a trade before it is placed.** | OpenAI, since 20 November 2026: no "automation of high-stakes actions in sensitive areas without human review. This includes actions, decisions, transactions, submissions, and other steps that materially affect a person's rights, safety, access to essential services, or other consequential interests", with "financial activities and credit" among the areas. Anthropic, since 12 November 2026: "A qualified person must meaningfully review a High-risk AI Recommendation ... before it is used to implement a decision", where finance is "a personalized recommendation to buy, sell, hold, or allocate among specific investment products, such as securities, funds, digital assets, or annuities", and not "Carrying out a decision a person has already made, or applying a fixed rule, formula, or algorithm where the model exercises no judgment about the individual." Google: no automated decisions "that have a material detrimental impact on individual rights without human supervision in high-risk domains", finance among them. xAI: no "high-stakes automated decisions that affect a person's safety, legal or material rights, or well-being", with decisions on "financial credit" and "other important decisions about or for them" as its examples. | A cycle runs when a candle closes. The plan of the Strategist, once the Risk Manager and the Risk Guard let it through, is placed as orders at once (section 3.2 of the specification). The user set the agent up, switched it to Live, and can stop it. Nobody sees the single trade first. | **Counsel, before anything else:** whether a trade that an agent places for the user who configured it is what these policies mean. If it is, the product changes: a person approves a plan before it is placed, for some roles or for all. |
| T5 | **A user's key fills the catalog that every user sees.** | The same words as T3: a key is for its holder's use, and what is done with it is done under that account. | Where the configuration file holds no `CatalogKey` for a provider, the catalog of that provider is refreshed with the key of whichever user had theirs checked, and GPT lists the models of that key's project: see [catalog.md](catalog.md). | **The deployment:** a public deployment sets a `CatalogKey` for each of the four providers. Then no user's key syncs a catalog. Nothing refuses a file without them. |
| T6 | **How a provider is named.** | OpenAI's brand guidelines list as what not to do: "Use "GPT" generically without identifying the specific model", and ask to name the model. Google's trademark guidelines ask for a trademark as an adjective before what it names, and for a line that says whose it is. Anthropic's allow its trademarks "only as specifically permitted by us". xAI's allow them "only to accurately refer to us or our services", and ask that text made with Grok and published say "Written with Grok". | A provider is named "Claude", "Gemini", "GPT", or "Grok", as the specification names it (constraint C9), in sentences such as "A model of GPT". The model itself is named by what its provider calls it. No company is named, and the foot says that the names are trademarks of their owners. | **Counsel, then the owner:** whether "GPT" may stand for the models of its provider, how the four are to be written, and what the line at the foot says. A change is a change of texts and of the names that the catalog seeds. |
| T7 | **Who may use each API is not passed on to the users.** | Google: "You must be 18 years of age or older to use the APIs", only in an available region, and only with a paid key for users in the European Economic Area, Switzerland, or the United Kingdom. OpenAI and Anthropic: only in the countries that each lists; Anthropic also excludes those who are in an unsupported region while they use it. xAI: users of at least 18 years. | The form that registers asks for no age. A country is asked for only while one is restricted, and none is. The Terms of Service are a draft. | **The Terms of Service, and the list of the countries:** an age of 18, and the regions of the five parties, as counsel reads them. |
| T8 | **What the terms have a service tell its users.** | Anthropic: a customer "must notify its Users, that factual assertions in Outputs should not be relied upon without independently checking their accuracy". OpenAI and xAI: output is not passed off as written by a person. | Every answer of a role is shown as the answer of that role and of its model, so nothing is passed off. The risk disclosure says that no model is a proven trader. Nothing says in those words that what a model states as a fact may be wrong. | **The Terms of Service, or a text of the pages,** with the wording of counsel. |
| T9 | **The service does not say who it is when it calls Gemini.** | Google's documentation asks a platform that calls with the keys of others to "identify your client using the `x-goog-api-client` header", and its terms that a client not "misrepresent or mask" its identity. | No call names the service: no header of the kind, and no User-Agent. | **Counsel, then a small change:** whether the guide means a service like this one. The header would carry the name of the product, which is not cleared. |

### What fits, as it was read

| What the service does | What was read of it |
|---|---|
| It keeps the answers of the models for 90 days and uses them in later prompts of the same user. | The customer owns the output under the terms of OpenAI, Anthropic, and xAI, and no limit on keeping it was found there. Gemini's grounded answers are the exception: T2. |
| It asks OpenAI and xAI not to keep its calls: `store: false`. | Nothing in the terms asks for it. It is the service's own choice. |
| It trains no model on what the models answer, and resells no access: every call is made with the key of the user it is for, and the service has no key for trading. `NoSharedKeyTests`. | What the four forbid here is training on output and reselling access. |
| It compares the providers and the models by what their trades made. | OpenAI's Usage Policies name "model evaluation and benchmarking" as a permitted use. No clause on comparisons was found in the terms of the other three. The comparison names no winner and gives no advice: see [experience.md](experience.md). |
| It shows no logo, names no company, and says at its foot that it is independent. | OpenAI's brand guidelines ask for just that sentence: "a clear indication to users that your product is independently developed and not affiliated, endorsed, or sponsored by OpenAI". None of the four allows its logo without permission, and none is used: see "Names and logos". |

### What the reading found about Binance

| # | What does not fit | Whose words | What the service does | What it is waiting for |
|---|---|---|---|---|
| B1 | **The keys are their users', handed to the service.** | Terms of Use: "You must not: (a) share your Access IDs with any other person", and an API key is such an id. Access through the API is "subject to separate API terms and our approval". The API Keys Product Terms, which stand above the Terms of Use where the two differ, speak of a user who grants access "to an order management system provider or similar third-party trading technology provider", and treat whoever uses a key "as authorised by you to do so". Prohibited Use Policy: "conducting transactions on behalf of other persons or entities, is prohibited unless expressly authorized by Binance in writing". Binance has a programme for services that connect "through their end users' API keys", the OMS Toolkit. Nothing that was read says that a service must join it. | As under T3: a user makes a key in their own account at Binance and gives it to the service, which places that user's orders with it (FR-KEY-1). | **Counsel, then the owner:** whether the service may run as it is, or asks Binance for its consent, or joins its programme. The programme has terms on the name: B5. |
| B2 | **Binance's test environment is what Demo trades in.** | Demo Trading Terms: "only for testing and engineering purposes", and not "for commercial purposes" or "as a basis for any trading activity or the provision of advice or any professional services to any third party". The terms of the testnets say the same. | A new agent trades in Demo, which is Binance's test environment, with a key that its user made there (FR-AGT-7, dependency D2). The service is free. | **Counsel:** whether a free service whose users try their agents there is within those words. If it is not, Demo needs other ground: trades that the service simulates itself. |
| B3 | **Prices from Binance are shown to every user.** | No document that was read gives a right to show the market data of the API to others, and none sets a rule for keeping it or for naming its source. The licence of the Terms of Use is for "non-commercial personal or internal business use". | Public market data is read once from `api.binance.com` and `fapi.binance.com`, held in memory, shared between users, and shown on their pages. None of it is stored, and none leaves the application: see "Market data" in [exchange.md](exchange.md). The hosts of Binance Vision, whose terms forbid such a use, are not read. | **Counsel.** |
| B4 | **Whom Binance excludes, and where it may be called from.** | List of Prohibited Countries: the United States, also for its citizens wherever they live; Canada and the Netherlands; Cuba, North Korea, Iran, Crimea, and the two regions of Ukraine that call themselves people's republics. And persons under sanctions. API Keys Product Terms: "You shall not access or permit access to the Binance Platform from a country listed in our List of Prohibited Countries." | The service calls Binance from its own addresses, so Binance does not see where a user is: the service is what keeps such a user out, or lets them in. No country is restricted yet. | **The list of the deployment:** `US`, `CA`, `NL`, `CU`, `KP`, and `IR` at the least, as counsel confirms it. **The Terms of Service,** for what the code of a country cannot say: a region of Ukraine, a citizenship, and a person under sanctions. **The deployment:** servers in none of those countries. |
| B5 | **The name.** | No rule of Binance on its name for those who are not its partners was found. Where it writes rules for partners, they bar a name "using or incorporating any Binance Marks", and any use "that suggests an association between the Company and Binance". Its Prohibited Use Policy forbids actions that "falsely claim affiliations". Its trade marks are outside every licence of its Terms of Use. | The product is named with the letters of "Binance" in reverse. It serves customers of Binance, and its start page says "AI trading agents on Binance" above a foot that says it is independent. Binance offers trading with AI of its own. | **Counsel:** the name cannot be cleared from the terms. It is a question of trademark law, which they do not govern. See "The product name". |
| B6 | **What a user is to be told about a key.** | API Keys Product Terms name as measures of the user: a key for one purpose, an address allow-list on every key, "Not storing your API Keys in cloud-based venues", and sharing a key with nobody whom the user would not give the account to. A user must "immediately report to Binance any loss or suspected or actual misuse". Binance "may revoke or disable an API Key at any time". | The pages of the keys say that a key that can withdraw is refused, show the addresses that the service calls from, and warn of a key that works from any address (FR-KEY-4). They do not say that the keys are kept on servers of a hosted service in those words, and nothing tells a user that a key may have been misused. | **The Terms of Service and the Privacy Policy,** and a way to tell the users of a breach at once. |

| What the service does | What was read of it |
|---|---|
| It places orders by itself. | The terms speak of automated trading systems, and the rules of the exchange of behaviour "whether intentional, accidental, manual or automated". What they forbid is market abuse: spoofing, layering, wash trading. An agent holds one position, and places an entry with the orders that protect it: none is placed to be cancelled. Whether many agents of many users that trade one symbol at the same close could be read as acting together is for counsel, with B1. |
| It keeps to 70% of Binance's limits on requests, for all users together, and holds back when Binance says so. | The limits are counted by address: 6,000 of weight a minute on Spot and 2,400 on Futures, and an address that goes over is banned. A block of the address ends as a failure and is counted by the watch on outages. The limits on the orders of one account are not counted by the service: see "Not built yet" in [exchange.md](exchange.md). |
| It refuses a key that can withdraw, and never moves funds. | It is the least that a key may do, which the terms name as a measure. |
| It has fixed addresses, and has its users allow-list them. | The terms name an allow-list on every key as a measure, and Binance's help says that a key of the usual kind without one may only read, unless its user turns that off. |
| It sends no broker id, and takes no commission. | Nothing asks for one. |

## Names and logos

The names of the AI providers and of the exchange are used only to identify them, with no suggestion of endorsement, and an official logo is used only where the terms of its brand allow it (NFR-LEG-4). No logo is used anywhere.

| Rule | Detail |
|---|---|
| **A provider and the exchange are named to say which one is meant, and for nothing else.** | "A model of GPT", "your key at Binance", "Binance reports that this key can withdraw". The providers are "Claude", "Gemini", "GPT", and "Grok", as the specification names them, by the names that the catalog has for them. |
| **No text names a company behind a provider.** | Not Anthropic, Google, OpenAI, or xAI. A company that is named reads as one that has a part in the service. |
| **No text has anyone stand behind the service.** | None says partner, endorsed, sponsored, affiliated, official, certified, powered by, approved by, trusted by, or in association with. `BrandRules.Says` is the list. |
| **Every page says whose service this is.** | At its foot, as a sentence with no link: "Ecnanib is an independent service. It is not affiliated with, endorsed by, or sponsored by Binance or by the providers of Claude, Gemini, GPT, and Grok. Those names say which services it works with, and are trademarks of their owners." It is `Screens.Independence`, and `LayoutPageTests` holds its words and that every page ends its foot with it. |
| **No page shows a picture of anyone.** | No page writes `img`, `picture`, `object`, `embed`, `canvas`, `video`, `audio`, or `iframe`. Two components draw with `svg`, and what they draw is the application's own: the QR code of an authenticator key, and the equity of an agent as a line. |
| **The application serves no picture and no font.** | It serves stylesheets and scripts, and no stylesheet loads anything from an address. So the service has no logo of its own either, and no icon in the tab of a browser. |
| **A provider has no colour of its own.** | See "The illustration of the start page" in [motion.md](motion.md). |

The sentence at the foot was written from the specification and from what the brand guidelines ask. **It has not been reviewed by counsel.** How the providers are to be named is finding T6, and whether the name of the product itself suggests what the sentence denies is finding B5.

### What the rule holds

`BrandRules` reads what was compiled of the whole application, and asks for no page: every type, with the parts that the compiler made of it, and every constant. So it finds a word in a text class, in a notification, in an email, in a prompt, and in the migrations that seed the names of the providers, whether or not a test reaches what shows it.

| What | How it is found |
|---|---|
| A word of the rule | In every text that a type holds. A word counts where it begins, so "unofficial" and "certificate" are left alone, and "logo" and "xAI" count only as whole words: they are in "logout" and in names of the code. |
| A picture | The name of an element that a component opens, and a tag in markup that a page holds as it is, in any letter case. |
| A file that is served | The addresses that the build wrote down beside what it built: the files of `wwwroot`, the styles that are written beside a component, and the scripts of the framework. A compressed copy is of the kind of what it copies. |

| Rule | Detail |
|---|---|
| **A text is found until it is listed.** | `BrandRules.TextsThatAreListed` has three, each with why: the header `anthropic-version` and the name `googleSearch`, which the APIs of two providers ask for and no page shows, and the sentence at the foot, which uses the words to say that there is nothing of the kind. |
| **A component draws only when it is listed.** | `BrandRules.ComponentsThatDraw`, each with what it draws. What such a component writes beside its `svg` is found all the same. |
| **What is listed is there.** | A text or a component that is listed and that the application does not have fails `BrandRuleTests`: a hole that nothing uses is closed. |
| **The start page is read as it is sent, too.** | `TeamIllustrationPageTests` looks for the same words in the content of the page, which holds the names that the database has for the providers. |

**What the rule does not read:** what the database holds, what the configuration file holds, such as `Application:Name` and the addresses of the APIs, and a sentence that says the same in other words. It looks for words. A person reads the texts.

**Before a logo is used,** the terms of its brand are read, and what they allow is written into "The terms of the APIs". Then its component is listed in `BrandRules`, with what it shows, and the kind of its file in `BrandRules.ServedFiles`.

## The product name

**The name is not cleared.** "Ecnanib" is "Binance" reversed, which may raise trademark or affiliation concerns (R8). Clearing it is for the owner, with counsel: a search of the trademark registers of the target jurisdictions, and a view on whether a name that is made from the mark of the exchange, on a service that trades at that exchange, can be kept. What Binance's own terms say of such a name is finding B5.

| Rule | Detail |
|---|---|
| **The name is not sent to anyone.** | No call to Binance or to an AI provider names the service: there is no User-Agent, no broker id, and no header of the kind. See T9. |
| **A deployment changes what the emails call the service, and not what the pages do.** | `Application:Name` is the name in the emails, the name of their sender, and the name that an authenticator app shows. The pages write the name themselves. The two are not joined, on purpose: a rename is a change of texts, which a setting cannot make. |

### What a rename touches

Should the name not be cleared, this is what changes.

| What users read | Where |
|---|---|
| The name in the bar of every page, and on the start page | `Screens.SiteName` |
| The sentence at the foot of every page | `Screens.Independence`, which `BrandRules.TextsThatAreListed` lists and `LayoutPageTests` holds |
| Twelve texts of the pages | `HomePages` (1), `CatalogPages` (1), `KeyPages` (6), `RiskDisclosurePages` (4) |
| The risk disclosure, as a document | A word of it changes, so it takes a new version in `LegalDocuments` and a new fingerprint in `RiskDisclosureTests`: see "The risk disclosure" in [accounts.md](accounts.md) |
| The emails, their sender, and the authenticator app | `Application:Name`, in the file of each deployment and in the tracked file |
| What the application says when it cannot start | `Program.cs` |

The tests that hold these words say which they are when they fail: `ScreenRules`, `LayoutPageTests`, `RiskDisclosureTests`, `TrackedFileTests`, and the tests of the pages that the twelve texts are on.

**What no user reads keeps the name, and need not change with it:** the names of the cookies, `__Host-ecnanib-`, and of the claims of a session, the namespaces and the names of the projects, the name of the database context in `ConnectionStrings`, and the repository. Changing a cookie name signs every user out once.

## Testing

| Tests | What they hold |
|---|---|
| `CountryRestrictionTests` | That nobody is asked while the file names no country, that a visitor who lives where the service is offered registers as before, that one who lives in a country of the file is refused, with nothing stored and nothing sent, that a registration without a country is refused when one is asked for, the order of the problems, that a refusal spends no attempt, that the answer is in no row, no log event, and no email, and that every code has one name. |
| `CountryRestrictionPageTests` | The form without the field and with it, the list in its order, a registration on the form, what the form says of a restricted country and of none, what keeps its value and what takes the focus, and the order of the names. |
| `SettingsFileTests`, `TrackedFileTests` | That the list is read, that it may be empty, that the tracked file names none, and that a code that is none, a code twice, and a list that is no list are each reported by their key. |
| `AccessibilityRules` | The states of the form with the list, as it opens, with every field refused, and with a country refused, held to the rules of the markup as every state is: see [accessibility.md](accessibility.md). |
| `LayoutPageTests` | That every page ends its foot with the sentence, once, word for word. |
| `BrandRuleTests` | That no text of the application names a company or has anyone stand behind the service, that no page shows a picture but the two drawings, that nothing but stylesheets and scripts is served and no stylesheet loads anything, that what is listed is there, and, with the planted types of `Planted/PlantedBrands.cs`, that the rule finds each kind of text and of picture and leaves alone what only looks like one. |

Nothing tests the reading of the terms, the review of counsel, or the clearance of the name. They are in the record above.

## Not built yet

- **Everything of "Before public launch" that says "Not done" or "Open".**
- What the findings ask for, each once it is decided: the sources of a live search kept and shown, or no live search (T1, T2); a person who approves a plan before it is placed (T4); a file that is refused without a `CatalogKey` for each provider (T5); other names for the providers (T6); an age and the regions in the Terms of Service (T7); what a model states as a fact said to be unchecked (T8); a header that names the service to Gemini (T9); a Demo that the service simulates itself (B2); the countries that Binance excludes in the file of the deployment (B4); and telling the users of a breach (B6).
- A restriction by country beyond the form that registers: at the sign-in, when an agent is switched to Live, for one market alone, or by where a request comes from. The country is asked once and not kept. Keeping it would add to the personal data that the specification allows (NFR-SEC-9), and is a change to the specification first.
- A record of what a visitor declared, and a way to find the accounts of a country that is restricted later.
- The countries that are restricted named to a visitor before they answer, in the Terms of Service or beside the form.
- A check that holds a deployment to the record: nothing keeps registration closed while the legal work is open.
- A logo, of a provider, of the exchange, or of the service itself, and an icon for the tab of a browser.
- The name of the product from one place. The pages write it, and the emails take it from the configuration file: see "The product name".
