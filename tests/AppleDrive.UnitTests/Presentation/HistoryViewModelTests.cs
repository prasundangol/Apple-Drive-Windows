using AppleDrive.Application.Interfaces;
using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Enums;
using AppleDrive.Presentation.Resources;
using AppleDrive.Presentation.ViewModels;
using AppleDrive.Testing;

namespace AppleDrive.UnitTests.Presentation;

public sealed class HistoryViewModelTests
{
    private readonly InMemoryTransferRepository _history = new();
    private readonly FakeShellServices _shell = new();

    [Fact]
    public async Task Empty_history_shows_the_empty_state()
    {
        var viewModel = new HistoryViewModel(_history, _shell);

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasNoSessions);
        Assert.False(viewModel.HasSessions);
        Assert.Null(viewModel.SelectedSession);
    }

    [Fact]
    public async Task Lists_runs_newest_first_and_selects_the_latest()
    {
        await SessionAsync("old", DateTimeOffset.UtcNow.AddDays(-3), TransferSessionStatus.Completed, transferred: 5);
        await SessionAsync("new", DateTimeOffset.UtcNow.AddHours(-1), TransferSessionStatus.Stopped, transferred: 2, failed: 1);
        var viewModel = new HistoryViewModel(_history, _shell);

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal(["new", "old"], viewModel.Sessions.Select(s => s.Session.Id));
        Assert.Equal("new", viewModel.SelectedSession!.Session.Id);
        Assert.Equal(Strings.SessionStopped, viewModel.Sessions[0].StatusText);
        Assert.Equal(Strings.Format(Strings.SessionSummaryFormat, 2, 0, 1, "1 KB"), viewModel.Sessions[0].SummaryText);
    }

    [Fact]
    public async Task Selected_run_lists_its_files_failures_first_with_reasons()
    {
        await SessionAsync("s1", DateTimeOffset.UtcNow, TransferSessionStatus.Completed, transferred: 1, failed: 1);
        var copied = await FileAsync("s1", "IMG_1.HEIC", TransferStatus.Completed, destination: @"D:\Photos\IMG_1.HEIC");
        await FileAsync("s1", "IMG_2.HEIC", TransferStatus.Failed, errorKind: "DeviceIo");
        await FileAsync("s1", "IMG_3.HEIC", TransferStatus.Interrupted);
        var viewModel = new HistoryViewModel(_history, _shell);

        await viewModel.LoadCommand.ExecuteAsync(null);
        await WaitForFilesAsync(viewModel, 3);

        Assert.Equal(["IMG_2.HEIC", "IMG_3.HEIC", "IMG_1.HEIC"], viewModel.Files.Select(f => f.FileName));
        Assert.Equal(Strings.ErrorDeviceIo, viewModel.Files[0].DetailText);
        Assert.Equal(Strings.FileInterruptedReason, viewModel.Files[1].DetailText);
        Assert.Equal(@"D:\Photos\IMG_1.HEIC", viewModel.Files[2].DetailText);
        Assert.Equal(Strings.Format(Strings.SessionFilesFormat, 3, 2), viewModel.FilesSummary);
        Assert.NotEqual(0, copied);
    }

    [Fact]
    public async Task Unknown_error_kinds_get_a_generic_message()
    {
        await SessionAsync("s1", DateTimeOffset.UtcNow, TransferSessionStatus.Completed, failed: 1);
        await FileAsync("s1", "IMG_1.HEIC", TransferStatus.Failed, errorKind: "SomethingNew");
        var viewModel = new HistoryViewModel(_history, _shell);

        await viewModel.LoadCommand.ExecuteAsync(null);
        await WaitForFilesAsync(viewModel, 1);

        Assert.Equal(Strings.ErrorGeneric, viewModel.Files[0].DetailText);
    }

    [Fact]
    public async Task Opens_the_destination_of_the_selected_run()
    {
        await SessionAsync("s1", DateTimeOffset.UtcNow, TransferSessionStatus.Completed);
        var viewModel = new HistoryViewModel(_history, _shell);
        await viewModel.LoadCommand.ExecuteAsync(null);

        await viewModel.OpenDestinationCommand.ExecuteAsync(null);

        Assert.Equal(@"D:\Photos", Assert.Single(_shell.OpenedFolders));
    }

    [Fact]
    public async Task Reloading_keeps_the_selection()
    {
        await SessionAsync("a", DateTimeOffset.UtcNow.AddDays(-1), TransferSessionStatus.Completed);
        await SessionAsync("b", DateTimeOffset.UtcNow, TransferSessionStatus.Completed);
        var viewModel = new HistoryViewModel(_history, _shell);
        await viewModel.LoadCommand.ExecuteAsync(null);
        viewModel.SelectedSession = viewModel.Sessions.Single(s => s.Session.Id == "a");

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal("a", viewModel.SelectedSession!.Session.Id);
    }

    private Task SessionAsync(string id, DateTimeOffset started, TransferSessionStatus status, int transferred = 0, int failed = 0) =>
        _history.CreateSessionAsync(
            new TransferSessionRecord
            {
                Id = id,
                DeviceName = "Test iPhone",
                DestinationRoot = @"D:\Photos",
                StartedAt = started,
                Status = status,
                TransferredCount = transferred,
                FailedCount = failed,
                TransferredBytes = 1_000,
            },
            CancellationToken.None);

    private async Task<long> FileAsync(string session, string name, TransferStatus status, string? destination = null, string? errorKind = null)
    {
        var id = await _history.AddAsync(
            new TransferRecord { SessionId = session, SourceAssetId = name, SourceFileName = name, StartedAt = DateTimeOffset.UtcNow, Status = TransferStatus.InProgress },
            CancellationToken.None);
        await _history.CompleteAsync(id, new TransferOutcomeRecord(status, DateTimeOffset.UtcNow, destination, ErrorKind: errorKind), CancellationToken.None);
        return id;
    }

    /// <summary>Files load in the background after the selection changes.</summary>
    private static async Task WaitForFilesAsync(HistoryViewModel viewModel, int count)
    {
        for (var attempt = 0; attempt < 100 && viewModel.Files.Count != count; attempt++)
        {
            await Task.Delay(10);
        }
    }
}
