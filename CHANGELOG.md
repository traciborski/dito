# Changelog

## 0.3.0

- Delete the selected history entry immediately with Delete or **Delete entry**.
- Give an entry an optional label and find it by that label using regular search.
- Do not offer an empty Older page when results end on a full page.
- Existing history is upgraded in place. Dito 0.2.0 and older cannot read the
  upgraded database.

## 0.2.0

- Add `Dito.Gnome`, a GNOME Shell 50 extension for Wayland clipboard capture and
  a searchable history popup. Enable the extension without launching another app.
- Add `Dito.X11` (previously named `Dito.Linux` in development), an Avalonia app
  for native X11 with text capture, searchable history, and manual paste.
- Split the shared history code from the Windows and Linux frontends. Releases
  include Linux x64 and Arm64 archives alongside the existing Windows ZIPs.

## 0.1.0

- Windows 11 tray app with pausable capture and persistent, deduplicated local history.
- Ctrl+backtick opens full-text search, with keyboard navigation and a plain-text preview.
- Enter pastes plain text; Shift+Enter preserves saved HTML/RTF when supported;
  Ctrl+Enter copies without pasting.
