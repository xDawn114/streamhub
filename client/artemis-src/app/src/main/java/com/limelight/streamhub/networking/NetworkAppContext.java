package com.limelight.streamhub.networking;

import android.content.Context;
import android.content.SharedPreferences;
import android.net.ConnectivityManager;
import android.net.LinkProperties;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.net.NetworkRequest;
import android.os.Build;
import android.os.ParcelFileDescriptor;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;

import org.json.JSONArray;
import org.json.JSONObject;

import java.net.InetAddress;
import java.net.InterfaceAddress;
import java.net.NetworkInterface;
import java.security.KeyStore;
import java.util.Collections;
import java.util.Enumeration;

import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

import libtailscale.AppContext;
import libtailscale.Libtailscale;

/** Android callbacks consumed by the official, in-process Go network backend. */
final class NetworkAppContext implements AppContext {
    private static final String KEY_ALIAS = "streamhub.network-state.v1";
    private final Context context;
    private final SharedPreferences state;
    private final ConnectivityManager connectivity;
    private boolean watching;
    NetworkAppContext(Context context) {
        this.context = context;
        state = context.getSharedPreferences("embedded-network-private", Context.MODE_PRIVATE);
        connectivity = (ConnectivityManager) context.getSystemService(Context.CONNECTIVITY_SERVICE);
    }
    private synchronized SecretKey key() throws Exception {
        KeyStore store = KeyStore.getInstance("AndroidKeyStore"); store.load(null);
        if (!store.containsAlias(KEY_ALIAS)) {
            KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore");
            generator.init(new KeyGenParameterSpec.Builder(KEY_ALIAS, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).build());
            generator.generateKey();
        }
        return (SecretKey) store.getKey(KEY_ALIAS, null);
    }
    @Override public synchronized void encryptToPref(String name, String value) throws Exception {
        if (value == null || value.isEmpty()) { if (!state.edit().remove(name).commit()) throw new IllegalStateException("无法保存内置连接状态。"); return; }
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding"); cipher.init(Cipher.ENCRYPT_MODE, key());
        byte[] encrypted = cipher.doFinal(value.getBytes(java.nio.charset.StandardCharsets.UTF_8));
        String encoded = Base64.encodeToString(cipher.getIV(), Base64.NO_WRAP) + ":" + Base64.encodeToString(encrypted, Base64.NO_WRAP);
        if (!state.edit().putString(name, encoded).commit()) throw new IllegalStateException("无法保存内置连接状态。");
    }
    @Override public synchronized String decryptFromPref(String name) throws Exception {
        String encoded = state.getString(name, null); if (encoded == null) return "";
        String[] parts = encoded.split(":", 2); if (parts.length != 2) throw new IllegalStateException("内置连接状态格式不完整。");
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding"); cipher.init(Cipher.DECRYPT_MODE, key(), new GCMParameterSpec(128, Base64.decode(parts[0], Base64.NO_WRAP)));
        return new String(cipher.doFinal(Base64.decode(parts[1], Base64.NO_WRAP)), java.nio.charset.StandardCharsets.UTF_8);
    }
    @Override public String getStateStoreKeysJSON() { JSONArray keys = new JSONArray(); for (String key : state.getAll().keySet()) if (key.startsWith("statestore-")) keys.put(key.substring("statestore-".length())); return keys.toString(); }
    @Override public String getOSVersion() { return Build.VERSION.RELEASE; }
    @Override public long getSDKInt() { return Build.VERSION.SDK_INT; }
    @Override public String getDeviceName() { return Build.MANUFACTURER + " " + Build.MODEL + " 我控制"; }
    @Override public String getInstallSource() { return "streamhub"; }
    @Override public boolean shouldUseGoogleDNSFallback() { return false; }
    @Override public boolean isChromeOS() { return context.getPackageManager().hasSystemFeature("org.chromium.arc.device_management"); }
    @Override public boolean isClientLoggingEnabled() { return false; }
    @Override public void log(String tag, String line) { /* Do not duplicate backend diagnostics or credentials into logcat. */ }
    @Override public String getInterfacesAsJson() throws Exception {
        JSONArray all = new JSONArray(); Enumeration<NetworkInterface> list = NetworkInterface.getNetworkInterfaces();
        if (list == null) return "[]";
        for (NetworkInterface iface : Collections.list(list)) {
            try {
                JSONArray addresses = new JSONArray();
                for (InterfaceAddress address : iface.getInterfaceAddresses()) { InetAddress ip = address.getAddress(); if (ip != null) addresses.put(new JSONObject().put("ip", ip.getHostAddress()).put("prefixLen", address.getNetworkPrefixLength())); }
                all.put(new JSONObject().put("name", iface.getName()).put("index", iface.getIndex()).put("mtu", iface.getMTU()).put("up", iface.isUp())
                        .put("broadcast", iface.supportsMulticast()).put("loopback", iface.isLoopback()).put("pointToPoint", iface.isPointToPoint()).put("multicast", iface.supportsMulticast()).put("addrs", addresses));
            } catch (Exception ignored) { }
        }
        return all.toString();
    }
    Network underlyingNetwork() {
        if (connectivity == null) return null;
        Network active = connectivity.getActiveNetwork();
        if (usable(active)) return active;
        for (Network network : connectivity.getAllNetworks()) if (usable(network)) return network;
        return null;
    }
    synchronized void startNetworkMonitor() {
        if (watching || connectivity == null) return;
        watching = true;
        connectivity.registerNetworkCallback(new NetworkRequest.Builder().addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
                .addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN).build(), new ConnectivityManager.NetworkCallback() {
            @Override public void onAvailable(Network network) { changed(network); }
            @Override public void onLinkPropertiesChanged(Network network, LinkProperties properties) { changed(network); }
            @Override public void onLost(Network network) { changed(underlyingNetwork()); }
            private void changed(Network current) {
                LinkProperties properties = current == null ? null : connectivity.getLinkProperties(current);
                Libtailscale.onDNSConfigChanged(properties == null || properties.getInterfaceName() == null ? "" : properties.getInterfaceName());
            }
        });
    }
    private boolean usable(Network network) { NetworkCapabilities caps = network == null ? null : connectivity.getNetworkCapabilities(network); return caps != null && caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) && caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN); }
    @Override public String getPlatformDNSConfig() {
        Network network = underlyingNetwork(); LinkProperties properties = network == null ? null : connectivity.getLinkProperties(network); if (properties == null) return "";
        StringBuilder servers = new StringBuilder(); for (InetAddress server : properties.getDnsServers()) { if (servers.length() > 0) servers.append(' '); servers.append(server.getHostAddress()); }
        return servers + "\n" + (properties.getDomains() == null ? "" : properties.getDomains());
    }
    @Override public boolean bindSocketToNetwork(int fd) {
        Network network = underlyingNetwork(); if (network == null) return false;
        try (ParcelFileDescriptor descriptor = ParcelFileDescriptor.fromFd(fd)) { network.bindSocket(descriptor.getFileDescriptor()); return true; } catch (Exception ignored) { return false; }
    }
    private Exception noPolicy() { return new Exception("no such key"); }
    @Override public String getSyspolicyStringValue(String name) throws Exception { throw noPolicy(); }
    @Override public boolean getSyspolicyBooleanValue(String name) throws Exception { throw noPolicy(); }
    @Override public String getSyspolicyStringArrayJSONValue(String name) throws Exception { throw noPolicy(); }
    @Override public boolean hardwareAttestationKeySupported() { return false; }
    @Override public String hardwareAttestationKeyCreate() throws Exception { throw new UnsupportedOperationException("此版本不启用硬件证明。"); }
    @Override public void hardwareAttestationKeyRelease(String id) throws Exception { throw new UnsupportedOperationException(); }
    @Override public byte[] hardwareAttestationKeyPublic(String id) throws Exception { throw new UnsupportedOperationException(); }
    @Override public byte[] hardwareAttestationKeySign(String id, byte[] data) throws Exception { throw new UnsupportedOperationException(); }
    @Override public void hardwareAttestationKeyLoad(String id) throws Exception { throw new UnsupportedOperationException(); }
    @Override public byte[] getUserCACertsPEM() { return new byte[0]; }
}

