package com.limelight.streamhub;

import android.app.AlertDialog;
import android.content.Context;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.os.Build;
import android.view.Gravity;
import android.view.ContextThemeWrapper;
import android.view.View;
import android.view.ViewGroup;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import com.limelight.R;
import org.json.JSONArray;
import org.json.JSONObject;

/** One explicit dark palette for streaming controls and their secondary menus. */
public final class HubStreamUi {
    public static final int BACKGROUND = 0xFF111821, SURFACE = 0xFF1B2432, CARD = 0xFF263244, BORDER = 0xFF3C4B60;
    public static final int TEXT = 0xFFEEF3FA, MUTED = 0xFFB5C2D4, ACCENT = 0xFF9AC1FF;
    public static final int SELECTED = 0xFF304766, DANGER = 0xFFFFB4BC, SUCCESS = 0xFF77DCB2;
    public static final int WARNING = 0xFFFFCD70;
    public static final int PRIMARY = 0xFF2D61B4;

    private HubStreamUi() { }
    public static Context context(Context source) { return new ContextThemeWrapper(source, R.style.HubControlDialog); }
    public static Context hubContext(Context source) {
        return new ContextThemeWrapper(source, HubTheme.isDark(source)
                ? R.style.HubControlDialog : R.style.HubControlDialogLight);
    }
    public static AlertDialog.Builder dialog(Context source) {
        return builder(source, context(source), R.style.HubControlDialog);
    }
    public static AlertDialog.Builder hubDialog(Context source) {
        return builder(source, hubContext(source), HubTheme.isDark(source)
                ? R.style.HubControlDialog : R.style.HubControlDialogLight);
    }
    private static AlertDialog.Builder builder(Context source, Context themed, int theme) {
        return new AlertDialog.Builder(themed, theme) {
            @Override public AlertDialog show() {
                AlertDialog dialog = super.show();
                if (dialog.getWindow() != null) {
                    keepColors(dialog.getWindow().getDecorView());
                    android.util.DisplayMetrics metrics = source.getResources().getDisplayMetrics();
                    int width = Math.round(HubWindowLayout.of(source).widthDp * metrics.density);
                    dialog.getWindow().setLayout(Math.max(1, Math.min(Math.round(520 * metrics.density),
                            width - Math.round(32 * metrics.density))), -2);
                }
                return dialog;
            }
        };
    }
    public static void keepColors(View view) {
        if (Build.VERSION.SDK_INT >= 29) view.setForceDarkAllowed(false);
    }

    public static View diagnosticsView(Context source, String sessionSummary) {
        int text = HubTheme.text(source), muted = HubTheme.muted(source), field = HubTheme.field(source);
        int border = HubTheme.border(source), warning = HubTheme.warning(source);
        float density = source.getResources().getDisplayMetrics().density;
        ScrollView scroll = new ScrollView(source);
        scroll.setFillViewport(false);
        scroll.setClipToPadding(false);
        scroll.setPadding(dp(density, 8), 0, dp(density, 8), 0);
        int maxHeight = Math.min(dp(density, 520), Math.round(HubWindowLayout.of(source).heightDp * density * 0.56f));
        scroll.setLayoutParams(new ViewGroup.LayoutParams(-1, Math.max(dp(density, 160), maxHeight)));

        LinearLayout content = new LinearLayout(source);
        content.setOrientation(LinearLayout.VERTICAL);
        scroll.addView(content, new ScrollView.LayoutParams(-1, -2));
        addSectionTitle(source, content, "连接阶段", text, density);
        JSONArray timeline = HubSessionDiagnostics.timeline(source);
        if (timeline.length() == 0) {
            content.addView(text(source, "暂无阶段记录。完成一次连接后会显示各阶段耗时。", 14, muted), spaced(density, 0, 0, 8));
        } else {
            for (int i = 0; i < timeline.length(); i++) {
                JSONObject row = timeline.optJSONObject(i);
                if (row == null) continue;
                String kind = row.optString("kind");
                String value = "event".equals(kind)
                        ? "连接后 " + duration(row.optLong("eventMs", -1))
                        : duration(row.optLong("durationMs", -1));
                boolean slowest = row.optBoolean("slowest");
                String label = row.optString("label") + (slowest ? " · 最慢" : "");
                addTableRow(source, content, label, value, row.optInt("depth", 0),
                        slowest ? warning : text, field, slowest ? warning : border, density);
            }
        }

        addSectionTitle(source, content, "帧率与延迟", text, density);
        JSONObject performance = HubSessionDiagnostics.performanceSummary(source);
        if (!performance.optBoolean("available")) {
            content.addView(text(source, "暂无采样。完成一次串流后会显示实际帧率、面板刷新率与延迟。", 14, muted), spaced(density, 0, 0, 8));
        } else {
            JSONArray metrics = performance.optJSONArray("rows");
            if (metrics != null) for (int i = 0; i < metrics.length(); i++) {
                JSONObject metric = metrics.optJSONObject(i);
                if (metric != null) addTableRow(source, content, metric.optString("label"),
                        metric.optString("value"), 0, text, field, border, density);
            }
        }
        if (sessionSummary != null && !sessionSummary.trim().isEmpty()) {
            addSectionTitle(source, content, "当前串流", text, density);
            content.addView(text(source, sessionSummary, 14, muted), spaced(density, 0, 0, 8));
        }
        return scroll;
    }

    private static void addSectionTitle(Context context, LinearLayout parent, String label, int color, float density) {
        TextView title = text(context, label, 15, color);
        title.setTypeface(null, Typeface.BOLD);
        parent.addView(title, spaced(density, 0, 10, 4));
    }

    private static void addTableRow(Context context, LinearLayout parent, String label, String value,
            int depth, int valueColor, int fill, int stroke, float density) {
        LinearLayout row = new LinearLayout(context);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setMinimumHeight(dp(density, 42));
        row.setPadding(dp(density, 10), dp(density, 5), dp(density, 10), dp(density, 5));
        if (stroke != HubTheme.border(context)) {
            GradientDrawable emphasis = new GradientDrawable();
            emphasis.setColor(fill);
            emphasis.setCornerRadius(dp(density, 6));
            emphasis.setStroke(dp(density, 1), stroke);
            row.setBackground(emphasis);
        }
        TextView name = text(context, label, 13, HubTheme.muted(context));
        name.setGravity(Gravity.CENTER_VERTICAL);
        name.setPadding(dp(density, depth * 12), 0, dp(density, 8), 0);
        row.addView(name, new LinearLayout.LayoutParams(0, -2, 1f));
        TextView metric = text(context, value, 13, valueColor);
        metric.setGravity(Gravity.CENTER_VERTICAL | Gravity.END);
        metric.setTextAlignment(View.TEXT_ALIGNMENT_VIEW_END);
        row.addView(metric, new LinearLayout.LayoutParams(0, -2, 1.2f));
        parent.addView(row, spaced(density, 0, 0, 2));
    }

    private static TextView text(Context context, String value, int size, int color) {
        TextView view = new TextView(context);
        view.setText(value);
        view.setTextSize(size);
        view.setTextColor(color);
        view.setLineSpacing(Math.round(context.getResources().getDisplayMetrics().density * 2), 1f);
        return view;
    }

    private static LinearLayout.LayoutParams spaced(float density, int width, int top, int bottom) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(width == 0 ? -1 : width, -2);
        params.topMargin = dp(density, top);
        params.bottomMargin = dp(density, bottom);
        return params;
    }

    private static int dp(float density, int value) { return Math.round(density * value); }

    private static String duration(long millis) {
        if (millis < 0) return "暂无";
        if (millis < 1000) return millis + " ms";
        return String.format(java.util.Locale.ROOT, "%.1f 秒", millis / 1000d);
    }
}
