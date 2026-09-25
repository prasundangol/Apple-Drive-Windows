using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Enums;

namespace AppleDrive.Application.Services;

/// <summary>Summary of the media found on a phone.</summary>
public sealed class PhoneScanResult
{
    public PhoneScanResult(DeviceInfo device, IReadOnlyList<MediaItem> items)
    {
        Device = device;
        Items = items;
        PhotoCount = items.Count(item => item.MediaType == MediaType.Image);
        VideoCount = items.Count(item => item.MediaType == MediaType.Video);
        LivePhotoCount = items.Count(item => item.IsLivePhoto);
        FileCount = items.Sum(item => item.Components.Count());
        TotalReportedBytes = items.SelectMany(item => item.Components).Sum(asset => asset.ReportedSize ?? 0);
    }

    public DeviceInfo Device { get; }

    public IReadOnlyList<MediaItem> Items { get; }

    /// <summary>Photos, counting each Live Photo once.</summary>
    public int PhotoCount { get; }

    /// <summary>Standalone videos (Live Photo motion clips are not counted).</summary>
    public int VideoCount { get; }

    public int LivePhotoCount { get; }

    /// <summary>Number of individual files, including both Live Photo components.</summary>
    public int FileCount { get; }

    /// <summary>Sum of sizes reported by the device; approximate.</summary>
    public long TotalReportedBytes { get; }
}
