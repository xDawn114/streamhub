package com.limelight.streamhub;

import android.content.Context;
import android.content.res.ColorStateList;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.view.Gravity;
import android.widget.Button;

/** Shared filled/neutral actions; setting actions and dialog actions stay text-only. */
final class HubButtons {
    private HubButtons() { }

    static void apply(Button button, boolean primary) {
        Context context = button.getContext();
        base(button);
        button.setTextColor(colors(context, primary ? HubTheme.onAccent(context) : HubTheme.text(context)));
        GradientDrawable surface = new GradientDrawable();
        surface.setColor(primary ? HubTheme.primary(context) : HubTheme.field(context));
        surface.setCornerRadius(dp(context, 12));
        surface.setStroke(dp(context, 1), primary ? HubTheme.primary(context) : HubTheme.border(context));
        button.setBackground(new RippleDrawable(ColorStateList.valueOf(HubTheme.ripple(context)), surface, null));
    }

    static void settingAction(Button button) {
        Context context = button.getContext();
        base(button);
        button.setGravity(Gravity.START | Gravity.CENTER_VERTICAL);
        button.setTextSize(15);
        button.setTextColor(colors(context, HubTheme.accent(context)));
        GradientDrawable surface = new GradientDrawable();
        surface.setColor(Color.TRANSPARENT);
        button.setBackground(new RippleDrawable(ColorStateList.valueOf(HubTheme.ripple(context)), surface, null));
        button.setPadding(0, dp(context, 8), 0, dp(context, 8));
        button.setMinHeight(dp(context, 56));
    }

    private static void base(Button button) {
        Context context = button.getContext();
        button.setAllCaps(false);
        button.setTextSize(16);
        button.setTypeface(null, Typeface.NORMAL);
        button.setElevation(0);
        button.setStateListAnimator(null);
        button.setMinHeight(dp(context, 48));
        button.setPadding(dp(context, 16), dp(context, 8), dp(context, 16), dp(context, 8));
    }

    private static ColorStateList colors(Context context, int enabled) {
        return new ColorStateList(new int[][]{new int[]{-android.R.attr.state_enabled}, new int[]{}},
                new int[]{HubTheme.muted(context), enabled});
    }

    private static int dp(Context context, int value) {
        return Math.round(value * context.getResources().getDisplayMetrics().density);
    }
}
