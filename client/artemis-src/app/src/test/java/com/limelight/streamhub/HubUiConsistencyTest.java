package com.limelight.streamhub;

import android.app.AlertDialog;
import android.content.Context;
import android.graphics.Rect;
import android.view.View;
import android.view.ViewGroup;
import android.widget.*;
import com.limelight.profiles.ProfilesManager;
import com.limelight.profiles.SettingsProfile;
import java.util.*;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.Robolectric;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.annotation.Config;
import org.robolectric.annotation.GraphicsMode;
import org.robolectric.shadows.ShadowAlertDialog;
import static org.junit.Assert.*;

/** Native view trees and isolated preference files; no real host, VPN, or user files. */
@RunWith(RobolectricTestRunner.class)
@Config(sdk=34,application=android.app.Application.class,qualifiers="w800dp-h1200dp-port-mdpi")
@GraphicsMode(GraphicsMode.Mode.NATIVE)
public class HubUiConsistencyTest {
    private Object field(Object target,String name) throws Exception {var f=target.getClass().getDeclaredField(name);f.setAccessible(true);return f.get(target);}
    private Object invoke(Object target,String method,Class<?>[] types,Object...args)throws Exception{var m=target.getClass().getDeclaredMethod(method,types);m.setAccessible(true);return m.invoke(target,args);}
    private void page(HubActivity activity,int value)throws Exception{invoke(activity,"showPage",new Class<?>[]{int.class},value);}
    private View root(android.app.Activity activity){return ((ViewGroup)activity.findViewById(android.R.id.content)).getChildAt(0);}
    private TextView text(View view,String label) {
        if(view.getVisibility()!=View.VISIBLE)return null;
        if(view instanceof TextView&&label.contentEquals(((TextView)view).getText()))return (TextView)view;
        if(view instanceof ViewGroup)for(int i=0;i<((ViewGroup)view).getChildCount();i++){TextView found=text(((ViewGroup)view).getChildAt(i),label);if(found!=null)return found;}
        return null;
    }
    private void layout(View root,int width,int height){for(int i=0;i<3;i++){root.measure(View.MeasureSpec.makeMeasureSpec(width,View.MeasureSpec.EXACTLY),View.MeasureSpec.makeMeasureSpec(height,View.MeasureSpec.EXACTLY));root.layout(0,0,width,height);}}
    private void snapshot(View root,String name)throws Exception{
        String path=System.getenv("STREAMHUB_RENDER_ROOT");if(path==null)return;
        java.io.File dir=new java.io.File(path);assertTrue(dir.isDirectory()||dir.mkdirs());
        var bitmap=android.graphics.Bitmap.createBitmap(root.getWidth(),root.getHeight(),android.graphics.Bitmap.Config.ARGB_8888);root.draw(new android.graphics.Canvas(bitmap));
        try(var out=new java.io.FileOutputStream(new java.io.File(dir,name))){assertTrue(bitmap.compress(android.graphics.Bitmap.CompressFormat.PNG,100,out));}finally{bitmap.recycle();}
    }
    @Test public void advancedSettingsStayInsideHubAndBackReturnsToSettings()throws Exception{
        for(int theme:new int[]{HubTheme.LIGHT,HubTheme.DARK}){
            HubTheme.setMode(RuntimeEnvironment.getApplication(),theme);
            try(var controller=Robolectric.buildActivity(HubActivity.class).create()){
                HubActivity activity=controller.get();page(activity,2);
                assertNull(text(root(activity),"兼容主机列表"));
                TextView entry=text(root(activity),"音视频与输入高级设置");assertNotNull(entry);
                ((View)entry.getParent().getParent()).performClick();
                assertEquals(5,field(activity,"currentPage"));assertNotNull(text(root(activity),"音视频与输入"));
                assertNull(org.robolectric.Shadows.shadowOf(activity).getNextStartedActivity());
                layout(root(activity),800,1200);snapshot(root(activity),"advanced-theme"+theme+".png");
                activity.onBackPressed();assertEquals(2,field(activity,"currentPage"));assertFalse(activity.isFinishing());
            }
        }
    }
    @Test public void disabledActionsRemainReadableInBothThemes(){
        Context context=RuntimeEnvironment.getApplication();
        for(int theme:new int[]{HubTheme.LIGHT,HubTheme.DARK}){
            HubTheme.setMode(context,theme);
            for(boolean primary:new boolean[]{true,false}){
                Button button=new Button(context);HubButtons.apply(button,primary);button.setEnabled(false);
                int background=((android.graphics.drawable.GradientDrawable)button.getBackground().getCurrent()).getColor().getDefaultColor();
                assertTrue(androidx.core.graphics.ColorUtils.calculateContrast(button.getCurrentTextColor(),background)>=3);
                assertEquals(1f,button.getAlpha(),0f);
            }
        }
    }
    @Test public void fileTabsKeepToolbarAndTabCoordinatesEvenWithLongStatus()throws Exception{
        for(int[] size:new int[][]{{360,720},{800,1200},{1280,800}}){
            RuntimeEnvironment.setQualifiers("w"+size[0]+"dp-h"+size[1]+"dp-"+(size[0]>size[1]?"land":"port")+"-mdpi");
            try(var controller=Robolectric.buildActivity(HubFilesActivity.class).setup()){
                HubFilesActivity activity=controller.get();View root=root(activity);layout(root,size[0],size[1]);
                View tab=(View)field(activity,"computerTab");Rect before=new Rect();tab.getGlobalVisibleRect(before);
                for(int count=0;count<2;count++){
                    ((TextView)field(activity,"state")).setText("请在电脑的设备权限中允许文件访问，并选择可浏览目录。");
                    ((View)field(activity,"tasksTab")).performClick();layout(root,size[0],size[1]);
                    Rect after=new Rect();assertTrue(tab.getGlobalVisibleRect(after));assertEquals(before,after);
                    assertEquals(View.VISIBLE,((View)field(activity,"sendButton")).getVisibility());assertFalse(((View)field(activity,"sendButton")).isEnabled());
                    ((View)field(activity,"computerTab")).performClick();layout(root,size[0],size[1]);tab.getGlobalVisibleRect(after);assertEquals(before,after);
                }
                snapshot(root,"files-fixed-"+size[0]+".png");
            }
        }
    }
    @Test public void shortCodeHintNeverBecomesInputAndHasNoLetterSpacing()throws Exception{
        try(var controller=Robolectric.buildActivity(HubActivity.class).create()){
            EditText code=(EditText)invoke(controller.get(),"shortCodeField",new Class<?>[]{LinearLayout.class},new LinearLayout(controller.get()));
            assertEquals("",code.getText().toString());assertEquals(0f,code.getLetterSpacing(),0f);assertFalse(code.getHint().toString().contains("012345"));
        }
    }
    @Test public void activeProfileEditsReachDecoderPreferencesAndRollbackOnFailure()throws Exception{
        Context context=RuntimeEnvironment.getApplication();ProfilesManager manager=ProfilesManager.getInstance();manager.load(context);
        SettingsProfile profile=new SettingsProfile(UUID.randomUUID(),"fixture",0,0,new LinkedHashMap<>(Map.of("list_fps","60")));
        manager.add(profile);manager.setActive(profile.getUuid());
        assertTrue(HubAdvancedPreferences.save(context,Map.of("list_fps","90","custom_refresh_rate","90","checkbox_absolute_mouse_mode",true)));
        assertEquals("90",HubAdvancedPreferences.active(context).getString("list_fps",""));
        assertTrue(HubAdvancedPreferences.active(context).getBoolean("checkbox_absolute_mouse_mode",false));
        java.io.File blocker=new java.io.File(context.getCacheDir(),"blocked-dir");try(var file=new java.io.FileOutputStream(blocker)){file.write(1);}
        Context failing=new android.content.ContextWrapper(context){@Override public java.io.File getFilesDir(){return blocker;}};
        assertFalse(HubAdvancedPreferences.save(failing,Map.of("list_fps","120")));
        assertEquals("90",HubAdvancedPreferences.active(context).getString("list_fps",""));
        manager.setActive(null);
    }
    @Test public void diagnosticViewShowsFirstFrameOnceAndSingleSampleWithoutStatistics()throws Exception{
        Context context=RuntimeEnvironment.getApplication();
        HubSessionDiagnostics.begin(context,"remote","lan");
        HubSessionDiagnostics.phase(context,"first-frame",2012);
        HubSessionDiagnostics.hostStages(context,new org.json.JSONObject().put("serverinfo-200",900).put("engine",0));
        HubSessionDiagnostics.performance(context,90,120,90,1,2,90,56,56.2f,7,4,0,100);
        var rows=HubSessionDiagnostics.performanceSummary(context).getJSONArray("rows");
        assertFalse(rows.toString().contains("中位数"));assertFalse(rows.toString().contains("均值"));assertFalse(rows.toString().contains("范围"));assertFalse(rows.toString().contains("mode "));
        View view=HubStreamUi.diagnosticsView(context,null);String all=allText(view);
        assertEquals(all,1,all.split("2012 ms",-1).length-1);
        assertTrue(all.contains("阶段记录不完整"));
        assertTrue(HubSessionDiagnostics.timeline(context).toString().contains("尚未归因的等待"));
        assertTrue(all.contains("检查时 900 ms"));assertFalse(all.contains("首帧总耗时"));assertNull(text(view,"0 ms"));
        assertTrue(new org.json.JSONObject(HubSessionDiagnostics.read(context)).getJSONArray("samples").getJSONObject(0).has("panelModeId"));
    }
    @Test public void switchTracksRemainVisibleAndThumbStaysInsideInBothThemesAndDirections() {
        Context context=RuntimeEnvironment.getApplication();
        context.getApplicationInfo().flags |= android.content.pm.ApplicationInfo.FLAG_SUPPORTS_RTL;
        for(int theme:new int[]{HubTheme.LIGHT,HubTheme.DARK})for(int direction:new int[]{View.LAYOUT_DIRECTION_LTR,View.LAYOUT_DIRECTION_RTL}) {
            HubTheme.setMode(context,theme);
            HubSwitch control=new HubSwitch(context);control.setLayoutDirection(direction);control.setPadding(8,8,8,8);
            control.setText("测试");int[] colors=new int[2];
            for(int selected=0;selected<2;selected++) {
                control.setChecked(selected==1);control.measure(View.MeasureSpec.makeMeasureSpec(160,1073741824),View.MeasureSpec.makeMeasureSpec(56,1073741824));control.layout(0,0,160,56);
                assertEquals(direction,control.getLayoutDirection());
                var bitmap=android.graphics.Bitmap.createBitmap(160,56,android.graphics.Bitmap.Config.ARGB_8888);
                bitmap.eraseColor(HubTheme.surface(context));control.draw(new android.graphics.Canvas(bitmap));
                int left=direction==View.LAYOUT_DIRECTION_LTR?100:8;
                int center=left+((selected==1)!=(direction==View.LAYOUT_DIRECTION_RTL)?38:14);
                assertEquals(android.graphics.Color.WHITE,bitmap.getPixel(center,28));
                assertEquals(android.graphics.Color.WHITE,bitmap.getPixel(center-8,28));
                assertEquals(android.graphics.Color.WHITE,bitmap.getPixel(center+8,28));
                colors[selected]=bitmap.getPixel(left+26,28);
                assertTrue("theme="+theme+", direction="+direction+", selected="+selected+", color="+Integer.toHexString(colors[selected])+", surface="+Integer.toHexString(HubTheme.surface(context)),androidx.core.graphics.ColorUtils.calculateContrast(colors[selected],HubTheme.surface(context))>=3);
                String renderRoot=System.getenv("STREAMHUB_RENDER_ROOT");
                if(renderRoot!=null)try(var out=new java.io.FileOutputStream(new java.io.File(renderRoot,"switch-"+theme+"-"+direction+"-"+selected+".png"))){bitmap.compress(android.graphics.Bitmap.CompressFormat.PNG,100,out);}catch(java.io.IOException error){throw new AssertionError(error);}
                bitmap.recycle();
                assertEquals("android.widget.Switch",control.getAccessibilityClassName());
            }
            assertNotEquals(colors[0],colors[1]);control.performClick();assertFalse(control.isChecked());
        }
    }
    private String allText(View view) {
        StringBuilder out=new StringBuilder();
        if(view instanceof TextView)out.append(((TextView)view).getText()).append('\n');
        if(view instanceof ViewGroup)for(int i=0;i<((ViewGroup)view).getChildCount();i++)out.append(allText(((ViewGroup)view).getChildAt(i)));
        return out.toString();
    }
}
