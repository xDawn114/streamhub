package com.limelight.streamhub;

import android.app.Activity;
import android.content.res.ColorStateList;
import android.graphics.Typeface;
import android.view.View;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.Switch;
import android.widget.TextView;
import java.util.function.BooleanSupplier;

/** Reuses the Hub palette and 48dp action targets in either orientation. */
public final class HubUpdatePanel extends LinearLayout {
    private final Activity activity;
    private final HubUpdates updates;
    private final BooleanSupplier idle;
    private final TextView version, state, notes;
    private final ProgressBar progress;
    private final Button check, download, install, cancel;
    private final Runnable listener = this::render;

    public HubUpdatePanel(Activity activity, HubUpdates updates, BooleanSupplier idle) {
        super(activity); this.activity = activity; this.updates = updates; this.idle = idle;
        setOrientation(VERTICAL); setClipToPadding(false);
        version = text(16, HubTheme.text(activity)); version.setTypeface(null, Typeface.BOLD);
        notes = text(14, HubTheme.text(activity));
        toggle("自动检查更新", updates.autoCheck(), true);
        toggle("空闲时自动下载", updates.autoDownload(), false);
        progress = new ProgressBar(activity, null, android.R.attr.progressBarStyleHorizontal);
        progress.setMax(100); progress.setProgressTintList(ColorStateList.valueOf(HubTheme.primary(activity))); progress.setContentDescription("更新下载进度");
        LayoutParams progressSize = new LayoutParams(-1, dp(6)); progressSize.setMargins(0, dp(8), 0, dp(8)); addView(progress, progressSize);
        check = button("检查更新", false, v -> updates.check(true, null));
        HubButtons.settingAction(check);
        check.setLayoutParams(new LayoutParams(-1, -2));
        download = button("下载更新", true, v -> updates.download());
        install = button("安装更新", true, v -> HubStreamUi.hubDialog(activity).setTitle("安装更新？")
                .setMessage("安装将保留电脑绑定和设置。请先结束串流与文件操作，并完成系统安装确认。")
                .setNegativeButton("稍后", null).setPositiveButton("继续", (dialog, which) -> updates.install(activity, idle)).show());
        cancel = button("取消", false, v -> updates.cancel());
        state = text(14, HubTheme.muted(activity));
        state.setPadding(0, dp(8), 0, 0);
        state.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
        check.setTooltipText("每12小时检查一次；强制停止应用后暂停，重新打开后恢复。更新保留绑定和设置，安装仍需系统确认。");
        render();
    }
    @Override protected void onAttachedToWindow() { super.onAttachedToWindow(); updates.listen(listener); render(); }
    @Override protected void onDetachedFromWindow() { updates.unlisten(listener); super.onDetachedFromWindow(); }
    private void render() {
        HubUpdatePolicy.Manifest available = updates.available();
        version.setText(updates.currentVersion() + (available == null ? "" : "\n最新 " + available.version + " / " + available.code));
        state.setText(updates.message());
        state.setVisibility(state.length() == 0 ? GONE : VISIBLE);
        notes.setText(available == null ? "" : available.notes); notes.setVisibility(notes.length() == 0 ? GONE : VISIBLE);
        boolean busy = updates.busy();
        progress.setVisibility(busy ? VISIBLE : GONE); progress.setIndeterminate(updates.progress() < 0); progress.setProgress(Math.max(0, updates.progress()));
        check.setEnabled(!busy && updates.configured());
        download.setVisibility(available != null && !updates.ready() ? VISIBLE : GONE); download.setEnabled(!busy);
        install.setVisibility(updates.ready() ? VISIBLE : GONE); install.setEnabled(!busy); cancel.setVisibility(busy ? VISIBLE : GONE);
    }
    private void toggle(String label, boolean checked, boolean autoCheck) {
        Switch control = new HubSwitch(activity); control.setText(label); control.setTextSize(14); control.setTextColor(HubTheme.text(activity));
        control.setTooltipText(autoCheck ? "每12小时检查一次" : "仅非计费网络；不会自动安装");
        control.setChecked(checked); control.setMinimumHeight(dp(56)); control.setPadding(0, dp(8), 0, dp(8));
        addView(control, new LayoutParams(-1, -2));
        control.setOnCheckedChangeListener((view, value) -> updates.configure(autoCheck ? value : updates.autoCheck(), autoCheck ? updates.autoDownload() : value));
    }
    private TextView text(int size, int color) {
        TextView view = new TextView(activity); view.setTextSize(size); view.setTextColor(color); view.setLineSpacing(dp(3), 1);
        LayoutParams layout = new LayoutParams(-1, -2); layout.bottomMargin = dp(8); addView(view, layout); return view;
    }
    private Button button(String title, boolean primary, OnClickListener action) {
        Button button = new Button(activity); button.setText(title); HubButtons.apply(button, primary);
        LayoutParams layout = new LayoutParams(-2, -2); layout.topMargin = dp(8); addView(button, layout); button.setOnClickListener(action); return button;
    }
    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
}
