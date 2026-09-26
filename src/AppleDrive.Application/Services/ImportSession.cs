using AppleDrive.Domain.Enums;

namespace AppleDrive.Application.Services;

/// <summary>
/// State shared across pages for the current import: the latest scans, the import plan, and which
/// of its items the user has chosen to transfer.
/// </summary>
public sealed class ImportSession
{
    private readonly HashSet<ItemClassification> _deselected = [];

    public PhoneScanResult? LastPhoneScan { get; private set; }

    public DestinationScanSummary? LastDestinationScan { get; private set; }

    /// <summary>The latest analysis. Cleared whenever the scans it was based on are replaced.</summary>
    public ImportPlan? Plan { get; private set; }

    public event EventHandler? Changed;

    /// <summary>Raised when the user selects or deselects items of the plan.</summary>
    public event EventHandler? SelectionChanged;

    public void SetPhoneScan(PhoneScanResult? result)
    {
        LastPhoneScan = result;
        SetPlanCore(null);
    }

    public void SetDestinationScan(DestinationScanSummary? summary)
    {
        LastDestinationScan = summary;
        SetPlanCore(null);
    }

    /// <summary>Replaces the plan. Every item that can be transferred starts selected.</summary>
    public void SetPlan(ImportPlan? plan) => SetPlanCore(plan);

    /// <summary>
    /// Whether an item will be transferred. New items and possible duplicates are selected unless
    /// the user deselected them; exact duplicates are never transferred.
    /// </summary>
    public bool IsSelected(ItemClassification item) =>
        item.Status != AssetStatus.ExactDuplicate && !_deselected.Contains(item);

    public void SetSelected(ItemClassification item, bool selected)
    {
        if (item.Status == AssetStatus.ExactDuplicate)
        {
            return;
        }

        var changed = selected ? _deselected.Remove(item) : _deselected.Add(item);
        if (changed)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Selects or deselects many items at once, raising one notification.</summary>
    public void SetSelected(IEnumerable<ItemClassification> items, bool selected)
    {
        var changed = false;
        foreach (var item in items.Where(item => item.Status != AssetStatus.ExactDuplicate))
        {
            changed |= selected ? _deselected.Remove(item) : _deselected.Add(item);
        }

        if (changed)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The plan's items that will be transferred (or, for exact duplicates, reported as already there).</summary>
    public IReadOnlyList<ItemClassification> SelectedItems =>
        Plan?.Items.Where(item => item.Status == AssetStatus.ExactDuplicate || !_deselected.Contains(item)).ToList() ?? [];

    private void SetPlanCore(ImportPlan? plan)
    {
        Plan = plan;
        _deselected.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
