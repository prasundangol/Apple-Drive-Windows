using AppleDrive.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace AppleDrive.App.Views;

public sealed partial class ImportPage : Microsoft.UI.Xaml.Controls.Page
{
    public ImportPage()
    {
        ViewModel = App.Services.GetRequiredService<ImportViewModel>();
        InitializeComponent();
    }

    public ImportViewModel ViewModel { get; }

    /// <summary>x:Bind helper: shows an element only when it has text to display.</summary>
    public Visibility IsNotEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    private void OnReviewFiles(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(ReviewPage), null, new Microsoft.UI.Xaml.Media.Animation.DrillInNavigationTransitionInfo());

    /// <summary>x:Bind helper: inverse of a boolean visibility.</summary>
    public Visibility IsFalse(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
}
