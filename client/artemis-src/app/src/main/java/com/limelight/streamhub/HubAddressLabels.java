package com.limelight.streamhub;

import java.net.InterfaceAddress;
import java.net.NetworkInterface;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Locale;

/** Describes the Android-side path; it never guesses the computer's adapter from an IP. */
public final class HubAddressLabels {
    public static final class Link {
        final String name, address;
        final int prefix;
        Link(String name, String address, int prefix) {
            this.name = name.toLowerCase(Locale.ROOT);
            this.address = address;
            this.prefix = prefix;
        }
    }

    private HubAddressLabels() { }

    public static List<Link> currentLinks() {
        List<Link> links = new ArrayList<>();
        try {
            for (NetworkInterface nic : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                if (!nic.isUp() || nic.isLoopback()) continue;
                for (InterfaceAddress local : nic.getInterfaceAddresses()) {
                    String address = local.getAddress().getHostAddress();
                    if (HubRoutes.isUsableIpv4(address))
                        links.add(new Link(nic.getName(), address, local.getNetworkPrefixLength()));
                }
            }
        } catch (Exception ignored) { }
        return links;
    }

    public static String describe(String host, List<Link> links) {
        if (!HubRoutes.isUsableIpv4(host)) return "其他地址";
        long target = ipv4(host);
        if (target >= ipv4("100.64.0.0") && target <= ipv4("100.127.255.255")) return "异地地址";
        String result = "其他地址";
        int bestPrefix = -1;
        for (Link link : links) {
            if (!HubRoutes.isUsableIpv4(link.address) || link.prefix <= 0 || link.prefix > 32) continue;
            long mask = (0xffffffffL << (32 - link.prefix)) & 0xffffffffL;
            if ((target & mask) != (ipv4(link.address) & mask)) continue;
            String label = link.name.contains("rndis") || link.name.contains("usb") || link.name.contains("ncm") ? "局域网/USB"
                    : link.name.startsWith("wlan") || link.name.startsWith("wifi") || link.name.startsWith("swlan") ? "局域网/USB"
                    : link.name.startsWith("eth") || link.name.startsWith("en") ? "局域网/USB" : "其他地址";
            if (link.prefix > bestPrefix) { result = label; bestPrefix = link.prefix; }
        }
        return result;
    }

    public static String line(String host, String currentHost, List<Link> links) {
        return describe(host, links) + (host.equals(currentHost) ? " · 当前使用" : "") + "\n" + host;
    }

    public static String actualRoute(String host, List<Link> links, String provider,
            boolean accountTunnelActive, List<String> manualAddresses) {
        String addressKind = describe(host, links);
        if ("局域网/USB".equals(addressKind)) return addressKind;
        if ("manual".equals(provider) && manualAddresses.contains(host)) return "手动地址";
        if ("异地地址".equals(addressKind) && accountTunnelActive) {
            if ("tailscale".equals(provider)) return "Tailscale";
            if ("self-hosted".equals(provider)) return "自建服务区";
        }
        // An address alone cannot identify which account provider carried it.
        return addressKind;
    }

    private static long ipv4(String value) {
        long result = 0;
        for (String part : value.split("\\.")) result = (result << 8) | Integer.parseInt(part);
        return result;
    }
}
