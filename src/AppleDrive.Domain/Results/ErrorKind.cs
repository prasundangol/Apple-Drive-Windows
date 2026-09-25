namespace AppleDrive.Domain.Results;

/// <summary>Stable categories of failure that the UI translates into user-facing text.</summary>
public enum ErrorKind
{
    Unknown = 0,
    Cancelled,
    DeviceNotFound,
    DeviceDisconnected,
    DeviceLockedOrUntrusted,
    DeviceBusy,
    DeviceIo,
    SourceUnavailable,
    DestinationUnavailable,
    DestinationFull,
    DestinationReadOnly,
    AccessDenied,
    VerificationFailed,
    Database,
}
