package com.limelight.streamhub;

import java.net.InetAddress;
import java.net.InterfaceAddress;
import java.net.NetworkInterface;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.List;
import java.util.Locale;

/** Select the actual destination route; USB mode cannot silently stream over Wi-Fi. */
public final class HubRoutes {
    private HubRoutes() { }
    public static boolean isIpv4(String host) {
        if (host == null || !host.matches("[0-9]{1,3}(\\.[0-9]{1,3}){3}")) return false;
        for (String part : host.split("\\.")) if (Integer.parseInt(part) > 255) return false;
        return true;
    }
    public static boolean isUsableIpv4(String host) {
        if (!isIpv4(host)) return false;
        String[] parts = host.split("\\.");
        for (String part : parts) if (part.length() > 1 && part.startsWith("0")) return false;
        int a = Integer.parseInt(parts[0]), b = Integer.parseInt(parts[1]);
        return a > 0 && a < 224 && a != 127 && !(a == 169 && b == 254);
    }
    private static boolean inSubnet(byte[] own, byte[] target, int prefix) {
        if (own.length != target.length || prefix <= 0) return false;
        for (int i = 0; i < own.length && prefix > 0; i++, prefix -= 8) {
            int mask = prefix >= 8 ? 255 : 255 << (8 - prefix);
            if ((own[i] & mask) != (target[i] & mask)) return false;
        }
        return true;
    }
    /** Only replace stale literal addresses on the same currently attached USB subnet. */
    public static boolean sameUsbSubnet(String first, String second) {
        if (!isIpv4(first) || !isIpv4(second)) return false;
        try {
            byte[] a = InetAddress.getByName(first).getAddress(), b = InetAddress.getByName(second).getAddress();
            for (NetworkInterface nic : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                String name = nic.getName().toLowerCase(Locale.ROOT);
                if (!(name.contains("rndis") || name.contains("usb") || name.contains("ncm"))) continue;
                for (InterfaceAddress address : nic.getInterfaceAddresses()) {
                    byte[] own = address.getAddress().getAddress(); int prefix = address.getNetworkPrefixLength();
                    if (inSubnet(own, a, prefix) && inSubnet(own, b, prefix)) return true;
                }
            }
        } catch (Exception ignored) { }
        return false;
    }
    public static String type(String host) {
        if (host.startsWith("100.") || host.toLowerCase(Locale.ROOT).startsWith("fd7a:") || host.endsWith(".ts.net")) return "remote";
        try {
            byte[] target = InetAddress.getByName(host).getAddress();
            for (NetworkInterface nic : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                String name = nic.getName().toLowerCase(Locale.ROOT);
                if (!(name.contains("rndis") || name.contains("usb") || name.contains("ncm"))) continue;
                for (InterfaceAddress address : nic.getInterfaceAddresses()) {
                    byte[] own = address.getAddress().getAddress(); int prefix = address.getNetworkPrefixLength();
                    if (inSubnet(own, target, prefix)) return "usb";
                }
            }
        } catch (Exception ignored) { }
        return "lan";
    }
    public static List<String> candidates(String saved, String selection) {
        List<String> hosts = new ArrayList<>();
        for (String part : saved.split("[,;，；\\s]+")) {
            String host = part.trim();
            if (isUsableIpv4(host) && !hosts.contains(host) && ("auto".equals(selection) || selection.equals(type(host)))) hosts.add(host);
        }
        if ("auto".equals(selection)) hosts.sort(Comparator.comparingInt(host -> "usb".equals(type(host)) ? 0 : "lan".equals(type(host)) ? 1 : 2));
        return hosts;
    }
    public static List<String> usableAddresses(String saved) {
        List<String> hosts = new ArrayList<>();
        for (String part : saved.split("[,;，；\\s]+")) {
            String host = part.trim();
            if (isUsableIpv4(host) && !hosts.contains(host)) hosts.add(host);
        }
        return hosts;
    }
    public static String label(String host) { String type = type(host); return "usb".equals(type) ? "USB 有线" : "remote".equals(type) ? "异地网络" : "局域网 / Wi-Fi"; }
}
