package com.limelight.streamhub;

import android.content.Context;
import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;
import java.nio.charset.StandardCharsets;
import java.security.KeyStore;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

/** Product binding belongs to this APK and is sealed by Android Keystore. */
public final class HubStore {
    interface InvitationCodec {
        String seal(String value) throws Exception;
        String open(String value) throws Exception;
    }
    private final SharedPreferences preferences;
    private final Context context;
    private final InvitationCodec codec;
    public HubStore(Context context) {
        this(context, new InvitationCodec() {
            public String seal(String value) throws Exception { return sealInvitation(value); }
            public String open(String value) throws Exception { return unseal(value); }
        });
    }
    HubStore(Context context, InvitationCodec codec) {
        this.context=context.getApplicationContext();
        preferences = context.getSharedPreferences("streamhub-device", Context.MODE_PRIVATE);
        this.codec = codec;
    }
    public String get(String key, String fallback) { return preferences.getString(key, fallback); }
    public void put(String key, String value) { preferences.edit().putString(key, value).apply(); }
    public boolean bound() { return !get("invitation", "").isEmpty(); }
    public HubTransports.Settings transport() {
        synchronized (HubStore.class) {
            try { return new HubTransports.Settings(get("transport", "tailscale"), get("transportServer", ""), get("transportManual", "")); }
            catch (ClassCastException error) { throw new IllegalArgumentException("连接通道配置无效，请重新选择并保存。"); }
        }
    }
    public void saveTransport(HubTransports.Settings settings) {
        synchronized (HubStore.class) {
            if (!preferences.edit().putString("transport", settings.provider).putString("transportServer", settings.serverUrl)
                    .putString("transportManual", settings.manualAddresses).putString("transportRevision", java.util.UUID.randomUUID().toString()).commit())
                throw new IllegalStateException("连接通道未保存，请重试。");
        }
    }
    public java.util.List<String> candidates(String route) {
        HubTransports.Settings settings = transport();
        String network = HubNetworkStamp.current(); long now = System.currentTimeMillis();
        java.util.List<String> result = new java.util.ArrayList<>();
        for (String host : settings.transport().candidates(HubRoutes.usableAddresses(get("hosts", "")), settings, route))
            if ("manual".equals(settings.provider) || HubAddressFreshness.eligible(HubRoutes.type(host), preferences.getLong("seen-" + host, 0),
                    get("network-" + host, ""), network, now)) result.add(host);
        String last = get("lastHost", "");
        if (result.remove(last)) result.add(0, last);
        // Apply route priority before the candidate cap even on a first launch.
        if ("auto".equals(route)) result.sort(java.util.Comparator.comparingInt(host ->
            "usb".equals(HubRoutes.type(host)) ? 0 : "lan".equals(HubRoutes.type(host)) ? 1 : 2));
        return result.size() > 8 ? new java.util.ArrayList<>(result.subList(0, 8)) : result;
    }
    public void mergeAddresses(org.json.JSONArray addresses, String reachableHost) {
        if (addresses == null || addresses.length() == 0) return;
        synchronized (HubStore.class) {
            java.util.LinkedHashSet<String> fresh = new java.util.LinkedHashSet<>();
            for (int i = 0; i < Math.min(64, addresses.length()); i++) {
                String host = addresses.optString(i, "").trim();
                if (HubRoutes.isUsableIpv4(host)) fresh.add(host);
            }
            if (fresh.isEmpty()) return;
            java.util.LinkedHashSet<String> merged = new java.util.LinkedHashSet<>();
            if (reachableHost != null && HubRoutes.isUsableIpv4(reachableHost.trim())) merged.add(reachableHost.trim());
            merged.addAll(fresh);
            for (String saved : get("hosts", "").split("[,;，；\\s]+")) {
                String host = saved.trim(); if (!HubRoutes.isUsableIpv4(host)) continue;
                if (!HubAddressFreshness.eligible(HubRoutes.type(host), preferences.getLong("seen-" + host, 0),
                        get("network-" + host, ""), HubNetworkStamp.current(), System.currentTimeMillis())) continue;
                if (merged.size() >= 24) break;
                boolean replacedUsb = false;
                if (!fresh.contains(host)) for (String discovered : fresh) {
                    if (HubRoutes.sameUsbSubnet(host, discovered)) { replacedUsb = true; break; }
                }
                if (!replacedUsb) merged.add(host);
            }
            SharedPreferences.Editor edit = preferences.edit().putString("hosts", android.text.TextUtils.join(",", merged));
            long now = System.currentTimeMillis(); String network = HubNetworkStamp.current();
            for (String host : fresh) edit.putLong("seen-" + host, now).putString("network-" + host, network);
            if (reachableHost != null) edit.putLong("seen-" + reachableHost, now).putString("network-" + reachableHost, network);
            edit.apply();
        }
    }
    public String clientId() {
        synchronized (HubStore.class) {
            String value = get("clientId", "");
            if (value.isEmpty()) { value = java.util.UUID.randomUUID().toString(); preferences.edit().putString("clientId", value).commit(); }
            return value;
        }
    }
    private static SecretKey key() throws Exception {
        KeyStore store = KeyStore.getInstance("AndroidKeyStore"); store.load(null);
        String alias = "StreamHubDeviceBinding";
        if (!store.containsAlias(alias)) {
            KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore");
            generator.init(new KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).build());
            generator.generateKey();
        }
        return (SecretKey) store.getKey(alias, null);
    }
    private static String sealInvitation(String invitation) throws Exception {
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding"); cipher.init(Cipher.ENCRYPT_MODE, key());
        return Base64.encodeToString(cipher.getIV(), Base64.NO_WRAP) + "." +
                Base64.encodeToString(cipher.doFinal(invitation.getBytes(StandardCharsets.UTF_8)), Base64.NO_WRAP);
    }
    public String fileKey() throws Exception {
        synchronized (HubStore.class) {
            String stored = get("fileDeviceKey", "");
            if (stored.isEmpty()) {
                byte[] bytes = new byte[32]; new java.security.SecureRandom().nextBytes(bytes);
                String value = HubControlClient.hex(bytes);
                if (!preferences.edit().putString("fileDeviceKey", codec.seal(value)).commit()) throw new java.io.IOException("无法保存文件设备密钥。");
                return value;
            }
            return codec.open(stored);
        }
    }
    /** Called only with an authenticated /info result. Commit new trust atomically. */
    public void acceptBinding(String invitation, String host, org.json.JSONObject info) throws Exception {
        acceptBinding(invitation, host, info, () -> false);
    }
    public void acceptBinding(String invitation, String host, org.json.JSONObject info,
                              java.util.function.BooleanSupplier cancelled) throws Exception {
        checkBindingCancelled(cancelled);
        String deviceId = info.optString("deviceId", "");
        if (deviceId.isEmpty() || deviceId.length() > 128) throw new IllegalStateException("电脑身份信息无效，原绑定已保留。");
        String sealed = codec.seal(invitation); // Encryption must succeed before touching the editor.
        java.util.LinkedHashSet<String> addresses = new java.util.LinkedHashSet<>(); addresses.add(host);
        org.json.JSONArray reported = info.optJSONArray("addresses");
        if (reported != null) for (int i = 0; i < Math.min(64, reported.length()); i++)
            if (HubRoutes.isUsableIpv4(reported.optString(i))) addresses.add(reported.optString(i));
        synchronized (HubStore.class) {
            checkBindingCancelled(cancelled);
            boolean sameTrust = false;
            if (deviceId.equals(get("deviceId", "")) && bound()) {
                try { sameTrust = invitation.equals(codec.open(get("invitation", ""))); }
                catch (Exception ignored) { /* Unreadable old trust must not preserve media pins. */ }
            }
            SharedPreferences.Editor edit = preferences.edit();
            for (String name : preferences.getAll().keySet())
                if ((!sameTrust && name.startsWith("cert-")) || name.startsWith("seen-") || name.startsWith("network-")) edit.remove(name);
            edit.putString("invitation", sealed).putString("deviceId", deviceId)
                .putString("deviceName", info.optString("deviceName", info.optString("name", "我的电脑")))
                .putString("hosts", android.text.TextUtils.join(",", addresses)).putString("lastHost", host)
                .putString("bindingRevision", java.util.UUID.randomUUID().toString());
            if (!sameTrust) edit.putString("lastMode", "");
            long now = System.currentTimeMillis(); String network = HubNetworkStamp.current();
            for (String address : addresses) edit.putLong("seen-" + address, now).putString("network-" + address, network);
            checkBindingCancelled(cancelled);
            if (!edit.commit()) throw new java.io.IOException("绑定保存失败，请重试。");
        }
    }
    private static void checkBindingCancelled(java.util.function.BooleanSupplier cancelled) {
        if (cancelled.getAsBoolean() || Thread.currentThread().isInterrupted())
            throw new java.util.concurrent.CancellationException("已取消绑定，原绑定已保留。");
    }
    public void clearBinding() {
        synchronized (HubStore.class) {
            new HubLoginSecrets(context).forget();
            SharedPreferences.Editor edit = preferences.edit();
            for (String name : preferences.getAll().keySet())
                if (name.startsWith("cert-") || name.startsWith("seen-") || name.startsWith("network-")) edit.remove(name);
            edit.putString("invitation", "").putString("deviceId", "").putString("hosts", "").putString("lastHost", "")
                .putString("bindingRevision", java.util.UUID.randomUUID().toString()).apply();
        }
    }
    public String invitation() throws Exception {
        return codec.open(get("invitation", ""));
    }
    private static String unseal(String sealed) throws Exception {
        String[] parts = sealed.split("\\.", 2);
        if (parts.length != 2) throw new IllegalStateException("请先绑定电脑。");
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.DECRYPT_MODE, key(), new GCMParameterSpec(128, Base64.decode(parts[0], Base64.NO_WRAP)));
        return new String(cipher.doFinal(Base64.decode(parts[1], Base64.NO_WRAP)), StandardCharsets.UTF_8);
    }
}
