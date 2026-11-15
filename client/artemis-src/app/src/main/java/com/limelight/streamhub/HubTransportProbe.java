package com.limelight.streamhub;

import java.io.IOException;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CancellationException;
import java.util.function.BooleanSupplier;
import java.util.ArrayDeque;
import java.util.Collections;
import java.util.IdentityHashMap;
import java.util.Set;

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
        List<String> details = new ArrayList<>();
        for (int i = 0; i < groups.size(); i++) {
            if (cancelled.getAsBoolean() || Thread.currentThread().isInterrupted()) throw new CancellationException("已取消连接。");
            long remaining = (deadline - System.nanoTime()) / 1000000L;
            if (remaining <= 0) break;
            long slice = Math.max(1, remaining / (groups.size() - i));
            try { return HubProbeRace.first(groups.get(i), probe, cancelled, slice); }
            catch (CancellationException error) { throw error; }
            catch (Exception error) {
                failure.addSuppressed(error);
                String route = routeType.apply(groups.get(i).get(0));
                details.add(("usb".equals(route) ? "USB 有线" : "remote".equals(route) ? "异地网络" : "局域网 / Wi-Fi") + "：" + explanation(error));
            }
        }
        IOException result = new IOException(details.isEmpty() ? "地址检测超时，请确认电脑在线后重试。" : String.join("\n", details));
        for (Throwable error : failure.getSuppressed()) result.addSuppressed(error);
        throw result;
    }
    // Classify bounded evidence, never append raw addresses, upstream bodies or tokens.
    static String explanation(Throwable error) {
        ArrayDeque<Throwable> pending = new ArrayDeque<>();
        Set<Throwable> visited = Collections.newSetFromMap(new IdentityHashMap<>());
        pending.add(error);
        boolean tls = false, denied = false, offline = false, timeout = false;
        while (!pending.isEmpty() && visited.size() < 64) {
            Throwable cause = pending.removeFirst();
            if (!visited.add(cause)) continue;
            tls |= cause instanceof javax.net.ssl.SSLException || cause instanceof java.security.cert.CertificateException;
            denied |= cause instanceof HubFileRetryPolicy.HttpFailure &&
                    (((HubFileRetryPolicy.HttpFailure) cause).status == 401 || ((HubFileRetryPolicy.HttpFailure) cause).status == 403);
            timeout |= cause instanceof java.net.SocketTimeoutException;
            offline |= cause instanceof java.net.ConnectException || cause instanceof java.net.NoRouteToHostException || cause instanceof java.net.UnknownHostException;
            if (cause.getCause() != null) pending.addLast(cause.getCause());
            for (Throwable suppressed : cause.getSuppressed()) pending.addLast(suppressed);
        }
        if (tls) return "电脑身份校验未通过，请在电脑端核对绑定，不能跳过证书校验。";
        if (denied) return "电脑已响应，但此设备未获授权或已被撤销，请检查电脑的设备权限。";
        if (timeout || offline) return "电脑暂未响应；请检查电脑在线状态、网络类型、防火墙和无线隔离。";
        return "未能完成验证，请查看连接诊断；尚不能判断网络或授权原因。";
    }
}
