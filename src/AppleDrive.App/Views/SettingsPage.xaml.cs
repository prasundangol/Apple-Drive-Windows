using AppleDrive.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AppleDrive.App.Views;

public sealed partial class SettingsPage : Microsoft.UI.Xaml.Controls.Page
{
    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }
}
