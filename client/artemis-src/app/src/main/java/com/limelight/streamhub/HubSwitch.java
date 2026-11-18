package com.limelight.streamhub;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.drawable.GradientDrawable;
import android.view.Gravity;
import android.view.View;
import android.widget.Switch;

/** Native switch interaction/accessibility, with one bounded drawing for track and thumb. */
final class HubSwitch extends Switch {
    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);

    private final boolean streaming;
    HubSwitch(Context context) { this(context,false); }
    HubSwitch(Context context,boolean streaming) {
        super(context);this.streaming=streaming;
        // Reserve space through the native layout, but avoid ROM-specific thumb insets/clipping.
        GradientDrawable track = new GradientDrawable();
        track.setColor(Color.TRANSPARENT); track.setSize(dp(52), dp(28));
        GradientDrawable thumb = new GradientDrawable();
        thumb.setColor(Color.TRANSPARENT); thumb.setSize(dp(24), dp(24));
        setTrackDrawable(track); setThumbDrawable(thumb);
        setThumbTintList(null); setTrackTintList(null);
        setShowText(false); setSplitTrack(false); setSwitchMinWidth(dp(52));
        setSwitchPadding(dp(12)); setGravity(Gravity.CENTER_VERTICAL);
    }

    @Override protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        boolean rtl = getLayoutDirection() == View.LAYOUT_DIRECTION_RTL;
        float left = rtl ? getPaddingLeft() : getWidth() - getPaddingRight() - dp(52);
        float top = getPaddingTop() + (getHeight() - getPaddingTop() - getPaddingBottom() - dp(28)) / 2f;
        boolean dark=streaming||HubTheme.isDark(getContext());
        int on=dark?0xFF4B7BEC:HubTheme.primary(getContext());
        int off=dark?0xFF6F7D91:HubTheme.muted(getContext());
        paint.setColor(isEnabled() ? isChecked()?on:off : streaming?HubStreamUi.BORDER:HubTheme.border(getContext()));
        canvas.drawRoundRect(left, top, left + dp(52), top + dp(28), dp(14), dp(14), paint);
        paint.setColor(Color.WHITE);
        float center = left + dp(isChecked() != rtl ? 38 : 14);
        canvas.drawCircle(center, top + dp(14), dp(10), paint);
    }

    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
}
