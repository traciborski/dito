# Dito specification

## Scope and distribution

- Local clipboard history for Windows 11 (WinForms) and native Linux X11
  sessions (Avalonia) on .NET 10 LTS, with bundled SQLite FTS5 and no application
  network traffic or telemetry. Wayland, XWayland, and macOS are not supported.
- Start manually; one instance per user per Windows session or X11 display.
  No startup registration, synchronization, encryption, pinning, standalone images,
  deletion, or automatic pruning.
  Keep all unique entries; 100,000 is a performance target, not a retention limit.
- Release from a `vMAJOR.MINOR.PATCH` Git tag. The tag sets the build version;
  the matching `CHANGELOG.md` section supplies release notes.
  Publish self-contained Windows x64 and Arm64 ZIPs and Linux x64 and Arm64
  tar.gz archives with bundled SQLite and .NET. Linux needs system desktop libraries.
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
  Rows show flattened text only, with subtle alternating backgrounds except in
  high-contrast mode. No dates or populated-page count. The selected entry has a
  read-only original plain-text preview. Distinguish empty history from no matches.
- Up/Down selects; PageUp/PageDown or Newer/Older changes page. Select the first row
  of a loaded page. Escape, closing the popup, or losing focus hides it.
- Enter or double-click pastes plain text; Shift+Enter also offers saved HTML/RTF
  alongside plain text. The destination chooses a format. Ctrl+Enter copies plain
  text without pasting. Without a selection, no recall occurs.

## Linux interaction and capture

- The first `Dito.Linux` launch opens history and starts listening. Reopening
  with `Dito.Linux --show` activates the existing instance over a local Unix
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

- On Windows, an explicitly created message-only HWND listens via `AddClipboardFormatListener`.
  Read on the STA thread. Ignore empty/non-text, monitor-excluded, and self-written
  clipboard content; preserve whitespace, case, and line endings. Retry a busy
  clipboard briefly and cancel superseded reads. The bounded capture queue drains
  on exit; failures can pause capture rather than silently dropping history.
- On Windows, store at `%LOCALAPPDATA%\Dito\history.db`. Both apps use WAL,
  bounded lock waits, and off-UI-thread database access with an explicitly owned,
  serialized connection.
  The database and sidecars are unencrypted. HTML/RTF can contain metadata or
  embedded content not shown in previews; Dito does not render these formats.
- Schema v2 (`PRAGMA user_version`) adds nullable HTML/RTF BLOBs to v1 without
  changing existing data or FTS. Deduplicate using binary equality of plain text.
  Recopies keep the ID and first-copy time, replace formatting (including clearing
  it for plain copies), and update last-copy time and monotonic `copy_order`.
  Timestamps are UTC Unix milliseconds. Recall does not reorder entries.
- Keep plain text immutable and index it on insert in the same transaction. Use
  external-content FTS5 with `unicode61` and 2/3/4-character prefix indexes.
  Search splits on whitespace, quotes and escapes literal terms, ANDs them, and
  allows a prefix on the final term only. Bind the MATCH expression as a parameter.
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
  `Dito.Core`). `src\Dito.Windows` (WinForms, `Dito.exe`) and `src\Dito.Linux`
  (Avalonia, `Dito.Linux`) each have a `Program` that composes the core with
  platform UI and integration. Run `Dito.Tests` on both platforms, plus the
  matching `Dito.Windows.Tests` or `Dito.Linux.Tests`. Use real temporary
  SQLite databases and fake clipboard/input boundaries for routine tests.
  Measure the 100,000-entry workflow before making performance claims. Record
  platform integration checks and limitations in `README.md`.
