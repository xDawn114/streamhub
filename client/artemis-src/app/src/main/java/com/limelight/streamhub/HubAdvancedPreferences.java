package com.limelight.streamhub;

import android.content.Context;
import android.content.SharedPreferences;
import com.limelight.profiles.ProfilesManager;

/** Writes the same active preference store used by the next stream. No live reconfiguration. */
final class HubAdvancedPreferences {
    private HubAdvancedPreferences() { }

    static SharedPreferences active(Context context) {
        return ProfilesManager.getInstance().getOverlayingSharedPreferences(context);
    }

    static boolean save(Context context, java.util.Map<String, Object> values) {
        ProfilesManager manager = ProfilesManager.getInstance();
        com.limelight.profiles.SettingsProfile profile = manager.getActive();
        if (profile != null) {
            java.util.Map<String, Object> previous = profile.getOptions();
            java.util.Map<String, Object> next = new java.util.LinkedHashMap<>();
            if (previous != null) next.putAll(previous);
            next.putAll(values); profile.setOptions(next);
            if (manager.save(context)) return true;
            profile.setOptions(previous);
        } else {
            SharedPreferences prefs = androidx.preference.PreferenceManager.getDefaultSharedPreferences(context);
            java.util.Map<String, ?> previous = prefs.getAll();
            SharedPreferences.Editor edit = prefs.edit();
            for (java.util.Map.Entry<String, Object> item : values.entrySet()) {
                put(edit,item.getKey(),item.getValue());
            }
            if (edit.commit()) return true;
            // commit() also updates the in-memory view when disk persistence fails.
            // Restore it once so the next read cannot claim the rejected choice.
            SharedPreferences.Editor rollback = prefs.edit();
            for (String key : values.keySet()) put(rollback,key,previous.get(key));
            rollback.commit();
        }
        android.widget.Toast.makeText(context, "设置未保存，请重试", android.widget.Toast.LENGTH_LONG).show();
        return false;
    }
    private static void put(SharedPreferences.Editor edit,String key,Object value) {
        if(value==null)edit.remove(key);
        else if(value instanceof Boolean)edit.putBoolean(key,(Boolean)value);
        else if(value instanceof Integer)edit.putInt(key,(Integer)value);
        else edit.putString(key,(String)value);
    }

    static String summary(Context context) {
        SharedPreferences prefs = active(context);
        // Rendering a settings row must not initialize the JNI streaming engine.
        String fps = prefs.getString("list_fps", "60");
        String codec = prefs.getString("video_format", "auto");
        codec = "neverh265".equals(codec) ? "H.264" : "forceh265".equals(codec) ? "HEVC" : "forceav1".equals(codec) ? "AV1" : "自动编码";
        String bitrate = prefs.contains("seekbar_bitrate_kbps")
                ? String.format(java.util.Locale.ROOT, "%.1f Mbps", prefs.getInt("seekbar_bitrate_kbps", 18000) / 1000d)
                : prefs.contains("seekbar_bitrate") ? prefs.getInt("seekbar_bitrate", 18) + " Mbps" : "自动码率";
        return fps + " FPS / " + bitrate + " / " + codec;
    }
}
