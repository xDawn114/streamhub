package com.limelight.streamhub;

/** Catch fast jobs promptly, then keep sustained control traffic bounded. */
final class HubJobPollPolicy {
    private HubJobPollPolicy() { }

    static long delayAfterPending(int pendingPolls) {
        return pendingPolls <= 4 ? 150 : 500;
    }
}
