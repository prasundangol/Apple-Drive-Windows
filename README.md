# Apple Drive

Apple Drive is a Windows desktop app that copies photos and videos from an iPhone connected by USB to a folder on your PC or an external drive. It detects files you already have, so duplicates aren't copied again.

Apple Drive never deletes or changes anything on your iPhone, never overwrites files on your PC, and never sends your photos anywhere. Everything runs locally and offline.

> **Status:** early development. Detecting an iPhone, reading its media, destination indexing, exact and visually-similar duplicate detection, and verified transfers with crash recovery, and a review screen with previews work. History, polish and packaging are next. See [Roadmap](#roadmap).

---

## Requirements

| | |
|---|---|
| Windows | Windows 10 version 2004 (build 19041) or later, Windows 11 recommended. x64 or ARM64. |
| .NET SDK | .NET 10 SDK (10.0.400 or later). Pinned in `global.json`. |
| IDE (optional) | Visual Studio 2022 17.14+ or Visual Studio 2026 with the **.NET desktop development** workload and **Windows App SDK C# templates**. Building from the command line needs only the SDK. |
| Windows App SDK | Restored from NuGet (`Microsoft.WindowsAppSDK`). The app is self-contained, so no separate runtime install is needed. |
| Image codecs | Built into Windows for JPEG, PNG, GIF, TIFF, BMP and (Windows 11) WebP. For HEIC photos, the free **HEIF Image Extensions** and **HEVC Video Extensions** from the Microsoft Store are needed to compare them visually; without them HEIC files are still copied and exact-duplicate checked, and the Import page says how many couldn't be compared. |

### NuGet packages

| Package | Purpose |
|---|---|
| Microsoft.WindowsAppSDK, Microsoft.Windows.SDK.BuildTools | WinUI 3 UI |
| CommunityToolkit.Mvvm | MVVM source generators (observable properties, commands) |
| Microsoft.Extensions.DependencyInjection / Logging | Dependency injection and logging abstractions |
| Serilog, Serilog.Extensions.Logging, Serilog.Sinks.File | Rolling diagnostic log files |
| Microsoft.Data.Sqlite, Dapper | Local media index and transfer history |
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

`tools/AppleDrive.DeviceProbe` is a console tool that checks the whole iPhone path without the UI. It lists devices, connects, enumerates media, and reads a few files into memory to verify size, content type, and throughput. By default it writes nothing to disk, and it never changes anything on the phone.

```powershell
dotnet run --project tools/AppleDrive.DeviceProbe -- --read 10
```

More checks:

| Option | What it does |
|---|---|
| `--copy-to <folder>` | Also saves the files it reads (never overwriting), for making test fixtures. |
| `--reconnect-check <n>` | Reads *n* files, reconnects, and reads them again by the same object id, confirming the content is identical. The transfer engine relies on this when it re-reads a suspicious file. |
| `--thumbnails <n>` | Compares the visual fingerprint of each image's phone thumbnail (in all 8 orientations) with that of the full file, and reports the distances and timing. |
| `--visual-check <folder>` | Runs the visual duplicate check with every phone image treated as new, against a folder that holds copies of them (use `--transfer` first). Each image should find its own copy; matches to other files are listed. |
| `--orientation-check <n>` | For *n* images, compares the EXIF orientation with the orientation in which the phone's thumbnail matches the upright photo, then re-reads every file to confirm the content is stable, and reports the video thumbnails. |
| `--early-close-check` | Reads 64 KB of a file, closes it, then reads another file in full, several times, and checks that the content is correct. Without the read-to-end safeguard, this fails on a real iPhone. |
| `--preview <names>` | Makes previews of the named phone files with the real preview service, into a throwaway cache, and reports size and time. |
| `--transfer <folder> [--organize flat\|month\|day] [--limit <items>]` | Runs the real duplicate check and transfer engine into `<folder>`, with a throwaway index database (the app's own index is untouched), then reports what was copied, the speed, and any leftovers. |

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
- **Files are always read to the end:** closing a file stream before its end leaves the iPhone in a bad state. Testing on a real iPhone showed that the next file fails to open (`0x8007001E`), then fails again even after reconnecting (`0x80042007`), and that a later read once returned another file's content. A stream that is closed early (a cancelled copy, a preview that scrolled away) therefore reads and discards the rest of the file first. With that in place, the same test gave no failures in three runs. Cancelling a transfer during a large video can take a few seconds longer because of this.
- **Thumbnails on the phone** are stored as the sensor saw them: portrait photos sideways, front-camera shots mirrored, and without an orientation tag. For some saved or edited JPEGs they show a different version of the picture. See [Reviewing files](#reviewing-files) for how previews handle this.

---

## Architecture

```
src/
  AppleDrive.Domain          Entities, enums, value types and pure domain logic (no dependencies)
  AppleDrive.Application     Use-case services and the interfaces infrastructure must implement
  AppleDrive.Infrastructure  WPD iPhone access, SQLite, file system, hashing, media metadata, settings
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

Apple Drive runs as a **single instance** (Windows App SDK `AppInstance` redirection, in `Program.cs`): launching it again brings the open window forward. Two instances would compete for the phone's single stream, and one's startup recovery could clean up the other's running transfer.

Key abstractions:

| Interface | Production implementation | Purpose |
|---|---|---|
| `IPhoneDeviceService` | `WpdPhoneDeviceService` | Lists attached iPhones; `DeviceWatcher` on the WPD interface class for plug/unplug |
| `IPhonePhotoSource` | `WpdPhotoSource` | Connect, enumerate media, open read-only streams |
| `IDestinationScanner` | `DestinationScanner` | Lists media files in the destination folder |
| `IMediaRepository` | `MediaRepository` | Index of destination files (SQLite) |
| `ITransferRepository` | `TransferRepository` | Transfer history (SQLite) |
| `IHashService` | `Sha256HashService` | Streaming SHA-256 |
| `ICaptureDateReader` | `CaptureDateReader` | Capture date from EXIF (JPEG, HEIC) and QuickTime (MOV, MP4) metadata |
| `ISettingsService` | `JsonSettingsService` | Persisted user preferences |
| `IAppPaths` | `AppPaths` | Per-user data locations |

Errors that can be expected (device locked, unplugged, busy) are returned as `Result<T>` / `AppError` values with a stable `ErrorKind`, not thrown as exceptions. The UI maps `ErrorKind` to plain-language text and keeps technical details behind **Show details**.

### Data locations

All app data lives under `%LOCALAPPDATA%\AppleDrive\`:

| Path | Contents |
|---|---|
| `settings.json` | User preferences (written atomically) |
| `Thumbnails\` | Preview cache (256 px JPEGs, safe to delete; they are made again when needed) |
| `media-index.db` | Index of destination media (see [Destination scanning](#destination-scanning-and-the-media-index)) |
| `Logs\apple-drive-YYYYMMDD.log` | Diagnostic logs, 14 days retained. Settings → *Open logs folder*. |

Logs record events (connection, scan counts, errors) but never image data.

### Localization

User-facing text lives in `src/AppleDrive.Presentation/Resources/Strings.resx`, accessed through the `Strings` class. XAML binds to it with `{x:Bind res:Strings.Name}`. To add a language, add `Strings.<culture>.resx`. A unit test checks that every string has a resource value.

---

## Destination scanning and the media index

Apple Drive keeps an index of the photos and videos already in your destination folder, so it doesn't have to re-read the whole drive every time.

- **Scanning** walks the folder recursively and reads file size and timestamps straight from the directory listing (`FileSystemEnumerable`), without opening each file. Unsupported files, `*.partial` files still being copied, system folders, and junctions/symlinks are skipped.
- **Unchanged files** (same path, size and modification time) keep their stored hashes, so a re-scan of tens of thousands of files takes seconds.
- **Changed files** have their stored hashes cleared and are re-hashed only when they're needed.
- **Deleted or moved files** are marked *not available*. They are never removed by a scan. If a file comes back unchanged (for example, an external drive is reconnected), its record and hashes are reused.
- **Disconnected drives are safe:** files are marked missing only after a *complete* scan succeeds. If the drive is unplugged, the folder is missing, or the scan is cancelled, the index is left as it was.

### Database

SQLite, at `%LOCALAPPDATA%\AppleDrive\media-index.db`, in WAL mode, accessed with Microsoft.Data.Sqlite and Dapper.

`MediaFiles`: `Id`, `FullPath` (unique, case-insensitive), `MediaType`, `FileSize`, `CreatedAt`, `ModifiedAt`, `Width`, `Height`, `CaptureDate`, `Sha256` (32-byte BLOB, computed lazily), `PerceptualHash` (64-bit), `FirstSeenAt`, `LastScannedAt`, `IsAvailable`. Times are UTC Unix milliseconds.

Indexes, each backed by a query-plan test:

| Index | Used for |
|---|---|
| `UX_MediaFiles_FullPath` | Path lookups, upserts, and folder-prefix range scans |
| `IX_MediaFiles_FileSize` (partial, available only) | Finding exact-duplicate candidates by size |
| `IX_MediaFiles_Sha256` (partial, hashed only) | Confirming duplicates and detecting moved files |

There is deliberately no index on media type, capture date, or perceptual hash. Those aren't queried against the destination table: perceptual matching is a Hamming-distance search done in memory.

Transfer history (schema 2):

- `TransferSessions`: one row per run. Device name, destination, start and end times, status (running, completed, cancelled, stopped) and totals.
- `Transfers`: one row per file. Source object and persistent ids, name, reported and delivered size, the temporary `.partial` path, final destination path, SHA-256, status (in progress, completed, failed, cancelled, duplicate), and the error kind and message for failures.

The `.partial` path is recorded *before* the file is created, so a transfer interrupted by a crash can always be found and cleaned up. `IX_Transfers_SessionId_Status` serves a run's summary and failure list; the partial index `IX_Transfers_InProgress` finds unfinished transfers at startup. Schema 3 adds `IX_Transfers_Completed_DestinationPath` (completed transfers into a folder, for the faster re-check) and `IX_TransferSessions_Running` (sessions left running by a crash). Just before a file is renamed into place, its final path and SHA-256 are recorded too.

**Migrations:** the schema version is stored in `PRAGMA user_version`. Migrations in `Infrastructure/Database/Migrations/Migrations.cs` are forward-only, each runs in its own transaction, and an existing database is backed up to `media-index.db.v<N>.bak` before an upgrade. A database created by a newer app version is refused and left untouched. To change the schema, append a new migration; never edit a released one.

## Duplicate detection

**Import → Check for new photos** scans the iPhone, updates the destination index, then classifies every item. Nothing is copied until you confirm.

1. **Size pre-filter.** Only a destination file with exactly the same byte size can be identical. A phone file whose size matches nothing in the destination is **new** and is never read. On a typical phone that rules out almost everything instantly.
2. **SHA-256 confirmation.** When sizes match, the phone file is read and hashed, and so is each same-size destination file. Destination hashes are stored in the index and reused while the file is unchanged, so they're computed only once. A file is an **exact duplicate** only when the digests are equal. A matching name, size or date alone never makes a duplicate.
3. **Safety rules:**
   - A destination file that changed after it was indexed is not trusted as a match.
   - A phone file that can't be read to compare is treated as **new**, and the summary says how many.
   - If the phone reports a wrong size (on-the-fly HEIC→JPEG conversion), the file is treated as **new**, never as a duplicate. The transfer engine re-checks by hash after copying (see [Transfer safety](#transfer-safety)).
   - If the phone disconnects mid-check, the check stops with an error.
4. **Live Photos** count as already imported only when *both* the image and the video exist. If only one does, the item is new and only the missing part is copied.
5. **Moved files:** when a hash matches a record that is no longer found at its old path, the stale record is removed.
6. **Files Apple Drive already transferred** are recognised from transfer history without reading the phone again, when the phone file has the same persistent id, name and size as that transfer, and the copy is still in the index, unchanged since (same size and modification time, same SHA-256). The proof is the SHA-256 verified during that transfer. If the copy was edited, moved or deleted, the phone file is read and compared as usual.
7. **Visually similar images: possible duplicates.** A new phone image that *looks* like a destination image (resized, recompressed, converted from HEIC to JPEG, renamed, lightly edited) is flagged as a **possible duplicate**. It is shown to you and **never skipped unless you choose to**. Videos are not compared visually.

### How the visual check works

Perceptual *difference hashes* (dHash) are computed through the Windows Imaging Component decoders (JPEG, PNG, GIF, TIFF, BMP, WebP and, with the HEIF/HEVC extensions, HEIC). The image is scaled to a tiny grey grid and each bit records whether a pixel is brighter than its right-hand neighbour. So resizing, recompression and format changes barely change the hash, while different pictures differ in about half the bits.

1. **Destination:** every image gets a 64-bit hash, computed once (four at a time) and stored in the index (`MediaFiles.PerceptualHash`). A changed file gets a new one.
2. **Phone, first pass:** only images still classified as new are checked. The 64-bit hash comes from the small **thumbnail** the iPhone keeps (5–10 KB), not the whole file. Testing on a real iPhone showed that:
   - thumbnails are stored as the sensor saw them, so portrait photos are sideways and front-camera shots are mirrored. The thumbnail is therefore hashed in all 8 orientations and the closest one counts.
   - for saved or edited **JPEGs** the phone often keeps a thumbnail of a *different* version (31 of 110 JPEGs on the test phone). So JPEGs, which are small, are read in full instead.
3. **Candidates:** destination images within 10 of 64 bits.
4. **Confirmation:** each candidate is re-checked with a **256-bit** hash of both full images (the phone file is read only now), and must be within 36 of 256 bits. This second, much more selective stage rules out chance matches between unrelated photos in a large library.

Thresholds, from test fixtures (resized, recompressed, PNG↔JPEG and slightly edited copies vs. 1,770 pairs of different pictures) and from a real iPhone:

| | 64-bit distance | 256-bit distance |
|---|---|---|
| Same picture: resized, recompressed, converted, slightly edited | 0–4 | 0–22 |
| Phone thumbnail vs its own full photo (real iPhone, 301 non-JPEG images) | 0–7 | — |
| Different pictures (closest pair of 1,770) | 11 | 44 |
| **Thresholds** | **≤ 10** | **≤ 36** |

**Tested on a real iPhone:**

- **Self-check:** with the destination holding copies of all 332 phone images and every image treated as new, all 332 found a match. The 8 that matched a different file were visually identical screenshots at distance 0, so there were no false positives across about 110,000 photo pairs.
- **HEIC vs JPEG:** with 10 HEIC photos converted to half-size JPEGs under new names, all 10 were flagged (256-bit distance 0–3), plus the phone's 2 *edited* versions of those photos (`IMG_E…`, distance 4 and 14).
- **Cost:** the check took 26 s in the app, reading 222 thumbnails and 121 full files.

In the confirmation, **Also copy the N possible duplicates, keeping both versions** is ticked by default, because keeping both never loses a photo. Untick it to leave them on the phone. Reviewing them one by one, with side-by-side previews, comes with the media review screen.

Tested on a real iPhone (441 items, 477 files) against a folder of 12 files copied from it, one renamed and one with a single byte changed. The check read only the 12 phone files that shared a size with a destination file and finished in under a second. The renamed copy was matched and the altered file was classified as new.

## Reviewing files

**Review files** on the Import page opens a grid of everything on the iPhone with a preview, name, type, size, date and status. For duplicates, the file already in the destination is shown next to it, so possible duplicates can be compared side by side.

- **Choose what to copy:** new items and possible duplicates are ticked; untick anything to leave it on the phone. For a possible duplicate the box reads **Keep both**. Exact duplicates are never copied again, so they have no box. **Select all** and **Select none** act on what is shown, **Only new** leaves out every possible duplicate, and **Copy possible duplicates** switches them all on or off. The confirmation and the transfer use this selection.
- **Filter** by status (new, possible duplicates, already in destination), type (photos, videos) and year; **search** by file name; **sort** by date (newest or oldest first), name, size or status.
- The date comes from the phone, or the month of its camera-roll folder when the phone reports none. The date read from the file itself is known only after copying.
- The grid is virtualized, so only visible items exist. Their previews load as they scroll into view, and loading stops when they scroll away.

### Previews

Previews are 256 px JPEGs, generated asynchronously on first display and cached on disk: in `%LOCALAPPDATA%\AppleDrive\Thumbnails`, or the folder chosen in Settings. Each is decoded only once, and requests for the same preview share one generation.

| Item | Preview from |
|---|---|
| iPhone HEIC and JPEG photos | The file itself, decoded upright (EXIF orientation applied). The phone's own thumbnail isn't used because it is unrotated, sometimes mirrored, and for some JPEGs shows another version. Reading a 1–3 MB photo takes about half a second. |
| iPhone PNG, WebP and GIF images, and videos | The small thumbnail the phone keeps (4–10 KB); these are never rotated. A video is never read just for a preview. |
| Destination files, photos and videos | The Windows shell thumbnail, so Windows' own thumbnail cache is reused. |

Phone reads for previews are one at a time, like every phone read, and always to the end of the file.

## Transferring

After **Check for new photos**, **Start transfer** shows a confirmation with the number of new items, the duplicates that will be skipped, the total size, the destination and the folder layout. Nothing starts until you confirm. While it runs, the page shows items done, the current file, bytes copied, speed, and the time remaining (only once the speed has settled). It also shows running counts of files transferred, skipped and failed. **Cancel** is always available.

The summary shows what was transferred, skipped and failed, and how much was copied. From it you can **Open folder**, view the failed files with the reason for each, and **Retry failed**, which copies only what is still missing. If the transfer was cancelled or stopped early, the same button reads **Copy remaining**.

### Folder layout and names

- **Organize imported files** (Settings): no subfolders, `2026\09 September`, or `2026\09 September\25`. Month folders are always named in English, so the layout doesn't change with the Windows display language.
- The date is **when the photo or video was taken**, read from the copied file: EXIF `DateTimeOriginal` for JPEG and HEIC, and the Apple creation date (else the movie header time) for MOV and MP4. The folder uses the local time *where it was taken*, so a photo taken at 23:30 on 31 March goes in March even when that was already April in UTC. When a file has no date (screenshots often don't), the date the phone reports is used. Failing that, the month of its camera-roll folder on the phone is used (`202504__` is April 2025); with day folders, such a file goes in the month folder rather than an invented day. Only when none of these exists is the transfer date used.
- Copied files get their capture date as their *modified* date, so they sort correctly in File Explorer.
- **Live Photos** keep both parts together with the same name, in the folder of the image's date. If the image is already in the destination and only the video is missing, the video is placed next to the existing image with the same name.
- **Name clashes** never overwrite: `IMG_1234.HEIC` becomes `IMG_1234 (1).HEIC`, then `(2)`, and so on. Both parts of a Live Photo get the same number. Names are reserved in memory while a transfer is running, so two files can never be given the same name.

## Transfer safety

1. **Nothing on the iPhone is ever modified or deleted.** The device layer declares only read operations.
2. **Copy to a temporary file.** Each file is streamed from the phone into `<name>.<id>.partial` in the destination folder, and hashed with SHA-256 as it is copied. The disk write of one chunk overlaps the device read of the next. The temporary file is flushed to the disk itself (`FlushFileBuffers`) before it is closed.
3. **Verify.** The temporary file must exist and have exactly the number of bytes read, and it is read back and hashed again. The hash must match the one computed while copying. When the duplicate check already hashed the file, that hash must match too.
4. **Check what the phone delivered.** A delivered size different from the size the phone reports, or content different from the duplicate check, is suspicious. Testing showed that after certain errors the driver could deliver one file's bytes under another file's name. The file is read again after reconnecting to the phone, and it is accepted only if the second read is consistent:
   - it matches the reported size, or
   - it is identical to the first read and is a genuine on-the-fly HEIC→JPEG conversion (JPEG content from a `.HEIC` file), in which case it is saved as `.JPG`.
5. **Re-check for duplicates.** If the delivered content turns out to already be in the destination (possible when the reported size was wrong), the copy is discarded and counted as skipped.
6. **Rename into place**, never overwriting. Only then is the file added to the index (with its SHA-256) and recorded as transferred. A file that is not verified is never reported as imported.
7. **One file at a time from the phone.** Only one device stream is ever open. Verifying and naming a copied file happens while the next file is being read, with at most two copied files waiting.

**Failures:**

| Situation | What happens |
|---|---|
| A read error (device busy or I/O error) | The file is read again once, after reconnecting. If it still fails, it is marked failed and the transfer continues. |
| iPhone unplugged, locked or untrusted | The transfer stops. Files already copied and verified are kept; the rest are listed as not copied. |
| Destination drive unplugged, full, read-only or not permitted | The transfer stops; nothing more is attempted. |
| Verification fails | The copy is discarded and the file is marked failed. |
| **Cancel** | The file being copied is abandoned. Nothing unverified is kept, and every history record is closed. |
| App closed during a transfer | The transfer is cancelled and cleaned up the same way (up to 10 seconds). |
| App killed, crash or power loss | At the next start, before anything else, unfinished transfers are resolved: a file that had already been renamed into place is checked against its recorded SHA-256 and kept; otherwise its recorded `.partial` file (and only that, recognised by path and name pattern) is deleted. If the destination drive isn't connected, this waits until a later start. |

In every case the only files deleted are the transfer's own `.partial` files. A write failure that affects only one file fails just that file.

**Resuming:** everything already transferred is in the index and in transfer history, so **Check for new photos** after an interruption shows only what is still missing, without reading the already-copied files from the phone again. Nothing is copied twice.

**Tested on a real iPhone** (441 items, 477 files, 4.3 GB) through the app: the transfer was cancelled after 60 items (no `.partial` file left), then **Copy remaining** finished the other 381 in about two minutes (roughly 30 MB/s), with 0 failures. Two photos that exist twice on the phone were copied once. Reading all 477 files from the phone again and comparing them by SHA-256 found every one identical in the destination. Month folders matched Windows' own *Date taken* / *Media created* for every file that has one, and object ids were confirmed to survive a reconnect.

Crash recovery was also tested for real: the app was killed 8 seconds into a transfer, leaving 51 finished files and one half-written `.partial` video. At the next start, the `.partial` was removed and nothing else was touched. **Check for new photos** then recognised all 51 copied items from transfer history without reading the phone, and listed the remaining 390 as new.

Tested against a simulated iPhone for: successful copies, read failures and retries, a disconnected phone, a missing or read-only destination, cancellation, verification (hash) mismatch, wrong content and wrong sizes from the phone, HEIC→JPEG conversion, name conflicts (`IMG.jpg` → `IMG (1).jpg` → `IMG (2).jpg`), Live Photo pairing and retries, folder organization by capture date, and progress reporting.

---

## Troubleshooting

| Symptom | What to do |
|---|---|
| "No iPhone detected" | Use a data-capable USB cable, try another port, unlock the phone. Check Device Manager → *Portable Devices* for "Apple iPhone". |
| "Unlock your iPhone" doesn't go away | Unlock the phone and tap **Trust**. If you tapped *Don't Trust* before, reset it on the iPhone: Settings → General → Transfer or Reset iPhone → Reset → **Reset Location & Privacy**, then reconnect. |
| "Can't open your iPhone" | Unplug and reconnect. Close other apps that may be importing from the phone (Photos, File Explorer windows on the phone). Install the **Apple Devices** app from the Microsoft Store to update the driver. |
| Fewer photos than on the phone | Items stored only in iCloud aren't available over USB (see *Known limitations*). |
| "The copy didn't match what your iPhone sent" | Retry. If it keeps happening, unplug and reconnect the iPhone. On the iPhone, set Settings → Apps → Photos → Transfer to Mac or PC → **Keep Originals**. |
| "The destination drive is full" | Free up space or choose another folder, then use **Copy remaining**. Everything copied so far is kept. |
| `*.partial` files in the destination | Left by a transfer that was interrupted (the app was killed or the PC lost power). Apple Drive removes its own at the next start; if the destination drive wasn't connected then, at a later start. They are incomplete copies and are safe to delete by hand. |
| Anything else | Settings → **Open logs folder**, and include the latest log when reporting the problem. |

---

## Roadmap

1. ✅ Solution, WinUI shell, MVVM, DI, logging, navigation, settings
2. ✅ iPhone detection and media enumeration (verified on a real iPhone)
3. ✅ Destination folder scanner
4. ✅ SQLite media index with migrations
5. ✅ SHA-256 exact duplicate detection
6. ✅ Transfer engine: verified copies, folder organization by capture date, conflict-free names, progress, cancel, retry
7. ✅ Crash recovery, faster re-check from transfer history, single instance
8. ✅ Perceptual hashing: possible duplicates, shown and never skipped silently
9. ✅ Thumbnails and media review screen: previews, per-item Keep both / Skip, filters, sorting, search
10. History, filtering, accessibility polish, MSIX packaging

---

Copyright © 2026 Prasun Dangol.
