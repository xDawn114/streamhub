package com.limelight.streamhub;

import java.io.EOFException;
import java.io.IOException;
import java.net.ConnectException;
import java.net.SocketException;
import java.net.SocketTimeoutException;
import java.net.UnknownHostException;
import java.util.ArrayDeque;
import java.util.Collections;
import java.util.IdentityHashMap;
import java.util.Set;
import javax.net.ssl.SSLException;

/** Retries only transient transport failures and never duplicates an unknown upload task. */
public final class HubFileRetryPolicy {
    private HubFileRetryPolicy() { }
    public static final int MAX_RETRIES = 3;
    public static final class HttpFailure extends IOException {
        public final int status;
        public HttpFailure(int status, String message) { super(message); this.status = status; }
    }
    public static final class DiscoveryFailure extends IOException {
        public DiscoveryFailure(IOException cause) { super(cause.getMessage(), cause); }
    }
    public static boolean shouldRetry(Exception error, int retryCount, boolean hasRemoteTask) {
        if (retryCount >= MAX_RETRIES || retryCount < 0) return false;
        boolean transientHttp = false;
        ArrayDeque<Throwable> pending = new ArrayDeque<>();
        Set<Throwable> visited = Collections.newSetFromMap(new IdentityHashMap<>());
        pending.add(error);
        while (!pending.isEmpty()) {
            Throwable cause = pending.removeFirst();
            if (!visited.add(cause)) continue;
            if (cause instanceof SSLException) return false;
            if (cause instanceof HttpFailure) {
                if (!isTransientHttpStatus(((HttpFailure) cause).status)) return false;
                transientHttp = true;
            }
            if (cause.getCause() != null) pending.addLast(cause.getCause());
            for (Throwable suppressed : cause.getSuppressed()) pending.addLast(suppressed);
        }
        if (transientHttp) {
            // Discovery is safe to repeat only for transient server responses;
            // authentication and conflict errors need an explicit user action.
            if (error instanceof DiscoveryFailure) return true;
            if (!hasRemoteTask) return false;
            return true;
        }
        if (error instanceof DiscoveryFailure) return true;
        if (!hasRemoteTask) return false;
        return error instanceof SocketTimeoutException || error instanceof SocketException ||
                error instanceof ConnectException || error instanceof UnknownHostException || error instanceof EOFException;
    }
    private static boolean isTransientHttpStatus(int status) {
        return status == 408 || status == 429 || status == 500 || status == 502 || status == 503 || status == 504;
    }
    public static long delayMs(int retryCount) {
        if (retryCount < 0) throw new IllegalArgumentException("retryCount");
        return Math.min(8000L, 1000L << Math.min(retryCount, 3));
    }
}
