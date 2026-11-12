package com.limelight.streamhub;

import android.app.Activity;
import android.widget.Toast;

/** Changing the next-session preset never leaves the active video activity. */
public final class HubQualitySettings {
    private HubQualitySettings() { }
    public static void show(Activity activity, boolean streaming, Runnable advanced) {
        HubStore store = new HubStore(activity);
        String[] ids = {"smooth", "balanced", "quality", "advanced"};
        String current = store.get("qualityPreset", "advanced"); int selected = 3;
        for (int i = 0; i < ids.length; i++) if (ids[i].equals(current)) selected = i;
        // AlertDialog gives a message priority over its single-choice list.
        // Keep the choices visible; the host negotiates supported modes.
        HubStreamUi.dialog(activity).setTitle("画质 · 下次连接生效")
                .setSingleChoiceItems(new String[]{"流畅 · 长边 1280 / 请求 30 FPS / 8 Mbps", "均衡 · 长边 1920 / 请求 60 FPS / 18 Mbps", "画质 · 长边 2560 / 系统调度，最高 120Hz（请求 90） / 32 Mbps", "使用高级自定义设置"}, selected,
                        (dialog, which) -> { store.put("qualityPreset", ids[which]); Toast.makeText(activity, "已保存，下次连接生效", Toast.LENGTH_SHORT).show(); dialog.dismiss(); })
                .setNeutralButton("高级", (dialog, which) -> {
                    if (streaming) Toast.makeText(activity, "请结束串流后，在设置页打开高级设置", Toast.LENGTH_LONG).show();
                    else if (advanced != null) { store.put("qualityPreset", "advanced"); advanced.run(); }
                }).setNegativeButton("关闭", null).show();
    }
}
