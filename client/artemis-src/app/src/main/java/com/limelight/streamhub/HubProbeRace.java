package com.limelight.streamhub;

import java.io.IOException;
import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.concurrent.CancellationException;
import java.util.concurrent.ExecutorCompletionService;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.function.BooleanSupplier;

/** Bounded discovery only: preparation and pairing never race across routes. */
public final class HubProbeRace {
    public interface Probe<T> { T check(String host) throws Exception; }
    public static final class Match<T> {
        public final String host;
        public final T value;
        Match(String host, T value) { this.host = host; this.value = value; }
    }
    public static final class ProbeFailure extends IOException {
        private ProbeFailure(String message, List<Exception> failures) {
            super(message);
            for (Exception failure : failures) addSuppressed(failure);
        }
    }
    private HubProbeRace() { }
    public static <T> Match<T> first(List<String> hosts, Probe<T> probe,
                                    BooleanSupplier cancelled, long budgetMs) throws Exception {
        List<String> unique = new ArrayList<>(new LinkedHashSet<>(hosts));
        if (unique.isEmpty()) throw new IOException("没有可用的连接地址。");
        int count = Math.min(8, unique.size()), parallel = Math.min(3, count);
        long until = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(Math.max(1, budgetMs));
        ExecutorService workers = Executors.newFixedThreadPool(parallel, task -> {
            Thread thread = new Thread(task, "Hub-AddressProbe"); thread.setDaemon(true); return thread;
        });
        ExecutorCompletionService<Match<T>> results = new ExecutorCompletionService<>(workers);
        List<Future<Match<T>>> pending = new ArrayList<>();
        List<Exception> failures = new ArrayList<>();
        int submitted = 0, completed = 0;
        try {
            while (completed < count) {
                if (cancelled.getAsBoolean() || Thread.currentThread().isInterrupted()) throw new CancellationException("已取消连接。");
                long remaining = until - System.nanoTime();
                if (remaining <= 0) throw new ProbeFailure("地址检测超时，请检查电脑在线状态后重试。", failures);
                while (submitted < count && submitted - completed < parallel) {
                    final String host = unique.get(submitted++);
                    pending.add(results.submit(() -> {
                        if (cancelled.getAsBoolean()) throw new CancellationException("已取消连接。");
                        return new Match<>(host, probe.check(host));
                    }));
                }
                Future<Match<T>> result = results.poll(Math.min(remaining, TimeUnit.MILLISECONDS.toNanos(50)), TimeUnit.NANOSECONDS);
                if (result == null) continue;
                completed++;
                try {
                    Match<T> match = result.get();
                    if (cancelled.getAsBoolean() || System.nanoTime() >= until) continue;
                    return match;
                } catch (java.util.concurrent.ExecutionException failure) {
                    Throwable cause = failure.getCause();
                    if (cause instanceof CancellationException) throw (CancellationException)cause;
                    failures.add(cause instanceof Exception ? (Exception)cause : new IOException("地址检测失败。", cause));
                }
            }
            throw new ProbeFailure("电脑暂不可达，请检查连接后重试。", failures);
        } finally {
            for (Future<?> task : pending) task.cancel(true);
            workers.shutdownNow();
        }
    }
}
