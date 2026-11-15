package com.limelight.streamhub;

import android.content.Context;
import com.limelight.preferences.PreferenceConfiguration;

/** Use the same preference/default calculation as Game. HDR keeps full probing. */
final class HubEncoderProbe {
    private HubEncoderProbe() { }
    static String forSession(Context context, boolean remote, String route) {
        PreferenceConfiguration preferences = PreferenceConfiguration.readPreferences(context);
        android.view.Display display = ((android.view.WindowManager) context.getSystemService(Context.WINDOW_SERVICE)).getDefaultDisplay();
        PreferenceConfiguration.applyStreamHubDefaults(context, preferences, display, remote, route);
        return plan(preferences.videoFormat, preferences.enableHdr);
    }
    static String plan(PreferenceConfiguration.FormatOption format, boolean hdr) {
        if (hdr) return "full";
        switch (format) {
            case FORCE_H264: return "sdr-h264";
            case FORCE_HEVC: return "sdr-hevc";
            case FORCE_AV1: return "sdr-av1";
            default: return "sdr-auto";
        }
    }
}
