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
        Current = this;
        Loaded += (_, _) =>
        {
            if (Navigation.SelectedItem is null)
            {
                Navigation.SelectedItem = Navigation.MenuItems[0];
            }
        };
    }

    /// <summary>The shell currently shown, for pages that link to another section.</summary>
    public static ShellPage? Current { get; private set; }

    /// <summary>Shows a section as if its navigation item had been clicked.</summary>
    public void SelectSection(string tag) =>
        Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(item => item.Tag as string == tag);

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
