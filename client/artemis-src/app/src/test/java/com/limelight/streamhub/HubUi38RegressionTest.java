package com.limelight.streamhub;

import android.app.*;
import android.graphics.*;
import android.os.Looper;
import android.view.*;
import android.widget.*;
import java.io.*;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.annotation.*;
import org.robolectric.shadows.ShadowAlertDialog;
import static org.junit.Assert.*;

/** Regression cases from the 180 UI review; create-only Hub activities never start networking. */
@RunWith(RobolectricTestRunner.class)
@Config(sdk=34,application=Application.class,qualifiers="w800dp-h1200dp-port-mdpi")
@GraphicsMode(GraphicsMode.Mode.NATIVE)
public class HubUi38RegressionTest {
    private View root(Activity activity){return ((ViewGroup)activity.findViewById(android.R.id.content)).getChildAt(0);}
    private void layout(View view,int width,int height){for(int i=0;i<3;i++){
        view.measure(View.MeasureSpec.makeMeasureSpec(width,1073741824),View.MeasureSpec.makeMeasureSpec(height,1073741824));view.layout(0,0,width,height);
    }}
    private Object field(Object target,String name)throws Exception{var f=target.getClass().getDeclaredField(name);f.setAccessible(true);return f.get(target);}
    private void page(HubActivity activity,int value)throws Exception{var m=HubActivity.class.getDeclaredMethod("showPage",int.class);m.setAccessible(true);m.invoke(activity,value);}
    private View find(View view,String text){if(view.getVisibility()!=View.VISIBLE)return null;
        if(view instanceof TextView&&text.contentEquals(((TextView)view).getText()))return view;
        if(view instanceof ViewGroup)for(int i=0;i<((ViewGroup)view).getChildCount();i++){View found=find(((ViewGroup)view).getChildAt(i),text);if(found!=null)return found;}return null;
    }
    private View described(View view,String text){if(view.getVisibility()!=View.VISIBLE)return null;
        if(view.getContentDescription()!=null&&text.contentEquals(view.getContentDescription()))return view;
        if(view instanceof ViewGroup)for(int i=0;i<((ViewGroup)view).getChildCount();i++){View found=described(((ViewGroup)view).getChildAt(i),text);if(found!=null)return found;}return null;
    }
    private EditText edit(View view){if(view instanceof EditText)return (EditText)view;
        if(view instanceof ViewGroup)for(int i=0;i<((ViewGroup)view).getChildCount();i++){EditText found=edit(((ViewGroup)view).getChildAt(i));if(found!=null)return found;}return null;
    }
    private void open(View root,String label){((View)find(root,label).getParent().getParent()).performClick();Shadows.shadowOf(Looper.getMainLooper()).idle();}
    private void snapshot(View view,String name)throws Exception{String directory=System.getenv("STREAMHUB_RENDER_ROOT");if(directory==null)return;
        File file=new File(directory,name);assertTrue(file.getParentFile().isDirectory()||file.getParentFile().mkdirs());
        Bitmap image=Bitmap.createBitmap(view.getWidth(),view.getHeight(),Bitmap.Config.ARGB_8888);view.draw(new Canvas(image));
        try(var out=new FileOutputStream(file)){image.compress(Bitmap.CompressFormat.PNG,100,out);}finally{image.recycle();}
    }

    @Test public void currentFpsAndDecimalBitrateSurviveOpeningSavingAndReopening()throws Exception{
        var app=RuntimeEnvironment.getApplication();
        HubAdvancedPreferences.save(app,java.util.Map.of("list_fps","90","custom_refresh_rate","90","seekbar_bitrate_kbps",49500,"seekbar_metered_bitrate_kbps",14250));
        try(var controller=Robolectric.buildActivity(HubActivity.class).create()){
            HubActivity activity=controller.get();page(activity,5);View root=root(activity);layout(root,800,1200);
            open(root,"帧率");AlertDialog dialog=ShadowAlertDialog.getLatestAlertDialog();assertEquals(2,dialog.getListView().getCheckedItemPosition());
            dialog.getListView().performItemClick(null,1,1);assertFalse(dialog.isShowing());open(root,"帧率");
            dialog=ShadowAlertDialog.getLatestAlertDialog();assertEquals(1,dialog.getListView().getCheckedItemPosition());dialog.dismiss();
            for(String title:new String[]{"码率","移动网络码率"}){
                open(root,title);dialog=ShadowAlertDialog.getLatestAlertDialog();EditText value=edit(dialog.getWindow().getDecorView());
                assertEquals(title.equals("码率")?"49.5":"14.25",value.getText().toString());
                dialog.getButton(AlertDialog.BUTTON_POSITIVE).performClick();assertFalse(dialog.isShowing());
            }
            assertEquals(49500,HubAdvancedPreferences.active(app).getInt("seekbar_bitrate_kbps",0));
            assertEquals(14250,HubAdvancedPreferences.active(app).getInt("seekbar_metered_bitrate_kbps",0));
            open(root,"码率");dialog=ShadowAlertDialog.getLatestAlertDialog();EditText value=edit(dialog.getWindow().getDecorView());
            value.setText("150.001");dialog.getButton(AlertDialog.BUTTON_POSITIVE).performClick();assertTrue(dialog.isShowing());assertNotNull(value.getError());
            assertEquals(49500,HubAdvancedPreferences.active(app).getInt("seekbar_bitrate_kbps",0));dialog.dismiss();
        }
    }

    @Test public void letterboxPanelHasRoomForContentAndItsLastActionIsNotUnderTheFooter()throws Exception{
        try(var controller=Robolectric.buildActivity(Activity.class).setup()){
            Activity activity=controller.get();activity.getSharedPreferences("streamhub_controls",0).edit().putString("panel_layout",HubPanelLayout.BARS).commit();
            FrameLayout root=new FrameLayout(activity){@Override public void getWindowVisibleDisplayFrame(Rect out){out.set(0,0,getWidth(),getHeight());}};
            activity.setContentView(root);
            RtInputToolbar toolbar=new RtInputToolbar(activity,root,new RtInputToolbar.Callbacks(){
                public void showKeyboard(){}public void showFullKeyboard(){}public void sendText(String text){}public void sendKeys(short[] keys){}
                public Rect videoBounds(){return new Rect(0,350,800,850);}public boolean privacyAvailable(){return true;}
            });
            layout(root,800,1200);toolbar.show();toolbar.refreshLayout();layout(root,800,1200);
            LinearLayout panel=(LinearLayout)field(toolbar,"panel");ScrollView body=(ScrollView)field(toolbar,"body");
            assertTrue(panel.getHeight()>=1200*.45f);assertTrue("body="+body.getHeight(),body.getHeight()>=240);
            assertNull(find(root,"快捷键使用说明"));
            View tabs=(View)field(toolbar,"tabRow");
            int tabTop=tabs.getTop(), bodyTop=body.getTop(), bodyHeight=body.getHeight();
            toolbar.setWarning(true,"解码或渲染负载较高");layout(root,800,1200);
            Button diagnostics=(Button)find(root,"诊断");
            assertTrue(diagnostics.getContentDescription().toString().contains("解码或渲染负载较高"));
            assertEquals(tabTop,tabs.getTop());assertEquals(bodyTop,body.getTop());assertEquals(bodyHeight,body.getHeight());
            LinearLayout fixed=(LinearLayout)field(toolbar,"fixed");
            for(int i=1;i<fixed.getChildCount();i++)assertEquals(8,fixed.getChildAt(i).getLeft()-fixed.getChildAt(i-1).getRight());
            find(root,"画面").performClick();Shadows.shadowOf(Looper.getMainLooper()).idle();layout(root,800,1200);
            for(String label:new String[]{"本机隐私屏","画质与编码设置"}){
                View target=find(root,label);Rect bounds=new Rect(0,0,target.getWidth(),target.getHeight());
                ((ViewGroup)body.getChildAt(0)).offsetDescendantRectToMyCoords(target,bounds);
                body.scrollTo(0,Math.max(0,bounds.bottom-body.getHeight()));layout(root,800,1200);
                Rect visible=new Rect(),footer=new Rect();assertTrue(label,target.getGlobalVisibleRect(visible));assertEquals(label,target.getHeight(),visible.height());
                assertTrue(((View)field(toolbar,"footer")).getGlobalVisibleRect(footer));assertTrue(visible.bottom<=footer.top);
                snapshot(root,label.equals("本机隐私屏")?"letterbox-panel-privacy.png":"letterbox-panel.png");
            }
            toolbar.dispose();
        }
    }

    @Test public void homeBackClosesOnlyTheOverflowAndNeverFinishesTheTask()throws Exception{
        try(var controller=Robolectric.buildActivity(HubActivity.class).create()){
            HubActivity activity=controller.get();View root=root(activity);layout(root,800,1200);find(root,"⋯").performClick();
            assertNotNull(field(activity,"homeMenu"));activity.onBackPressed();assertNull(field(activity,"homeMenu"));assertEquals(0,field(activity,"currentPage"));
            activity.onBackPressed();assertFalse(activity.isFinishing());assertEquals(0,field(activity,"currentPage"));
        }
    }

    @Test public void secondaryHeadersMatchAdvancedSettingsInPortraitAndLandscape()throws Exception{
        for(int[] window:new int[][]{{800,1200},{1200,800}}){
            RuntimeEnvironment.setQualifiers("w"+window[0]+"dp-h"+window[1]+"dp-"+(window[0]>window[1]?"land":"port")+"-mdpi");
            int[] advanced=new int[2];
            try(var controller=Robolectric.buildActivity(HubActivity.class).create()){
                page(controller.get(),5);View root=root(controller.get());layout(root,window[0],window[1]);described(root,"返回设置").getLocationOnScreen(advanced);
            }
            try(var controller=Robolectric.buildActivity(HubKeyboardLayoutsActivity.class).create()){
                View root=root(controller.get());layout(root,window[0],window[1]);int[] actual=new int[2];described(root,"返回上一级").getLocationOnScreen(actual);assertArrayEquals(advanced,actual);
            }
            try(var controller=Robolectric.buildActivity(HubCompatibleActivity.class).create()){
                View root=root(controller.get());layout(root,window[0],window[1]);int[] actual=new int[2];described(root,"返回上一级").getLocationOnScreen(actual);assertArrayEquals(advanced,actual);
            }
        }
    }

    @Test public void updateFeedbackAppearsBelowActionsOnlyWhenThereIsAResult() throws Exception {
        var app = RuntimeEnvironment.getApplication();
        long lastChecked = System.currentTimeMillis() - 86400000;
        var preferences = app.getSharedPreferences("streamhub-updates", 0);
        preferences.edit().putLong("lastChecked", lastChecked).commit();
        HubUpdates updates = HubUpdates.get(app);
        try (var controller = Robolectric.buildActivity(Activity.class).setup()) {
            Activity activity = controller.get();
            HubUpdatePanel panel = new HubUpdatePanel(activity, updates, () -> true);
            activity.setContentView(panel);
            View check = find(panel, "检查更新");
            assertNotNull(check);
            TextView feedback = (TextView) field(panel, "state");
            assertEquals("", updates.message());
            assertEquals(View.GONE, feedback.getVisibility());
            assertNotNull(find(panel, updates.currentVersion()));
            int visibleTextRows = 0;
            for (int i = 0; i < panel.getChildCount(); i++) {
                View child = panel.getChildAt(i);
                if (child.getVisibility() == View.VISIBLE && child instanceof TextView && !(child instanceof Button)) {
                    visibleTextRows++;
                }
            }
            assertEquals(1, visibleTextRows);

            var failed = HubUpdates.class.getDeclaredMethod("failed", Exception.class);
            failed.setAccessible(true);
            var setFeedback = HubUpdates.class.getDeclaredMethod("state", String.class);
            setFeedback.setAccessible(true);
            try {
                failed.invoke(updates, new IOException("更新清单签名、版本或下载地址无效，已停止更新"));
                Shadows.shadowOf(Looper.getMainLooper()).idle();
                assertEquals(View.VISIBLE, feedback.getVisibility());
                assertEquals("暂时无法检查更新，请稍后重试。", feedback.getText().toString());
                assertTrue(panel.indexOfChild(feedback) > panel.indexOfChild(check));
                assertEquals(lastChecked, preferences.getLong("lastChecked", 0));
                assertFalse(updates.ready());
                assertNull(updates.available());
            } finally {
                setFeedback.invoke(updates, "");
                Shadows.shadowOf(Looper.getMainLooper()).idle();
            }
            assertEquals(View.GONE, feedback.getVisibility());
        }
    }

    @Test public void updateCardBalancesVisibleTextWithoutShrinkingTheCheckTarget()throws Exception{
        try(var controller=Robolectric.buildActivity(HubActivity.class).create()){
            HubActivity activity=controller.get();page(activity,2);
            for(int[] size:new int[][]{{800,1200},{480,800}}){
                View root=root(activity);layout(root,size[0],size[1]);
                Button check=(Button)find(root,"检查更新");assertNotNull(check);
                HubUpdatePanel panel=(HubUpdatePanel)check.getParent();
                View card=(View)panel.getParent();TextView version=(TextView)panel.getChildAt(0);
                Rect topGlyphs=new Rect(), bottomGlyphs=new Rect();
                version.getPaint().getTextBounds(version.getText().toString(),0,version.length(),topGlyphs);
                check.getPaint().getTextBounds(check.getText().toString(),0,check.length(),bottomGlyphs);
                int top=panel.getTop()+version.getTop()+version.getBaseline()+topGlyphs.top;
                int bottom=card.getHeight()-panel.getTop()-check.getTop()-check.getBaseline()-bottomGlyphs.bottom;
                assertTrue("card insets: "+top+" / "+bottom,Math.abs(top-bottom)<=6);
                assertTrue(check.getHeight()>=48);
                snapshot(card,"update-card-"+size[0]+".png");
            }
        }
    }
}
