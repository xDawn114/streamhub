package com.limelight.streamhub;

import android.app.Activity;
import android.content.pm.Signature;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.Switch;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.Robolectric;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.annotation.Config;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk = 34, application = android.app.Application.class)
public class HubUpdatePanelTest {
    @Test public void unconfiguredSourceHasNoDownloadOrInstallAndKeepsPreferences() {
        try(org.robolectric.android.controller.ActivityController<Activity> controller=Robolectric.buildActivity(Activity.class).setup()) {
            Activity activity=controller.get();HubUpdates updates=HubUpdates.get(activity);
            HubUpdatePanel panel=new HubUpdatePanel(activity,updates,()->true);activity.setContentView(panel);
            int switches=0;
            for(int i=0;i<panel.getChildCount();i++) {
                View child=panel.getChildAt(i);
                if(child instanceof Button && !(child instanceof Switch)) {
                    Button button=(Button)child;
                    if("检查更新".contentEquals(button.getText()))assertFalse(button.isEnabled());
                    else assertEquals(View.GONE,button.getVisibility());
                    assertTrue(button.getMinimumHeight()>=48*activity.getResources().getDisplayMetrics().density);
                    assertTrue(((ViewGroup.MarginLayoutParams)button.getLayoutParams()).topMargin>=8*activity.getResources().getDisplayMetrics().density);
                }
                if(child instanceof Switch) { switches++;((Switch)child).setChecked(!((Switch)child).isChecked()); }
            }
            assertEquals(2,switches);assertFalse(updates.autoCheck());assertTrue(updates.autoDownload());
            assertTrue(updates.message().contains("尚未接入"));assertFalse(updates.busy());
        }
    }
    @Test public void originalSignerIsRequiredIncludingMultiSignerPackages() {
        Signature original=new Signature(new byte[]{1,2,3}), foreign=new Signature(new byte[]{4,5,6});
        assertTrue(HubUpdates.sameSigners(new Signature[]{original},new Signature[]{original}));
        assertFalse(HubUpdates.sameSigners(new Signature[]{original},new Signature[]{foreign}));
        assertFalse(HubUpdates.sameSigners(new Signature[]{original},new Signature[]{original,foreign}));
        assertFalse(HubUpdates.sameSigners(new Signature[]{original},null));
        assertFalse(HubUpdates.sameSigners(new Signature[0],new Signature[0]));
    }
}
