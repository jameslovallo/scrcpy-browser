# Scrcpy Launcher

A portable WinUI 3 desktop app that mirrors individual Android apps with [scrcpy](https://github.com/Genymobile/scrcpy), embedded in a taskbar-style shell.

The layout is inspired by the Arc and Zen browsers:

- **Title bar**
  - Device button: shows the device name. Its dropdown selects the device and can refresh devices or reload the app list.
  - **Settings** button (gear): opens the settings view in the main area
  - The current app's name, left-aligned above the mirror view
  - Android **Back** button (a rounded chevron), aligned with the sidebar's right edge; it sends `KEYCODE_BACK` to the current app's display
- **Keyboard shortcuts** (these work even while the mirrored app has focus, and aren't forwarded to the device)
  - `Ctrl+T` (or `Ctrl+F`): focus the search box
  - `Ctrl+W`: close the current app (or the settings view)
- **Sidebar** (icon-only, except for running app names)
  - **Search** box ("Open an app", localized), aligned with the top of the mirror view: click it or type to see installed apps (icon + name). Enter launches the best match.
  - **Pinned apps**: a grid of icon tiles that can be rearranged by dragging. An accent border marks the app on screen.
  - **Running apps**: unpinned sessions, each with an icon, its name, and ✕ to close it
  - Right-click a tile or running app to **Pin/Unpin**, **Relaunch** (or **Launch**), or **Close** it. **Hide** removes it from the sidebar and search (restore it in Settings). **Capture mouse** launches that app with `-M` (UHID mouse, captured) from its next launch; press Alt to release the mouse.
  - The app list contains user apps plus common system apps (Chrome, Settings, Camera, Photos, ...).
- **Main view**: the selected app's scrcpy window, borderless and embedded in an inset card with all four corners rounded. Each app runs on its own resizable virtual display (`--new-display -x`), so the Android layout follows the window size.
- **Settings view**: restore hidden apps, and uninstall apps. Uninstall asks for confirmation first. User apps are removed with `adb uninstall <pkg>`; system apps with `adb shell pm uninstall --user 0 <pkg>`, which removes them for the current user only.

## Requirements

- Windows 10 1809+ (x64 or ARM64)
- `scrcpy` with `-x` / `--flex-display` support on `PATH` (tested with 5.0.1; `winget install Genymobile.scrcpy`). `adb` is used from `PATH` or from scrcpy's folder.
- Android device with USB debugging enabled. Virtual displays need Android 10+, and Android 13+ is recommended.

## Build & run

Requires the .NET 10 SDK.

```powershell
dotnet run --project src\ScrcpyLauncher
```

To run the app-list encoding regression test:

```powershell
dotnet run --project tests\AppListEncoding
```

## Portable build

```powershell
.\publish.ps1                  # -> dist\win-x64\ScrcpyLauncher.exe
.\publish.ps1 -Runtime win-arm64 -Zip
```

The output is self-contained (.NET and Windows App SDK runtimes are bundled), unpackaged, and needs no installer. Copy the folder anywhere and run it.

## Settings

`settings.json` is created next to the executable on first run:

| Key | Description |
| --- | --- |
| `ScrcpyPath` / `AdbPath` | Executable name (resolved via `PATH`) or absolute path |
| `Theme` | `System` (default, follows Windows light/dark), `Light` or `Dark` |
| `ExtraScrcpyArgs` | Extra arguments for every session, e.g. `["--no-audio", "--max-fps=60"]` |
| `PinnedApps` | Package names shown as pinned tiles (default: Chrome, Settings). Updated by Pin/Unpin. |
| `CaptureMouseApps` | Packages launched with `-M` (mouse captured). Updated by the Capture mouse menu option. |
| `HiddenApps` | Packages hidden from the sidebar and search. Updated by Hide and by Restore in Settings. |
| `ShowAllSystemApps` | List every system app instead of only the allowlist |
| `SystemAppAllowlist` | System packages that remain visible |

## How it works

For each app, the launcher runs:

```
scrcpy --serial=<id> --new-display -x --no-vd-system-decorations --window-borderless
       --window-title=<unique> --start-app=<package> --window-x=-20000 --window-y=-20000
       -K --mouse-bind=+hbn:bhsn --audio-source=output
```

- No display size is passed. The launcher sizes the scrcpy *window*, and with `-x` the virtual display follows it.
- The window starts off-screen. The launcher finds it by its unique title, re-parents it into the main view (`SetParent`, `WS_CHILD`), and keeps it sized to that view. A one-time window region (`SetWindowRgn`) rounds all four corners to match the view card. It's rebuilt only when the size changes, so there's no per-frame cost.
- `--no-vd-system-decorations` hides the Android status and navigation bars.
- `-K` uses UHID keyboard input (physical keyboard simulation). Apps with **Capture mouse** enabled also get `-M` (UHID mouse; scrcpy captures the pointer until Alt is pressed).
- `--mouse-bind=+hbn:bhsn` sets up the mouse:
  - Right click is forwarded to the app.
  - Middle click = HOME, the back side button = BACK, and the forward side button = notification panel.
  - With Shift held, scrcpy's default bindings apply.
- Each session captures the device audio. To avoid hearing it once per session, the launcher mutes the Windows audio session of every scrcpy process except the one on screen (Core Audio `ISimpleAudioVolume`). The app list comes from `scrcpy --list-apps` and is decoded as UTF-8, where `*` marks system apps. Closing a session (or the launcher) closes scrcpy gracefully, which removes the virtual display.

### App icons

Icons are rendered on the device. The launcher pushes `Assets\iconhelper.jar` (a small dex program, source in [tools/IconHelper](tools/IconHelper/Main.java)) to `/data/local/tmp` and runs it with `app_process`, the same way scrcpy runs its server. The helper loads each app's resources, draws its icon (adaptive icons use the device's icon mask) to a 96 px PNG, and the launcher `adb pull`s the results.

If the device can't render an icon, the launcher uses the icon from the app's Play Store listing (`og:image`) instead. That request goes through the system proxy with your Windows credentials. Apps with no icon from either source keep a letter tile.

Icons are cached in `IconCache\<serial>\` next to the executable, or in `%LOCALAPPDATA%\ScrcpyLauncher\IconCache` if that folder isn't writable. To refresh an icon, delete its file. **Reload apps** in the device menu also retries apps that previously had no icon.

To rebuild the helper, you need a JDK and `r8.jar` from Google Maven:

```powershell
winget install Microsoft.OpenJDK.21
.\tools\IconHelper\build.ps1 -R8Jar path\to\r8.jar
```
