package com.limelight.streamhub;

import android.app.Activity;
import android.app.AlertDialog;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.TextView;
import com.limelight.R;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.Robolectric;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.annotation.Config;
import org.robolectric.shadows.ShadowAlertDialog;
import static org.robolectric.Shadows.shadowOf;
import java.util.ArrayList;
import java.util.List;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=28, manifest=Config.NONE, application=android.app.Application.class)
public class HubControlsTest {
    @Test public void temporaryViewportShrinkDoesNotForgetPreferredAnchorPosition() {
        Activity activity=Robolectric.buildActivity(Activity.class).setup().get();
        activity.getSharedPreferences("streamhub_controls",0).edit().putFloat("toolbar_x",1100).putFloat("toolbar_y",900).commit();
        FrameLayout root=new FrameLayout(activity){
            @Override public void getWindowVisibleDisplayFrame(android.graphics.Rect out){out.set(0,0,getWidth(),getHeight());}
        };
        RtInputToolbar toolbar=new RtInputToolbar(activity,root,new RtInputToolbar.Callbacks(){
            public void showKeyboard(){} public void showFullKeyboard(){} public void sendText(String value){} public void sendKeys(short[] keys){}
        });
        Button entry=button(root,"控制");
        for(int[] size:new int[][]{{1600,1400},{800,500},{1600,1400}}){
            root.measure(View.MeasureSpec.makeMeasureSpec(size[0],View.MeasureSpec.EXACTLY),View.MeasureSpec.makeMeasureSpec(size[1],View.MeasureSpec.EXACTLY));
            root.layout(0,0,size[0],size[1]);toolbar.refreshLayout();
            FrameLayout.LayoutParams lp=(FrameLayout.LayoutParams)entry.getLayoutParams();
            assertTrue(lp.leftMargin>=0 && lp.topMargin>=0);
            assertTrue(lp.leftMargin+lp.width<=size[0] && lp.topMargin+lp.height<=size[1]);
            if(size[0]==1600){assertEquals(1100,lp.leftMargin);assertEquals(900,lp.topMargin);}
        }
        toolbar.dispose();activity.finish();
    }
    private static class Fixture implements RtInputToolbar.Callbacks {
        final Activity activity = Robolectric.buildActivity(Activity.class).setup().get();
        final FrameLayout root = new FrameLayout(activity);
        final RtInputToolbar toolbar;
        int disconnects, stats;
        Fixture() {
            activity.setContentView(root);
            toolbar = new RtInputToolbar(activity, root, this);
            root.measure(View.MeasureSpec.makeMeasureSpec(900, View.MeasureSpec.EXACTLY),
                    View.MeasureSpec.makeMeasureSpec(1400, View.MeasureSpec.EXACTLY));
            root.layout(0, 0, 900, 1400);
            toolbar.show();
        }
        public void showKeyboard() { }
        public void showFullKeyboard() { }
        public void sendText(String value) { }
        public void sendKeys(short[] keys) { }
        public void disconnect() { disconnects++; }
        public void toggleStats() { stats++; }
    }
    private List<Button> buttons(View root) {
        List<Button> result = new ArrayList<>();
        if (root instanceof Button) result.add((Button) root);
        if (root instanceof ViewGroup) {
            ViewGroup group = (ViewGroup) root;
            for (int i=0; i<group.getChildCount(); i++) result.addAll(buttons(group.getChildAt(i)));
        }
        return result;
    }
    private Button button(View root, String label) {
        return buttons(root).stream().filter(view -> label.contentEquals(view.getText())).findFirst().orElseThrow(AssertionError::new);
    }
    @Test public void eachOperationHasOneEntryWithoutLegacyMenu() {
        Fixture f = new Fixture();
        assertEquals(1, buttons(f.root).stream().filter(v -> "断开连接".contentEquals(v.getText())).count());
        assertEquals(1, buttons(f.root).stream().filter(v -> v.getText().toString().contains("性能信息")).count());
        assertEquals(1, buttons(f.root).stream().filter(v -> v.getText().toString().contains("鼠标模式")).count());
        assertFalse(buttons(f.root).stream().anyMatch(v -> v.getText().toString().matches("会话|设置|打开更多设置|安全与输入权限|高级菜单|终止会话")));
        button(f.root, "画面").performClick();
        assertTrue(button(f.root, "查看串流诊断").isShown());
        f.toolbar.dispose();
    }
    @Test public void statsToggleHasOneSynchronizedState() {
        Fixture f = new Fixture();
        button(f.root, "显示性能信息").performClick();
        assertEquals(1, f.stats);
        f.toolbar.setStatsVisible(true);
        assertTrue(button(f.root, "隐藏性能信息").isSelected());
        f.toolbar.setStatsVisible(false);
        assertFalse(button(f.root, "显示性能信息").isSelected());
        f.toolbar.dispose();
    }
    @Test public void disconnectCanCancelAndConfirmsExactlyOnce() {
        Fixture f = new Fixture();
        button(f.root, "断开连接").performClick();
        AlertDialog dialog = ShadowAlertDialog.getLatestAlertDialog();
        assertNotNull(dialog);
        dialog.getButton(AlertDialog.BUTTON_NEGATIVE).performClick();
        shadowOf(android.os.Looper.getMainLooper()).idle();
        assertEquals(0, f.disconnects);
        button(f.root, "断开连接").performClick();
        ShadowAlertDialog.getLatestAlertDialog().getButton(AlertDialog.BUTTON_POSITIVE).performClick();
        shadowOf(android.os.Looper.getMainLooper()).idle();
        assertEquals(1, f.disconnects);
        f.toolbar.dispose();
    }
    @Test public void shortcutsOpenFirstAndKeyboardClipboardStayVisibleAcrossTabs() {
        Fixture f=new Fixture();
        assertTrue(button(f.root,"切换窗口 · Alt+Tab").isShown());
        for(String tab:new String[]{"输入","画面","快捷键"}) {
            button(f.root,tab).performClick();
            assertTrue(button(f.root,"键盘").isShown());
            assertTrue(button(f.root,"剪贴板").isShown());
        }
        f.toolbar.dispose();
    }
    @Test public void backClosesPanelBeforeEndingSession() {
        Fixture f = new Fixture();
        assertTrue(f.toolbar.collapseOrConsumeBack());
        assertFalse(f.toolbar.collapseOrConsumeBack());
        assertEquals(0, f.disconnects);
        f.toolbar.dispose();
    }
    @Test public void secondaryDialogUsesDarkThemeAndReadableText() {
        Fixture f = new Fixture();
        AlertDialog dialog = HubStreamUi.dialog(f.activity).setTitle("音频输出").setMessage("设置说明")
                .setPositiveButton("知道了", null).setNegativeButton("取消", null).show();
        android.util.TypedValue background = new android.util.TypedValue();
        assertTrue(dialog.getContext().getTheme().resolveAttribute(android.R.attr.colorBackground, background, true));
        assertEquals(HubStreamUi.SURFACE, background.data);
        TextView message = dialog.findViewById(android.R.id.message);
        assertEquals(HubStreamUi.TEXT, message.getCurrentTextColor());
        assertReadableDialogButton(dialog, AlertDialog.BUTTON_POSITIVE);
        assertReadableDialogButton(dialog, AlertDialog.BUTTON_NEGATIVE);
        dialog.dismiss();
        AlertDialog created = HubStreamUi.dialog(f.activity).setTitle("触控模式")
                .setNeutralButton("关闭", null).create();
        created.show();
        assertReadableDialogButton(created, AlertDialog.BUTTON_NEUTRAL);
        created.dismiss(); f.toolbar.dispose();
    }
    @Test public void deviceAndFilePagesKeepReadableDarkPalette() {
        int[][] pairs = {
                {HubStreamUi.TEXT, HubStreamUi.SURFACE}, {HubStreamUi.MUTED, HubStreamUi.SURFACE},
                {HubStreamUi.ACCENT, HubStreamUi.CARD}, {android.graphics.Color.WHITE, HubStreamUi.PRIMARY},
                {HubStreamUi.WARNING, HubStreamUi.SURFACE}, {HubStreamUi.DANGER, HubStreamUi.SURFACE},
                {HubStreamUi.SUCCESS, HubStreamUi.SURFACE}
        };
        for (int[] pair : pairs) assertTrue("Dark page text must meet 4.5:1 contrast",
                androidx.core.graphics.ColorUtils.calculateContrast(pair[0], pair[1]) >= 4.5);
    }
    @Test public void manualThemeModeReachesPreferenceActivity() {
        Activity activity = Robolectric.buildActivity(Activity.class).setup().get();
        try {
            HubTheme.setMode(activity, HubTheme.LIGHT);
            assertFalse(HubTheme.isDark(activity));
            assertEquals(R.style.HubSettingsThemeLight, HubTheme.settingsTheme(activity));
            HubTheme.setMode(activity, HubTheme.DARK);
            assertTrue(HubTheme.isDark(activity));
            assertEquals(R.style.HubSettingsThemeDark, HubTheme.settingsTheme(activity));
        } finally {
            HubTheme.setMode(activity, HubTheme.SYSTEM);
        }
    }
    private void assertReadableDialogButton(AlertDialog dialog, int which) {
        assertTrue("Secondary dialog action must remain readable on the dark surface",
                androidx.core.graphics.ColorUtils.calculateContrast(
                        dialog.getButton(which).getCurrentTextColor(), HubStreamUi.SURFACE) >= 4.5);
    }
    @Test public void redundantHeaderLabelsAreAbsent() {
        Fixture f = new Fixture();
        assertFalse(containsText(f.root, "电脑控制"));
        assertFalse(containsText(f.root, "已连接"));
        assertTrue(button(f.root, "收起").isShown());
        f.toolbar.dispose();
    }
    private boolean containsText(View root, String text) {
        if (root instanceof TextView && text.contentEquals(((TextView)root).getText())) return true;
        if (root instanceof ViewGroup) {
            ViewGroup group=(ViewGroup)root;
            for (int i=0;i<group.getChildCount();i++) if(containsText(group.getChildAt(i),text)) return true;
        }
        return false;
    }
}
