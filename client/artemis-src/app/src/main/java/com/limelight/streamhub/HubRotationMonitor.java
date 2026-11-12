package com.limelight.streamhub;

import android.app.Activity;
import android.content.Intent;
import android.content.pm.ActivityInfo;
import android.graphics.Point;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.view.Display;
import android.view.OrientationEventListener;
import android.view.Surface;

/** Detects a stable display rotation and requests a fresh, serialized tablet session. */
public final class HubRotationMonitor implements AutoCloseable {
    public interface Callback { void reconnect(int windowsDegrees); }
    private final Activity activity;
    private final Callback callback;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final OrientationEventListener listener;
    private final boolean naturalLandscape;
    private final int preparedDegrees;
    private int pendingDegrees = -1;
    private int pendingRotation = -1;
    private boolean enabled;
    private boolean dispatched;
    private boolean validSensorReading;
    private final Runnable commit = this::commitRotation;

    private void commitRotation() {
        if (!enabled || dispatched || !validSensorReading || activity.isFinishing() || activity.isDestroyed() || activity.isInPictureInPictureMode()) return;
        if (!systemRotationUnlocked()) { cancelCandidate(); return; }
        int rotation = display().getRotation();
        int degrees = toWindowsDegrees(naturalLandscape, rotation);
        if (rotation != pendingRotation || degrees != pendingDegrees || degrees == preparedDegrees) return;
        dispatched = true;
        close();
        callback.reconnect(degrees);
    }

    public HubRotationMonitor(Activity activity, Callback callback) {
        this.activity = activity;
        this.callback = callback;
        Display display = display();
        Point size = new Point();
        display.getRealSize(size);
        boolean sideways = display.getRotation() == Surface.ROTATION_90 || display.getRotation() == Surface.ROTATION_270;
        naturalLandscape = sideways ? size.y > size.x : size.x > size.y;
        int initial = toWindowsDegrees(naturalLandscape, display.getRotation());
        preparedDegrees = activity.getIntent().getIntExtra("HubOrientation", initial);
        listener = new OrientationEventListener(activity) {
            @Override public void onOrientationChanged(int orientation) {
                validSensorReading = orientation != ORIENTATION_UNKNOWN;
                if (!validSensorReading) { cancelCandidate(); return; }
                inspectActualRotation();
            }
        };
    }

    public static boolean isAutomaticTablet(Intent intent, String mode) {
        return isAutomaticSession(intent, mode) && RtStreamSession.TABLET.equals(mode);
    }

    public static boolean isAutomaticSession(Intent intent, String mode) {
        return intent.getBooleanExtra("HubManaged", false)
                && (RtStreamSession.TABLET.equals(mode) || RtStreamSession.REMOTE.equals(mode))
                && intent.getBooleanExtra("HubAutoRotate", false);
    }

    public static int toWindowsDegrees(boolean naturalLandscape, int surfaceRotation) {
        int[] map = naturalLandscape ? new int[] { 0, 90, 180, 270 } : new int[] { 90, 0, 270, 180 };
        return map[surfaceRotation & 3];
    }

    public static void allowAllDirections(Activity activity) {
        // Keep the stream and the preparation activity under the same system
        // rotation-lock policy. FULL_SENSOR ignores that lock and can bounce
        // between two orientations every time a reconnect changes activities.
        activity.setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_FULL_USER);
    }

    public void start() {
        if (dispatched || enabled || !listener.canDetectOrientation()) return;
        enabled = true;
        listener.enable();
    }

    private Display display() { return activity.getWindowManager().getDefaultDisplay(); }

    private boolean systemRotationUnlocked() {
        return Settings.System.getInt(activity.getContentResolver(), Settings.System.ACCELEROMETER_ROTATION, 1) != 0;
    }

    private void inspectActualRotation() {
        if (!enabled || dispatched || activity.isFinishing() || activity.isDestroyed()) return;
        // A lock toggled during the debounce must also cancel the candidate.
        // Keep listening, so unlocking can resume rotation without a new stream.
        if (!systemRotationUnlocked()) { cancelCandidate(); return; }
        if (activity.isInPictureInPictureMode()) { cancelCandidate(); return; }
        int rotation = display().getRotation();
        int degrees = toWindowsDegrees(naturalLandscape, rotation);
        if (degrees == preparedDegrees) { cancelCandidate(); return; }
        if (pendingDegrees == degrees && pendingRotation == rotation) return;
        cancelCandidate();
        pendingDegrees = degrees;
        pendingRotation = rotation;
        handler.postDelayed(commit, 1000);
    }

    private void cancelCandidate() {
        handler.removeCallbacks(commit);
        pendingDegrees = pendingRotation = -1;
    }

    @Override public void close() {
        enabled = false;
        listener.disable();
        cancelCandidate();
    }
}
