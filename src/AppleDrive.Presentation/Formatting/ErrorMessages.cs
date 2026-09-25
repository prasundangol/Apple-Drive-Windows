using AppleDrive.Domain.Results;
using AppleDrive.Presentation.Resources;

namespace AppleDrive.Presentation.Formatting;

/// <summary>Maps structured errors to plain-language messages.</summary>
public static class ErrorMessages
{
    public static string For(AppError error) => error.Kind switch
    {
        ErrorKind.Cancelled => Strings.ScanCancelled,
        ErrorKind.DeviceNotFound or ErrorKind.DeviceDisconnected => Strings.ErrorDeviceDisconnected,
        ErrorKind.DeviceLockedOrUntrusted => Strings.ErrorDeviceLocked,
        ErrorKind.DeviceBusy => Strings.ErrorDeviceBusy,
        ErrorKind.DeviceIo => Strings.ErrorDeviceIo,
        _ => Strings.ErrorGeneric,
    };
}
