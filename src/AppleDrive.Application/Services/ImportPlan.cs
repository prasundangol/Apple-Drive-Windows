using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Enums;

namespace AppleDrive.Application.Services;

/// <summary>How closely a possible duplicate resembles the existing file.</summary>
public enum Similarity
{
    High,
    VeryHigh,
}

/// <summary>How one file of a media item compares with the destination.</summary>
/// <param name="Asset">The file on the phone.</param>
/// <param name="Status"><see cref="AssetStatus.New"/>, <see cref="AssetStatus.ExactDuplicate"/> or <see cref="AssetStatus.PossibleDuplicate"/>.</param>
/// <param name="ExistingPath">For an exact duplicate, the identical file already in the destination.</param>
/// <param name="Sha256">The phone file's hash, when it had to be read to decide.</param>
/// <param name="SimilarPath">For a possible duplicate, the destination image that looks the same.</param>
/// <param name="SimilarityDistance">For a possible duplicate, differing bits of the 256-bit visual hash (0 = identical look).</param>
public sealed record ComponentClassification(
    PhotoAsset Asset,
    AssetStatus Status,
    string? ExistingPath = null,
    byte[]? Sha256 = null,
    string? SimilarPath = null,
    int? SimilarityDistance = null)
{
    /// <summary>Differing bits (of 256) up to which a possible duplicate counts as very similar.</summary>
    public const int VeryHighSimilarityDistance = 12;

    /// <summary>Everything except an exact duplicate has to be copied (possible duplicates only if the user includes them).</summary>
    public bool NeedsTransfer => Status != AssetStatus.ExactDuplicate;

    public Similarity? Similarity => SimilarityDistance is { } distance
        ? distance <= VeryHighSimilarityDistance ? Services.Similarity.VeryHigh : Services.Similarity.High
        : null;
}

/// <summary>How a media item (photo, video or Live Photo) compares with the destination.</summary>
public sealed class ItemClassification
{
    public ItemClassification(MediaItem item, IReadOnlyList<ComponentClassification> components)
    {
        Item = item;
        Components = components;
        Status = components.All(component => component.Status == AssetStatus.ExactDuplicate)
            ? AssetStatus.ExactDuplicate
            : components.Any(component => component.Status == AssetStatus.PossibleDuplicate)
                ? AssetStatus.PossibleDuplicate
                : AssetStatus.New;
    }

    public MediaItem Item { get; }

    public MediaType MediaType => Item.MediaType;

    /// <summary>
    /// Per-file results. A Live Photo whose image already exists but whose video does not is
    /// <see cref="AssetStatus.New"/>, and only its missing part is transferred.
    /// </summary>
    public IReadOnlyList<ComponentClassification> Components { get; }

    /// <summary>
    /// <see cref="AssetStatus.ExactDuplicate"/> only when every file already exists byte for byte;
    /// <see cref="AssetStatus.PossibleDuplicate"/> when a file looks like one in the destination.
    /// A possible duplicate is never skipped unless the user chooses to.
    /// </summary>
    public AssetStatus Status { get; }

    /// <summary>Destination path of the identical primary file, for display.</summary>
    public string? ExistingPath => Components[0].ExistingPath;

    /// <summary>For a possible duplicate, the destination image it resembles.</summary>
    public ComponentClassification? SimilarComponent => Components.FirstOrDefault(component => component.Status == AssetStatus.PossibleDuplicate);

    public long TransferBytes => Components.Where(c => c.NeedsTransfer).Sum(c => c.Asset.ReportedSize ?? 0);

    /// <summary>The same item with one component's classification replaced.</summary>
    public ItemClassification With(ComponentClassification replaced, ComponentClassification replacement) =>
        new(Item, Components.Select(component => component == replaced ? replacement : component).ToList());
}

/// <summary>What an import would do: every phone item classified against the destination.</summary>
public sealed class ImportPlan
{
    public ImportPlan(string destinationRoot, IReadOnlyList<ItemClassification> items, int unverifiedFiles, int visuallyUncheckedImages = 0)
    {
        DestinationRoot = destinationRoot;
        Items = items;
        UnverifiedFiles = unverifiedFiles;
        VisuallyUncheckedImages = visuallyUncheckedImages;
        NewCount = items.Count(item => item.Status == AssetStatus.New);
        ExactDuplicateCount = items.Count(item => item.Status == AssetStatus.ExactDuplicate);
        PossibleDuplicateCount = items.Count(item => item.Status == AssetStatus.PossibleDuplicate);
        VideoCount = items.Count(item => item.MediaType == MediaType.Video);
        TransferBytes = items.Where(item => item.Status == AssetStatus.New).Sum(item => item.TransferBytes);
        PossibleDuplicateBytes = items.Where(item => item.Status == AssetStatus.PossibleDuplicate).Sum(item => item.TransferBytes);
    }

    public string DestinationRoot { get; }

    public IReadOnlyList<ItemClassification> Items { get; }

    public int TotalCount => Items.Count;

    public int NewCount { get; }

    public int ExactDuplicateCount { get; }

    /// <summary>Items that look like a destination image but aren't byte-for-byte identical.</summary>
    public int PossibleDuplicateCount { get; }

    public int VideoCount { get; }

    /// <summary>Approximate bytes to copy for new items (sizes reported by the phone).</summary>
    public long TransferBytes { get; }

    /// <summary>Approximate bytes to copy for possible duplicates, if they are included.</summary>
    public long PossibleDuplicateBytes { get; }

    /// <summary>Whether anything could be transferred.</summary>
    public bool HasWork => NewCount + PossibleDuplicateCount > 0;

    /// <summary>
    /// Files that had possible matches but could not be read to confirm; they are treated as new,
    /// so nothing is skipped without proof.
    /// </summary>
    public int UnverifiedFiles { get; }

    /// <summary>New images that could not be compared visually (unreadable, or no decoder for the format).</summary>
    public int VisuallyUncheckedImages { get; }

    /// <summary>The same plan with some items replaced.</summary>
    public ImportPlan WithItems(IReadOnlyList<ItemClassification> items, int visuallyUncheckedImages) =>
        new(DestinationRoot, items, UnverifiedFiles, visuallyUncheckedImages);
}
