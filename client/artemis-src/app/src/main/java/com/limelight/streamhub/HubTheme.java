package com.limelight.streamhub;

import android.content.Context;
import android.content.SharedPreferences;
import android.content.res.Configuration;

import com.limelight.R;

/** Resolves the Hub surface palette without changing the always-dark stream surface. */
public final class HubTheme {
    public static final int SYSTEM = 0;
    public static final int LIGHT = 1;
    public static final int DARK = 2;
    private static final String PREFS = "streamhub-ui";
    private static final String KEY_MODE = "themeMode";

    private HubTheme() { }

    public static int mode(Context context) {
        return context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getInt(KEY_MODE, SYSTEM);
    }

    public static void setMode(Context context, int mode) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
                .putInt(KEY_MODE, mode < SYSTEM || mode > DARK ? SYSTEM : mode).apply();
    }

    public static boolean isDark(Context context) {
        int mode = mode(context);
        if (mode == DARK) return true;
        if (mode == LIGHT) return false;
        return (context.getResources().getConfiguration().uiMode & Configuration.UI_MODE_NIGHT_MASK)
                == Configuration.UI_MODE_NIGHT_YES;
    }

    /** Select an explicit settings theme so manual mode overrides reach the preference activity. */
    public static int settingsTheme(Context context) {
        return isDark(context) ? R.style.HubSettingsThemeDark : R.style.HubSettingsThemeLight;
    }

    public static int color(Context context, int base, int light, int dark) {
        int mode = mode(context);
        if (mode == LIGHT) return context.getColor(light);
        if (mode == DARK) return context.getColor(dark);
        return context.getColor(base);
    }

    public static int background(Context context) { return color(context, R.color.hub_background, R.color.hub_background_light, R.color.hub_background_dark); }
    public static int surface(Context context) { return color(context, R.color.hub_surface, R.color.hub_surface_light, R.color.hub_surface_dark); }
    public static int field(Context context) { return color(context, R.color.hub_field, R.color.hub_field_light, R.color.hub_field_dark); }
    public static int border(Context context) { return color(context, R.color.hub_border, R.color.hub_border_light, R.color.hub_border_dark); }
    public static int text(Context context) { return color(context, R.color.hub_text, R.color.hub_text_light, R.color.hub_text_dark); }
    public static int muted(Context context) { return color(context, R.color.hub_muted, R.color.hub_muted_light, R.color.hub_muted_dark); }
    public static int primary(Context context) { return color(context, R.color.hub_primary, R.color.hub_primary_light, R.color.hub_primary_dark); }
    public static int accent(Context context) { return color(context, R.color.hub_accent, R.color.hub_accent_light, R.color.hub_accent_dark); }
    public static int selected(Context context) { return color(context, R.color.hub_selected, R.color.hub_selected_light, R.color.hub_selected_dark); }
    public static int success(Context context) { return color(context, R.color.hub_success, R.color.hub_success_light, R.color.hub_success_dark); }
    public static int warning(Context context) { return color(context, R.color.hub_warning, R.color.hub_warning_light, R.color.hub_warning_dark); }
    public static int danger(Context context) { return color(context, R.color.hub_danger, R.color.hub_danger_light, R.color.hub_danger_dark); }
    public static int onAccent(Context context) { return color(context, R.color.hub_on_accent, R.color.hub_on_accent_light, R.color.hub_on_accent_dark); }
    public static int ripple(Context context) { return color(context, R.color.hub_ripple, R.color.hub_ripple_light, R.color.hub_ripple_dark); }
}
