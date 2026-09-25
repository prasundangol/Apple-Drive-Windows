using AppleDrive.Domain.Results;

namespace AppleDrive.Infrastructure.Iphone.Wpd;

/// <summary>Translates WPD HRESULTs into structured errors.</summary>
internal static class WpdErrors
{
    public const int SOk = 0;
    public const int SFalse = 1;

    private const int EAccessDenied = unchecked((int)0x80070005);
    private const int ErrorGenFailure = unchecked((int)0x8007001F);
    private const int ErrorNotReady = unchecked((int)0x80070015);
    private const int ErrorDevNotExist = unchecked((int)0x80070037);
    private const int ErrorSemTimeout = unchecked((int)0x80070079);
    private const int ErrorBusy = unchecked((int)0x800700AA);
    private const int ErrorNoSuchDevice = unchecked((int)0x800701B1);
    private const int ErrorDeviceNotConnected = unchecked((int)0x8007048F);
    private const int ErrorNotFound = unchecked((int)0x80070490);
    private const int ErrorCancelled = unchecked((int)0x800704C7);
    private const int ErrorFileNotFound = unchecked((int)0x80070002);
    private const int ErrorInvalidHandle = unchecked((int)0x80070006);
    private const int RpcServerUnavailable = unchecked((int)0x800706BA);

    public static bool Failed(int hr) => hr < 0;

    public static AppError FromHResult(int hr, string operation)
    {
        var kind = hr switch
        {
            EAccessDenied => ErrorKind.DeviceLockedOrUntrusted,
            ErrorDeviceNotConnected or ErrorNoSuchDevice or ErrorDevNotExist or ErrorNotReady
                or ErrorInvalidHandle or RpcServerUnavailable => ErrorKind.DeviceDisconnected,
            ErrorBusy or ErrorSemTimeout => ErrorKind.DeviceBusy,
            ErrorNotFound or ErrorFileNotFound => ErrorKind.SourceUnavailable,
            ErrorCancelled => ErrorKind.Cancelled,
            ErrorGenFailure => ErrorKind.DeviceIo,
            _ => ErrorKind.DeviceIo,
        };

        return new AppError(kind, $"{operation} failed.", hr);
    }
}
