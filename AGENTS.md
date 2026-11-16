# Working on Dito

## Scope and source of truth

- Read `SPEC.md` before implementing or changing behavior. It records the agreed
  first-version scope; do not silently broaden it.
- Implementation lives in `src\Dito`; focused tests live in `tests\Dito.Tests`.
- Favor the simplest complete implementation. Make small, coherent changes and
  verify each working increment.
- Keep one production WinForms project. A separate test project is appropriate;
  separate production assemblies are not currently needed.

## Abstraction and responsibility

**Keep statements within a method at the same abstraction level. Separate
abstraction layers, not just files.**

- An event handler translates the event and delegates an action; it does not also
  run SQL, construct native input structures, or implement a search algorithm.
- An application operation coordinates named capture/search/recall operations; it
  does not know SQL syntax, HWND message constants, or control layout.
- Storage methods own SQL, transactions, connection lifetimes, and row mapping.
- Windows integration methods own native handles, clipboard access, hotkey
  registration, focus checks, and input injection.
- UI methods own controls, rendering, selection, and user-facing feedback.
- Keep pure transformations, such as FTS query construction, separate from I/O.
- Extract a meaningful lower-level operation when levels mix. Do not extract every
  expression or add forwarding classes simply to make methods shorter.
- A class should have a clear responsibility; do not accumulate unrelated helpers
  in a generic `Utils`, `Manager`, or base class.

Prefer direct composition of concrete classes. Introduce an interface only for a
real substitution or a useful test boundary. Do not add a generic repository,
mediator, DI container, event bus, plugin system, or speculative image abstraction.

## C# conventions

- Target .NET 10 LTS and WinForms; enable nullable reference types and implicit usings.
- Use standard C# naming: PascalCase types/methods/properties, camelCase parameters
  and locals, `_camelCase` private instance fields. Name asynchronous methods `Async`.
- Use four-space indentation, braces on separate lines, and file-scoped namespaces.
- Prefer explicit, readable control flow, early guards, and small cohesive methods.
  Use `var` when the type is evident; avoid clever or compressed expressions.
- Use small records for data when useful, not elaborate domain models for plain rows.
- No `async void` except framework event handlers. Handle asynchronous failures at
  an appropriate operation/UI boundary so event-handler exceptions cannot terminate
  the app unexpectedly.
- Dispose owned resources explicitly. Give background work a bounded lifetime and
  support cancellation/supersession where it matters.
- Comment only a non-obvious constraint or reason. Do not narrate straightforward
  code or use comments in place of clear names.

## Correctness boundaries

- Preserve original text exactly. Truncation and newline flattening are display-only.
- Keep clipboard operations on an STA/message-loop thread; marshal UI updates onto
  the WinForms thread. Never block that thread with database work or sleeps.
- Do not assume a method is nonblocking just because its name ends in `Async`.
- Own database connections explicitly; do not share one across concurrent operations.
  Keep SQL parameterized and FTS syntax safely constructed as well as parameterized.
- Keep entry insertion and FTS updates atomic. Maintain schema versions without
  destructive resets. Exact deduplication must not become case-insensitive search
  equality.
- Discard stale search/preview results. Do not create a task or thread per stored
  entry, or queue unlimited work while the user types.
- Create the hidden listener's HWND and register it explicitly. Do not depend on
  `Load` firing for a form that is never shown.
- Identify self-generated clipboard changes; never use an unconditional
  "ignore next update" flag.
- Verify the paste target immediately before injecting input. Failure must leave
  text ready for manual paste, not send keys to an unrelated application.
- Keep error messages actionable but never log clipboard text, query contents,
  or selected-entry contents. Do not add network activity.

## Dependencies and future features

- Use `Microsoft.Data.Sqlite` with FTS5 support and pin a compatible stable version.
  Prefer framework APIs for everything else unless a dependency solves a concrete
  requirement.
- Do not build generic infrastructure for possible future features. Images,
  encryption, startup registration, and deletion are outside the current scope.
- No capture-count cap, expiry, automatic pruning, or Clear History command.
- Do not claim that locally written software or unencrypted local storage is
  automatically permitted by an employer.

## Validation and handoff

- Add focused tests for introduced/fixed behavior, preferably against temporary
  real SQLite databases. Test outcomes, not private implementation details.
- Test text preservation, deduplication/recency, persistence, FTS escaping and word
  matching, and pagination. Avoid tests that only assert constants or trivial getters.
- Do not use the live clipboard or send keystrokes in routine automated tests.
  Verify Windows-specific interaction deliberately and report manual checks honestly.
- Once project files exist, run the relevant build/tests and the repository's
  formatter checks. Use `dotnet format --verify-no-changes` if no other formatter
  is configured. Do not install extra tooling merely to validate Markdown.
- Measure the specified 100,000-entry workflow before claiming the performance
  target is met. Do not optimize speculatively ahead of measurements.
- Update the specification for approved behavioral changes. State what was changed,
  what was verified, and any unverified Windows interaction or remaining limitation.
