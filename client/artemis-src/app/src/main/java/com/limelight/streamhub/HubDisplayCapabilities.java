package com.limelight.streamhub;

import android.content.Context;
import android.graphics.Point;
import android.media.MediaCodecInfo;
import android.media.MediaCodecList;
import android.os.Build;
import android.view.Display;
import android.view.WindowManager;
import org.json.JSONArray;
import org.json.JSONObject;

/** Physical panel capabilities, independent of density and the current power-saving refresh rate. */
public final class HubDisplayCapabilities {
    private HubDisplayCapabilities() { }
    public static int currentOrientation(Context context) {
        Display display = ((WindowManager) context.getSystemService(Context.WINDOW_SERVICE)).getDefaultDisplay();
        Point size = new Point(); display.getRealSize(size);
        boolean sideways = display.getRotation() == android.view.Surface.ROTATION_90 || display.getRotation() == android.view.Surface.ROTATION_270;
        return HubRotationMonitor.toWindowsDegrees(sideways ? size.y > size.x : size.x > size.y, display.getRotation());
    }
    public static JSONObject describe(Context context, int orientation) throws Exception {
        return describe(context, orientation, "usb");
    }

    /**
     * Describe the panel for the selected transport.  Wireless second-screen
     * sessions use the same frame-rate ceiling as the video request. USB retains
     * up to 90 Hz; lower explicit refresh rates remain respected.
     */
    public static JSONObject describe(Context context, int orientation, String route) throws Exception {
        return describe(context, orientation, route, true);
    }

    /** Native panel size must remain selectable after a smaller quality preset was applied. */
    public static JSONObject describeNative(Context context, int orientation) throws Exception {
        return describe(context, orientation, "usb", false);
    }

    private static JSONObject describe(Context context, int orientation, String route, boolean applyPreset) throws Exception {
        Display display = ((WindowManager) context.getSystemService(Context.WINDOW_SERVICE)).getDefaultDisplay();
        Point point = new Point(); display.getRealSize(point);
        int longSide = Math.max(point.x, point.y), shortSide = Math.min(point.x, point.y);
        float maximum = display.getRefreshRate();
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            Display.Mode[] modes = display.getSupportedModes();
            if (modes.length > 0) {
                longSide = Math.max(modes[0].getPhysicalWidth(), modes[0].getPhysicalHeight());
                shortSide = Math.min(modes[0].getPhysicalWidth(), modes[0].getPhysicalHeight());
            }
            for (Display.Mode mode : modes) {
                int a = Math.max(mode.getPhysicalWidth(), mode.getPhysicalHeight());
                int b = Math.min(mode.getPhysicalWidth(), mode.getPhysicalHeight());
                if ((long)a * b > (long)longSide * shortSide) { longSide = a; shortSide = b; }
            }
            // A faster mode at a lower resolution does not describe native-panel capability.
            float nativeMaximum = 0;
            for (Display.Mode mode : modes) {
                int a = Math.max(mode.getPhysicalWidth(), mode.getPhysicalHeight());
                int b = Math.min(mode.getPhysicalWidth(), mode.getPhysicalHeight());
                if (a == longSide && b == shortSide) nativeMaximum = Math.max(nativeMaximum, mode.getRefreshRate());
            }
            if (nativeMaximum > 0) maximum = nativeMaximum;
        }
        float requested = Math.min(90, maximum);
        // This product opens the global StreamSettings entry point, which edits
        // the base preferences. A legacy active game profile must not silently
        // replace the FPS selected in that visible settings page.
        android.content.SharedPreferences prefs = androidx.preference.PreferenceManager.getDefaultSharedPreferences(context);
        // list_fps is the selected entry, including a selected custom numeric value.
        // custom_refresh_rate only remembers the editor's last input.
        if (prefs.contains("list_fps")) {
            try {
                float selected = Float.parseFloat(prefs.getString("list_fps", "90"));
                if (Float.isFinite(selected) && selected > 0) requested = Math.min(requested, selected);
            }
            catch (NumberFormatException ignored) { }
        }
        String preset = new HubStore(context).get("qualityPreset", "advanced");
        if (applyPreset && !"advanced".equals(preset)) {
            int maxEdge = "smooth".equals(preset) ? 1280 : "quality".equals(preset) ? 2560 : 1920;
            float scale = Math.min(1f, maxEdge / (float)longSide);
            longSide = Math.max(2, Math.round(longSide * scale) / 2 * 2);
            shortSide = Math.max(2, Math.round(shortSide * scale) / 2 * 2);
            requested = Math.min(maximum, "smooth".equals(preset) ? 30 : "quality".equals(preset) && "usb".equals(route) ? 90 : 60);
        }
        requested = HubTransportPolicy.limitFps(Math.round(requested), route);
        boolean portrait = orientation == 90 || orientation == 270;
        return new JSONObject().put("width", portrait ? shortSide : longSide)
                .put("height", portrait ? longSide : shortSide)
                .put("refreshRate", Math.max(30, Math.round(requested)));
    }
    public static JSONArray codecs() {
        JSONArray result = new JSONArray(); boolean hevc = false, av1 = false;
        try {
            for (MediaCodecInfo codec : new MediaCodecList(MediaCodecList.REGULAR_CODECS).getCodecInfos()) {
                if (codec.isEncoder()) continue;
                for (String type : codec.getSupportedTypes()) {
                    hevc |= "video/hevc".equalsIgnoreCase(type); av1 |= "video/av01".equalsIgnoreCase(type);
                }
            }
        } catch (RuntimeException ignored) { }
        if (av1) result.put("av1"); if (hevc) result.put("hevc"); return result.put("h264");
    }
}
