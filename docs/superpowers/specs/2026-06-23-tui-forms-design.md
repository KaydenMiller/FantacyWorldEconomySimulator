# TUI Create-Forms — Design

**Status:** Built autonomously (overnight, per user directive "keep iterating and building the forms until you have something that works; make decisions and we can correct in the morning"). Decisions below were made without user confirmation and are open to revision.

**Goal:** Let the DM create world entities (goods, settlements, shops, recipes, …) from inside the TUI, instead of only via the seed JSON / CLI. This is the long-deferred "data entry" capability (the TUI architecture always anticipated it as "just new IActions").

## Approach

Forms reuse the existing field-by-field prompt model. The TUI's proven input path is the **in-shell prompt bar** (`IUserInteraction`) — modal Terminal.Gui dialogs don't receive keystrokes in this TG v2 build. So a "form" is a guided sequence of single-field prompts, validated by the domain's `ErrorOr` factories, then persisted via `WorldDbContext`. This keeps the whole feature **UI-agnostic and unit-testable** with the existing `FakeUserInteraction`.

### Launch (decision)

A **global `n` (new)** key opens a chooser ("Create what?") listing the creatable entity types, then runs that entity's form. Global (not context-sensitive to the current view) because drilled sub-views have no clean resource tag, and a chooser is unambiguous and discoverable. After a successful create, the shell shows a confirmation and navigates to the entity's resource root (when it has one) so the result is visible.

### New interaction primitive (decision)

Add `Task<int?> AskChoiceAsync(title, prompt, options)` to `IUserInteraction` for enums and entity references. Shell implementation renders the options inline in the prompt bar (`1) Raw  2) Food …`) and accepts a 1-based number or a unique name prefix; re-prompts on invalid; `Esc` → null (cancel). Fake enqueues choices.

### Components

- `IUserInteraction.AskChoiceAsync` (+ shell + fake impls).
- `FormPrompts` (static helpers, UI-agnostic): `AskRequiredText`, `AskEnum<T>`, `AskMoney` (base units via number), `AskBool`, `AskRef` (pick an existing entity by name → id), `AskCount`/optional numbers. Each returns null on cancel.
- `IEntityForm { string Label; string? ResourceName; Task<FormOutcome> RunAsync(ctx, ui); }` and `FormOutcome(bool Created, string Message)`.
- One form class per entity under `src/WorldEcon.Tui/Forms/`.
- `FormRegistry` — the ordered list of forms the `n` chooser shows.

### Entities (create-only this pass)

Geography: **Continent, Country, Region, Settlement, Route.** Economy: **Good, Shop, Stockpile (stock a shop), Recipe (+ input/output lines), Production Node, Resource Endowment, Merchant, Consumer.**

Each form: prompt fields in dependency order; entity refs are chosen from existing rows (if a required ref has no candidates, the form aborts with a "create a … first" message). Validation failures from the domain factory are shown and the form aborts (no partial writes — a single `SaveChanges` at the end). All entities are world-scoped to the loaded world.

### Out of scope (deferred)

- **Edit** of existing entities (many domain aggregates expose no setters; create is the larger win). A follow-up can add edit for the entities that support mutation (e.g. `Settlement.SetState`, world pricing params).
- Multi-select / bulk create, field defaults remembered across invocations, in-place table editing.

## Testing

`FormPrompts` + representative forms (Good, Settlement, Recipe) unit-tested with `FakeUserInteraction` (enqueue answers, assert the entity persisted with the right fields, and that cancel writes nothing). Plus a live tmux smoke of `n` creating a Good and a Settlement.
