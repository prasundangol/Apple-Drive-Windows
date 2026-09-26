using System.Runtime.InteropServices;
using AppleDrive.Application.Interfaces;

namespace AppleDrive.Infrastructure.FileSystem;

/// <summary>
/// Where the app keeps its data (per-user, not roamed: the media index and thumbnail cache are
/// machine-specific). Unpackaged: <c>%LOCALAPPDATA%\AppleDrive</c>. Installed from the MSIX
/// package: the package's own local folder.
/// </summary>
/// <remarks>
/// A packaged desktop app may modify files that already exist in <c>%LOCALAPPDATA%</c>, but new
/// files there are redirected to the package's private storage. Sharing the unpackaged folder
/// would split one data set between two places (SQLite's <c>-wal</c> file, created each session,
/// would land somewhere other than its database), so the installed app keeps everything in its
/// package folder instead.
/// </remarks>
public sealed partial class AppPaths : IAppPaths
{
    private const int ErrorInsufficientBuffer = 122;

    public AppPaths()
        : this(DefaultDataFolder())
    {
    }

    /// <summary>Uses a custom root, for tests.</summary>
    public AppPaths(string dataFolder)
    {
        DataFolder = dataFolder;
        Directory.CreateDirectory(DataFolder);
    }

    public string DataFolder { get; }

    public string SettingsFile => Path.Combine(DataFolder, "settings.json");

    public string LogsFolder => Path.Combine(DataFolder, "Logs");

    public string DatabaseFile => Path.Combine(DataFolder, "media-index.db");

    public string DefaultThumbnailCacheFolder => Path.Combine(DataFolder, "Thumbnails");

    /// <summary>True when running from an installed MSIX package.</summary>
    public static bool IsPackaged
    {
        get
        {
            var length = 0u;
            // Asking for the name with no buffer: a packaged process answers "buffer too small".
            return GetCurrentPackageFullName(ref length, 0) == ErrorInsufficientBuffer;
        }
    }

    private static string DefaultDataFolder() => IsPackaged
        ? Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "AppleDrive")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppleDrive");

    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentPackageFullName(ref uint packageFullNameLength, nint packageFullName);
}
