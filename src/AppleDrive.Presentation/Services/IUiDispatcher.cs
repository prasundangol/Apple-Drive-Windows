namespace AppleDrive.Presentation.Services;

/// <summary>Runs work on the UI thread. Implemented by the app over the WinUI dispatcher queue.</summary>
public interface IUiDispatcher
{
    /// <summary>True when called on the UI thread.</summary>
    bool HasThreadAccess { get; }

    /// <summary>Queues <paramref name="action"/> to run on the UI thread.</summary>
    void Post(Action action);
}
