using AppleDrive.Presentation.Services;

namespace AppleDrive.Testing;

/// <summary>Runs UI work immediately on the calling thread.</summary>
public sealed class InlineUiDispatcher : IUiDispatcher
{
    public bool HasThreadAccess => true;

    public void Post(Action action) => action();
}
