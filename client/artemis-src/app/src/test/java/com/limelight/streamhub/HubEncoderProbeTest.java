package com.limelight.streamhub;

import com.limelight.preferences.PreferenceConfiguration;
import org.junit.Test;
import static org.junit.Assert.*;

public class HubEncoderProbeTest {
    @Test public void requestedCodecKeepsFallbackAndHdrRequiresFullCapabilities() {
        assertEquals("sdr-h264", HubEncoderProbe.plan(PreferenceConfiguration.FormatOption.FORCE_H264, false));
        assertEquals("sdr-hevc", HubEncoderProbe.plan(PreferenceConfiguration.FormatOption.FORCE_HEVC, false));
        assertEquals("sdr-av1", HubEncoderProbe.plan(PreferenceConfiguration.FormatOption.FORCE_AV1, false));
        assertEquals("sdr-auto", HubEncoderProbe.plan(PreferenceConfiguration.FormatOption.AUTO, false));
        for (PreferenceConfiguration.FormatOption format : PreferenceConfiguration.FormatOption.values())
            assertEquals("full", HubEncoderProbe.plan(format, true));
    }
}
