package com.limelight.streamhub;

import android.app.*;
import android.graphics.*;
import android.os.*;
import android.view.*;
import android.widget.*;
import java.io.*;
import java.util.*;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.annotation.*;
import org.robolectric.shadows.ShadowAlertDialog;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=34,application=Application.class,qualifiers="w800dp-h1200dp-port-mdpi")
@GraphicsMode(GraphicsMode.Mode.NATIVE)
public class HubRetainedFeaturesTest {
    @Test public void layoutChoicePersistsFromDialogWithoutChangingVideoPreferences()throws Exception{
        try(var controller=Robolectric.buildActivity(Activity.class).setup()){
            Activity activity=controller.get();var video=androidx.preference.PreferenceManager.getDefaultSharedPreferences(activity);
            video.edit().putString("list_fps","90").commit();int[] changed={0};
            HubPanelLayout.show(activity,()->changed[0]++);AlertDialog dialog=ShadowAlertDialog.getLatestAlertDialog();
            dialog.getListView().performItemClick(null,2,2);assertEquals(HubPanelLayout.FLOAT,HubPanelLayout.mode(activity));assertEquals(1,changed[0]);
            HubPanelLayout.show(activity,()->changed[0]++);ShadowAlertDialog.getLatestAlertDialog().getButton(AlertDialog.BUTTON_NEGATIVE).performClick();
            assertEquals(HubPanelLayout.FLOAT,HubPanelLayout.mode(activity));assertEquals("90",video.getString("list_fps",""));assertEquals(1,changed[0]);
        }
    }
    @Test public void customShortcutImportValidatesAndAppearsInTheUnifiedToolbar()throws Exception{
        String json="{\"data\":[{\"id\":\"copy\",\"name\":\"我的复制\",\"keys\":[\"VK_LCONTROL\",\"0x43\"]}]}";
        assertEquals(2,HubShortcutBindings.parse(json).get(0).keys.length);
        try{HubShortcutBindings.parse("{\"data\":[{\"name\":\"bad\",\"keys\":[\"KEY_RESERVED\"]}]}");fail();}catch(org.json.JSONException expected){}
        try(var controller=Robolectric.buildActivity(Activity.class).setup()){
            Activity activity=controller.get();activity.getSharedPreferences(HubShortcutBindings.PREFS,0).edit().putString(HubShortcutBindings.KEY,json).commit();
            FrameLayout root=new FrameLayout(activity);activity.setContentView(root);List<short[]> sent=new ArrayList<>();
            RtInputToolbar toolbar=new RtInputToolbar(activity,root,new RtInputToolbar.Callbacks(){
                public void showKeyboard(){}public void showFullKeyboard(){}public void sendText(String s){}public void sendKeys(short[] keys){sent.add(keys);}
            });layout(root,800,1200);toolbar.show();layout(root,800,1200);text(root,"我的复制").performClick();
            assertArrayEquals(new short[]{(short)0xA2,0x43},sent.get(0));toolbar.dispose();
        }
    }
    @Test public void offlineAccountRecordRemovalStaysLocalAndCanBeRestored()throws Exception{
        try(var controller=Robolectric.buildActivity(HubActivity.class).create()){
            HubActivity activity=controller.get();new HubStore(activity).put("deviceId","trusted");
            var json=new org.json.JSONObject("{\"BackendState\":\"Running\",\"TailscaleIPs\":[\"100.70.1.1\"],\"Peer\":{\"fixture\":{\"HostName\":\"离线电脑\",\"OS\":\"windows\",\"Online\":false,\"TailscaleIPs\":[\"100.70.2.2\"]}}}");
            var type=com.limelight.streamhub.networking.EmbeddedNetwork.Status.class;
            var constructor=type.getDeclaredConstructor(org.json.JSONObject.class,HubTransports.Settings.class,String.class);constructor.setAccessible(true);
            Object state=constructor.newInstance(json,new HubStore(activity).transport(),new HubStore(activity).get("transportRevision",""));
            var render=HubActivity.class.getDeclaredMethod("renderNetwork",type,String.class);render.setAccessible(true);render.invoke(activity,state,null);page(activity,4);
            View root=root(activity);text(root,"查看离线电脑（1） ▾").performClick();
            TextView remove=text(root,"移除");assertNotNull(remove);remove.performClick();
            ShadowAlertDialog.getLatestAlertDialog().getButton(AlertDialog.BUTTON_POSITIVE).performClick();
            Shadows.shadowOf(Looper.getMainLooper()).idle();
            assertEquals("trusted",new HubStore(activity).get("deviceId",""));assertEquals(1,activity.getSharedPreferences("streamhub_controls",0).getStringSet("hidden_account_peers",Set.of()).size());
            assertNotNull(text(root,"恢复本机已移除的离线记录（1）"));text(root,"恢复本机已移除的离线记录（1）").performClick();
            assertTrue(activity.getSharedPreferences("streamhub_controls",0).getStringSet("hidden_account_peers",Set.of()).isEmpty());
        }
    }
    private View root(Activity activity){return ((ViewGroup)activity.findViewById(android.R.id.content)).getChildAt(0);}
    private void layout(View view,int width,int height){for(int i=0;i<3;i++){view.measure(View.MeasureSpec.makeMeasureSpec(width,1073741824),View.MeasureSpec.makeMeasureSpec(height,1073741824));view.layout(0,0,width,height);}}
    private TextView text(View root,String label){if(root.getVisibility()!=View.VISIBLE)return null;
        if(root instanceof TextView&&label.contentEquals(((TextView)root).getText()))return (TextView)root;
        if(root instanceof ViewGroup)for(int i=0;i<((ViewGroup)root).getChildCount();i++){TextView found=text(((ViewGroup)root).getChildAt(i),label);if(found!=null)return found;}return null;}
    private Object field(Object value,String name)throws Exception{var field=value.getClass().getDeclaredField(name);field.setAccessible(true);return field.get(value);}
    private void page(HubActivity activity,int page)throws Exception{var method=HubActivity.class.getDeclaredMethod("showPage",int.class);method.setAccessible(true);method.invoke(activity,page);}
    private void snapshot(View view,String name)throws Exception{String directory=System.getenv("STREAMHUB_RENDER_ROOT");if(directory==null)return;
        File file=new File(directory,name);assertTrue(file.getParentFile().isDirectory()||file.getParentFile().mkdirs());
        Bitmap bitmap=Bitmap.createBitmap(view.getWidth(),view.getHeight(),Bitmap.Config.ARGB_8888);view.draw(new Canvas(bitmap));
        try(var out=new FileOutputStream(file)){assertTrue(bitmap.compress(Bitmap.CompressFormat.PNG,100,out));}finally{bitmap.recycle();}}

    @Test public void settingsRetainRealCapabilitiesAndDetailsBackReturnsHome()throws Exception{
        for(int theme:new int[]{HubTheme.LIGHT,HubTheme.DARK}){
            HubTheme.setMode(RuntimeEnvironment.getApplication(),theme);
            try(var controller=Robolectric.buildActivity(HubActivity.class).create()){
                HubActivity activity=controller.get();page(activity,5);View view=root(activity);layout(view,800,1200);
                text(view,"展开手柄设置 ▾").performClick();
                for(String label:new String[]{"移动网络码率","完整色彩范围","HDR画面","触控板水平灵敏度","摇杆死区","USB手柄驱动","离开串流时使用小窗"})assertNotNull(label,text(view,label));
                ((View)text(view,"触控板水平灵敏度").getParent().getParent()).performClick();
                AlertDialog dialog=ShadowAlertDialog.getLatestAlertDialog();Shadows.shadowOf(Looper.getMainLooper()).idle();EditText edit=find(dialog.getWindow().getDecorView(),EditText.class);
                edit.setText("-150");dialog.getButton(AlertDialog.BUTTON_POSITIVE).performClick();
                assertEquals(-150,HubAdvancedPreferences.active(activity).getInt("seekbar_trackpad_sensitivity_x",0));
                FrameLayout slot=(FrameLayout)field(activity,"pageSlot");ScrollView scroll=(ScrollView)slot.getChildAt(4);
                scroll.fullScroll(View.FOCUS_DOWN);layout(view,800,1200);snapshot(view,"advanced-restored-bottom-"+theme+".png");
                page(activity,6);layout(view,800,1200);assertNotNull(text(view,"设备详情"));snapshot(view,"device-details-"+theme+".png");
                activity.onBackPressed();assertEquals(0,field(activity,"currentPage"));layout(view,800,1200);snapshot(view,"home-layered-"+theme+".png");
                page(activity,2);assertNotNull(text(view,"屏幕键盘布局"));assertNotNull(text(view,"兼容主机"));assertNotNull(text(view,"面板展开方式"));
            }
        }
    }
    @SuppressWarnings("unchecked") private <T> T find(View root,Class<T> type){if(type.isInstance(root))return (T)root;
        if(root instanceof ViewGroup)for(int i=0;i<((ViewGroup)root).getChildCount();i++){T value=find(((ViewGroup)root).getChildAt(i),type);if(value!=null)return value;}return null;}

    @Test public void themedCompatibilityAndKeyboardPagesRenderWithoutTouchingHubBinding()throws Exception{
        var app=RuntimeEnvironment.getApplication();new HubStore(app).put("deviceId","trusted-fixture");
        for(int theme:new int[]{HubTheme.LIGHT,HubTheme.DARK}){
            HubTheme.setMode(app,theme);
            try(var controller=Robolectric.buildActivity(HubCompatibleActivity.class).create()){
                View view=root(controller.get());layout(view,800,1200);assertNotNull(text(view,"兼容主机"));snapshot(view,"compatible-"+theme+".png");
            }
            try(var controller=Robolectric.buildActivity(HubKeyboardLayoutsActivity.class).create()){
                View view=root(controller.get());layout(view,800,1200);assertNotNull(text(view,"导入布局文件"));snapshot(view,"keyboard-layouts-"+theme+".png");
            }
        }assertEquals("trusted-fixture",new HubStore(app).get("deviceId",""));
    }
    @Test public void keyboardImportPreservesFormatRejectsOversizeAndNeverWritesWhileParsing()throws Exception{
        var app=RuntimeEnvironment.getApplication();var prefs=app.getSharedPreferences("OSC_Keyboard",0);
        prefs.edit().putString("key","{\"x\":12,\"future\":true}").commit();String exported=HubKeyboardLayoutsActivity.exportJson(prefs);
        Map<String,String> parsed=HubKeyboardLayoutsActivity.parse(new ByteArrayInputStream(exported.getBytes(java.nio.charset.StandardCharsets.UTF_8)));
        assertEquals(prefs.getString("key",""),parsed.get("key"));
        try{HubKeyboardLayoutsActivity.parse(new ByteArrayInputStream("{\"a\":42}".getBytes()));fail();}catch(org.json.JSONException expected){}
        try{HubKeyboardLayoutsActivity.parse(new ByteArrayInputStream(new byte[262145]));fail();}catch(IOException expected){}
        assertEquals(1,prefs.getAll().size());
    }
    @Test public void panelModesPersistAndPrivacyControlCanScrollAboveFixedFooter()throws Exception{
        try(var controller=Robolectric.buildActivity(Activity.class).setup()){
            Activity activity=controller.get();FrameLayout root=new FrameLayout(activity){@Override public void getWindowVisibleDisplayFrame(Rect out){out.set(0,0,getWidth(),getHeight());}};
            activity.setContentView(root);RtInputToolbar toolbar=new RtInputToolbar(activity,root,new RtInputToolbar.Callbacks(){
                public void showKeyboard(){}public void showFullKeyboard(){}public void sendText(String s){}public void sendKeys(short[] k){}
                public boolean privacyAvailable(){return true;}
                public Rect videoBounds(){return new Rect(0,300,800,800);}
            });
            for(String mode:HubPanelLayout.VALUES){
                HubPanelLayout.prefs(activity).edit().putString("panel_layout",mode).commit();layout(root,800,1200);toolbar.show();layout(root,800,1200);
                assertEquals(mode,HubPanelLayout.mode(activity));text(root,"画面").performClick();Shadows.shadowOf(Looper.getMainLooper()).idle();layout(root,800,1200);
                Button privacy=(Button)text(root,"本机隐私屏");assertNotNull(privacy);
                ScrollView scroll=(ScrollView)field(toolbar,"body");privacy.requestRectangleOnScreen(new Rect(0,0,privacy.getWidth(),privacy.getHeight()),true);layout(root,800,1200);
                Rect visible=new Rect();assertTrue(privacy.getGlobalVisibleRect(visible));snapshot(root,"privacy-"+mode+".png");assertEquals("mode="+mode+", scroll="+scroll.getScrollY()+", body="+scroll.getHeight()+", bounds="+visible,privacy.getHeight(),visible.height());
                toolbar.setWarning(true,"网络拥塞");layout(root,800,1200);snapshot(root,"controls-"+mode+".png");
            }
            layout(root,1280,800);toolbar.refreshLayout();layout(root,1280,800);snapshot(root,"controls-landscape.png");toolbar.dispose();
        }
    }
}
