package com.limelight.streamhub;

/** One bounded recovery window per period without confirmed file progress. */
public final class HubFileRetryWindow {
    private static final long WAIT_WINDOW_MS = 30000;
    private static final long WAIT_PROBE_MS = 5000;
    private int fastRetries;
    private long waitDeadline;

    public static final class Next {
        public final long delayMs;
        public final int fastRetry;
        private Next(long delayMs, int fastRetry) { this.delayMs = delayMs; this.fastRetry = fastRetry; }
        public boolean waitingForNetwork() { return fastRetry == 0; }
    }

    public Next afterFailure(Exception error, boolean hasRemoteTask, long startOffset, long endOffset, long now) {
        if (!HubFileRetryPolicy.shouldRetry(error, 0, hasRemoteTask)) return null;
        if (endOffset > startOffset) { fastRetries = 0; waitDeadline = 0; }
        if (fastRetries < HubFileRetryPolicy.MAX_RETRIES) {
            long delay = HubFileRetryPolicy.delayMs(fastRetries++);
            return new Next(delay, fastRetries);
        }
        if (!hasRemoteTask) return null;
        if (waitDeadline == 0) waitDeadline = now + WAIT_WINDOW_MS;
        if (now >= waitDeadline || waitDeadline - now < WAIT_PROBE_MS) return null;
        return new Next(WAIT_PROBE_MS, 0);
    }
}
