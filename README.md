# Dito

A small, local Windows clipboard history app: WinForms, .NET 10, and SQLite FTS5.
Text only, persistent exact-text deduplication, and no history-count limit.

## Build and run

Requires Windows and the .NET 10 SDK (including Windows desktop support).

```powershell
dotnet build .\Dito.slnx -c Release
.\src\Dito\bin\Release\net10.0-windows\Dito.exe
```

For a separate distributable folder (requires the .NET 10 Desktop Runtime):

```powershell
dotnet publish .\src\Dito\Dito.csproj -c Release -o .\artifacts\Dito
.\artifacts\Dito\Dito.exe
```

Keep the whole published folder, not only the executable.

The app starts in the notification area. Copy some text, then press
**Ctrl + the backtick/tilde key, without Shift**. The shortcut is defined centrally
in `src\Dito\Windows\MessageWindow.cs`. If another app owns it, use the tray icon
to open history.

Type to search. All words must match; the last word can be a prefix. Search is
case-insensitive word matching, not arbitrary substring matching. For example,
`server con` matches `Server connection`.

| Action | Key |
|---|---|
| Select an entry | Up / Down |
| Paste into the previously active app | Enter |
| Copy the full entry without pasting | Ctrl+Enter |
| Older / newer page | PageDown / PageUp |
| Close the popup, keeping capture running | Escape |

The tray menu provides Pause/Resume Capture and Exit. Closing the popup does not
exit the app. No automatic startup registration is performed.

Automatic paste is best effort. If Windows refuses focus restoration or input
injection (for example into an elevated app), the selected text remains ready for
manual Ctrl+V. If another app changes the clipboard during recall, automatic paste
is canceled instead of overwriting that newer copy. Opening from the tray may not
provide a usable target; the keyboard shortcut is the primary recall workflow.

## Data and privacy

History is saved immediately to `%LOCALAPPDATA%\Dito\history.db`, with SQLite WAL
sidecar files as needed. All unique entries are retained across restarts. Recopying
identical text moves that entry to the top without adding another row; recalling
an entry does not reorder it. Whitespace, case, and line endings are preserved.

**Storage is unencrypted and may retain secrets indefinitely.** There is no Clear
History command, automatic pruning, network activity, or telemetry. Pause prevents
future capture; it does not remove existing data. The Windows clipboard-monitor
exclusion format is respected when present, but this is not a general secret
detector. Use only where persistent clipboard retention is permitted.

Images, rich-text formats, encryption, pinning, synchronization, and automatic
startup are not implemented.

## Development and validation

See `SPEC.md` for behavior and architecture and `AGENTS.md` for coding standards.
There is one production project, with UI, application, storage, and Windows API
responsibilities kept separate.

```powershell
dotnet test .\Dito.slnx -c Release --filter "Category!=Performance"
dotnet format .\Dito.slnx --verify-no-changes --no-restore
dotnet test .\Dito.slnx -c Release --filter "Category=Performance" --logger "console;verbosity=detailed"
```

Functional tests use temporary real SQLite databases and fake clipboard/input
boundaries. WinForms smoke tests render a hidden form on a real STA message loop;
they do not modify the live clipboard or inject keystrokes. The opt-in performance
filter seeds 100,000 varied unique clips, measures inserts, recopy, reopen, search,
and preview loading, and checks that the hidden UI continues processing messages.
Running `dotnet test` without a filter also runs that slower performance test.

### Measured 100,000-entry results

Measured in Release on Windows 10 x64, .NET 10.0.14, with a warm OS file cache.
The synthetic clips contain 82-1,608 characters (584 average), including code,
multiline text, whitespace, and Unicode. The checkpointed database was 221 MiB.
These are local measurements, not guaranteed latency limits.

| Operation | Median | p95 |
|---|---:|---:|
| Insert into existing history | 7.86 ms | 15.52 ms |
| Recopy an existing entry | 6.36 ms | 7.50 ms |
| Recent first page | 0.29 ms | 6.57 ms |
| Search `server con` | 10.25 ms | 17.16 ms |
| Broad prefix `c` | 55.87 ms | 76.66 ms |
| No-match search | 16.56 ms | 21.14 ms |

Database reopen took 2.83 ms. During 40 hidden WinForms searches with full selected
previews, the UI timer continued running with a maximum observed 19.57 ms gap.
The 150 ms typing debounce is additional to database query time.

### Remaining manual checks

Before relying on automatic paste, check the real shortcut, capture, pause/resume,
Escape/focus-loss dismissal, Ctrl+Enter, and Enter in a disposable text editor.
Also check the manual-paste fallback against a target Windows will not activate
or accept input into. Automated tests deliberately do not send keys to other apps.
An isolated native-clipboard integration check was attempted, but Windows denied
creation of its private window station. Live clipboard round trips, actual shortcut
conflicts, input injection, and Windows 11-specific interaction remain unverified;
the real hidden-window listener registration and WinForms rendering checks passed.
