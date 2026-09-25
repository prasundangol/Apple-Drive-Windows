namespace AppleDrive.Application.Interfaces;

/// <summary>Per-user locations where the app keeps its own data.</summary>
public interface IAppPaths
{
    /// <summary>Root folder for all app data.</summary>
    string DataFolder { get; }

    string SettingsFile { get; }

    string LogsFolder { get; }

    string DatabaseFile { get; }

    string DefaultThumbnailCacheFolder { get; }
}
