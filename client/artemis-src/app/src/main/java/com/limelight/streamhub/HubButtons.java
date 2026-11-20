package com.limelight.streamhub;

import android.content.Context;
import android.content.res.ColorStateList;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.Drawable;
import android.graphics.drawable.LayerDrawable;
import android.graphics.drawable.RippleDrawable;
import android.graphics.drawable.StateListDrawable;
import android.view.Gravity;
import android.widget.Button;

/** Shared button surfaces and input states. */
final class HubButtons {
    private HubButtons() { }

    static void apply(Button button, boolean primary) {
        Context context = button.getContext();
        base(button);
        button.setTextColor(colors(context, primary ? HubTheme.onAccent(context) : HubTheme.text(context)));
        button.setBackgroundTintList(null);
        button.setBackground(background(context, primary, false));
    }

    static void settingAction(Button button) {
        Context context = button.getContext();
        base(button);
        button.setGravity(Gravity.START | Gravity.CENTER_VERTICAL);
        button.setTextSize(15);
        button.setTextColor(colors(context, HubTheme.accent(context)));
        button.setBackgroundTintList(null);
        button.setBackground(background(context, false, true));
        button.setPadding(0, dp(context, 8), 0, dp(context, 8));
        button.setMinHeight(dp(context, 56));
    }

    static void danger(Button button) {
        apply(button, false);
        button.setTextColor(colors(button.getContext(), HubTheme.danger(button.getContext())));
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

    private static Drawable background(Context context, boolean primary, boolean textOnly) {
        int normal = textOnly ? Color.TRANSPARENT : primary ? HubTheme.primary(context) : HubTheme.field(context);
        int hover = primary ? HubTheme.primaryHover(context) : HubTheme.hover(context);
        int pressed = primary ? HubTheme.primaryPressed(context) : HubTheme.pressed(context);
        int border = textOnly ? Color.TRANSPARENT : primary ? HubTheme.primary(context) : HubTheme.controlBorder(context);
        StateListDrawable states = new StateListDrawable();
        states.addState(new int[]{android.R.attr.state_focused, android.R.attr.state_pressed}, face(context, pressed, border, true));
        states.addState(new int[]{android.R.attr.state_focused}, face(context, normal, border, true));
        states.addState(new int[]{android.R.attr.state_pressed}, face(context, pressed, border, false));
        states.addState(new int[]{android.R.attr.state_selected}, face(context,
                primary ? HubTheme.primaryPressed(context) : HubTheme.selected(context), HubTheme.accent(context), false));
        states.addState(new int[]{android.R.attr.state_hovered}, face(context, hover, border, false));
        states.addState(new int[]{}, face(context, normal, border, false));
        StateListDrawable enabled = new StateListDrawable();
        enabled.addState(new int[]{-android.R.attr.state_enabled}, face(context,
                textOnly ? Color.TRANSPARENT : HubTheme.disabled(context), border, false));
        enabled.addState(new int[]{}, new RippleDrawable(ColorStateList.valueOf(HubTheme.ripple(context)), states, null));
        return enabled;
    }

    private static Drawable face(Context context, int fill, int border, boolean focused) {
        GradientDrawable surface = new GradientDrawable();
        surface.setColor(fill);
        surface.setCornerRadius(dp(context, 8));
        surface.setStroke(dp(context, 1), border);
        if (!focused) return surface;
        GradientDrawable ring = new GradientDrawable();
        ring.setColor(Color.TRANSPARENT);
        ring.setCornerRadius(dp(context, 8));
        ring.setStroke(dp(context, 2), HubTheme.focus(context));
        LayerDrawable layers = new LayerDrawable(new Drawable[]{surface, ring});
        layers.setLayerInset(0, dp(context, 3), dp(context, 3), dp(context, 3), dp(context, 3));
        return layers;
    }

    private static ColorStateList colors(Context context, int enabled) {
        return new ColorStateList(new int[][]{new int[]{-android.R.attr.state_enabled}, new int[]{}},
                new int[]{HubTheme.muted(context), enabled});
    }

    private static int dp(Context context, int value) {
        return Math.round(value * context.getResources().getDisplayMetrics().density);
    }
}
