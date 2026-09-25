using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Enums;

namespace AppleDrive.Application.Services;

/// <summary>How one file of a media item compares with the destination.</summary>
/// <param name="Asset">The file on the phone.</param>
/// <param name="Status"><see cref="AssetStatus.New"/> or <see cref="AssetStatus.ExactDuplicate"/>.</param>
/// <param name="ExistingPath">For an exact duplicate, the identical file already in the destination.</param>
/// <param name="Sha256">The phone file's hash, when it had to be read to decide.</param>
public sealed record ComponentClassification(
    PhotoAsset Asset,
    AssetStatus Status,
    string? ExistingPath = null,
    byte[]? Sha256 = null)
{
    public bool NeedsTransfer => Status != AssetStatus.ExactDuplicate;
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
    /// <see cref="AssetStatus.ExactDuplicate"/> only when every file already exists byte for byte.
    /// </summary>
    public AssetStatus Status { get; }

    /// <summary>Destination path of the identical primary file, for display.</summary>
    public string? ExistingPath => Components[0].ExistingPath;

    public long TransferBytes => Components.Where(c => c.NeedsTransfer).Sum(c => c.Asset.ReportedSize ?? 0);
}

/// <summary>What an import would do: every phone item classified against the destination.</summary>
public sealed class ImportPlan
{
    public ImportPlan(string destinationRoot, IReadOnlyList<ItemClassification> items, int unverifiedFiles)
    {
        DestinationRoot = destinationRoot;
        Items = items;
        UnverifiedFiles = unverifiedFiles;
        NewCount = items.Count(item => item.Status == AssetStatus.New);
        ExactDuplicateCount = items.Count(item => item.Status == AssetStatus.ExactDuplicate);
        VideoCount = items.Count(item => item.MediaType == MediaType.Video);
        TransferBytes = items.Sum(item => item.TransferBytes);
    }

    public string DestinationRoot { get; }

    public IReadOnlyList<ItemClassification> Items { get; }

    public int TotalCount => Items.Count;

    public int NewCount { get; }

    public int ExactDuplicateCount { get; }

    public int VideoCount { get; }

    /// <summary>Approximate bytes to copy (sizes reported by the phone).</summary>
    public long TransferBytes { get; }

    /// <summary>
    /// Files that had possible matches but could not be read to confirm; they are treated as new,
    /// so nothing is skipped without proof.
    /// </summary>
    public int UnverifiedFiles { get; }
}

