package com.limelight.streamhub;

import com.limelight.nvstream.StreamConfiguration;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.annotation.Config;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=28, manifest=Config.NONE, application=android.app.Application.class,
        shadows=com.limelight.shadows.ShadowMoonBridge.class)
public class HubAudioPolicyTest {
    private void expect(String mode, String selected, boolean host, boolean android) {
        int output = HubAudioPolicy.outputMode(selected);
        StreamConfiguration wireConfig = new StreamConfiguration.Builder()
                .enableLocalAudioPlayback(HubAudioPolicy.playOnHost(mode, output)).build();
        assertEquals("Windows playback flag serialized by NvHTTP", host, wireConfig.getPlayLocalAudio());
        assertEquals("Android playback renderer", android, HubAudioPolicy.playOnAndroid(mode, output));
    }
    @Test public void computerOnlyKeepsHostAudioAndDoesNotOpenAndroidAudio() {
        expect("remote", "computer", true, false);
    }
    @Test public void tabletOnlyPlaysOnAndroidAndRequestsHostSilence() {
        expect("remote", "tablet", false, true);
    }
    @Test public void bothPlaysAtBothEndpoints() {
        expect("remote", "both", true, true);
    }
    @Test public void secondaryDisplayAlwaysLeavesHostAudioAlone() {
        for (String selected : new String[]{"computer", "tablet", "both"}) expect("tablet", selected, true, false);
    }
    @Test public void missingOrUnknownSelectionPreservesComputerSound() {
        for (String selected : new String[]{null, "", "invalid"}) expect("remote", selected, true, false);
    }
}
