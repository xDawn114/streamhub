package com.limelight.streamhub;

import java.util.ArrayList;
import java.util.List;

/** Even dimensions with the source aspect ratio; no arbitrary or oversized modes. */
public final class HubStreamResolution {
    private HubStreamResolution() { }
    public static boolean valid(int width, int height, int fps) {
        return width >= 320 && height >= 320 && width <= 7680 && height <= 7680 &&
                width % 2 == 0 && height % 2 == 0 && (long)width * height <= 33177600 && fps >= 24 && fps <= 240;
    }
    public static List<int[]> choices(int width, int height) {
        if (!valid(width, height, 60)) throw new IllegalArgumentException("画面尺寸无效");
        List<int[]> result = new ArrayList<>();
        int longest = Math.max(width, height);
        for (int edge : new int[]{1280, 1920, 2560, longest}) {
            if (edge > longest) continue;
            float ratio = (float)edge / longest;
            int w = Math.max(320, Math.round(width * ratio / 2) * 2), h = Math.max(320, Math.round(height * ratio / 2) * 2);
            boolean duplicate = false;
            for (int[] item : result) if (item[0] == w && item[1] == h) duplicate = true;
            if (!duplicate) result.add(new int[]{w, h});
        }
        return result;
    }
    /** Driver modes are authoritative for an extended desktop; never invent a VDD size. */
    public static List<int[]> supportedChoices(List<int[]> modes) {
        List<int[]> result = new ArrayList<>();
        for (int[] mode : modes) {
            if (mode == null || mode.length < 3 || !valid(mode[0], mode[1], mode[2])) continue;
            boolean duplicate = false;
            for (int[] item : result) if (item[0] == mode[0] && item[1] == mode[1]) duplicate = true;
            if (!duplicate) result.add(new int[]{mode[0], mode[1]});
        }
        result.sort((left, right) -> Long.compare((long)left[0] * left[1], (long)right[0] * right[1]));
        return result;
    }
}
