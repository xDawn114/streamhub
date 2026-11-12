package com.limelight.streamhub;

import android.app.Activity;
import android.widget.Toast;
import org.json.JSONObject;
import org.json.JSONArray;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Session controls use the existing pin and current lease; all I/O stays off the UI thread. */
public final class HubLiveControls implements AutoCloseable {
    public interface ResolutionChange { void apply(int width, int height, int fps); }
    private final Activity activity;
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private volatile HubControlClient control;
    private volatile boolean busy;
    private volatile boolean closed;
    public HubLiveControls(Activity activity) { this.activity = activity; }
    private boolean alive() { return !activity.isFinishing() && !activity.isDestroyed(); }
    private void privacy(Boolean enabled) {
        if (busy || closed || !alive()) return; busy = true;
        worker.execute(() -> {
            HubControlClient client = null;
            try {
                HubStore store = new HubStore(activity);
                client = new HubControlClient(activity.getIntent().getStringExtra(com.limelight.Game.EXTRA_HOST), store.invitation()); control = client;
                JSONObject caps = client.probeInfo().optJSONObject("capabilities");
                if (caps == null || !caps.optBoolean("remotePrivacy")) throw new java.io.IOException("请更新电脑端后使用隐私屏。");
                JSONObject body = new JSONObject().put("mode", "remote").put("leaseId", activity.getIntent().getStringExtra("HubLeaseId")).put("clientId", store.clientId());
                if (enabled != null) body.put("enabled", enabled);
                JSONObject result = client.request("session/privacy", body);
                activity.runOnUiThread(() -> {
                    busy = false; if (!alive()) return;
                    if (enabled != null) Toast.makeText(activity, result.optString("message"), Toast.LENGTH_LONG).show();
                    else {
                        boolean current = result.optBoolean("enabled");
                        HubStreamUi.dialog(activity).setTitle("本机隐私屏 · " + (current ? "已开启" : "已关闭"))
                            .setMessage("开启后电脑本机显示黑屏，平板继续显示并接受远控输入。\n\n紧急恢复：在电脑物理键盘按 Ctrl+Alt+F12。断开连接后自动恢复。")
                            .setPositiveButton(current ? "关闭隐私屏" : "开启隐私屏", (dialog, which) -> privacy(!current))
                            .setNegativeButton("取消", null).show();
                    }
                });
            } catch (Exception error) { activity.runOnUiThread(() -> { busy = false; if (alive()) Toast.makeText(activity,"未完成："+error.getMessage(),Toast.LENGTH_LONG).show(); }); }
            finally { if (client != null) client.cancel(); control = null; }
        });
    }
    public void showLockSettings() {
        android.widget.ScrollView scroll=new android.widget.ScrollView(HubStreamUi.context(activity));
        scroll.setPadding(24,8,24,16);
        scroll.addView(HubLoginSettings.view(activity,true,()->HubStreamUi.dialog(activity).setTitle("立即锁定电脑？")
                .setMessage("电脑将进入 Windows 锁屏；可以从本菜单打开键盘或设置自动填写密码。")
                .setPositiveButton("锁屏",(confirm,index)->desktop("lock",null,null)).setNegativeButton("取消",null).show()));
        HubStreamUi.dialog(activity).setTitle("锁屏与自动输入").setView(scroll).setPositiveButton("完成",(d,w)->
            desktop("configure",activity.getSharedPreferences("streamhub_controls",android.content.Context.MODE_PRIVATE).getBoolean("lock_on_exit",false),null)).show();
    }
    private void desktop(String action,Boolean enabled,Runnable saved) {
        if(busy||closed||!alive())return;busy=true;
        worker.execute(()->{
            HubControlClient client=null;
            try {
                HubStore store=new HubStore(activity);
                client=new HubControlClient(activity.getIntent().getStringExtra(com.limelight.Game.EXTRA_HOST),store.invitation());
                control=client;
                JSONObject caps=client.probeInfo().optJSONObject("capabilities");
                if(caps==null||!caps.optBoolean("remoteDesktopLock"))throw new java.io.IOException("请更新电脑端后使用锁屏功能。");
                JSONObject body=new JSONObject().put("leaseId",activity.getIntent().getStringExtra("HubLeaseId")).put("clientId",store.clientId()).put("action",action);
                if(enabled!=null)body.put("enabled",enabled);
                JSONObject result=client.request("session/desktop",body);
                activity.runOnUiThread(()->{if(alive()){if(saved!=null)saved.run();Toast.makeText(activity,result.optString("message"),Toast.LENGTH_LONG).show();}});
            }catch(Exception error){activity.runOnUiThread(()->{if(alive())Toast.makeText(activity,"未完成："+error.getMessage(),Toast.LENGTH_LONG).show();});}
            finally{if(client!=null)client.cancel();control=null;busy=false;}
        });
    }
    public void showPrivacy() { privacy(null); }
    public void showResolution(int width, int height, int fps, int nativeWidth, int nativeHeight, String supportedModes, ResolutionChange callback) {
        List<int[]> available = new ArrayList<>();
        if (supportedModes != null) try {
            JSONArray modes = new JSONArray(supportedModes);
            for (int i = 0; i < Math.min(128, modes.length()); i++) {
                JSONObject mode = modes.optJSONObject(i); if (mode != null) available.add(new int[]{mode.optInt("width"), mode.optInt("height"), mode.optInt("refreshRate")});
            }
        } catch (Exception ignored) { }
        List<int[]> choices = supportedModes == null ? HubStreamResolution.choices(nativeWidth, nativeHeight) : HubStreamResolution.supportedChoices(available);
        if (choices.isEmpty()) {
            HubStreamUi.dialog(activity).setTitle("当前分辨率").setMessage("电脑未提供可切换的副屏模式，请更新电脑端后重新连接。").setPositiveButton("知道了", null).show(); return;
        }
        String[] labels = new String[choices.size()]; final int[] selected = {0};
        for (int i = 0; i < choices.size(); i++) {
            int[] size = choices.get(i); boolean current = size[0] == width && size[1] == height;
            labels[i] = size[0] + " × " + size[1] + (current ? " · 当前" : "");
            if (current) selected[0] = i;
        }
        HubStreamUi.dialog(activity).setTitle("分辨率 · 应用时短暂重连")
            .setSingleChoiceItems(labels,selected[0],(dialog,which)->selected[0]=which)
            .setPositiveButton("立即应用",(dialog,which)->{
                int[] size=choices.get(selected[0]); if(size[0]==width && size[1]==height)return;
                HubStreamUi.dialog(activity).setTitle("应用新分辨率？")
                    .setMessage(size[0]+" × "+size[1]+"\n画面会短暂中断并自动恢复；原有电脑绑定和连接通道保留。")
                    .setPositiveButton("应用并重连",(d,w)->callback.apply(size[0],size[1],fps)).setNegativeButton("取消",null).show();
            }).setNegativeButton("取消",null).show();
    }
    @Override public void close() { closed = true; HubControlClient client = control; if (client != null) client.cancel(); worker.shutdownNow(); }
}
