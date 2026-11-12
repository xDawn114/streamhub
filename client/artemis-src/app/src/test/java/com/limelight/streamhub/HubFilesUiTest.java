package com.limelight.streamhub;

import android.os.Bundle;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import org.json.JSONArray;
import org.json.JSONObject;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.Robolectric;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.annotation.Config;
import org.robolectric.android.controller.ActivityController;
import static org.junit.Assert.*;

/** Real Activity, generated file listing, unbound store: no network or user documents. */
@RunWith(RobolectricTestRunner.class)
@Config(sdk=28,manifest=Config.NONE,application=android.app.Application.class,qualifiers="w800dp-h1200dp")
public class HubFilesUiTest {
    private Button find(View root,String label) {
        if(root instanceof Button && label.contentEquals(((Button)root).getText()))return (Button)root;
        if(root instanceof ViewGroup)for(int i=0;i<((ViewGroup)root).getChildCount();i++){
            Button match=find(((ViewGroup)root).getChildAt(i),label);if(match!=null)return match;
        }
        return null;
    }
    @Test public void fileAndTaskPanesSeparateActionsAndLargeTextTargetsRemainReachable() throws Exception {
        ActivityController<HubFilesActivity> controller=Robolectric.buildActivity(HubFilesActivity.class).setup();
        HubFilesActivity activity=controller.get();View root=activity.getWindow().getDecorView();
        assertTrue(find(root,"电脑文件").isShown());
        assertFalse(find(root,"选择文件发到电脑").isShown());
        find(root,"传输任务").performClick();
        assertTrue(find(root,"选择文件发到电脑").isShown());
        assertFalse(find(root,"下载所选文件（0）").isShown());
        find(root,"电脑文件").performClick();
        JSONArray entries=new JSONArray().put(new JSONObject().put("name","报告 中文 emoji 😀.txt").put("path","报告.txt").put("size",25));
        java.lang.reflect.Method render=HubFilesActivity.class.getDeclaredMethod("renderBrowse",JSONObject.class,String.class);render.setAccessible(true);
        render.invoke(activity,new JSONObject().put("path","").put("rootName","样本目录").put("entries",entries),"");
        Button file=find(root,"报告 中文 emoji 😀.txt\n"+android.text.format.Formatter.formatFileSize(activity,25));assertNotNull(file);
        assertTrue(file.getMinimumHeight()>=48*activity.getResources().getDisplayMetrics().density);
        find(root,"排序：名称 A–Z").performClick();
        assertNotNull(org.robolectric.shadows.ShadowAlertDialog.getLatestAlertDialog());
        controller.pause().stop().destroy();
    }
    @Test public void documentPickerContextSurvivesRecreation() throws Exception {
        Bundle state=new Bundle();state.putString("pendingComputer","fixture-pc");state.putString("pendingRootId","root-two");state.putString("browseSort","size");
        state.putString("pendingBrowseDownloads",new JSONArray().put(new JSONObject().put("path","sample.txt")).toString());
        ActivityController<HubFilesActivity> controller=Robolectric.buildActivity(HubFilesActivity.class).create(state).start().resume().visible();
        Bundle saved=new Bundle();controller.saveInstanceState(saved);
        assertEquals("fixture-pc",saved.getString("pendingComputer"));assertEquals("root-two",saved.getString("pendingRootId"));
        assertEquals("size",saved.getString("browseSort"));assertEquals(1,new JSONArray(saved.getString("pendingBrowseDownloads")).length());
        controller.pause().stop().destroy();
    }
}
