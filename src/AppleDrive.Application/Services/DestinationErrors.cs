using AppleDrive.Domain.Results;

namespace AppleDrive.Application.Services;

/// <summary>Maps file-system failures while writing to the destination to structured errors.</summary>
public static class DestinationErrors
{
    private const int ErrorWriteProtect = unchecked((int)0x80070013);
    private const int ErrorNotReady = unchecked((int)0x80070015);
    private const int ErrorHandleDiskFull = unchecked((int)0x80070027);
    private const int ErrorDeviceNotExist = unchecked((int)0x80070037);
    private const int ErrorNetNameDeleted = unchecked((int)0x80070040);
    private const int ErrorDiskFull = unchecked((int)0x80070070);
    private const int ErrorNoSuchDevice = unchecked((int)0x800701B1);

    /// <summary>
    /// Classifies a failure. <paramref name="rootExists"/> tells whether the destination folder is
    /// still there: if it vanished (drive unplugged), every later write would fail too.
    /// </summary>
    public static AppError FromException(Exception exception, bool rootExists)
    {
        var kind = exception switch
        {
            UnauthorizedAccessException => ErrorKind.AccessDenied,
            IOException { HResult: ErrorDiskFull or ErrorHandleDiskFull } => ErrorKind.DestinationFull,
            IOException { HResult: ErrorWriteProtect } => ErrorKind.DestinationReadOnly,
            IOException { HResult: ErrorNotReady or ErrorDeviceNotExist or ErrorNoSuchDevice or ErrorNetNameDeleted } => ErrorKind.DestinationUnavailable,
            DriveNotFoundException => ErrorKind.DestinationUnavailable,
            IOException when !rootExists => ErrorKind.DestinationUnavailable,
            _ => ErrorKind.DestinationIo,
        };

        return new AppError(kind, exception.Message, exception.HResult);
    }

    /// <summary>Errors that affect every file, so the transfer stops instead of failing each one.</summary>
    public static bool AffectsAllFiles(AppError error) => error.Kind is
        ErrorKind.DestinationFull or ErrorKind.DestinationReadOnly or ErrorKind.DestinationUnavailable or ErrorKind.AccessDenied;
}
