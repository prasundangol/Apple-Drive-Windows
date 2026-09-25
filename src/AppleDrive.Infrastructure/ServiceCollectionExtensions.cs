using AppleDrive.Application.Interfaces;
using AppleDrive.Application.Services;
using AppleDrive.Application.Settings;
using AppleDrive.Infrastructure.Database;
using AppleDrive.Infrastructure.FileSystem;
using AppleDrive.Infrastructure.Iphone;
using AppleDrive.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace AppleDrive.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the production infrastructure and application services.</summary>
    public static IServiceCollection AddAppleDriveCore(this IServiceCollection services, IAppPaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISettingsService, JsonSettingsService>();

        services.AddSingleton<IPhoneDeviceService, WpdPhoneDeviceService>();
        services.AddSingleton<IPhonePhotoSource, WpdPhotoSource>();
        services.AddSingleton<PhoneScanService>();

        services.AddSingleton(new SqliteDatabase(paths.DatabaseFile));
        services.AddSingleton<DatabaseMigrator>();
        services.AddSingleton<IMediaRepository, MediaRepository>();
        services.AddSingleton<IDestinationScanner, DestinationScanner>();
        services.AddSingleton<DestinationIndexService>();
        return services;
    }
}
