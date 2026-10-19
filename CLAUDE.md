# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

MonitorSwitch is a small Windows tray app (WinForms, .NET Framework 4.x) that lists monitors and switches their input / adjusts brightness over DDC/CI, driven by global hotkeys.

## Build & run

```
build.cmd                 # produces MonitorSwitch.exe in the repo root
MonitorSwitch.exe         # shows the window
MonitorSwitch.exe /hidden # tray only (used by the autostart entry)
```

- `build.cmd` calls the `csc.exe` bundled with .NET Framework 4.x — no SDK, no project file. Every `src\**\*.cs` is compiled (`/recurse:src\*.cs`), so new files are picked up automatically; new framework references must be added as `/r:` in `build.cmd`.
- That compiler is **C# 5**: no `?.`, `??=`, `$"..."` interpolation, `nameof`, expression-bodied members, `out var`, tuples, or pattern matching. The existing code uses explicit `var handler = Evt; if (handler != null) handler(...)` for this reason.
- `/warnaserror+` is on — any warning fails the build.
- `build.cmd` kills a running `MonitorSwitch.exe` first (the exe is locked while running).
- `MonitorSwitch.exe` is committed to the repo; rebuild it when source changes.
- There are no tests or linters. Verifying a change means building and running it against real monitors.
- Runtime files: `%APPDATA%\MonitorSwitch\settings.ini` and `log.txt` (rotated at 256 KB to `log.txt.1`). Check the log for errors from worker threads — they're caught and logged, not shown.

## Architecture

- **`Program`** — single-instance mutex. A second launch broadcasts `WM_SHOWME` (`HWND_BROADCAST`) and exits; the running instance's `HotkeyWindow` receives it.
- **`TrayApp`** — the app's owner/composition root: tray icon + menu, `HotkeyWindow`, `MainForm`, `Osd`. All DDC work goes through `RunInBackground` (thread pool) and results come back via `OnUi` (`BeginInvoke` on a hidden control). Brightness hotkeys auto-repeat faster than DDC can write, so deltas are accumulated in `pendingDelta` and drained by a single worker.
- **`Ddc`** — all DDC/CI and monitor enumeration. Every public method is blocking and serialized on one lock (I2C transactions must not overlap); never call it from the UI thread. `ForEach` opens physical-monitor handles and always destroys them afterwards.
- **`HotkeyWindow`** — hidden top-level `NativeWindow` that outlives `MainForm`. Hotkey IDs: `0` = show/hide, `1..` = bindings (indexes into a snapshot of `Settings.Bindings` taken at `Register()`), `100..` = fixed Ctrl+Alt+Arrow brightness keys (Up/Down use `brightness_step`, Left/Right use `FineBrightnessStep`).
- **`Settings`** — hand-rolled ini (`show=`, `brightness_step=`, `hotkey=key,monitor,input`, `label=monitor,input,name`, `inputs=monitor,codes...`). UI thread only. Saved atomically (temp file + `File.Replace`). Bad lines are logged and skipped, not fatal. Values can't contain commas.
- **`UI/`** — `MainForm` (monitor cards), `MonitorCard`, `Osd` (brightness overlay), `Dialogs`, `Theme` (colors/fonts, `FlatBtn`, drawn `AppIcon`).

### Key concepts that span files

- **Monitor identity**: `ShortId` (e.g. `ACR075A`, the PnP id segment of the monitor's device ID) is stable across reboots and is what hotkeys/labels/inputs are keyed on. Hotkeys target *every* monitor with that id (`device == null`); the UI passes `Device` (`DISPLAY1`) to narrow to one physical display so identical models can be told apart. `Ddc.AllMonitors` (`"*"`) targets all.
- **Input codes** are VCP 0x60 values stored in decimal in the ini (15 = DP1, 17 = HDMI1, …; see `Ddc.InputName`).
- **Capabilities cache**: reading a monitor's capabilities string takes seconds, so the parsed input list is persisted as `inputs=` lines. On startup/reload `Ddc.SeedInputs` replaces the in-memory cache from settings; newly discovered lists raise `Ddc.InputsDiscovered` (worker thread) → `TrayApp` saves them on the UI thread. Deleting an `inputs=` line forces a re-query.
- **Two-pass loading**: `MainForm.Reload` first publishes a quick list without DDC reads (`Loaded == false`), then the full read. A `loadVersion` counter discards results from superseded reloads.
- **Settings change flow**: UI edits mutate `Settings` → `TrayApp.SettingsChanged()` saves and re-registers hotkeys. "Reload settings" in the tray menu re-reads the file from disk. Hotkeys that fail to register (owned by another app) are reported via balloon tip and in the main window.
- **Autostart** is an HKCU `Run` value; on each start it is rewritten if present so it follows the exe if it was moved.
