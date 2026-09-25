namespace AppleDrive.Presentation.ViewModels;

/// <summary>Dashboard page: device status, phone scan, and destination folder.</summary>
public sealed class DashboardViewModel(DeviceStatusViewModel device, PhoneScanViewModel scan, DestinationViewModel destination)
{
    public DeviceStatusViewModel Device { get; } = device;

    public PhoneScanViewModel Scan { get; } = scan;

    public DestinationViewModel Destination { get; } = destination;

    public void OnNavigatedTo() => Device.Activate();
}
