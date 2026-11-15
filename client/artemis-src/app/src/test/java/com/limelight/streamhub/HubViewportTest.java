package com.limelight.streamhub;

import android.content.Intent;
import android.view.InputDevice;
import android.view.MotionEvent;
import android.view.View;
import android.widget.FrameLayout;
import com.limelight.Game;
import com.limelight.binding.input.touch.TouchContext;
import com.limelight.nvstream.NvConnection;
import com.limelight.nvstream.jni.MoonBridge;
import com.limelight.preferences.PreferenceConfiguration;
import com.limelight.ui.StreamView;
import com.limelight.utils.PanZoomHandler;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.Robolectric;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.annotation.Config;
import org.robolectric.util.ReflectionHelpers;
import static org.junit.Assert.*;
import static org.mockito.Mockito.*;

/** Actual Game motion routing with synthetic gestures and a mocked connection. */
@RunWith(RobolectricTestRunner.class)
@Config(sdk=28,manifest=Config.NONE,application=android.app.Application.class)
public class HubViewportTest {
    private static class Fixture {
        final Game game = spy(Robolectric.buildActivity(Game.class,new Intent().putExtra("HubManaged",true)).get());
        final FrameLayout root = new FrameLayout(game);
        final View background = new View(game);
        final StreamView stream = new StreamView(game);
        final PreferenceConfiguration preferences = new PreferenceConfiguration();
        final NvConnection connection = mock(NvConnection.class);
        final TouchContext first = mock(TouchContext.class), second = mock(TouchContext.class);
        final PanZoomHandler viewport;
        Fixture() {
            doNothing().when(game).updatePipAutoEnter();
            root.addView(background,new FrameLayout.LayoutParams(-1,-1));
            root.addView(stream,new FrameLayout.LayoutParams(-1,-1));
            game.setContentView(root); layout(1000,600,1000,600);
            preferences.ignoreSynthEvents=false;
            ReflectionHelpers.setField(game,"prefConfig",preferences);
            ReflectionHelpers.setField(game,"streamView",stream);
            ReflectionHelpers.setField(game,"conn",connection);
            ReflectionHelpers.setField(game,"connected",true);
            ReflectionHelpers.setField(game,"grabbedInput",true);
            TouchContext[] contexts=ReflectionHelpers.getField(game,"touchContextMap");
            contexts[0]=first; contexts[1]=second;
            when(first.getActionIndex()).thenReturn(0);when(second.getActionIndex()).thenReturn(1);
            viewport=new PanZoomHandler(game,game,stream,preferences);
            ReflectionHelpers.setField(game,"panZoomHandler",viewport);
            viewport.handleSurfaceChange();
        }
        void layout(int width,int height,int videoWidth,int videoHeight) {
            root.layout(0,0,width,height);background.layout(0,0,width,height);stream.layout(0,0,videoWidth,videoHeight);
        }
        void pointer(View source,int action,long time,int tool,int inputSource,float... xy) {
            MotionEvent event=event(action,time,tool,inputSource,xy);
            try {assertTrue(game.handleMotionEvent(source,event));} finally {event.recycle();}
        }
        void touch(int action,long time,float... xy) {pointer(background,action,time,MotionEvent.TOOL_TYPE_FINGER,InputDevice.SOURCE_TOUCHSCREEN,xy);}
    }
    private static MotionEvent event(int action,long time,int tool,int source,float... xy) {
        int count=xy.length/2;
        MotionEvent.PointerProperties[] properties=new MotionEvent.PointerProperties[count];
        MotionEvent.PointerCoords[] coordinates=new MotionEvent.PointerCoords[count];
        for(int i=0;i<count;i++) {
            properties[i]=new MotionEvent.PointerProperties();properties[i].id=i;properties[i].toolType=tool;
            coordinates[i]=new MotionEvent.PointerCoords();coordinates[i].x=xy[2*i];coordinates[i].y=xy[2*i+1];coordinates[i].pressure=1;
        }
        return MotionEvent.obtain(1000,time,action,count,properties,coordinates,0,0,1,1,1,0,source,0);
    }
    @Test public void adjustmentConsumesTapLongPressPinchAndStylusBeforeAnyRemoteRouting() {
        Fixture f=new Fixture(); f.game.toggleZoomMode();
        verify(f.first).cancelTouch();verify(f.second).cancelTouch();
        clearInvocations(f.connection,f.first,f.second);
        f.preferences.ignoreSynthEvents=true;
        f.touch(MotionEvent.ACTION_DOWN,1000,200,200);
        f.touch(MotionEvent.ACTION_MOVE,1800,220,220);
        f.touch(MotionEvent.ACTION_POINTER_DOWN | (1 << MotionEvent.ACTION_POINTER_INDEX_SHIFT),1850,200,200,600,200);
        f.touch(MotionEvent.ACTION_MOVE,1900,100,200,700,200);
        f.touch(MotionEvent.ACTION_MOVE,1920,50,200,750,200);
        assertTrue("pinch enlarges local picture",f.viewport.getScaleFactor()>1);
        f.touch(MotionEvent.ACTION_POINTER_UP | (1 << MotionEvent.ACTION_POINTER_INDEX_SHIFT),1950,100,200,700,200);
        f.touch(MotionEvent.ACTION_UP,2000,100,200);
        f.pointer(f.background,MotionEvent.ACTION_DOWN,2100,MotionEvent.TOOL_TYPE_STYLUS,InputDevice.SOURCE_STYLUS,250,250);
        f.pointer(f.background,MotionEvent.ACTION_UP,2200,MotionEvent.TOOL_TYPE_STYLUS,InputDevice.SOURCE_STYLUS,250,250);
        verifyNoInteractions(f.connection,f.first,f.second);
    }
    @Test public void finishKeepsTransformDropsOldGestureTailAndMapsNextCompatibilityClick() {
        Fixture f=new Fixture(); f.game.toggleZoomMode(); f.viewport.setInitialZoomAndPan(2,-200,-100);
        f.touch(MotionEvent.ACTION_DOWN,1000,300,200);
        f.game.toggleZoomMode();clearInvocations(f.connection,f.first,f.second);
        f.touch(MotionEvent.ACTION_MOVE,1050,320,220);f.touch(MotionEvent.ACTION_UP,1100,320,220);
        verifyNoInteractions(f.connection,f.first,f.second);
        assertEquals(2,f.viewport.getScaleFactor(),0);assertEquals(-200,f.stream.getX(),0);
        f.touch(MotionEvent.ACTION_DOWN,1200,300,200);
        verify(f.first).touchDownEvent(250,150,1200,true);
    }
    @Test public void compatibilitySurfaceCoordinatesAreNotInverseTransformedTwice() {
        Fixture f=new Fixture();f.viewport.setInitialZoomAndPan(2,-200,-100);
        f.pointer(f.stream,MotionEvent.ACTION_DOWN,1000,MotionEvent.TOOL_TYPE_FINGER,InputDevice.SOURCE_TOUCHSCREEN,250,150);
        verify(f.first).touchDownEvent(250,150,1000,true);
    }
    @Test public void nativeTouchAfterFinishUsesTheZoomedContentCoordinate() {
        Fixture f=new Fixture();f.preferences.enableMultiTouchScreen=true;
        f.game.getSharedPreferences("streamhub_controls",0).edit().putBoolean("touch_compatibility",false).commit();
        f.game.toggleZoomMode();f.viewport.setInitialZoomAndPan(2,-200,-100);f.game.toggleZoomMode();
        clearInvocations(f.connection);
        f.touch(MotionEvent.ACTION_DOWN,1000,300,200);
        verify(f.connection).sendTouchEvent(eq(MoonBridge.LI_TOUCH_EVENT_DOWN),eq(0),eq(0.25f),eq(0.25f),anyFloat(),anyFloat(),anyFloat(),anyShort());
    }
    @Test public void buttonZoomClampsAtFourAndResetRestoresLetterboxedWholePicture() {
        Fixture f=new Fixture();f.layout(600,1000,600,360);f.viewport.handleSurfaceChange();
        f.viewport.scaleBy(100);assertEquals(4,f.viewport.getScaleFactor(),0);
        f.viewport.setInitialZoomAndPan(2,99999,-99999);
        assertTrue(f.stream.getX()<=0 && f.stream.getX()>=-600);
        assertEquals(140,f.stream.getY(),0);
        f.viewport.reset();assertEquals(1,f.viewport.getScaleFactor(),0);assertEquals(320,f.stream.getY(),0);
        f.viewport.setInitialZoomAndPan(Float.NaN,Float.NaN,Float.POSITIVE_INFINITY);
        assertEquals(1,f.stream.getScaleX(),0);assertEquals(320,f.stream.getY(),0);
    }
    @Test public void rotationReclampsWithoutDiscardingMagnification() {
        Fixture f=new Fixture();f.viewport.setInitialZoomAndPan(3,-2000,-1200);
        f.layout(600,1000,600,360);f.viewport.handleSurfaceChange();
        assertEquals(3,f.viewport.getScaleFactor(),0);
        assertTrue(f.stream.getX()<=0 && f.stream.getX()>=-1200);
        assertTrue(f.stream.getY()<=0 && f.stream.getY()>=-80);
    }
    @Test public void draggingAScaledSurfaceUsesParentPixelsInsteadOfMagnifyingTheDrag() {
        Fixture f=new Fixture();f.game.toggleZoomMode();f.viewport.setInitialZoomAndPan(2,-500,-300);
        // Same physical drag (400,300)->(500,350), inverse-transformed by Android.
        f.pointer(f.stream,MotionEvent.ACTION_DOWN,1000,MotionEvent.TOOL_TYPE_FINGER,InputDevice.SOURCE_TOUCHSCREEN,450,300);
        f.pointer(f.stream,MotionEvent.ACTION_MOVE,1030,MotionEvent.TOOL_TYPE_FINGER,InputDevice.SOURCE_TOUCHSCREEN,500,325);
        assertEquals(-400,f.stream.getX(),1);assertEquals(-250,f.stream.getY(),1);
    }
}
