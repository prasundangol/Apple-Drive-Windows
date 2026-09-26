using System.Globalization;
using AppleDrive.Application.Interfaces;
using AppleDrive.Presentation.Resources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AppleDrive.Presentation.ViewModels;

/// <summary>Dashboard page: device status, phone scan, destination folder, and the last import.</summary>
public sealed partial class DashboardViewModel(
    DeviceStatusViewModel device,
    PhoneScanViewModel scan,
    DestinationViewModel destination,
    ITransferRepository history) : ObservableObject
{
    public DeviceStatusViewModel Device { get; } = device;

    public PhoneScanViewModel Scan { get; } = scan;

    public DestinationViewModel Destination { get; } = destination;

    /// <summary>When the last transfer ran and what it did, or that there hasn't been one.</summary>
    [ObservableProperty]
    public partial string LastImportText { get; private set; } = Strings.LastImportNone;

    public void OnNavigatedTo()
    {
        Device.Activate();
        _ = LoadLastImportAsync();
    }

    private async Task LoadLastImportAsync()
    {
        try
        {
            var last = (await history.GetRecentSessionsAsync(1, CancellationToken.None)).FirstOrDefault();
            LastImportText = last is null
                ? Strings.LastImportNone
                : Strings.Format(
                    Strings.LastImportFormat,
                    last.StartedAt.ToLocalTime().ToString("D", CultureInfo.CurrentCulture),
                    new HistorySessionViewModel(last).SummaryText);
        }
        catch (DatabaseException)
        {
            // The dashboard still works; the history page reports the database problem.
            LastImportText = Strings.LastImportNone;
        }
    }
}
