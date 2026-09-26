using System.Globalization;
using AppleDrive.Application.Interfaces;
using AppleDrive.Application.Services;
using AppleDrive.Domain.Enums;
using AppleDrive.Presentation.Formatting;
using AppleDrive.Presentation.Resources;
using AppleDrive.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppleDrive.Presentation.ViewModels;

public enum ReviewStatusFilter
{
    All,
    New,
    PossibleDuplicates,
    AlreadyInDestination,
}

public enum ReviewTypeFilter
{
    All,
    Photos,
    Videos,
}

public enum ReviewSort
{
    NewestFirst,
    OldestFirst,
    Name,
    Size,
    Status,
}

/// <summary>
/// The review screen: every item of the import plan with its preview, filters, sorting, search,
/// and choosing what to copy. Selection lives in the <see cref="ImportSession"/>.
/// </summary>
public sealed partial class ReviewViewModel : ObservableObject
{
    private readonly ImportSession _session;
    private readonly IThumbnailService _thumbnails;
    private List<ReviewItemViewModel> _all = [];
    private bool _applyingSelection;

    public ReviewViewModel(ImportSession session, IThumbnailService thumbnails, IUiDispatcher dispatcher)
    {
        _session = session;
        _thumbnails = thumbnails;
        session.Changed += (_, _) => dispatcher.Post(Rebuild);
        session.SelectionChanged += (_, _) => dispatcher.Post(OnSelectionChanged);
        Rebuild();
    }

    /// <summary>The items passing the filters, in the chosen order.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVisibleItems), nameof(HasNoVisibleItems))]
    public partial IReadOnlyList<ReviewItemViewModel> VisibleItems { get; private set; } = [];

    public bool HasVisibleItems => VisibleItems.Count > 0;

    public bool HasNoVisibleItems => HasPlan && VisibleItems.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoVisibleItems))]
    public partial bool HasPlan { get; private set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int StatusFilterIndex { get; set; }

    [ObservableProperty]
    public partial int TypeFilterIndex { get; set; }

    [ObservableProperty]
    public partial int SortIndex { get; set; }

    /// <summary>"All years" followed by each year that has items.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> YearOptions { get; private set; } = [];

    [ObservableProperty]
    public partial int YearIndex { get; set; }

    [ObservableProperty]
    public partial string SelectionSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ShownSummary { get; private set; } = string.Empty;

    /// <summary>Whether possible duplicates are copied; mixed selections show as unchecked.</summary>
    [ObservableProperty]
    public partial bool IncludePossibleDuplicates { get; set; } = true;

    public bool HasPossibleDuplicates => _all.Any(item => item.IsPossibleDuplicate);

    partial void OnSearchTextChanged(string value) => ApplyFilters();

    partial void OnStatusFilterIndexChanged(int value) => ApplyFilters();

    partial void OnTypeFilterIndexChanged(int value) => ApplyFilters();

    partial void OnSortIndexChanged(int value) => ApplyFilters();

    partial void OnYearIndexChanged(int value) => ApplyFilters();

    partial void OnIncludePossibleDuplicatesChanged(bool value)
    {
        if (!_applyingSelection)
        {
            _session.SetSelected(_all.Where(item => item.IsPossibleDuplicate).Select(item => item.Item), value);
        }
    }

    /// <summary>Selects every shown item that can be copied.</summary>
    [RelayCommand]
    private void SelectAll() => _session.SetSelected(VisibleItems.Select(item => item.Item), true);

    /// <summary>Deselects every shown item.</summary>
    [RelayCommand]
    private void SelectNone() => _session.SetSelected(VisibleItems.Select(item => item.Item), false);

    /// <summary>Selects the new items and leaves out possible duplicates, across the whole plan.</summary>
    [RelayCommand]
    private void SelectOnlyNew()
    {
        _session.SetSelected(_all.Where(item => item.Status == AssetStatus.New).Select(item => item.Item), true);
        _session.SetSelected(_all.Where(item => item.IsPossibleDuplicate).Select(item => item.Item), false);
    }

    private void Rebuild()
    {
        var plan = _session.Plan;
        HasPlan = plan is not null;
        _all = plan?.Items.Select(item => new ReviewItemViewModel(item, _session, _thumbnails)).ToList() ?? [];
        YearOptions =
        [
            Strings.AllYears,
            .. _all.Where(item => item.Date is not null).Select(item => item.Date!.Value.Year).Distinct().OrderDescending()
                .Select(year => year.ToString(CultureInfo.CurrentCulture)),
        ];
        YearIndex = 0;
        OnPropertyChanged(nameof(HasPossibleDuplicates));
        ApplyFilters();
        OnSelectionChanged();
    }

    private void ApplyFilters()
    {
        IEnumerable<ReviewItemViewModel> items = _all;
        items = (ReviewStatusFilter)StatusFilterIndex switch
        {
            ReviewStatusFilter.New => items.Where(item => item.Status == AssetStatus.New),
            ReviewStatusFilter.PossibleDuplicates => items.Where(item => item.IsPossibleDuplicate),
            ReviewStatusFilter.AlreadyInDestination => items.Where(item => item.IsExactDuplicate),
            _ => items,
        };
        items = (ReviewTypeFilter)TypeFilterIndex switch
        {
            ReviewTypeFilter.Photos => items.Where(item => !item.IsVideo),
            ReviewTypeFilter.Videos => items.Where(item => item.IsVideo),
            _ => items,
        };
        if (YearIndex > 0 && YearIndex < YearOptions.Count && int.TryParse(YearOptions[YearIndex], NumberStyles.Integer, CultureInfo.CurrentCulture, out var year))
        {
            items = items.Where(item => item.Date?.Year == year);
        }

        if (SearchText.Trim() is { Length: > 0 } search)
        {
            items = items.Where(item => item.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        items = (ReviewSort)SortIndex switch
        {
            ReviewSort.OldestFirst => items.OrderBy(item => item.Date ?? DateTime.MaxValue).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            ReviewSort.Name => items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            ReviewSort.Size => items.OrderByDescending(item => item.TotalBytes),
            ReviewSort.Status => items.OrderBy(item => StatusOrder(item.Status)).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            _ => items.OrderByDescending(item => item.Date ?? DateTime.MinValue).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
        };

        VisibleItems = items.ToList();
        ShownSummary = Strings.Format(Strings.ShownFormat, VisibleItems.Count, _all.Count);
    }

    private void OnSelectionChanged()
    {
        foreach (var item in _all)
        {
            item.RefreshSelection();
        }

        var selected = _all.Where(item => item.IsSelectable && item.IsSelected).ToList();
        SelectionSummary = Strings.Format(Strings.SelectedFormat, selected.Count, _all.Count(item => item.IsSelectable), ByteSize.Format(selected.Sum(item => item.TotalBytes)));

        _applyingSelection = true;
        IncludePossibleDuplicates = _all.Where(item => item.IsPossibleDuplicate).All(item => item.IsSelected);
        _applyingSelection = false;
    }

    /// <summary>Possible duplicates first: they are the ones that need a decision.</summary>
    private static int StatusOrder(AssetStatus status) => status switch
    {
        AssetStatus.PossibleDuplicate => 0,
        AssetStatus.New => 1,
        _ => 2,
    };
}
