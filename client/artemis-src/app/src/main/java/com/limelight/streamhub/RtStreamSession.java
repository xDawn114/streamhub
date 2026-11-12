package com.limelight.streamhub;

import android.content.Intent;

import com.limelight.Game;
import com.limelight.nvstream.av.audio.AudioRenderer;
import com.limelight.nvstream.jni.MoonBridge;

/** Product session identification; account networking remains a separate component. */
public final class RtStreamSession {
    public static final String EXTRA_MODE = "StreamHubMode";
    public static final String REMOTE = "remote";
    public static final String TABLET = "tablet";
    public static final int REMOTE_PORT = 48989;
    public static final int TABLET_PORT = 49089;

    private RtStreamSession() { }

    public static String mode(Intent intent) {
        String explicit = intent.getStringExtra(EXTRA_MODE);
        if (REMOTE.equals(explicit) || TABLET.equals(explicit)) return explicit;
        int port = intent.getIntExtra(Game.EXTRA_PORT, 0);
        if (port == TABLET_PORT) return TABLET;
        if (port == REMOTE_PORT) return REMOTE;
        String name = intent.getStringExtra(Game.EXTRA_APP_NAME);
        if ("平板副屏".equals(name)) return TABLET;
        if ("远程桌面".equals(name)) return REMOTE;
        return null;
    }

    /** A tablet display session must never start an Android audio output device. */
    public static final class MutedAudioRenderer implements AudioRenderer {
        @Override public int setup(MoonBridge.AudioConfiguration configuration, int sampleRate, int samplesPerFrame) { return 0; }
        @Override public void start() { }
        @Override public void stop() { }
        @Override public void playDecodedAudio(short[] audioData) { }
        @Override public void cleanup() { }
    }
}
