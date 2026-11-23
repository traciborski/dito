# Dito

A small, local clipboard history app for Windows 11, native Linux X11, and GNOME
Shell 50 on Wayland. All versions use .NET 10 and SQLite FTS5 for persistent text history, exact-text
deduplication, and search. Windows also captures optional HTML/RTF formatting.

## Install and run

### Windows

Download the self-contained ZIP for your Windows 11 PC from
[GitHub Releases](https://github.com/traciborski/dito/releases): `win-x64` for most
Intel/AMD PCs or `win-arm64` for Arm PCs. Extract the whole ZIP and run `Dito.exe`.
No SDK, Desktop Runtime, or installer is needed.

The app starts in the notification area. Copy some text, then press
**Ctrl + the backtick/tilde key, without Shift**. The shortcut is defined centrally
in `src\Dito.Windows\Windows\MessageWindow.cs`. If another app owns it, use the tray icon
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
| Delete the selected entry without confirmation | Delete |
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

### Linux (native X11)

Use a native X11 session (`echo "$XDG_SESSION_TYPE"` should print `x11`).
This executable does not run on Wayland or XWayland. Select the
`Dito-X11` tar.gz for `linux-x64` or `linux-arm64`, extract the whole archive,
and run `./Dito.X11`.
The archive includes .NET and SQLite. It still needs your distro's desktop
libraries (X11 with XFixes, fontconfig, and standard .NET system dependencies).
The same app is used across distros; compatibility has not been verified
across every distro and window manager.

Copy text in another app, then open history from the tray or by running
`/absolute/path/to/Dito.X11 --show`. Set that command as a custom keyboard
shortcut in your desktop settings. Search, use Up/Down and PageUp/PageDown to
navigate, and press Enter or Ctrl+Enter to copy the original plain text and hide
history. Paste manually in the destination app. Keep Dito running until you've
pasted; X11 clipboard data may not persist after Dito exits.

Escape and closing the window hide it. The tray requires StatusNotifierItem
support; use the window's **Pause Capture** and **Exit** buttons if the tray icon
is unavailable. There is no automatic paste, formatted capture or recall, or
automatic startup registration on Linux.

### GNOME Shell 50 (Wayland)

Download the `Dito-Gnome` extension ZIP for `linux-x64` or `linux-arm64` and install
it without extracting it first:

```bash
gnome-extensions install ./Dito-Gnome-v0.2.0-linux-x64.zip
```

Log out and back in if GNOME doesn't see the extension yet. Enable **Dito** in
the Extensions app or run `gnome-extensions enable dito@traciborski.github.io`.
Use the clipboard icon in the top panel to open history, search, preview, pause,
or copy back. Press Enter to copy, then paste manually in your destination.
You don't need to launch the X11 app or install .NET. Disabling the extension
stops capture. The extension and native X11 app share Linux history if used in
separate sessions.

### Find by label or delete an entry (all platforms)

Select an entry, type an **Optional label**, and choose **Save label** (or press
Enter in the label field). For example, label `alice@example.com` as `email`,
then search for `email` and press Enter to recall `alice@example.com`, not the
label. The usual search rules apply: all terms must match the clip text or its
label. Labels can be shared by multiple entries, survive recopies, and can be
cleared by saving a blank label.

Press **Delete** or choose **Delete entry** to remove the selected result without
confirmation or undo. With a result selected, Delete removes it even while the
search box is focused; in the label field, Delete edits the label as usual.
Deleting an entry does not clear the system clipboard.
This version upgrades the history database; v0.2.0 and older cannot read it
after the upgrade.

## Data and privacy

History is saved to `%LOCALAPPDATA%\Dito\history.db` on Windows and
`$XDG_DATA_HOME/dito/history.db` (default `~/.local/share/dito/history.db`) on Linux,
with SQLite WAL sidecar files as needed. Unique entries are retained across
restarts unless deleted. Copying identical text again from another app moves
the existing entry to the top and replaces its saved formatting without adding
another row or removing its label. Recalling an entry does not reorder it. Whitespace,
case, and line endings are preserved.

**Storage is unencrypted and may retain secrets indefinitely.** HTML and RTF can
also include source metadata or embedded content not shown in the preview. There
is no Clear History command, automatic pruning, network activity, or telemetry.
Deleting individual entries is not secure erasure. Pause prevents
future capture; it does not remove existing data. The Windows clipboard-monitor
exclusion format is respected when present, but this is not a general secret
detector. Use only where persistent clipboard retention is permitted.

Standalone images, encryption, pinning, synchronization, and automatic
startup are not implemented.

## Development and validation

See `SPEC.md` for behavior and architecture and `AGENTS.md` for coding standards.
Building from source requires the .NET 10 SDK. The locally built Windows EXE
uses the installed Desktop Runtime; to package the runtime, publish with
`-r win-x64 --self-contained true` (or `-r win-arm64`).

### Windows

Run `.\scripts\restart.ps1` in PowerShell to stop Dito in your session, publish a slim
Release build, and start `artifacts\Dito\Dito.exe`. This needs the SDK to build,
and the .NET 10 Desktop Runtime to run. Both local builds and GitHub releases
bundle SQLite with FTS5; only GitHub releases also bundle .NET.
The script stops Dito without draining pending captures; use **Exit** in the
tray first if you need a graceful shutdown.

```powershell
dotnet build .\Dito.slnx -c Release
.\src\Dito.Windows\bin\Release\net10.0-windows\Dito.exe
dotnet test .\tests\Dito.Tests -c Release --filter "Category!=Performance"
dotnet test .\tests\Dito.Windows.Tests -c Release --filter "Category!=Performance"
dotnet format .\Dito.slnx --verify-no-changes --no-restore
dotnet test .\Dito.slnx -c Release --filter "Category=Performance" --logger "console;verbosity=detailed"
```

### Linux

```bash
dotnet test tests/Dito.Tests -c Release --filter 'Category!=Performance'
dotnet test tests/Dito.X11.Tests -c Release
dotnet run --project src/Dito.X11 -c Release -- --show
dotnet publish src/Dito.X11 -c Release -r linux-x64 --self-contained true
```

Swap `linux-x64` for `linux-arm64` to build the Arm64 archive. To verify formatting
for the entire solution from Linux, run
`dotnet restore Dito.slnx -p:EnableWindowsTargeting=true` followed by
`EnableWindowsTargeting=true dotnet format Dito.slnx --verify-no-changes --no-restore`.

To package the GNOME extension locally (requires `jq` and `zip`):

```bash
bash scripts/package-gnome.sh linux-x64 0.2.0 artifacts/Dito-Gnome-local.zip
```

On GNOME 50, the smoke test requires `gnome-shell-test-tool` and Python with
PyGObject and GTK 4. It uses a private headless compositor and temporary history:

```bash
bash scripts/test-gnome.sh "$PWD/artifacts/Dito-Gnome-local.zip"
```

Functional tests use temporary real SQLite databases and fake clipboard/input
boundaries. WinForms smoke tests render a hidden form on a real STA message loop;
they do not modify the live clipboard or inject keystrokes. The opt-in performance
filter seeds 100,000 varied unique clips, measures inserts, recopy, reopen, search,
and preview loading, and checks that the hidden UI continues processing messages.
Running the core or Windows test project without a filter also runs its slower
performance test.

### Release process

1. Move pending notes from `Unreleased` to `## MAJOR.MINOR.PATCH` in `CHANGELOG.md`.
2. Bump `version.yaml` in the same change and land it on `main`. For example:

   ```yaml
   version: "0.3"
   ```

This example builds `0.3.0` and uses the `## 0.3.0` notes. The Release workflow
runs the core and matching app tests on each OS and packages Windows ZIPs, native
X11 tar.gz archives, and GNOME extension ZIPs. Once packaging passes, it tags the
built commit `v0.3.0` and publishes the GitHub release. No manual tag is needed.
If the workflow fails, rerun it from Actions. Local builds still use the .NET SDK
default (`1.0.0`) unless you pass `-p:Version=...`.

### Measured 100,000-entry results

Baseline measured before label search was added, in Release on Windows 10 x64,
.NET 10.0.14, with a warm OS file cache.
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

Linux capture, pause/resume, `--show`, search, exact plain-text recall, and
self-write filtering were exercised on an isolated Xvfb server on Fedora x64.
Tray registration and menu actions were checked against a test StatusNotifier
watcher on a private D-Bus. Focus and tray behavior on real desktops still need
manual checks.

On Fedora x64, the packaged GNOME extension passed an isolated GNOME Shell 50.6
smoke test for native Wayland capture, search, preview, exact-text recall, pause,
and persistence. It was not installed in the live session or tested on Ubuntu.
