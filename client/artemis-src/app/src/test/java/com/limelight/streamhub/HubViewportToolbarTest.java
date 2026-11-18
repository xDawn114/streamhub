package com.limelight.streamhub;

import android.app.Activity;
import android.content.res.Configuration;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Rect;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.Switch;
import android.widget.TextView;
import java.io.File;
import java.io.FileOutputStream;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.Robolectric;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.annotation.Config;
import org.robolectric.annotation.GraphicsMode;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=34,manifest=Config.NONE,application=android.app.Application.class,qualifiers="w360dp-h720dp-port-mdpi")
@GraphicsMode(GraphicsMode.Mode.NATIVE)
public class HubViewportToolbarTest {
    private static class Fixture implements RtInputToolbar.Callbacks {
        final Activity activity=Robolectric.buildActivity(Activity.class).setup().get();
        final FrameLayout root=new FrameLayout(activity) {
            @Override public void getWindowVisibleDisplayFrame(Rect out) {out.set(0,0,getWidth(),getHeight());}
        };
        final RtInputToolbar toolbar;
        boolean zoom;float scale=1;int toggles,resets;
        Fixture() {activity.setContentView(root);toolbar=new RtInputToolbar(activity,root,this);layout(360,720);}
        void layout(int w,int h) {
            for(int i=0;i<3;i++) {root.measure(View.MeasureSpec.makeMeasureSpec(w,View.MeasureSpec.EXACTLY),View.MeasureSpec.makeMeasureSpec(h,View.MeasureSpec.EXACTLY));root.layout(0,0,w,h);toolbar.refreshLayout();}
        }
        void start() {toolbar.show();((Button)find(root,"画面")).performClick();((Switch)find(root,"调整视图（暂停触控）")).setChecked(true);}
        public void showKeyboard(){}public void showFullKeyboard(){}public void sendText(String text){}public void sendKeys(short[] keys){}
        public boolean zoomEnabled(){return zoom;}public float zoomScale(){return scale;}
        public void toggleZoom(){zoom=!zoom;toggles++;toolbar.setViewportState(zoom,scale);}
        public void resetZoom(){scale=1;resets++;toolbar.setViewportState(zoom,scale);}
        public void scaleZoom(float factor){scale=Math.max(1,Math.min(4,scale*factor));toolbar.setViewportState(zoom,scale);}
    }
    static View find(View root,String text) {
        if(root instanceof TextView && text.contentEquals(((TextView)root).getText()))return root;
        if(root instanceof ViewGroup)for(int i=0;i<((ViewGroup)root).getChildCount();i++) {
            View result=find(((ViewGroup)root).getChildAt(i),text);if(result!=null)return result;
        }
        return null;
    }
    private static Button button(Fixture f,String text){
        if("控制".equals(text)){try{var field=RtInputToolbar.class.getDeclaredField("entry");field.setAccessible(true);return (Button)field.get(f.toolbar);}catch(Exception error){throw new AssertionError(error);}}
        return (Button)find(f.root,text);
    }
    @Test public void adjustmentHasExplicitModeScaleResetAndFinishWithoutScrollingTheMenu() {
        Fixture f=new Fixture();f.start();f.layout(360,720);
        assertTrue(f.zoom);assertEquals(1,f.toggles);
        assertTrue(find(f.root,"调整视图 · 100%").isShown());
        assertFalse(button(f,"−").isEnabled());assertFalse(button(f,"还原").isEnabled());
        assertTrue(button(f,"完成").isShown());assertFalse(button(f,"控制").isShown());
        button(f,"＋").performClick();assertEquals(1.25f,f.scale,0);assertTrue(find(f.root,"调整视图 · 125%").isShown());
        button(f,"完成").performClick();assertFalse(f.zoom);assertEquals(1.25f,f.scale,0);
        assertTrue(button(f,"控制").isShown());assertFalse(button(f,"完成").isShown());
        f.toolbar.dispose();
    }
    @Test public void resetStaysInAdjustmentAndBackCompletesInsteadOfDisconnecting() {
        Fixture f=new Fixture();f.start();button(f,"＋").performClick();button(f,"还原").performClick();
        assertTrue(f.zoom);assertEquals(1,f.resets);assertEquals(1,f.scale,0);
        assertTrue(f.toolbar.collapseOrConsumeBack());assertFalse(f.zoom);
        assertFalse(f.toolbar.collapseOrConsumeBack());f.toolbar.dispose();
    }
    @Test public void narrowLandscapeAndLargeTextKeepActionsInsideVisibleWindow()throws Exception {
        for(int[] size:new int[][]{{360,720},{720,360},{720,180}}) {
            RuntimeEnvironment.setQualifiers("w"+size[0]+"dp-h"+size[1]+"dp-"+(size[0]>size[1]?"land":"port")+"-mdpi");
            android.content.res.Resources resources=RuntimeEnvironment.getApplication().getResources();
            Configuration config=new Configuration(resources.getConfiguration());config.fontScale=1.3f;
            resources.updateConfiguration(config,resources.getDisplayMetrics());
            Fixture f=new Fixture();f.start();
            f.layout(size[0],size[1]);
            for(String name:new String[]{"−","＋","还原","完成"}) {
                Button action=button(f,name);assertTrue(name,action.isShown());
                Rect bounds=new Rect(0,0,action.getWidth(),action.getHeight());f.root.offsetDescendantRectToMyCoords(action,bounds);
                assertTrue(name+" "+bounds,bounds.left>=0 && bounds.top>=0 && bounds.right<=size[0] && bounds.bottom<=size[1]);
                assertTrue(name,action.getHeight()>=48 && action.getWidth()>=48);
            }
            String output=System.getenv("STREAMHUB_RENDER_ROOT");
            if(output!=null) {
                new File(output).mkdirs();Bitmap bitmap=Bitmap.createBitmap(size[0],size[1],Bitmap.Config.ARGB_8888);f.root.draw(new Canvas(bitmap));
                try(FileOutputStream stream=new FileOutputStream(new File(output,"viewport-"+size[0]+"x"+size[1]+".png"))){bitmap.compress(Bitmap.CompressFormat.PNG,100,stream);}
            }
            f.toolbar.dispose();f.activity.finish();
        }
    }
    @Test public void pictureInPictureHidesAdjustmentAndDisposalDoesNotRestoreControls() {
        Fixture f=new Fixture();f.start();f.toolbar.setPictureInPicture(true);assertFalse(button(f,"完成").isShown());
        f.toolbar.setPictureInPicture(false);assertTrue(button(f,"完成").isShown());assertFalse(button(f,"控制").isShown());
        f.toolbar.dispose();assertFalse(button(f,"完成").isShown());
    }
}
