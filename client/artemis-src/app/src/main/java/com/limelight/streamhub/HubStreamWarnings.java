package com.limelight.streamhub;

import android.app.Activity;
import android.content.Context;
import android.content.SharedPreferences;

/** Only advisory overlays are muted. Connection failures and diagnostics stay active. */
public final class HubStreamWarnings {
    private final SharedPreferences prefs;
    private boolean muted;
    private int lastMask;
    private long visibleUntil;
    public HubStreamWarnings(Context context) { prefs=context.getSharedPreferences("streamhub_controls",Context.MODE_PRIVATE); }
    public boolean visible(boolean network,boolean load) {
        return networkVisible(network) || loadVisible(load);
    }
    public boolean networkVisible(boolean active) { return !muted && active && prefs.getBoolean("warning_network",true); }
    public boolean loadVisible(boolean active) { return !muted && active && prefs.getBoolean("warning_load",true); }
    public void dismissForSession() { muted=true; }
    public int activeMask(boolean network,boolean load) {
        return (network&&prefs.getBoolean("warning_network",true)?1:0)|(load&&prefs.getBoolean("warning_load",true)?2:0);
    }
    public boolean bannerVisible(int mask,long now) {
        if(mask!=lastMask){lastMask=mask;visibleUntil=now+6000;}
        return !muted&&mask!=0&&now<visibleUntil;
    }
    public long remaining(long now){return Math.max(1,visibleUntil-now);}
    public void show(Activity activity,Runnable changed) {
        String[] labels={"网络拥塞提示","解码与渲染负载提示"};
        boolean[] selected={prefs.getBoolean("warning_network",true),prefs.getBoolean("warning_load",true)};
        HubStreamUi.dialog(activity).setTitle("串流提示")
                .setMultiChoiceItems(labels,selected,(d,index,value)->{
                    prefs.edit().putBoolean(index==0?"warning_network":"warning_load",value).apply();changed.run();
                }).setNeutralButton(muted?"恢复本次提示":"本次不再提示",(d,w)->{muted=!muted;changed.run();})
                .setPositiveButton("完成",null).show();
    }
}
