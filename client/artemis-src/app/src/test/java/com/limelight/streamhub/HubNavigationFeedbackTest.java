package com.limelight.streamhub;

import android.app.AlertDialog;
import android.content.Context;
import android.graphics.Typeface;
import android.os.Bundle;
import android.os.Looper;
import android.util.TypedValue;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ImageButton;
import android.widget.ScrollView;
import android.widget.TextView;
import org.junit.Before;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.Robolectric;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.annotation.Config;
import org.robolectric.annotation.GraphicsMode;
import org.robolectric.shadows.ShadowAlertDialog;
import static org.junit.Assert.*;
import static org.robolectric.Shadows.shadowOf;

/** Create-only activities: no network, installed state or native VPN is started. */
@RunWith(RobolectricTestRunner.class)
@Config(sdk=34, application=android.app.Application.class, qualifiers="w800dp-h1200dp-port-mdpi")
@GraphicsMode(GraphicsMode.Mode.NATIVE)
public class HubNavigationFeedbackTest {
    @Before public void reset() {
        Context context = RuntimeEnvironment.getApplication();
        context.getSharedPreferences("streamhub-device", Context.MODE_PRIVATE).edit().clear().commit();
        HubTheme.setMode(context, HubTheme.LIGHT);
    }
    private Object invoke(HubActivity activity, String name, Class<?>[] types, Object... args) throws Exception {
        java.lang.reflect.Method method = HubActivity.class.getDeclaredMethod(name, types); method.setAccessible(true);
        return method.invoke(activity, args);
    }
    private void page(HubActivity activity, int page) throws Exception { invoke(activity, "showPage", new Class<?>[]{int.class}, page); }
    private int page(HubActivity activity) throws Exception {
        java.lang.reflect.Field field = HubActivity.class.getDeclaredField("currentPage"); field.setAccessible(true); return field.getInt(activity);
    }
    private View root(HubActivity activity) { return ((ViewGroup)activity.findViewById(android.R.id.content)).getChildAt(0); }
    private TextView text(View root, String value) {
        if (root.getVisibility() != View.VISIBLE) return null;
        if (root instanceof TextView && value.contentEquals(((TextView)root).getText())) return (TextView)root;
        if (root instanceof ViewGroup) for (int i=0; i<((ViewGroup)root).getChildCount(); i++) {
            TextView found = text(((ViewGroup)root).getChildAt(i), value); if (found != null) return found;
        }
        return null;
    }
    private ImageButton back(View root) {
        if (root.getVisibility() != View.VISIBLE) return null;
        if (root instanceof ImageButton && root.getContentDescription() != null && root.getContentDescription().toString().startsWith("返回")) return (ImageButton)root;
        if (root instanceof ViewGroup) for (int i=0; i<((ViewGroup)root).getChildCount(); i++) {
            ImageButton found = back(((ViewGroup)root).getChildAt(i)); if (found != null) return found;
        }
        return null;
    }
    private void snapshot(View view, String name, int width, int height) throws Exception {
        String path = System.getenv("STREAMHUB_RENDER_ROOT"); if (path == null || path.isEmpty()) return;
        for (int i=0; i<3; i++) {
            view.measure(View.MeasureSpec.makeMeasureSpec(width, View.MeasureSpec.EXACTLY), View.MeasureSpec.makeMeasureSpec(height, View.MeasureSpec.EXACTLY));
            view.layout(0, 0, width, height);
        }
        android.graphics.Bitmap bitmap = android.graphics.Bitmap.createBitmap(width, height, android.graphics.Bitmap.Config.ARGB_8888);
        view.draw(new android.graphics.Canvas(bitmap));
        java.io.File directory = new java.io.File(path); assertTrue(directory.isDirectory() || directory.mkdirs());
        try (var output = new java.io.FileOutputStream(new java.io.File(directory, name))) { assertTrue(bitmap.compress(android.graphics.Bitmap.CompressFormat.PNG, 100, output)); }
        finally { bitmap.recycle(); }
    }
    @Test public void systemBackAndHeaderReturnToDevicesWithoutFinishingApp() throws Exception {
        try (var controller = Robolectric.buildActivity(HubActivity.class).create()) {
            HubActivity activity = controller.get(); page(activity, 1);
            assertNotNull(text(root(activity), "添加电脑")); assertNull(text(root(activity), "账号与连接"));
            snapshot(root(activity), "tablet-add-computer.png", 800, 1200);
            activity.onBackPressed(); assertEquals(0, page(activity)); assertFalse(activity.isFinishing());
            page(activity, 1); ImageButton target = back(root(activity)); assertNotNull(target);
            assertTrue(target.getLayoutParams().width >= 48 && target.getLayoutParams().height >= 48);
            target.performClick(); assertEquals(0, page(activity)); assertFalse(activity.isFinishing());
        }
    }
    @Test public void accountHasItsOwnDestinationAndReturnsToItsSettingsParent() throws Exception {
        try (var controller = Robolectric.buildActivity(HubActivity.class).create()) {
            HubActivity activity = controller.get(); page(activity, 2);
            invoke(activity, "openAccount", new Class<?>[]{});
            assertEquals(4, page(activity)); assertNotNull(text(root(activity), "账号与连接"));
            assertNull(text(root(activity), "附近电脑")); assertNotNull(text(root(activity), "连接通道"));
            snapshot(root(activity), "tablet-account-connection.png", 800, 1200);
            activity.onBackPressed(); assertEquals(2, page(activity)); assertFalse(activity.isFinishing());
        }
    }
    @Test public void recreatedSubpageKeepsItsParentAndSystemBackBehavior() throws Exception {
        Bundle saved = new Bundle();
        try (var controller = Robolectric.buildActivity(HubActivity.class).create()) {
            page(controller.get(), 2); invoke(controller.get(), "openAccount", new Class<?>[]{});
            invoke(controller.get(), "onSaveInstanceState", new Class<?>[]{Bundle.class}, saved);
        }
        try (var controller = Robolectric.buildActivity(HubActivity.class).create(saved)) {
            assertEquals(4, page(controller.get())); controller.get().onBackPressed(); assertEquals(2, page(controller.get()));
        }
    }
    @Test public void checkAndLoginFeedbackIsVisibleOnThePageWhereItWasRequested() throws Exception {
        try (var controller = Robolectric.buildActivity(HubActivity.class).create()) {
            HubActivity activity = controller.get(); page(activity, 1);
            invoke(activity, "checkComputer", new Class<?>[]{}); shadowOf(Looper.getMainLooper()).idle();
            assertNotNull(text(root(activity), "请先添加电脑。"));
            assertNull(text(root(activity), "电脑地址"));
            text(root(activity), "使用完整绑定码").performClick(); assertNotNull(text(root(activity), "电脑地址"));
            invoke(activity, "refreshBindingSummary", new Class<?>[]{}); assertNotNull(text(root(activity), "电脑地址"));
            invoke(activity, "openAccount", new Class<?>[]{});
            invoke(activity, "showProgress", new Class<?>[]{String.class}, "正在获取账号授权地址…"); shadowOf(Looper.getMainLooper()).idle();
            assertNotNull(text(root(activity), "正在获取账号授权地址…"));
            invoke(activity, "showProgress", new Class<?>[]{String.class}, "授权请求超时，请重试。"); shadowOf(Looper.getMainLooper()).idle();
            assertNotNull(text(root(activity), "授权请求超时，请重试。"));
        }
    }
    @Test public void transientAccountErrorKeepsTheLoginRetryAction() throws Exception {
        try (var controller = Robolectric.buildActivity(HubActivity.class).create()) {
            HubActivity activity = controller.get(); invoke(activity, "openAccount", new Class<?>[]{});
            invoke(activity, "renderNetwork", new Class<?>[]{com.limelight.streamhub.networking.EmbeddedNetwork.Status.class, String.class}, null, "网络暂不可用");
            assertNotNull(text(root(activity), "网络暂不可用")); assertNotNull(text(root(activity), "登录连接账号"));
        }
    }
    private int background(Context context) {
        TypedValue value = new TypedValue(); assertTrue(context.getTheme().resolveAttribute(android.R.attr.colorBackground, value, true)); return value.data;
    }
    @Test public void settingsQualityUsesTheAppThemeWhileStreamingKeepsDarkControls() throws Exception {
        for (int theme : new int[]{HubTheme.LIGHT, HubTheme.DARK}) {
            HubTheme.setMode(RuntimeEnvironment.getApplication(), theme);
            try (var controller = Robolectric.buildActivity(HubActivity.class).create()) {
                HubActivity activity = controller.get(); HubQualitySettings.show(activity, false, null);
                AlertDialog dialog = ShadowAlertDialog.getLatestAlertDialog();
                assertEquals(background(HubStreamUi.hubContext(activity)), background(dialog.getContext()));
                snapshot(dialog.getWindow().getDecorView(), "quality-settings-" + (theme == HubTheme.LIGHT ? "light" : "dark") + ".png", 520, 560); dialog.dismiss();
                HubQualitySettings.show(activity, true, null); dialog = ShadowAlertDialog.getLatestAlertDialog();
                assertEquals(background(HubStreamUi.context(activity)), background(dialog.getContext())); dialog.dismiss();
            }
        }
    }
    @Test public void manualSeparatesSectionHeadingsFromBodyInAScrollableView() throws Exception {
        try (var controller = Robolectric.buildActivity(HubActivity.class).create()) {
            HubActivity activity = controller.get(); invoke(activity, "showManual", new Class<?>[]{});
            AlertDialog dialog = ShadowAlertDialog.getLatestAlertDialog(); View decor = dialog.getWindow().getDecorView();
            TextView heading = text(decor, "添加电脑"); assertNotNull(heading); assertTrue(heading.getTypeface().isBold());
            ViewGroup sections = (ViewGroup)heading.getParent(); TextView body = (TextView)sections.getChildAt(1);
            assertTrue(heading.getTextSize() > body.getTextSize()); assertFalse(body.getTypeface().isBold());
            assertTrue(sections.getParent() instanceof ScrollView);
            assertTrue(((TextView)sections.getChildAt(sections.getChildCount()-1)).getText().toString().contains("重启后仍保持暂停"));
            snapshot(decor, "tablet-manual.png", 520, 720);
            ScrollView scroll = (ScrollView)sections.getParent(); scroll.scrollTo(0, 100000);
            TextView last = (TextView)sections.getChildAt(sections.getChildCount()-1);
            assertTrue("last manual paragraph remains reachable", last.getBottom() <= scroll.getScrollY() + scroll.getHeight());
            snapshot(decor, "tablet-manual-end.png", 520, 720);
            dialog.dismiss();
        }
    }
}
