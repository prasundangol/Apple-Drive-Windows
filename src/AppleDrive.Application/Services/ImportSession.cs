namespace AppleDrive.Application.Services;

/// <summary>State shared across pages for the current import: the latest scans and import plan.</summary>
public sealed class ImportSession
{
    public PhoneScanResult? LastPhoneScan { get; private set; }

    public DestinationScanSummary? LastDestinationScan { get; private set; }

    /// <summary>The latest analysis. Cleared whenever the scans it was based on are replaced.</summary>
    public ImportPlan? Plan { get; private set; }

    public event EventHandler? Changed;

    public void SetPhoneScan(PhoneScanResult? result)
    {
        LastPhoneScan = result;
        Plan = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetDestinationScan(DestinationScanSummary? summary)
    {
        LastDestinationScan = summary;
        Plan = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetPlan(ImportPlan? plan)
    {
        Plan = plan;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
