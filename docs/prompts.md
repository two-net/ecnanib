# Prompts

The prompt of a role call is composed from templates: the instructions of the role, and a module each for the market, the timeframe, and the trading profile of the agent (FR-PRM-1). The templates are managed by the system and delivered as seed data, through migrations (FR-PRM-7, NFR-DAT-10). A template is versioned: a version is never changed, an edit creates a new version, and each role output records the template versions it used (FR-PRM-6). A user reads the templates that their agents use, and cannot edit them (FR-PRM-7).

What is built is the templates themselves: their table, their seed, the rule of their versions, what reads the ones in use, and the page that shows a user the prompts of an agent. Nothing composes a prompt or calls a model yet, and no role output is stored: see "Not built yet".

The code is in `src/Ecnanib/DecisionEngine`, and the page in `src/Ecnanib/WebUi/Pages/Agents`. The rules below are enforced by tests in `tests/Ecnanib.Tests/DecisionEngine`, and by `SeedDataTests` in `tests/Ecnanib.Tests/Persistence`.

How rows are seeded is in [persistence.md](persistence.md). Whose the rows of a table are is in [ownership.md](ownership.md). The settings of an agent that its prompts go by are in [agents.md](agents.md).

## The templates

A template is a row of `prompt_templates`, and a row is one version of it.

| Column | What it holds |
|---|---|
| `id` | The id of the version, which the seed assigns. It is what a role output points at. |
| `kind` | What the template is for, as the name of a member of `PromptTemplateKind`: `Role`, `Market`, `Timeframe`, or `Profile`. |
| `subject` | Which one of its kind: a role by the name of its member of `Role`, a market by the name of its member of `Market`, a timeframe by its code, and the profile by its name. |
| `version` | Counts up from 1 for each kind and subject. |
| `text` | The template itself. |
| `is_active` | Whether this version is the one in use. |

| Kind | Subjects |
|---|---|
| `Role` | `MarketAnalyst`, `SentimentAnalyst`, `Strategist`, `RiskManager` |
| `Market` | `Spot`, `Futures` |
| `Timeframe` | `15m`, `1H`, `4H`, `1D` |
| `Profile` | `Aggressive` |

- **Each kind and subject has one version in use.** A unique index over the rows that are in use holds the database to at most one, and `SeedDataTests` holds the seed to one for every role, market, timeframe, and profile. Another unique index holds a kind and subject to one row for each version.
- **Version 1 of each has the outline that the specification gives for it:** the job and the structured result of the role (SRS 4.5), the modules of the timeframes and the markets (SRS 4.6), and the Aggressive profile (FR-PRM-5). The full wording follows as later versions.
- **The rows belong to the service.** The templates are the same for every user, so the table is listed in `OwnershipRules.cs`, and a row names no user.

## A version is never changed

**The database holds to it itself** (FR-PRM-6). The migration `AddPromptTemplateVersionGuard` gives `prompt_templates` a trigger, `prompt_templates_keep_versions`, and whatever asks for a change is refused: a later migration, the application, or a person at the database.

| What is asked of a row that is stored | Answer |
|---|---|
| A change to its `text`, `version`, `subject`, `kind`, or `id` | Refused. |
| A change to `is_active` alone | Done. It is how the next version takes the place of this one. |
| Deleting it, or emptying the table | Refused. An agent may have used the version. |
| Adding a row | Done. It is how a version arrives. |

- **A refusal says what to do instead:** `A version of a prompt template is never changed or deleted. To edit a template, add its next version (FR-PRM-6).` Its SQLSTATE is `23001`, `restrict_violation`.
- **A version is held to it from the day it is released, and not from the day it is first used.** A deployment that applied the migration may have used the version, and nothing that generates a migration can know. A version that is out of use is held to it as well: it is what was used.
- **A migration that edits a seeded text is refused where it is applied.** The tool generates one without complaint, and `ModelTests` passes with it: the model and its migrations agree. Every test that migrates a database fails with it, with the message above, `SeedDataTests` among them, and an application that carries it does not start: see "When it fails" in [persistence.md](persistence.md).
- **The trigger is schema that the model does not know.** It was written by hand, in a migration that the tool generated empty, and it is not in the snapshot. A database that was created without the migrations has no such trigger.
- **Only the seed creates a template.** `PromptRulesTests` finds every type of the application that constructs a `PromptTemplate`, and fails for one that is not the configuration of its table. A row that the application stored would be a template that no migration delivered.
- **Taking a template away, or renaming its subject, is refused as well.** Renaming a member of `Role` or of `Market` changes the subject of its template, which is a change to a version. The migration that does either drops the trigger first and creates it again after, by hand, and says why.

## Editing a template

An edit is a new version, and nothing else (FR-PRM-6).

1. In `PromptTemplateConfiguration.Versions`, in `DecisionEngine/PromptTemplate.cs`, add a line: the same kind and subject, the next version, the next id, and the new text. Change no line that is there.
2. Generate a migration, as [persistence.md](persistence.md) describes. **The latest version of a kind and subject is the one in use,** so the migration takes the earlier version out of use and inserts the new one.
3. Read the migration. It updates `is_active` of the earlier row to `false`, and then inserts the new row. The tool generates them in that order, which is the one that works: the unique index refuses two versions in use at once.
4. Run the tests. `PromptTemplateVersionTests` holds the seed to versions that count up from 1 with none left out, and to the latest being the one in use.

- **A version that was applied is not taken back.** The `Down` of such a migration deletes the new row, which the trigger refuses. To undo an edit, add the next version with the earlier text.
- **Keep each text on one line in the source file.** A text that spans lines would change with the line endings of a checkout, and the model with it.
- What reads the templates answers with the new version once the migration is applied, and the page of an agent shows it. The earlier version stays stored, for the role outputs that used it.

## Reading the templates

```csharp
public interface IPromptTemplates
{
    Task<TemplatesInUse> InUseAsync(Market market, Timeframe timeframe, CancellationToken cancellationToken);
}

public sealed record TemplatesInUse(
    IReadOnlyDictionary<Role, PromptTemplate> Roles,
    PromptTemplate Market,
    PromptTemplate Timeframe,
    PromptTemplate Profile)
{
    public IReadOnlyList<PromptTemplate> For(Role role) => [Roles[role], Market, Timeframe, Profile];
}
```

| Rule | Detail |
|---|---|
| **The templates of an agent follow from its settings.** | `InUseAsync` takes the market and the timeframe of the agent, as the types of `Shared`. So the module of a market or of a timeframe that the agent does not have cannot be asked for (FR-PRM-4). Every agent has the four roles and the one profile. |
| **It answers with the versions in use.** | Seven templates: one for each role, and the modules of the market, the timeframe, and the profile. A template says its id and its version. |
| **It only reads.** | `IPromptTemplates` has no operation that stores, changes, or deletes a template, and neither has anything else. |
| **It asks for no owner.** | The templates are the service's. Whose agent a question is about is settled by whoever asks, before it asks: the page finds the agent among those of the signed-in user. |
| **A template that is missing is an error of the seed.** | A kind and subject with no version in use throws, and names it. No user can cause it. |

`TemplatesInUse.For(role)` is the four templates that one call of a role uses, in the order a prompt is composed in. It is what composes a prompt takes, and what the output of the call records.

## The page

`/agents/<id>/prompts` shows a user the prompts that an agent of theirs uses now (FR-PRM-7). It is rendered on the server for each request, and is not interactive.

- **It shows seven prompts:** the instructions of each of the four roles, in the order they work in, and the modules of the agent's market, of its timeframe, and of the profile. Each has its name, its version, and its text.
- **Which they are follows from the agent,** as it is stored when the page is asked for. An agent whose market or timeframe is changed shows the modules of the new one.
- **An agent of another user is not found,** like one that does not exist. See [ownership.md](ownership.md).
- **The page has no form, and takes nothing.** A form that is posted at it all the same is the form of no page, and is answered with "bad request".
- **A text is written as text,** with the line breaks it has, and never as markup. Its look is in `app.css`.
- The page says in plain language what a prompt is, that the service supplies it, and that a version is never changed (NFR-USA-2). Its texts are in `AgentPages`.
- The page of the state of an agent and the page of its settings lead to it, and it has the badge of the agent's mode under its heading, as every page of an agent has (FR-UI-3).

Whether users may edit prompts, or add instructions of their own for an agent, is an open question of the specification (Q5). Until it is decided, they may not.

## What a role output records

**Each role output records the template versions it used** (FR-PRM-6, FR-TEAM-8). Role outputs are not built: the role pipeline stores them. What is built is what the pipeline needs for it, and a test that waits.

- A version is a row that is never changed and never deleted, so a role output that points at the row says for good what the model was told.
- `TemplatesInUse.For(role)` hands out the four templates of a call, each with its id.
- **`PromptRulesTests` fails as soon as a table of role outputs is in the model and its rows name no template.** A table is one of role outputs by its name: it has the word `role` in it, and the word `output` or `call`. It names a template when it has a foreign key to `prompt_templates`, or when a table that hangs on it has one.
- Declare the foreign key with `OnDelete(DeleteBehavior.Restrict)`, as every key to a seeded table is: see [persistence.md](persistence.md).

## Not built yet

- Composing a prompt from the templates, the agent's context, the data package, and recalled lessons (FR-PRM-1 to FR-PRM-5), and the full wording of the templates, which arrives as later versions.
- The instructions about untrusted content, and keeping personal data out of a prompt (FR-PRM-8, FR-PRM-9).
- The role outputs, and with them the record of the template versions that each used (FR-TEAM-8). See "What a role output records".
- Showing the earlier versions of a template. The page shows what an agent uses now. What a past call was told is shown with the decision record, which points at the versions it used (FR-CYC-10).
- Prompts that a user edits, or instructions that a user adds for an agent (Q5).
