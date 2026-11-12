package com.limelight.streamhub;

import java.io.IOException;

/** End-to-start handoff waits for confirmed idle, never a blind fixed delay. */
public final class HubSessionDrain {
    private HubSessionDrain() { }
    public interface Probe { boolean isIdle() throws Exception; }
    public interface Pause { void sleep(long ms) throws Exception; }
    public static void awaitIdle(Probe probe, Pause pause) throws Exception {
        for (int attempt = 0; attempt < 8; attempt++) {
            if (probe.isIdle()) return;
            if (attempt < 7) pause.sleep(500);
        }
        throw new IOException("电脑仍在结束上次会话，请稍后重试。");
    }
}
