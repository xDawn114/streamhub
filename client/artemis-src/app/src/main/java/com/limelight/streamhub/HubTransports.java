package com.limelight.streamhub;

import java.net.URI;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

/** Provider configuration contains no tokens. Unknown/corrupt settings fail closed. */
public final class HubTransports {
    public static final String OFFICIAL_URL = "https://controlplane.tailscale.com";
    public static final String[] IDS = {"tailscale", "self-hosted", "lan", "manual"};
    public static final String[] LABELS = {"Tailscale 官方", "自建服务区", "局域网 / USB", "手动地址"};
    private HubTransports() { }

    public static final class Settings {
        public final String provider, serverUrl, manualAddresses;
        public Settings(String provider, String serverUrl, String manualAddresses) {
            get(provider);
            this.provider = provider;
            this.serverUrl = "self-hosted".equals(provider) ? serverUrl(serverUrl) : "";
            List<String> manual = new ArrayList<>();
            if ("manual".equals(provider)) {
                if (manualAddresses == null || manualAddresses.trim().isEmpty() || manualAddresses.length() > 512)
                    throw new IllegalArgumentException("请填写电脑的 IPv4 地址，最多 8 个，用逗号或换行分隔。");
                for (String part : manualAddresses.trim().split("[,;，；\\s]+")) {
                    if (!HubRoutes.isUsableIpv4(part)) throw new IllegalArgumentException("手动地址仅支持有效 IPv4，不含端口或网址。");
                    if (!manual.contains(part)) manual.add(part);
                }
                if (manual.isEmpty() || manual.size() > 8) throw new IllegalArgumentException("请填写 1 至 8 个手动地址。");
            }
            this.manualAddresses = android.text.TextUtils.join(",", manual);
        }
        public HubTransport transport() { return get(provider); }
        public String controlUrl() { return "self-hosted".equals(provider) ? serverUrl : OFFICIAL_URL; }
    }

    public static HubTransport get(String id) {
        for (int i = 0; i < IDS.length; i++) if (IDS[i].equals(id)) return new Provider(IDS[i], LABELS[i]);
        throw new IllegalArgumentException("连接通道配置无效，请重新选择并保存。");
    }
    public static String serverUrl(String value) {
        try {
            URI uri = checkedHttps(value);
            if (!uri.getRawPath().isEmpty() && !"/".equals(uri.getRawPath())) throw new IllegalArgumentException();
            if (uri.getRawQuery() != null || uri.getRawFragment() != null) throw new IllegalArgumentException();
            return "https://" + uri.getHost().toLowerCase(Locale.ROOT) + (port(uri) == 443 ? "" : ":" + port(uri));
        } catch (Exception error) { throw new IllegalArgumentException("服务区须为 HTTPS 根地址，例如 https://vpn.example.com，不含账号、路径或查询参数。"); }
    }
    public static String authorizationUrl(Settings settings, String value) {
        if (!settings.transport().usesAccount()) return "";
        try {
            URI uri = checkedHttps(value);
            String host = uri.getHost().toLowerCase(Locale.ROOT);
            if ("tailscale".equals(settings.provider)) {
                if (port(uri) != 443 || !(host.equals("tailscale.com") || host.endsWith(".tailscale.com"))) return "";
            } else {
                URI control = checkedHttps(settings.serverUrl);
                if (!host.equalsIgnoreCase(control.getHost()) || port(uri) != port(control)) return "";
            }
            return uri.toASCIIString();
        } catch (Exception ignored) { return ""; }
    }
    public static String controlServer(String value) {
        if (value == null || value.isEmpty()) return OFFICIAL_URL;
        String normalized = serverUrl(value);
        return "https://login.tailscale.com".equals(normalized) ? OFFICIAL_URL : normalized;
    }
    private static URI checkedHttps(String value) throws Exception {
        if (value == null || value.isEmpty() || value.length() > 2048 || !value.equals(value.trim()) || value.indexOf('\\') >= 0)
            throw new IllegalArgumentException();
        for (int i = 0; i < value.length(); i++) if (Character.isISOControl(value.charAt(i)) || Character.isWhitespace(value.charAt(i))) throw new IllegalArgumentException();
        URI uri = new URI(value);
        if (!"https".equalsIgnoreCase(uri.getScheme()) || uri.getHost() == null || uri.getRawUserInfo() != null ||
                uri.getHost().endsWith(".") || uri.getPort() == 0 || uri.getPort() > 65535) throw new IllegalArgumentException();
        return uri;
    }
    private static int port(URI uri) { return uri.getPort() < 0 ? 443 : uri.getPort(); }
    private static final class Provider implements HubTransport {
        private final String id, label;
        Provider(String id, String label) { this.id = id; this.label = label; }
        public String id() { return id; }
        public String label() { return label; }
        public boolean usesAccount() { return id.equals("tailscale") || id.equals("self-hosted"); }
        public List<String> candidates(List<String> known, Settings settings, String route) {
            List<String> result = new ArrayList<>();
            List<String> source = "manual".equals(id) ? HubRoutes.usableAddresses(settings.manualAddresses) : known;
            for (String host : source) {
                if (!HubRoutes.isUsableIpv4(host)) continue;
                String type = HubRoutes.type(host);
                if ("lan".equals(id) && "remote".equals(type)) continue;
                if (!("auto".equals(route) || route.equals(type))) continue;
                if (!result.contains(host)) result.add(host);
            }
            return result;
        }
    }
}
