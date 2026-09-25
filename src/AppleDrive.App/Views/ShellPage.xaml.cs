using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace AppleDrive.App.Views;

/// <summary>Navigation shell. Maps navigation items to pages; holds no application logic.</summary>
public sealed partial class ShellPage : Page
{
    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["Dashboard"] = typeof(DashboardPage),
        ["Import"] = typeof(ImportPage),
        ["History"] = typeof(HistoryPage),
    };

    public ShellPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (Navigation.SelectedItem is null)
            {
                Navigation.SelectedItem = Navigation.MenuItems[0];
            }
        };
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var pageType = args.IsSettingsSelected
            ? typeof(SettingsPage)
            : args.SelectedItemContainer?.Tag is string tag && Pages.TryGetValue(tag, out var type) ? type : null;

        if (pageType is not null && ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo());
        }
    }
}
