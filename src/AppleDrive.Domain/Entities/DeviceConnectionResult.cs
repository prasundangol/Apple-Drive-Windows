using AppleDrive.Domain.Enums;
using AppleDrive.Domain.Results;

namespace AppleDrive.Domain.Entities;

/// <summary>Outcome of connecting to a phone.</summary>
public sealed record DeviceConnectionResult(
    DeviceConnectionStatus Status,
    DeviceInfo? Device,
    AppError? Error = null)
{
    public static DeviceConnectionResult NotFound { get; } = new(DeviceConnectionStatus.NotFound, null);
}
