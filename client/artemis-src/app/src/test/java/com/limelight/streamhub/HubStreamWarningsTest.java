package com.limelight.streamhub;

import android.app.Application;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.annotation.Config;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=28,manifest=Config.NONE,application=Application.class)
public class HubStreamWarningsTest {
    @Test public void sessionMuteSurvivesReopeningAndDoneUntilExplicitlyRestored() {
        android.app.Activity activity=org.robolectric.Robolectric.buildActivity(android.app.Activity.class).setup().get();
        activity.getSharedPreferences("streamhub_controls",0).edit().putBoolean("warning_network",true).putBoolean("warning_load",true).commit();
        HubStreamWarnings warnings=new HubStreamWarnings(activity);int[] changes={0};
        warnings.show(activity,()->changes[0]++);
        org.robolectric.shadows.ShadowAlertDialog.getLatestAlertDialog().getButton(android.app.AlertDialog.BUTTON_NEUTRAL).performClick();
        org.robolectric.Shadows.shadowOf(android.os.Looper.getMainLooper()).idle();
        assertFalse(warnings.visible(true,true));
        warnings.show(activity,()->changes[0]++);
        org.robolectric.shadows.ShadowAlertDialog.getLatestAlertDialog().getButton(android.app.AlertDialog.BUTTON_POSITIVE).performClick();
        org.robolectric.Shadows.shadowOf(android.os.Looper.getMainLooper()).idle();
        assertFalse(warnings.visible(true,true));assertEquals(1,changes[0]);
        assertTrue(new HubStreamWarnings(activity).visible(true,true));
        warnings.show(activity,()->changes[0]++);
        org.robolectric.shadows.ShadowAlertDialog.getLatestAlertDialog().getButton(android.app.AlertDialog.BUTTON_NEUTRAL).performClick();
        org.robolectric.Shadows.shadowOf(android.os.Looper.getMainLooper()).idle();
        assertTrue(warnings.visible(true,true));assertEquals(2,changes[0]);activity.finish();
    }
    @Test public void mutingNetworkDoesNotHideSimultaneousDecoderAdvisory() {
        Application app=RuntimeEnvironment.getApplication();
        app.getSharedPreferences("streamhub_controls",0).edit().putBoolean("warning_network",false).putBoolean("warning_load",true).commit();
        HubStreamWarnings warnings=new HubStreamWarnings(app);
        assertFalse(warnings.networkVisible(true));assertTrue(warnings.loadVisible(true));assertTrue(warnings.visible(true,true));
        app.getSharedPreferences("streamhub_controls",0).edit().putBoolean("warning_load",false).commit();
        assertFalse(new HubStreamWarnings(app).visible(true,true));
    }
}
