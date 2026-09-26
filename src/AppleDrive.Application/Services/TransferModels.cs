using AppleDrive.Domain.Enums;
using AppleDrive.Domain.Results;

namespace AppleDrive.Application.Services;

/// <summary>What to transfer and how to lay it out.</summary>
/// <param name="Items">Classified items; only their components that need transferring are copied.</param>
/// <param name="DestinationRoot">The destination folder the items were classified against.</param>
/// <param name="SkipExactDuplicates">When false, files identical to one already in the destination are copied too.</param>
/// <param name="DeviceName">Recorded in transfer history.</param>
public sealed record TransferRequest(
    IReadOnlyList<ItemClassification> Items,
    string DestinationRoot,
    FolderOrganization Organization,
    bool SkipExactDuplicates = true,
    string? DeviceName = null);

/// <summary>A snapshot of a running transfer.</summary>
/// <param name="ItemsDone">Items with something to copy that are finished (transferred, found to be duplicates, or failed).</param>
/// <param name="ItemsTotal">Items with something to copy. Items already in the destination are counted in <paramref name="Skipped"/> only.</param>
/// <param name="CurrentFile">The file being read from the phone, or <c>null</c> between files.</param>
/// <param name="BytesDone">Bytes read from the phone so far.</param>
/// <param name="BytesTotal">Approximate total, from the sizes the phone reports.</param>
/// <param name="BytesPerSecond">Recent speed, or <c>null</c> until it can be measured.</param>
/// <param name="Remaining">Estimated time left, or <c>null</c> while it would be unreliable.</param>
public sealed record TransferProgress(
    int ItemsDone,
    int ItemsTotal,
    string? CurrentFile,
    long BytesDone,
    long BytesTotal,
    double? BytesPerSecond,
    TimeSpan? Remaining,
    int Transferred,
    int Skipped,
    int Failed);

public enum ItemTransferStatus
{
    /// <summary>Every part that needed copying was copied and verified.</summary>
    Transferred,

    /// <summary>Already in the destination according to the import plan; nothing was copied.</summary>
    AlreadyExists,

    /// <summary>Copied, but the content turned out to already exist in the destination, so the copy was discarded.</summary>
    DuplicateFound,

    /// <summary>At least one part failed. Parts that succeeded are kept.</summary>
    Failed,

    /// <summary>Not attempted because the transfer was cancelled or stopped first.</summary>
    NotAttempted,
}

/// <summary>What happened to one file of an item.</summary>
/// <param name="DestinationPath">Where the file is now: the new copy, or the existing identical file.</param>
public sealed record ComponentTransferOutcome(
    ComponentClassification Component,
    ItemTransferStatus Status,
    string? DestinationPath = null,
    AppError? Error = null);

/// <summary>What happened to one media item.</summary>
public sealed class ItemTransferOutcome(ItemClassification item, IReadOnlyList<ComponentTransferOutcome> components)
{
    public ItemClassification Item { get; } = item;

    public IReadOnlyList<ComponentTransferOutcome> Components { get; } = components;

    public ItemTransferStatus Status { get; } = Summarize(components);

    /// <summary>The first failure, for display.</summary>
    public AppError? Error => Components.FirstOrDefault(component => component.Error is not null)?.Error;

    public string FileName => Item.Item.Primary.FileName;

    /// <summary>
    /// The item as it now stands, for a retry: parts that are already in the destination become
    /// exact duplicates pointing at their file, so only the missing parts are copied again, and a
    /// Live Photo's missing video is placed next to its image.
    /// </summary>
    public ItemClassification ToRemainingWork() => new(
        Item.Item,
        Components.Select(component => component.Status is ItemTransferStatus.Transferred or ItemTransferStatus.DuplicateFound
                && component.DestinationPath is { } path
            ? component.Component with { Status = AssetStatus.ExactDuplicate, ExistingPath = path }
            : component.Component).ToList());

    private static ItemTransferStatus Summarize(IReadOnlyList<ComponentTransferOutcome> components)
    {
        if (components.Any(component => component.Status == ItemTransferStatus.Failed))
        {
            return ItemTransferStatus.Failed;
        }

        if (components.Any(component => component.Status == ItemTransferStatus.NotAttempted))
        {
            // Partly done before a cancel counts as not finished, so a retry picks it up.
            return components.Any(component => component.Status == ItemTransferStatus.Transferred)
                ? ItemTransferStatus.Failed
                : ItemTransferStatus.NotAttempted;
        }

        if (components.Any(component => component.Status == ItemTransferStatus.Transferred))
        {
            return ItemTransferStatus.Transferred;
        }

        return components.Any(component => component.Status == ItemTransferStatus.DuplicateFound)
            ? ItemTransferStatus.DuplicateFound
            : ItemTransferStatus.AlreadyExists;
    }
}

/// <summary>The outcome of a transfer run.</summary>
public sealed class TransferRunResult
{
    public TransferRunResult(
        string sessionId,
        string destinationRoot,
        TransferSessionStatus status,
        IReadOnlyList<ItemTransferOutcome> items,
        long bytesTransferred,
        TimeSpan elapsed,
        AppError? stopReason)
    {
        SessionId = sessionId;
        DestinationRoot = destinationRoot;
        Status = status;
        Items = items;
        BytesTransferred = bytesTransferred;
        Elapsed = elapsed;
        StopReason = stopReason;
        TransferredCount = items.Count(item => item.Status == ItemTransferStatus.Transferred);
        SkippedCount = items.Count(item => item.Status is ItemTransferStatus.AlreadyExists or ItemTransferStatus.DuplicateFound);
        FailedCount = items.Count(item => item.Status == ItemTransferStatus.Failed);
        NotAttemptedCount = items.Count(item => item.Status == ItemTransferStatus.NotAttempted);
    }

    public string SessionId { get; }

    public string DestinationRoot { get; }

    public TransferSessionStatus Status { get; }

    public IReadOnlyList<ItemTransferOutcome> Items { get; }

    /// <summary>Bytes written to the destination as new, verified files.</summary>
    public long BytesTransferred { get; }

    public TimeSpan Elapsed { get; }

    /// <summary>Why the run ended early (<see cref="TransferSessionStatus.Stopped"/>), otherwise <c>null</c>.</summary>
    public AppError? StopReason { get; }

    public int TransferredCount { get; }

    public int SkippedCount { get; }

    public int FailedCount { get; }

    public int NotAttemptedCount { get; }

    public IEnumerable<ItemTransferOutcome> FailedItems => Items.Where(item => item.Status == ItemTransferStatus.Failed);

    /// <summary>Items to run again: failed ones and those not reached, with finished parts marked done.</summary>
    public IReadOnlyList<ItemClassification> GetRetryItems() => Items
        .Where(item => item.Status is ItemTransferStatus.Failed or ItemTransferStatus.NotAttempted)
        .Select(item => item.ToRemainingWork())
        .ToList();
}
