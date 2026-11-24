namespace WorldEcon.WorldGen.Pipeline;

/// <summary>
/// Estimated time remaining for a run from its progress fraction (design 2026-11-24, section 8a): elapsed × (1 − f) / f,
/// shown once at least 5% is done ("estimating…" before), rounded to whole seconds, and changed at most once per
/// second. The clock starts when the object is created.
/// </summary>
public sealed class RemainingTime(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly DateTimeOffset _start = (clock ?? TimeProvider.System).GetUtcNow();
    private DateTimeOffset? _lastUpdate;
    private string _last = "estimating…";

    public string Text(double fraction)
    {
        var now = _clock.GetUtcNow();
        if (_lastUpdate is { } last && now - last < TimeSpan.FromSeconds(1)) return _last;
        _lastUpdate = now;
        if (fraction < 0.05) return _last = "estimating…";
        var f = Math.Min(1.0, fraction);
        var elapsed = (now - _start).TotalSeconds;
        var remaining = (int)Math.Round(Math.Max(0, elapsed * (1 - f) / f));
        return _last = remaining >= 60 ? $"about {remaining / 60} min {remaining % 60} s remaining" : $"about {remaining} s remaining";
    }
}
