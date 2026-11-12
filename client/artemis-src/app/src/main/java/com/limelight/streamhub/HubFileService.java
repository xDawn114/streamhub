package com.limelight.streamhub;

import android.app.*;
import android.content.*;
import android.net.Uri;
import android.os.*;
import org.json.JSONObject;
import java.io.*;
import java.security.MessageDigest;
import java.util.concurrent.*;

/** User-started transfer foreground service, independent of any video session. */
public final class HubFileService extends Service {
    private static final String CHANNEL = "streamhub-files";
    private static final int NOTICE = 7403;
    private static volatile HubFileService instance;
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private volatile boolean paused, active;
    private volatile HubFileClient client;
    private volatile InputStream sourceInput;
    private String currentId = "";
    private HubFileTasks tasks;
    private long lastNotice, runStartedAt, previousElapsed, initialOffset;
    public static boolean busy() { return instance != null && instance.active; }
    public static void start(Context context, String id, boolean discard) {
        Intent intent = new Intent(context, HubFileService.class).putExtra("id", id).putExtra("discard", discard);
        context.startForegroundService(intent);
    }
    public static void pause() { HubFileService current = instance; if (current != null) { current.paused = true; if (current.client != null) current.client.cancel();
        try { if(current.sourceInput!=null)current.sourceInput.close(); }catch(IOException ignored){} } }
    @Override public void onCreate() {
        super.onCreate(); instance = this; tasks = new HubFileTasks(this);
        ((NotificationManager)getSystemService(NOTIFICATION_SERVICE)).createNotificationChannel(new NotificationChannel(CHANNEL, "文件传输", NotificationManager.IMPORTANCE_LOW));
        startForeground(NOTICE, notice("正在准备文件传输", 0, true));
    }
    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent != null && "pause".equals(intent.getAction())) { pause(); return START_NOT_STICKY; }
        if (intent == null || active) { if (!active) stopSelf(); return START_NOT_STICKY; }
        currentId = intent.getStringExtra("id"); boolean discard = intent.getBooleanExtra("discard", false); active = true; paused = false;
        worker.execute(() -> {
            runTask(currentId,discard);
            while(!paused && !discard) {
                try {
                    if(!"completed".equals(tasks.read(currentId).optString("state")))break;
                    JSONObject next=tasks.nextQueued(new HubStore(this).get("deviceId",""));
                    if(next==null)break;
                    currentId=next.getString("id");runTask(currentId,false);
                } catch(Exception error){break;}
            }
            new Handler(Looper.getMainLooper()).post(()->{
                // Recheck on the main thread: a picker may have queued another
                // file while the worker was completing its final transfer.
                JSONObject next=null;
                if(!paused && !discard)next=tasks.nextQueued(new HubStore(this).get("deviceId",""));
                active=false;
                if(next!=null)onStartCommand(new Intent().putExtra("id",next.optString("id")),0,startId);
                else {stopForeground(true);stopSelfResult(startId);}
            });
        });
        return START_NOT_STICKY; // Persisted tasks resume explicitly after process death.
    }
    private Notification notice(String message, int percent, boolean indeterminate) {
        PendingIntent open = PendingIntent.getActivity(this, 0, new Intent(this, HubFilesActivity.class), PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        PendingIntent pause = PendingIntent.getService(this, 1, new Intent(this, HubFileService.class).setAction("pause"), PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        return new Notification.Builder(this, CHANNEL).setSmallIcon(android.R.drawable.stat_sys_upload).setContentTitle("我控制 · 文件传输")
            .setContentText(message).setContentIntent(open).setOngoing(true).setOnlyAlertOnce(true).setVisibility(Notification.VISIBILITY_PRIVATE)
            .setProgress(100, percent, indeterminate).addAction(0, "暂停", pause).build();
    }
    private void check() { if (paused || Thread.currentThread().isInterrupted()) throw new CancellationException("已暂停，可以继续传输。"); if (client != null) client.check(); }
    private void waitForRetry(long delayMs) throws Exception {
        long until = SystemClock.elapsedRealtime() + delayMs;
        while (SystemClock.elapsedRealtime() < until) {
            check();
            Thread.sleep(Math.min(200, Math.max(1, until - SystemClock.elapsedRealtime())));
        }
        check();
    }
    private void progress(JSONObject task, String message) throws Exception {
        if (runStartedAt > 0) {
            long elapsed = Math.max(1, SystemClock.elapsedRealtime() - runStartedAt);
            task.put("elapsedMs", previousElapsed + elapsed);
            task.put("bytesPerSecond", "running".equals(task.optString("state")) ? Math.max(0, task.optLong("offset") - initialOffset) * 1000 / elapsed : 0);
        }
        task.put("message", message); tasks.save(task);
        long now = SystemClock.elapsedRealtime(); if (now - lastNotice > 800) {
            lastNotice = now; long size = task.optLong("size", 0);
            int percent = (int)Math.min(100, Math.max(0, task.optLong("offset", 0) * 100 / Math.max(1, size)));
            ((NotificationManager)getSystemService(NOTIFICATION_SERVICE)).notify(NOTICE, notice(message, percent, size == 0));
        }
    }
    private void runTask(String id, boolean discard) {
        JSONObject task = null;
        try {
            task = tasks.read(id);
            task.put("attempted",true);tasks.save(task);
            runStartedAt = SystemClock.elapsedRealtime(); previousElapsed = task.optLong("elapsedMs"); initialOffset = task.optLong("offset");
            if ("completed".equals(task.optString("state"))) return;
            if (discard) {
                String message=task.has("publicationPhase") && !task.optBoolean("saved")
                        ? "已取消本机传输；保存阶段可能留有文件，请检查接收文件夹。" : "已取消，本机暂存文件已清理。";
                boolean remotePending=false;
                if(!task.optString("remoteId").isEmpty())try {
                    if (!new HubStore(this).get("deviceId", "").equals(task.getString("computer"))) throw new IOException("绑定已改变");
                    client = new HubFileClient(this);check();client.connect();client.json("tasks/" + task.getString("remoteId") + "/cancel", new JSONObject());
                }catch(Exception unavailable){remotePending=true;message += " 电脑未确认取消，可重试或在电脑文件页取消。";}
                task.put("state", "cancelled").put("remoteCancelPending",remotePending); tasks.data(id).delete(); progress(task, message); return;
            }
            if (!new HubStore(this).get("deviceId", "").equals(task.getString("computer"))) throw new IOException("电脑绑定已改变，旧任务不会发给新电脑。");
            task.put("state", "running"); progress(task, "正在准备文件");
            HubFileRetryWindow retries = new HubFileRetryWindow();
            for (;;) {
                check();
                long startOffset = task.optLong("offset");
                try {
                    client = new HubFileClient(this); client.connect();
                    if ("upload".equals(task.getString("direction"))) upload(task); else download(task);
                    break;
                } catch (Exception error) {
                    check();
                    HubFileRetryWindow.Next next = retries.afterFailure(error, !task.optString("remoteId").isEmpty(),
                            startOffset, task.optLong("offset"), SystemClock.elapsedRealtime());
                    if (next == null) throw error;
                    progress(task, next.waitingForNetwork() ? "正在等待网络恢复，可随时暂停…" :
                            "连接中断，正在重试（" + next.fastRetry + "/" + HubFileRetryPolicy.MAX_RETRIES + "）…");
                    if (client != null) client.cancel(); client = null;
                    waitForRetry(next.delayMs);
                }
            }
            task.put("state", "completed").put("offset", task.getLong("size")); progress(task, "传输完成"); tasks.data(id).delete();
        } catch (Exception error) {
            if (task != null) try { task.put("state", "paused"); progress(task, "已暂停：" + (error.getMessage() == null ? "请刷新或重试" : error.getMessage())); } catch (Exception ignored) { }
        } finally { runStartedAt = 0; if (client != null) client.cancel(); client = null; }
    }
    private void upload(JSONObject task) throws Exception {
        String id = task.getString("id"); File spool = tasks.data(id);
        if (!task.optBoolean("staged")) {
            MessageDigest digest = MessageDigest.getInstance("SHA-256"); long size = 0;
            try (InputStream input = getContentResolver().openInputStream(Uri.parse(task.getString("uri"))); FileOutputStream output = new FileOutputStream(spool)) {
                if (input == null) throw new IOException("选中文件不可读，请重新选择。");
                sourceInput=input; long shownAt=0;
                byte[] buffer = new byte[256 * 1024]; int read;
                while ((read = input.read(buffer)) != -1) { check(); size += read; if (size > task.getLong("size")) throw new IOException("源文件大小已改变。"); output.write(buffer, 0, read); digest.update(buffer, 0, read);
                    if(SystemClock.elapsedRealtime()-shownAt>1000){shownAt=SystemClock.elapsedRealtime();progress(task,"正在读取文件 "+size*100/Math.max(1,task.getLong("size"))+"%");} }
                output.getFD().sync();
            }finally{sourceInput=null;}
            if (size != task.getLong("size")) throw new IOException("源文件大小已改变，请重新选择。");
            task.put("sha256", HubControlClient.hex(digest.digest())).put("staged", true); tasks.save(task);
        }
        if (task.optString("remoteId").isEmpty()) {
            JSONObject remote = client.json("tasks", new JSONObject().put("name", task.getString("name")).put("size", task.getLong("size")).put("sha256", task.getString("sha256"))).getJSONObject("task");
            task.put("remoteId", remote.getString("id")); tasks.save(task);
        }
        String remoteId = task.getString("remoteId"); JSONObject remote = client.json("tasks/" + remoteId, null).getJSONObject("task");
        if ("completed".equals(remote.optString("state"))) return;
        if (!"receiving".equals(remote.optString("state")) && !"copying".equals(remote.optString("state")) && !"publishing".equals(remote.optString("state")))
            throw new IOException("电脑任务已结束，请取消记录后重新选择文件。");
        long offset = remote.getLong("offset"), size = task.getLong("size");
        if (offset < 0 || offset > size) throw new IOException("电脑返回的进度无效。");
        try (RandomAccessFile input = new RandomAccessFile(spool, "r")) {
            input.seek(offset);
            while (offset < size) { check(); byte[] bytes = new byte[(int)Math.min(256 * 1024, size-offset)]; input.readFully(bytes);
                client.data(remoteId, offset, bytes); offset += bytes.length; task.put("offset", offset); progress(task, "发送 " + offset * 100 / Math.max(1,size) + "% · " + task.getString("name")); }
        }
        check(); JSONObject completion = client.json("tasks/" + remoteId + "/complete", new JSONObject());
        if (!"running".equals(completion.optString("completionState"))) return; // Older hosts finish synchronously.
        progress(task, "已发送，电脑正在校验并保存文件…");
        while (true) {
            check(); Thread.sleep(1500); check();
            JSONObject status = client.json("tasks/" + remoteId, null);
            String state = status.optString("completionState");
            if ("failed".equals(state)) throw new IOException(status.optString("completionMessage", "电脑保存失败，请继续重试。"));
            if ("completed".equals(status.getJSONObject("task").optString("state"))) return;
            if (!"running".equals(state)) throw new IOException("电脑保存未完成，请继续重试。");
        }
    }
    private void download(JSONObject task) throws Exception {
        if(task.has("browsePath") && !task.optBoolean("browseReady")) {
            JSONObject remote;
            if(task.optString("remoteId").isEmpty()) {
                remote=client.json("fetch",new JSONObject().put("rootId",task.optString("browseRootId")).put("path",task.getString("browsePath"))).getJSONObject("task");
                task.put("remoteId",remote.getString("id"));tasks.save(task);
            } else remote=client.json("tasks/"+task.getString("remoteId"),null).getJSONObject("task");
            long deadline=SystemClock.elapsedRealtime()+180000;
            while(!"ready".equals(remote.optString("state"))) {
                check();
                if(SystemClock.elapsedRealtime()>deadline || "failed".equals(remote.optString("state")) || "cancelled".equals(remote.optString("state")))
                    throw new IOException("电脑准备文件未完成，请重新选择文件。");
                progress(task,"电脑正在准备下载文件…");waitForRetry(500);
                remote=client.json("tasks/"+task.getString("remoteId"),null).getJSONObject("task");
            }
            if(remote.getLong("size")!=task.getLong("size"))throw new IOException("文件大小已改变，请重新选择。");
            task.put("sha256",remote.getString("sha256")).put("browseReady",true);tasks.save(task);
        }
        String id = task.getString("id"), remoteId = task.getString("remoteId"); File spool = tasks.data(id); long size = task.getLong("size"), offset = task.optLong("offset");
        JSONObject remote = client.json("tasks/" + remoteId, null).getJSONObject("task");
        if (!remote.getString("sha256").equalsIgnoreCase(task.getString("sha256")) || remote.getLong("size") != size) throw new IOException("电脑文件信息已改变。");
        if (task.optBoolean("saved")) { client.json("tasks/" + remoteId + "/complete", new JSONObject()); return; }
        if (offset < 0 || offset > size || spool.length() < offset) { offset = 0; task.put("offset",0); }
        try (RandomAccessFile output = new RandomAccessFile(spool, "rw")) {
            output.setLength(offset); output.seek(offset);
            while (offset < size) { check(); byte[] bytes = client.data(remoteId, offset, null);
                if (bytes.length == 0 || bytes.length > size-offset) throw new IOException("接收文件块长度不符。");
                output.write(bytes); output.getFD().sync(); offset += bytes.length; task.put("offset", offset); progress(task, "接收 " + offset * 100 / Math.max(1,size) + "% · " + task.getString("name")); }
        }
        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        try (InputStream input = new FileInputStream(spool)) { byte[] bytes = new byte[256*1024]; int read; while ((read=input.read(bytes))!=-1) { check(); digest.update(bytes,0,read); } }
        if (!HubControlClient.hex(digest.digest()).equalsIgnoreCase(task.getString("sha256"))) { task.put("offset",0); tasks.save(task); throw new IOException("文件校验失败，尚未保存到接收文件夹。"); }
        check(); progress(task, "校验通过，正在保存到所选文件夹…");
        HubFilePublication.publish(task, spool, tasks::save,
                new HubSafDocuments(getContentResolver(), Uri.parse(task.getString("uri"))), this::check);
        client.json("tasks/" + remoteId + "/complete", new JSONObject());
    }
    @Override public void onDestroy() { pause(); worker.shutdownNow(); instance = null; super.onDestroy(); }
    @Override public IBinder onBind(Intent intent) { return null; }
}
