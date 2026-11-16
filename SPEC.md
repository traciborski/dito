# Dito specification

Status: first version implemented. Automated validation and remaining manual Windows
checks are documented in `README.md`.

## Goal and scope

Build a small Windows 11 clipboard history application with persistent text history,
a searchable popup, and paste into the previously active application.

Use one C# WinForms application targeting .NET 10 LTS (`net10.0-windows`) and a local
SQLite database through `Microsoft.Data.Sqlite`. No server, network calls, telemetry,
WPF, fzf, separate daemon, or PowerShell runtime scripts.

Keep all unique entries. **100,000 entries is a performance target, not a retention
limit.** Do not automatically delete history.

Not in the first version: images, rich-text preservation, encryption, automatic
startup, synchronization, pinning, history deletion, or a Clear History command.

## User experience

- Start the application manually. It runs in the current Windows user session with
  a tray icon, without a permanent window or console.
- **Ctrl + the backtick/tilde key, without Shift**, opens the popup and focuses the
  search input. This means the US-layout OEM key, not typing the `~` character.
  Centralize the binding so a different keyboard layout can be accommodated later.
- Capture the foreground target before showing the popup. If the popup is already
  open, activate it without replacing the original paste target with our own window.
- A new popup starts with an empty query and the most recently copied entries.
- Show a search input, a compact result list, and a read-only full-text preview of
  the selected entry. Shorten/flatten list previews only; never modify stored text.
- Typing filters results. Up/Down changes selection while search retains focus.
  Select the first result after a query change.
- Enter copies the selection to the clipboard and attempts automatic paste.
  Ctrl+Enter copies it without sending paste keystrokes.
- Escape, the close button, or losing focus hides the popup; none exits the app.
- The tray menu contains Show History, Pause/Resume Capture, and Exit. Pausing stops
  new capture, not searching or pasting existing entries.
- Show clear empty-history and no-match states. With no selection, Enter does nothing.

## Capture and retention

Capture **clipboard text changes**, not typing or paste operations. A paste normally
does not change the clipboard.

- Use `AddClipboardFormatListener` with an explicitly created, hidden message-only
  window. Keep a WinForms message loop running while the popup is hidden.
- Capture Unicode text while enabled. Ignore empty strings and non-text content.
  Preserve whitespace-only text, case, line endings, and leading/trailing whitespace.
- Respect the Windows `ExcludeClipboardContentFromMonitorProcessing` format when
  present. This is not a general secret detector or a guarantee of DLP compliance.
- Deduplicate by exact, case-sensitive text equality, without trimming or Unicode
  normalization. Recopying updates last-copied time and moves the existing entry to
  the top; its ID and first-copied time remain unchanged.
- Selecting an entry for reuse must not count as another copy or change its ordering.
  Identify our own clipboard writes; do not blindly ignore the next clipboard event.
- Retry briefly when another process has the clipboard locked. Abandon superseded
  retries so an old notification cannot capture a newer value under the wrong event.
  Recheck pause/shutdown state before processing a retry.
- Do not silently truncate large entries. Keep database work off the UI thread.

## Storage

Store the database at `%LOCALAPPDATA%\Dito\history.db`. Persist immediately as entries
are captured; do not wait until application exit.

The database, full-text index, and SQLite sidecar files are **unencrypted**. They can
contain passwords and company data indefinitely. Pause does not remove existing data.
Use only where persistent clipboard retention is permitted.

Enable WAL mode and a bounded busy timeout. Use parameterized commands and atomic
writes. Use a simple schema version (`PRAGMA user_version`) for future migrations;
never recover from an error by silently deleting/recreating the database.

Illustrative initial schema:

```sql
CREATE TABLE clips (
    id INTEGER PRIMARY KEY,
    text TEXT NOT NULL COLLATE BINARY UNIQUE,
    first_copied_utc_ms INTEGER NOT NULL,
    last_copied_utc_ms INTEGER NOT NULL,
    copy_order INTEGER NOT NULL
);

CREATE UNIQUE INDEX clips_recent ON clips(copy_order);

CREATE VIRTUAL TABLE clips_fts USING fts5(
    text,
    content = 'clips',
    content_rowid = 'id',
    tokenize = 'unicode61',
    prefix = '2 3 4'
);

CREATE TRIGGER clips_after_insert AFTER INSERT ON clips BEGIN
    INSERT INTO clips_fts(rowid, text) VALUES (new.id, new.text);
END;
```

Text is immutable in this version. Recopies update metadata only, so they do not
rebuild the FTS entry. Any later text-edit/delete feature must maintain the index
transactionally; it is not part of this version.

`copy_order` is a monotonically increasing recency value. It avoids timestamp ties
and clock adjustments affecting the requirement that a repeated copy moves to the
top. Its unique index also supports recent-first reads and cursor pagination.
Timestamps are UTC Unix milliseconds for display and metadata.

Illustrative atomic insert/recopy operation:

```sql
INSERT INTO clips (
    text, first_copied_utc_ms, last_copied_utc_ms, copy_order
)
VALUES (
    $text, $now, $now,
    (SELECT COALESCE(MAX(copy_order), 0) + 1 FROM clips)
)
ON CONFLICT(text) DO UPDATE SET
    last_copied_utc_ms = excluded.last_copied_utc_ms,
    copy_order = excluded.copy_order
RETURNING id;
```

Do not introduce a hash-based deduplication scheme unless measurements justify it.
Exact text uniqueness is the simpler starting point.

## Search and result loading

Use SQLite FTS5 word search, not arbitrary substring matching.

- Matching is case-insensitive, using the `unicode61` tokenizer's word and case-folding
  rules. Its default diacritic folding applies.
- All query terms must match. Completed terms match whole words; the last term also
  matches word prefixes. For example, `server con` matches a clip containing the
  word `server` and a word beginning with `con`, in any order.
- Treat user input literally, not as user-authored FTS operators. Quote and escape
  terms, then bind the resulting MATCH expression as a SQL parameter.
- Punctuation follows FTS tokenization, not source-code substring semantics. For
  example, searching `net` need not match `internet`.
- Empty/whitespace input lists recent entries without issuing an empty MATCH query.
- Debounce typing by approximately 150 ms. Supersede obsolete requests and discard
  late results; an older search must never replace a newer search's results.
- Fetch at most 100 result rows per page, ordered by `copy_order DESC`, not relevance.
  Load older pages on demand using a recency cursor rather than increasing OFFSET.
- Fetch only IDs, metadata, and short previews for list rows. Load full text for the
  selected preview/paste, checking selection identity before displaying late results.
  Do not load all 100,000 full texts or build a second in-memory search index.

Illustrative FTS query builder; keep this pure and test it separately from SQL:

```csharp
internal static string BuildMatchQuery(string input)
{
    var terms = input.Split(
        (char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    return string.Join(" AND ", terms.Select((term, index) =>
    {
        var literal = "\"" + term.Replace("\"", "\"\"") + "\"";
        return index == terms.Length - 1 ? literal + "*" : literal;
    }));
}
```

For `server con`, the bound expression is `"server" AND "con"*`. The caller handles
an empty expression through the recent-items path. Verify punctuation and quote
edge cases against the actual SQLite provider, not only generated strings.

Illustrative first-page search:

```sql
SELECT clips.id,
       clips.last_copied_utc_ms,
       clips.copy_order,
       substr(clips.text, 1, 240) AS preview
FROM clips INDEXED BY clips_recent
WHERE clips.id IN (
    SELECT rowid FROM clips_fts WHERE clips_fts MATCH $match
)
ORDER BY clips.copy_order DESC
LIMIT $pageSize;
```

Following pages add `clips.copy_order < $beforeCopyOrder`. Restart pagination when
the query changes or the popup is reopened. Do not let background clipboard updates
reset the current selection while the user is browsing.

## Paste and Windows integration

1. Load the selected entry's original text and write it to the clipboard on an STA
   thread, with bounded handling of a busy clipboard. On failure, leave the popup
   open and report failure; never paste whatever happened to be on the clipboard.
2. Record the successful self-write so capture does not reorder the selected entry.
3. Hide the popup. For Enter, try to restore the saved target window.
4. Verify the target is valid and actually foreground, and that trigger modifiers
   have been released, before sending Ctrl+V through `SendInput`.
5. If focus restoration, modifier release, or input injection fails, do not type into
   another window. Leave the selected text on the clipboard for manual Ctrl+V.

If another application changes the clipboard while recall is waiting, cancel paste
and report it. Do not overwrite that newer clipboard content.

Do not use a fixed sleep as proof that focus was restored, low-level keyboard hooks,
forced modifier-key releases, or elevation as a workaround for Windows restrictions.
Automatic paste is best effort, especially into elevated applications.

Handle hotkey registration failure visibly without crashing. Run one instance per
Windows user session; a second launch should exit with a clear notice. Unregister
the listener/hotkey and dispose tray, window, and database resources on normal exit.

## Code organization

Use one production project, with responsibilities separated inside it:

```text
src\Dito\
    Program.cs          Composition root and STA startup
    UI\                 Search form, previews, tray, UI event handling
    Application\        Capture/search/recall coordination and small result records
    Storage\            SQLite schema, commands, FTS query construction
    Windows\            Clipboard listener/access, hotkey, foreground/input APIs
tests\Dito.Tests\        Focused behavioral tests
```

This is a responsibility map, not a requirement to create empty folders or one
class per operation. `Program` wires concrete collaborators. UI handlers delegate
use cases; application methods coordinate named operations; storage owns SQL;
Windows integration owns native calls and threading constraints.

Keep every method at one abstraction level. Do not combine clipboard event
orchestration with SQL construction, P/Invoke structure setup, or control layout.
Use named lower-level operations for those details, without adding pass-through
layers that have no responsibility.

Database calls must execute off the WinForms thread. `Microsoft.Data.Sqlite` async
method names alone do not guarantee this. Keep connection ownership explicit,
never use a connection concurrently, and avoid unbounded background-work queues.

For later image support, retain stable entry IDs and keep text storage behind its
current boundary. Do not implement image tables, blob storage, or a format-plugin
system in anticipation of that future work.

## Implementation and acceptance

Implement in three working increments:

1. Persistent capture: STA message loop, hidden listener, SQLite, deduplication,
   pause/resume, tray exit, and single-instance handling.
2. Search: FTS indexing, query construction, paged reads, popup, and full preview.
3. Recall: global hotkey, copy-only action, guarded automatic paste, and cleanup.

Use focused automated tests with temporary SQLite databases for exact round trips
(including multiline/whitespace/Unicode), exact deduplication, recopy ordering,
restart persistence, FTS behavior, literal query handling, and page boundaries.
Exercise the real FTS5 provider rather than mocking SQL.

Manually verify the actual Windows integration: hotkey and conflict handling,
keyboard navigation, focus-loss dismissal, copy-only, pause/resume, self-write
suppression, paste into a normal editor, and safe fallback for a rejected target.
Routine tests must not modify the developer's clipboard or inject keystrokes.

Seed 100,000 varied, unique text entries and measure capture, reopen, first-page,
and prefix-search timings on the development machine. Record the data shape and
timings; verify that searching and preview loading do not freeze the UI. A large
row count alone is not evidence that the interactive workflow performs adequately.
