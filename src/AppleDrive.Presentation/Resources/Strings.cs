using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace AppleDrive.Presentation.Resources;

/// <summary>
/// User-facing text. Values live in Strings.resx (add Strings.&lt;culture&gt;.resx to localize);
/// each property name is its resource key.
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Manager =
        new("AppleDrive.Presentation.Resources.Strings", typeof(Strings).Assembly);

    public static string AppTitle => Get();

    public static string NavDashboard => Get();

    public static string NavImport => Get();

    public static string NavHistory => Get();

    public static string NavSettings => Get();

    public static string WelcomeTitle => Get();

    public static string WelcomeBody => Get();

    public static string WelcomeConnectHint => Get();

    public static string WelcomeGetStarted => Get();

    public static string DeviceSearchingTitle => Get();

    public static string DeviceNoneTitle => Get();

    public static string DeviceNoneBody => Get();

    public static string DeviceLockedTitle => Get();

    public static string DeviceLockedBody => Get();

    public static string DeviceUnavailableTitle => Get();

    public static string DeviceUnavailableBody => Get();

    public static string DeviceConnectedTitle => Get();

    public static string DeviceLabel => Get();

    public static string StatusLabel => Get();

    public static string StatusConnected => Get();

    public static string Refresh => Get();

    public static string ScanPhotos => Get();

    public static string Cancel => Get();

    public static string ShowDetails => Get();

    public static string Scanning => Get();

    public static string ScanningFoundFormat => Get();

    public static string ScanCompleteTitle => Get();

    public static string PhotosLabel => Get();

    public static string VideosLabel => Get();

    public static string LivePhotosLabel => Get();

    public static string FilesLabel => Get();

    public static string TotalSizeLabel => Get();

    public static string ScanNoMedia => Get();

    public static string ScanNoMediaBody => Get();

    public static string ScanCancelled => Get();

    public static string ErrorDeviceDisconnected => Get();

    public static string ErrorDeviceLocked => Get();

    public static string ErrorDeviceBusy => Get();

    public static string ErrorDeviceIo => Get();

    public static string ErrorGeneric => Get();

    public static string SettingsTitle => Get();

    public static string DestinationHeader => Get();

    public static string DestinationDescription => Get();

    public static string DestinationNotSet => Get();

    public static string Browse => Get();

    public static string OrganizationHeader => Get();

    public static string OrganizationDescription => Get();

    public static string OrganizationFlat => Get();

    public static string OrganizationYearMonth => Get();

    public static string OrganizationYearMonthDay => Get();

    public static string SkipDuplicatesHeader => Get();

    public static string SkipDuplicatesDescription => Get();

    public static string LogLevelHeader => Get();

    public static string LogLevelDescription => Get();

    public static string LogLevelDebug => Get();

    public static string LogLevelInformation => Get();

    public static string LogLevelWarning => Get();

    public static string LogLevelError => Get();

    public static string OpenLogsFolder => Get();

    public static string PrivacyHeader => Get();

    public static string PrivacyBody => Get();

    public static string AboutHeader => Get();

    public static string VersionFormat => Get();

    public static string ImportEmptyTitle => Get();

    public static string ImportEmptyBody => Get();

    public static string HistoryEmptyTitle => Get();

    public static string HistoryEmptyBody => Get();

    /// <summary>Formats a resource string with the current culture.</summary>
    public static string Format(string format, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, format, arguments);

    private static string Get([CallerMemberName] string key = "") =>
        Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
}
