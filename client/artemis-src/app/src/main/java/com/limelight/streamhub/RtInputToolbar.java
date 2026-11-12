package com.limelight.streamhub;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Context;
import android.content.res.ColorStateList;
import android.content.SharedPreferences;
import android.graphics.Rect;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.os.Build;
import android.text.Editable;
import android.text.InputType;
import android.text.TextWatcher;
import android.view.ContextThemeWrapper;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.ViewTreeObserver;
import android.view.WindowInsets;
import android.view.WindowManager;
import android.view.inputmethod.InputMethodManager;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.ToggleButton;

import com.limelight.binding.input.KeyboardTranslator;
import com.limelight.nvstream.input.MouseButtonPacket;

import java.util.ArrayList;
import java.util.List;

/** Compact controls that remain reachable without taking over the remote desktop. */
public final class RtInputToolbar {
    public interface Callbacks {
        void showKeyboard();
        void sendText(String text);
        void sendKeys(short[] keys);
        default void sendMouseButton(byte button) { }
        default void sendMouseScroll(byte amount) { }
        void showFullKeyboard();
        default void toggleStats() { }
        default void showDiagnostics() { }
        default void showAudioSettings() { }
        default void showMouseMode() { }
        default void showDisplaySettings() { }
        default void showResolutionSettings() { }
        default void showPrivacySettings() { }
        default boolean privacyAvailable() { return false; }
        default void rotateScreen() { }
        default void toggleZoom() { }
        default boolean zoomEnabled() { return false; }
        default void sendClipboard() { }
        default void fetchClipboard() { }
        default void showWarningSettings() { }
        default void showLockSettings() { }
        default void disconnect() { }
        default void hideKeyboard() { }
    }

    private static final int SURFACE = HubStreamUi.SURFACE, CARD = HubStreamUi.CARD, BORDER = HubStreamUi.BORDER;
    private static final int TEXT = HubStreamUi.TEXT, MUTED = HubStreamUi.MUTED, ACCENT = HubStreamUi.ACCENT;
    private static final int SELECTED = HubStreamUi.SELECTED, DANGER = HubStreamUi.DANGER;
    private final Activity activity;
    private final FrameLayout parent;
    private final Callbacks callbacks;
    private final LinearLayout panel, pages, fixed, footer, tabRow;
    private final FrameLayout handleArea;
    private final ScrollView body;
    private final Button entry, quickDisconnect, statsButton, zoomButton, keyboardEntry, compactClose;
    private final Button[] tabs = new Button[3];
    private final LinearLayout[] sections = new LinearLayout[3];
    private final TextView sessionTitle, sessionDetail;
    private final ToggleButton[] modifiers = new ToggleButton[4];
    private final short[] modifierKeys = { KeyboardTranslator.VK_LCONTROL, KeyboardTranslator.VK_LMENU,
            KeyboardTranslator.VK_LSHIFT, KeyboardTranslator.VK_LWIN };
    private final ViewTreeObserver.OnGlobalLayoutListener layoutListener = this::refreshLayout;
    private AlertDialog textDialog, disconnectDialog;
    private String textDraft = "";
    private boolean pictureInPicture, disposed, keyboardVisible;
    private final SharedPreferences positionPrefs;
    private boolean customPosition;
    private float toolbarX = Float.NaN, toolbarY = Float.NaN;
    private float dragDownX, dragDownY, dragOriginX, dragOriginY;
    private boolean dragging;
    private int selectedTab;
    private final int[] scrollPositions = new int[3];

    public RtInputToolbar(Activity activity, FrameLayout parent, Callbacks callbacks) {
        this.activity = activity;
        this.parent = parent;
        this.callbacks = callbacks;
        positionPrefs = activity.getSharedPreferences("streamhub_controls", Context.MODE_PRIVATE);
        if (positionPrefs.contains("toolbar_x") && positionPrefs.contains("toolbar_y")) {
            customPosition = true;
            toolbarX = positionPrefs.getFloat("toolbar_x", 0f);
            toolbarY = positionPrefs.getFloat("toolbar_y", 0f);
        }

        panel = column();
        HubStreamUi.keepColors(panel);
        panel.setBackground(shape(SURFACE, BORDER, 20));
        panel.setElevation(dp(18));
        panel.setPadding(dp(16), dp(14), dp(16), dp(16));
        panel.setVisibility(View.GONE);
        panel.setClickable(true);
        parent.addView(panel, new FrameLayout.LayoutParams(dp(420), dp(500), Gravity.TOP | Gravity.CENTER_HORIZONTAL));

        handleArea = new FrameLayout(activity);
        View handle = new View(activity);
        handle.setBackground(shape(BORDER, BORDER, 2));
        handleArea.addView(handle, new FrameLayout.LayoutParams(dp(36), dp(4), Gravity.CENTER));
        handleArea.setContentDescription("拖动控制面板");
        handleArea.setOnTouchListener(this::handleDrag);
        panel.addView(handleArea, new LinearLayout.LayoutParams(-1, dp(20)));

        tabRow = new LinearLayout(activity);
        tabRow.setPadding(0, dp(4), 0, dp(12));
        String[] labels = {"快捷键", "输入", "画面"};
        for (int i = 0; i < labels.length; i++) {
            final int index = i;
            Button tab = action(labels[i], () -> selectTab(index), false);
            tabs[i] = tab;
            tab.setContentDescription(labels[i] + "，电脑控制选项");
            LinearLayout.LayoutParams size = new LinearLayout.LayoutParams(0, -2, 1);
            if (i > 0) size.leftMargin = dp(8);
            tabRow.addView(tab, size);
        }
        Button close = action("收起", this::collapse, false);
        close.setContentDescription("收起控制面板");
        LinearLayout.LayoutParams closeSize = new LinearLayout.LayoutParams(dp(64), -2);
        closeSize.leftMargin = dp(8);
        tabRow.addView(close, closeSize);
        panel.addView(tabRow);

        body = new ScrollView(activity);
        body.setFillViewport(false);
        body.setClipToPadding(false);
        body.setOverScrollMode(View.OVER_SCROLL_IF_CONTENT_SCROLLS);
        body.setVerticalScrollBarEnabled(true);
        pages = column();
        body.addView(pages, new ScrollView.LayoutParams(-1, -2));
        panel.addView(body, new LinearLayout.LayoutParams(-1, 0, 1));
        for (int i = 0; i < sections.length; i++) {
            sections[i] = column();
            pages.addView(sections[i], new LinearLayout.LayoutParams(-1, -2));
        }

        LinearLayout input = sections[1];
        sectionHeading(input, "文字与键盘");
        addWide(input, action("中文文本", this::showTextDialog, true));
        addHint(input, "先在电脑上点选输入位置，再输入或粘贴文字。", 8);
        AdaptiveGrid keyboards = grid(input, 128, 2, 12);
        keyboards.addView(action("软键盘", () -> { collapse(); callbacks.showKeyboard(); }, false));
        keyboards.addView(action("完整键盘", () -> { collapse(); callbacks.showFullKeyboard(); }, false));
        addWide(input, action("收起屏幕键盘", () -> { callbacks.hideKeyboard(); collapse(); }, false), 8);
        sectionHeading(input, "常用按键");
        AdaptiveGrid plainKeys = grid(input, 64, 4, 0);
        plainKey(plainKeys, "Tab", KeyboardTranslator.VK_TAB);
        plainKey(plainKeys, "回车", KeyboardTranslator.VK_RETURN);
        plainKey(plainKeys, "退格", KeyboardTranslator.VK_BACK_SPACE);
        plainKey(plainKeys, "Esc", KeyboardTranslator.VK_ESCAPE);
        plainKey(plainKeys, "空格", KeyboardTranslator.VK_SPACE);
        plainKey(plainKeys, "←", KeyboardTranslator.VK_LEFT);
        plainKey(plainKeys, "↑", KeyboardTranslator.VK_UP);
        plainKey(plainKeys, "↓", KeyboardTranslator.VK_DOWN);
        plainKey(plainKeys, "→", KeyboardTranslator.VK_RIGHT);
        plainKey(plainKeys, "Home", KeyboardTranslator.VK_HOME);
        plainKey(plainKeys, "End", KeyboardTranslator.VK_END);
        plainKey(plainKeys, "删除", 0x2E);
        addHint(input, "常用组合键在“快捷键”页，长按组合可置顶。", 12);

        sectionHeading(input, "虚拟鼠标");
        AdaptiveGrid mouseButtons = grid(input, 96, 3, 0);
        mouseButtons.addView(action("左键", () -> callbacks.sendMouseButton(MouseButtonPacket.BUTTON_LEFT), false));
        mouseButtons.addView(action("右键", () -> callbacks.sendMouseButton(MouseButtonPacket.BUTTON_RIGHT), false));
        mouseButtons.addView(action("中键", () -> callbacks.sendMouseButton(MouseButtonPacket.BUTTON_MIDDLE), false));
        mouseButtons.addView(action("滚轮上", () -> callbacks.sendMouseScroll((byte) 1), false));
        mouseButtons.addView(action("滚轮下", () -> callbacks.sendMouseScroll((byte) -1), false));
        addWide(input, action("触控与鼠标模式", () -> { collapse(); callbacks.showMouseMode(); }, false), 8);
        addWide(input, action("手势说明", this::showGestureGuide, false), 8);
        addHint(input, "双指上下滑动滚动页面；长按后移动可拖动；双指点按可使用右键。", 6);
        sectionHeading(input, "剪贴板");
        AdaptiveGrid clipboard = grid(input, 128, 2, 0);
        clipboard.addView(action("发送剪贴板到电脑", () -> { collapse(); callbacks.sendClipboard(); }, false));
        clipboard.addView(action("获取电脑剪贴板", () -> { collapse(); callbacks.fetchClipboard(); }, false));

        // Shortcuts have their own first page; input options no longer push them below the fold.
        LinearLayout shortcuts = sections[0];
        sectionHeading(shortcuts, "常用组合");
        addHint(shortcuts, "长按任意组合键可置顶或取消置顶。", 0);
        AdaptiveGrid combinations = grid(shortcuts, 128, 2, 0);
        shortcut(combinations, "切换窗口 · Alt+Tab", KeyboardTranslator.VK_LMENU, KeyboardTranslator.VK_TAB);
        shortcut(combinations, "任务视图 · Win+Tab", KeyboardTranslator.VK_LWIN, KeyboardTranslator.VK_TAB);
        shortcut(combinations, "复制 · Ctrl+C", KeyboardTranslator.VK_LCONTROL, KeyboardTranslator.VK_C);
        shortcut(combinations, "粘贴 · Ctrl+V", KeyboardTranslator.VK_LCONTROL, KeyboardTranslator.VK_V);
        shortcut(combinations, "全选 · Ctrl+A", KeyboardTranslator.VK_LCONTROL, KeyboardTranslator.VK_A);
        shortcut(combinations, "撤销 · Ctrl+Z", KeyboardTranslator.VK_LCONTROL, 'Z');
        shortcut(combinations, "保存 · Ctrl+S", KeyboardTranslator.VK_LCONTROL, KeyboardTranslator.VK_S);
        shortcut(combinations, "显示桌面 · Win+D", KeyboardTranslator.VK_LWIN, KeyboardTranslator.VK_D);
        shortcut(combinations, "任务管理器", KeyboardTranslator.VK_LCONTROL, KeyboardTranslator.VK_LSHIFT, KeyboardTranslator.VK_ESCAPE);
        sectionHeading(shortcuts, "自定义组合");
        addHint(shortcuts, "选中修饰键，再点下方按键；发送后自动释放。", 0);
        AdaptiveGrid modifierGrid = grid(shortcuts, 56, 4, 10);
        String[] modifierLabels = {"Ctrl", "Alt", "Shift", "Win"};
        for (int i = 0; i < modifierLabels.length; i++) {
            ToggleButton toggle = new ToggleButton(activity);
            configureButton(toggle);
            toggle.setTextOff(modifierLabels[i]);
            toggle.setTextOn(modifierLabels[i]);
            toggle.setChecked(false);
            toggle.setContentDescription(modifierLabels[i] + " 组合键修饰符");
            toggle.setOnCheckedChangeListener((button, checked) -> {
                button.setTextColor(checked ? TEXT : MUTED);
                button.setBackground(buttonBackground(checked ? SELECTED : CARD, checked ? ACCENT : BORDER));
            });
            toggle.setTextColor(MUTED);
            toggle.setBackground(buttonBackground(CARD, BORDER));
            modifierGrid.addView(toggle);
            modifiers[i] = toggle;
        }
        AdaptiveGrid customKeys = grid(shortcuts, 56, 4, 8);
        customKey(customKeys, "Tab", KeyboardTranslator.VK_TAB);
        customKey(customKeys, "回车", KeyboardTranslator.VK_RETURN);
        customKey(customKeys, "退格", KeyboardTranslator.VK_BACK_SPACE);
        customKey(customKeys, "Esc", KeyboardTranslator.VK_ESCAPE);
        for (char letter : new char[] {'A','C','V','X','Z','S','F','R','D','L','T','W'}) customKey(customKeys, String.valueOf(letter), letter);
        customKey(customKeys, "←", KeyboardTranslator.VK_LEFT);
        customKey(customKeys, "↑", KeyboardTranslator.VK_UP);
        customKey(customKeys, "↓", KeyboardTranslator.VK_DOWN);
        customKey(customKeys, "→", KeyboardTranslator.VK_RIGHT);
        customKey(customKeys, "F5", KeyboardTranslator.VK_F1 + 4);
        customKey(customKeys, "F11", KeyboardTranslator.VK_F11);
        addWide(shortcuts, action("清除修饰键", this::clearModifiers, false), 8);

        LinearLayout settings = sections[2];
        sessionTitle = text("远程电脑", 18, TEXT);
        sessionTitle.setTypeface(null, Typeface.BOLD);
        settings.addView(sessionTitle);
        sessionDetail = text("连接信息由当前会话提供。", 14, MUTED);
        addWide(settings, sessionDetail, 6);
        sectionHeading(settings, "当前画面");
        AdaptiveGrid display = grid(settings, 128, 2, 0);
        display.addView(action("旋转屏幕", () -> { collapse(); callbacks.rotateScreen(); }, false));
        zoomButton = action("开启平移缩放", () -> { collapse(); callbacks.toggleZoom(); }, false);
        display.addView(zoomButton);
        addWide(settings, action("当前分辨率", () -> { collapse(); callbacks.showResolutionSettings(); }, false), 8);
        if (callbacks.privacyAvailable()) addWide(settings, action("本机隐私屏", () -> { collapse(); callbacks.showPrivacySettings(); }, false), 8);
        sectionHeading(settings, "性能与声音");
        statsButton = action("显示性能信息", callbacks::toggleStats, false);
        addWide(settings, statsButton);
        addWide(settings, action("查看串流诊断", () -> { collapse(); callbacks.showDiagnostics(); }, false), 8);
        addWide(settings, action("音频输出", () -> { collapse(); callbacks.showAudioSettings(); }, false), 8);
        addWide(settings, action("串流提示", callbacks::showWarningSettings, false), 8);
        if (callbacks.privacyAvailable()) addWide(settings, action("锁屏与退出", callbacks::showLockSettings, false), 8);
        android.widget.Switch adaptive = new android.widget.Switch(activity);
        adaptive.setText("菜单自适应展开"); adaptive.setTextColor(TEXT); adaptive.setMinHeight(dp(48));
        adaptive.setChecked(positionPrefs.getBoolean("adaptive_expand", true));
        adaptive.setOnCheckedChangeListener((v, enabled) -> { positionPrefs.edit().putBoolean("adaptive_expand", enabled).apply(); refreshLayout(); });
        addWide(settings, adaptive, 8);
        sectionHeading(settings, "下次连接");
        addWide(settings, action("画质与编码设置", () -> { collapse(); callbacks.showDisplaySettings(); }, false));
        addHint(settings, "可在串流中选择画质预设，下次连接生效。", 6);

        footer = new LinearLayout(activity); footer.setOrientation(LinearLayout.VERTICAL);
        fixed = new LinearLayout(activity);
        keyboardEntry=action("键盘", () -> { collapse(); if (keyboardVisible) callbacks.hideKeyboard(); else callbacks.showKeyboard(); }, false);
        Button clipboardEntry=action("剪贴板", this::showClipboardActions, false);
        fixed.addView(keyboardEntry,new LinearLayout.LayoutParams(0,-2,1));
        LinearLayout.LayoutParams clipSize=new LinearLayout.LayoutParams(0,-2,1);clipSize.leftMargin=dp(8);fixed.addView(clipboardEntry,clipSize);
        LinearLayout.LayoutParams fixedSize=new LinearLayout.LayoutParams(-1,-2);fixedSize.topMargin=dp(8);footer.addView(fixed,fixedSize);
        // The persistent exit action is separate from the scrolling pages.
        quickDisconnect = action("断开连接", this::confirmDisconnect, false);
        quickDisconnect.setTextColor(DANGER);
        quickDisconnect.setContentDescription("断开当前电脑连接并返回设备页");
        quickDisconnect.setBackground(buttonBackground(0xFF482D38, 0xFF885365));
        LinearLayout.LayoutParams disconnectSize = new LinearLayout.LayoutParams(-1, -2);
        disconnectSize.topMargin = dp(12);
        footer.addView(quickDisconnect, disconnectSize);
        compactClose = action("收起", this::collapse, false);
        compactClose.setContentDescription("收起控制面板");
        compactClose.setVisibility(View.GONE);
        LinearLayout.LayoutParams compactCloseSize = new LinearLayout.LayoutParams(0, -2, 1);
        compactCloseSize.topMargin = dp(8); compactCloseSize.leftMargin = dp(8);
        footer.addView(compactClose, compactCloseSize);
        panel.addView(footer, new LinearLayout.LayoutParams(-1, -2));

        entry = action("控制", this::show, false);
        entry.setTextSize(14);
        entry.setTypeface(null, Typeface.BOLD);
        entry.setContentDescription("展开电脑控制：输入、快捷键和画面设置");
        entry.setElevation(dp(20));
        entry.setBackground(new RippleDrawable(ColorStateList.valueOf(0x336F9DFF), shape(SURFACE, BORDER, 24), null));
        entry.setOnTouchListener(this::handleDrag);
        parent.addView(entry, new FrameLayout.LayoutParams(dp(88), dp(48), Gravity.TOP | Gravity.CENTER_HORIZONTAL));
        selectTab(0);
        parent.getViewTreeObserver().addOnGlobalLayoutListener(layoutListener);
        // A tap on the surrounding root closes the expanded controls while the
        // event continues to the streaming surface underneath.
        parent.setOnTouchListener((view, event) -> {
            if (event.getActionMasked() == MotionEvent.ACTION_DOWN && panel.getVisibility() == View.VISIBLE) {
                Rect bounds = new Rect();
                panel.getHitRect(bounds);
                if (!bounds.contains(Math.round(event.getX()), Math.round(event.getY()))) collapse();
            }
            return false;
        });
        parent.post(this::refreshLayout);
    }

    private LinearLayout column() { LinearLayout view = new LinearLayout(activity); view.setOrientation(LinearLayout.VERTICAL); return view; }
    private TextView text(String value, int size, int color) {
        TextView view = new TextView(activity);
        view.setText(value); view.setTextSize(size); view.setTextColor(color);
        view.setLineSpacing(dp(3), 1);
        return view;
    }
    private void sectionHeading(LinearLayout section, String title) {
        TextView heading = text(title, 14, MUTED);
        heading.setTypeface(null, Typeface.BOLD);
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
        params.topMargin = section.getChildCount() == 0 ? 0 : dp(20);
        params.bottomMargin = dp(10);
        section.addView(heading, params);
    }
    private void addHint(LinearLayout section, String value, int top) { addWide(section, text(value, 12, MUTED), top); }
    private void addWide(LinearLayout section, View view) { addWide(section, view, 0); }
    private void addWide(LinearLayout section, View view, int top) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
        params.topMargin = dp(top);
        section.addView(view, params);
    }
    private AdaptiveGrid grid(LinearLayout section, int minimumCell, int maxColumns, int top) {
        AdaptiveGrid grid = new AdaptiveGrid(activity, dp(minimumCell), maxColumns, dp(8));
        addWide(section, grid, top);
        return grid;
    }
    private void configureButton(Button button) {
        button.setAllCaps(false); button.setTextSize(14); button.setGravity(Gravity.CENTER);
        button.setMinWidth(0); button.setMinimumWidth(0);
        button.setMinHeight(dp(48)); button.setMinimumHeight(dp(48));
        button.setPadding(dp(10), dp(8), dp(10), dp(8));
        button.setMaxLines(2);
        button.setStateListAnimator(null);
    }
    private Button action(String label, Runnable action, boolean primary) {
        Button button = new Button(activity);
        configureButton(button);
        button.setText(label);
        button.setTextColor(primary ? 0xFFFFFFFF : TEXT);
        button.setBackground(buttonBackground(primary ? HubStreamUi.PRIMARY : CARD, primary ? HubStreamUi.PRIMARY : BORDER));
        button.setOnClickListener(view -> action.run());
        return button;
    }
    private void plainKey(AdaptiveGrid grid, String label, int code) {
        Button button = action(label, () -> callbacks.sendKeys(new short[] {(short) code}), false);
        button.setContentDescription(label + "，发送到电脑");
        grid.addView(button);
    }
    private void customKey(AdaptiveGrid grid, String label, int code) {
        Button button = action(label, () -> {
            List<Short> keys = new ArrayList<>();
            for (int i = 0; i < modifiers.length; i++) if (modifiers[i].isChecked() && modifierKeys[i] != code) keys.add(modifierKeys[i]);
            keys.add((short) code);
            short[] chord = new short[keys.size()];
            for (int i = 0; i < chord.length; i++) chord[i] = keys.get(i);
            callbacks.sendKeys(chord);
            clearModifiers();
        }, false);
        button.setContentDescription(label + "，与选中的修饰键一起发送到电脑");
        grid.addView(button);
    }
    private void shortcut(AdaptiveGrid grid, String label, int... codes) {
        Button button = action(label, () -> {
            short[] chord = new short[codes.length];
            for (int i = 0; i < codes.length; i++) chord[i] = (short) codes[i];
            callbacks.sendKeys(chord);
            clearModifiers();
        }, false);
        button.setTextSize(13);
        if(positionPrefs.getBoolean("pinned_"+label,false))button.setText("★ "+label);
        button.setOnLongClickListener(v->{boolean pinned=!positionPrefs.getBoolean("pinned_"+label,false);
            positionPrefs.edit().putBoolean("pinned_"+label,pinned).apply();button.setText((pinned?"★ ":"")+label);
            grid.removeView(button);grid.addView(button,pinned?0:grid.getChildCount());return true;});
        button.setContentDescription(label + "，组合键发送到电脑，长按置顶");
        grid.addView(button,positionPrefs.getBoolean("pinned_"+label,false)?0:grid.getChildCount());
    }
    private void selectTab(int index) {
        scrollPositions[selectedTab]=body.getScrollY(); selectedTab=index;
        clearModifiers();
        for (int i = 0; i < sections.length; i++) {
            sections[i].setVisibility(i == index ? View.VISIBLE : View.GONE);
            tabs[i].setSelected(i == index);
            tabs[i].setTextColor(i == index ? ACCENT : MUTED);
            tabs[i].setBackground(buttonBackground(i == index ? SELECTED : CARD, i == index ? ACCENT : BORDER));
        }
        body.post(() -> body.scrollTo(0,scrollPositions[index]));
    }
    private void clearModifiers() { for (ToggleButton modifier : modifiers) if (modifier != null) modifier.setChecked(false); }

    private void showGestureGuide() {
        AlertDialog dialog = HubStreamUi.dialog(activity)
                .setTitle("触控与手势")
                .setMessage("单指移动：移动鼠标\n单指点按：左键\n长按后移动：拖动窗口或文件\n双指点按：右键\n双指上下滑动：滚轮\n三指点按：打开屏幕键盘")
                .setPositiveButton("知道了", null)
                .create();
        dialog.show();
        if (dialog.getWindow() != null) dialog.getWindow().setBackgroundDrawable(shape(SURFACE, BORDER, 16));
        TextView message = dialog.findViewById(android.R.id.message);
        if (message != null) { message.setTextColor(TEXT); message.setTextSize(15); }
        Button ok = dialog.getButton(AlertDialog.BUTTON_POSITIVE);
        if (ok != null) { ok.setTextColor(ACCENT); ok.setMinHeight(dp(48)); }
    }

    private void showClipboardActions() {
        HubStreamUi.dialog(activity).setTitle("剪贴板")
                .setItems(new String[]{"获取电脑剪贴板","发送平板剪贴板"},(d,w)->{
                    collapse(); if(w==0)callbacks.fetchClipboard();else callbacks.sendClipboard();
                }).setNegativeButton("取消",null).show();
    }

    private boolean handleDrag(View view, MotionEvent event) {
        switch(event.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                dragDownX=event.getRawX();dragDownY=event.getRawY();
                // Start a new drag from the visible anchor, which may currently
                // be clamped by the keyboard or a different orientation.
                FrameLayout.LayoutParams anchor=(FrameLayout.LayoutParams)entry.getLayoutParams();
                dragOriginX=anchor.leftMargin;
                dragOriginY=anchor.topMargin;
                dragging=false;return true;
            case MotionEvent.ACTION_MOVE:
                float dx=event.getRawX()-dragDownX,dy=event.getRawY()-dragDownY;
                if(!dragging && Math.hypot(dx,dy)<dp(8))return true;
                dragging=true;setToolbarPosition(dragOriginX+dx,dragOriginY+dy,false);return true;
            case MotionEvent.ACTION_UP:
                if(dragging)setToolbarPosition(toolbarX,toolbarY,true);
                else if(view==entry)show();
                dragging=false;return true;
            case MotionEvent.ACTION_CANCEL:
                if(dragging)setToolbarPosition(toolbarX,toolbarY,true);
                dragging=false;return true;
            default:return true;
        }
    }
    private void setToolbarPosition(float x,float y,boolean persist) {
        toolbarX=x;toolbarY=y;customPosition=true;refreshLayout();
        if(persist)positionPrefs.edit().putFloat("toolbar_x",toolbarX).putFloat("toolbar_y",toolbarY).apply();
    }
    private void place(View view,HubOverlayPlacement.Box bounds) {
        FrameLayout.LayoutParams lp=(FrameLayout.LayoutParams)view.getLayoutParams();
        if(lp.gravity!=(Gravity.TOP|Gravity.LEFT)||lp.leftMargin!=bounds.x||lp.topMargin!=bounds.y||
                lp.width!=bounds.width||lp.height!=bounds.height||lp.rightMargin!=0) {
            lp.gravity=Gravity.TOP|Gravity.LEFT;lp.leftMargin=bounds.x;lp.topMargin=bounds.y;
            lp.rightMargin=0;lp.width=bounds.width;lp.height=bounds.height;view.setLayoutParams(lp);
        }
    }

    public void show() {
        if (disposed || pictureInPicture) return;
        zoomButton.setText(callbacks.zoomEnabled() ? "关闭平移缩放" : "开启平移缩放");
        refreshLayout();
        panel.setVisibility(View.VISIBLE);
        entry.setVisibility(View.GONE);
        quickDisconnect.setVisibility(View.VISIBLE);
        refreshLayout();
    }
    private void collapse() {
        panel.setVisibility(View.GONE);
        entry.setVisibility(pictureInPicture || disposed ? View.GONE : View.VISIBLE);
        quickDisconnect.setVisibility(View.GONE);
        clearModifiers();
    }
    /** True means the Back action closed local controls and should not leave the stream. */
    public boolean collapseOrConsumeBack() {
        if (textDialog != null && textDialog.isShowing()) { textDialog.dismiss(); return true; }
        if (disconnectDialog != null && disconnectDialog.isShowing()) { disconnectDialog.dismiss(); return true; }
        if (panel.getVisibility() == View.VISIBLE) { collapse(); return true; }
        return false;
    }
    /** Close the panel when a touch is delivered to the stream surface. */
    public void collapseFromOutsideTouch() {
        if (panel.getVisibility() == View.VISIBLE) collapse();
    }
    public void setSessionInfo(String title, String detail) {
        sessionTitle.setText(title == null ? "远程电脑" : title);
        sessionDetail.setText(detail == null ? "" : detail);
    }
    public void setStatsVisible(boolean visible) {
        statsButton.setText(visible ? "隐藏性能信息" : "显示性能信息");
        statsButton.setSelected(visible);
        statsButton.setBackground(buttonBackground(visible ? SELECTED : CARD, visible ? ACCENT : BORDER));
    }

    public void refreshLayout() {
        if (disposed || parent.getWidth() <= 0 || parent.getHeight() <= 0) return;
        int left = 0, right = 0, top = 0, bottom = 0;
        WindowInsets insets = parent.getRootWindowInsets();
        if (insets != null) {
            left = insets.getSystemWindowInsetLeft(); right = insets.getSystemWindowInsetRight();
            top = insets.getSystemWindowInsetTop(); bottom = insets.getSystemWindowInsetBottom();
            if (Build.VERSION.SDK_INT >= 28 && insets.getDisplayCutout() != null) {
                left = Math.max(left, insets.getDisplayCutout().getSafeInsetLeft());
                right = Math.max(right, insets.getDisplayCutout().getSafeInsetRight());
                top = Math.max(top, insets.getDisplayCutout().getSafeInsetTop());
                bottom = Math.max(bottom, insets.getDisplayCutout().getSafeInsetBottom());
            }
        }
        Rect visible = new Rect();
        parent.getWindowVisibleDisplayFrame(visible);
        if (visible.isEmpty()) { int[] position=new int[2];parent.getLocationOnScreen(position);visible.set(position[0],position[1],position[0]+parent.getWidth(),position[1]+parent.getHeight()); }
        int[] location = new int[2]; parent.getLocationOnScreen(location);
        top = Math.max(top, Math.max(0, visible.top - location[1]));
        int usableBottom = Math.min(parent.getHeight() - bottom, visible.bottom - location[1]);
        int margin=dp(8);
        HubOverlayPlacement.Box safe=new HubOverlayPlacement.Box(left+margin,top+margin,
                Math.max(1,parent.getWidth()-left-right-2*margin),Math.max(1,usableBottom-top-2*margin));
        boolean minimalWindow = safe.height < dp(176);
        keyboardVisible = minimalWindow && ((Build.VERSION.SDK_INT >= 30 && insets != null && insets.isVisible(WindowInsets.Type.ime()))
                || parent.getHeight() - usableBottom > dp(100));
        String keyboardLabel = keyboardVisible ? "收起键盘" : "键盘";
        if (!keyboardLabel.contentEquals(keyboardEntry.getText())) keyboardEntry.setText(keyboardLabel);
        // A landscape IME can leave less room than the two fixed action rows.
        // Keep complete touch targets and the exit reachable; hide the keyboard
        // to restore tabs and scrolling controls instead of clipping them.
        tabRow.setVisibility(minimalWindow ? View.GONE : View.VISIBLE);
        body.setVisibility(minimalWindow ? View.GONE : View.VISIBLE);
        compactClose.setVisibility(minimalWindow ? View.VISIBLE : View.GONE);
        // Keep a useful scrolling viewport on phones in landscape or above a docked IME.
        boolean shortWindow = safe.height < dp(480);
        int footerDirection = shortWindow ? LinearLayout.HORIZONTAL : LinearLayout.VERTICAL;
        if (footer.getOrientation() != footerDirection) {
            footer.setOrientation(footerDirection);
            LinearLayout.LayoutParams fixedSize = new LinearLayout.LayoutParams(shortWindow ? 0 : -1, -2, shortWindow ? 2 : 0);
            fixedSize.topMargin = dp(8); fixed.setLayoutParams(fixedSize);
            LinearLayout.LayoutParams exitSize = new LinearLayout.LayoutParams(shortWindow ? 0 : -1, -2, shortWindow ? 1 : 0);
            exitSize.topMargin = dp(shortWindow ? 8 : 12); exitSize.leftMargin = dp(shortWindow ? 8 : 0);
            quickDisconnect.setLayoutParams(exitSize);
        }
        handleArea.setVisibility(shortWindow ? View.GONE : View.VISIBLE);
        int verticalPadding = dp(shortWindow ? 8 : 16);
        if (panel.getPaddingBottom() != verticalPadding) panel.setPadding(dp(16), dp(shortWindow ? 8 : 14), dp(16), verticalPadding);
        HubOverlayPlacement.Box anchor=HubOverlayPlacement.anchor(
                Math.round(customPosition&&!Float.isNaN(toolbarX)?toolbarX:safe.x+(safe.width-dp(88))/2f),
                Math.round(customPosition&&!Float.isNaN(toolbarY)?toolbarY:safe.y),
                dp(88),dp(48),safe);
        // Keep the user's preferred position. A temporary IME/rotation clamp
        // must not permanently move the floating control button.
        place(entry,anchor);
        HubOverlayPlacement.Box menu=HubOverlayPlacement.menu(anchor,dp(420),dp(minimalWindow ? 104 : 620),safe,dp(8),
                positionPrefs.getBoolean("adaptive_expand",true));
        place(panel,menu);
    }
    public void setPictureInPicture(boolean active) {
        pictureInPicture = active;
        if (active) collapse();
        else if (panel.getVisibility() != View.VISIBLE) entry.setVisibility(View.VISIBLE);
        if (!active && panel.getVisibility() == View.VISIBLE) quickDisconnect.setVisibility(View.VISIBLE);
    }

    private Context dialogContext() { return HubStreamUi.context(activity); }
    private void positionTextDialog(AlertDialog dialog, EditText text) {
        if (!dialog.isShowing() || dialog.getWindow() == null || parent.getWidth() <= 0 || parent.getHeight() <= 0) return;
        int left = 0, right = 0, top = 0, bottom = 0;
        WindowInsets insets = parent.getRootWindowInsets();
        if (insets != null) {
            left = insets.getSystemWindowInsetLeft(); right = insets.getSystemWindowInsetRight();
            top = insets.getSystemWindowInsetTop(); bottom = insets.getSystemWindowInsetBottom();
            if (Build.VERSION.SDK_INT >= 28 && insets.getDisplayCutout() != null) {
                left = Math.max(left, insets.getDisplayCutout().getSafeInsetLeft());
                right = Math.max(right, insets.getDisplayCutout().getSafeInsetRight());
                top = Math.max(top, insets.getDisplayCutout().getSafeInsetTop());
                bottom = Math.max(bottom, insets.getDisplayCutout().getSafeInsetBottom());
            }
        }
        Rect visible = new Rect(); parent.getWindowVisibleDisplayFrame(visible);
        int[] location = new int[2]; parent.getLocationOnScreen(location);
        top = Math.max(top, Math.max(0, visible.top - location[1]));
        int usableHeight = Math.min(parent.getHeight() - bottom, visible.bottom - location[1]) - top - dp(24);
        int editorHeight = Math.max(dp(48), Math.min(dp(parent.getWidth() > parent.getHeight() ? 88 : 120), usableHeight - dp(176)));
        int editorMinimum = Math.min(dp(72), editorHeight);
        if (text.getMinHeight() != editorMinimum) text.setMinHeight(editorMinimum);
        if (text.getMaxHeight() != editorHeight) text.setMaxHeight(editorHeight);
        // Floating IMEs may report zero bottom inset. Keep actions at the top,
        // rather than centering the dialog behind the keyboard's floating area.
        WindowManager.LayoutParams layout = dialog.getWindow().getAttributes();
        int width = Math.max(1, Math.min(dp(640), parent.getWidth() - left - right - dp(24)));
        int gravity = Gravity.TOP | Gravity.CENTER_HORIZONTAL;
        if (layout.width != width || layout.height != WindowManager.LayoutParams.WRAP_CONTENT || layout.gravity != gravity || layout.y != top + dp(12)) {
            layout.width = width; layout.height = WindowManager.LayoutParams.WRAP_CONTENT;
            layout.gravity = gravity; layout.y = top + dp(12);
            dialog.getWindow().setAttributes(layout);
        }
    }
    private void showTextDialog() {
        if (textDialog != null && textDialog.isShowing()) return;
        collapse();
        Context context = dialogContext();
        EditText text = new EditText(context);
        text.setHint("在这里输入中文或粘贴文本，然后发送到电脑当前光标位置");
        text.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_MULTI_LINE | InputType.TYPE_TEXT_FLAG_CAP_SENTENCES);
        text.setTextSize(16); text.setTextColor(TEXT); text.setHintTextColor(MUTED);
        text.setMinLines(2); text.setMaxLines(4); text.setGravity(Gravity.TOP);
        text.setPadding(dp(18), dp(12), dp(18), dp(12));
        text.setHorizontallyScrolling(false); text.setVerticalScrollBarEnabled(true);
        text.setOverScrollMode(View.OVER_SCROLL_IF_CONTENT_SCROLLS);
        text.setText(textDraft); text.setSelection(text.length());
        LinearLayout form = column(); form.setPadding(dp(18), dp(4), dp(18), dp(16));
        LinearLayout actions = new LinearLayout(context);
        Button cancel = action("取消", () -> { }, false);
        Button hideKeyboard = action("收起键盘", () -> ((InputMethodManager) activity.getSystemService(Context.INPUT_METHOD_SERVICE))
                .hideSoftInputFromWindow(text.getWindowToken(), 0), false);
        hideKeyboard.setContentDescription("收起当前文字输入键盘");
        Button send = action("发送", () -> { }, true);
        for (Button button : new Button[] {cancel, hideKeyboard, send}) {
            LinearLayout.LayoutParams size = new LinearLayout.LayoutParams(0, dp(48), 1);
            if (actions.getChildCount() > 0) size.leftMargin = dp(8);
            actions.addView(button, size);
        }
        form.addView(actions, new LinearLayout.LayoutParams(-1, dp(48)));
        LinearLayout.LayoutParams editorSize = new LinearLayout.LayoutParams(-1, -2);
        editorSize.topMargin = dp(12); form.addView(text, editorSize);
        AlertDialog dialog = HubStreamUi.dialog(activity).setTitle("发送文字到电脑").setView(form).create();
        final ViewTreeObserver.OnGlobalLayoutListener layoutText = () -> positionTextDialog(dialog, text);
        final android.os.IBinder[] inputWindow = new android.os.IBinder[1];
        textDialog = dialog;
        dialog.setOnShowListener(ignored -> {
            inputWindow[0] = text.getWindowToken();
            cancel.setOnClickListener(view -> dialog.dismiss());
            send.setEnabled(text.length() > 0);
            send.setOnClickListener(view -> {
                callbacks.sendText(text.getText().toString());
                text.setText(""); textDraft = "";
                dialog.dismiss();
            });
            text.addTextChangedListener(new TextWatcher() {
                @Override public void beforeTextChanged(CharSequence s, int start, int count, int after) { }
                @Override public void onTextChanged(CharSequence s, int start, int before, int count) { send.setEnabled(s.length() > 0); }
                @Override public void afterTextChanged(Editable s) { }
            });
            text.requestFocus();
            if (dialog.getWindow() != null) dialog.getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_STATE_ALWAYS_VISIBLE | WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
            positionTextDialog(dialog, text);
            if (dialog.getWindow() != null) dialog.getWindow().getDecorView().getViewTreeObserver().addOnGlobalLayoutListener(layoutText);
            text.postDelayed(() -> {
                if (dialog.isShowing()) ((InputMethodManager) activity.getSystemService(Context.INPUT_METHOD_SERVICE)).showSoftInput(text, InputMethodManager.SHOW_IMPLICIT);
            }, 100);
        });
        dialog.setOnDismissListener(ignored -> {
            if (dialog.getWindow() != null && dialog.getWindow().getDecorView().getViewTreeObserver().isAlive())
                dialog.getWindow().getDecorView().getViewTreeObserver().removeOnGlobalLayoutListener(layoutText);
            textDraft = text.getText().toString();
            InputMethodManager keyboard = (InputMethodManager) activity.getSystemService(Context.INPUT_METHOD_SERVICE);
            if (inputWindow[0] != null) keyboard.hideSoftInputFromWindow(inputWindow[0], 0);
            // A floating IME can transfer to the streaming window as the dialog
            // closes, without changing the visible frame used by Android layouts.
            callbacks.hideKeyboard();
            if (textDialog == dialog) textDialog = null;
        });
        dialog.show();
    }
    private void confirmDisconnect() {
        if (disconnectDialog != null && disconnectDialog.isShowing()) return;
        AlertDialog dialog = HubStreamUi.dialog(activity).setTitle("断开远程连接？")
                .setMessage("结束本次串流并返回设备页。")
                .setNegativeButton("取消", null)
                .setPositiveButton("断开", (ignored, which) -> { collapse(); callbacks.disconnect(); }).create();
        disconnectDialog = dialog;
        dialog.setOnDismissListener(ignored -> { if (disconnectDialog == dialog) disconnectDialog = null; });
        dialog.show();
        // The platform alert theme can render muted gray text over a gray
        // surface on some tablet skins. Apply an explicit high-contrast theme.
        if (dialog.getWindow() != null) dialog.getWindow().setBackgroundDrawable(shape(SURFACE, BORDER, 16));
        int titleId = activity.getResources().getIdentifier("alertTitle", "id", "android");
        TextView title = titleId == 0 ? null : dialog.findViewById(titleId);
        TextView message = dialog.findViewById(android.R.id.message);
        if (title != null) { title.setTextColor(TEXT); title.setTypeface(null, Typeface.BOLD); }
        if (message != null) message.setTextColor(TEXT);
        Button cancel = dialog.getButton(AlertDialog.BUTTON_NEGATIVE);
        Button disconnect = dialog.getButton(AlertDialog.BUTTON_POSITIVE);
        if (cancel != null) { cancel.setTextColor(ACCENT); cancel.setMinHeight(dp(48)); }
        if (disconnect != null) { disconnect.setTextColor(DANGER); disconnect.setMinHeight(dp(48)); }
    }
    public void dispose() {
        disposed = true;
        if (parent.getViewTreeObserver().isAlive()) parent.getViewTreeObserver().removeOnGlobalLayoutListener(layoutListener);
        if (textDialog != null) textDialog.dismiss();
        if (disconnectDialog != null) disconnectDialog.dismiss();
        clearModifiers();
        textDraft = "";
    }
    private int dp(int value) { return Math.round(value * activity.getResources().getDisplayMetrics().density); }
    private GradientDrawable shape(int fill, int stroke, int radius) {
        GradientDrawable shape = new GradientDrawable();
        shape.setColor(fill); shape.setCornerRadius(dp(radius)); shape.setStroke(dp(1), stroke);
        return shape;
    }
    private RippleDrawable buttonBackground(int fill, int border) {
        return new RippleDrawable(ColorStateList.valueOf(0x337FA8FF), shape(fill, border, 10), null);
    }

    /** A native grid that reflows whole buttons instead of hiding them offscreen. */
    private static final class AdaptiveGrid extends ViewGroup {
        private final int minimumCell, maxColumns, gap;
        private int columns = 1;
        AdaptiveGrid(Context context, int minimumCell, int maxColumns, int gap) {
            super(context); this.minimumCell = minimumCell; this.maxColumns = maxColumns; this.gap = gap;
        }
        @Override protected LayoutParams generateDefaultLayoutParams() { return new LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT); }
        @Override protected void onMeasure(int widthSpec, int heightSpec) {
            int width = MeasureSpec.getSize(widthSpec);
            if (MeasureSpec.getMode(widthSpec) == MeasureSpec.UNSPECIFIED) width = minimumCell * maxColumns + gap * (maxColumns - 1);
            int available = Math.max(1, width - getPaddingLeft() - getPaddingRight());
            columns = Math.max(1, Math.min(maxColumns, (available + gap) / (minimumCell + gap)));
            int cell = Math.max(1, (available - gap * (columns - 1)) / columns);
            int total = getPaddingTop() + getPaddingBottom(), rowHeight = 0;
            for (int i = 0; i < getChildCount(); i++) {
                View child = getChildAt(i);
                child.measure(MeasureSpec.makeMeasureSpec(cell, MeasureSpec.EXACTLY), MeasureSpec.makeMeasureSpec(0, MeasureSpec.UNSPECIFIED));
                rowHeight = Math.max(rowHeight, child.getMeasuredHeight());
                if (i % columns == columns - 1 || i == getChildCount() - 1) {
                    total += rowHeight;
                    if (i < getChildCount() - 1) total += gap;
                    rowHeight = 0;
                }
            }
            setMeasuredDimension(resolveSize(width, widthSpec), resolveSize(total, heightSpec));
        }
        @Override protected void onLayout(boolean changed, int left, int top, int right, int bottom) {
            int available = Math.max(1, right - left - getPaddingLeft() - getPaddingRight());
            int cell = Math.max(1, (available - gap * (columns - 1)) / columns);
            int y = getPaddingTop();
            for (int start = 0; start < getChildCount(); start += columns) {
                int height = 0;
                for (int i = start; i < Math.min(start + columns, getChildCount()); i++) height = Math.max(height, getChildAt(i).getMeasuredHeight());
                for (int i = start; i < Math.min(start + columns, getChildCount()); i++) {
                    int column = i - start;
                    if (getLayoutDirection() == View.LAYOUT_DIRECTION_RTL) column = columns - 1 - column;
                    int x = getPaddingLeft() + column * (cell + gap);
                    getChildAt(i).layout(x, y, x + cell, y + height);
                }
                y += height + gap;
            }
        }
    }
}
