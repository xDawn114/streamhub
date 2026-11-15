package com.limelight.streamhub;

import android.app.Activity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.Switch;
import android.widget.TextView;
import org.json.JSONObject;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.Robolectric;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.annotation.Config;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=28,manifest=Config.NONE,application=android.app.Application.class)
public class HubHostPolicyTest {
    private Activity activity() {
        Activity result=Robolectric.buildActivity(Activity.class).setup().get();
        result.getSharedPreferences("streamhub-device",0).edit().clear().putString("invitation","fixture-binding")
                .putString("deviceId","fixture-pc").commit();
        result.getSharedPreferences("streamhub_controls",0).edit().clear().putBoolean("lock_on_exit",true).commit();
        return result;
    }
    private JSONObject info(boolean managed)throws Exception {
        return new JSONObject().put("deviceId","fixture-pc").put("capabilities",new JSONObject().put("hostManagedPolicy",managed))
                .put("policy",new JSONObject().put("lockOnExit",false).put("automaticLoginEnabled",false).put("revision",3));
    }
    @Test public void newHostSettingsCannotBeOverriddenByExistingAndroidPreferences()throws Exception {
        Activity activity=activity();HubStore store=new HubStore(activity);store.rememberHostPolicy(info(true));
        View view=HubLoginSettings.view(activity,false,null);
        assertEquals("host",store.hostPolicyMode());
        assertNull(find(view,"退出远控后自动锁定电脑"));assertNull(find(view,"锁屏密码自动填入"));assertNull(find(view,"设置密码"));
        assertTrue(activity.getSharedPreferences("streamhub_controls",0).getBoolean("lock_on_exit",false));
        assertTrue(allText(view).contains("已移至电脑端"));assertTrue(allText(view).contains("不会自动上传"));
    }
    @Test public void unknownHostShowsNoWritableSecurityPolicyUntilAuthenticatedNegotiation() {
        Activity activity=activity();View view=HubLoginSettings.view(activity,false,null);
        assertEquals("unknown",new HubStore(activity).hostPolicyMode());assertNull(find(view,"设置密码"));
        assertTrue(allText(view).contains("连接时会核验电脑版本"));
    }
    @Test public void oldHostKeepsExplicitLegacyControlsAndPreferences()throws Exception {
        Activity activity=activity();new HubStore(activity).rememberHostPolicy(info(false));
        View view=HubLoginSettings.view(activity,false,null);
        Switch control=(Switch)find(view,"退出远控后自动锁定电脑");assertNotNull(control);assertTrue(control.isChecked());
        control.setChecked(false);assertFalse(activity.getSharedPreferences("streamhub_controls",0).getBoolean("lock_on_exit",true));
        assertNotNull(find(view,"设置密码"));
    }
    @Test public void metadataForAnotherComputerCannotSwitchCurrentBindingBackToLegacy()throws Exception {
        Activity activity=activity();HubStore store=new HubStore(activity);store.rememberHostPolicy(info(true));
        store.rememberHostPolicy(info(false).put("deviceId","foreign-pc"));assertEquals("host",store.hostPolicyMode());
        store.put("deviceId","other-pc");assertEquals("unknown",store.hostPolicyMode());
    }
    @Test public void immediateLockRemainsAvailableForHostManagedPolicy()throws Exception {
        Activity activity=activity();new HubStore(activity).rememberHostPolicy(info(true));int[] calls={0};
        View view=HubLoginSettings.view(activity,true,()->calls[0]++);
        ((Button)find(view,"立即锁定电脑")).performClick();assertEquals(1,calls[0]);assertNull(find(view,"设置密码"));
    }
    private static View find(View view,String text) {
        if(view instanceof TextView && text.contentEquals(((TextView)view).getText()))return view;
        if(view instanceof ViewGroup){ViewGroup group=(ViewGroup)view;for(int i=0;i<group.getChildCount();i++){View found=find(group.getChildAt(i),text);if(found!=null)return found;}}return null;
    }
    private static String allText(View view) {
        StringBuilder text=new StringBuilder();if(view instanceof TextView)text.append(((TextView)view).getText());
        if(view instanceof ViewGroup){ViewGroup group=(ViewGroup)view;for(int i=0;i<group.getChildCount();i++)text.append(allText(group.getChildAt(i)));}return text.toString();
    }
}
