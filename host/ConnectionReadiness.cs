using System.Diagnostics;
using StreamHub.Host.Runtime;

namespace StreamHub.Host;

/// <summary>Tracks one continuous unsuccessful startup, including automatic process retries.</summary>
public sealed class ConnectionReadiness(Func<TimeSpan>? clock = null)
{
    public static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(45);
    private readonly Func<TimeSpan> _clock = clock ?? (() => Stopwatch.GetElapsedTime(0));
    private TimeSpan? _waitingSince;
    private DateTimeOffset? _serviceStartedAt;
    public bool TimedOut { get; private set; }
    public string Observe(RuntimeStatus? status)
    {
        if (status is null || status.ConnectionsPaused || status.Remote.BackendReleasePending || status.Remote.Healthy || !status.Remote.Desired ||
            status.Remote.Streaming || status.Tablet.Streaming)
        {
            _waitingSince = null; TimedOut = false; _serviceStartedAt = status?.StartedAt;
            return status is null ? "后台未连接" : status.ConnectionsPaused ? "已暂停被控"
                : status.Remote.Streaming || status.Tablet.Streaming ? "正在被控"
                : status.Remote.BackendReleasePending ? "正在结束连接"
                : status.Remote.Healthy ? "接受连接" : "远控未就绪";
        }
        if (_serviceStartedAt != status.StartedAt) { _waitingSince = null; _serviceStartedAt = status.StartedAt; }
        _waitingSince ??= _clock();
        TimedOut = _clock() - _waitingSince.Value >= StartupTimeout;
        return TimedOut ? "远控未就绪（引擎未启动）" : "准备中";
    }
}
