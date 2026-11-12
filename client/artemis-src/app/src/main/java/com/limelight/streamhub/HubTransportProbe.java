package com.limelight.streamhub;

import java.io.IOException;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CancellationException;
import java.util.function.BooleanSupplier;

/** Race within a route, then fall back USB -> LAN -> remote inside one deadline. */
public final class HubTransportProbe {
    private HubTransportProbe() { }
    public static <T> HubProbeRace.Match<T> first(List<String> hosts, HubProbeRace.Probe<T> probe,
            BooleanSupplier cancelled, long maximumMs) throws Exception {
        return first(hosts, probe, cancelled, maximumMs, HubRoutes::type);
    }
    static <T> HubProbeRace.Match<T> first(List<String> hosts, HubProbeRace.Probe<T> probe,
            BooleanSupplier cancelled, long maximumMs, java.util.function.Function<String, String> routeType) throws Exception {
        List<List<String>> groups = new ArrayList<>();
        for (String type : new String[]{"usb", "lan", "remote"}) {
            List<String> group = new ArrayList<>();
            for (String host : hosts) if (type.equals(routeType.apply(host))) group.add(host);
            if (!group.isEmpty()) groups.add(group);
        }
        if (groups.isEmpty()) throw new IOException("当前通道没有可用地址，请检查通道设置和电脑地址。");
        long deadline = System.nanoTime() + maximumMs * 1000000L;
        IOException failure = new IOException("当前通道的连接地址均未通过验证。");
        for (int i = 0; i < groups.size(); i++) {
            if (cancelled.getAsBoolean() || Thread.currentThread().isInterrupted()) throw new CancellationException("已取消连接。");
            long remaining = (deadline - System.nanoTime()) / 1000000L;
            if (remaining <= 0) break;
            long slice = Math.max(1, remaining / (groups.size() - i));
            try { return HubProbeRace.first(groups.get(i), probe, cancelled, slice); }
            catch (CancellationException error) { throw error; }
            catch (Exception error) { failure.addSuppressed(error); }
        }
        throw failure;
    }
}
