namespace AppleDrive.Application.Settings;

/// <summary>Loads and persists <see cref="AppSettings"/>.</summary>
public interface ISettingsService
{
    /// <summary>The current settings. Always available; defaults are used until a file exists.</summary>
    AppSettings Current { get; }

    /// <summary>Raised after settings are changed and saved.</summary>
    event EventHandler<AppSettings>? SettingsChanged;

    /// <summary>Applies <paramref name="update"/> to the current settings and saves the result.</summary>
    Task UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken = default);
}
