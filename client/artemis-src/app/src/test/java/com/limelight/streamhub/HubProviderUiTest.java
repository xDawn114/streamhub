package com.limelight.streamhub;

import android.os.Looper;
import android.view.View;
import android.widget.EditText;
import android.widget.RadioGroup;
import android.widget.TextView;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.Robolectric;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.Shadows;
import org.robolectric.annotation.Config;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=28, application=android.app.Application.class)
public class HubProviderUiTest {
    @Test public void applyRequiresARealChangeEvenAfterBusyWorkFinishes() throws Exception {
        try (org.robolectric.android.controller.ActivityController<HubActivity> controller = Robolectric.buildActivity(HubActivity.class).create()) {
            HubActivity activity = controller.get();
            View apply = (View)field(activity, "applyProvider");
            assertFalse(apply.isEnabled());
            RadioGroup group = (RadioGroup)field(activity, "provider"); group.check(group.getChildAt(2).getId());
            Shadows.shadowOf(Looper.getMainLooper()).idle(); assertTrue(apply.isEnabled());
            java.lang.reflect.Method busy = HubActivity.class.getDeclaredMethod("setBusy", boolean.class); busy.setAccessible(true);
            busy.invoke(activity, true); assertFalse(apply.isEnabled());
            busy.invoke(activity, false); assertTrue(apply.isEnabled());
            group.check(group.getChildAt(0).getId());
            Shadows.shadowOf(Looper.getMainLooper()).idle(); assertFalse(apply.isEnabled());
        }
    }
    private static Object field(HubActivity activity, String name) throws Exception {
        java.lang.reflect.Field field = HubActivity.class.getDeclaredField(name); field.setAccessible(true); return field.get(activity);
    }
    @Test public void providerFieldsAppearOnlyWhenRelevant() throws Exception {
        try (org.robolectric.android.controller.ActivityController<HubActivity> controller = Robolectric.buildActivity(HubActivity.class).create()) {
            HubActivity activity = controller.get();
            RadioGroup providers = (RadioGroup)field(activity, "provider");
            assertEquals(4, providers.getChildCount()); assertEquals("连接通道", providers.getContentDescription());
            for (int index = 0; index < 4; index++) {
                providers.check(providers.getChildAt(index).getId()); Shadows.shadowOf(Looper.getMainLooper()).idle();
                assertEquals(index == 1 ? View.VISIBLE : View.GONE, ((View)field(activity, "providerServerPanel")).getVisibility());
                assertEquals(index == 3 ? View.VISIBLE : View.GONE, ((View)field(activity, "providerManualPanel")).getVisibility());
            }
        }
    }
    @Test public void invalidSelfHostedUrlShowsAnInlineErrorAndLeavesConfigurationUntouched() throws Exception {
        try (org.robolectric.android.controller.ActivityController<HubActivity> controller = Robolectric.buildActivity(HubActivity.class).create()) {
            HubActivity activity = controller.get();
            RadioGroup group = (RadioGroup)field(activity, "provider"); group.check(group.getChildAt(1).getId());
            ((EditText)field(activity, "providerServer")).setText("http://vpn.example.com");
            java.lang.reflect.Method save = HubActivity.class.getDeclaredMethod("saveProvider"); save.setAccessible(true); save.invoke(activity);
            assertTrue(((TextView)field(activity, "providerMessage")).getText().toString().contains("HTTPS"));
            assertEquals("tailscale", new HubStore(activity).transport().provider);
            assertEquals("", new HubStore(activity).get("transportRevision", ""));
        }
    }
}
