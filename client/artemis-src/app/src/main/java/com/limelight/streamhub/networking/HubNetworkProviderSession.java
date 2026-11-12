package com.limelight.streamhub.networking;

import com.limelight.streamhub.HubTransports;
import org.json.JSONArray;
import org.json.JSONObject;

/** LocalAPI protocol, independently testable without starting a VPN or the native backend. */
public final class HubNetworkProviderSession {
    public interface Api { JSONObject call(String method, String endpoint, String body) throws Exception; }
    private final Api api;
    public HubNetworkProviderSession(Api api) { this.api = api; }
    private String currentServer() throws Exception {
        String saved = api.call("GET", "prefs", null).optString("ControlURL");
        return HubTransports.controlServer(saved);
    }
    public void requireCurrent(HubTransports.Settings settings) throws Exception {
        if (!settings.transport().usesAccount()) throw new IllegalStateException("当前通道无需连接账号。请先选择 Tailscale 或自建服务区。");
        if (!HubTransports.controlServer(settings.controlUrl()).equals(currentServer())) throw new IllegalStateException("内置连接的服务区与设置不一致，请重新应用通道设置。");
    }
    public JSONObject status(HubTransports.Settings settings) throws Exception {
        requireCurrent(settings);
        return api.call("GET", "status", null);
    }
    public void configure(HubTransports.Settings settings) throws Exception {
        JSONObject patch = new JSONObject().put("WantRunning", false).put("WantRunningSet", true);
        if (settings.transport().usesAccount()) {
            String actual = currentServer();
            if (!HubTransports.controlServer(settings.controlUrl()).equals(actual)) {
                JSONObject status = api.call("GET", "status", null);
                JSONArray ips = status.optJSONArray("TailscaleIPs");
                JSONObject self = status.optJSONObject("Self");
                String user = self == null ? "" : self.optString("UserID");
                if ((ips != null && ips.length() > 0) || (!user.isEmpty() && !"0".equals(user)))
                    throw new IllegalStateException("更换服务区前请先退出当前连接账号。电脑绑定会保留。");
                patch.put("ControlURL", settings.controlUrl()).put("ControlURLSet", true);
            }
        }
        api.call("PATCH", "prefs", patch.toString());
        // Start({}) rebuilds controlclient from the now-saved preferences without
        // copying/replacing any unrelated preferences or initiating interactive login.
        // Reapply also repairs an interrupted PATCH -> Start operation.
        if (settings.transport().usesAccount()) api.call("POST", "start", "{}");
    }
}
