# Dito specification

## Scope and distribution

- Local clipboard history for Windows 11 (WinForms), native Linux X11
  sessions (Avalonia), and GNOME Shell 50 on Wayland (Shell extension). All use
  .NET 10 LTS and bundled SQLite FTS5 for history. No application network
  traffic or telemetry. Other Wayland desktops and macOS are not supported.
- Start manually; one instance per user per Windows session or X11 display.
  No startup registration, synchronization, encryption, pinning, standalone images,
  bulk deletion, or automatic pruning.
  Keep unique entries until individually deleted; 100,000 is a performance target,
  not a retention limit.
- Release from a `vMAJOR.MINOR.PATCH` Git tag. The tag sets the build version;
  the matching `CHANGELOG.md` section supplies release notes.
  Publish self-contained Windows x64 and Arm64 ZIPs and Linux x64 and Arm64
  tar.gz archives and GNOME extension ZIPs with bundled SQLite and .NET.
  Linux needs system desktop libraries.
  Local restart builds use the installed .NET Desktop Runtime. See `README.md`
  for release and development commands.

## Windows interaction

- Run in the tray with no permanent window or console. The tray, popup, and EXE use
  the same gray clipboard-outline icon. Tray actions: Show History, Pause/Resume
  Capture, Exit. Pausing does not affect existing history.
- The global shortcut is Ctrl + the US OEM backtick key without Shift. On registration
  failure, warn and leave the tray usable. Save the foreground target before showing
  the popup; reopening an already visible popup only refocuses search.
- Opening a hidden popup starts at newest history with the search box focused.
  Rows show an optional label followed by flattened text, with subtle alternating
  backgrounds except in high-contrast mode. No dates or populated-page count. The selected entry has a
  read-only original plain-text preview. Distinguish empty history from no matches.
- Up/Down selects; PageUp/PageDown or Newer/Older changes page. Select the first row
  of a loaded page. Escape, closing the popup, or losing focus hides it.
- Enter or double-click pastes plain text; Shift+Enter also offers saved HTML/RTF
  alongside plain text. The destination chooses a format. Ctrl+Enter copies plain
  text without pasting. Without a selection, no recall occurs.

## X11 interaction and capture

- The first `Dito.X11` launch opens history and starts listening. Reopening
  with `Dito.X11 --show` activates the existing instance over a local Unix
  socket in the user runtime directory. Bind this command in desktop settings
  for a shortcut; Dito does not register a global shortcut itself.
- Search, page navigation, and plain-text preview use the shared history logic.
  Escape or closing the window hides it. Enter, Ctrl+Enter, double-click, or
  Copy copies the selection as plain text and hides history on success. Paste
  manually in the destination app; Dito does not inject input or restore focus.
- Use the tray menu on desktops with StatusNotifierItem support, or use
  `--show` and the window buttons to pause/resume capture or exit.
- Watch changes to the X11 `CLIPBOARD` selection with XFixes. Capture only
  nonempty external text, ignoring marked Dito writes. Rapid or superseded
  changes may be skipped; do not save stale reads or record clipboard changes
  from while capture was paused. Linux does not capture HTML/RTF or interpret
  the Windows clipboard-monitor exclusion format.
- Store history in `$XDG_DATA_HOME/dito/history.db` or, by default,
  `~/.local/share/dito/history.db`. Clipboard recall does not reorder entries.

## Capture, storage, and search

### GNOME Shell 50 on Wayland

- Enable `Dito.Gnome` in GNOME Shell. The extension owns the panel menu and
  watches the compositor's `CLIPBOARD` selection. It reads external UTF-8
  plain text asynchronously, discards superseded reads, and ignores its own
  recall by selection-owner identity. No automatic paste or rich-text capture.
- Open the panel menu to search, page, and preview. Copy or Enter offers the
  original text and closes the popup; paste manually in the destination.
  Pause stops new capture. Disabling the extension stops monitoring.
- The extension starts its bundled history helper on demand, communicates over
  private process pipes, and closes it on disable. No companion app is required.
  The helper reuses the native X11 app's Linux database and the shared .NET core.

### Shared history and Windows capture

- On Windows, an explicitly created message-only HWND listens via `AddClipboardFormatListener`.
  Read on the STA thread. Ignore empty/non-text, monitor-excluded, and self-written
  clipboard content; preserve whitespace, case, and line endings. Retry a busy
  clipboard briefly and cancel superseded reads. The bounded capture queue drains
  on exit; failures can pause capture rather than silently dropping history.
- On Windows, store at `%LOCALAPPDATA%\Dito\history.db`. All frontends use WAL,
  bounded lock waits, and off-UI-thread database access with an explicitly owned,
  serialized connection.
  The database and sidecars are unencrypted. HTML/RTF can contain metadata or
  embedded content not shown in previews; Dito does not render these formats.
- Schema v2 (`PRAGMA user_version`) adds nullable HTML/RTF BLOBs to v1 without
  changing existing data or FTS. Deduplicate using binary equality of plain text.
  Recopies keep the ID and first-copy time, replace formatting (including clearing
  it for plain copies), preserve the label, and update last-copy time and monotonic
  `copy_order`. The existing row moves to the top of recent history and matching results.
  Timestamps are UTC Unix milliseconds. Recall does not reorder entries.
- Schema v3 adds nullable labels, a label index, delete triggers, and a persistent
  sequence so deleting rows does not reuse their IDs or move copy order backwards.
- In each history popup, Delete or **Delete entry** immediately removes the
  selected row and its search entries without confirmation or undo. Delete works
  from the search field, but edits text normally in the label field. Holding the
  key does not delete additional entries. Deletion does not clear the current
  system clipboard or prevent a later external copy from being captured.
- Set one optional label per entry in the label field and use **Save label** or
  Enter while editing. Trim surrounding whitespace; saving an empty label
  clears it. Labels need not be unique. Display the label in the result row but
  preview and recall the original clip only. Editing the label does not reorder
  the entry. After deletion or a label edit, reload the first page of the current query.
- Keep plain text immutable and index it on insert in the same transaction. Use
  external-content FTS5 with `unicode61` and 2/3/4-character prefix indexes.
  Search splits on whitespace, quotes and escapes literal terms, ANDs them, and
  allows a prefix on the final term only. All terms must match the clip text or
  all must match its label; matches across both fields are not combined. Bind the
  MATCH expression as a parameter.
  Matching follows the tokenizer's case, diacritic, and punctuation rules, not
  arbitrary substring matching. Blank queries bypass FTS.
- Debounce typing 150 ms. Cancel obsolete searches/previews and discard stale
  results. Load up to 100 rows per page by `copy_order DESC` with seek cursors,
  including only IDs, metadata and up to 240 preview characters per row. Load full
  text only for preview or recall; load formatting only for formatted recall.

## Recall safety and validation

- On Windows, write clipboard data before hiding the popup. On failure, report
  it and never paste stale data. Track self-writes by sequence number and owner. For paste,
  restore the saved target; recheck target identity, foreground, released modifiers,
  and unchanged clipboard before `SendInput` sends Ctrl+V. On rejection, leave
  the selection ready for manual paste; if replaced externally, leave the newer
  clipboard alone. Do not elevate, hook the keyboard, or force modifier releases.
- `src\Dito` is the platform-neutral core (`Application`, `Storage`; assembly
  `Dito.Core`). `src\Dito.Windows` (WinForms, `Dito.exe`) and `src\Dito.X11`
  (Avalonia, `Dito.X11`) each have a `Program` that composes the core with
  platform UI and integration. Run `Dito.Tests` on both platforms, plus the
  matching `Dito.Windows.Tests` or `Dito.X11.Tests`. `src/Dito.Gnome` contains
  the GJS extension and its bundled .NET helper. Test it in an isolated GNOME
  compositor with a temporary database and synthetic clipboard data. Use real temporary
  SQLite databases and fake clipboard/input boundaries for routine tests.
  Measure the 100,000-entry workflow before making performance claims. Record
  platform integration checks and limitations in `README.md`.
