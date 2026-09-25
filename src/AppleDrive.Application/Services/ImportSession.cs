namespace AppleDrive.Application.Services;

/// <summary>State shared across pages for the current import: the latest phone and destination scans.</summary>
public sealed class ImportSession
{
    public PhoneScanResult? LastPhoneScan { get; private set; }

    public DestinationScanSummary? LastDestinationScan { get; private set; }

    public event EventHandler? Changed;

    public void SetPhoneScan(PhoneScanResult? result)
    {
        LastPhoneScan = result;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetDestinationScan(DestinationScanSummary? summary)
    {
        LastDestinationScan = summary;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
