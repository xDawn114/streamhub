package com.limelight;

import android.accessibilityservice.AccessibilityService;
import android.view.KeyEvent;
import android.view.accessibility.AccessibilityEvent;
import com.limelight.streamhub.HubSystemShortcuts;

/** Optional foreground bridge for Alt+Tab and Windows keys. Ordinary keys pass through. */
public final class KeyboardAccessibilityService extends AccessibilityService {
    @Override protected boolean onKeyEvent(KeyEvent event) { return HubSystemShortcuts.dispatch(this, event); }
    @Override public void onAccessibilityEvent(AccessibilityEvent event) { }
    @Override public void onInterrupt() { HubSystemShortcuts.reset(); }
    @Override public void onDestroy() { HubSystemShortcuts.reset(); super.onDestroy(); }
}
