package com.limelight.streamhub;

/** Host playback and Android playback are independent ends of a stream. */
public final class HubAudioPolicy {
    public static final int TABLET_ONLY = 0, COMPUTER_ONLY = 1, BOTH = 2;
    private HubAudioPolicy() { }

    public static int outputMode(String saved) {
        if ("tablet".equals(saved)) return TABLET_ONLY;
        if ("both".equals(saved)) return BOTH;
        return COMPUTER_ONLY;
    }

    /** NvHTTP serializes this as localAudioPlayMode: local means the Windows host. */
    public static boolean playOnHost(String sessionMode, int outputMode) {
        return !RtStreamSession.REMOTE.equals(sessionMode) || outputMode != TABLET_ONLY;
    }

    public static boolean playOnAndroid(String sessionMode, int outputMode) {
        return RtStreamSession.REMOTE.equals(sessionMode)
                && (outputMode == TABLET_ONLY || outputMode == BOTH);
    }
}
