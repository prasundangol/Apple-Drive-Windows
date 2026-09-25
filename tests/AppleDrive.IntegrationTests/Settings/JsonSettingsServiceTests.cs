using AppleDrive.Application.Settings;
using AppleDrive.Domain.Enums;
using AppleDrive.Infrastructure.FileSystem;
using AppleDrive.Infrastructure.Settings;
using AppleDrive.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppleDrive.IntegrationTests.Settings;

public sealed class JsonSettingsServiceTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();
    private readonly AppPaths _paths;

    public JsonSettingsServiceTests() => _paths = new AppPaths(_directory.Path);

    [Fact]
    public void Uses_safe_defaults_when_no_file_exists()
    {
        var settings = CreateService().Current;

        Assert.True(settings.SkipExactDuplicates);
        Assert.False(settings.HasCompletedOnboarding);
        Assert.Null(settings.DestinationFolder);
        Assert.Equal(FolderOrganization.YearMonth, settings.Organization);
    }

    [Fact]
    public async Task Persists_changes_across_instances()
    {
        var first = CreateService();
        AppSettings? raised = null;
        first.SettingsChanged += (_, value) => raised = value;

        await first.UpdateAsync(s => s with { DestinationFolder = @"D:\Photos\iPhone", Organization = FolderOrganization.YearMonthDay }, TestContext.Current.CancellationToken);

        Assert.NotNull(raised);
        var reloaded = CreateService().Current;
        Assert.Equal(@"D:\Photos\iPhone", reloaded.DestinationFolder);
        Assert.Equal(FolderOrganization.YearMonthDay, reloaded.Organization);
        Assert.False(File.Exists(_paths.SettingsFile + ".tmp"));
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults_and_is_preserved()
    {
        File.WriteAllText(_paths.SettingsFile, "{ not json");

        var settings = CreateService().Current;

        Assert.True(settings.SkipExactDuplicates);
        Assert.True(File.Exists(_paths.SettingsFile + ".unreadable"));
    }

    [Fact]
    public void Reads_enums_written_as_strings()
    {
        File.WriteAllText(_paths.SettingsFile, """{ "Version": 1, "Organization": "Flat", "LogLevel": "Debug" }""");

        var settings = CreateService().Current;

        Assert.Equal(FolderOrganization.Flat, settings.Organization);
        Assert.Equal(DiagnosticLogLevel.Debug, settings.LogLevel);
    }

    private JsonSettingsService CreateService() => new(_paths, NullLogger<JsonSettingsService>.Instance);

    public void Dispose() => _directory.Dispose();
}
