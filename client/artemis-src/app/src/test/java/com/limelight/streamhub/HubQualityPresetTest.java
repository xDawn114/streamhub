package com.limelight.streamhub;

import android.content.Context;
import android.app.Activity;
import androidx.preference.PreferenceManager;
import org.json.JSONObject;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.Robolectric;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.annotation.Config;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=28, application=android.app.Application.class)
public class HubQualityPresetTest {
    @Test public void presetParticipatesInHostPreparationAndClosingQualityKeepsStreamActivity() throws Exception {
        try (org.robolectric.android.controller.ActivityController<Activity> controller = Robolectric.buildActivity(Activity.class).setup()) {
            Activity activity = controller.get();
            PreferenceManager.getDefaultSharedPreferences(activity).edit().putString("list_fps", "90").commit();
            new HubStore(activity).put("qualityPreset", "smooth");
            JSONObject request = HubDisplayCapabilities.describe(activity, 90, "usb");
            assertEquals(30, request.getInt("refreshRate"));
            assertTrue(Math.max(request.getInt("width"), request.getInt("height")) <= 1280);
            assertEquals(0, request.getInt("width") % 2); assertEquals(0, request.getInt("height") % 2);
            HubQualitySettings.show(activity, true, null);
            android.app.AlertDialog dialog = org.robolectric.shadows.ShadowAlertDialog.getLatestAlertDialog();
            dialog.getListView().performItemClick(null, 1, 1);
            assertEquals("balanced", new HubStore(activity).get("qualityPreset", ""));
            assertFalse(activity.isFinishing());
            assertNull(org.robolectric.Shadows.shadowOf(activity).getNextStartedActivity());
            new HubStore(activity).put("qualityPreset", "advanced");
            PreferenceManager.getDefaultSharedPreferences(activity).edit().putString("list_fps", "30").commit();
            assertEquals(30, HubDisplayCapabilities.describe(activity, 90, "usb").getInt("refreshRate"));
        }
    }
}
