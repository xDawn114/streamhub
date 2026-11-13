package com.limelight.streamhub;

import android.app.Activity;
import android.graphics.Color;
import android.graphics.drawable.ColorDrawable;
import android.graphics.drawable.Drawable;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.LayerDrawable;
import android.graphics.drawable.StateListDrawable;
import android.view.Gravity;
import android.view.View;
import android.widget.*;
import com.limelight.R;
import java.util.ArrayList;
import java.util.List;

/** Shared by the hub and independent file browser, including landscape navigation. */
final class HubNavigation {
    // Keep the outgoing surface until Android has drawn the next destination.
    // A zero-animation close can discard it one frame early on some devices.
    static void prepareWindow(Activity activity) {
        if (android.os.Build.VERSION.SDK_INT >= 34) {
            activity.overrideActivityTransition(Activity.OVERRIDE_TRANSITION_OPEN,
                    R.anim.hub_navigation_hold, R.anim.hub_navigation_hold);
            activity.overrideActivityTransition(Activity.OVERRIDE_TRANSITION_CLOSE,
                    R.anim.hub_navigation_hold, R.anim.hub_navigation_hold);
        }
    }
    static android.os.Bundle transition(Activity activity) {
        return android.app.ActivityOptions.makeCustomAnimation(activity,
                R.anim.hub_navigation_hold, R.anim.hub_navigation_hold).toBundle();
    }
    static void holdSurface(Activity activity) {
        activity.overridePendingTransition(R.anim.hub_navigation_hold, R.anim.hub_navigation_hold);
    }
    interface Listener { void select(int page); }
    private final Activity activity;
    private final List<LinearLayout> items = new ArrayList<>();
    final LinearLayout view;
    HubNavigation(Activity activity, View content, int selected, Listener listener) {
        this.activity = activity;
        HubWindowLayout layout = HubWindowLayout.of(activity);
        boolean rail = layout.railWidthDp() > 0;
        view = new LinearLayout(activity); view.setOrientation(rail ? LinearLayout.HORIZONTAL : LinearLayout.VERTICAL);
        LinearLayout navigation = new LinearLayout(activity);
        navigation.setOrientation(rail ? LinearLayout.VERTICAL : LinearLayout.HORIZONTAL);
        navigation.setGravity(rail ? Gravity.TOP : Gravity.CENTER);
        navigation.setPadding(dp(8), dp(8), dp(8), dp(8)); navigation.setBackgroundColor(HubTheme.surface(activity));
        if (rail) {
            if (!layout.compact()) {
                TextView brand = new TextView(activity); brand.setText("我控制"); brand.setTextColor(HubTheme.text(activity));
                brand.setTextSize(18); brand.setGravity(Gravity.CENTER); brand.setTypeface(null, android.graphics.Typeface.BOLD);
                navigation.addView(brand, new LinearLayout.LayoutParams(-1, dp(56)));
            }
            ScrollView railScroll = new ScrollView(activity);
            railScroll.setFillViewport(true); railScroll.setVerticalScrollBarEnabled(false);
            railScroll.setBackgroundColor(HubTheme.surface(activity)); railScroll.addView(navigation);
            view.addView(railScroll, new LinearLayout.LayoutParams(dp(layout.railWidthDp()), -1));
            view.addView(content, new LinearLayout.LayoutParams(0, -1, 1));
        } else {
            view.addView(content, new LinearLayout.LayoutParams(-1, 0, 1));
            view.addView(navigation, new LinearLayout.LayoutParams(-1, dp(layout.bottomHeightDp())));
        }
        String[] labels = {"设备", "连接", "设置", "文件"};
        int[] icons = {R.drawable.ic_nav_devices, R.drawable.ic_nav_connection, R.drawable.ic_nav_settings, R.drawable.ic_nav_files};
        for (int i = 0; i < labels.length; i++) {
            final int page = i;
            LinearLayout item = new LinearLayout(activity); item.setOrientation(LinearLayout.VERTICAL); item.setGravity(Gravity.CENTER);
            item.setMinimumHeight(dp(rail ? 64 : layout.bottomHeightDp() - 16)); item.setPadding(dp(2), dp(2), dp(2), dp(2));
            ImageView icon = new ImageView(activity); icon.setImageResource(icons[i]); icon.setScaleType(ImageView.ScaleType.CENTER);
            // Preserve a complete 24dp outline inside the existing selection capsule.
            icon.setPadding(dp(12),dp(4),dp(12),dp(4));
            icon.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
            icon.setDuplicateParentStateEnabled(true);
            item.addView(icon, new LinearLayout.LayoutParams(dp(48), dp(32)));
            TextView label = new TextView(activity); label.setText(labels[i]); label.setTextSize(12); label.setGravity(Gravity.CENTER);
            label.setIncludeFontPadding(false); label.setTypeface(null, android.graphics.Typeface.BOLD);
            label.setMinHeight(dp(20));
            item.addView(label, new LinearLayout.LayoutParams(-1, -2));
            item.setContentDescription(labels[i]); item.setFocusable(true); item.setClickable(true);
            item.setStateListAnimator(null); item.setElevation(0);
            item.setDefaultFocusHighlightEnabled(false);
            item.setOnClickListener(v -> listener.select(page));
            navigation.addView(item, rail ? new LinearLayout.LayoutParams(-1, -2) : new LinearLayout.LayoutParams(0, -1, 1));
            items.add(item);
        }
        select(selected);
    }
    void select(int page) {
        for (int i = 0; i < items.size(); i++) {
            LinearLayout item = items.get(i); boolean selected = i == page;
            ImageView icon = (ImageView)item.getChildAt(0); TextView label = (TextView)item.getChildAt(1);
            int color = selected ? HubTheme.accent(activity) : HubTheme.muted(activity);
            label.setTextColor(color); icon.setImageTintList(android.content.res.ColorStateList.valueOf(color));
            GradientDrawable capsule = new GradientDrawable(); capsule.setColor(selected ? HubTheme.selected(activity) : android.graphics.Color.TRANSPARENT); capsule.setCornerRadius(dp(16));
            // Draw feedback in the icon's bounds so it stays aligned with the
            // selection capsule in both bottom navigation and the side rail.
            GradientDrawable pressed = new GradientDrawable();
            pressed.setColor(HubTheme.ripple(activity)); pressed.setCornerRadius(dp(16));
            StateListDrawable states = new StateListDrawable();
            states.addState(new int[]{android.R.attr.state_pressed}, pressed);
            states.addState(new int[]{android.R.attr.state_focused}, pressed);
            states.addState(new int[]{android.R.attr.state_hovered}, pressed);
            states.addState(new int[]{}, new ColorDrawable(Color.TRANSPARENT));
            icon.setBackground(new LayerDrawable(new Drawable[]{capsule, states})); item.setSelected(selected);
        }
    }
    private int dp(int value) { return Math.round(value * activity.getResources().getDisplayMetrics().density); }
}
