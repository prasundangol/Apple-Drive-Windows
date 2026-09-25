using AppleDrive.Application.Services;
using AppleDrive.Domain.Results;
using AppleDrive.Presentation.Formatting;
using AppleDrive.Presentation.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppleDrive.Presentation.ViewModels;

/// <summary>Scans the connected phone and presents the counts.</summary>
public sealed partial class PhoneScanViewModel : ObservableObject
{
    private readonly PhoneScanService _scanService;
    private readonly ImportSession _session;
    private readonly DeviceStatusViewModel _device;

    public PhoneScanViewModel(PhoneScanService scanService, ImportSession session, DeviceStatusViewModel device)
    {
        _scanService = scanService;
        _session = session;
        _device = device;
        _device.ConnectionChanged += (_, _) => ScanCommand.NotifyCanExecuteChanged();
        ApplyResult(session.LastPhoneScan);
    }

    [ObservableProperty]
    public partial bool IsScanning { get; private set; }

    [ObservableProperty]
    public partial string ProgressText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ErrorDetails { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMedia), nameof(IsEmpty))]
    public partial bool HasResult { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMedia), nameof(IsEmpty))]
    public partial int FileCount { get; private set; }

    [ObservableProperty]
    public partial int PhotoCount { get; private set; }

    [ObservableProperty]
    public partial int VideoCount { get; private set; }

    [ObservableProperty]
    public partial int LivePhotoCount { get; private set; }

    [ObservableProperty]
    public partial string TotalSize { get; private set; } = string.Empty;

    public bool HasError => ErrorMessage.Length > 0;

    public bool HasMedia => HasResult && FileCount > 0;

    public bool IsEmpty => HasResult && FileCount == 0;

    private bool CanScan() => _device.IsConnected && !IsScanning;

    [RelayCommand(CanExecute = nameof(CanScan), IncludeCancelCommand = true)]
    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        IsScanning = true;
        ScanCommand.NotifyCanExecuteChanged();
        ErrorMessage = string.Empty;
        ErrorDetails = string.Empty;
        ProgressText = Strings.Format(Strings.ScanningFoundFormat, 0);
        try
        {
            var progress = new Progress<int>(count => ProgressText = Strings.Format(Strings.ScanningFoundFormat, count));
            var result = await _scanService.ScanAsync(progress, cancellationToken);
            if (result.IsSuccess)
            {
                _session.SetPhoneScan(result.Value);
                ApplyResult(result.Value);
                return;
            }

            ShowError(result.Error);
        }
        finally
        {
            IsScanning = false;
            ScanCommand.NotifyCanExecuteChanged();
        }
    }

    private void ShowError(AppError error)
    {
        ErrorMessage = ErrorMessages.For(error);
        ErrorDetails = error.Kind == ErrorKind.Cancelled ? string.Empty : error.ToString();
    }

    private void ApplyResult(PhoneScanResult? result)
    {
        HasResult = result is not null;
        FileCount = result?.FileCount ?? 0;
        PhotoCount = result?.PhotoCount ?? 0;
        VideoCount = result?.VideoCount ?? 0;
        LivePhotoCount = result?.LivePhotoCount ?? 0;
        TotalSize = result is null ? string.Empty : ByteSize.Format(result.TotalReportedBytes);
    }
}
