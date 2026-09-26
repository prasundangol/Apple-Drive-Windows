using AppleDrive.Application.Services;
using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Enums;
using AppleDrive.Domain.Media;
using AppleDrive.Presentation.Resources;
using AppleDrive.Presentation.ViewModels;
using AppleDrive.Testing;

namespace AppleDrive.UnitTests.Presentation;

public sealed class ReviewViewModelTests
{
    private readonly ImportSession _session = new();
    private readonly FakeThumbnailService _thumbnails = new();

    [Fact]
    public void Shows_every_item_with_new_and_possible_duplicates_selected()
    {
        var viewModel = Create();

        Assert.Equal(5, viewModel.VisibleItems.Count);
        Assert.All(viewModel.VisibleItems.Where(item => item.IsSelectable), item => Assert.True(item.IsSelected));
        Assert.False(Find(viewModel, "IMG_0003.JPG").IsSelectable); // Exact duplicate.
        Assert.StartsWith("4 of 4 selected", viewModel.SelectionSummary);
        Assert.True(viewModel.IncludePossibleDuplicates);
        Assert.True(viewModel.HasPossibleDuplicates);
    }

    [Fact]
    public void Newest_first_by_default_with_undated_items_last()
    {
        var viewModel = Create();

        Assert.Equal(["IMG_0002.HEIC", "IMG_0001.HEIC", "IMG_0004.MOV", "IMG_0005.PNG", "IMG_0003.JPG"], Names(viewModel));
    }

    [Fact]
    public void Undated_items_use_their_camera_roll_month()
    {
        var viewModel = Create();

        Assert.Equal(new DateTime(2023, 4, 1), Find(viewModel, "IMG_0005.PNG").Date);
        Assert.Null(Find(viewModel, "IMG_0003.JPG").Date);
        Assert.Equal(Strings.DateUnknown, Find(viewModel, "IMG_0003.JPG").DateText);
    }

    [Theory]
    [InlineData(ReviewStatusFilter.New, new[] { "IMG_0001.HEIC", "IMG_0004.MOV", "IMG_0005.PNG" })]
    [InlineData(ReviewStatusFilter.PossibleDuplicates, new[] { "IMG_0002.HEIC" })]
    [InlineData(ReviewStatusFilter.AlreadyInDestination, new[] { "IMG_0003.JPG" })]
    public void Filters_by_status(ReviewStatusFilter filter, string[] expected)
    {
        var viewModel = Create();

        viewModel.StatusFilterIndex = (int)filter;

        Assert.Equal(expected.Order(), Names(viewModel).Order());
    }

    [Fact]
    public void Filters_by_type_year_and_name()
    {
        var viewModel = Create();

        viewModel.TypeFilterIndex = (int)ReviewTypeFilter.Videos;
        Assert.Equal(["IMG_0004.MOV"], Names(viewModel));

        viewModel.TypeFilterIndex = (int)ReviewTypeFilter.All;
        viewModel.YearIndex = viewModel.YearOptions.ToList().IndexOf("2024");
        Assert.Equal(["IMG_0002.HEIC", "IMG_0001.HEIC"], Names(viewModel));

        viewModel.YearIndex = 0;
        viewModel.SearchText = "0005";
        Assert.Equal(["IMG_0005.PNG"], Names(viewModel));
        Assert.Equal(Strings.Format(Strings.ShownFormat, 1, 5), viewModel.ShownSummary);

        viewModel.SearchText = "nothing like this";
        Assert.True(viewModel.HasNoVisibleItems);
    }

    [Fact]
    public void Year_options_list_the_years_present_newest_first()
    {
        var viewModel = Create();

        Assert.Equal([Strings.AllYears, "2024", "2023"], viewModel.YearOptions);
    }

    [Theory]
    [InlineData(ReviewSort.OldestFirst, "IMG_0005.PNG")]
    [InlineData(ReviewSort.Name, "IMG_0001.HEIC")]
    [InlineData(ReviewSort.Size, "IMG_0004.MOV")]
    [InlineData(ReviewSort.Status, "IMG_0002.HEIC")]
    public void Sorts(ReviewSort sort, string first)
    {
        var viewModel = Create();

        viewModel.SortIndex = (int)sort;

        Assert.Equal(first, viewModel.VisibleItems[0].Name);
    }

    [Fact]
    public void Unticking_an_item_leaves_it_out_of_the_transfer()
    {
        var viewModel = Create();

        Find(viewModel, "IMG_0001.HEIC").IsSelected = false;

        Assert.DoesNotContain(_session.SelectedItems, item => item.Item.Primary.FileName == "IMG_0001.HEIC");
        Assert.Contains(_session.SelectedItems, item => item.Item.Primary.FileName == "IMG_0003.JPG"); // Reported as already there.
        Assert.StartsWith("3 of 4 selected", viewModel.SelectionSummary);
    }

    [Fact]
    public void Only_new_leaves_out_the_possible_duplicates()
    {
        var viewModel = Create();

        viewModel.SelectOnlyNewCommand.Execute(null);

        Assert.False(Find(viewModel, "IMG_0002.HEIC").IsSelected);
        Assert.True(Find(viewModel, "IMG_0001.HEIC").IsSelected);
        Assert.False(viewModel.IncludePossibleDuplicates);
    }

    [Fact]
    public void The_possible_duplicates_switch_selects_and_deselects_them_all()
    {
        var viewModel = Create();

        viewModel.IncludePossibleDuplicates = false;
        Assert.False(Find(viewModel, "IMG_0002.HEIC").IsSelected);

        viewModel.IncludePossibleDuplicates = true;
        Assert.True(Find(viewModel, "IMG_0002.HEIC").IsSelected);
    }

    [Fact]
    public void Select_none_and_all_act_on_what_is_shown()
    {
        var viewModel = Create();
        viewModel.TypeFilterIndex = (int)ReviewTypeFilter.Videos;

        viewModel.SelectNoneCommand.Execute(null);

        viewModel.TypeFilterIndex = (int)ReviewTypeFilter.All;
        Assert.False(Find(viewModel, "IMG_0004.MOV").IsSelected);
        Assert.True(Find(viewModel, "IMG_0001.HEIC").IsSelected);

        viewModel.SelectAllCommand.Execute(null);
        Assert.True(Find(viewModel, "IMG_0004.MOV").IsSelected);
    }

    [Fact]
    public void A_new_plan_resets_the_selection()
    {
        var viewModel = Create();
        Find(viewModel, "IMG_0001.HEIC").IsSelected = false;

        _session.SetPlan(Plan());

        Assert.True(Find(viewModel, "IMG_0001.HEIC").IsSelected);
    }

    [Fact]
    public async Task Previews_load_once_and_duplicates_also_show_the_existing_file()
    {
        var viewModel = Create();
        var possible = Find(viewModel, "IMG_0002.HEIC");

        await possible.LoadThumbnailsAsync(CancellationToken.None);
        await possible.LoadThumbnailsAsync(CancellationToken.None);

        Assert.Equal(@"C:\previews\IMG_0002.HEIC.jpg", possible.Thumbnail);
        Assert.Equal(@"D:\Photos\Converted.jpg.preview.jpg", possible.OtherThumbnail);
        Assert.Single(_thumbnails.PhoneRequests);
        Assert.Equal(Strings.ItemKeepBoth, possible.SelectLabel);
        Assert.Contains(@"D:\Photos\Converted.jpg", possible.DetailText);
    }

    [Fact]
    public async Task A_cancelled_preview_load_is_tried_again_later()
    {
        var viewModel = Create();
        var item = Find(viewModel, "IMG_0001.HEIC");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await item.LoadThumbnailsAsync(cancelled.Token);
        Assert.Null(item.Thumbnail);

        await item.LoadThumbnailsAsync(CancellationToken.None);
        Assert.NotNull(item.Thumbnail);
    }

    private ReviewViewModel Create()
    {
        _session.SetPlan(Plan());
        return new ReviewViewModel(_session, _thumbnails, new InlineUiDispatcher());
    }

    private static ImportPlan Plan()
    {
        var items = new List<ItemClassification>
        {
            Item(Asset("IMG_0001.HEIC", 3_000_000, new DateTimeOffset(2024, 5, 1, 12, 0, 0, TimeSpan.Zero)), AssetStatus.New),
            Item(Asset("IMG_0002.HEIC", 2_000_000, new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero)), AssetStatus.PossibleDuplicate, similar: @"D:\Photos\Converted.jpg"),
            Item(Asset("IMG_0003.JPG", 1_000_000, null, "Internal Storage/DCIM/100APPLE"), AssetStatus.ExactDuplicate, existing: @"D:\Photos\IMG_0003.JPG"),
            Item(Asset("IMG_0004.MOV", 90_000_000, new DateTimeOffset(2023, 7, 1, 12, 0, 0, TimeSpan.Zero)), AssetStatus.New),
            Item(Asset("IMG_0005.PNG", 500_000, null, "Internal Storage/202304__"), AssetStatus.New),
        };
        return new ImportPlan(@"D:\Photos", items, 0);
    }

    private static PhotoAsset Asset(string name, long size, DateTimeOffset? created, string folder = "Internal Storage/202409__") => new()
    {
        Id = name,
        PersistentId = "pid-" + name,
        FileName = name,
        SourcePath = folder + "/" + name,
        MediaType = MediaFormats.GetMediaType(name),
        ReportedSize = size,
        CreatedAt = created,
    };

    private static ItemClassification Item(PhotoAsset asset, AssetStatus status, string? existing = null, string? similar = null) =>
        new(new MediaItem(asset), [new ComponentClassification(asset, status, existing, SimilarPath: similar, SimilarityDistance: similar is null ? null : 3)]);

    private static ReviewItemViewModel Find(ReviewViewModel viewModel, string name) =>
        viewModel.VisibleItems.Single(item => item.Name == name);

    private static List<string> Names(ReviewViewModel viewModel) => viewModel.VisibleItems.Select(item => item.Name).ToList();
}
