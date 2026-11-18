package com.limelight.streamhub;

import java.util.Arrays;
import java.util.Collections;
import org.junit.Test;
import static org.junit.Assert.*;

public class HubAddressLabelsTest {
    @Test public void privateIpAloneDoesNotClaimWifiOrUsb() {
        assertEquals("其他地址",HubAddressLabels.describe("10.61.235.35",Collections.emptyList()));
        assertEquals("其他地址",HubAddressLabels.describe("10.74.226.160",Collections.emptyList()));
        assertEquals("其他地址",HubAddressLabels.describe("100.40.0.1",Collections.emptyList()));
    }
    @Test public void currentlyAttachedSubnetsIdentifyActualAndroidPaths() {
        java.util.List<HubAddressLabels.Link> links=Arrays.asList(
                new HubAddressLabels.Link("wlan0","10.20.0.2",24),
                new HubAddressLabels.Link("rndis0","192.168.42.129",24),
                new HubAddressLabels.Link("eth0","172.16.5.2",24));
        assertEquals("局域网/USB",HubAddressLabels.describe("10.20.0.10",links));
        assertEquals("局域网/USB",HubAddressLabels.describe("192.168.42.3",links));
        assertEquals("局域网/USB",HubAddressLabels.describe("172.16.5.4",links));
        assertEquals("其他地址",HubAddressLabels.describe("10.21.0.10",links));
    }
    @Test public void defaultRouteCannotLabelEveryPrivateAddressAsWifi() {
        assertEquals("其他地址",HubAddressLabels.describe("10.2.3.4",Collections.singletonList(
                new HubAddressLabels.Link("wlan0","10.1.0.2",0))));
        assertEquals("局域网/USB",HubAddressLabels.describe("10.2.3.4",Arrays.asList(
                new HubAddressLabels.Link("wlan0","10.1.0.2",8),
                new HubAddressLabels.Link("usb0","10.2.3.1",24))));
    }
    @Test public void currentMarkerComesFromConfirmedAddressNotAddressOrder() {
        java.util.List<HubAddressLabels.Link> links=Collections.singletonList(new HubAddressLabels.Link("wlan0","192.168.1.2",24));
        assertFalse(HubAddressLabels.line("192.168.1.3","192.168.1.4",links).contains("当前使用"));
        assertTrue(HubAddressLabels.line("192.168.1.4","192.168.1.4",links).contains("当前使用"));
        assertFalse(HubAddressLabels.line("192.168.1.4","",links).contains("当前使用"));
        assertEquals("异地地址",HubAddressLabels.describe("100.64.0.1",links));
    }
    @Test public void configuredProviderCannotReplaceTheConfirmedAddressPath() {
        java.util.List<HubAddressLabels.Link> links=Collections.singletonList(new HubAddressLabels.Link("wlan0","192.168.1.2",24));
        assertEquals("局域网/USB",HubAddressLabels.actualRoute("192.168.1.4",links,"tailscale",true,Collections.emptyList()));
        assertEquals("异地地址",HubAddressLabels.actualRoute("100.89.1.4",links,"tailscale",false,Collections.emptyList()));
        assertEquals("异地地址",HubAddressLabels.actualRoute("100.89.1.4",links,"lan",true,Collections.emptyList()));
        assertEquals("Tailscale",HubAddressLabels.actualRoute("100.89.1.4",links,"tailscale",true,Collections.emptyList()));
        assertEquals("自建服务区",HubAddressLabels.actualRoute("100.89.1.4",links,"self-hosted",true,Collections.emptyList()));
        assertEquals("手动地址",HubAddressLabels.actualRoute("203.0.113.8",links,"manual",false,Collections.singletonList("203.0.113.8")));
        assertEquals("其他地址",HubAddressLabels.actualRoute("203.0.113.9",links,"manual",false,Collections.singletonList("203.0.113.8")));
    }
}
