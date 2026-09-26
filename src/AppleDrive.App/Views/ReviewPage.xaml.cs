using System.Runtime.CompilerServices;
using AppleDrive.Presentation.Resources;
using AppleDrive.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppleDrive.App.Views;

/// <summary>Review screen. Starts loading an item's previews when it scrolls into view and cancels when it leaves.</summary>
public sealed partial class ReviewPage : Page
{
    private readonly ConditionalWeakTable<UIElement, CancellationTokenSource> _loading = [];

    public ReviewPage()
    {
        ViewModel = App.Services.GetRequiredService<ReviewViewModel>();
        InitializeComponent();
    }

    public ReviewViewModel ViewModel { get; }

    // Same order as ReviewStatusFilter, ReviewTypeFilter and ReviewSort.
    public IReadOnlyList<string> StatusOptions { get; } =
        [Strings.FilterStatusAll, Strings.FilterStatusNew, Strings.FilterStatusPossible, Strings.FilterStatusExisting];

    public IReadOnlyList<string> TypeOptions { get; } = [Strings.FilterTypeAll, Strings.FilterTypePhotos, Strings.FilterTypeVideos];

    public IReadOnlyList<string> SortOptions { get; } =
        [Strings.SortNewest, Strings.SortOldest, Strings.SortName, Strings.SortSize, Strings.SortStatus];

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        // The element's DataContext is not reliably set yet at this point; the items view is.
        if (sender.ItemsSourceView?.GetAt(args.Index) is ReviewItemViewModel item)
        {
            var cancellation = new CancellationTokenSource();
            _loading.AddOrUpdate(args.Element, cancellation);
            _ = LoadAsync(item, cancellation.Token);
        }
    }

    private static async Task LoadAsync(ReviewItemViewModel item, CancellationToken cancellationToken)
    {
        try
        {
            await item.LoadThumbnailsAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // A preview is never worth crashing for, but it is worth knowing why it failed.
            Serilog.Log.Warning(exception, "Could not load a preview");
        }
    }

    private void OnElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (_loading.TryGetValue(args.Element, out var cancellation))
        {
            cancellation.Cancel();
            cancellation.Dispose();
            _loading.Remove(args.Element);
        }
    }
}
