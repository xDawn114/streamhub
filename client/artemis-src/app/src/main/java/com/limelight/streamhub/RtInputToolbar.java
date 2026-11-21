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
        default boolean audioAvailable() { return false; }
        default boolean audioMuted() { return false; }
        default void toggleAudio() { }
        default boolean viewOnly() { return false; }
        default void toggleViewOnly() { }
        default boolean pipAvailable() { return false; }
        default void enterPip() { }
        default boolean fullscreen(){return true;}
        default void toggleFullscreen(){}
        default boolean remoteDesktop(){return false;}
        default Rect videoBounds() { return null; }
        default void showMouseMode() { }
        default boolean trackpadMode(){return false;}
        default void setTrackpadMode(boolean value){}
        default void showDisplaySettings() { }
        default void showResolutionSettings() { }
        default void showPrivacySettings() { }
        default boolean privacyAvailable() { return false; }
        default void rotateScreen() { }
        default void toggleZoom() { }
        default boolean zoomEnabled() { return false; }
        default float zoomScale() { return 1f; }
        default void resetZoom() { }
        default void scaleZoom(float factor) { }
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
    private static final int CONTROL_GAP = 8;
    private final Activity activity;
    private final FrameLayout parent;
    private final Callbacks callbacks;
    private final LinearLayout panel, pages, fixed, footer, tabRow;
    private final android.widget.HorizontalScrollView quickActions;
    private final Button audioQuick, watchQuick, desktopQuick, windowsQuick;
    private final Button diagnosticsQuick;
    private final HubControlGlyph diagnosticGlyph = new HubControlGlyph("diagnostics", TEXT);
    private final Button directTouch,trackpadTouch;
    private final HubControlGlyph entryGlyph = new HubControlGlyph("controls", TEXT);
    private final Runnable fadeEntry = this::fadeEntry;
    private final FrameLayout handleArea;
    private final ScrollView body;
    private final Button entry, quickDisconnect, statsButton, keyboardEntry, compactClose;
    private final android.widget.Switch viewportSwitch;
    private final LinearLayout viewportControls,importedShortcuts;
    private String importedValue;
    private final TextView viewportLabel, viewportHint;
    private final Button viewportSmaller, viewportLarger, viewportReset, viewportDone, resetView;
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
    private boolean adjustingViewport, updatingViewport;
    private float viewportScale = 1f;
    private final SharedPreferences positionPrefs;
    private boolean customPosition;
    private float toolbarX = Float.NaN, toolbarY = Float.NaN;
    private float dragDownX, dragDownY, dragOriginX, dragOriginY;
    private boolean dragging,draggingPanel;
    private float floatingX=Float.NaN,floatingY=Float.NaN;
    private int selectedTab;
    private final int[] scrollPositions = new int[3];

    public RtInputToolbar(Activity activity, FrameLayout parent, Callbacks callbacks) {
        this.activity = activity;
        this.parent = parent;
        this.callbacks = callbacks;
        positionPrefs = activity.getSharedPreferences("streamhub_controls", Context.MODE_PRIVATE);
        floatingX=positionPrefs.getFloat("panel_x",Float.NaN);floatingY=positionPrefs.getFloat("panel_y",Float.NaN);
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

        quickActions = new android.widget.HorizontalScrollView(activity);
        quickActions.setFillViewport(true);
        LinearLayout quickRow = new LinearLayout(activity);
        quickActions.addView(quickRow);
        quickAction(quickRow,"退出","exit",this::confirmDisconnect).setTextColor(DANGER);
        diagnosticsQuick = quickAction(quickRow, "诊断", diagnosticGlyph, () -> { collapse(); callbacks.showDiagnostics(); });
        diagnosticsQuick.setTooltipText("查看串流诊断");
        quickAction(quickRow,"旋转","rotate",() -> {collapse();callbacks.rotateScreen();});
        audioQuick=quickAction(quickRow,"声音","audio",() -> {callbacks.toggleAudio();refreshQuickState();});
        audioQuick.setEnabled(callbacks.audioAvailable());
        if(!callbacks.audioAvailable())audioQuick.setContentDescription("本次声音仅在电脑输出；可在音频输出设置中选择下次输出设备");
        watchQuick=quickAction(quickRow,"仅观看","watch",() -> {callbacks.toggleViewOnly();refreshQuickState();});
        Button pipQuick=quickAction(quickRow,"小窗","pip",() -> {collapse();callbacks.enterPip();});
        pipQuick.setEnabled(callbacks.pipAvailable());
        quickAction(quickRow,"全屏","fullscreen",() -> {callbacks.toggleFullscreen();refreshLayout();});
        panel.addView(quickActions,new LinearLayout.LayoutParams(-1,-2));

        tabRow = new LinearLayout(activity);
        tabRow.setPadding(0, dp(CONTROL_GAP), 0, dp(CONTROL_GAP));
        String[] labels = {"快捷键", "输入", "画面"};
        for (int i = 0; i < labels.length; i++) {
            final int index = i;
            Button tab = action(labels[i], () -> selectTab(index), false);
            tabs[i] = tab;
            tab.setContentDescription(labels[i] + "，电脑控制选项");
            addRowControl(tabRow, tab, 0, -2, 1);
        }
        Button close = action("收起", this::collapse, false);
        close.setContentDescription("收起控制面板");
        addRowControl(tabRow, close, dp(56), -2, 0);
        panel.addView(tabRow);

        body = new ScrollView(activity);
        body.setFillViewport(false);
        body.setClipToPadding(true);
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
        sectionHeading(input,"触控模式");AdaptiveGrid modes=grid(input,128,2,0);
        directTouch=action("屏幕触控",()->{callbacks.setTrackpadMode(false);refreshQuickState();},false);
        trackpadTouch=action("鼠标指针",()->{callbacks.setTrackpadMode(true);refreshQuickState();},false);
        modes.addView(directTouch);modes.addView(trackpadTouch);
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
        AdaptiveGrid combinations = grid(shortcuts, 128, 2, 0);
        shortcut(combinations, "切换窗口 · Alt+Tab", KeyboardTranslator.VK_LMENU, KeyboardTranslator.VK_TAB);
        shortcut(combinations, "窗口列表 · Ctrl+Alt+Tab", KeyboardTranslator.VK_LCONTROL, KeyboardTranslator.VK_LMENU, KeyboardTranslator.VK_TAB);
        shortcut(combinations, "任务视图 · Win+Tab", KeyboardTranslator.VK_LWIN, KeyboardTranslator.VK_TAB);
        shortcut(combinations, "复制 · Ctrl+C", KeyboardTranslator.VK_LCONTROL, KeyboardTranslator.VK_C);
        shortcut(combinations, "粘贴 · Ctrl+V", KeyboardTranslator.VK_LCONTROL, KeyboardTranslator.VK_V);
        shortcut(combinations, "全选 · Ctrl+A", KeyboardTranslator.VK_LCONTROL, KeyboardTranslator.VK_A);
        shortcut(combinations, "撤销 · Ctrl+Z", KeyboardTranslator.VK_LCONTROL, 'Z');
        shortcut(combinations, "保存 · Ctrl+S", KeyboardTranslator.VK_LCONTROL, KeyboardTranslator.VK_S);
        shortcut(combinations, "显示桌面 · Win+D", KeyboardTranslator.VK_LWIN, KeyboardTranslator.VK_D);
        shortcut(combinations, "任务管理器", KeyboardTranslator.VK_LCONTROL, KeyboardTranslator.VK_LSHIFT, KeyboardTranslator.VK_ESCAPE);
        importedShortcuts=column();shortcuts.addView(importedShortcuts);refreshImportedShortcuts();
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
        viewportSwitch = new HubSwitch(activity,true);
        viewportSwitch.setText("调整视图（暂停触控）");
        viewportSwitch.setTextColor(TEXT); viewportSwitch.setTextSize(14); viewportSwitch.setMinHeight(dp(48));
        viewportSwitch.setOnCheckedChangeListener((view, enabled) -> {
            if (!updatingViewport && enabled != callbacks.zoomEnabled()) {
                collapse(); callbacks.toggleZoom();
                setViewportState(callbacks.zoomEnabled(), callbacks.zoomScale());
            }
        });
        addWide(settings, viewportSwitch, 8);
        addHint(settings, "开启后单指拖动、双指缩放，触屏不操作电脑。点“完成”保留放大位置并恢复点击。", 6);
        resetView = action("还原画面 · 100%", () -> { callbacks.resetZoom(); setViewportState(callbacks.zoomEnabled(), callbacks.zoomScale()); }, false);
        addWide(settings, resetView, 8);
        addWide(settings, action("当前分辨率", () -> { collapse(); callbacks.showResolutionSettings(); }, false), 8);
        if (callbacks.privacyAvailable()) addWide(settings, action("本机隐私屏", () -> { collapse(); callbacks.showPrivacySettings(); }, false), 8);
        sectionHeading(settings, "性能与声音");
        statsButton = action("显示性能信息", callbacks::toggleStats, false);
        addWide(settings, statsButton);
        addWide(settings, action("查看串流诊断", () -> { collapse(); callbacks.showDiagnostics(); }, false), 8);
        addWide(settings, action("音频输出", () -> { collapse(); callbacks.showAudioSettings(); }, false), 8);
        addWide(settings, action("串流提示", callbacks::showWarningSettings, false), 8);
        if (callbacks.privacyAvailable()) addWide(settings, action("锁屏与退出", callbacks::showLockSettings, false), 8);
        Button panelLayout=action("面板展开方式 · "+HubPanelLayout.label(activity),()->{},false);
        panelLayout.setOnClickListener(v->HubPanelLayout.show(activity,true,()->{
            panelLayout.setText("面板展开方式 · "+HubPanelLayout.label(activity));refreshLayout();
        }));addWide(settings,panelLayout,8);
        addHint(settings,"侧边模式适合横屏；黑边不足时停靠底部；自由浮动可拖动顶部手柄。",6);
        sectionHeading(settings, "下次连接");
        addWide(settings, action("画质与编码设置", () -> { collapse(); callbacks.showDisplaySettings(); }, false));
        addHint(settings, "可在串流中选择画质预设，下次连接生效。", 6);

        footer = new LinearLayout(activity); footer.setOrientation(LinearLayout.VERTICAL);
        fixed = new LinearLayout(activity);
        keyboardEntry=action("键盘", () -> { collapse(); if (keyboardVisible) callbacks.hideKeyboard(); else callbacks.showKeyboard(); }, false);
        Button clipboardEntry=action("剪贴板", this::showClipboardActions, false);
        windowsQuick=action("窗口",()->callbacks.sendKeys(new short[]{KeyboardTranslator.VK_LWIN,KeyboardTranslator.VK_TAB}),false);
        desktopQuick=action("桌面",()->callbacks.sendKeys(new short[]{KeyboardTranslator.VK_LWIN,KeyboardTranslator.VK_D}),false);
        for (Button action : new Button[]{windowsQuick, desktopQuick, keyboardEntry, clipboardEntry}) {
            addRowControl(fixed, action, 0, -2, 1);
        }
        LinearLayout.LayoutParams fixedSize=new LinearLayout.LayoutParams(-1,-2);fixedSize.topMargin=dp(CONTROL_GAP);footer.addView(fixed,fixedSize);
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

        entry = action("", this::show, false);
        entryGlyph.setBounds(0,0,dp(24),dp(24));
        entry.setCompoundDrawables(null,entryGlyph,null,null);
        entry.setPadding(dp(12),dp(12),dp(12),dp(12));
        entry.setTextSize(14);
        entry.setTypeface(null, Typeface.BOLD);
        entry.setContentDescription("展开电脑控制：输入、快捷键和画面设置");
        entry.setElevation(dp(20));
        entry.setBackground(new RippleDrawable(ColorStateList.valueOf(0x332F59D6),shape(0xB31A212C,0x669DAFC7,24),null));
        entry.setOnTouchListener(this::handleDrag);
        parent.addView(entry, new FrameLayout.LayoutParams(dp(48), dp(48), Gravity.TOP | Gravity.CENTER_HORIZONTAL));

        viewportControls = column();
        HubStreamUi.keepColors(viewportControls);
        viewportControls.setPadding(dp(12), dp(8), dp(12), dp(12));
        viewportControls.setBackground(shape(SURFACE, BORDER, 16));
        viewportControls.setElevation(dp(20)); viewportControls.setClickable(true);
        viewportControls.setVisibility(View.GONE);
        LinearLayout viewportHeader = column();
        viewportHeader.setMinimumHeight(dp(48));
        viewportHeader.setGravity(Gravity.CENTER_VERTICAL);
        viewportHeader.setContentDescription("拖动缩放调整条");
        viewportHeader.setOnTouchListener(this::handleDrag);
        viewportLabel = text("调整视图 · 100%", 14, TEXT);
        viewportLabel.setTypeface(null, Typeface.BOLD);
        viewportHeader.addView(viewportLabel);
        viewportHint = text("单指拖动 · 双指缩放 · 不操作电脑", 12, MUTED);
        viewportHeader.addView(viewportHint);
        viewportControls.addView(viewportHeader, new LinearLayout.LayoutParams(-1, -2));
        AdaptiveGrid viewportActions = grid(viewportControls, 48, 4, 8);
        viewportSmaller = action("−", () -> changeViewportScale(0.8f), false);
        viewportSmaller.setContentDescription("缩小本机画面");
        viewportLarger = action("＋", () -> changeViewportScale(1.25f), false);
        viewportLarger.setContentDescription("放大本机画面，最大四倍");
        viewportReset = action("还原", () -> { callbacks.resetZoom(); setViewportState(callbacks.zoomEnabled(), callbacks.zoomScale()); }, false);
        viewportReset.setContentDescription("还原画面至100%，继续调整");
        viewportDone = action("完成", () -> { callbacks.toggleZoom(); setViewportState(callbacks.zoomEnabled(), callbacks.zoomScale()); }, true);
        viewportDone.setContentDescription("保留当前缩放，恢复触屏控制电脑");
        for (Button button : new Button[] {viewportSmaller, viewportLarger, viewportReset, viewportDone}) viewportActions.addView(button);
        parent.addView(viewportControls, new FrameLayout.LayoutParams(dp(400), -2, Gravity.TOP | Gravity.LEFT));
        setViewportState(callbacks.zoomEnabled(), callbacks.zoomScale());
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
        parent.post(this::refreshLayout); wakeEntry();
    }

    private void fadeEntry(){ if(!disposed && entry.getVisibility()==View.VISIBLE)entry.setAlpha(0.45f); }
    private void wakeEntry(){entry.setAlpha(0.95f);entry.removeCallbacks(fadeEntry);entry.postDelayed(fadeEntry,4000);}
    private Button quickAction(LinearLayout row,String label,String glyph,Runnable callback) {
        return quickAction(row, label, new HubControlGlyph(glyph, "exit".equals(glyph) ? DANGER : TEXT), callback);
    }
    private Button quickAction(LinearLayout row, String label, HubControlGlyph icon, Runnable callback) {
        Button button=action(label,callback,false);button.setTextSize(12);
        button.setPadding(dp(4), dp(8), dp(4), dp(8));
        icon.setBounds(0,0,dp(20),dp(20));button.setCompoundDrawables(null,icon,null,null);
        button.setCompoundDrawablePadding(dp(4));button.setBackground(buttonBackground(CARD,CARD));
        int width = Math.max(dp(50), (int)Math.ceil(button.getTextSize() * 3) + dp(8));
        addRowControl(row, button, width, dp(64), 1);
        return button;
    }
    private void addRowControl(LinearLayout row, Button button, int width, int height, float weight) {
        LinearLayout.LayoutParams size = new LinearLayout.LayoutParams(width, height, weight);
        if (row.getChildCount() > 0) size.setMarginStart(dp(CONTROL_GAP));
        row.addView(button, size);
    }
    private void refreshImportedShortcuts(){
        String json=activity.getSharedPreferences(HubShortcutBindings.PREFS,Context.MODE_PRIVATE).getString(HubShortcutBindings.KEY,"");
        if(json.equals(importedValue))return;importedValue=json;importedShortcuts.removeAllViews();
        if(json.isEmpty())return;
        try{
            List<HubShortcutBindings.Binding> bindings=HubShortcutBindings.parse(json);if(bindings.isEmpty())return;
            sectionHeading(importedShortcuts,"导入的快捷键");AdaptiveGrid grid=grid(importedShortcuts,128,2,0);
            for(HubShortcutBindings.Binding binding:bindings)shortcut(grid,binding.name,binding.keys);
        }catch(Exception error){com.limelight.LimeLog.warning("Custom shortcuts: "+error);addHint(importedShortcuts,"已保存的快捷键文件格式无效，请在设置中重新导入。",8);}
    }
    private void refreshQuickState(){
        boolean audible=callbacks.audioAvailable()&&!callbacks.audioMuted(),viewOnly=callbacks.viewOnly();
        audioQuick.setSelected(audible);audioQuick.setText(audible?"声音开":"声音关");
        audioQuick.setBackground(buttonBackground(audible?HubStreamUi.PRIMARY:CARD,audible?HubStreamUi.PRIMARY:CARD));
        watchQuick.setSelected(viewOnly);watchQuick.setText(viewOnly?"观看中":"仅观看");
        watchQuick.setBackground(buttonBackground(viewOnly?HubStreamUi.PRIMARY:CARD,viewOnly?HubStreamUi.PRIMARY:CARD));
        boolean trackpad=callbacks.trackpadMode();directTouch.setSelected(!trackpad);trackpadTouch.setSelected(trackpad);
        directTouch.setBackground(buttonBackground(!trackpad?SELECTED:CARD,!trackpad?ACCENT:BORDER));
        trackpadTouch.setBackground(buttonBackground(trackpad?SELECTED:CARD,trackpad?ACCENT:BORDER));
    }
    public void setWarning(boolean visible,String detail){
        entryGlyph.warning(visible);
        diagnosticGlyph.warning(visible);
        diagnosticsQuick.setTextColor(visible ? HubStreamUi.WARNING : TEXT);
        String label = visible ? detail + " · 查看串流诊断" : "查看串流诊断";
        if (!label.contentEquals(diagnosticsQuick.getContentDescription() == null ? "" : diagnosticsQuick.getContentDescription())) {
            diagnosticsQuick.setContentDescription(label);
            diagnosticsQuick.setTooltipText(label);
        }
        entry.setContentDescription("展开电脑控制：输入、快捷键和画面设置"+(visible?"，"+detail:""));
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
        AdaptiveGrid grid = new AdaptiveGrid(activity, dp(minimumCell), maxColumns, dp(CONTROL_GAP));
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
        button.setTextColor(new ColorStateList(new int[][]{{-android.R.attr.state_enabled},{}},new int[]{MUTED,primary?0xFFFFFFFF:TEXT}));
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
            if (codes[codes.length - 1] == KeyboardTranslator.VK_TAB) collapse();
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
        refreshQuickState();
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
                wakeEntry();
                dragDownX=event.getRawX();dragDownY=event.getRawY();
                // Start a new drag from the visible anchor, which may currently
                // be clamped by the keyboard or a different orientation.
                draggingPanel=view==handleArea && HubPanelLayout.FLOAT.equals(HubPanelLayout.mode(activity));
                FrameLayout.LayoutParams anchor=(FrameLayout.LayoutParams)
                        (draggingPanel?panel:view.getParent() == viewportControls ? viewportControls : entry).getLayoutParams();
                dragOriginX=anchor.leftMargin;
                dragOriginY=anchor.topMargin;
                dragging=false;return true;
            case MotionEvent.ACTION_MOVE:
                float dx=event.getRawX()-dragDownX,dy=event.getRawY()-dragDownY;
                if(!dragging && Math.hypot(dx,dy)<dp(8))return true;
                dragging=true;
                if(draggingPanel){floatingX=dragOriginX+dx;floatingY=dragOriginY+dy;refreshLayout();}
                else setToolbarPosition(dragOriginX+dx,dragOriginY+dy,false);return true;
            case MotionEvent.ACTION_UP:
                if(dragging){
                    if(draggingPanel)positionPrefs.edit().putFloat("panel_x",floatingX).putFloat("panel_y",floatingY).apply();
                    else setToolbarPosition(toolbarX,toolbarY,true);
                }
                else if(view==entry)show();
                dragging=false;return true;
            case MotionEvent.ACTION_CANCEL:
                if(dragging){
                    if(draggingPanel)positionPrefs.edit().putFloat("panel_x",floatingX).putFloat("panel_y",floatingY).apply();
                    else setToolbarPosition(toolbarX,toolbarY,true);
                }
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
        setViewportState(callbacks.zoomEnabled(), callbacks.zoomScale());
        if (adjustingViewport) return;
        refreshLayout();
        HubMotion.showPanel(activity, panel); refreshQuickState(); refreshImportedShortcuts();
        entry.setVisibility(View.GONE);
        quickDisconnect.setVisibility(View.VISIBLE);
        refreshLayout();
    }
    private void collapse() {
        HubMotion.hidePanel(activity, panel, () -> {
            entry.setVisibility(pictureInPicture || disposed || adjustingViewport ? View.GONE : View.VISIBLE);
            quickDisconnect.setVisibility(View.GONE);
            clearModifiers(); wakeEntry();
        });
    }
    /** True means the Back action closed local controls and should not leave the stream. */
    public boolean collapseOrConsumeBack() {
        if (textDialog != null && textDialog.isShowing()) { textDialog.dismiss(); return true; }
        if (disconnectDialog != null && disconnectDialog.isShowing()) { disconnectDialog.dismiss(); return true; }
        if (adjustingViewport) {
            callbacks.toggleZoom(); setViewportState(callbacks.zoomEnabled(), callbacks.zoomScale()); return true;
        }
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

    private void changeViewportScale(float factor) {
        callbacks.scaleZoom(factor);
        setViewportState(callbacks.zoomEnabled(), callbacks.zoomScale());
    }
    public void setViewportState(boolean adjusting, float scale) {
        boolean modeChanged = adjustingViewport != adjusting;
        adjustingViewport = adjusting;
        viewportScale = Float.isNaN(scale) || Float.isInfinite(scale) ? 1f : Math.max(1f, Math.min(4f, scale));
        updatingViewport = true;
        try { viewportSwitch.setChecked(adjusting); } finally { updatingViewport = false; }
        String label = "调整视图 · " + Math.round(viewportScale * 100) + "%";
        if (!label.contentEquals(viewportLabel.getText())) viewportLabel.setText(label);
        String resetLabel = "还原画面 · 当前 " + Math.round(viewportScale * 100) + "%";
        if (!resetLabel.contentEquals(resetView.getText())) resetView.setText(resetLabel);
        viewportSmaller.setEnabled(viewportScale > 1.005f);
        viewportLarger.setEnabled(viewportScale < 3.995f);
        viewportReset.setEnabled(viewportScale > 1.005f);
        resetView.setEnabled(viewportScale > 1.005f);
        viewportControls.setVisibility(adjusting && !pictureInPicture && !disposed ? View.VISIBLE : View.GONE);
        if (adjusting) { HubMotion.hideImmediately(panel); quickDisconnect.setVisibility(View.GONE); }
        entry.setVisibility(!adjusting && !pictureInPicture && !disposed && panel.getVisibility() != View.VISIBLE ? View.VISIBLE : View.GONE);
        if (modeChanged) refreshLayout();
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
        HubOverlayPlacement.Box anchor=HubOverlayPlacement.anchor(
                Math.round(customPosition&&!Float.isNaN(toolbarX)?toolbarX:safe.x+(safe.width-dp(48))),
                Math.round(customPosition&&!Float.isNaN(toolbarY)?toolbarY:safe.y+dp(12)),
                dp(48),dp(48),safe);
        Rect video=callbacks.videoBounds();
        HubOverlayPlacement.Box picture=video==null||video.isEmpty()?null:new HubOverlayPlacement.Box(video.left,video.top,video.width(),video.height());
        String expansion=HubPanelLayout.mode(activity);
        int menuHeight=dp(minimalWindow?104:620);
        HubOverlayPlacement.Box menu=HubPanelLayout.SIDE.equals(expansion)
            ?HubOverlayPlacement.sidePanel(anchor,dp(440),menuHeight,safe,dp(8)):
            HubPanelLayout.FLOAT.equals(expansion)
            ?HubOverlayPlacement.anchor(Math.round(Float.isNaN(floatingX)?safe.x+(safe.width-Math.min(dp(440),safe.width))/2f:floatingX),
                Math.round(Float.isNaN(floatingY)?safe.y+(safe.height-Math.min(menuHeight,safe.height))/2f:floatingY),dp(440),menuHeight,safe)
            :HubOverlayPlacement.blackBars(dp(440),menuHeight,safe,picture,dp(280),callbacks.remoteDesktop());
        // A landscape IME can leave less room than the two fixed action rows.
        // Keep complete touch targets and the exit reachable; hide the keyboard
        // to restore tabs and scrolling controls instead of clipping them.
        tabRow.setVisibility(minimalWindow ? View.GONE : View.VISIBLE);
        quickActions.setVisibility(minimalWindow ? View.GONE : View.VISIBLE);
        quickDisconnect.setVisibility(panel.getVisibility()==View.VISIBLE && minimalWindow ? View.VISIBLE : View.GONE);
        desktopQuick.setVisibility(minimalWindow?View.GONE:View.VISIBLE);
        windowsQuick.setVisibility(minimalWindow?View.GONE:View.VISIBLE);
        LinearLayout.LayoutParams keyboardSize=(LinearLayout.LayoutParams)keyboardEntry.getLayoutParams();
        int keyboardStart=dp(minimalWindow ? 0 : CONTROL_GAP);
        if(keyboardSize.getMarginStart()!=keyboardStart){keyboardSize.setMarginStart(keyboardStart);keyboardEntry.setLayoutParams(keyboardSize);}
        body.setVisibility(minimalWindow ? View.GONE : View.VISIBLE);
        compactClose.setVisibility(minimalWindow ? View.VISIBLE : View.GONE);
        // Keep a useful scrolling viewport on phones in landscape or above a docked IME.
        boolean shortWindow = menu.height < dp(480);
        LinearLayout quickRow=(LinearLayout)quickActions.getChildAt(0);
        for(int i=0;i<quickRow.getChildCount();i++){
            Button quick=(Button)quickRow.getChildAt(i);LinearLayout.LayoutParams size=(LinearLayout.LayoutParams)quick.getLayoutParams();
            int height=dp(shortWindow?52:64);
            if(size.height!=height){size.height=height;quick.setLayoutParams(size);quick.setPadding(dp(4),dp(shortWindow?4:8),dp(4),dp(shortWindow?4:8));}
        }
        int footerDirection = shortWindow ? LinearLayout.HORIZONTAL : LinearLayout.VERTICAL;
        if (footer.getOrientation() != footerDirection) {
            footer.setOrientation(footerDirection);
            LinearLayout.LayoutParams fixedSize = new LinearLayout.LayoutParams(shortWindow ? 0 : -1, -2, shortWindow ? 2 : 0);
            fixedSize.topMargin = dp(8); fixed.setLayoutParams(fixedSize);
            LinearLayout.LayoutParams exitSize = new LinearLayout.LayoutParams(shortWindow ? 0 : -1, -2, shortWindow ? 1 : 0);
            exitSize.topMargin = dp(shortWindow ? 8 : 12); exitSize.leftMargin = dp(shortWindow ? 8 : 0);
            quickDisconnect.setLayoutParams(exitSize);
        }
        boolean floating=HubPanelLayout.FLOAT.equals(HubPanelLayout.mode(activity));
        handleArea.setVisibility(minimalWindow || (shortWindow&&!floating) ? View.GONE : View.VISIBLE);
        handleArea.setContentDescription(floating?"拖动自由浮动面板":"控制面板");
        handleArea.setOnTouchListener(floating?this::handleDrag:null);
        int verticalPadding = dp(shortWindow ? 8 : 16);
        if (panel.getPaddingBottom() != verticalPadding) panel.setPadding(dp(16), dp(shortWindow ? 8 : 14), dp(16), verticalPadding);
        // Keep the user's preferred position. A temporary IME/rotation clamp
        // must not permanently move the floating control button.
        place(entry,anchor);
        int viewportWidth = Math.min(dp(400), safe.width);
        viewportControls.measure(View.MeasureSpec.makeMeasureSpec(viewportWidth, View.MeasureSpec.EXACTLY),
                View.MeasureSpec.makeMeasureSpec(0, View.MeasureSpec.UNSPECIFIED));
        HubOverlayPlacement.Box viewportBounds = HubOverlayPlacement.anchor(anchor.x, anchor.y,
                viewportWidth, viewportControls.getMeasuredHeight(), safe);
        place(viewportControls, viewportBounds);
        place(panel,menu);
    }
    public void setPictureInPicture(boolean active) {
        pictureInPicture = active;
        if (active) collapse();
        else if (panel.getVisibility() != View.VISIBLE && !adjustingViewport) entry.setVisibility(View.VISIBLE);
        viewportControls.setVisibility(!active && adjustingViewport && !disposed ? View.VISIBLE : View.GONE);
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
        entry.removeCallbacks(fadeEntry);
        disposed = true;
        viewportControls.setVisibility(View.GONE);
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
