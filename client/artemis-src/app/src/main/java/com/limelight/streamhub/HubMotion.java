package com.limelight.streamhub;

import android.content.Context;
import android.provider.Settings;
import android.view.View;
import android.view.animation.Interpolator;
import android.view.animation.PathInterpolator;

/** Shared motion tokens for hub surfaces; the streamed video surface is excluded. */
final class HubMotion {
    static final long FAST_MS = 120L;
    static final long STANDARD_MS = 150L;
    static final long PAGE_MS = 240L;
    static final long PANEL_MS = 260L;

    private HubMotion() { }

    static boolean reduced(Context context) {
        try {
            float animator = Settings.Global.getFloat(context.getContentResolver(),
                    Settings.Global.ANIMATOR_DURATION_SCALE, 1f);
            float transition = Settings.Global.getFloat(context.getContentResolver(),
                    Settings.Global.TRANSITION_ANIMATION_SCALE, 1f);
            return animator <= 0f || transition <= 0f;
        } catch (RuntimeException ignored) {
            return false;
        }
    }

    static Interpolator curve() {
        return new PathInterpolator(0.2f, 0f, 0f, 1f);
    }

    static int dp(Context context, int value) {
        return Math.round(value * context.getResources().getDisplayMetrics().density);
    }

    static void pageIn(Context context, View view, boolean subpage, boolean cardOrigin) {
        view.animate().cancel();
        view.setVisibility(View.VISIBLE);
        view.setPivotX(view.getWidth() > 0 ? view.getWidth() / 2f : 0f);
        view.setPivotY(view.getHeight() > 0 ? view.getHeight() / 2f : 0f);
        if (reduced(context)) {
            view.setAlpha(1f);
            view.setTranslationX(0f);
            view.setTranslationY(0f);
            view.setScaleX(1f);
            view.setScaleY(1f);
            return;
        }
        view.setAlpha(0f);
        view.setTranslationX(subpage ? dp(context, 28) : 0f);
        view.setTranslationY(subpage ? 0f : dp(context, 8));
        view.setScaleX(cardOrigin ? 0.98f : 1f);
        view.setScaleY(cardOrigin ? 0.98f : 1f);
        view.animate().alpha(1f).translationX(0f).translationY(0f)
                .scaleX(1f).scaleY(1f)
                .setDuration(cardOrigin ? 180L : subpage ? PAGE_MS : STANDARD_MS)
                .setInterpolator(curve()).start();
    }

    static void showPanel(Context context, View view) {
        view.animate().cancel();
        view.setVisibility(View.VISIBLE);
        if (reduced(context)) {
            view.setAlpha(1f);
            view.setTranslationY(0f);
            return;
        }
        view.setAlpha(0f);
        view.setTranslationY(dp(context, 16));
        view.animate().alpha(1f).translationY(0f).setDuration(PANEL_MS)
                .setInterpolator(curve()).start();
    }

    static void hidePanel(Context context, View view, Runnable after) {
        view.animate().cancel();
        if (view.getVisibility() != View.VISIBLE) {
            if (after != null) after.run();
            return;
        }
        if (reduced(context)) {
            view.setVisibility(View.GONE);
            view.setAlpha(1f);
            view.setTranslationY(0f);
            if (after != null) after.run();
            return;
        }
        view.animate().alpha(0f).translationY(dp(context, 12)).setDuration(PANEL_MS)
                .setInterpolator(curve()).withEndAction(() -> {
                    view.setVisibility(View.GONE);
                    view.setAlpha(1f);
                    view.setTranslationY(0f);
                    if (after != null) after.run();
                }).start();
    }

    static void hideImmediately(View view) {
        view.animate().cancel();
        view.setVisibility(View.GONE);
        view.setAlpha(1f);
        view.setTranslationY(0f);
    }

    static void popupIn(Context context, View view) {
        view.animate().cancel();
        view.setPivotX(view.getWidth() > 0 ? view.getWidth() / 2f : 0f);
        view.setPivotY(0f);
        if (reduced(context)) {
            view.setAlpha(1f);
            view.setScaleX(1f);
            view.setScaleY(1f);
            return;
        }
        view.setAlpha(0f);
        view.setScaleX(0.96f);
        view.setScaleY(0.96f);
        view.animate().alpha(1f).scaleX(1f).scaleY(1f).setDuration(STANDARD_MS)
                .setInterpolator(curve()).start();
    }
}
