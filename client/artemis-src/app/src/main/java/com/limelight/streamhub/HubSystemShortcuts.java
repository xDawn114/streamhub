package com.limelight.streamhub;

import android.accessibilityservice.AccessibilityServiceInfo;
import android.content.ComponentName;
import android.content.Context;
import android.view.KeyEvent;
import android.view.accessibility.AccessibilityManager;
import java.lang.ref.WeakReference;

/** No activity, key history or window content is retained by the service. */
public final class HubSystemShortcuts {
    public interface Target {
        boolean isActive();
        void switchWindow(boolean reverse);
        default void finishSwitching() {}
        default void showTaskView() {}
        default void windowsKey(boolean right, boolean down) {}
    }
    private static WeakReference<Target> current = new WeakReference<>(null);
    private static final HubSystemShortcutKeys keys = new HubSystemShortcutKeys();
    private HubSystemShortcuts() {}

    public static boolean requested(Context context) {
        return context.getSharedPreferences("hub_shortcuts", Context.MODE_PRIVATE).getBoolean("enabled", false);
    }
    public static void request(Context context, boolean enabled) {
        context.getSharedPreferences("hub_shortcuts", Context.MODE_PRIVATE).edit().putBoolean("enabled", enabled).apply();
        reset();
    }
    public static boolean authorized(Context context) {
        AccessibilityManager manager = (AccessibilityManager) context.getSystemService(Context.ACCESSIBILITY_SERVICE);
        if (manager == null) return false;
        ComponentName expected = new ComponentName(context, com.limelight.KeyboardAccessibilityService.class);
        for (AccessibilityServiceInfo service : manager.getEnabledAccessibilityServiceList(AccessibilityServiceInfo.FEEDBACK_ALL_MASK)) {
            if (expected.equals(ComponentName.unflattenFromString(service.getId()))) return true;
        }
        return false;
    }
    public static void activate(Target target) { reset(); current = new WeakReference<>(target); }
    public static void deactivate(Target target) {
        if (current.get() == target) { reset(); current.clear(); }
    }
    public static void reset() { keys.reset(); Target target=current.get(); if(target!=null)target.finishSwitching(); }
    public static boolean dispatch(Context context, KeyEvent event) {
        Target target = current.get();
        boolean active = requested(context) && target != null && target.isActive();
        return keys.handle(event, active, new HubSystemShortcutKeys.Sender() {
            public void step(boolean reverse) { if(target!=null)target.switchWindow(reverse); }
            public void finish() { if(target!=null)target.finishSwitching(); }
            public void taskView() { if(target!=null)target.showTaskView(); }
            public void windowsKey(boolean right,boolean down) { if(target!=null)target.windowsKey(right,down); }
        });
    }
}
