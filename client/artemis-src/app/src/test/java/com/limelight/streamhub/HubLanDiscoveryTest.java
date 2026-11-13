package com.limelight.streamhub;

import android.app.Application;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.annotation.Config;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=34,manifest=Config.NONE,application=Application.class)
public class HubLanDiscoveryTest {
    @Test public void enrollmentScopeExcludesInternetAndOverlayAddresses() {
        for(String value:new String[]{"10.1.2.3","172.16.0.1","172.31.255.254","192.168.1.2"})assertTrue(value,HubLanDiscovery.localIpv4(value));
        for(String value:new String[]{"169.254.2.1","100.89.106.49","8.8.8.8","127.0.0.1","172.32.0.1","::1","192.168.1.256","bad"})assertFalse(value,HubLanDiscovery.localIpv4(value));
    }
    @Test public void android14StopIsIdempotentWithoutStartingNetworkDiscovery() {
        HubLanDiscovery discovery=new HubLanDiscovery(RuntimeEnvironment.getApplication());
        discovery.stop();discovery.stop();
    }
    @Test public void subnetFilterAllowsSameCampusNetworkButRejectsOtherLinksAndOverlay() {
        assertTrue(HubLanDiscovery.sameSubnet(new byte[]{10,61,(byte)179,(byte)246},new byte[]{10,61,(byte)235,35},16));
        assertFalse(HubLanDiscovery.sameSubnet(new byte[]{10,61,1,2},new byte[]{10,62,1,3},16));
        assertFalse(HubLanDiscovery.sameSubnet(new byte[]{10,61,1,2},new byte[]{100,89,1,3},16));
        assertFalse(HubLanDiscovery.sameSubnet(new byte[]{10,61,1,2},new byte[]{10,61,1,3},0));
        assertFalse(HubLanDiscovery.sameSubnet(new byte[16],new byte[]{10,61,1,3},16));
    }
}
