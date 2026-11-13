package com.limelight.streamhub;

import android.app.Activity;
import android.app.KeyguardManager;
import android.app.AlertDialog;
import android.content.Context;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.Switch;
import org.json.JSONObject;
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
public class HubDesktopInputTest {
    @Test public void lockedSnapshotStopsApplyingAfterExpiryAndNeverReturnsCaptionFallback()throws Exception {
        HubDesktopInput.Snapshot state=HubDesktopInput.Snapshot.parse(new JSONObject("{\"known\":true,\"locked\":true,\"captionButtons\":[{\"left\":0.8,\"top\":0,\"right\":1,\"bottom\":0.05}]}"),1000);
        assertTrue(state.locked(1001));assertFalse(state.captionButton(0.9f,0.02f,1001));
        assertFalse(state.locked(4501));assertFalse(state.locked(999));
    }
    @Test public void desktopCaptionWorkaroundIsLimitedToFreshMeasuredButtonRegion()throws Exception {
        HubDesktopInput.Snapshot state=HubDesktopInput.Snapshot.parse(new JSONObject("{\"known\":true,\"locked\":false,\"captionButtons\":[{\"left\":0.8,\"top\":0,\"right\":1,\"bottom\":0.05}]}"),1000);
        assertTrue(state.captionButton(0.9f,0.02f,1200));assertFalse(state.captionButton(0.7f,0.02f,1200));
        assertFalse(state.captionButton(0.9f,0.2f,1200));assertFalse(state.captionButton(0.9f,0.02f,4501));
    }
    @Test public void malformedAndWholeScreenCaptionRegionsCannotTurnNativeTouchIntoMouse()throws Exception {
        HubDesktopInput.Snapshot state=HubDesktopInput.Snapshot.parse(new JSONObject("{\"known\":true,\"captionButtons\":[{\"left\":0,\"top\":0,\"right\":1,\"bottom\":1},{\"left\":-1,\"top\":0,\"right\":0.2,\"bottom\":0.02}]}"),100);
        assertFalse(state.captionButton(0.1f,0.01f,200));assertFalse(HubDesktopInput.Snapshot.parse(null,100).locked(101));
    }
    @Test public void keepsCaptionRegionsForMultipleVisibleWindows()throws Exception {
        StringBuilder json=new StringBuilder("{\"known\":true,\"captionButtons\":[");
        for(int i=0;i<8;i++){if(i>0)json.append(',');float left=0.02f*i;json.append("{\"left\":").append(left).append(",\"top\":0.01,\"right\":").append(left+0.03f).append(",\"bottom\":0.05}");}
        HubDesktopInput.Snapshot state=HubDesktopInput.Snapshot.parse(new JSONObject(json.append("]}").toString()),100);
        assertTrue(state.captionButton(0.14f,0.02f,200));
    }
    private Activity activity(){Activity a=Robolectric.buildActivity(Activity.class).setup().get();
        KeyguardManager keyguard=(KeyguardManager)a.getSystemService(Context.KEYGUARD_SERVICE);
        shadowOf(keyguard).setIsDeviceSecure(true);shadowOf(keyguard).setIsDeviceLocked(false);return a;}
    private static class MemoryCodec implements HubLoginSecrets.Codec {
        final java.util.Map<String,String> memory=new java.util.HashMap<>();int seals,opens;
        public String seal(String scope,String value){seals++;String handle=java.util.UUID.randomUUID().toString();memory.put(scope+":"+handle,value);return handle;}
        public String open(String scope,String handle)throws Exception{opens++;String value=memory.get(scope+":"+handle);if(value==null)throw new java.io.IOException("invalid");return value;}
    }
    @Test public void savedSecretIsScopedDisabledAndForgottenWithoutPlaintextPreferences()throws Exception {
        Activity a=activity();MemoryCodec codec=new MemoryCodec();HubLoginSecrets first=new HubLoginSecrets(a,"pc-A",codec),second=new HubLoginSecrets(a,"pc-B",codec);
        first.save("test-password-中文🙂",false);assertEquals("test-password-中文🙂",first.password());
        assertTrue(first.enabled());assertFalse(first.submit());assertFalse(second.saved());
        assertFalse(a.getSharedPreferences("streamhub-login-secrets",0).getAll().toString().contains("test-password"));
        first.setEnabled(false);assertFalse(first.enabled());try{first.password();fail();}catch(java.io.IOException expected){}
        first.setEnabled(true);first.forget();assertFalse(first.saved());assertFalse(first.enabled());
    }
    @Test public void localLockAndInvalidCharactersBlockBeforeOpeningOrSavingSecrets()throws Exception {
        Activity a=activity();MemoryCodec codec=new MemoryCodec();HubLoginSecrets secrets=new HubLoginSecrets(a,"pc-A",codec);
        secrets.save("test-password",true);assertTrue(secrets.submit());
        KeyguardManager manager=(KeyguardManager)a.getSystemService(Context.KEYGUARD_SERVICE);shadowOf(manager).setIsDeviceLocked(true);
        try{secrets.password();fail();}catch(java.io.IOException expected){}assertEquals(0,codec.opens);
        try{secrets.save("other",false);fail();}catch(java.io.IOException expected){}assertEquals(1,codec.seals);
        for(String invalid:new String[]{"","a\nb","a\u0000b",new String(new char[257]).replace('\0','a')})
            try{HubLoginSecrets.validate(invalid);fail();}catch(IllegalArgumentException expected){}
    }
    @Test public void exitLockIsVisibleBeforeConnectionAndPersistsImmediately() {
        Activity a=activity();View content=HubLoginSettings.view(a,false,null);
        Switch lock=(Switch)find(content,"退出远控后自动锁定电脑");assertNotNull(lock);assertFalse(lock.isChecked());lock.setChecked(true);
        assertTrue(a.getSharedPreferences("streamhub_controls",0).getBoolean("lock_on_exit",false));
        assertTrue(((Switch)find(HubLoginSettings.view(a,false,null),"退出远控后自动锁定电脑")).isChecked());
    }
    @Test public void credentialDialogProtectsItsWindowAndDoesNotPrepopulatePassword() {
        Activity a=activity();a.getSharedPreferences("streamhub-device",0).edit().putString("invitation","test-only").putString("deviceId","pc-A").commit();
        ((Button)find(HubLoginSettings.view(a,false,null),"设置密码")).performClick();AlertDialog dialog=ShadowAlertDialog.getLatestAlertDialog();
        assertNotNull(dialog);assertTrue((dialog.getWindow().getAttributes().flags&WindowManager.LayoutParams.FLAG_SECURE)!=0);
        assertFalse(((android.widget.CheckBox)find(dialog.getWindow().getDecorView(),"填入后自动按 Enter 确认登录")).isChecked());
        dialog.dismiss();
    }
    private static View find(View view,String text){if(view instanceof android.widget.TextView && text.contentEquals(((android.widget.TextView)view).getText()))return view;
        if(view instanceof ViewGroup){ViewGroup group=(ViewGroup)view;for(int i=0;i<group.getChildCount();i++){View found=find(group.getChildAt(i),text);if(found!=null)return found;}}return null;}
}
