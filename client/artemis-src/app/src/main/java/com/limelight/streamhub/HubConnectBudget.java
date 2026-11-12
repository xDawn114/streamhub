package com.limelight.streamhub;

import java.io.IOException;
import java.util.concurrent.CancellationException;
import java.util.concurrent.TimeUnit;
import java.util.function.BooleanSupplier;

/** One monotonic deadline shared by discovery, VPN startup and remote discovery. */
public final class HubConnectBudget {
    private final long until;
    private final BooleanSupplier cancelled;
    public HubConnectBudget(long milliseconds, BooleanSupplier cancelled) {
        until = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(milliseconds); this.cancelled = cancelled;
    }
    public void check() throws IOException {
        if (cancelled.getAsBoolean() || Thread.currentThread().isInterrupted()) throw new CancellationException("已取消连接。");
        if (System.nanoTime() >= until) throw new IOException("连接检测超时，请重试或查看连接诊断。");
    }
    public long remaining(long maximumMs) throws IOException {
        check(); return Math.max(1, Math.min(maximumMs, TimeUnit.NANOSECONDS.toMillis(until - System.nanoTime())));
    }
}
