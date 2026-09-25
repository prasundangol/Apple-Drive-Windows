using System.Text.Json;
using System.Text.Json.Serialization;
using AppleDrive.Application.Interfaces;
using AppleDrive.Application.Settings;
using Microsoft.Extensions.Logging;

namespace AppleDrive.Infrastructure.Settings;

/// <summary>
/// Stores settings as JSON in the app data folder. Writes go to a temporary file that
/// then replaces the original, so a crash never leaves a half-written settings file.
/// </summary>
public sealed class JsonSettingsService : ISettingsService
{
    private readonly string _path;
    private readonly ILogger<JsonSettingsService> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonSettingsService(IAppPaths paths, ILogger<JsonSettingsService> logger)
    {
        _path = paths.SettingsFile;
        _logger = logger;
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public event EventHandler<AppSettings>? SettingsChanged;

    public async Task UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        AppSettings updated;
        try
        {
            updated = update(Current) with { Version = AppSettings.CurrentVersion };
            var temporaryPath = _path + ".tmp";
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, updated, SettingsJsonContext.Default.AppSettings, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _path, overwrite: true);
            Current = updated;
        }
        finally
        {
            _writeLock.Release();
        }

        SettingsChanged?.Invoke(this, updated);
    }

    private AppSettings Load()
    {
        if (!File.Exists(_path))
        {
            return new AppSettings();
        }

        try
        {
            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // Keep the unreadable file for diagnosis instead of overwriting it silently.
            _logger.LogWarning(exception, "Settings file could not be read; using defaults");
            TryPreserveCorruptFile();
            return new AppSettings();
        }
    }

    private void TryPreserveCorruptFile()
    {
        try
        {
            File.Copy(_path, _path + ".unreadable", overwrite: true);
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "Could not preserve the unreadable settings file");
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
