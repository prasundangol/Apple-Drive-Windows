using System.Globalization;
using AppleDrive.Application.Interfaces;
using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Enums;
using AppleDrive.Domain.Results;
using AppleDrive.Presentation.Formatting;
using AppleDrive.Presentation.Resources;
using AppleDrive.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppleDrive.Presentation.ViewModels;

/// <summary>One past transfer run in the history list.</summary>
public sealed class HistorySessionViewModel(TransferSessionRecord session)
{
    public TransferSessionRecord Session { get; } = session;

    public string DateText { get; } = session.StartedAt.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);

    public string DeviceText { get; } = session.DeviceName ?? Strings.UnknownDevice;

    public string Destination { get; } = session.DestinationRoot;

    public string StatusText { get; } = session.Status switch
    {
        TransferSessionStatus.Completed => Strings.SessionCompleted,
        TransferSessionStatus.Cancelled => Strings.SessionCancelled,
        TransferSessionStatus.Stopped => Strings.SessionStopped,
        TransferSessionStatus.Interrupted => Strings.SessionInterrupted,
        _ => Strings.SessionRunning,
    };

    /// <summary>Status is shown as an icon and text, never by colour alone.</summary>
    public string StatusGlyph { get; } = session.Status switch
    {
        TransferSessionStatus.Completed when session.FailedCount == 0 => "",
        TransferSessionStatus.Cancelled => "",
        TransferSessionStatus.Running => "",
        _ => "",
    };

    public string SummaryText { get; } = Strings.Format(
        Strings.SessionSummaryFormat,
        session.TransferredCount,
        session.SkippedCount,
        session.FailedCount,
        ByteSize.Format(session.TransferredBytes));

    public string AccessibleName => $"{DateText}, {StatusText}, {SummaryText}";
}

/// <summary>One file of a past transfer run.</summary>
public sealed class HistoryFileViewModel(TransferRecord record)
{
    public TransferRecord Record { get; } = record;

    public string FileName { get; } = record.SourceFileName;

    public bool IsFailed { get; } = record.Status is TransferStatus.Failed or TransferStatus.Interrupted;

    public string StatusText { get; } = record.Status switch
    {
        TransferStatus.Completed => Strings.FileCopied,
        TransferStatus.Duplicate => Strings.FileAlreadyThere,
        TransferStatus.Failed => Strings.FileFailed,
        TransferStatus.Cancelled => Strings.FileNotCopied,
        TransferStatus.Interrupted => Strings.FileInterrupted,
        _ => Strings.FileInProgress,
    };

    public string StatusGlyph { get; } = record.Status switch
    {
        TransferStatus.Completed => "",
        TransferStatus.Duplicate => "",
        TransferStatus.Failed or TransferStatus.Interrupted => "",
        _ => "",
    };

    /// <summary>Where it went, or why it failed in plain language.</summary>
    public string DetailText { get; } = record.Status switch
    {
        TransferStatus.Failed or TransferStatus.Interrupted => Reason(record),
        _ => record.DestinationPath ?? string.Empty,
    };

    public bool HasDetail => DetailText.Length > 0;

    public string AccessibleName => $"{FileName}, {StatusText}. {DetailText}";

    private static string Reason(TransferRecord record)
    {
        if (record.Status == TransferStatus.Interrupted)
        {
            return Strings.FileInterruptedReason;
        }

        return Enum.TryParse<ErrorKind>(record.ErrorKind, out var kind)
            ? ErrorMessages.For(new AppError(kind, record.ErrorMessage ?? string.Empty))
            : Strings.ErrorGeneric;
    }
}

/// <summary>History page: past transfer runs, and the files of the selected one.</summary>
public sealed partial class HistoryViewModel(ITransferRepository history, IShellServices shell) : ObservableObject
{
    private const int MaxSessions = 200;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSessions), nameof(HasNoSessions))]
    public partial IReadOnlyList<HistorySessionViewModel> Sessions { get; private set; } = [];

    public bool HasSessions => Sessions.Count > 0;

    public bool HasNoSessions => IsLoaded && Sessions.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoSessions))]
    public partial bool IsLoaded { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(OpenDestinationCommand))]
    public partial HistorySessionViewModel? SelectedSession { get; set; }

    public bool HasSelection => SelectedSession is not null;

    /// <summary>The selected run's files, failed ones first.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<HistoryFileViewModel> Files { get; private set; } = [];

    [ObservableProperty]
    public partial string FilesSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; private set; } = string.Empty;

    public bool HasError => ErrorMessage.Length > 0;

    /// <summary>Reloads the list; called whenever the page is shown, so new runs appear.</summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            var selectedId = SelectedSession?.Session.Id;
            Sessions = (await history.GetRecentSessionsAsync(MaxSessions, CancellationToken.None))
                .Select(session => new HistorySessionViewModel(session))
                .ToList();
            ErrorMessage = string.Empty;
            SelectedSession = Sessions.FirstOrDefault(session => session.Session.Id == selectedId) ?? Sessions.FirstOrDefault();
        }
        catch (DatabaseException)
        {
            ErrorMessage = Strings.ErrorDatabase;
        }
        finally
        {
            IsLoaded = true;
        }
    }

    private bool CanOpenDestination() => SelectedSession is not null;

    [RelayCommand(CanExecute = nameof(CanOpenDestination))]
    private Task OpenDestinationAsync() =>
        SelectedSession is { } session ? shell.OpenFolderAsync(session.Destination) : Task.CompletedTask;

    partial void OnSelectedSessionChanged(HistorySessionViewModel? value) => _ = LoadFilesAsync(value);

    private async Task LoadFilesAsync(HistorySessionViewModel? session)
    {
        if (session is null)
        {
            Files = [];
            FilesSummary = string.Empty;
            return;
        }

        try
        {
            var records = await history.GetBySessionAsync(session.Session.Id, CancellationToken.None);
            if (SelectedSession != session)
            {
                return; // Another run was selected meanwhile.
            }

            Files = records
                .Select(record => new HistoryFileViewModel(record))
                .OrderByDescending(file => file.IsFailed)
                .ThenBy(file => file.Record.Id)
                .ToList();
            FilesSummary = Strings.Format(Strings.SessionFilesFormat, Files.Count, Files.Count(file => file.IsFailed));
        }
        catch (DatabaseException)
        {
            ErrorMessage = Strings.ErrorDatabase;
        }
    }
}
