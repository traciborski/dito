# Public release checklist

## Before broadly promoting Dito

- [ ] Confirm distribution rights for the source and icon. Choose a project
  license if Dito is intended to be open source, add `LICENSE`, and link it
  from the README. Public GitHub visibility does not grant an open-source license.
- [ ] Inventory bundled dependencies and verify their redistribution terms.
  Include required third-party notices in each Windows, X11, and GNOME archive,
  then inspect the built archives to confirm the notices are actually present.
  The published v0.2.0 GNOME x64 ZIP has no standalone license or notice files.
- [ ] Smoke-test the downloaded packages on Windows 11, a real X11 desktop, and
  GNOME Shell 50 on Wayland. Cover Windows automatic paste and its manual
  fallback, Linux tray/panel behavior, and Arm64 startup on supported targets.
  Record what passed and what remains untested.

## README and GitHub

- [ ] Put a [Latest release](https://github.com/traciborski/dito/releases/latest)
  link and a short platform/package table near the top of the README. Add one
  screenshot using synthetic clipboard entries.
- [ ] Document how to stop or disable Dito and remove its local history,
  including SQLite sidecars. On Linux, explain that both frontends share the
  history and must be stopped before removing it.
- [ ] Add bug-reporting guidance that asks for the Dito version, OS, and desktop
  session without asking users to share clipboard contents or databases. Add
  `SECURITY.md` with a private reporting route.
- [ ] Update the GitHub About description to cover Windows, X11, and GNOME
  Wayland. Add topics such as `clipboard-manager`, `windows`, `x11`, and `gnome`.

## Code and release checks

- [ ] Run tests and formatter checks on pushes and pull requests, not just
  releases. Keep the isolated GNOME smoke test in the release checklist until it
  can run on a suitable CI runner.
- [ ] Update the artifact actions flagged for Node.js 20 deprecation in the
  v0.2.0 build. Check dependency advisories and document the signing status of
  Windows downloads.
