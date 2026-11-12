package com.limelight.streamhub;

import android.app.Activity;
import android.os.SystemClock;
import org.json.JSONArray;
import org.json.JSONObject;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

/** Desktop observation has its own worker; it cannot stall the streaming/lease heartbeat. */
public final class HubDesktopInput implements AutoCloseable {
    private final Activity activity;
    private final ScheduledExecutorService worker=Executors.newSingleThreadScheduledExecutor();
    private final String host,lease,deviceId;
    private final AtomicBoolean fillQueued=new AtomicBoolean();
    private volatile boolean closed,connected,filled,unsupported;
    private volatile HubControlClient client;
    private volatile Snapshot snapshot=Snapshot.UNKNOWN;
    public HubDesktopInput(Activity activity,String host,String lease) {
        this.activity=activity;this.host=host;this.lease=lease;deviceId=new HubStore(activity).get("deviceId","");
        worker.scheduleWithFixedDelay(this::observe,0,1,TimeUnit.SECONDS);
    }
    public void connected(){connected=true;}
    private boolean current(){return !closed && connected && !activity.isFinishing() && !activity.isDestroyed() &&
        deviceId.equals(new HubStore(activity).get("deviceId",""));}
    private HubControlClient client()throws Exception {
        if(client==null) {
            HubControlClient candidate=new HubControlClient(host,new HubStore(activity).invitation());
            JSONObject caps=candidate.probeInfo().optJSONObject("capabilities");
            if(caps==null || !caps.optBoolean("desktopInputState")){unsupported=true;candidate.cancel();throw new java.io.IOException("unsupported");}
            if(closed){candidate.cancel();throw new java.io.IOException("closed");}client=candidate;
        }
        return client;
    }
    private JSONObject body(String action)throws Exception {return new JSONObject().put("leaseId",lease).put("clientId",new HubStore(activity).clientId()).put("action",action);}
    private void observe() {
        if(!current() || unsupported)return;
        try {
            JSONObject result=client().request("session/desktop",body("status"));
            JSONObject state=result.optJSONObject("state"),desktop=state==null?null:state.optJSONObject("desktop");
            snapshot=Snapshot.parse(desktop,SystemClock.elapsedRealtime());
            if(snapshot.locked(SystemClock.elapsedRealtime()) && desktop!=null && desktop.optBoolean("passwordFocused"))fillPassword();
        }catch(Exception ignored){snapshot=Snapshot.UNKNOWN;}
    }
    public boolean locked(){return current() && snapshot.locked(SystemClock.elapsedRealtime());}
    public boolean captionButton(float x,float y){return current() && snapshot.captionButton(x,y,SystemClock.elapsedRealtime());}
    /** Native-touch release gives LogonUI a moment to focus the selected password control. */
    public void touchedLoginField() {
        if(!locked() || !fillQueued.compareAndSet(false,true))return;
        try {worker.schedule(()->{try{fillPassword();}finally{fillQueued.set(false);}},250,TimeUnit.MILLISECONDS);}
        catch(java.util.concurrent.RejectedExecutionException ignored){fillQueued.set(false);}
    }
    private void fillPassword() {
        HubLoginSecrets secrets=new HubLoginSecrets(activity);
        if(!current() || filled || !locked() || !secrets.enabled())return;
        try {
            // Only an explicit not-ready reply permits retry. An uncertain result must
            // never repeat a password submission or risk locking the Windows account.
            filled=true;
            JSONObject request=body("login-fill").put("password",secrets.password()).put("submit",secrets.submit());
            JSONObject result;
            try {result=client().request("session/desktop",request);}finally{request.remove("password");}
            JSONObject state=result.optJSONObject("state"),login=state==null?null:state.optJSONObject("login");
            if(login!=null && "not-ready".equals(login.optString("state"))){filled=false;return;}
            boolean success=login!=null && login.optBoolean("filled");
            if(!success)secrets.setEnabled(false);
            activity.runOnUiThread(()->{if(current())android.widget.Toast.makeText(activity,
                success?"密码已填写，请确认电脑登录结果":"自动填写已暂停，请手动输入或重新设置密码",android.widget.Toast.LENGTH_LONG).show();});
        }catch(Exception ignored){
            secrets.setEnabled(false);
            activity.runOnUiThread(()->{if(current())android.widget.Toast.makeText(activity,"自动填写未完成，已暂停；请手动输入密码",android.widget.Toast.LENGTH_LONG).show();});
        }
    }
    @Override public void close(){closed=true;snapshot=Snapshot.UNKNOWN;worker.shutdownNow();HubControlClient control=client;if(control!=null)control.cancel();}

    static final class Snapshot {
        static final Snapshot UNKNOWN=new Snapshot(false,false,new float[0][],0);
        final boolean known,isLocked;final float[][] captions;final long observed;
        Snapshot(boolean known,boolean locked,float[][] captions,long observed){this.known=known;isLocked=locked;this.captions=captions;this.observed=observed;}
        static Snapshot parse(JSONObject data,long now) {
            if(data==null || !data.optBoolean("known"))return UNKNOWN;
            JSONArray rects=data.optJSONArray("captionButtons");java.util.ArrayList<float[]> valid=new java.util.ArrayList<>();
            for(int i=0;rects!=null && i<Math.min(4,rects.length());i++) {
                JSONObject rect=rects.optJSONObject(i);if(rect==null)continue;
                float l=(float)rect.optDouble("left",-1),t=(float)rect.optDouble("top",-1),r=(float)rect.optDouble("right",-1),b=(float)rect.optDouble("bottom",-1);
                if(l>=0 && t>=0 && r<=1 && b<=1 && r>l && b>t && r-l<0.5f && b-t<0.2f)valid.add(new float[]{l,t,r,b});
            }
            return new Snapshot(true,data.optBoolean("locked"),valid.toArray(new float[0][]),now);
        }
        boolean fresh(long now){return known && now>=observed && now-observed<=3500;}
        boolean locked(long now){return fresh(now) && isLocked;}
        boolean captionButton(float x,float y,long now){if(!fresh(now) || isLocked)return false;for(float[] r:captions)if(x>=r[0] && x<r[2] && y>=r[1] && y<r[3])return true;return false;}
    }
}
