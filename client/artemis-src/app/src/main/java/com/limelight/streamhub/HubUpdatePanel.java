package com.limelight.streamhub;

import android.app.Activity;
import android.content.res.ColorStateList;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.view.View;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.Switch;
import android.widget.TextView;
import java.util.function.BooleanSupplier;

/** Reuses the Hub palette and full-width 48dp targets in either orientation. */
public final class HubUpdatePanel extends LinearLayout {
    private final Activity activity;
    private final HubUpdates updates;
    private final BooleanSupplier idle;
    private final TextView version, state, notes, lastChecked;
    private final ProgressBar progress;
    private final Button check, download, install, cancel;
    private final Runnable listener = this::render;

    public HubUpdatePanel(Activity activity, HubUpdates updates, BooleanSupplier idle) {
        super(activity); this.activity = activity; this.updates = updates; this.idle = idle;
        setOrientation(VERTICAL); setClipToPadding(false);
        version = text(16, HubTheme.text(activity)); version.setTypeface(null, Typeface.BOLD);
        state = text(14, HubTheme.muted(activity)); state.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
        lastChecked = text(12, HubTheme.muted(activity)); notes = text(14, HubTheme.text(activity));
        toggle("自动检查更新（每 12 小时）", updates.autoCheck(), true);
        toggle("空闲时自动下载（仅非计费网络）", updates.autoDownload(), false);
        progress = new ProgressBar(activity, null, android.R.attr.progressBarStyleHorizontal);
        progress.setMax(100); progress.setProgressTintList(ColorStateList.valueOf(HubTheme.primary(activity))); progress.setContentDescription("更新下载进度");
        LayoutParams progressSize = new LayoutParams(-1, dp(6)); progressSize.setMargins(0, dp(8), 0, dp(8)); addView(progress, progressSize);
        check = button("检查更新", false, v -> updates.check(true, null));
        download = button("下载更新", true, v -> updates.download());
        install = button("安装更新", true, v -> HubStreamUi.hubDialog(activity).setTitle("安装更新？")
                .setMessage("安装将保留电脑绑定和设置。请先结束串流与文件操作，并完成系统安装确认。")
                .setNegativeButton("稍后", null).setPositiveButton("继续", (dialog, which) -> updates.install(activity, idle)).show());
        cancel = button("取消", false, v -> updates.cancel());
        TextView hint = text(12, HubTheme.muted(activity));
        hint.setText("自动检查和下载不会自动安装。系统安装确认由你操作；更新保留绑定和设置。应用被强制停止时，后台检查会暂停至再次打开。");
        render();
    }
    @Override protected void onAttachedToWindow() { super.onAttachedToWindow(); updates.listen(listener); render(); }
    @Override protected void onDetachedFromWindow() { updates.unlisten(listener); super.onDetachedFromWindow(); }
    private void render() {
        HubUpdatePolicy.Manifest available = updates.available();
        version.setText(updates.currentVersion() + (available == null ? "" : "\n最新 " + available.version + " / " + available.code));
        state.setText(updates.message()); lastChecked.setText(updates.lastChecked()); lastChecked.setVisibility(lastChecked.length() == 0 ? GONE : VISIBLE);
        notes.setText(available == null ? "" : available.notes); notes.setVisibility(notes.length() == 0 ? GONE : VISIBLE);
        boolean busy = updates.busy();
        progress.setVisibility(busy ? VISIBLE : GONE); progress.setIndeterminate(updates.progress() < 0); progress.setProgress(Math.max(0, updates.progress()));
        check.setEnabled(!busy && updates.configured());
        download.setVisibility(available != null && !updates.ready() ? VISIBLE : GONE); download.setEnabled(!busy);
        install.setVisibility(updates.ready() ? VISIBLE : GONE); install.setEnabled(!busy); cancel.setVisibility(busy ? VISIBLE : GONE);
    }
    private void toggle(String label, boolean checked, boolean autoCheck) {
        Switch control = new Switch(activity); control.setText(label); control.setTextSize(14); control.setTextColor(HubTheme.text(activity));
        control.setChecked(checked); control.setMinimumHeight(dp(56)); control.setPadding(0, dp(8), 0, dp(8));
        control.setThumbTintList(new ColorStateList(new int[][]{new int[]{android.R.attr.state_checked}, new int[]{}}, new int[]{HubTheme.accent(activity), HubTheme.muted(activity)}));
        control.setTrackTintList(new ColorStateList(new int[][]{new int[]{android.R.attr.state_checked}, new int[]{}}, new int[]{HubTheme.primary(activity), HubTheme.border(activity)}));
        addView(control, new LayoutParams(-1, -2));
        control.setOnCheckedChangeListener((view, value) -> updates.configure(autoCheck ? value : updates.autoCheck(), autoCheck ? updates.autoDownload() : value));
    }
    private TextView text(int size, int color) {
        TextView view = new TextView(activity); view.setTextSize(size); view.setTextColor(color); view.setLineSpacing(dp(3), 1);
        LayoutParams layout = new LayoutParams(-1, -2); layout.bottomMargin = dp(8); addView(view, layout); return view;
    }
    private Button button(String title, boolean primary, OnClickListener action) {
        Button button = new Button(activity); button.setText(title); button.setAllCaps(false); button.setTextSize(16);
        button.setTextColor(primary ? HubTheme.onAccent(activity) : HubTheme.accent(activity));
        GradientDrawable shape = new GradientDrawable(); shape.setColor(primary ? HubTheme.primary(activity) : HubTheme.selected(activity)); shape.setCornerRadius(dp(10));
        button.setBackground(new android.graphics.drawable.RippleDrawable(ColorStateList.valueOf(HubTheme.ripple(activity)), shape, null));
        button.setElevation(0); button.setStateListAnimator(null); button.setMinimumHeight(dp(48)); button.setPadding(dp(16), dp(8), dp(16), dp(8));
        LayoutParams layout = new LayoutParams(-1, -2); layout.topMargin = dp(8); addView(button, layout); button.setOnClickListener(action); return button;
    }
    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
}
