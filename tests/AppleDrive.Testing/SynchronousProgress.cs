namespace AppleDrive.Testing;

/// <summary><see cref="IProgress{T}"/> that reports inline, so tests see every value deterministically.</summary>
public sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
