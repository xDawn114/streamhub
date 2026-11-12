namespace StreamHub.Host.Runtime;

/// <summary>Recover an idle, persistently unhealthy owned engine without interrupting a live lease.</summary>
public sealed class RemoteWarmupPolicy
{
    private DateTimeOffset _unhealthySince;
    public bool ShouldRestart(DateTimeOffset now, DateTimeOffset started, bool desired, bool running, bool healthy, bool activeLease)
    {
        if (!desired || !running || healthy || activeLease || started == default || now < started.AddSeconds(45))
        { _unhealthySince = default; return false; }
        if (_unhealthySince == default || now < _unhealthySince) { _unhealthySince = now; return false; }
        if (now - _unhealthySince < TimeSpan.FromSeconds(30)) return false;
        _unhealthySince = default;
        return true;
    }

    public static bool NeedsHealthWait(string profile, bool initialHealthy) => profile != "remote" || !initialHealthy;
}
