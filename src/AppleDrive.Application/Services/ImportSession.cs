namespace AppleDrive.Application.Services;

/// <summary>State shared across pages for the current import: the latest phone scan.</summary>
public sealed class ImportSession
{
    public PhoneScanResult? LastPhoneScan { get; private set; }

    public event EventHandler? Changed;

    public void SetPhoneScan(PhoneScanResult? result)
    {
        LastPhoneScan = result;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
