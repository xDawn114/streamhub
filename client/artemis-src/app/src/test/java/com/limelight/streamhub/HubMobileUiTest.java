package com.limelight.streamhub;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.res.Configuration;
import android.graphics.Rect;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.Robolectric;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.annotation.Config;
import org.robolectric.annotation.GraphicsMode;
import org.robolectric.shadows.ShadowAlertDialog;
import static org.junit.Assert.*;

/** Actual native view trees; unbound stores and create-only hub avoid starting any network. */
@RunWith(RobolectricTestRunner.class)
@Config(sdk=34, application=android.app.Application.class, qualifiers="w360dp-h720dp-port-mdpi")
@GraphicsMode(GraphicsMode.Mode.NATIVE)
public class HubMobileUiTest {
    private void window(int width, int height, float scale) {
        RuntimeEnvironment.setQualifiers("w"+width+"dp-h"+height+"dp-"+(width>height?"land":"port")+"-mdpi");
        Configuration config = new Configuration(RuntimeEnvironment.getApplication().getResources().getConfiguration());
        config.fontScale=scale;
        RuntimeEnvironment.getApplication().getResources().updateConfiguration(config,
                RuntimeEnvironment.getApplication().getResources().getDisplayMetrics());
    }
    private void layout(View view,int width,int height) {
        for(int i=0;i<3;i++) {
            view.measure(View.MeasureSpec.makeMeasureSpec(width,View.MeasureSpec.EXACTLY),
                    View.MeasureSpec.makeMeasureSpec(height,View.MeasureSpec.EXACTLY));
            view.layout(0,0,width,height);
        }
    }
    private void snapshot(View view,String name) {
        String directory=System.getenv("STREAMHUB_RENDER_ROOT");if(directory==null || directory.isEmpty())return;
        java.io.File root=new java.io.File(directory);assertTrue(root.isDirectory() || root.mkdirs());
        android.graphics.Bitmap image=android.graphics.Bitmap.createBitmap(view.getWidth(),view.getHeight(),android.graphics.Bitmap.Config.ARGB_8888);
        view.draw(new android.graphics.Canvas(image));
        try(java.io.OutputStream output=new java.io.FileOutputStream(new java.io.File(root,name))) {
            assertTrue(image.compress(android.graphics.Bitmap.CompressFormat.PNG,100,output));
        }catch(java.io.IOException error){throw new AssertionError(error);}finally{image.recycle();}
    }
    private View content(Activity activity) { return ((ViewGroup)activity.findViewById(android.R.id.content)).getChildAt(0); }
    private TextView text(View root,String value) {
        if(root.getVisibility()!=View.VISIBLE)return null;
        if(root instanceof TextView && value.contentEquals(((TextView)root).getText()))return (TextView)root;
        if(root instanceof ViewGroup)for(int i=0;i<((ViewGroup)root).getChildCount();i++) {
            TextView found=text(((ViewGroup)root).getChildAt(i),value);if(found!=null)return found;
        }
        return null;
    }
    private Button button(View root,String value) {
        if(root.getVisibility()!=View.VISIBLE)return null;
        if(root instanceof Button && value.contentEquals(((Button)root).getText()))return (Button)root;
        if(root instanceof ViewGroup)for(int i=0;i<((ViewGroup)root).getChildCount();i++) {
            Button found=button(((ViewGroup)root).getChildAt(i),value);if(found!=null)return found;
        }
        return null;
    }
    private void readable(View root) {
        if(root.getVisibility()!=View.VISIBLE)return;
        if(root instanceof TextView) {
            TextView view=(TextView)root;
            if(view.getLayout()!=null && view.getText().length()>0)assertTrue("clipped label: "+view.getText()+" height="+view.getHeight()+" padding="+view.getCompoundPaddingTop()+"/"+view.getCompoundPaddingBottom()+" layout="+view.getLayout().getHeight()+" lines="+view.getLayout().getLineCount()+" scale="+view.getResources().getConfiguration().fontScale+" density="+view.getResources().getDisplayMetrics().density,
                    view.getLayout().getHeight()<=view.getHeight()-view.getCompoundPaddingTop()-view.getCompoundPaddingBottom()+2);
        }
        if(root instanceof Button)assertTrue("small target: "+((Button)root).getText(),root.getHeight()>=48);
        if(root instanceof ViewGroup)for(int i=0;i<((ViewGroup)root).getChildCount();i++) {
            View child=((ViewGroup)root).getChildAt(i);
            if(child.getVisibility()==View.VISIBLE && root.getWidth()>0)
                assertTrue("horizontal overflow in "+root.getClass().getSimpleName(),child.getLeft()>=0 && child.getRight()<=root.getWidth());
            readable(child);
        }
    }
    @Test public void phoneNavigationFitsPortraitLandscapeAndLargeTextWithoutLosingSelection() {
        for(int[] size:new int[][]{{320,640},{411,850},{800,360}})for(float scale:new float[]{1f,1.5f}) {
            window(size[0],size[1],scale);
            try(org.robolectric.android.controller.ActivityController<Activity> c=Robolectric.buildActivity(Activity.class).setup()) {
                Activity activity=c.get();final int[] selected={-1};
                HubNavigation nav=new HubNavigation(activity,new FrameLayout(activity),0,index->selected[0]=index);
                activity.setContentView(nav.view);layout(nav.view,size[0],size[1]);
                if(size[0]>size[1])assertEquals(80,nav.view.getChildAt(0).getWidth());
                for(String name:new String[]{"设备","连接","设置","文件"}) {
                    TextView label=text(nav.view,name);assertNotNull(label);
                    View target=(View)label.getParent();Rect bounds=new Rect();
                    assertTrue(target.getGlobalVisibleRect(bounds));assertTrue(bounds.width()>=48);assertTrue(bounds.height()>=48);
                    assertTrue(label.getBottom()<=target.getHeight());target.performClick();
                }
                assertEquals(3,selected[0]);nav.select(3);assertTrue(((View)text(nav.view,"文件").getParent()).isSelected());
                readable(nav.view);
            }
        }
    }
    @Test public void homeActionsReflowAndFullLabelsFitOnSmallPhonesInBothThemes() {
        for(int[] size:new int[][]{{320,640},{411,850}})for(int theme:new int[]{HubTheme.LIGHT,HubTheme.DARK}) {
            window(size[0],size[1],1.5f);HubTheme.setMode(RuntimeEnvironment.getApplication(),theme);
            try(org.robolectric.android.controller.ActivityController<HubActivity> c=Robolectric.buildActivity(HubActivity.class).create()) {
                View root=content(c.get());layout(root,size[0],size[1]);
                TextView remote=text(root,"远程控制"),extended=text(root,"用作扩展屏");
                assertNotNull(remote);assertNotNull(extended);
                View first=(View)remote.getParent(),second=(View)extended.getParent();
                assertTrue(second.getTop()>=first.getBottom()+8);readable(root);
                assertNotNull(text(root,"文件传输"));
                snapshot(root,"phone-home-"+size[0]+"-"+(theme==HubTheme.LIGHT?"light":"dark")+"-font150.png");
            }finally{HubTheme.setMode(RuntimeEnvironment.getApplication(),HubTheme.SYSTEM);}
        }
    }
    @Test public void wideTabletKeepsFullRailAndSideBySideHomeActions() {
        window(1280,800,1f);
        try(org.robolectric.android.controller.ActivityController<HubActivity> c=Robolectric.buildActivity(HubActivity.class).create()) {
            View root=content(c.get());layout(root,1280,800);
            LinearLayout nav=(LinearLayout)((ViewGroup)root).getChildAt(0);
            assertEquals(132,nav.getChildAt(0).getWidth());
            View first=(View)text(root,"远程控制").getParent(),second=(View)text(root,"用作扩展屏").getParent();
            assertEquals(first.getTop(),second.getTop());assertTrue(second.getLeft()>=first.getRight()+8);readable(root);
            snapshot(root,"tablet-home-landscape.png");
        }
    }
    @Test public void shortCodeFormScrollsAndDoesNotClipTheSixDigitsOnPhone() throws Exception {
        window(320,640,1.5f);
        try(org.robolectric.android.controller.ActivityController<HubActivity> c=Robolectric.buildActivity(HubActivity.class).create()) {
            java.lang.reflect.Method show=HubActivity.class.getDeclaredMethod("showNearbyAddress");show.setAccessible(true);show.invoke(c.get());
            AlertDialog dialog=ShadowAlertDialog.getLatestAlertDialog();
            View root=dialog.getWindow().getDecorView();layout(root,280,420);
            EditText code=findCode(root);assertNotNull(code);code.setText("012345");layout(root,280,420);
            assertEquals("012345",code.getText().toString());assertTrue(code.getHeight()>=64);
            View parent=(View)code.getParent();assertTrue(parent.getParent() instanceof ScrollView);
            assertTrue(dialog.getButton(AlertDialog.BUTTON_POSITIVE).isEnabled());
            assertTrue(code.getLayout().getLineWidth(0)<=code.getWidth()-code.getCompoundPaddingLeft()-code.getCompoundPaddingRight());
            code.requestFocus();code.requestRectangleOnScreen(new Rect(0,0,code.getWidth(),code.getHeight()),true);
            snapshot(root,"phone-shortcode-font150.png");
            // The create-only host is not a real displayed window. Check the form's viewport,
            // rather than asking WindowManager for visibility on a nonexistent physical screen.
            ScrollView scroll=(ScrollView)parent.getParent();Rect shown=new Rect(0,0,code.getWidth(),code.getHeight());
            scroll.offsetDescendantRectToMyCoords(code,shown);shown.offset(-scroll.getScrollX(),-scroll.getScrollY());
            assertTrue(shown.toString(),shown.top>=0 && shown.bottom<=scroll.getHeight());
            dialog.dismiss();
        }
    }
    private EditText findCode(View root) {
        if(root instanceof EditText && ((EditText)root).getHint()!=null && ((EditText)root).getHint().toString().contains("012345"))return (EditText)root;
        if(root instanceof ViewGroup)for(int i=0;i<((ViewGroup)root).getChildCount();i++) {EditText result=findCode(((ViewGroup)root).getChildAt(i));if(result!=null)return result;}
        return null;
    }
    @Test public void filePageTabsAndHeaderFitACompactLargeTextWindow() {
        window(320,640,1.5f);
        try(org.robolectric.android.controller.ActivityController<HubFilesActivity> c=Robolectric.buildActivity(HubFilesActivity.class).setup()) {
            View root=content(c.get());layout(root,320,640);readable(root);
            assertNotNull(text(root,"远程文件"));text(root,"传输任务").performClick();layout(root,320,640);
            assertTrue(text(root,"选择文件发到电脑").isShown());readable(root);
            snapshot(root,"phone-files-font150.png");
        }
    }
    @Test public void settingsAndConnectionStayReadableWithLargePhoneText() {
        window(320,640,1.5f);
        try(org.robolectric.android.controller.ActivityController<HubActivity> c=Robolectric.buildActivity(HubActivity.class).create()) {
            View root=content(c.get());layout(root,320,640);
            for(int page:new int[]{1,2}) {
                try{java.lang.reflect.Method show=HubActivity.class.getDeclaredMethod("showPage",int.class);show.setAccessible(true);show.invoke(c.get(),page);}
                catch(Exception error){throw new AssertionError(error);}
                layout(root,320,640);readable(root);
                snapshot(root,"phone-"+(page==1?"connection":"settings")+"-font150.png");
            }
        }
    }
    @Test public void landscapeKeyboardKeepsCompleteExitAndKeyboardActions() {
        window(800,360,1f);
        try(org.robolectric.android.controller.ActivityController<Activity> c=Robolectric.buildActivity(Activity.class).setup()) {
            Activity activity=c.get();final int[] visibleHeight={360},hidden={0};
            FrameLayout root=new FrameLayout(activity) {
                @Override public void getWindowVisibleDisplayFrame(Rect out){out.set(0,0,getWidth(),visibleHeight[0]);}
            };activity.setContentView(root);
            RtInputToolbar toolbar=new RtInputToolbar(activity,root,new RtInputToolbar.Callbacks(){
                public void showKeyboard(){}public void showFullKeyboard(){}public void sendText(String text){}public void sendKeys(short[] keys){}
                public void hideKeyboard(){hidden[0]++;}
            });
            layout(root,800,360);toolbar.show();layout(root,800,360);
            visibleHeight[0]=140;toolbar.refreshLayout();layout(root,800,360);
            for(String action:new String[]{"收起键盘","剪贴板","断开连接","收起"}) {
                Button target=button(root,action);assertNotNull(target);Rect bounds=new Rect();
                assertTrue(target.getGlobalVisibleRect(bounds));
                assertEquals("partially clipped: "+action,target.getHeight(),bounds.height());
                assertTrue(bounds.height()>=48);assertTrue(bounds.bottom<=visibleHeight[0]);
            }
            snapshot(root,"phone-controls-landscape-ime.png");
            button(root,"收起键盘").performClick();assertEquals(1,hidden[0]);
            visibleHeight[0]=360;toolbar.refreshLayout();toolbar.show();layout(root,800,360);
            assertNotNull(button(root,"快捷键"));assertNotNull(button(root,"键盘"));
            toolbar.dispose();
        }
    }
    @Test public void shortStreamWindowKeepsTabsFooterAndScrollableBodyReachable() {
        window(800,360,1.3f);
        try(org.robolectric.android.controller.ActivityController<Activity> c=Robolectric.buildActivity(Activity.class).setup()) {
            Activity activity=c.get();FrameLayout root=new FrameLayout(activity) {
                @Override public void getWindowVisibleDisplayFrame(Rect out){out.set(0,0,getWidth(),getHeight());}
            };activity.setContentView(root);
            RtInputToolbar toolbar=new RtInputToolbar(activity,root,new RtInputToolbar.Callbacks(){
                public void showKeyboard(){}public void showFullKeyboard(){}public void sendText(String text){}public void sendKeys(short[] keys){}
            });
            layout(root,800,320);toolbar.show();layout(root,800,320);toolbar.refreshLayout();layout(root,800,320);
            for(String name:new String[]{"快捷键","输入","画面"}) {
                text(root,name).performClick();layout(root,800,320);
                for(String action:new String[]{"收起","键盘","剪贴板","断开连接"}) {
                    TextView button=button(root,action);Rect bounds=new Rect();
                    assertTrue("not visible: "+action,button.getGlobalVisibleRect(bounds));assertTrue("clipped target: "+action+" height="+bounds.height()+" scale="+activity.getResources().getDisplayMetrics().density,bounds.height()>=48);
                }
            }
            View panel=((ViewGroup)root).getChildAt(0);ScrollView body=null;
            for(int i=0;i<((ViewGroup)panel).getChildCount();i++)if(((ViewGroup)panel).getChildAt(i) instanceof ScrollView)body=(ScrollView)((ViewGroup)panel).getChildAt(i);
            assertNotNull(body);assertTrue("controls body needs a usable viewport",body.getHeight()>=96);
            snapshot(root,"phone-controls-landscape.png");
            toolbar.dispose();
        }
    }
}
