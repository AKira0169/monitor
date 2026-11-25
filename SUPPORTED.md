# Supported displays

MonitorSwitch talks to your monitors through Windows' DDC/CI support. Whether a display works depends on the display and on everything between it and the PC. Each monitor card in the app shows a line saying what works for that display.

## What works

| Display | Input switching | Brightness |
| --- | --- | --- |
| External monitor over DisplayPort, HDMI or USB-C (DisplayPort Alt Mode), DDC/CI enabled | Yes | Yes (DDC/CI) |
| Laptop / internal panel | No | Usually (if the panel exposes Windows brightness control; no DDC/CI needed) |

Input switching also needs the monitor to report its list of inputs. If it doesn't, the card shows "Input switching ✗ (no input list)" and only brightness is available.

## What usually does not work

- Most TVs (DDC/CI is rarely implemented).
- Many docks, DisplayLink / USB graphics adapters, KVM switches and some USB-C hubs: they often do not pass DDC/CI through.
- Monitors with DDC/CI switched off in their on-screen menu (OSD). Turn it on there.
- Apple displays (no DDC/CI on Windows).
- Virtual displays and remote-desktop screens.

These displays still get a card, with the reason shown.

## Troubleshooting

1. Enable **DDC/CI** in the monitor's OSD menu (often under System, Setup or Other).
2. Connect the monitor directly to the graphics card, with the cable it came with. Remove docks, adapters and KVMs to test.
3. Try another port or cable (a DisplayPort cable is usually the most reliable).
4. Press **F5** in the app (or Refresh) after changing anything.
5. Update the graphics driver.
6. If the monitor is switched to another input it stops answering. That is expected, and the card says so.

## Models with known quirks

Built in (`src/Quirks.cs`):

| Vendor / prefix | Behaviour | Confidence |
| --- | --- | --- |
| LG (`GSM`) | Some models use non-standard input codes (0x90/0x91 for HDMI, 0xD0/0xD1 for DisplayPort), shown as "(alt code)" when the monitor lists them. Some models switch only through a vendor-specific command that Windows' DDC API cannot send: those are not supported. | Medium. Which models need which is not confirmed. |
| Apple (`APP`) | Generally no DDC/CI on Windows. | Medium-high |

The app can also work around quirks that need a different input code, a fixed input list or an extra delay, but no built-in model uses these yet.

## Tested

Only these were tested by the developer.

| Model | Manufacturer | Id |
| --- | --- | --- |
| Acer | Acer | `ACR075A` |
| Samsung | Samsung | `SAM7B4F` |

Everything else is untested. It may well work if it supports DDC/CI.

## Reporting a display that doesn't work

Open an issue and attach `%APPDATA%\MonitorSwitch\log.txt`. At startup and after a refresh that changes something, the log has one `Monitor ...` line per display (model id, adapter, whether DDC/CI answered, brightness method, inputs). Also say how the display is connected (cable, dock, adapter) and the monitor's model.
