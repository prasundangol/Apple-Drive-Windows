using AppleDrive.Presentation.Services;

namespace AppleDrive.Testing;

public sealed class FakeShellServices : IShellServices
{
    /// <summary>Returned by the next folder picker call; <c>null</c> simulates cancelling.</summary>
    public string? NextPickedFolder { get; set; }

    public List<string> OpenedFolders { get; } = [];

    public Task<string?> PickFolderAsync() => Task.FromResult(NextPickedFolder);

    public Task OpenFolderAsync(string path)
    {
        OpenedFolders.Add(path);
        return Task.CompletedTask;
    }

    /// <summary>Answer given to confirmation dialogs.</summary>
    public bool ConfirmResult { get; set; } = true;

    public List<ConfirmationRequest> Confirmations { get; } = [];

    public Task<bool> ConfirmAsync(ConfirmationRequest request)
    {
        Confirmations.Add(request);
        return Task.FromResult(ConfirmResult);
    }
}
