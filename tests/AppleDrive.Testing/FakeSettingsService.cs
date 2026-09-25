using AppleDrive.Application.Settings;

namespace AppleDrive.Testing;

public sealed class FakeSettingsService(AppSettings? initial = null) : ISettingsService
{
    public AppSettings Current { get; private set; } = initial ?? new AppSettings();

    public event EventHandler<AppSettings>? SettingsChanged;

    public Task UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken = default)
    {
        Current = update(Current);
        SettingsChanged?.Invoke(this, Current);
        return Task.CompletedTask;
    }
}
