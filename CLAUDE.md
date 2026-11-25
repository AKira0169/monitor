# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

MonitorSwitch is a small Windows tray app (WinForms, .NET Framework 4.x) that lists monitors and switches their input / adjusts brightness over DDC/CI, driven by global hotkeys. End-user compatibility info (what works, troubleshooting, quirks table, tested models) is in `SUPPORTED.md` — keep it in sync with `src/Quirks.cs`.

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
- **`Ddc` details** — every dxva2 call goes through `GetVcp`/`SetVcp` (and the capabilities read) with up to 3 attempts, 45 ms apart, only on failure. Physical-monitor handle `0` is valid on recent Windows; `Ddc.NoHandle` (-1) is the sentinel for "no DDC", used for displays Windows gives no physical monitor for (laptop panels): they are still listed (`MonitorInfo.NoHandle`). Laptop brightness goes through WMI (`root\wmi` `WmiMonitorBrightness` / `WmiMonitorBrightnessMethods.WmiSetBrightness`), joined to the monitor by PnP instance (`MonitorInfo.Instance`) like the serial lookup. `MonitorInfo.BrightnessVia` (`None/Ddc/Wmi`) records the source; when DDC brightness can't be read but WMI has the panel, `List` marks it WMI (`wmiPanels`) and `Set`/`AdjustBrightness` route brightness through WMI *outside* the DDC `Lock` (WMI has its own lock; "no WMI brightness at all" is cached 60 s). `AdjustBrightness` (hotkey path) uses a single DDC attempt, skips recently switched-away monitors, uses WMI only for known WMI panels / no-handle displays, and never forces a WMI reload (only `List` may; `ForEach(..., allowWmiReload)`). When an input switch actually changes the input, the monitor is remembered for 90 s (`switchedAt`, keyed ShortId+Device) so a monitor that stops answering shows `SwitchedAway` instead of an error. `Ddc.List(true)` logs one `Monitor ...` INFO line per display (Device, ShortId, UnitId, Adapter, DdcOk, Handle, BrightnessVia, Inputs, Quirk) only when that set changed since the last log; `TrayApp` runs one scan at startup so the log always has them.
- **`Quirks`** (`src/Quirks.cs`) — static built-in table keyed by ShortId (exact) or prefix such as the 3-letter PnP manufacturer code (longest match wins). A `Quirk` can remap the displayed input code to the code written to VCP 0x60 (`Remap`), supply a fixed input list (`FixedInputs`), add a post-write delay (`DelayMs`), and carry a `Note` (shown on not-controllable cards). Current entries (LG `GSM`, Apple `APP`) are note-only. The vendor-specific VCP 0xF4 special-address switch is not possible with dxva2 and is documented as unsupported. There is no ini override for quirks.
- **`HotkeyWindow`** — hidden top-level `NativeWindow` that outlives `MainForm`. Hotkey IDs: `0` = show/hide, `1..` = bindings (indexes into a snapshot of `Settings.Bindings` taken at `Register()`), `100..` = fixed Ctrl+Alt+Arrow brightness keys (Up/Down use `brightness_step`, Left/Right use `FineBrightnessStep`).
- **`Settings`** — hand-rolled ini (`show=`, `brightness_step=`, `hotkey=key,monitor,input`, `label=monitor,input,name`, `inputs=monitor,codes...`). UI thread only. Saved atomically (temp file + `File.Replace`). Bad lines are logged and skipped, not fatal. Values can't contain commas.
- **`UI/`** — `MainForm` (monitor cards), `MonitorCard` (each card has a support line from `AddSupport`: what works, or the likely reason it doesn't; Theme.Muted / Theme.Warn), `Osd` (brightness overlay), `Dialogs`, `Theme` (colors/fonts, `FlatBtn`, drawn `AppIcon`).

### Key concepts that span files

- **Monitor identity**: `ShortId` (e.g. `ACR075A`, the PnP model id from the monitor's device interface path) is the model id: it is stable across reboots and keys `inputs=` (same model, same inputs). Identical models share it, so `MonitorInfo.UnitId` identifies one physical unit: `ShortId-<EDID serial>` (WMI `WmiMonitorID.SerialNumberID`) or, if the serial is empty/`0`, `ShortId-<port token>` (e.g. `ACR075A-UID4357`, the instance segment of the interface path, stable while the cable stays on the same port). `MonitorInfo.PortId` is always the port form and is accepted as an alias for `UnitId` (so ids survive WMI failing or a serial appearing later); units with a duplicate serial fall back to the port form (de-duplicated in `Ddc.ForEach`, so `List`/`Set` agree). `UnitId` equals `ShortId` when nothing better is known and never contains commas/whitespace. `Ddc.Set` matches an id against `AllMonitors` (`"*"`), `ShortId` (every unit of the model, so old ini files keep working) or `UnitId` (one unit); `device` (`DISPLAY1`, not stable across reboots) only narrows at runtime. The UI stores `UnitId` in `hotkey=`/`label=` only when another listed monitor shares the `ShortId`, else `ShortId`; card lookups (`Settings.Label(MonitorInfo, …)`, `Settings.FindBinding`) check `UnitId` first, then the model-wide entry. The WMI query (names + serials) is cached; a monitor missing from it triggers one reload (max every 10 s).
- **Input codes** are VCP 0x60 values stored in decimal in the ini (15 = DP1, 17 = HDMI1, …; see `Ddc.InputName`).
- **Capabilities cache**: reading a monitor's capabilities string takes seconds, so the parsed input list is persisted as `inputs=` lines. On startup/reload `Ddc.SeedInputs` replaces the in-memory cache from settings; newly discovered lists raise `Ddc.InputsDiscovered` (worker thread) → `TrayApp` saves them on the UI thread. Deleting an `inputs=` line forces a re-query.
- **Two-pass loading**: `MainForm.Reload` first publishes a quick list without DDC reads (`Loaded == false`), then the full read. A `loadVersion` counter discards results from superseded reloads.
- **Settings change flow**: UI edits mutate `Settings` → `TrayApp.SettingsChanged()` saves and re-registers hotkeys. "Reload settings" in the tray menu re-reads the file from disk. Hotkeys that fail to register (owned by another app) are reported via balloon tip and in the main window.
- **Autostart** is an HKCU `Run` value; on each start it is rewritten if present so it follows the exe if it was moved.
