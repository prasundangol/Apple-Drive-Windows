using System.Globalization;
using AppleDrive.Application.Interfaces;
using AppleDrive.Application.Services;
using AppleDrive.Domain.Enums;
using AppleDrive.Presentation.Formatting;
using AppleDrive.Presentation.Resources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AppleDrive.Presentation.ViewModels;

/// <summary>One phone item in the review grid: preview, details, status and whether it will be copied.</summary>
public sealed partial class ReviewItemViewModel : ObservableObject
{
    private readonly ImportSession _session;
    private readonly IThumbnailService _thumbnails;

    public ReviewItemViewModel(ItemClassification item, ImportSession session, IThumbnailService thumbnails)
    {
        Item = item;
        _session = session;
        _thumbnails = thumbnails;

        var primary = item.Item.Primary;
        Name = primary.FileName;
        TotalBytes = item.Item.Components.Sum(component => component.ReportedSize ?? 0);
        SizeText = ByteSize.Format(TotalBytes);
        KindText = item.Item.IsLivePhoto ? Strings.KindLivePhoto : item.MediaType == MediaType.Video ? Strings.KindVideo : Strings.KindPhoto;
        IsVideo = item.MediaType == MediaType.Video;

        // The phone often reports no date; its camera-roll folder still gives the month.
        if ((primary.CreatedAt ?? primary.ModifiedAt) is { } date)
        {
            Date = date.ToLocalTime().DateTime;
            DateText = Date.Value.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        }
        else if (DestinationLayout.MonthFromPhoneFolder(primary.SourceFolder) is { } month)
        {
            Date = month;
            DateText = month.ToString("MMM yyyy", CultureInfo.CurrentCulture);
        }
        else
        {
            DateText = Strings.DateUnknown;
        }

        (StatusText, DetailText, OtherPath) = item.Status switch
        {
            AssetStatus.ExactDuplicate => (Strings.StatusAlreadyInDestination, Strings.Format(Strings.ExistingFormat, item.ExistingPath), item.ExistingPath),
            AssetStatus.PossibleDuplicate when item.SimilarComponent is { } similar => (
                Strings.StatusPossibleDuplicate,
                Strings.Format(
                    Strings.LooksLikeFormat,
                    similar.SimilarPath,
                    similar.Similarity == Similarity.VeryHigh ? Strings.SimilarityVeryHigh : Strings.SimilarityHigh),
                similar.SimilarPath),
            _ => (Strings.StatusNew, string.Empty, null),
        };
        IsSelected = session.IsSelected(item);
    }

    public ItemClassification Item { get; }

    public AssetStatus Status => Item.Status;

    public string Name { get; }

    public string KindText { get; }

    public bool IsVideo { get; }

    public long TotalBytes { get; }

    public string SizeText { get; }

    /// <summary>When the item was taken, as far as the phone says; used for sorting and the year filter.</summary>
    public DateTime? Date { get; }

    public string DateText { get; }

    public string StatusText { get; }

    /// <summary>For duplicates, the file already in the destination.</summary>
    public string DetailText { get; }

    public bool HasDetail => DetailText.Length > 0;

    /// <summary>The destination file shown next to a duplicate's preview.</summary>
    public string? OtherPath { get; }

    public bool IsPossibleDuplicate => Status == AssetStatus.PossibleDuplicate;

    public bool IsExactDuplicate => Status == AssetStatus.ExactDuplicate;

    /// <summary>Exact duplicates are never copied again, so they can't be selected.</summary>
    public bool IsSelectable => Status != AssetStatus.ExactDuplicate;

    /// <summary>"Keep both" for a possible duplicate (copying keeps both versions), otherwise "Copy".</summary>
    public string SelectLabel => IsPossibleDuplicate ? Strings.ItemKeepBoth : Strings.ItemCopy;

    /// <summary>Accessible name for the item's check box.</summary>
    public string SelectAccessibleName => $"{SelectLabel}: {Name}";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Preview of the phone item, once loaded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    public partial string? Thumbnail { get; private set; }

    public bool HasThumbnail => Thumbnail is not null;

    /// <summary>Preview of the destination file it duplicates, once loaded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOtherThumbnail))]
    public partial string? OtherThumbnail { get; private set; }

    public bool HasOtherThumbnail => OtherThumbnail is not null;

    /// <summary>
    /// Loads the previews that aren't loaded yet. Called whenever the item scrolls into view and
    /// cancelled when it leaves; overlapping calls are fine, as the service makes each preview once.
    /// </summary>
    public async Task LoadThumbnailsAsync(CancellationToken cancellationToken)
    {
        try
        {
            Thumbnail ??= await _thumbnails.GetPhoneThumbnailAsync(Item.Item.Primary, cancellationToken);
            if (OtherPath is { } other)
            {
                OtherThumbnail ??= await _thumbnails.GetFileThumbnailAsync(other, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Scrolled away before it loaded; it is asked for again when it comes back.
        }
    }

    /// <summary>Picks up a selection change made elsewhere (bulk commands, the confirmation).</summary>
    public void RefreshSelection() => IsSelected = _session.IsSelected(Item);

    partial void OnIsSelectedChanged(bool value) => _session.SetSelected(Item, value);
}
