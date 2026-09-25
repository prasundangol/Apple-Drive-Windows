using AppleDrive.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AppleDrive.App.Views;

public sealed partial class WelcomePage : Microsoft.UI.Xaml.Controls.Page
{
    public WelcomePage()
    {
        ViewModel = App.Services.GetRequiredService<ShellViewModel>();
        InitializeComponent();
        Loaded += (_, _) => GetStartedButton.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }

    public ShellViewModel ViewModel { get; }
}
