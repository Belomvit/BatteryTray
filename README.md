# BatteryTray

A tiny Windows 11 utility that shows the battery level of wireless peripherals **right on the taskbar** — next to the weather widget, styled like a native part of the shell.

![Windows 11 style](docs/style-windows11.png)

## Supported devices

| Device | Connection | How it's read |
|---|---|---|
| **JBL Quantum350 Wireless** headset | 2.4 GHz dongle (`0ECB:206B`) | HID feature reports: `0x68` = link state, `0x49` = battery % |
| **ATK X1 Ultimate** mouse (Compx chipset) | 8K dongle (`373B:11D9`) | Vendor HID report `0x08`, command `0x04` → level, charging flag, voltage |
| **Xbox Wireless Controller** | Bluetooth | Battery value Windows keeps for paired devices (same as *Settings → Bluetooth & devices*) |

Devices that are turned off show **Off**; the Xbox controller is hidden while it's disconnected. A bolt icon appears while a device is charging.

## Styles

Pick a look from the right‑click menu → **Style**. It's remembered across restarts.

| Windows 11 | Modern | Classic (macOS) |
|---|---|---|
| ![Windows 11](docs/style-windows11.png) | ![Modern](docs/style-modern.png) | ![Classic](docs/style-classic.png) |

- **Windows 11** — thin Fluent progress ring in the system accent color.
- **Modern** — glassy capsules with a gradient level bar (amber/rose when low, green while charging).
- **Classic** — the macOS *Batteries* look: green ring, red when low.

All styles follow the system light/dark theme.

## Features

- Embedded into the taskbar itself (not the notification area), placed right after the Widgets/weather button; falls back to the left edge if the weather widget is off.
- Survives Explorer restarts — the widget is re‑attached automatically.
- Right‑click menu: **Refresh now**, **Auto‑refresh every** 1–30 minutes, **Start with Windows**, **Style**, **Exit**.
- Bluetooth devices (and anything showing *Off*) are re‑checked every 20 s, so a device appears soon after you turn it on.
- Hover a device for a tooltip with details (e.g. mouse voltage).
- Every device read has a 5 s timeout, so a stuck dongle can't freeze the widget.
- Autostart uses a shortcut in the Startup folder, so it can also be toggled in *Task Manager → Startup apps*.
- Errors never pop up dialogs; they are logged to `%LOCALAPPDATA%\BatteryTray\errors.log`.

## Requirements

- Windows 11
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (to build) or .NET 9 Desktop Runtime (to run)

## Build & run

```powershell
cd BatteryTray
dotnet build -c Release
.\bin\Release\net9.0-windows\BatteryTray.exe
```

Only one instance runs at a time.

### Troubleshooting

Write one reading per device to a file and exit:

```powershell
BatteryTray.exe --test readings.txt
```

## Adding a device

Implement `IBatteryDevice` (see [`Devices.cs`](BatteryTray/Devices.cs)) — return a `BatteryReading` with the percentage, charging flag and a detail string — then add it to the `items` list in [`Program.cs`](BatteryTray/Program.cs) with a Segoe Fluent Icons glyph.

## Project structure

| File | Purpose |
|---|---|
| `Program.cs` | Entry point, polling, settings, autostart, context menu |
| `TaskbarWidget.cs` | Layered window embedded in the taskbar and its rendering |
| `Fluent.cs` | Windows 11 colors/accent, styles, Fluent context‑menu renderer |
| `Devices.cs` | Headset and mouse readers |
| `Bluetooth.cs` | Bluetooth battery properties, Xbox controller |
| `Hid.cs` | Minimal Win32 HID helpers |
