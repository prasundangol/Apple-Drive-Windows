using AppleDrive.Presentation.Services;
using Microsoft.UI.Dispatching;

namespace AppleDrive.App.Services;

internal sealed class UiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public bool HasThreadAccess => queue.HasThreadAccess;

    public void Post(Action action)
    {
        if (!queue.TryEnqueue(() => action()))
        {
            // The queue only refuses work once the window is shutting down.
            Serilog.Log.Debug("UI dispatcher rejected work item during shutdown");
        }
    }
}
