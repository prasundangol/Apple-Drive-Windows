# Apple Drive

Apple Drive is a Windows desktop app that copies photos and videos from an iPhone connected by USB to a folder on your PC or an external drive. It detects files you already have, so duplicates aren't copied again.

Apple Drive never deletes or changes anything on your iPhone, never overwrites files on your PC, and never sends your photos anywhere. Everything runs locally and offline.

> **Status:** early development. Detecting an iPhone, reading its media, and showing a scan summary works on real hardware. Destination scanning, duplicate detection, and transfers are in progress. See [Roadmap](#roadmap).

---

## Requirements

| | |
|---|---|
| Windows | Windows 10 version 2004 (build 19041) or later, Windows 11 recommended. x64 or ARM64. |
| .NET SDK | .NET 10 SDK (10.0.400 or later). Pinned in `global.json`. |
| IDE (optional) | Visual Studio 2022 17.14+ or Visual Studio 2026 with the **.NET desktop development** workload and **Windows App SDK C# templates**. Building from the command line needs only the SDK. |
| Windows App SDK | Restored from NuGet (`Microsoft.WindowsAppSDK`). The app is self-contained, so no separate runtime install is needed. |

### NuGet packages

| Package | Purpose |
|---|---|
| Microsoft.WindowsAppSDK, Microsoft.Windows.SDK.BuildTools | WinUI 3 UI |
| CommunityToolkit.Mvvm | MVVM source generators (observable properties, commands) |
| Microsoft.Extensions.DependencyInjection / Logging | Dependency injection and logging abstractions |
| Serilog, Serilog.Extensions.Logging, Serilog.Sinks.File | Rolling diagnostic log files |
| xunit.v3, Microsoft.NET.Test.Sdk, xunit.runner.visualstudio | Tests |

Versions are managed centrally in `Directory.Packages.props`.

## Build and run

```powershell
# Build everything
dotnet build AppleDrive.slnx

# Run the app (x64)
dotnet run --project src/AppleDrive.App

# Run all tests
dotnet test --solution AppleDrive.slnx
```

For ARM64, add `-p:Platform=ARM64`.

### Device probe (diagnostics)

`tools/AppleDrive.DeviceProbe` is a console tool that checks the whole iPhone path without the UI. It lists devices, connects, enumerates media, and reads a few files into memory to verify size, content type, and throughput. It writes nothing to disk and changes nothing on the phone.

```powershell
dotnet run --project tools/AppleDrive.DeviceProbe -- --read 10
```

---

## iPhone setup

1. Connect the iPhone to the PC with a USB cable (not only Wi-Fi sync).
2. Unlock the iPhone.
3. When the iPhone asks **"Trust This Computer?"**, tap **Trust** and enter your passcode.
4. Recommended: on the iPhone, open **Settings → Apps → Photos** (older iOS: **Settings → Photos**). Under **Transfer to Mac or PC**, choose **Keep Originals**. With **Automatic**, the iPhone converts HEIC/HEVC to JPEG/H.264 while copying. That is slower and is known to cause transfer errors.

### Is Apple software required?

Usually not. Windows installs the *Apple Mobile Device USB Device* driver automatically through Windows Update the first time an iPhone is connected. If Windows sees the iPhone but Apple Drive can't read it, install the free **Apple Devices** app from the Microsoft Store. It installs Apple's current USB driver.

### How the iPhone is accessed

An iPhone is **not** a USB mass-storage device. It exposes its camera roll over PTP (Picture Transfer Protocol), which Windows presents through **Windows Portable Devices (WPD)**, the same layer File Explorer uses for "Apple iPhone › Internal Storage". Apple Drive uses the documented WPD COM API directly. It declares only the read operations it needs, so it has no way to modify the device.

Alternatives considered and rejected:

| Option | Why not |
|---|---|
| `Windows.Media.Import` (PhotoImportManager) | Many reported iPhone failures on large or HEIC imports. Less control over the files and their integrity. |
| WinRT `StorageDevice` | Designed for removable mass storage; access issues from WinUI 3 desktop apps. |
| Shell namespace (`IShellItem`) | A thin wrapper over WPD with less metadata and more overhead. |
| libimobiledevice / AFC | Third-party native stack that needs Apple Mobile Device Support. The .NET binding was archived in 2024. |

### Known limitations and behaviour

- **Locked or untrusted iPhone:** Windows still lists the phone, but its storage looks empty. Apple Drive shows "Unlock your iPhone" and checks again every few seconds until the phone is readable.
- **iCloud Photos with "Optimize iPhone Storage":** photos whose full-resolution original is only in iCloud are **not available over USB**. Only items stored on the phone are listed. To include them, download the originals to the iPhone first (Settings → Photos → Download and Keep Originals).
- **Live Photos** appear as two files with the same name, for example `IMG_1234.HEIC` and `IMG_1234.MOV`. Apple Drive pairs them and always imports both together.
- **Edited photos** appear as separate `IMG_E1234.*` files next to the original. `.AAE` edit-instruction sidecars are not imported; Windows cannot use them.
- **Folder names** on the phone vary between iOS versions (`100APPLE`, `202409__`, `202409_a`, …). Apple Drive enumerates recursively and doesn't depend on them.
- **Metadata:** many items report no capture date over USB, and none report dimensions. Those details are read from the file itself after copying.
- **One file at a time:** the iPhone driver allows one open file stream per device. Apple Drive reads from the phone strictly one file at a time (see [Transfer safety](#transfer-safety)).

---

## Architecture

```
src/
  AppleDrive.Domain          Entities, enums, value types and pure domain logic (no dependencies)
  AppleDrive.Application     Use-case services and the interfaces infrastructure must implement
  AppleDrive.Infrastructure  WPD iPhone access, file system, settings (later: SQLite, hashing, imaging)
  AppleDrive.Presentation    ViewModels and localizable strings (no WinUI dependency, unit-testable)
  AppleDrive.App             WinUI 3 views, window, composition root (DI, logging)
tools/
  AppleDrive.DeviceProbe     Console diagnostics for the iPhone connection
tests/
  AppleDrive.Testing         Shared test doubles (FakeIPhonePhotoSource, FakePhoneDeviceService, …)
  AppleDrive.UnitTests
  AppleDrive.IntegrationTests
```

Dependencies point inward: `App → Presentation → Application → Domain`. `Infrastructure` implements `Application` interfaces and is connected up only in the composition root (`App.xaml.cs`). ViewModels never touch the file system, Windows APIs or hashing directly.

Key abstractions:

| Interface | Production implementation | Purpose |
|---|---|---|
| `IPhoneDeviceService` | `WpdPhoneDeviceService` | Lists attached iPhones; `DeviceWatcher` on the WPD interface class for plug/unplug |
| `IPhonePhotoSource` | `WpdPhotoSource` | Connect, enumerate media, open read-only streams |
| `ISettingsService` | `JsonSettingsService` | Persisted user preferences |
| `IAppPaths` | `AppPaths` | Per-user data locations |

Errors that can be expected (device locked, unplugged, busy) are returned as `Result<T>` / `AppError` values with a stable `ErrorKind`, not thrown as exceptions. The UI maps `ErrorKind` to plain-language text and keeps technical details behind **Show details**.

### Data locations

All app data lives under `%LOCALAPPDATA%\AppleDrive\`:

| Path | Contents |
|---|---|
| `settings.json` | User preferences (written atomically) |
| `Logs\apple-drive-YYYYMMDD.log` | Diagnostic logs, 14 days retained. Settings → *Open logs folder*. |

Logs record events (connection, scan counts, errors) but never image data.

### Localization

User-facing text lives in `src/AppleDrive.Presentation/Resources/Strings.resx`, accessed through the `Strings` class. XAML binds to it with `{x:Bind res:Strings.Name}`. To add a language, add `Strings.<culture>.resx`. A unit test checks that every string has a resource value.

---

## Duplicate detection

*(In progress.)* Planned design:

1. **Metadata pre-filter:** only destination files with the same byte size (and media type) can be exact duplicates.
2. **SHA-256:** exact duplicates are confirmed by content hash. Only byte-for-byte identical files are skipped automatically.
3. **Perceptual hash (images only):** visually similar images (resized, recompressed, HEIC vs JPEG) are flagged as *possible duplicates* and always shown to you for a decision. They are never skipped silently.

## Transfer safety

*(In progress.)* Guarantees the transfer engine is built around:

- Nothing on the iPhone is ever modified or deleted. The device layer can't do it.
- Files are copied to a temporary `*.partial` file, flushed, verified (size + SHA-256), and only then renamed into place.
- Existing destination files are never overwritten. Name clashes get ` (1)`, ` (2)`, … suffixes.
- Reads from the iPhone are strictly one at a time. Testing showed that a device stream left open causes the driver to report *busy*, and after such failures it could deliver one file's bytes under another file's name. The device layer releases every stream deterministically, and the delivered byte count is always checked against the size the phone reports.

---

## Troubleshooting

| Symptom | What to do |
|---|---|
| "No iPhone detected" | Use a data-capable USB cable, try another port, unlock the phone. Check Device Manager → *Portable Devices* for "Apple iPhone". |
| "Unlock your iPhone" doesn't go away | Unlock the phone and tap **Trust**. If you tapped *Don't Trust* before, reset it on the iPhone: Settings → General → Transfer or Reset iPhone → Reset → **Reset Location & Privacy**, then reconnect. |
| "Can't open your iPhone" | Unplug and reconnect. Close other apps that may be importing from the phone (Photos, File Explorer windows on the phone). Install the **Apple Devices** app from the Microsoft Store to update the driver. |
| Fewer photos than on the phone | Items stored only in iCloud aren't available over USB (see *Known limitations*). |
| Anything else | Settings → **Open logs folder**, and include the latest log when reporting the problem. |

---

## Roadmap

1. ✅ Solution, WinUI shell, MVVM, DI, logging, navigation, settings
2. ✅ iPhone detection and media enumeration (verified on a real iPhone)
3. Destination folder scanner
4. SQLite media index with migrations
5. SHA-256 exact duplicate detection
6. Transfer engine
7. Transfer verification and crash recovery
8. Perceptual hashing
9. Thumbnails and media review UI
10. History, filtering, accessibility polish, MSIX packaging

---

Copyright © 2026 Prasun Dangol.
