using AppleDrive.Application.Interfaces;

namespace AppleDrive.Infrastructure.FileSystem;

/// <summary>
/// App data under <c>%LOCALAPPDATA%\AppleDrive</c> (per-user, not roamed: the media index and
/// thumbnail cache are machine-specific).
/// </summary>
public sealed class AppPaths : IAppPaths
{
    public AppPaths()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppleDrive"))
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
}
