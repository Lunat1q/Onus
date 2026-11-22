namespace Onus.Core;

/// <summary>Enforces a minimum gap between downloads, shared by all parallel workers.</summary>
public sealed class RateLimiter(TimeSpan interval)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _next = DateTime.MinValue;

    public async Task WaitAsync(CancellationToken ct)
    {
        if (interval <= TimeSpan.Zero) return;
        await _gate.WaitAsync(ct);
        try
        {
            var delay = _next - DateTime.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            _next = DateTime.UtcNow + interval;
        }
        finally { _gate.Release(); }
    }
}
