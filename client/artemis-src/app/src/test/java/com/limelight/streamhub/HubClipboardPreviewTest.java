package com.limelight.streamhub;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.os.Looper;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.Robolectric;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.annotation.Config;
import org.robolectric.shadows.ShadowAlertDialog;
import static org.robolectric.Shadows.shadowOf;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=28,manifest=Config.NONE,application=android.app.Application.class)
public class HubClipboardPreviewTest {
    @Test public void failedFetchKeepsClipboardAndOffersExplicitRetry() {
        Activity activity=Robolectric.buildActivity(Activity.class).setup().get();
        ClipboardManager manager=(ClipboardManager)activity.getSystemService(Context.CLIPBOARD_SERVICE);
        manager.setPrimaryClip(ClipData.newPlainText("fixture","original"));
        int[] retries={0};HubClipboardPreview.showFailure(activity,"电脑无响应",()->retries[0]++);
        AlertDialog dialog=ShadowAlertDialog.getLatestAlertDialog();
        assertEquals("original",manager.getPrimaryClip().getItemAt(0).getText().toString());
        dialog.getButton(AlertDialog.BUTTON_POSITIVE).performClick();shadowOf(Looper.getMainLooper()).idle();
        assertEquals(1,retries[0]);activity.finish();
    }
    @Test public void previewCopiesFullHtmlAndAllowsRetryAfterAnotherAppChangesClipboard() {
        Activity activity=Robolectric.buildActivity(Activity.class).setup().get();
        String value="中文😀"+new String(new char[17000]).replace('\0','a');
        ClipData incoming=ClipData.newHtmlText("fixture",value,"<p>"+value+"</p>");
        HubClipboardPreview.show(activity,incoming,true);
        shadowOf(Looper.getMainLooper()).idle();
        AlertDialog dialog=ShadowAlertDialog.getLatestAlertDialog();assertNotNull(dialog);assertTrue(dialog.isShowing());
        ClipboardManager manager=(ClipboardManager)activity.getSystemService(Context.CLIPBOARD_SERVICE);
        assertEquals(value,manager.getPrimaryClip().getItemAt(0).getText().toString());
        assertEquals(incoming.getItemAt(0).getHtmlText(),manager.getPrimaryClip().getItemAt(0).getHtmlText());
        assertTrue(manager.getPrimaryClipDescription().getExtras().getBoolean(com.limelight.Game.CLIPBOARD_IDENTIFIER));
        manager.setPrimaryClip(ClipData.newPlainText("fixture","changed"));
        dialog.getButton(AlertDialog.BUTTON_POSITIVE).performClick();
        assertEquals(value,manager.getPrimaryClip().getItemAt(0).getText().toString());assertTrue(dialog.isShowing());
        dialog.dismiss();activity.finish();
    }
}
