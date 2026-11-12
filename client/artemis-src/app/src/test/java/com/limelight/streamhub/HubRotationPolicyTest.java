package com.limelight.streamhub;

import android.app.Activity;
import android.content.Intent;
import android.content.pm.ActivityInfo;
import android.hardware.Sensor;
import android.hardware.SensorManager;
import android.os.Looper;
import android.provider.Settings;
import android.view.Display;
import android.view.OrientationEventListener;
import android.view.Surface;
import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import com.limelight.binding.video.PerfOverlayListener;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.Robolectric;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.annotation.Config;
import org.robolectric.shadows.ShadowSensor;
import org.robolectric.util.ReflectionHelpers;
import static org.junit.Assert.*;
import static org.robolectric.Shadows.shadowOf;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=28, manifest=Config.NONE, application=android.app.Application.class)
public class HubRotationPolicyTest {
    @Test public void normalTabletExitQuitsCaptureWhileRotationAndRemoteKeepTheirExistingPolicy() {
        assertTrue(HubSessionLease.shouldQuitBackend(true, "tablet", true, false));
        assertFalse(HubSessionLease.shouldQuitBackend(true, "tablet", true, true));
        assertFalse(HubSessionLease.shouldQuitBackend(true, "remote", true, false));
        assertTrue(HubSessionLease.shouldQuitBackend(false, "remote", true, false));
        assertFalse(HubSessionLease.shouldQuitBackend(false, "remote", false, false));
    }
    @Test public void remoteRotationDoesNotRequestTabletReconnect() {
        Intent intent = new Intent().putExtra("HubManaged", true).putExtra("HubAutoRotate", true);
        assertTrue(HubRotationMonitor.isAutomaticSession(intent, "remote"));
        assertFalse(HubRotationMonitor.isAutomaticTablet(intent, "remote"));
        assertTrue(HubRotationMonitor.isAutomaticTablet(intent, "tablet"));
        intent.putExtra("HubAutoRotate", false);
        assertFalse(HubRotationMonitor.isAutomaticTablet(intent, "tablet"));
        intent.putExtra("HubAutoRotate", true).putExtra("HubManaged", false);
        assertFalse(HubRotationMonitor.isAutomaticTablet(intent, "tablet"));
    }
    @Test public void bothPanelTypesKeepFourDistinctDirections() {
        int[] landscape = {0,90,180,270}, portrait = {90,0,270,180};
        for (int rotation=0; rotation<4; rotation++) {
            assertEquals(landscape[rotation], HubRotationMonitor.toWindowsDegrees(true, rotation));
            assertEquals(portrait[rotation], HubRotationMonitor.toWindowsDegrees(false, rotation));
        }
    }
    @Test public void automaticSessionsRespectTheSystemRotationLock() {
        Activity activity = Robolectric.buildActivity(Activity.class).setup().get();
        HubRotationMonitor.allowAllDirections(activity);
        assertEquals(ActivityInfo.SCREEN_ORIENTATION_FULL_USER, activity.getRequestedOrientation());
    }
    @Test public void lockedRotationNeverStartsAnAutomaticReconnect() {
        RotationFixture fixture = new RotationFixture();
        Settings.System.putInt(fixture.activity.getContentResolver(), Settings.System.ACCELEROMETER_ROTATION, 0);
        fixture.rotate(Surface.ROTATION_90);
        shadowOf(Looper.getMainLooper()).idleFor(Duration.ofSeconds(2));
        fixture.rotate(Surface.ROTATION_0);
        shadowOf(Looper.getMainLooper()).idleFor(Duration.ofSeconds(2));
        assertTrue(fixture.reconnects.isEmpty());
        fixture.monitor.close();
    }
    @Test public void lockingDuringDebounceCancelsAndUnlockingCanResume() {
        RotationFixture fixture = new RotationFixture();
        fixture.rotate(Surface.ROTATION_90);
        shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(500));
        Settings.System.putInt(fixture.activity.getContentResolver(), Settings.System.ACCELEROMETER_ROTATION, 0);
        shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(600));
        assertTrue(fixture.reconnects.isEmpty());
        Settings.System.putInt(fixture.activity.getContentResolver(), Settings.System.ACCELEROMETER_ROTATION, 1);
        fixture.rotate(Surface.ROTATION_90);
        shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(1100));
        assertEquals(1, fixture.reconnects.size());
        assertEquals(Integer.valueOf(0), fixture.reconnects.get(0));
        fixture.rotate(Surface.ROTATION_180);
        shadowOf(Looper.getMainLooper()).idleFor(Duration.ofSeconds(2));
        assertEquals(1, fixture.reconnects.size());
        fixture.monitor.close();
    }
    @Test public void aTransientDisplayRotationDoesNotReconnect() {
        RotationFixture fixture = new RotationFixture();
        fixture.rotate(Surface.ROTATION_90);
        shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(500));
        fixture.rotate(Surface.ROTATION_0);
        shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(1100));
        assertTrue(fixture.reconnects.isEmpty());
        fixture.rotate(Surface.ROTATION_90);
        shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(1100));
        assertEquals(1, fixture.reconnects.size());
        fixture.monitor.close();
    }
    private static final class RotationFixture {
        final Activity activity = Robolectric.buildActivity(Activity.class).setup().get();
        final List<Integer> reconnects = new ArrayList<>();
        final Display display = activity.getWindowManager().getDefaultDisplay();
        final HubRotationMonitor monitor;
        final OrientationEventListener listener;
        RotationFixture() {
            Settings.System.putInt(activity.getContentResolver(), Settings.System.ACCELEROMETER_ROTATION, 1);
            shadowOf(display).setRealWidth(600);
            shadowOf(display).setRealHeight(900);
            shadowOf(display).setRotation(Surface.ROTATION_0);
            activity.setIntent(new Intent().putExtra("HubOrientation", 90));
            shadowOf((SensorManager) activity.getSystemService(Activity.SENSOR_SERVICE))
                    .addSensor(ShadowSensor.newInstance(Sensor.TYPE_ACCELEROMETER));
            monitor = new HubRotationMonitor(activity, reconnects::add);
            listener = ReflectionHelpers.getField(monitor, "listener");
            monitor.start();
        }
        void rotate(int rotation) {
            shadowOf(display).setRotation(rotation);
            listener.onOrientationChanged(90);
        }
    }
    @Test public void wirelessPreparationAndVideoShareFpsCeiling() {
        for (String route : new String[]{"lan", "remote", "auto"}) {
            assertEquals(60, HubTransportPolicy.limitFps(90, route), 0);
            assertEquals(30, HubTransportPolicy.limitFps(30, route), 0);
        }
        assertEquals(90, HubTransportPolicy.limitFps(144, "usb"), 0);
        assertEquals(59.94, HubTransportPolicy.limitFps(59.94f, "usb"), .001);
    }
    @Test public void clearProfileIsOptInAndMeteredBudgetStillApplies() {
        assertEquals(8000, HubTransportPolicy.bitrateCap("stable", "lan", false));
        assertEquals(16000, HubTransportPolicy.bitrateCap("clear", "lan", false));
        assertEquals(6000, HubTransportPolicy.bitrateCap("clear", "lan", true));
        assertEquals(6000, HubTransportPolicy.bitrateCap("unknown", "remote", false));
        assertEquals(24000, HubTransportPolicy.bitrateCap("clear", "usb", false));
    }
    @Test public void legacyPerfListenerDoesNotEnableBackgroundDiagnostics() {
        PerfOverlayListener legacy = text -> { };
        assertFalse(legacy.needsPerfUpdates());
    }
}
