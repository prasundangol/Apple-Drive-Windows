namespace AppleDrive.Application.Services;

/// <summary>
/// Transfer speed over a sliding window, and a remaining-time estimate that is offered only once
/// the speed has had time to settle. A cold start or a single large video would otherwise make
/// the estimate jump around.
/// </summary>
public sealed class TransferRateEstimator
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinimumForSpeed = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinimumForEstimate = TimeSpan.FromSeconds(5);

    private readonly Queue<(TimeSpan At, long Bytes)> _samples = new();

    /// <summary>Records the running byte count at <paramref name="elapsed"/> since the transfer started.</summary>
    public void Add(TimeSpan elapsed, long totalBytes)
    {
        _samples.Enqueue((elapsed, totalBytes));
        while (_samples.Count > 2 && elapsed - _samples.Peek().At > Window)
        {
            _samples.Dequeue();
        }
    }

    /// <summary>Bytes per second over the recent window, or <c>null</c> until there is enough data.</summary>
    public double? BytesPerSecond
    {
        get
        {
            if (_samples.Count < 2)
            {
                return null;
            }

            var first = _samples.Peek();
            var last = _samples.Last();
            var span = last.At - first.At;
            return span < MinimumForSpeed ? null : (last.Bytes - first.Bytes) / span.TotalSeconds;
        }
    }

    /// <summary>Time left for <paramref name="remainingBytes"/>, or <c>null</c> while the speed is not yet reliable.</summary>
    public TimeSpan? EstimateRemaining(long remainingBytes)
    {
        if (_samples.Count < 2 || _samples.Last().At < MinimumForEstimate || BytesPerSecond is not { } rate || rate <= 0)
        {
            return null;
        }

        return TimeSpan.FromSeconds(Math.Max(0, remainingBytes) / rate);
    }
}
