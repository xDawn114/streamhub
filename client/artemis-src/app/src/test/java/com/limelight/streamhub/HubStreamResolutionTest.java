package com.limelight.streamhub;

import org.junit.Test;
import static org.junit.Assert.*;
import java.util.List;

public class HubStreamResolutionTest {
    @Test public void keepsPortraitAspectAndEvenDimensions() {
        List<int[]> values=HubStreamResolution.choices(2064,3096);
        for(int[] size:values) {
            assertTrue(size[0]<size[1]); assertTrue(HubStreamResolution.valid(size[0],size[1],60));
            assertEquals(2064f/3096,size[0]/(float)size[1],.002);
        }
        assertArrayEquals(new int[]{2064,3096},values.get(values.size()-1));
    }
    @Test public void boundsByNativeAndDeduplicatesPreset() {
        List<int[]> values=HubStreamResolution.choices(1920,1080);
        assertEquals(2,values.size()); assertArrayEquals(new int[]{1280,720},values.get(0));
        assertArrayEquals(new int[]{1920,1080},values.get(1));
    }
    @Test public void invalidNegotiationNeverPasses() {
        assertFalse(HubStreamResolution.valid(0,1080,60)); assertFalse(HubStreamResolution.valid(1921,1080,60));
        assertFalse(HubStreamResolution.valid(8000,4320,60)); assertFalse(HubStreamResolution.valid(1920,1080,0));
        assertFalse(HubStreamResolution.valid(7680,7680,60));
    }
    @Test public void secondaryDisplayUsesOnlyAdvertisedDriverSizes() {
        List<int[]> values=HubStreamResolution.supportedChoices(java.util.Arrays.asList(
                new int[]{2064,3096,60}, new int[]{1080,1920,60}, new int[]{1080,1920,90},
                new int[]{1081,1920,60}, new int[]{7680,7680,60}, new int[]{800,1280,0}));
        assertEquals(2,values.size()); assertArrayEquals(new int[]{1080,1920},values.get(0));
        assertArrayEquals(new int[]{2064,3096},values.get(1));
        assertTrue(HubStreamResolution.supportedChoices(java.util.Collections.emptyList()).isEmpty());
    }
}
