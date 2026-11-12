package com.limelight.streamhub;

import java.net.InterfaceAddress;
import java.net.Inet4Address;
import java.net.NetworkInterface;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Locale;

/** No SSID/location permission: only the attached non-tunnel IPv4 interfaces. */
public final class HubNetworkStamp {
    private HubNetworkStamp() { }
    public static String current() {
        ArrayList<String> parts = new ArrayList<>();
        try {
            for (NetworkInterface nic : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                String name = nic.getName().toLowerCase(Locale.ROOT);
                if (!nic.isUp() || nic.isLoopback() || name.startsWith("tun") || name.startsWith("wg") || name.contains("tailscale") ||
                    name.contains("rndis") || name.contains("usb") || name.contains("ncm")) continue;
                for (InterfaceAddress address : nic.getInterfaceAddresses())
                    if (address.getAddress() instanceof Inet4Address) {
                        byte[] subnet = address.getAddress().getAddress(); int prefix = address.getNetworkPrefixLength();
                        for (int i = 0, bits = prefix; i < subnet.length; i++, bits -= 8)
                            subnet[i] &= (byte)(bits >= 8 ? 255 : bits <= 0 ? 0 : 255 << (8 - bits));
                        parts.add(name + ":" + java.net.InetAddress.getByAddress(subnet).getHostAddress() + "/" + prefix);
                    }
            }
        } catch (Exception ignored) { return "unknown"; }
        Collections.sort(parts); return String.join(";", parts);
    }
}
