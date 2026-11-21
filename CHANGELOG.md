# Changelog

## Unreleased

- Add an Avalonia Linux app for native X11 sessions with text capture, searchable
  history, and copy-back for manual paste. Wayland and XWayland are not supported.
- Split the shared history code from the Windows and Linux frontends. Releases
  include Linux x64 and Arm64 archives alongside the existing Windows ZIPs.

## 0.1.0

- Windows 11 tray app with pausable capture and persistent, deduplicated local history.
- Ctrl+backtick opens full-text search, with keyboard navigation and a plain-text preview.
- Enter pastes plain text; Shift+Enter preserves saved HTML/RTF when supported;
  Ctrl+Enter copies without pasting.
