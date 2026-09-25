namespace AppleDrive.Presentation.ViewModels;

/// <summary>Dashboard page: device status plus phone scan.</summary>
public sealed class DashboardViewModel(DeviceStatusViewModel device, PhoneScanViewModel scan)
{
    public DeviceStatusViewModel Device { get; } = device;

    public PhoneScanViewModel Scan { get; } = scan;

    public void OnNavigatedTo() => Device.Activate();
}
