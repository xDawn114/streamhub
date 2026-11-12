namespace StreamHub.Host.Runtime;

/// <summary>Use the same engine state terms in the header and individual cards.</summary>
public static class SessionStatusPresentation
{
    public static string Engine(EngineStatus? engine) => engine is null ? "后台未连接" :
        engine.DesktopCleanupPending ? "正在回收副屏" :
        engine.Streaming && !engine.SessionVerified ? "正在核实会话" :
        engine.Streaming ? "正在串流" :
        engine.Healthy ? "已就绪" :
        engine.Running ? "正在初始化" : engine.Desired ? "正在恢复" : "已停止";

    public static string Header(RuntimeStatus? status)
    {
        if (status is null) return "后台未连接";
        var engines = new[] { status.Remote, status.Tablet };
        if (engines.Any(engine => engine.Streaming && !engine.SessionVerified)) return "正在核实会话";
        if (engines.Any(engine => engine.Streaming)) return "正在串流";
        if (engines.Any(engine => engine.DesktopCleanupPending)) return "正在回收副屏";
        if (engines.Any(engine => engine.Healthy)) return "已就绪";
        if (engines.Any(engine => engine.Running)) return "正在初始化";
        return engines.Any(engine => engine.Desired) ? "正在恢复" : "已停止";
    }
}
