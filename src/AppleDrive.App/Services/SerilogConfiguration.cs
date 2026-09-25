using AppleDrive.Application.Interfaces;
using Serilog;

namespace AppleDrive.App.Services;

/// <summary>Configures the diagnostic log (rolling files under the app data folder).</summary>
internal static class SerilogConfiguration
{
    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    public static ILogger Create(IAppPaths paths, SerilogLogLevelController levelController)
    {
        Directory.CreateDirectory(paths.LogsFolder);
        return new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelController.Switch)
            .WriteTo.File(
                Path.Combine(paths.LogsFolder, "apple-drive-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 20 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: OutputTemplate)
            .CreateLogger();
    }
}
