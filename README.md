# Dito

A small, local Windows clipboard history app: WinForms, .NET 10, and SQLite FTS5.
Persistent text history with optional HTML/RTF formatting, exact-text
deduplication, and no history-count limit.

## Install and run

Download the self-contained ZIP for your Windows 11 PC from
[GitHub Releases](https://github.com/traciborski/dito/releases): `win-x64` for most
Intel/AMD PCs or `win-arm64` for Arm PCs. Extract the whole ZIP and run `Dito.exe`.
No SDK, Desktop Runtime, or installer is needed. macOS and Linux (including Fedora
and Ubuntu) are not supported.

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
| Paste as plain text into the previously active app | Enter |
| Paste with original formatting when available | Shift+Enter |
| Copy plain text without pasting | Ctrl+Enter |
| Older / newer page | PageDown / PageUp |
| Close the popup, keeping capture running | Escape |

The tray menu provides Pause/Resume Capture and Exit. Closing the popup does not
exit the app. No automatic startup registration is performed.

Shift+Enter falls back to plain text for older entries and entries copied without
HTML or RTF. The destination app decides whether to use the available formatting.

Automatic paste is best effort. If Windows refuses focus restoration or input
injection (for example into an elevated app), the selected text remains ready for
manual Ctrl+V. If another app changes the clipboard during recall, automatic paste
is canceled instead of overwriting that newer copy. Opening from the tray may not
provide a usable target; the keyboard shortcut is the primary recall workflow.

## Data and privacy

History is saved immediately to `%LOCALAPPDATA%\Dito\history.db`, with SQLite WAL
sidecar files as needed. All unique entries are retained across restarts. Recopying
identical text moves that entry to the top and replaces its saved formatting
without adding another row; recalling an entry does not reorder it. Whitespace,
case, and line endings are preserved.

**Storage is unencrypted and may retain secrets indefinitely.** HTML and RTF can
also include source metadata or embedded content not shown in the preview. There
is no Clear History command, automatic pruning, network activity, or telemetry. Pause prevents
future capture; it does not remove existing data. The Windows clipboard-monitor
exclusion format is respected when present, but this is not a general secret
detector. Use only where persistent clipboard retention is permitted.

Standalone images, encryption, pinning, synchronization, and automatic
startup are not implemented.

## Development and validation

See `SPEC.md` for behavior and architecture and `AGENTS.md` for coding standards.
Building from source requires Windows and the .NET 10 SDK. The locally built EXE
uses the installed Desktop Runtime; to package the runtime, publish with
`-r win-x64 --self-contained true` (or `-r win-arm64`).

```powershell
dotnet build .\Dito.slnx -c Release
.\src\Dito\bin\Release\net10.0-windows\Dito.exe
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

### Release process

1. Set `<Version>` in `src\Dito\Dito.csproj` to the new `MAJOR.MINOR.PATCH`.
   The SDK generates assembly version metadata; there is no `AssemblyInfo.cs`.
2. Move pending notes from `Unreleased` to `## MAJOR.MINOR.PATCH` in `CHANGELOG.md`.
   Commit the version, notes, and matching `SPEC.md` changes together and push `main`.
3. Tag that commit and push the tag, for example:

   ```powershell
   git tag -a v0.1.0 -m "Dito 0.1.0"
   git push origin v0.1.0
   ```

The Release workflow verifies the tag and changelog, runs Windows tests, and
publishes both ZIPs with those release notes. Do not reuse a released version or tag.

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
Escape/focus-loss dismissal, Ctrl+Enter, and both paste shortcuts in a disposable
text editor. Compare Enter and Shift+Enter with styled text in a rich-text editor.
Also check the manual-paste fallback against a target Windows will not activate
or accept input into. Automated tests deliberately do not send keys to other apps.
An isolated native-clipboard integration check was attempted, but Windows denied
creation of its private window station. Live clipboard round trips, actual shortcut
conflicts, input injection, and Windows 11-specific interaction remain unverified;
the real hidden-window listener registration and WinForms rendering checks passed.
