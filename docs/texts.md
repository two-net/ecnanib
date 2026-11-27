# Texts

The UI is in English, and text is kept separate from code so that other languages can be added (NFR-USA-4). A page holds no words of its own: it asks a text class for each. Risk settings, agent states, and Risk Guard rejections are explained in plain language where they appear (NFR-USA-2), and those explanations are texts of the same classes.

A text class is a static class beside the pages that it serves, such as `SignInPages` in `src/Ecnanib/WebUi/Pages/Account`. What holds the pages to the rule is `TextRules`, with `TextRuleTests` and the planted components of `Planted/PlantedTexts.cs`, in `tests/Ecnanib.Tests/Texts`. The scripts hold no text either: `SessionScriptTests`, `MotionScriptTests`, and `TimeScriptTests` hold each to that.

The frame that every page is written in is in [layout.md](layout.md), how a time is written is in [times.md](times.md), and what a screen reader is told is in [accessibility.md](accessibility.md). What the Risk Guard checks is in [risk-guard.md](risk-guard.md), and the states of an agent are in [agents.md](agents.md).

## Where a text is

| Text class | In `src/Ecnanib/WebUi` | The texts of |
|---|---|---|
| `Screens` | `Layout` | The bar at the top, the navigations, the names of the screens, what a page says of an unhandled error, and the language of the pages. |
| `SessionPages` | `Layout` | The notice before a session ends. |
| `ConnectionPages` | `Layout` | The dialog that says a page is rejoining the server. |
| `MaintenanceTexts` | `Layout` | The banner that announces maintenance on every page. |
| `LivePages` | `Live` | The parts of a page that follow what changes: the steps of a cycle, the position, the team at work, and what pauses a page. |
| `HomePages` | `Pages` | The start page, the page that says there is no page at an address, and the page of an error. |
| `LegalPages` | `Pages/Legal` | The legal documents: their names, their versions, and what stands in the place of a text that is not written yet. |
| `AccountPages` | `Pages/Account` | The page of the account, the page that deletes it, and what the pages of the account share. |
| `SignInPages`, `RegistrationPages`, `PasswordPages`, `TwoFactorPages` | `Pages/Account` | Signing in and confirming who one is, registering and verifying, a forgotten password, and two-factor authentication. |
| `KeyPages` | `Pages/Account` | The keys of the AI providers and of Binance. |
| `AppearancePages` | `Pages/Account` | The appearance: the theme, how much moves, and the time zone. |
| `NotificationPages` | `Pages/Notifications` | The notifications, and email for critical events. |
| `DashboardPages`, `SetupPages` | `Pages/Dashboard` | The dashboard, and the setup checklist. |
| `AgentPages` | `Pages/Agents` | The list of the agents, the form of an agent, its prompts, and what the pages of an agent share: what a state, a control, a market, and a mode are called and mean. |
| `AgentStatePages`, `AgentModePages` | `Pages/Agents` | The state of an agent and what changes it, and the two pages that switch its mode. |
| `CyclePages`, `RiskCheckPages`, `AgentReportPages` | `Pages/Agents` | The cycles and their timelines, the checks of the Risk Guard, and the trades, decisions, and performance of an agent. |
| `ExperiencePages`, `BoardPages`, `LessonPages` | `Pages/Experience` | The experience screens, the platform-wide board, and the lessons. |
| `CatalogPages`, `UsagePages` | `Pages/Catalog`, `Pages/Usage` | The model catalog, and the AI usage. |
| `OperatorPages`, `OperatorStatusPages`, `OperatorAccountPages`, `OperatorSyncPages`, `OperatorMaintenancePages` | `Pages/Operator` | What frames the operator status screen, with the page of the global halt, what the screen shows, the pages on which an operator disables and enables an account, the page on which they have the catalog synced, and the page on which they announce maintenance. |
| `ExportFiles` | `Exports` | The links to the two downloads. The names of their columns are words of the files, and no texts: see [export.md](export.md). |

What a market, a mode, and a state of an agent are called is one text for every screen: `AgentPages.NameOf` and `AgentPages.Describe`, wherever a page writes one. What the pages of one screen say is in the class of that screen, also where another screen says the same words: a heading and a link that read alike in English need not read alike in another language.

## What a text is

| Rule | Detail |
|---|---|
| **A text is a property, and never a constant.** | `public static string Title => "Sign in";` The compiler copies a constant into whatever uses it. A page that wrote a constant of a text class would hold the words as if they were typed into it, and nothing could tell the two apart. A property is asked when a page is written, which is also what another language needs: see "Adding a language". |
| **A sentence with a value in it is a method.** | `TooManyAttempts(int minutes)` returns the whole sentence, so the words stand in the order of the sentence, and a number is written with `CultureInfo.InvariantCulture`. |
| **A name that code uses stays a constant.** | The name of a form or of a field, an id, the value of a button, a key of an address, a path: `AppearancePages.Form`, `Screens.SignOutFormId`. It is handed over where no text is, and an attribute of the code needs a constant. |
| **A member of an enum is never written by its name.** | `@agent.Market` would write a word of the code. A page asks what the member is called: `AgentPages.NameOf(market)`, `AgentPages.Describe(state)`, `CyclePages.Describe(outcome)`. |
| **Every text says what it is for.** | In its summary, in plain words: "The button that saves the theme." |
| **Signs and digits are no words.** | A comma, a full stop, `%`, `×`, and a number may stand in the markup of a page, between two texts. |

### Writing a sentence

- **A sentence with markup inside it is one text for each stretch of words,** with the blank and the full stop left in the markup, as the start page writes it:

  ```razor
  <p>@HomePages.Risk <a href="@LegalPages.RiskDisclosurePath">@HomePages.RiskLink</a>.</p>
  ```

- **A page writes no blank where an element begins or ends.** In a paragraph, a cell, or a button that shows nothing. Where the words would run into their neighbours without it, the blank is of the text, and its summary says so: `ConnectionPages.TriesAgainIn` is `" in "`.
- **What a page writes from a text is encoded,** as everything that it writes from a value: an apostrophe in a text is `&#x27;` in the markup, which a browser shows as an apostrophe. A test that reads a page reads it decoded.

## What the rule holds

`TextRules` reads what was compiled of every component, which is a page or a part of one, and finds each literal that is handed over as text. No source file is read, and no page is asked for: a word that a component holds is found whether or not a test reaches the state that shows it.

| Where | What is a text there |
|---|---|
| Markup that a page holds as it is | A letter outside a tag, and in the value of an attribute that is not listed in `AttributesThatAreNoText`. |
| What a page writes as content | A letter in a literal, on either side of a value. And a member of an enum that is written as it is. |
| The value of an attribute | A letter in a literal, unless the attribute is listed. And a member of an enum, likewise. |
| A parameter of a part | A letter in a literal, unless the parameter is listed in `ParametersThatAreNoText`, as `Component.Parameter`. A member of an enum is handed over as it is: the part decides what to write. |
| The code of a page | A literal that reads as words: one with a letter and white space, one capitalised word, or capitals and digits. What an exception or a log is told is no text of a page. |

| Rule | Detail |
|---|---|
| **An attribute is text until it is listed.** | `class`, `id`, `href`, `role`, `type` and the like are listed: a value of one names something of the page, or tells the browser how to treat it. `aria-label`, `alt`, `placeholder`, and `lang` are never listed, and `TextRuleTests` holds the list to that. |
| **What is listed is there.** | An attribute or a parameter that is listed and that no component writes fails `TextRuleTests`: a hole that nothing uses is closed. |
| **A constant of a text class is found.** | At the page that writes it, as the words that the compiler copied there. |
| **The rule is shown to find what it looks for.** | With components that hold a text each, in another place each, and with one that holds none though it has everything that looks like one: `Planted/PlantedTexts.cs`. |
| **A script holds no text.** | A literal of a script has no blank in it: `SessionScriptTests`, `MotionScriptTests`, `TimeScriptTests`. What a script shows, the page wrote or the server said. |
| **The stylesheet writes no text.** | Every `content` of `app.css` is empty. |

Two things that the rule cannot see are kept to by hand:

- **`value` is listed,** because the value of a field, of a choice, or of a button is the word that a form is read by. A button that said its text in `value` would not be found: the text of a button stands between its tags.
- **An enum inside a text class.** A sentence of a text class that put a member of an enum into itself as it is would write its name. `AgentPages.NameOf` is used there too.

### Adding a text

1. Add a property or a method to the text class of the screen, with a summary that says what the text is for. For a new screen, add a class beside its pages, whose summary opens "The texts of …, in one place (NFR-USA-4)".
2. Ask for it in the page: `@SignInPages.Title`.
3. Run `TextRuleTests`. A word that is left in the page is named, with where it is.
4. Where the rule names something that is no text, the value of an attribute or of a parameter that names something for the code, list the attribute or the parameter in `TextRules`, with what it holds.

## Plain language

Risk settings, agent states, and Risk Guard rejections are explained where they appear (NFR-USA-2). Each explanation is a text of a text class, so a screen that shows the thing shows the same words for it.

| What | Where it appears | Its explanation |
|---|---|---|
| A risk setting | The form of an agent, under each field. | `AgentPages.Explain(AgentField)`: what the setting does, and what follows when it is reached, then its bounds and its default from the configuration file (`AgentPages.Within`). The two limits that are fixed, and the stale limit of each timeframe, are said under the settings. See "The risk settings" in [agents.md](agents.md). |
| A state of an agent | The list of the agents, the dashboard, the page of the state with the history of its changes, the form of the settings, and the pages that switch the mode, run a test cycle, and close a position. | `AgentPages.Explain(AgentState)`: beside the state, or in a list of the states under a table that shows several (`AgentStateMeanings.razor`). Why a state was reached is `AgentPages.Says(AgentStateCause)`. See "The states" in [agents.md](agents.md). |
| A rejection of the Risk Guard | The page of the risk checks of an agent, the timeline of a cycle, and the decisions on the screen of an agent. | `RiskCheckPages.Says`: a sentence for every rule and every outcome, with the numbers that the check stored. A rule that failed says what was measured, what it was held to and whose limit that is, and what follows or what its user can do. See "The page" in [risk-guard.md](risk-guard.md). |

## Adding a language

The pages are in English, and no other language is built. What there is makes one possible without changing a page:

| What | How it stands |
|---|---|
| **Every text is asked for when a page is written.** | Through a property or a method of a text class. So a text class is the one place where a text of another language is looked up, by the language of the request, and no page changes for it. |
| **The language of a page is said in one place.** | `Screens.Language`, which `App.razor` writes as `lang` on the first element. |
| **No text is in a script or in the stylesheet.** | So neither changes. |
| **What a member of an enum is called is a text.** | `NameOf` and `Describe`, and not the name in the code. |

What a second language would still have to settle:

- **The order of words around a link.** A sentence that is split into stretches around a link or an emphasis has the order of English: the stretch before, the link, the stretch after. A language that orders them otherwise needs that sentence as a whole, with a place for the link.
- **Plurals.** A count is said by the rules of English, in the text classes: "1 of the 4 steps is done", "3 of the 4 steps are done".
- **Numbers, amounts, and times** are written one way for everyone: `PageTimes`, and the numbers of `AgentPages.Number` and `DashboardPages`, use no culture.
- **The names of the screens** are read once, when the application starts: `Screens` keeps them in fields.

## Testing

| Test class | What it holds |
|---|---|
| `TextRuleTests` | That no component holds a text of its own. That what is listed as no text is there, and that what a person is read is never listed. That a text is found between the tags of markup, in an attribute of markup, as content on either side of a value, in an attribute that is joined with a value, in what is put into a part, in a parameter of a part, as a member of an enum that is written by its name, in the code of a page, and as a constant of a text class. That a property of a text class, a name of the code, signs and digits, and what an exception and a log are told are not found. Which literals of code read as words. |
| `SessionScriptTests`, `MotionScriptTests`, `TimeScriptTests` | That no script holds a sentence. |
| `AgentPageTests`, `AgentStateTests`, `RiskCheckPageTests`, `DashboardPageTests` | That every risk setting, every state, and every rule of the Risk Guard is explained where it appears (NFR-USA-2): see the test classes of [agents.md](agents.md), [risk-guard.md](risk-guard.md), and [dashboard.md](dashboard.md). |
| The page tests of every screen | What a page says, word for word: a text that is moved or changed shows there. |

No test reads a page in another language, because there is none.

## Not built yet

- A second language, and what chooses the language of a request.
- **What a notification says** is written when it is raised, by the component that raises it, and stored as it was written: the words of a notification are English for good. To say it in the language of its reader, a notification would store what happened and its numbers in place of a sentence. See [notifications.md](notifications.md).
- **The emails,** which Notifications writes: it may not use the Web UI, and has its texts in `AccountMailer`, `NotificationEmails`, and `NotificationTypes`.
- **The legal documents,** whose texts are not written yet: a document in another language is another document.
- **What a model is told and what it answers:** the prompts are in English, and so is a rationale that a page shows as the model wrote it. See [prompts.md](prompts.md).
- What an operator reads on the console, and what the API answers an error with.
