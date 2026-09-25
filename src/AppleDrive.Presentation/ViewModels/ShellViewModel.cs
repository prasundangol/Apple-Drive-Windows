using AppleDrive.Application.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppleDrive.Presentation.ViewModels;

/// <summary>Main window: first-run welcome versus the navigation shell.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly ISettingsService _settings;

    public ShellViewModel(ISettingsService settings)
    {
        _settings = settings;
        ShowWelcome = !settings.Current.HasCompletedOnboarding;
    }

    [ObservableProperty]
    public partial bool ShowWelcome { get; private set; }

    [RelayCommand]
    private async Task GetStartedAsync()
    {
        ShowWelcome = false;
        await _settings.UpdateAsync(current => current with { HasCompletedOnboarding = true });
    }
}
