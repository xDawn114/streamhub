package com.limelight.streamhub;

import android.app.Activity;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.content.pm.Signature;
import android.net.ConnectivityManager;
import android.net.NetworkCapabilities;
import android.net.Uri;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.util.AtomicFile;
import androidx.core.content.FileProvider;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import com.limelight.BuildConfig;
import com.limelight.R;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.UUID;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import okhttp3.Call;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;

/** Process-wide downloads survive page changes. Installation always requires an explicit user action. */
public final class HubUpdates {
    private static HubUpdates instance;
    private static volatile boolean preparing;
    public static void setPreparing(boolean value) { preparing = value; }
    public static synchronized HubUpdates get(Context context) {
        if (instance == null) instance = new HubUpdates(context.getApplicationContext());
        return instance;
    }
    private final Context context;
    private final SharedPreferences preferences;
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Handler main = new Handler(Looper.getMainLooper());
    private final List<Runnable> listeners = new ArrayList<>();
    private final AtomicBoolean running = new AtomicBoolean(), cancelled = new AtomicBoolean();
    private final OkHttpClient http = new OkHttpClient.Builder().followRedirects(false).followSslRedirects(false)
            .connectTimeout(15, TimeUnit.SECONDS).readTimeout(30, TimeUnit.SECONDS).callTimeout(10, TimeUnit.MINUTES).build();
    private final HubUpdatePolicy.Trust trust;
    private final String previousInstall;
    private volatile Call currentCall;
    private volatile String message;
    private volatile HubUpdatePolicy.Manifest available;
    private volatile File downloaded;
    private volatile int progress = -1;
    private byte[] acceptedEnvelope;

    private HubUpdates(Context context) {
        this.context = context;
        preferences = context.getSharedPreferences("streamhub-updates", Context.MODE_PRIVATE);
        HubUpdatePolicy.Trust loaded;
        try (InputStream input = context.getResources().openRawResource(R.raw.streamhub_update_trust)) {
            JsonObject json = JsonParser.parseString(new String(readBounded(input, 8192), java.nio.charset.StandardCharsets.UTF_8)).getAsJsonObject();
            loaded = new HubUpdatePolicy.Trust(json.get("feed").getAsString(), json.get("manifestPublicKey").getAsString());
        } catch (Exception error) { loaded = new HubUpdatePolicy.Trust("", ""); }
        trust = loaded;
        message = trust.configured() ? "尚未检查更新。" : "更新发布源尚未接入，当前使用手动安装。";
        previousInstall = HubUpdateInstallState.describe(preferences.getInt("installPrevious", 0), preferences.getInt("installTarget", 0),
                preferences.getString("installChannel", ""), preferences.getLong("installStarted", 0), BuildConfig.VERSION_CODE,
                BuildConfig.STREAMHUB_INTERNAL ? "internal" : "stable", System.currentTimeMillis());
        if (trust.configured()) worker.execute(this::restoreCached);
    }
    public boolean configured() { return trust.configured(); }
    public boolean busy() { return running.get(); }
    public boolean autoCheck() { return preferences.getBoolean("autoCheck", true); }
    public boolean autoDownload() { return preferences.getBoolean("autoDownload", false); }
    public int progress() { return progress; }
    public String message() { return message; }
    public HubUpdatePolicy.Manifest available() { return available; }
    public boolean ready() { return downloaded != null && available != null; }
    public boolean checkDue() { return HubUpdateSchedule.due(autoCheck(), preferences.getLong("nextCheck", 0), System.currentTimeMillis()); }
    public boolean lastCheckFailed() { return preferences.getInt("failures", 0) > 0; }
    public String currentVersion() { return "当前 " + BuildConfig.VERSION_NAME + " / " + BuildConfig.VERSION_CODE + (BuildConfig.STREAMHUB_INTERNAL ? " · 内测渠道" : " · 正式渠道") +
            (previousInstall.isEmpty() ? "" : "\n" + previousInstall); }
    public String lastChecked() {
        long value = preferences.getLong("lastChecked", 0);
        return value == 0 ? "" : "上次成功检查：" + new java.text.SimpleDateFormat("MM-dd HH:mm", java.util.Locale.CHINA).format(new java.util.Date(value));
    }
    public void listen(Runnable listener) { if (!listeners.contains(listener)) listeners.add(listener); }
    public void unlisten(Runnable listener) { listeners.remove(listener); }
    private void changed() { main.post(() -> { for (Runnable listener : new ArrayList<>(listeners)) listener.run(); }); }
    private void state(String value) { message = value; changed(); }
    public void configure(boolean autoCheck, boolean autoDownload) {
        if (!preferences.edit().putBoolean("autoCheck", autoCheck).putBoolean("autoDownload", autoDownload).commit())
            state("更新偏好未能保存，请重试。");
        HubUpdateJobService.schedule(context, autoCheck && configured());
    }
    public void maybeCheck() {
        HubUpdateJobService.schedule(context, autoCheck() && configured());
        if (checkDue() && !deviceBusy()) check(false, null);
    }
    public boolean deviceBusy() {
        com.limelight.Game game = com.limelight.Game.instance;
        return !HubUpdateSchedule.canInstall(preparing, game != null && !game.isDestroyed() || "true".equals(new HubStore(context).get("sessionWasLaunched", "false")), HubFileService.busy());
    }
    public boolean check(boolean manual, Runnable finished) {
        return check(manual, manual, finished);
    }
    public boolean checkFromJob(Runnable finished) {
        return autoCheck() && check(false, lastCheckFailed(), finished);
    }
    private boolean check(boolean manual, boolean scheduledRetry, Runnable finished) {
        if (!configured() || !manual && ((!scheduledRetry && !checkDue()) || deviceBusy()) || !running.compareAndSet(false, true)) return false;
        cancelled.set(false); progress = -1; changed();
        worker.execute(() -> {
            try {
                state("正在检查并验证新版本…");
                byte[] envelope;
                try (Response response = execute(trust.feed, 30)) {
                    requireResponse(response, trust.feed);
                    if (response.body().contentLength() > HubUpdatePolicy.MAX_MANIFEST) throw new IOException("更新清单过大。");
                    envelope = readBounded(response.body().byteStream(), HubUpdatePolicy.MAX_MANIFEST);
                }
                HubUpdatePolicy.Manifest manifest = parse(envelope);
                preferences.edit().putLong("lastChecked", System.currentTimeMillis()).putLong("nextCheck", System.currentTimeMillis() + HubUpdateSchedule.INTERVAL).putInt("failures", 0).commit();
                if (manifest.code == BuildConfig.VERSION_CODE) {
                    available = null; downloaded = null; acceptedEnvelope = null;
                    state("已经是当前渠道的最新版本。");
                } else {
                    File previous = downloaded;
                    available = manifest; acceptedEnvelope = envelope; downloaded = null;
                    if (previous != null && previous.isFile()) {
                        try { verifyPackage(previous, manifest); downloaded = previous; } catch (IOException ignored) { }
                    }
                    if (downloaded == null && autoDownload() && unmetered() && !deviceBusy()) downloadCore();
                    state(downloaded == null ? "发现新版本，可下载更新。" : "更新已下载并验证，可在空闲时安装。");
                    if (!manual) notifyUpdate(manifest);
                }
            } catch (Exception error) { failed(error); }
            finally { currentCall = null; running.set(false); progress = -1; changed(); if (finished != null) main.post(finished); }
        });
        return true;
    }
    private HubUpdatePolicy.Manifest parse(byte[] bytes) throws IOException {
        return HubUpdatePolicy.read(bytes, trust, BuildConfig.STREAMHUB_INTERNAL ? "internal" : "stable", BuildConfig.VERSION_NAME, BuildConfig.VERSION_CODE, System.currentTimeMillis());
    }
    public void download() {
        if (available == null || !running.compareAndSet(false, true)) return;
        cancelled.set(false); changed();
        worker.execute(() -> {
            try { downloadCore(); state("更新已下载并验证，可在空闲时安装。"); }
            catch (Exception error) { failed(error); }
            finally { currentCall = null; running.set(false); progress = -1; changed(); }
        });
    }
    private void downloadCore() throws Exception {
        HubUpdatePolicy.Manifest manifest = available;
        if (manifest == null || acceptedEnvelope == null) throw new IOException("请先检查更新。");
        manifest.requireFresh(System.currentTimeMillis());
        File directory = new File(updateRoot(), UUID.randomUUID().toString());
        if (!directory.mkdirs()) throw new IOException("无法创建更新缓存，请检查剩余空间。");
        File part = new File(directory, "update.part"), target = new File(directory, "StreamHub-update.apk");
        boolean keep = false;
        try {
            try (Response response = execute(manifest.url, 600)) {
                requireResponse(response, manifest.url);
                long expected = response.body().contentLength();
                if (expected >= 0 && expected != manifest.size) throw new IOException("更新包大小与清单不符。");
                try (InputStream input = response.body().byteStream(); FileOutputStream output = new FileOutputStream(part)) {
                    byte[] buffer = new byte[65536]; long total = 0; int count;
                    while ((count = input.read(buffer)) != -1) {
                        requireNotCancelled(); total += count;
                        if (total > manifest.size) throw new IOException("更新包超过签名清单大小。");
                        output.write(buffer, 0, count);
                        int next = (int)(total * 100 / manifest.size);
                        if (progress != next) { progress = next; state("正在下载并验证更新 " + next + "%"); }
                    }
                    if (total != manifest.size) throw new IOException("更新包未下载完整，请重试。");
                    output.getFD().sync();
                }
            }
            requireNotCancelled();
            if (!part.renameTo(target)) throw new IOException("无法保存已验证的更新包。");
            verifyPackage(target, manifest);
            File old = downloaded;
            JsonObject receipt = new JsonObject();
            receipt.addProperty("envelope", java.util.Base64.getEncoder().encodeToString(acceptedEnvelope));
            receipt.addProperty("directory", directory.getName());
            writeAtomic(new File(updateRoot(), "downloaded.json"), receipt.toString().getBytes(java.nio.charset.StandardCharsets.UTF_8));
            downloaded = target; keep = true;
            if (old != null && !old.equals(target)) removeOwnPackage(old);
        } finally {
            if (!keep) { part.delete(); target.delete(); directory.delete(); }
        }
    }
    private void restoreCached() {
        try {
            File receipt = new File(updateRoot(), "downloaded.json");
            if (!receipt.isFile()) return;
            JsonObject value;
            try (InputStream input = new FileInputStream(receipt)) {
                value = JsonParser.parseString(new String(readBounded(input, 100000), java.nio.charset.StandardCharsets.UTF_8)).getAsJsonObject();
            }
            String directory = value.get("directory").getAsString();
            if (!directory.matches("[a-f0-9-]{36}")) return;
            byte[] envelope = java.util.Base64.getDecoder().decode(value.get("envelope").getAsString());
            HubUpdatePolicy.Manifest manifest = parse(envelope);
            if (manifest.code <= BuildConfig.VERSION_CODE) return;
            File target = new File(new File(updateRoot(), directory), "StreamHub-update.apk");
            verifyPackage(target, manifest);
            acceptedEnvelope = envelope; available = manifest; downloaded = target;
            state("更新已下载并验证，可在空闲时安装。");
        } catch (Exception ignored) { /* Stale or altered cache cannot become an install candidate. */ }
    }
    private void verifyPackage(File file, HubUpdatePolicy.Manifest manifest) throws IOException {
        manifest.requireFresh(System.currentTimeMillis()); requireNotCancelled();
        try {
            if (file.length() != manifest.size) throw new IOException("更新包大小校验失败。");
            MessageDigest digest = MessageDigest.getInstance("SHA-256");
            try (InputStream input = new FileInputStream(file)) {
                byte[] buffer = new byte[65536]; int count;
                while ((count = input.read(buffer)) != -1) { requireNotCancelled(); digest.update(buffer, 0, count); }
            }
            if (!hex(digest.digest()).equalsIgnoreCase(manifest.sha256)) throw new IOException("更新包校验失败，已停止安装。");
            PackageManager manager = context.getPackageManager();
            int flags = Build.VERSION.SDK_INT >= 28 ? PackageManager.GET_SIGNING_CERTIFICATES : PackageManager.GET_SIGNATURES;
            PackageInfo archive = manager.getPackageArchiveInfo(file.getAbsolutePath(), flags);
            PackageInfo installed = manager.getPackageInfo(context.getPackageName(), flags);
            if (archive == null || !context.getPackageName().equals(archive.packageName) || !manifest.version.equals(archive.versionName) ||
                    (Build.VERSION.SDK_INT >= 28 ? archive.getLongVersionCode() : archive.versionCode) != manifest.code)
                throw new IOException("更新包的应用身份或版本不匹配。");
            if (!sameSigners(signatures(installed), signatures(archive))) throw new IOException("更新包签名与当前应用不一致，已停止安装。");
        } catch (IOException error) { throw error; }
        catch (Exception error) { throw new IOException("无法验证更新包，请重新下载。", error); }
    }
    static boolean sameSigners(Signature[] expected, Signature[] actual) {
        if (expected == null || actual == null || expected.length == 0 || expected.length != actual.length) return false;
        for (Signature signer : expected) if (!Arrays.asList(actual).contains(signer)) return false;
        return true;
    }
    private static Signature[] signatures(PackageInfo value) {
        return Build.VERSION.SDK_INT >= 28 ? value.signingInfo == null ? null : value.signingInfo.getApkContentsSigners() : value.signatures;
    }
    public void install(Activity activity, java.util.function.BooleanSupplier idle) {
        if (!ready() || !running.compareAndSet(false, true)) return;
        if (!idle.getAsBoolean() || deviceBusy()) { running.set(false); state("请先结束串流并等待文件传输完成，再安装更新。"); return; }
        if (!activity.getPackageManager().canRequestPackageInstalls()) {
            running.set(false); state("请在系统中允许“我控制”安装更新，然后返回这里再次点“安装更新”。");
            activity.startActivity(new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:" + context.getPackageName())));
            return;
        }
        cancelled.set(false); state("正在安装前重新验证更新包…");
        final File target = downloaded; final HubUpdatePolicy.Manifest manifest = available;
        worker.execute(() -> {
            try {
                verifyPackage(target, manifest); requireNotCancelled();
                main.post(() -> {
                    try {
                        if (activity.isDestroyed() || activity.isFinishing() || !idle.getAsBoolean() || deviceBusy()) throw new IOException("当前仍有连接或文件操作，请空闲后重试安装。");
                        Uri uri = FileProvider.getUriForFile(context, context.getPackageName() + ".updates", target);
                        Intent install = new Intent(Intent.ACTION_INSTALL_PACKAGE).setDataAndType(uri, "application/vnd.android.package-archive")
                                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION).putExtra(Intent.EXTRA_NOT_UNKNOWN_SOURCE, true);
                        if (!preferences.edit().putInt("installPrevious", BuildConfig.VERSION_CODE).putInt("installTarget", manifest.code)
                                .putString("installChannel", BuildConfig.STREAMHUB_INTERNAL ? "internal" : "stable")
                                .putLong("installStarted", System.currentTimeMillis()).commit()) throw new IOException("安装记录未能保存，请重试。");
                        try { activity.startActivity(install); }
                        catch (Exception error) { preferences.edit().remove("installTarget").commit(); throw error; }
                        state("已打开系统安装确认；取消后仍可重新安装。绑定和设置保留。");
                    } catch (Exception error) { state("未开始安装：" + error.getMessage()); }
                    finally { running.set(false); changed(); }
                });
            } catch (Exception error) { failed(error); running.set(false); changed(); }
        });
    }
    public void cancel() { cancelled.set(true); Call call = currentCall; if (call != null) call.cancel(); }
    private void requireNotCancelled() throws IOException { if (cancelled.get()) throw new IOException("更新操作已取消，可重试。"); }
    private Response execute(String url, int timeoutSeconds) throws IOException {
        requireNotCancelled();
        Call call = http.newCall(new Request.Builder().url(url).header("Cache-Control", "no-cache").build());
        call.timeout().timeout(timeoutSeconds, TimeUnit.SECONDS); currentCall = call;
        if (cancelled.get()) call.cancel();
        return call.execute();
    }
    private static void requireResponse(Response response, String expected) throws IOException {
        if (response.code() != 200 || response.body() == null || !response.request().url().toString().equals(expected))
            throw new IOException("更新源响应失败或发生重定向，请稍后重试。");
    }
    private void failed(Exception error) {
        int failures = preferences.getInt("failures", 0);
        preferences.edit().putInt("failures", Math.min(4, failures + 1)).putLong("nextCheck", System.currentTimeMillis() + HubUpdateSchedule.retryDelay(failures)).commit();
        state(cancelled.get() ? "更新操作已取消，可重试。" : "更新未完成，可重试：" + (error.getMessage() == null ? "网络或存储暂时不可用。" : error.getMessage()));
    }
    private boolean unmetered() {
        ConnectivityManager manager = (ConnectivityManager)context.getSystemService(Context.CONNECTIVITY_SERVICE);
        NetworkCapabilities capabilities = manager.getNetworkCapabilities(manager.getActiveNetwork());
        return capabilities != null && capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED) &&
                capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED);
    }
    private void notifyUpdate(HubUpdatePolicy.Manifest manifest) {
        if (preferences.getInt("notifiedCode", 0) == manifest.code) return;
        try {
            if (Build.VERSION.SDK_INT >= 33 && context.checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return;
            NotificationManager notifications = (NotificationManager)context.getSystemService(Context.NOTIFICATION_SERVICE);
            notifications.createNotificationChannel(new NotificationChannel("streamhub-updates", "应用更新", NotificationManager.IMPORTANCE_LOW));
            PendingIntent open = PendingIntent.getActivity(context, 7406, new Intent(context, HubActivity.class).putExtra("HubPage", 2), PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
            notifications.notify(7406, new Notification.Builder(context, "streamhub-updates").setSmallIcon(android.R.drawable.stat_sys_download_done)
                    .setContentTitle("我控制有新版本 " + manifest.version).setContentText("打开设置 → 版本与更新查看").setAutoCancel(true).setContentIntent(open).build());
            preferences.edit().putInt("notifiedCode", manifest.code).apply();
        } catch (RuntimeException ignored) { }
    }
    private File updateRoot() { return new File(context.getFilesDir(), "updates"); }
    private void removeOwnPackage(File file) throws IOException {
        File directory = file.getParentFile();
        if (directory == null || !directory.getName().matches("[a-f0-9-]{36}") || !directory.getParentFile().getCanonicalFile().equals(updateRoot().getCanonicalFile())) return;
        file.delete(); directory.delete();
    }
    private static byte[] readBounded(InputStream input, int maximum) throws IOException {
        ByteArrayOutputStream output = new ByteArrayOutputStream(); byte[] buffer = new byte[8192]; int count;
        while ((count = input.read(buffer)) != -1) { if (output.size() + count > maximum) throw new IOException("更新数据过大。"); output.write(buffer, 0, count); }
        return output.toByteArray();
    }
    private static void writeAtomic(File path, byte[] bytes) throws IOException {
        AtomicFile file = new AtomicFile(path); FileOutputStream output = file.startWrite();
        try { output.write(bytes); file.finishWrite(output); }
        catch (IOException error) { file.failWrite(output); throw error; }
    }
    private static String hex(byte[] bytes) {
        StringBuilder result = new StringBuilder(); for (byte value : bytes) result.append(String.format(java.util.Locale.ROOT, "%02x", value & 255)); return result.toString();
    }
}
