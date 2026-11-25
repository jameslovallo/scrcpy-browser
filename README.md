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
  - **Audio capture**: starts for the selected device at launch; the footer button shows whether it is active and toggles capture
  - **Media card**: shows the active Android media session's artwork, title, and playback controls; click the artwork to open its app in scrcpy
  - Right-click a tile or running app to **Pin/Unpin**, **Relaunch** (or **Launch**), or **Close** it. **Hide** removes it from the sidebar and search (restore it in Settings). **Capture mouse** launches that app with `-M` (UHID mouse, captured) from its next launch; press Alt to release the mouse.
  - The app list contains user apps plus common system apps (Chrome, Settings, Camera, Photos, ...).
- **Main view**: the selected app's scrcpy window, borderless and embedded in an inset card with all four corners rounded. Each app runs on its own resizable virtual display (`--new-display=/<dpi> -x`), so the Android layout follows the window size at the host display's scaling.
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

The output is a single self-contained executable (.NET and Windows App SDK files are bundled), unpackaged, and needs no installer. Copy `ScrcpyLauncher.exe` anywhere and run it. Runtime files are extracted to the user's temporary .NET bundle cache when the app starts; the embedded icon and Android helper are extracted under `%LOCALAPPDATA%\ScrcpyLauncher\BundledAssets`.

## Code signing

`publish.ps1` can Authenticode-sign the distributed executable:

```powershell
.\publish.ps1 -PfxPath C:\certs\scrcpy-launcher.pfx -PfxPassword 'secret'
.\publish.ps1 -CertificateThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
```

Signatures use SHA-256 and are timestamped (override with `-TimestampUrl`). `signtool.exe` is taken from `PATH`, or from the `Microsoft.Windows.SDK.BuildTools` package restored by the build.

A self-signed certificate is enough for local tooling that checks Authenticode (and to test the pipeline):

```powershell
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Scrcpy Launcher Dev' `
    -KeyUsage DigitalSignature -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(2)
.\publish.ps1 -CertificateThumbprint $cert.Thumbprint
```

Note: **a self-signed certificate does not satisfy Smart App Control** — SAC only accepts RSA certificates issued by a CA in the Microsoft Trusted Root Program, and it has no per-app exclusion. With Smart App Control enabled, unsigned builds (including local ones) are blocked from loading, so for development either turn SAC off (Settings → Privacy & security → Windows Security → App & browser control → Smart App Control) or test unsigned builds in a VM.

## Settings

`settings.json` is created next to the executable on first run:

| Key | Description |
| --- | --- |
| `ScrcpyPath` / `AdbPath` | Executable name (resolved via `PATH`) or absolute path |
| `Theme` | `System` (default, follows Windows light/dark), `Light` or `Dark` |
| `ExtraScrcpyArgs` | Extra arguments for every session, e.g. `["--max-fps=60"]` |
| `PinnedApps` | Package names shown as pinned tiles (default: Chrome, Settings). Updated by Pin/Unpin. |
| `CaptureMouseApps` | Packages launched with `-M` (mouse captured). Updated by the Capture mouse menu option. |
| `HiddenApps` | Packages hidden from the sidebar and search. Updated by Hide and by Restore in Settings. |
| `ShowAllSystemApps` | List every system app instead of only the allowlist |
| `SystemAppAllowlist` | System packages that remain visible |

## How it works

For each app, the launcher runs:

```
scrcpy --serial=<id> --new-display=/<dpi> -x --no-vd-system-decorations --window-borderless
       --window-title=<unique> --start-app=<package> --window-x=-20000 --window-y=-20000
       -K --mouse-bind=+hbn:bhsn --no-audio
```

- No display size is passed; the launcher uses a calibrated density of 180 dpi. The launcher sizes the scrcpy *window*, and with `-x` the virtual display follows it, so Android content keeps its logical size on high-DPI screens.
- The window starts off-screen. The launcher finds it by its unique title, re-parents it into the main view (`SetParent`, `WS_CHILD`), and keeps it sized to that view. A one-time window region (`SetWindowRgn`) rounds all four corners to match the view card. It's rebuilt only when the size changes, so there's no per-frame cost.
- `--no-vd-system-decorations` hides the Android status and navigation bars.
- `-K` uses UHID keyboard input (physical keyboard simulation). Apps with **Capture mouse** enabled also get `-M` (UHID mouse; scrcpy captures the pointer until Alt is pressed).
- `--mouse-bind=+hbn:bhsn` sets up the mouse:
  - Right click is forwarded to the app.
  - Middle click = HOME, the back side button = BACK, and the forward side button = notification panel.
  - With Shift held, scrcpy's default bindings apply.
- `--no-audio` disables audio forwarding. The app list comes from `scrcpy --list-apps` and is decoded as UTF-8, where `*` marks system apps. Closing a session (or the launcher) closes scrcpy gracefully, which removes the virtual display.
- A separate `scrcpy --serial=<id> --no-video --no-window --no-control` process captures device audio, independently of app windows. The launcher starts it for the selected device and stops it when toggled off, when switching devices, or on exit.
- The sidebar media card polls the active media-key session through the on-device helper and uses `cmd media_session dispatch` for playback controls. Artwork is cached under `%LOCALAPPDATA%\ScrcpyLauncher\MediaCache`.

### App icons

Icons and active media metadata are read on the device. The launcher extracts its bundled `iconhelper.jar` (a small dex program, source in [tools/IconHelper](tools/IconHelper/Main.java)) under `%LOCALAPPDATA%\ScrcpyLauncher\BundledAssets`, pushes it to `/data/local/tmp`, and runs it with `app_process`, the same way scrcpy runs its server. The helper loads each app's resources, draws its icon (adaptive icons use the device's icon mask) to a 96 px PNG, and can retrieve the current media session and export its artwork; the launcher `adb pull`s the results.

If the device can't render an icon, the launcher uses the icon from the app's Play Store listing (`og:image`) instead. That request goes through the system proxy with your Windows credentials. Apps with no icon from either source keep a letter tile.

Icons are cached in `IconCache\<serial>\` next to the executable, or in `%LOCALAPPDATA%\ScrcpyLauncher\IconCache` if that folder isn't writable. To refresh an icon, delete its file. **Reload apps** in the device menu also retries apps that previously had no icon.

To rebuild the helper, you need a JDK and `r8.jar` from Google Maven:

```powershell
winget install Microsoft.OpenJDK.21
.\tools\IconHelper\build.ps1 -R8Jar path\to\r8.jar
```
