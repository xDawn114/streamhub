package com.limelight.streamhub;

import android.app.AlertDialog;
import android.view.View;
import android.widget.RadioGroup;
import com.limelight.streamhub.networking.EmbeddedNetwork;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.Robolectric;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.annotation.Config;
import org.robolectric.shadows.ShadowAlertDialog;
import static org.junit.Assert.*;
import static org.mockito.Mockito.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=34, application=android.app.Application.class)
public class HubProviderUiTest {
    private static Object field(Object target,String name) throws Exception {
        var f=target.getClass().getDeclaredField(name);f.setAccessible(true);return f.get(target);
    }
    private static void replaceNetwork(HubActivity activity, EmbeddedNetwork network) throws Exception {
        var f=HubActivity.class.getDeclaredField("network");f.setAccessible(true);f.set(activity,network);
    }
    @Test public void choiceAppliesImmediatelyAndFailureRestoresSavedProvider() throws Exception {
        try(var controller=Robolectric.buildActivity(HubActivity.class).create()) {
            HubActivity activity=controller.get();HubStore store=new HubStore(activity);
            EmbeddedNetwork backend=mock(EmbeddedNetwork.class);
            doAnswer(call->{HubTransports.Settings settings=call.getArgument(0);store.saveTransport(settings);((EmbeddedNetwork.Callback)call.getArgument(1)).complete(null,null);return null;}).when(backend).configure(any(),any());
            replaceNetwork(activity,backend);
            RadioGroup providers=(RadioGroup)field(activity,"provider");
            providers.check(providers.getChildAt(2).getId()); org.robolectric.Shadows.shadowOf(android.os.Looper.getMainLooper()).idle();
            assertEquals("lan",store.transport().provider);
            assertEquals(providers.getChildAt(2).getId(),providers.getCheckedRadioButtonId());
            assertEquals(View.GONE,((View)field(activity,"applyProvider")).getVisibility());
            doAnswer(call->{((EmbeddedNetwork.Callback)call.getArgument(1)).complete(null,"模拟失败");return null;}).when(backend).configure(any(),any());
            providers.check(providers.getChildAt(0).getId()); org.robolectric.Shadows.shadowOf(android.os.Looper.getMainLooper()).idle();
            assertEquals("lan",store.transport().provider);
            assertEquals(providers.getChildAt(2).getId(),providers.getCheckedRadioButtonId());
            assertFalse(((Boolean)field(activity,"busy")));
            verify(backend,times(2)).configure(any(),any());
        }
    }
    @Test public void addressChoiceRemainsUnchangedUntilValidInputIsSaved() throws Exception {
        try(var controller=Robolectric.buildActivity(HubActivity.class).create()) {
            HubActivity activity=controller.get();EmbeddedNetwork backend=mock(EmbeddedNetwork.class);replaceNetwork(activity,backend);
            RadioGroup providers=(RadioGroup)field(activity,"provider");
            providers.check(providers.getChildAt(1).getId()); org.robolectric.Shadows.shadowOf(android.os.Looper.getMainLooper()).idle();
            AlertDialog dialog=ShadowAlertDialog.getLatestAlertDialog();assertTrue(dialog.isShowing());
            assertEquals(providers.getChildAt(0).getId(),providers.getCheckedRadioButtonId());
            dialog.getButton(AlertDialog.BUTTON_POSITIVE).performClick();assertTrue(dialog.isShowing());
            assertEquals("tailscale",new HubStore(activity).transport().provider);verifyNoInteractions(backend);
            dialog.getButton(AlertDialog.BUTTON_NEGATIVE).performClick();org.robolectric.Shadows.shadowOf(android.os.Looper.getMainLooper()).idle();assertFalse(dialog.isShowing());
        }
    }
}
