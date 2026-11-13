package com.limelight.streamhub;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Intent;
import android.graphics.Color;
import android.graphics.Point;
import android.graphics.Rect;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.text.InputType;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.Surface;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.RadioGroup;
import android.widget.RadioButton;
import android.graphics.drawable.RippleDrawable;
import android.widget.TextView;
import android.widget.Switch;
import android.content.pm.PackageManager;
import android.Manifest;
import android.os.Build;
import android.content.res.Configuration;
import org.json.JSONObject;
import com.limelight.PcView;
import com.limelight.R;
import com.limelight.preferences.StreamSettings;
import com.limelight.profiles.ProfilesManager;
import com.limelight.streamhub.networking.EmbeddedNetwork;
import com.limelight.streamhub.networking.EmbeddedVpnService;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;
import java.util.Collections;

/** One device entry, shared trust, remote desktop and tablet display in the same APK. */
public final class HubActivity extends Activity {
    private static final int REQUEST_AUTO_VPN = 83;
    private int ACCENT, ACCENT_DARK, TEXT, MUTED, BACKGROUND, SURFACE, BORDER, FIELD;
    private int SELECTED, SUCCESS, WARNING, DANGER, ON_ACCENT, RIPPLE;
    private final Handler main = new Handler(Looper.getMainLooper());
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final ExecutorService presenceWorker = Executors.newSingleThreadExecutor();
    private HubStore store;
    private HubUpdates updates;
    private final Runnable checkAppUpdates = () -> { if (this.foreground && !this.busy && updates != null) updates.maybeCheck(); };
    private TextView status, computerName, computerDetail, networkStatus;
    private TextView lastConnectionSummary, networkDetails;
    private TextView deviceState;
    private View deviceStateDot;
    private boolean computerOnline, lastOperationFailed;
    private boolean presenceChecking;
    private long presenceValidUntil, nextPresenceAt, operationGeneration;
    private String presenceNetwork = "", mediaState = "", presenceHost = "";
    private volatile HubSessionCoordinator presenceCoordinator;
    private EditText hosts, invitation;
    private FrameLayout pageRoot;
    private Spinner orientation;
    private RadioGroup provider;
    private EditText providerServer, providerManual;
    private TextView providerSaved, providerMessage;
    private LinearLayout providerServerPanel, providerManualPanel, remoteAccount;
    private LinearLayout content, peerList, bindingForm;
    private FrameLayout pageSlot;
    private HubNavigation navigation;
    private final List<Button> actions = new ArrayList<>();
    private boolean busy, foreground, networkChecking;
    private volatile HubSessionCoordinator activeCoordinator;
    private Button cancelConnection, applyProvider;
    private Button loginAccountButton, authorizationButton, copyAuthorizationButton, connectNetworkButton,
            disconnectNetworkButton, logoutAccountButton;
    private TextView bindingSummary;
    private LinearLayout nearbyList;
    private TextView nearbyStatus;
    private HubLanDiscovery lanDiscovery;
    private volatile HubLocalBinding localBinding;
    private final java.util.concurrent.atomic.AtomicBoolean cancelLocalBinding = new java.util.concurrent.atomic.AtomicBoolean();
    private Switch receiverSwitch;
    private AlertDialog receiverDialog;
    private String receiverDialogId = "";
    private final List<View> modeActions = new ArrayList<>();
    private String authorizationUrl = "";
    private EmbeddedNetwork network;
    private volatile FallbackWait pendingFallback;
    private boolean revealNetworkDetails, showOfflinePeers;
    private EmbeddedNetwork.Status lastNetworkState;
    private int currentPage, selectedOrientationIndex;
    private static final class FallbackWait {
        final CountDownLatch done = new CountDownLatch(1);
        volatile List<String> hosts = Collections.emptyList();
        volatile Exception error;
        volatile boolean completed;
        HubConnectBudget budget;
        HubSessionCoordinator owner;
    }
    private void refreshPalette() {
        ACCENT = HubTheme.primary(this); ACCENT_DARK = HubTheme.accent(this);
        TEXT = HubTheme.text(this); MUTED = HubTheme.muted(this);
        BACKGROUND = HubTheme.background(this); SURFACE = HubTheme.surface(this);
        BORDER = HubTheme.border(this); FIELD = HubTheme.field(this);
        SELECTED = HubTheme.selected(this); SUCCESS = HubTheme.success(this);
        WARNING = HubTheme.warning(this); DANGER = HubTheme.danger(this);
        ON_ACCENT = HubTheme.onAccent(this); RIPPLE = HubTheme.ripple(this);
        getWindow().setStatusBarColor(BACKGROUND); getWindow().setNavigationBarColor(BACKGROUND);
        int flags = HubTheme.isDark(this) ? 0 : View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && !HubTheme.isDark(this)) flags |= View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR;
        getWindow().getDecorView().setSystemUiVisibility(flags); HubStreamUi.keepColors(getWindow().getDecorView());
        applyWindowSurface();
    }
    /** Keep activity transitions on the same themed surface instead of the platform black window. */
    private void applyWindowSurface() {
        getWindow().setBackgroundDrawable(new android.graphics.drawable.ColorDrawable(BACKGROUND));
        getWindow().getDecorView().setBackgroundColor(BACKGROUND);
    }
    private final Runnable poll = new Runnable() { @Override public void run() { if (foreground) { refreshNetwork(); refreshComputerPresence(); main.postDelayed(this, 3000); } } };

    @Override public void onCreate(Bundle state) {
        super.onCreate(state); HubNavigation.prepareWindow(this); store = new HubStore(this); updates = HubUpdates.get(this); network = EmbeddedNetwork.get(this); refreshPalette();
        build();
        if (HubReceiverService.isEnabled(this) && store.bound()) HubReceiverService.enable(this, true);
        handleReceiverIntent(getIntent());
        if (getIntent().hasExtra("HubPage")) showPage(getIntent().getIntExtra("HubPage", 0));
        if (getIntent().getBooleanExtra("HubReconnect", false)) {
            int degrees = getIntent().getIntExtra("HubOrientation", 0);
            String reconnectLink = getIntent().getStringExtra("HubLink");
            String reconnectMode = getIntent().getStringExtra("HubReconnectMode");
            String reconnectLease = getIntent().getStringExtra("HubLeaseId");
            String reconnectRequest = getIntent().getStringExtra("HubRequestId");
            if (reconnectMode == null || reconnectMode.isEmpty()) reconnectMode = "tablet";
            final String mode = reconnectMode;
            final String lease = reconnectLease == null ? "" : reconnectLease;
            final String request = reconnectRequest == null ? "" : reconnectRequest;
            final int targetWidth=getIntent().getIntExtra("HubTargetWidth",0), targetHeight=getIntent().getIntExtra("HubTargetHeight",0), targetFps=getIntent().getIntExtra("HubTargetFps",0);
            final boolean automatic=getIntent().getBooleanExtra("HubAutoRotate",false);
            main.post(() -> startSession(mode, reconnectLink, true, degrees, request, lease, targetWidth, targetHeight, targetFps, automatic));
            setIntent(new Intent(this, HubActivity.class));
        }
    }
    @Override protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        handleReceiverIntent(intent);
        if (intent.hasExtra("HubPage")) showPage(intent.getIntExtra("HubPage", 0));
        if (intent.getBooleanExtra("HubReconnect", false)) {
            String mode = intent.getStringExtra("HubReconnectMode");
            startSession(mode == null || mode.isEmpty() ? "tablet" : mode, intent.getStringExtra("HubLink"), true,
                    intent.getIntExtra("HubOrientation", 0), intent.getStringExtra("HubRequestId"), intent.getStringExtra("HubLeaseId"),
                    intent.getIntExtra("HubTargetWidth",0), intent.getIntExtra("HubTargetHeight",0), intent.getIntExtra("HubTargetFps",0), intent.getBooleanExtra("HubAutoRotate",false));
        }
    }
    @Override protected void onResume() {
        super.onResume(); foreground = true; main.removeCallbacks(poll); main.post(poll);
        HubReceiverService.listen(this::showPcRequest);
        if (!busy && activeCoordinator == null) {
            boolean returning = "true".equals(store.get("sessionWasLaunched", "false"));
            store.put("sessionWasLaunched", "false");
            status.setVisibility(store.bound() ? View.GONE : View.VISIBLE);
            if (!store.bound()) showProgress("请先绑定电脑。");
            refreshBindingSummary();
            cancelConnection.setVisibility(View.GONE);
            updateComputerAddress();
            refreshDiagnosticsCard();
            if (!hosts.hasFocus()) hosts.setText(bindingAddresses());
        }
        // Restore only a tunnel the user previously enabled; manual disconnect clears this flag.
        network.resumeIfNeeded(this::renderNetwork);
        main.removeCallbacks(checkAppUpdates); main.postDelayed(checkAppUpdates, 15000);
    }
    @Override public void onConfigurationChanged(Configuration configuration) {
        super.onConfigurationChanged(configuration); refreshPalette();
        build();
        showPage(currentPage);
    }
    @Override protected void onPause() { foreground = false; if (lanDiscovery != null) lanDiscovery.stop(); cancelLocalBinding.set(true); if (localBinding != null) localBinding.cancel(); if (presenceCoordinator != null) presenceCoordinator.cancel(); HubReceiverService.listen(null); dismissPcRequest(); main.removeCallbacks(poll); super.onPause(); }
    @Override protected void onDestroy() {
        main.removeCallbacks(checkAppUpdates); HubUpdates.setPreparing(false);
        if (activeCoordinator != null) activeCoordinator.cancel();
        if (presenceCoordinator != null) presenceCoordinator.cancel();
        presenceWorker.shutdownNow();
        FallbackWait pending = pendingFallback;
        if (pending != null) completeFallback(pending, Collections.emptyList(), new java.io.IOException("连接界面已关闭。"));
        worker.shutdown(); main.removeCallbacks(poll); super.onDestroy();
    }
    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
    private LinearLayout column() { LinearLayout view = new LinearLayout(this); view.setOrientation(LinearLayout.VERTICAL); return view; }
    private TextView label(String text, int size, int color) { TextView v = new TextView(this); v.setText(text); v.setTextSize(size); v.setTextColor(color); v.setLineSpacing(dp(4), 1); return v; }
    /**
     * Spinner rows use the platform theme by default.  On some device themes that
     * resolves to an unreadable foreground on the Hub surface, making the
     * selected value and popup options effectively unreadable.  Keep both states
     * on the Hub palette so the settings controls have a stable contrast.
     */
    private final class HubSpinnerAdapter extends ArrayAdapter<String> {
        private static final int FIELD_HEIGHT_DP = 56;
        private static final int OPTION_HEIGHT_DP = 64;
        private int selectedPosition;

        HubSpinnerAdapter(String[] values) {
            super(HubActivity.this, android.R.layout.simple_spinner_item, values);
            setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        }

        void setSelectedPosition(int position) {
            selectedPosition = Math.max(0, position);
            notifyDataSetChanged();
        }

        private View styleRow(View view, int position, boolean dropdown) {
            if (!(view instanceof TextView)) return view;
            TextView row = (TextView) view;
            boolean selected = position == selectedPosition;
            row.setTextColor(dropdown && selected ? ACCENT_DARK : TEXT);
            row.setTextSize(15);
            row.setGravity(Gravity.CENTER_VERTICAL);
            row.setIncludeFontPadding(true);
            row.setSingleLine(true);
            row.setEllipsize(TextUtils.TruncateAt.END);
            if (dropdown) {
                GradientDrawable option = new GradientDrawable();
                option.setColor(selected ? SELECTED : SURFACE);
                option.setCornerRadius(dp(8));
                option.setStroke(dp(1), selected ? ACCENT_DARK : BORDER);
                row.setBackground(option);
                row.setTypeface(null, selected ? Typeface.BOLD : Typeface.NORMAL);
                row.setMinHeight(dp(OPTION_HEIGHT_DP));
                row.setPadding(dp(16), 0, dp(16), 0);
                row.setCompoundDrawablesRelativeWithIntrinsicBounds(selected ? R.drawable.ic_check : 0, 0, 0, 0);
                if (selected) row.setCompoundDrawableTintList(android.content.res.ColorStateList.valueOf(ACCENT_DARK));
            } else {
                // The Spinner owns the field border. Keeping the adapter row
                // transparent prevents a second, misaligned border around the
                // selected value while retaining the same 56dp touch target.
                row.setBackgroundColor(Color.TRANSPARENT);
                row.setTypeface(null, Typeface.NORMAL);
                row.setMinHeight(dp(FIELD_HEIGHT_DP));
                row.setPadding(dp(16), 0, dp(12), 0);
                row.setCompoundDrawablePadding(dp(8));
                row.setCompoundDrawablesRelativeWithIntrinsicBounds(0, 0, R.drawable.ic_spinner_chevron, 0);
            }
            if (dropdown) {
                row.setContentDescription(getItem(position));
            }
            return row;
        }

        @Override public View getView(int position, View convertView, ViewGroup parent) {
            return styleRow(super.getView(position, convertView, parent), position, false);
        }

        @Override public View getDropDownView(int position, View convertView, ViewGroup parent) {
            return styleRow(super.getDropDownView(position, convertView, parent), position, true);
        }
    }

    private GradientDrawable spinnerFieldBackground() {
        GradientDrawable field = new GradientDrawable();
        field.setColor(FIELD);
        field.setCornerRadius(dp(8));
        field.setStroke(dp(1), BORDER);
        return field;
    }

    private Spinner styledSpinner(String[] values, String description) {
        Spinner view = new Spinner(this, Spinner.MODE_DROPDOWN);
        view.setAdapter(spinnerAdapter(values));
        view.setBackground(spinnerFieldBackground());
        view.setPadding(0, 0, 0, 0);
        view.setMinimumHeight(dp(HubSpinnerAdapter.FIELD_HEIGHT_DP));
        view.setContentDescription(description);
        view.setDropDownWidth(-1);
        view.setDropDownVerticalOffset(dp(4));
        view.setDropDownHorizontalOffset(0);
        GradientDrawable popup = new GradientDrawable();
        popup.setColor(SURFACE);
        popup.setCornerRadius(dp(10));
        popup.setStroke(dp(1), BORDER);
        view.setPopupBackgroundDrawable(popup);
        view.setElevation(0); view.setStateListAnimator(null);
        return view;
    }

    private HubSpinnerAdapter spinnerAdapter(String[] values) {
        return new HubSpinnerAdapter(values);
    }

    private void markSpinnerSelection(Spinner spinner, int position) {
        if (spinner.getAdapter() instanceof HubSpinnerAdapter) {
            ((HubSpinnerAdapter) spinner.getAdapter()).setSelectedPosition(position);
        }
    }

    private TextView settingLabel(LinearLayout parent, String text, int size, int topMargin) {
        TextView view = label(text, size, size >= 16 ? TEXT : MUTED);
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
        params.topMargin = dp(topMargin);
        parent.addView(view, params);
        return view;
    }
    private void gap(LinearLayout parent, int height) { View gap = new View(this); parent.addView(gap, new LinearLayout.LayoutParams(1, dp(height))); }
    private LinearLayout card(String title) {
        LinearLayout card = column(); int inset = dp(HubWindowLayout.of(this).cardInsetDp()); card.setPadding(inset, inset, inset, inset);
        GradientDrawable bg = new GradientDrawable(); bg.setColor(SURFACE); bg.setCornerRadius(dp(8)); bg.setStroke(dp(1), BORDER); card.setBackground(bg);
        LinearLayout.LayoutParams size = new LinearLayout.LayoutParams(-1, -2); size.bottomMargin = dp(16); content.addView(card, size);
        TextView heading = label(title, 21, TEXT); heading.setTypeface(null, Typeface.BOLD); card.addView(heading); gap(card, 12); return card;
    }
    private LinearLayout nestedCard(String title) {
        LinearLayout card = column(); card.setPadding(dp(16), dp(14), dp(16), dp(14));
        GradientDrawable bg = new GradientDrawable(); bg.setColor(FIELD); bg.setCornerRadius(dp(8)); card.setBackground(bg);
        TextView heading = label(title, 14, TEXT); heading.setTypeface(null, Typeface.BOLD); card.addView(heading); gap(card, 6); return card;
    }
    private LinearLayout insetGroup(String title) {
        LinearLayout group = column(); group.setPadding(0, dp(10), 0, dp(10));
        GradientDrawable bg = new GradientDrawable(); bg.setColor(Color.TRANSPARENT); group.setBackground(bg);
        TextView heading = label(title, 16, TEXT); heading.setTypeface(null, Typeface.BOLD); group.addView(heading); return group;
    }
    private View settingLink(LinearLayout parent, String title, String detail, View.OnClickListener action) {
        LinearLayout row = new LinearLayout(this); row.setGravity(Gravity.CENTER_VERTICAL); row.setPadding(dp(16), dp(12), dp(16), dp(12));
        GradientDrawable bg = new GradientDrawable(); bg.setColor(Color.TRANSPARENT); bg.setCornerRadius(dp(8)); row.setBackground(new RippleDrawable(android.content.res.ColorStateList.valueOf(RIPPLE), bg, null));
        LinearLayout text = column(); text.setGravity(Gravity.CENTER_VERTICAL);
        TextView heading = label(title, 15, TEXT); heading.setTypeface(null, Typeface.BOLD); text.addView(heading);
        if (detail != null && !detail.isEmpty()) text.addView(label(detail, 12, MUTED));
        row.addView(text, new LinearLayout.LayoutParams(0, -2, 1));
        TextView arrow = label("›", 28, ACCENT_DARK); arrow.setGravity(Gravity.CENTER); arrow.setIncludeFontPadding(false); arrow.setContentDescription(title + "，打开");
        row.addView(arrow, new LinearLayout.LayoutParams(dp(28), dp(44)));
        LinearLayout.LayoutParams size = new LinearLayout.LayoutParams(-1, -2); size.topMargin = dp(8); parent.addView(row, size);
        row.setMinimumHeight(dp(64)); row.setClickable(true); row.setFocusable(true); row.setOnClickListener(action); modeActions.add(row); return row;
    }
    /** Compact entry cards keep the common actions visible without a long settings scroll. */
    private View moduleCell(String title, String detail, boolean primary, View.OnClickListener action) {
        LinearLayout cell = column();
        cell.setGravity(Gravity.CENTER_VERTICAL);
        cell.setPadding(dp(16), dp(14), dp(16), dp(14));
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(primary ? ACCENT : FIELD);
        bg.setCornerRadius(dp(8));
        bg.setStroke(dp(1), primary ? ACCENT : BORDER);
        cell.setBackground(new android.graphics.drawable.RippleDrawable(
                android.content.res.ColorStateList.valueOf(RIPPLE), bg, null));
        TextView name = label(title, 16, primary ? ON_ACCENT : TEXT);
        name.setTypeface(null, Typeface.BOLD);
        TextView caption = label(detail, 12, primary ? ON_ACCENT : MUTED);
        caption.setMaxLines(2);
        cell.addView(name);
        cell.addView(caption, new LinearLayout.LayoutParams(-1, -2));
        cell.setMinimumHeight(dp(86));
        cell.setClickable(true);
        cell.setFocusable(true);
        cell.setOnClickListener(action);
        modeActions.add(cell);
        return cell;
    }
    private void moduleRow(LinearLayout parent, View first, View second) {
        HubWindowLayout.ActionRow row = new HubWindowLayout.ActionRow(this, 176, 12);
        row.addView(first); row.addView(second);
        parent.addView(row, new LinearLayout.LayoutParams(-1, -2));
    }
    private void moduleRow3(LinearLayout parent, View first, View second, View third) {
        if (!isLandscapeWindow() || HubWindowLayout.of(this).compact()) {
            LinearLayout stack = column();
            addStackedMode(stack, first);
            addStackedMode(stack, second);
            addStackedMode(stack, third);
            parent.addView(stack, new LinearLayout.LayoutParams(-1, -2));
            return;
        }
        HubWindowLayout.ActionRow row = new HubWindowLayout.ActionRow(this, 160, 16);
        row.addView(first); row.addView(second); row.addView(third);
        parent.addView(row, new LinearLayout.LayoutParams(-1, -2));
    }
    private void addStackedMode(LinearLayout parent, View item) {
        LinearLayout.LayoutParams size = new LinearLayout.LayoutParams(-1, -2);
        size.bottomMargin = dp(8);
        parent.addView(item, size);
    }
    private Button button(LinearLayout parent, String title, boolean primary, View.OnClickListener action) {
        Button v = new Button(this); v.setStateListAnimator(null); v.setElevation(0); v.setText(title); v.setTextSize(16); v.setAllCaps(false); v.setTextColor(primary ? ON_ACCENT : ACCENT_DARK); v.setMinHeight(dp(52));
        GradientDrawable shape = new GradientDrawable(); shape.setCornerRadius(dp(8)); shape.setColor(primary ? ACCENT : FIELD); shape.setStroke(dp(1), primary ? ACCENT : BORDER); v.setBackground(shape);
        android.content.res.ColorStateList ripple = android.content.res.ColorStateList.valueOf(RIPPLE);
        v.setBackground(new android.graphics.drawable.RippleDrawable(ripple, shape, null));
        v.setPadding(dp(12), dp(10), dp(12), dp(10));
        LinearLayout.LayoutParams size = new LinearLayout.LayoutParams(-1, -2); size.topMargin = dp(8); parent.addView(v, size);
        v.setOnClickListener(action); actions.add(v); return v;
    }
    private Button compactButton(LinearLayout parent, String title, View.OnClickListener action) {
        Button v = new Button(this); v.setStateListAnimator(null); v.setElevation(0); v.setText(title); v.setTextSize(14); v.setAllCaps(false); v.setTextColor(ACCENT_DARK); v.setMinHeight(dp(48));
        GradientDrawable shape = new GradientDrawable(); shape.setCornerRadius(dp(8)); shape.setColor(FIELD); shape.setStroke(dp(1), BORDER);
        v.setBackground(new android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(RIPPLE), shape, null));
        v.setPadding(dp(12), dp(8), dp(12), dp(8));
        LinearLayout.LayoutParams size = new LinearLayout.LayoutParams(0, -2, 1); size.topMargin = dp(8); parent.addView(v, size);
        v.setOnClickListener(action); actions.add(v); return v;
    }
    private Button textButton(LinearLayout parent, String title, int color, View.OnClickListener action) {
        Button v = new Button(this); v.setStateListAnimator(null); v.setElevation(0); v.setText(title); v.setTextSize(14); v.setAllCaps(false); v.setTextColor(color);
        v.setGravity(Gravity.START | Gravity.CENTER_VERTICAL); v.setMinHeight(dp(48)); v.setPadding(0, 0, 0, 0);
        v.setBackgroundColor(Color.TRANSPARENT);
        LinearLayout.LayoutParams size = new LinearLayout.LayoutParams(-1, -2); size.topMargin = dp(6); parent.addView(v, size);
        v.setOnClickListener(action); actions.add(v); return v;
    }
    private EditText field(LinearLayout parent, String title, String hint, boolean multiLine) {
        TextView name = label(title, 14, MUTED); LinearLayout.LayoutParams ns = new LinearLayout.LayoutParams(-1, -2); ns.topMargin = dp(12); parent.addView(name, ns);
        EditText field = new EditText(this); field.setTextColor(TEXT); field.setHintTextColor(MUTED); field.setTextSize(15); field.setHint(hint); field.setPadding(dp(12), dp(12), dp(12), dp(12));
        field.setSingleLine(!multiLine); field.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS | (multiLine ? InputType.TYPE_TEXT_FLAG_MULTI_LINE : InputType.TYPE_TEXT_VARIATION_URI));
        GradientDrawable shape = new GradientDrawable(); shape.setColor(FIELD); shape.setCornerRadius(dp(8)); shape.setStroke(dp(1), BORDER); field.setBackground(shape);
        field.setMinHeight(dp(multiLine ? 96 : 52));
        LinearLayout.LayoutParams fs = new LinearLayout.LayoutParams(-1, -2); fs.topMargin = dp(6); parent.addView(field, fs); return field;
    }
    private ScrollView pageScroll(LinearLayout page) {
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setFocusableInTouchMode(true);
        scroll.setDescendantFocusability(android.view.ViewGroup.FOCUS_BEFORE_DESCENDANTS);
        FrameLayout centered = new FrameLayout(this);
        scroll.addView(centered, new ScrollView.LayoutParams(-1, -2));
        HubWindowLayout layout = HubWindowLayout.of(this);
        page.setPadding(dp(layout.pageInsetDp()), dp(layout.compact() ? 16 : 24), dp(layout.pageInsetDp()), dp(30));
        int width = dp(layout.contentWidthDp());
        centered.addView(page, new FrameLayout.LayoutParams(width, -2, Gravity.TOP | Gravity.CENTER_HORIZONTAL));
        return scroll;
    }
    private Rect windowBounds() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) return new Rect(getWindowManager().getCurrentWindowMetrics().getBounds());
        Point point = new Point(); getWindowManager().getDefaultDisplay().getSize(point);
        return new Rect(0, 0, point.x, point.y);
    }
    private boolean isLandscapeWindow() {
        Rect bounds = windowBounds();
        return bounds.width() > bounds.height();
    }
    private void showPage(int pageIndex) {
        if (pageSlot == null || pageIndex < 0 || pageIndex > 2) return;
        currentPage = pageIndex;
        for (int i = 0; i < pageSlot.getChildCount(); i++) pageSlot.getChildAt(i).setVisibility(i == pageIndex ? View.VISIBLE : View.GONE);
        View selected = pageSlot.getChildAt(pageIndex);
        selected.requestFocus();
        if (selected instanceof ScrollView) selected.post(() -> ((ScrollView)selected).scrollTo(0, 0));
        if (navigation != null) navigation.select(pageIndex);
    }
    private void build() {
        actions.clear();
        modeActions.clear();
        FrameLayout page = new FrameLayout(this); pageRoot = page;
        page.setFocusableInTouchMode(true); page.setBackgroundColor(BACKGROUND); setContentView(page); page.requestFocus();
        pageSlot = new FrameLayout(this);
        LinearLayout home = column(), connection = column(), settings = column();
        pageSlot.addView(pageScroll(home), new FrameLayout.LayoutParams(-1, -1));
        pageSlot.addView(pageScroll(connection), new FrameLayout.LayoutParams(-1, -1));
        pageSlot.addView(pageScroll(settings), new FrameLayout.LayoutParams(-1, -1));
        navigation = new HubNavigation(this, pageSlot, currentPage, index -> {
            if (index == 3) HubFilesActivity.open(this); else showPage(index);
        });
        page.addView(navigation.view, new FrameLayout.LayoutParams(-1, -1));

        content = home;
        TextView title = label("我的设备", HubWindowLayout.of(this).compact() ? 26 : 30, TEXT); title.setTypeface(null, Typeface.BOLD); home.addView(title); gap(home, 8);
        home.addView(label("选择电脑，开始远控或扩展屏幕", 15, MUTED)); gap(home, 20);
        LinearLayout computer = card("Windows 电脑");
        LinearLayout identity = new LinearLayout(this); identity.setGravity(Gravity.CENTER_VERTICAL);
        TextView icon = label("PC", 16, ACCENT_DARK); icon.setGravity(Gravity.CENTER); icon.setTypeface(null, Typeface.BOLD);
        GradientDrawable iconBg = new GradientDrawable(); iconBg.setColor(SELECTED); iconBg.setCornerRadius(dp(8)); icon.setBackground(iconBg);
        icon.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
        identity.addView(icon, new LinearLayout.LayoutParams(dp(54), dp(54)));
        LinearLayout identityText = column(); LinearLayout.LayoutParams nameSize = new LinearLayout.LayoutParams(0, -2, 1); nameSize.leftMargin = dp(14); identity.addView(identityText, nameSize);
        computerName = label(store.get("deviceName", "未绑定电脑"), 25, TEXT); computerName.setTypeface(null, Typeface.BOLD);
        computerName.setMaxLines(1); computerName.setEllipsize(android.text.TextUtils.TruncateAt.END); identityText.addView(computerName);
        LinearLayout stateRow = new LinearLayout(this); stateRow.setGravity(Gravity.CENTER_VERTICAL);
        deviceStateDot = new View(this); deviceStateDot.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO); stateRow.addView(deviceStateDot, new LinearLayout.LayoutParams(dp(8), dp(8)));
        deviceState = label("已绑定 · 待检查", 14, MUTED); LinearLayout.LayoutParams stateSize = new LinearLayout.LayoutParams(-2, -2); stateSize.leftMargin = dp(7); stateRow.addView(deviceState, stateSize); identityText.addView(stateRow);
        computer.addView(identity); gap(computer, 18);
        computerDetail = label("", 14, MUTED); computerDetail.setSingleLine(true); computerDetail.setMaxLines(1);
        computerDetail.setEllipsize(android.text.TextUtils.TruncateAt.END); computerDetail.setPadding(dp(12), dp(9), dp(12), dp(9));
        GradientDrawable addressBg = new GradientDrawable(); addressBg.setColor(FIELD); addressBg.setCornerRadius(dp(8)); computerDetail.setBackground(addressBg);
        computerDetail.setMinHeight(dp(48)); computerDetail.setGravity(Gravity.CENTER_VERTICAL); computerDetail.setClickable(true); computerDetail.setFocusable(true);
        computerDetail.setOnClickListener(v -> {
            List<String> annotated = new ArrayList<>();
            for (String address : HubRoutes.usableAddresses(store.get("hosts", ""))) annotated.add(HubRoutes.label(address) + "：" + address);
            String addresses = android.text.TextUtils.join("\n", annotated);
            HubStreamUi.hubDialog(this).setTitle("连接地址").setMessage(addresses.isEmpty() ? "尚未发现可用的 IPv4 地址" : addresses)
                .setPositiveButton("关闭", null).show();
        });
        computer.addView(computerDetail, new LinearLayout.LayoutParams(-1, -2)); updateComputerAddress(); gap(computer, 10);
        moduleRow(computer,
                moduleCell("远程控制", "查看并操作电脑主屏", true, v -> startSession("remote", "auto", false, 0)),
                moduleCell("用作扩展屏", "作为电脑第二块屏幕", false, v -> HubStreamUi.hubDialog(this)
                        .setTitle("扩展屏连接方式").setItems(new String[]{"无线网络", "USB 网络共享"},
                            (dialog, which) -> startSession("tablet", which == 0 ? "lan" : "usb", false, selectedOrientation())).show()));
        button(computer, "文件传输", false, v -> HubFilesActivity.open(this));
        gap(computer, 16);
        LinearLayout recent = nestedCard("上次连接");
        lastConnectionSummary = label(HubSessionDiagnostics.summary(this), 14, TEXT); recent.addView(lastConnectionSummary);
        LinearLayout diagnosticsActions = new HubWindowLayout.ActionRow(this, 152, 8);
        compactButton(diagnosticsActions, "查看详细诊断", v -> HubStreamUi.hubDialog(this).setTitle("最近一次连接诊断")
                .setView(HubStreamUi.diagnosticsView(this, "连接：" + HubSessionDiagnostics.summary(this)))
                .setPositiveButton("关闭", null)
                .setNeutralButton("复制原始诊断", (dialog, which) -> ((ClipboardManager)getSystemService(CLIPBOARD_SERVICE))
                        .setPrimaryClip(ClipData.newPlainText("我控制连接诊断", HubSessionDiagnostics.read(this)))).show());
        LinearLayout.LayoutParams secondAction = new LinearLayout.LayoutParams(0, -2, 1); secondAction.leftMargin = dp(8); secondAction.topMargin = dp(8);
        Button checkDevice = new Button(this); checkDevice.setStateListAnimator(null); checkDevice.setElevation(0); checkDevice.setText("检查电脑状态"); checkDevice.setTextSize(14); checkDevice.setAllCaps(false); checkDevice.setTextColor(ACCENT_DARK); checkDevice.setMinHeight(dp(48));
        GradientDrawable checkShape = new GradientDrawable(); checkShape.setCornerRadius(dp(8)); checkShape.setColor(FIELD); checkShape.setStroke(dp(1), BORDER);
        checkDevice.setBackground(new RippleDrawable(android.content.res.ColorStateList.valueOf(RIPPLE), checkShape, null)); diagnosticsActions.addView(checkDevice, secondAction); actions.add(checkDevice);
        checkDevice.setOnClickListener(v -> run(() -> { new HubSessionCoordinator(this, this::showProgress).check(); return "电脑在线，可以连接。"; }));
        LinearLayout.LayoutParams diagnosticsSize = new LinearLayout.LayoutParams(-1, -2); diagnosticsSize.topMargin = dp(8);
        recent.addView(diagnosticsActions, diagnosticsSize);
        computer.addView(recent, new LinearLayout.LayoutParams(-1, -2));
        gap(computer, 16);
        status = label(store.bound() ? "已绑定，可检查电脑状态或开始连接。" : "请先到“连接”页绑定电脑。", 14, MUTED);
        status.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE); computer.addView(status); renderDeviceState();
        cancelConnection = button(computer, "取消连接", false, v -> {
            HubSessionCoordinator coordinator = activeCoordinator;
            if (coordinator != null) { coordinator.cancel(); showProgress("正在取消并还原电脑显示…"); cancelConnection.setEnabled(false); }
        });
        cancelConnection.setVisibility(View.GONE);

        content = connection;
        TextView connectionTitle = label("连接", 30, TEXT); connectionTitle.setTypeface(null, Typeface.BOLD); connection.addView(connectionTitle); gap(connection, 18);
        buildNearbyComputers();
        LinearLayout binding = card("设备绑定");
        bindingSummary = label("", 15, TEXT); binding.addView(bindingSummary);
        bindingForm = column(); binding.addView(bindingForm);
        button(binding, "绑定 / 重新绑定", false, v -> bindingForm.setVisibility(bindingForm.getVisibility() == View.VISIBLE ? View.GONE : View.VISIBLE));
        hosts = field(bindingForm, "电脑地址", "输入电脑地址", false); hosts.setId(View.generateViewId()); hosts.setContentDescription("电脑地址"); hosts.setText(bindingAddresses());
        invitation = field(bindingForm, "电脑端设备绑定码", "粘贴电脑端显示的绑定码", true); invitation.setContentDescription("电脑端设备绑定码"); invitation.setImportantForAutofill(View.IMPORTANT_FOR_AUTOFILL_NO);
        button(bindingForm, "保存并验证绑定", true, v -> bindComputer());
        refreshBindingSummary();
        button(binding, "检查电脑状态", false, v -> run(() -> { new HubSessionCoordinator(this, this::showProgress).check(); return "电脑在线，可以连接。"; }));
        buildProviderCard();
        LinearLayout account = card("账号与异地连接"); networkStatus = label("正在读取异地网络状态…", 14, MUTED); account.addView(networkStatus);
        networkDetails = label("", 14, TEXT); networkDetails.setPadding(dp(12), dp(9), dp(12), dp(9));
        networkDetails.setMinHeight(dp(48)); networkDetails.setGravity(Gravity.CENTER_VERTICAL); networkDetails.setClickable(true); networkDetails.setFocusable(true);
        GradientDrawable networkDetailBg = new GradientDrawable(); networkDetailBg.setColor(FIELD); networkDetailBg.setCornerRadius(dp(8)); networkDetails.setBackground(networkDetailBg);
        networkDetails.setVisibility(View.GONE); networkDetails.setOnClickListener(v -> { revealNetworkDetails = !revealNetworkDetails; renderNetworkDetails(lastNetworkState); }); account.addView(networkDetails);
        remoteAccount = column(); account.addView(remoteAccount);
        loginAccountButton = button(remoteAccount, "登录连接账号", false, v -> { showProgress("正在获取账号授权地址…"); setBusy(true); network.login((state, error) -> { if (!isDestroyed()) setBusy(false); renderNetwork(state, error); if (state != null && !state.authorizationUrl.isEmpty()) openAuthorization(); }); });
        authorizationButton = button(remoteAccount, "打开账号授权页", false, v -> openAuthorization());
        copyAuthorizationButton = button(remoteAccount, "复制授权链接", false, v -> { if (authorizationUrl.isEmpty()) { showProgress("请先获取授权地址。"); return; } ((ClipboardManager)getSystemService(CLIPBOARD_SERVICE)).setPrimaryClip(ClipData.newPlainText("我控制账号授权", authorizationUrl)); showProgress("授权链接已复制。"); });
        connectNetworkButton = button(remoteAccount, "连接异地网络", true, v -> { Intent consent = network.prepareVpn(); if (consent != null) startActivityForResult(consent, 81); else network.connect(this::renderNetwork); });
        disconnectNetworkButton = button(remoteAccount, "断开异地网络", false, v -> network.disconnect(this::renderNetwork));
        logoutAccountButton = button(account, "退出当前账号", false, v -> HubStreamUi.hubDialog(this).setTitle("退出当前连接账号？")
                .setMessage("退出后可登录其他账号；电脑绑定和本地通道设置会保留。")
                .setNegativeButton("取消", null).setPositiveButton("退出并切换", (dialog, which) -> { authorizationUrl = ""; network.logout(this::renderNetwork); }).show());
        gap(account, 12); account.addView(label("账号内的电脑", 15, TEXT)); peerList = column(); account.addView(peerList);

        content = settings;
        TextView settingsTitle = label("设置", 30, TEXT); settingsTitle.setTypeface(null, Typeface.BOLD); settings.addView(settingsTitle); gap(settings, 18);
        LinearLayout appearance = card("外观");
        settingLabel(appearance, "应用主题", 14, 0);
        Spinner theme = styledSpinner(new String[]{getString(R.string.hub_theme_system), getString(R.string.hub_theme_light), getString(R.string.hub_theme_dark)}, "应用主题");
        theme.setSelection(Math.min(HubTheme.mode(this), theme.getAdapter().getCount() - 1)); markSpinnerSelection(theme, theme.getSelectedItemPosition());
        theme.setOnItemSelectedListener(new android.widget.AdapterView.OnItemSelectedListener() {
            @Override public void onNothingSelected(android.widget.AdapterView<?> parent) { }
            @Override public void onItemSelected(android.widget.AdapterView<?> parent, View view, int position, long id) {
                markSpinnerSelection(theme, position);
                if (position != HubTheme.mode(HubActivity.this)) {
                    HubTheme.setMode(HubActivity.this, position); refreshPalette(); build();
                }
            }
        });
        LinearLayout.LayoutParams themeSize = new LinearLayout.LayoutParams(-1, dp(HubSpinnerAdapter.FIELD_HEIGHT_DP)); themeSize.topMargin = dp(6); appearance.addView(theme, themeSize);
        appearance.addView(label("控制界面跟随系统时自动切换；串流画面和串流工具栏始终保持深色。", 13, MUTED));
        LinearLayout updateCard = card("版本与更新");
        updateCard.addView(new HubUpdatePanel(this, updates, () -> !busy && activeCoordinator == null), new LinearLayout.LayoutParams(-1, -2));
        LinearLayout desktopSecurity = card("锁屏与自动输入");
        desktopSecurity.addView(HubLoginSettings.view(this,false,null),new LinearLayout.LayoutParams(-1,-2));
        LinearLayout advanced = card("高级设置");
        settingLink(advanced, "音视频与输入高级设置", "分辨率、编码器、解码、键盘与手势", v -> startActivity(new Intent(this, StreamSettings.class)));
        settingLink(advanced, "兼容主机列表", "管理上游兼容连接", v -> startActivity(new Intent(this, PcView.class)));
        LinearLayout privacy = card("远控隐私保护");
        privacy.addView(label("连接主屏后，打开串流工具栏 → 画面与设置 → 本机隐私屏，即可开关。紧急恢复请在电脑按 Ctrl+Alt+F12。", 14, MUTED));
        LinearLayout receiver = card("电脑主动副屏");
        receiverSwitch = new Switch(this); receiverSwitch.setText("接收电脑副屏请求"); receiverSwitch.setTextColor(TEXT); receiverSwitch.setTextSize(16); receiverSwitch.setPadding(0, dp(8), 0, dp(8));
        receiverSwitch.setThumbTintList(new android.content.res.ColorStateList(new int[][]{new int[]{android.R.attr.state_checked}, new int[]{}}, new int[]{ACCENT_DARK, MUTED}));
        receiverSwitch.setTrackTintList(new android.content.res.ColorStateList(new int[][]{new int[]{android.R.attr.state_checked}, new int[]{}}, new int[]{ACCENT, BORDER}));
        receiverSwitch.setChecked(HubReceiverService.isEnabled(this)); receiver.addView(receiverSwitch, new LinearLayout.LayoutParams(-1, dp(56)));
        receiverSwitch.setOnCheckedChangeListener((view, enabled) -> {
            if (enabled && !store.bound()) { receiverSwitch.setChecked(false); showProgress("请先绑定电脑。"); return; }
            if (enabled && Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS}, 82);
            }
            try { HubReceiverService.enable(this, enabled); showProgress(enabled ? "已开启接收；电脑可在副屏页面发起请求。" : "已关闭接收电脑副屏请求。"); }
            catch (RuntimeException e) { receiverSwitch.setChecked(false); showProgress("暂时无法启用接收，请重新打开应用后重试。"); }
        });
        LinearLayout display = card("显示与串流");
        LinearLayout orientationGroup = insetGroup("副屏方向");
        orientationGroup.addView(label("自动跟随设备方向，并遵循系统旋转锁定；也可固定一个方向用于副屏连接。", 13, MUTED));
        orientation = styledSpinner(new String[]{"自动", "横屏", "竖屏", "反向横屏", "反向竖屏"}, "副屏方向");
        LinearLayout.LayoutParams orientationSize = new LinearLayout.LayoutParams(-1, dp(HubSpinnerAdapter.FIELD_HEIGHT_DP)); orientationSize.topMargin = dp(8); orientationGroup.addView(orientation, orientationSize);
        LinearLayout.LayoutParams orientationGroupSize = new LinearLayout.LayoutParams(-1, -2); orientationGroupSize.bottomMargin = dp(4); display.addView(orientationGroup, orientationGroupSize);
        orientation.setSelection(Math.min(selectedOrientationIndex, orientation.getAdapter().getCount() - 1));
        markSpinnerSelection(orientation, orientation.getSelectedItemPosition());
        orientation.setOnItemSelectedListener(new android.widget.AdapterView.OnItemSelectedListener() {
            @Override public void onNothingSelected(android.widget.AdapterView<?> parent) { }
            @Override public void onItemSelected(android.widget.AdapterView<?> parent, View view, int position, long id) { selectedOrientationIndex = position; markSpinnerSelection(orientation, position); }
        });
        settingLink(display, "远程控制画质", "三档预设与高级选项；串流中可从顶部控制菜单调整", v -> showQualityPresets());

        final View[] tabletQualityRow = new View[1];
        tabletQualityRow[0] = settingLink(display, "扩展屏画质", tabletQualityLabel(), v -> HubStreamUi.hubDialog(this)
                .setTitle("扩展屏画质 · 下次连接生效")
                .setSingleChoiceItems(new String[]{"稳定优先", "清晰优先（需要更好网络）"}, "clear".equals(store.get("tabletQuality", "stable")) ? 1 : 0,
                        (dialog, which) -> { store.put("tabletQuality", which == 1 ? "clear" : "stable");
                            ((TextView)((LinearLayout)((LinearLayout)tabletQualityRow[0]).getChildAt(0)).getChildAt(1)).setText(tabletQualityLabel()); dialog.dismiss(); }).setNegativeButton("取消", null).show());
        settingLink(display, "USB 网络共享设置", "打开系统网络设置，配置 USB 直连副屏", v -> { try { startActivity(new Intent("android.settings.TETHER_SETTINGS")); } catch (Exception e) { startActivity(new Intent(Settings.ACTION_WIRELESS_SETTINGS)); } });
        LinearLayout sound = card("声音输出");
        settingLabel(sound, "远程控制下次连接时使用的输出设备", 14, 0);
        Spinner audio = styledSpinner(new String[]{"仅平板扬声器", "仅电脑扬声器", "电脑和平板同时输出"}, "声音输出");
        android.content.SharedPreferences audioPrefs = androidx.preference.PreferenceManager.getDefaultSharedPreferences(this);
        TextView audioBehavior = label("", 14, MUTED);
        String savedAudio = audioPrefs.getString("streamhub_audio_output", "computer");
        audio.setSelection("tablet".equals(savedAudio) ? 0 : "both".equals(savedAudio) ? 2 : 1);
        markSpinnerSelection(audio, audio.getSelectedItemPosition());
        audio.setOnItemSelectedListener(new android.widget.AdapterView.OnItemSelectedListener() {
            @Override public void onNothingSelected(android.widget.AdapterView<?> parent) { }
            @Override public void onItemSelected(android.widget.AdapterView<?> parent, View view, int position, long id) {
                markSpinnerSelection(audio, position);
                String mode = position == 0 ? "tablet" : position == 1 ? "computer" : "both";
                audioBehavior.setText(position == 0
                        ? "仅平板输出会临时接管电脑的默认音频设备；需要在电脑自由切换扬声器或耳机时，请选电脑输出或同时输出。断开后恢复原设备。"
                        : "保留电脑当前音频设备，可在电脑切换扬声器或耳机；下次连接生效。");
                audioPrefs.edit().putString("streamhub_audio_output", mode)
                        .putBoolean("checkbox_host_audio", position != 0).apply();
            }
        });
        LinearLayout.LayoutParams audioSize = new LinearLayout.LayoutParams(-1, dp(HubSpinnerAdapter.FIELD_HEIGHT_DP)); audioSize.topMargin = dp(6); sound.addView(audio, audioSize);
        sound.addView(audioBehavior);
        LinearLayout authorization = card("设备授权");
        authorization.addView(label("绑定码仅用于已授权设备；重新绑定会替换当前电脑信任。", 14, MUTED));
        Button clearBinding = textButton(authorization, "清除当前电脑绑定", DANGER, v -> HubStreamUi.hubDialog(this)
                .setTitle("清除电脑绑定？").setMessage("清除后需要重新输入电脑端绑定码，已保存的串流证书也会失效。")
                .setNegativeButton("取消", null).setPositiveButton("清除", (dialog, which) -> {
                    store.clearBinding(); computerOnline = false; presenceValidUntil = 0; lastOperationFailed = false; mediaState = "";
                    computerName.setText("未绑定电脑"); computerDetail.setText(""); showProgress("电脑绑定已清除。");
                }).show().getButton(AlertDialog.BUTTON_POSITIVE).setTextColor(DANGER));
        settings.addView(label("版本 " + com.limelight.BuildConfig.VERSION_NAME +
                (com.limelight.BuildConfig.STREAMHUB_INTERNAL ? " · 内测版" : ""), 13, MUTED));
        showPage(Math.min(currentPage, pageSlot.getChildCount() - 1));
        setBusy(busy);
    }
    private int selectedOrientation() { int i = orientation.getSelectedItemPosition(); return i == 0 ? deviceOrientation() : (i - 1) * 90; }
    private int deviceOrientation() {
        android.view.Display display = getWindowManager().getDefaultDisplay(); Point point = new Point(); display.getRealSize(point);
        int rotation = display.getRotation(); boolean naturalPortrait = (rotation == Surface.ROTATION_0 || rotation == Surface.ROTATION_180) ? point.y > point.x : point.x > point.y;
        return (naturalPortrait ? new int[]{90, 0, 270, 180} : new int[]{0, 90, 180, 270})[rotation];
    }
    private interface Work { String execute() throws Exception; }
    private void setBusy(boolean value) {
        HubUpdates.setPreparing(value);
        if (provider != null) { provider.setEnabled(!value); for (int i=0;i<provider.getChildCount();i++) provider.getChildAt(i).setEnabled(!value); providerServer.setEnabled(!value); providerManual.setEnabled(!value); } busy = value; if (value && presenceCoordinator != null) presenceCoordinator.cancel(); for (Button button : actions) if (button != cancelConnection) { button.setEnabled(!value); button.setAlpha(value ? 0.5f : 1f); } for (View view : modeActions) { view.setEnabled(!value); view.setAlpha(value ? 0.5f : 1f); } renderDeviceState(); updateProviderApply(); }
    private void run(Work work) {
        if (busy) return; final long operation = ++operationGeneration; setBusy(true);
        worker.execute(() -> { String message; boolean success = false; try { message = work.execute(); success = true; } catch (java.util.concurrent.CancellationException e) { message = e.getMessage(); } catch (Exception e) { message = "未完成：" + (e.getMessage() == null ? e.getClass().getSimpleName() : e.getMessage()); }
            final String result = message; final boolean succeeded = success; main.post(() -> { if (!isDestroyed() && operation == operationGeneration) { computerOnline = succeeded && store.bound(); presenceValidUntil = computerOnline ? android.os.SystemClock.elapsedRealtime() + 15000 : 0; presenceNetwork = HubNetworkStamp.current(); lastOperationFailed = !succeeded; nextPresenceAt = android.os.SystemClock.elapsedRealtime() + 12000; setBusy(false); showProgress(result); computerName.setText(store.get("deviceName", "我的电脑")); updateComputerAddress(); refreshBindingSummary(); refreshDiagnosticsCard(); if (!hosts.hasFocus()) hosts.setText(bindingAddresses()); } }); });
    }
    private void refreshComputerPresence() {
        long now = android.os.SystemClock.elapsedRealtime(); String networkKey = HubNetworkStamp.current();
        if (now >= presenceValidUntil || !networkKey.equals(presenceNetwork)) { computerOnline = false; mediaState = ""; renderDeviceState(); }
        if (busy || presenceChecking || !store.bound() || now < nextPresenceAt) return;
        presenceChecking = true; nextPresenceAt = now + 12000;
        final String revision = store.get("bindingRevision", "");
        HubSessionCoordinator checker = new HubSessionCoordinator(this, message -> { }); presenceCoordinator = checker;
        presenceWorker.execute(() -> {
            JSONObject info = null;
            try { info = checker.check(); } catch (Exception ignored) { }
            final JSONObject checked = info;
            main.post(() -> {
                presenceChecking = false; if (presenceCoordinator == checker) presenceCoordinator = null;
                if (isDestroyed() || !foreground || busy || checker.isCancelled() || !revision.equals(store.get("bindingRevision", ""))) return;
                computerOnline = checked != null; presenceNetwork = networkKey; presenceHost = checked == null ? "" : checker.selectedHost();
                presenceValidUntil = checked == null ? 0 : android.os.SystemClock.elapsedRealtime() + 15000;
                mediaState = "";
                if (checked != null) {
                    lastOperationFailed = false;
                    JSONObject remote = checked.optJSONObject("remote"), tablet = checked.optJSONObject("tablet");
                    if ((remote != null && remote.optBoolean("streaming")) || (tablet != null && tablet.optBoolean("streaming"))) mediaState = " · 正在串流";
                    else if (tablet != null && tablet.optBoolean("desktopCleanupPending")) mediaState = " · 正在回收副屏";
                }
                renderDeviceState(); updateComputerAddress();
            });
        });
    }
    private void showProgress(String message) { main.post(() -> { if (!isDestroyed()) { status.setText(message); status.setVisibility(View.VISIBLE); } }); }
    private void refreshDiagnosticsCard() {
        if (lastConnectionSummary != null) lastConnectionSummary.setText(HubSessionDiagnostics.summary(this));
    }
    private String bindingAddresses() {
        try { HubTransports.Settings settings = store.transport(); return "manual".equals(settings.provider) ? settings.manualAddresses : store.get("hosts", ""); }
        catch (IllegalArgumentException error) { return ""; }
    }
    private void updateComputerAddress() {
        if (computerDetail == null) return;
        List<String> addresses = HubRoutes.usableAddresses(bindingAddresses());
        String text;
        try { text = (computerOnline && !presenceHost.isEmpty() ? HubRoutes.label(presenceHost) : "通道待检测") + " · " + (addresses.isEmpty() ? "尚无可用地址" : "点按查看连接详情"); }
        catch (IllegalArgumentException error) { text = "请重新设置连接通道"; }
        computerDetail.setText(text); computerDetail.setContentDescription("连接地址：" + text + "，点按查看全部");
    }
    private void renderDeviceState() {
        if (deviceState == null || deviceStateDot == null) return;
        int color = busy ? WARNING : lastOperationFailed ? DANGER : computerOnline ? SUCCESS : MUTED;
        deviceState.setText(busy ? "正在连接" : lastOperationFailed ? "连接未完成" : computerOnline ? "电脑在线" + mediaState : store.bound() ? "已绑定 · 暂不可达" : "未绑定");
        deviceState.setTextColor(color); GradientDrawable dot = new GradientDrawable(); dot.setShape(GradientDrawable.OVAL); dot.setColor(color); deviceStateDot.setBackground(dot);
    }
    private void finishBindingInput() {
        hosts.clearFocus(); invitation.clearFocus(); pageRoot.requestFocus();
        ((android.view.inputmethod.InputMethodManager)getSystemService(INPUT_METHOD_SERVICE)).hideSoftInputFromWindow(pageRoot.getWindowToken(), 0);
    }
    private void buildProviderCard() {
        LinearLayout card = card("连接通道");
        providerSaved = label("正在读取通道设置…", 14, MUTED); card.addView(providerSaved); gap(card, 12);
        card.addView(label("通道", 14, MUTED));
        provider = new RadioGroup(this); provider.setOrientation(RadioGroup.VERTICAL); provider.setContentDescription("连接通道");
        String[] descriptions = {"跨网络连接，使用 Tailscale 账号", "连接自己的 Headscale 服务", "同一网络或 USB 共享，无需账号", "指定电脑 IPv4 地址，无需账号"};
        for (int i = 0; i < HubTransports.IDS.length; i++) {
            RadioButton choice = new RadioButton(this); choice.setId(View.generateViewId());
            choice.setText(getString(R.string.streamhub_transport_choice, HubTransports.LABELS[i], descriptions[i])); choice.setTextColor(TEXT); choice.setTextSize(15);
            choice.setButtonTintList(android.content.res.ColorStateList.valueOf(ACCENT_DARK)); choice.setMinHeight(dp(72)); choice.setPadding(dp(12),dp(8),dp(12),dp(8));
            GradientDrawable shape = new GradientDrawable(); shape.setColor(FIELD); shape.setCornerRadius(dp(8)); shape.setStroke(dp(1), BORDER);
            choice.setBackground(new RippleDrawable(android.content.res.ColorStateList.valueOf(RIPPLE),shape,null));
            RadioGroup.LayoutParams size = new RadioGroup.LayoutParams(-1,-2); size.bottomMargin=dp(8); provider.addView(choice,size);
        }
        provider.check(provider.getChildAt(0).getId()); card.addView(provider); gap(card, 12);
        providerServerPanel = column(); card.addView(providerServerPanel);
        providerServer = field(providerServerPanel, "服务区 HTTPS 地址", "https://vpn.example.com", false);
        providerServer.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        providerServerPanel.addView(label("兼容 Headscale；电脑与平板须使用同一服务区。", 14, MUTED));
        providerManualPanel = column(); card.addView(providerManualPanel);
        providerManual = field(providerManualPanel, "电脑 IPv4 地址", "最多 8 个，用逗号或换行分隔", false);
        providerManualPanel.addView(label("仅使用填写的地址，不自动启用 VPN。首次使用仍需电脑绑定码。", 14, MUTED));
        providerMessage = label("保存会暂停异地连接。更换服务区前须先退出连接账号，电脑绑定保留。", 14, MUTED);
        card.addView(providerMessage);
        try {
            HubTransports.Settings settings = store.transport();
            hosts.setEnabled(!"manual".equals(settings.provider));
            for (int i = 0; i < HubTransports.IDS.length; i++) if (HubTransports.IDS[i].equals(settings.provider)) provider.check(provider.getChildAt(i).getId());
            providerServer.setText(settings.serverUrl); providerManual.setText(settings.manualAddresses);
            providerSaved.setText(getString(R.string.streamhub_transport_current, settings.transport().label()));
        } catch (IllegalArgumentException error) { providerSaved.setText(error.getMessage()); }
        provider.setOnCheckedChangeListener((group, checkedId) -> showProviderFields());
        showProviderFields();
        applyProvider = button(card, "保存并应用通道", true, v -> saveProvider());
        android.text.TextWatcher changes = new android.text.TextWatcher() {
            public void beforeTextChanged(CharSequence s, int start, int count, int after) { }
            public void onTextChanged(CharSequence s, int start, int before, int count) { updateProviderApply(); }
            public void afterTextChanged(android.text.Editable value) { }
        };
        providerServer.addTextChangedListener(changes); providerManual.addTextChangedListener(changes);
        updateProviderApply();
    }
    private int selectedProviderIndex() {
        for (int i=0;i<provider.getChildCount();i++) if (provider.getChildAt(i).getId()==provider.getCheckedRadioButtonId()) return i;
        return 0;
    }
    private void showProviderFields() {
        String id = HubTransports.IDS[selectedProviderIndex()];
        providerServerPanel.setVisibility("self-hosted".equals(id) ? View.VISIBLE : View.GONE);
        providerManualPanel.setVisibility("manual".equals(id) ? View.VISIBLE : View.GONE);
        updateProviderApply();
    }
    private void refreshBindingSummary() {
        if (bindingSummary == null) return;
        bindingSummary.setText(store.bound() ? "已绑定 · " + store.get("deviceName", "我的电脑") : "首次使用，请输入电脑地址和绑定码");
        bindingForm.setVisibility(store.bound() ? View.GONE : View.VISIBLE);
    }
    private void updateProviderApply() {
        if (applyProvider == null) return;
        boolean changed = true;
        try {
            HubTransports.Settings current = store.transport();
            HubTransports.Settings selected = new HubTransports.Settings(HubTransports.IDS[selectedProviderIndex()],
                    providerServer.getText().toString().trim(), providerManual.getText().toString().trim());
            changed = !current.provider.equals(selected.provider) || !current.serverUrl.equals(selected.serverUrl) || !current.manualAddresses.equals(selected.manualAddresses);
        } catch (IllegalArgumentException ignored) { }
        applyProvider.setEnabled(!busy && changed); applyProvider.setAlpha(!busy && changed ? 1f : 0.45f);
    }
    private String tabletQualityLabel() { return "clear".equals(store.get("tabletQuality", "stable")) ? "清晰优先 · 下次连接生效" : "稳定优先 · 下次连接生效"; }
    private void showQualityPresets() {
        HubQualitySettings.show(this, false, () -> startActivity(new Intent(this, StreamSettings.class)));
    }
    private void saveProvider() {
        if (busy || HubFileService.busy() || HubSessionLease.active()) { providerMessage.setText("请先结束串流和文件传输，再切换通道。"); return; }
        final HubTransports.Settings settings;
        try { settings = new HubTransports.Settings(HubTransports.IDS[selectedProviderIndex()],
                providerServer.getText().toString().trim(), providerManual.getText().toString().trim()); }
        catch (IllegalArgumentException error) { providerMessage.setText(error.getMessage()); return; }
        if (presenceCoordinator != null) presenceCoordinator.cancel();
        authorizationUrl = ""; setBusy(true); providerMessage.setText("正在应用通道设置…");
        network.configure(settings, (state, error) -> {
            if (isDestroyed()) return;
            setBusy(false); authorizationUrl = "";
            if (error != null) { providerMessage.setText(error); renderNetwork(null, error); return; }
            providerSaved.setText(getString(R.string.streamhub_transport_current, settings.transport().label()));
            providerMessage.setText("已保存。异地连接已暂停，账号与电脑绑定保留。");
            updateProviderApply();
            hosts.setText(bindingAddresses()); hosts.setEnabled(!"manual".equals(settings.provider));
            presenceValidUntil = nextPresenceAt = 0; computerOnline = false; renderDeviceState();
            renderNetwork(state, null); refreshComputerPresence();
        });
    }
    private void buildNearbyComputers() {
        if (lanDiscovery != null) lanDiscovery.stop();
        lanDiscovery = new HubLanDiscovery(this);
        LinearLayout nearby = card("附近电脑");
        nearby.addView(label("同一 Wi-Fi 或 USB 共享网络内，无需填写 IP。请先在电脑端生成短码。", 14, MUTED));
        nearbyStatus = label("点按查找，选择需要绑定的电脑。", 14, MUTED);
        nearbyStatus.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE); nearby.addView(nearbyStatus);
        nearbyList = column(); nearby.addView(nearbyList);
        button(nearby, "查找附近电脑", false, v -> startNearbySearch(""));
        settingLink(nearby,"使用电脑地址与短码","自动查找无结果时，填写电脑显示的局域网地址。",v->showNearbyAddress());
        textButton(nearby,"查找详情",MUTED,v->HubStreamUi.hubDialog(this).setTitle("附近电脑查找详情")
                .setMessage(lanDiscovery.diagnostics()).setPositiveButton("关闭",null).show());
    }
    private void startNearbySearch(String address) {
        if (busy) return;
        lanDiscovery.start((devices, message) -> {
                if (isDestroyed() || !foreground) return;
                nearbyStatus.setText(message); nearbyList.removeAllViews();
                for (HubLanDiscovery.Device device : devices) {
                    View row = settingLink(nearbyList, device.name,
                            ("usb".equals(HubRoutes.type(device.address)) ? "USB 有线" : "局域网") + " · " + device.address + " · 输入短码绑定",
                            ignored -> showShortCode(device));
                    modeActions.remove(row); // Discovery replaces these rows; do not retain detached views.
                }
            },address);
    }
    private void showNearbyAddress() {
        if(busy)return;
        LinearLayout input=column();input.setPadding(dp(24),dp(8),dp(24),dp(8));
        input.addView(label("先在电脑端生成六位短码，再填写电脑显示的局域网 IPv4 地址。两端核对数字一致后，在电脑允许绑定。"
                + (store.bound() ? "成功后才替换当前绑定。" : ""),14,MUTED));
        EditText address=field(input,"电脑地址","例如 192.168.1.10",false);
        address.setInputType(InputType.TYPE_CLASS_PHONE);address.setSingleLine(true);
        EditText code=shortCodeField(input);
        AlertDialog dialog=HubStreamUi.hubDialog(this).setTitle("使用电脑地址与短码").setView(bindingForm(input))
                .setPositiveButton("继续",null).setNegativeButton("取消",null).create();
        dialog.setOnShowListener(d->dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(v->{
            String value=address.getText().toString().trim();
            if(!HubLanDiscovery.localIpv4(value)){address.setError("请输入电脑显示的局域网 IPv4 地址");return;}
            String digits=code.getText().toString().trim();
            if(!digits.matches("[0-9]{6}")){code.setError("请输入六位数字，开头的 0 也要保留");return;}
            dialog.dismiss();if(lanDiscovery!=null)lanDiscovery.stop();beginLocalBinding(value,null,digits);
        }));
        dialog.show();sizeBindingDialog(dialog);positionBindingDialog(dialog);
    }
    private EditText shortCodeField(LinearLayout input) {
        EditText code = field(input, "六位短码", "例如 012345", false);
        code.setInputType(InputType.TYPE_CLASS_NUMBER); code.setFilters(new android.text.InputFilter[]{new android.text.InputFilter.LengthFilter(6)});
        code.setSingleLine(true); code.setGravity(Gravity.CENTER); code.setTextSize(26); code.setTypeface(Typeface.MONOSPACE);
        code.setLetterSpacing(0.12f); code.setMinimumHeight(dp(64));
        return code;
    }
    private void showShortCode(HubLanDiscovery.Device device) {
        if (busy) return;
        LinearLayout input = column(); input.setPadding(dp(24), dp(8), dp(24), dp(8));
        input.addView(label("输入电脑“设备 → 手机和平板绑定”显示的六位短码。"
                + (store.bound() ? "确认成功后会替换当前电脑绑定。" : ""), 14, MUTED));
        EditText code = shortCodeField(input);
        AlertDialog dialog = HubStreamUi.hubDialog(this).setTitle("绑定 " + device.name).setView(bindingForm(input))
                .setPositiveButton("继续", null).setNegativeButton("取消", null).create();
        dialog.setOnShowListener(ignored -> dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(v -> {
            String value = code.getText().toString().trim();
            if (!value.matches("[0-9]{6}")) { code.setError("请输入六位数字，开头的 0 也要保留"); return; }
            dialog.dismiss(); beginLocalBinding(device, value);
        }));
        dialog.show();
        sizeBindingDialog(dialog);
        positionBindingDialog(dialog);
    }
    private View bindingForm(LinearLayout input) {
        if (HubWindowLayout.of(this).compact()) input.setPadding(dp(16), dp(8), dp(16), dp(8));
        ScrollView scroll = new ScrollView(this); scroll.setFillViewport(false);
        scroll.addView(input, new ScrollView.LayoutParams(-1, -2));
        return scroll;
    }
    private void positionBindingDialog(AlertDialog dialog) {
        // Floating tablet keyboards do not resize the window; keep the code above them.
        if (dialog.getWindow() != null) {
            dialog.getWindow().setGravity(Gravity.TOP | Gravity.CENTER_HORIZONTAL);
            android.view.WindowManager.LayoutParams layout = dialog.getWindow().getAttributes();
            layout.y = dp(24);
            dialog.getWindow().setAttributes(layout);
        }
    }
    private void sizeBindingDialog(AlertDialog dialog) {
        android.view.Window window = dialog.getWindow();
        if (window == null) return;
        window.setLayout(Math.max(1, Math.min(dp(560), windowBounds().width() - dp(40))), ViewGroup.LayoutParams.WRAP_CONTENT);
        window.setSoftInputMode(android.view.WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
    }
    private void beginLocalBinding(HubLanDiscovery.Device device, String code) {
        beginLocalBinding(device.address,device,code);
    }
    private void beginLocalBinding(String address, HubLanDiscovery.Device discovered, String code) {
        cancelLocalBinding.set(false);
        LinearLayout confirmation = column(); confirmation.setPadding(dp(24), dp(8), dp(24), dp(8));
        TextView comparisonView = label("正在获取核对码…", 28, TEXT);
        comparisonView.setTypeface(Typeface.MONOSPACE, Typeface.BOLD); comparisonView.setGravity(Gravity.CENTER);
        comparisonView.setLetterSpacing(0.12f); comparisonView.setPadding(0, dp(16), 0, dp(16));
        confirmation.addView(comparisonView, new LinearLayout.LayoutParams(-1, -2));
        TextView confirmationHint = label("正在提交设备确认…", 14, MUTED); confirmation.addView(confirmationHint);
        AlertDialog waiting = HubStreamUi.hubDialog(this).setTitle("等待电脑确认")
                .setView(confirmation)
                .setNegativeButton("取消", (dialog, which) -> {
                    cancelLocalBinding.set(true); if (localBinding != null) localBinding.cancel();
                    if (activeCoordinator != null) activeCoordinator.cancel();
                }).create();
        waiting.setCanceledOnTouchOutside(false); waiting.setOnCancelListener(dialog -> {
            cancelLocalBinding.set(true); if (localBinding != null) localBinding.cancel();
            if (activeCoordinator != null) activeCoordinator.cancel();
        });
        waiting.show();
        sizeBindingDialog(waiting);
        run(() -> {
            HubLocalBinding client = null;
            try {
            if (cancelLocalBinding.get()) throw new java.util.concurrent.CancellationException("绑定已取消。");
            final HubLanDiscovery.Device device = discovered == null ? HubLocalBinding.lookup(address) : discovered;
            client = new HubLocalBinding(device); localBinding = client;
                if (cancelLocalBinding.get()) throw new java.util.concurrent.CancellationException("绑定已取消。");
                String name = Build.MODEL == null ? "Android 平板" : Build.MODEL;
                JSONObject request = client.begin(code, store.clientId(), name.length() > 64 ? name.substring(0, 64) : name);
                String requestId = request.getString("requestId"), comparison = request.getString("comparison");
                main.post(() -> { if (waiting.isShowing()) {
                    comparisonView.setText(comparison);
                    comparisonView.setContentDescription("设备核对码：" + comparison);
                    confirmationHint.setText("请核对电脑上显示的六位数字，一致后在电脑点“允许绑定”。\n\n不一致请取消。保持此页面开启，请勿把短码交给陌生人。");
                }});
                long deadline = android.os.SystemClock.elapsedRealtime() + 90000;
                while (android.os.SystemClock.elapsedRealtime() < deadline && !cancelLocalBinding.get()) {
                    Thread.sleep(1500);
                    JSONObject result = client.poll(requestId); String state = result.optString("state");
                    if ("approved".equals(state)) {
                        String trust = client.verifiedInvitation(result);
                        if (cancelLocalBinding.get()) throw new java.util.concurrent.CancellationException("绑定已取消。");
                        JSONObject info = new HubControlClient(device.address, trust).probeInfo();
                        if (!device.id.equalsIgnoreCase(info.optString("deviceId"))) throw new java.io.IOException("发现的设备与确认身份不一致，原绑定已保留。");
                        HubSessionCoordinator binder = new HubSessionCoordinator(this, this::showProgress); activeCoordinator = binder;
                        try { binder.bind(device.address, trust); } finally { activeCoordinator = null; }
                        return "已绑定 " + device.name + "，现在可以远控、使用副屏或访问已授权文件。";
                    }
                    if (!"pending".equals(state)) throw new java.io.IOException("电脑已拒绝或确认已过期，请重新生成短码。");
                }
                throw new java.util.concurrent.CancellationException("绑定已取消或确认超时，原绑定已保留。");
            } finally {
                if(client!=null)client.cancel(); if (localBinding == client) localBinding = null;
                main.post(() -> { if (waiting.isShowing()) waiting.dismiss(); });
            }
        });
    }
    private void bindComputer() {
        String address = hosts.getText().toString().trim(), code = invitation.getText().toString().trim();
        try { if ("manual".equals(store.transport().provider)) address = store.transport().manualAddresses; }
        catch (IllegalArgumentException error) { showProgress(error.getMessage()); return; }
        final String bindingAddress = address;
        if (address.isEmpty() || (code.isEmpty() && !store.bound())) { showProgress("请填写电脑地址和绑定码。"); return; }
        finishBindingInput();
        run(() -> {
            HubSessionCoordinator binder = new HubSessionCoordinator(this, this::showProgress); activeCoordinator = binder;
            try { binder.bind(bindingAddress, code); } finally { activeCoordinator = null; }
            main.post(() -> invitation.setText("")); return "绑定已保存。现在可以远程控制或用作副屏。";
        });
    }
    private void startSession(String mode, String link, boolean reconnect, int degrees) {
        startSession(mode, link, reconnect, degrees, "", "");
    }
    private void startSession(String mode, String link, boolean reconnect, int degrees, String pcRequestId) {
        startSession(mode, link, reconnect, degrees, pcRequestId, "");
    }
    private void startSession(String mode, String link, boolean reconnect, int degrees, String pcRequestId, String reuseLeaseId) {
        startSession(mode, link, reconnect, degrees, pcRequestId, reuseLeaseId, 0, 0, 0, orientation.getSelectedItemPosition() == 0);
    }
    private void startSession(String mode, String link, boolean reconnect, int degrees, String pcRequestId, String reuseLeaseId,
                              int targetWidth, int targetHeight, int targetFps, boolean requestedAutomatic) {
        pcRequestId = pcRequestId == null ? "" : pcRequestId;
        reuseLeaseId = reuseLeaseId == null ? "" : reuseLeaseId;
        final String requestId = pcRequestId, leaseId = reuseLeaseId;
        if (isDestroyed() || isFinishing()) return;
        if (busy) { showProgress("正在处理当前连接，请稍候。"); return; }
        if (!store.bound()) { showPage(1); showProgress("请先在“连接”页绑定电脑。"); return; }
        showPage(0);
        finishBindingInput();
        final String route = link == null ? "auto" : link;
        final boolean automatic = requestId.isEmpty() && requestedAutomatic;
        run(() -> {
            final long operation = operationGeneration;
            HubSessionCoordinator coordinator = new HubSessionCoordinator(this,
                message -> main.post(() -> { if (!isDestroyed() && operation == operationGeneration) status.setText(message); }), this::enableTailnetFallback);
            activeCoordinator = coordinator;
            main.post(() -> { cancelConnection.setVisibility(View.VISIBLE); cancelConnection.setEnabled(true); });
            try {
            if (reconnect) showProgress("正在调整画面并自动重连…");
            Intent session = coordinator.prepare(mode, degrees, route, automatic, requestId, leaseId, targetWidth, targetHeight, targetFps);
            main.post(() -> {
                if (!isDestroyed() && !isFinishing() && !coordinator.isCancelled()) { store.put("sessionWasLaunched", "true"); startActivity(session); }
                else new Thread(() -> { HubSessionLease lease = HubSessionLease.start(this, session); if (lease != null) lease.end(); }, "Hub-AbandonedSession").start();
            }); return "正在连接；串流中可点顶部“控制”。";
            } finally { activeCoordinator = null; main.post(() -> cancelConnection.setVisibility(View.GONE)); }
        });
    }
    private void dismissPcRequest() {
        if (receiverDialog != null) receiverDialog.dismiss(); receiverDialog = null; receiverDialogId = "";
    }
    private void handleReceiverIntent(Intent intent) {
        String data = intent.getStringExtra(HubReceiverService.EXTRA_REQUEST);
        if (data == null) return;
        intent.removeExtra(HubReceiverService.EXTRA_REQUEST);
        // The foreground service rechecks expiry/state before a request can be accepted.
        main.post(() -> HubReceiverService.listen(this::showPcRequest));
    }
    private void showPcRequest(JSONObject request) {
        if (!foreground || isFinishing() || isDestroyed()) return;
        if (request == null || !"waiting".equals(request.optString("state"))) { dismissPcRequest(); return; }
        String id = request.optString("id"); if (id.isEmpty() || id.equals(receiverDialogId)) return;
        dismissPcRequest(); receiverDialogId = id;
        int degrees = request.optInt("orientation", 0); String link = request.optString("link", "auto");
        receiverDialog = HubStreamUi.hubDialog(this).setTitle("电脑请求使用此设备作为副屏")
                .setMessage(store.get("deviceName", "我的电脑") + " · " + ("usb".equals(link) ? "USB 有线" : "auto".equals(link) ? "自动选择" : "无线连接") + "\n是否接受？")
                .setPositiveButton("接受", (dialog, which) -> {
                    if (busy) { receiverDialogId = ""; showProgress("请先完成或取消当前连接，再接受电脑请求。"); return; }
                    setBusy(true); showProgress("正在接受电脑的副屏请求…");
                    HubReceiverService.respond(this, id, "accept", error -> {
                        if (error != null) { setBusy(false); receiverDialogId = ""; showProgress(error.getMessage()); return; }
                        HubReceiverService.respond(this, id, "connecting", connectingError -> {
                            setBusy(false);
                            if (connectingError != null) { showProgress(connectingError.getMessage()); return; }
                            startSession("tablet", link, false, degrees, id);
                        });
                    });
                }).setNegativeButton("拒绝", (dialog, which) -> HubReceiverService.respond(this, id, "reject", error -> { if (error != null) showProgress(error.getMessage()); }))
                .setOnCancelListener(dialog -> { receiverDialogId = ""; }).create();
        receiverDialog.show(); receiverDialog.getButton(AlertDialog.BUTTON_POSITIVE).setTextColor(ACCENT_DARK); receiverDialog.getButton(AlertDialog.BUTTON_NEGATIVE).setTextColor(TEXT);
    }
    private void refreshNetwork() { if (networkChecking) return; networkChecking = true; network.status((state, error) -> { networkChecking = false; renderNetwork(state, error); }); }

    private List<String> enableTailnetFallback(HubConnectBudget budget) throws Exception {
        if (!store.transport().transport().usesAccount()) throw new java.io.IOException("当前通道不会自动启用异地连接。");
        HubSessionCoordinator owner = activeCoordinator;
        EmbeddedNetwork.Status current = awaitNetworkStatus(budget, 5000);
        if (current == null || !current.loggedIn())
            throw new java.io.IOException("局域网无法直连。请先在“连接”页面登录连接账号并完成一次设备授权，之后失败时会自动切换。");
        FallbackWait pending = new FallbackWait();
        pending.budget = budget; pending.owner = owner; pendingFallback = pending;
        main.post(() -> {
            if (isDestroyed() || isFinishing()) { completeFallback(pending, Collections.emptyList(), new java.io.IOException("连接界面已关闭。")); return; }
            if (fallbackExpired(pending)) return;
            Intent consent = network.prepareVpn();
            if (consent != null) {
                showProgress("本地网络无法直连，请允许一次安全连接；之后会自动重试…");
                try { startActivityForResult(consent, REQUEST_AUTO_VPN); }
                catch (Exception error) { pendingFallback = null; completeFallback(pending, Collections.emptyList(), error); }
            } else startFallbackTunnel(pending);
        });
        while (!pending.done.await(100, TimeUnit.MILLISECONDS)) if (fallbackExpired(pending)) break;
        if (pending.error != null) throw pending.error;
        return pending.hosts;
    }

    private boolean fallbackExpired(FallbackWait pending) {
        if (pending.completed) return true;
        try {
            pending.budget.check();
            if (isDestroyed() || pending.owner != activeCoordinator) throw new java.util.concurrent.CancellationException("连接已结束。");
            return false;
        } catch (Exception error) { completeFallback(pending, Collections.emptyList(), error); return true; }
    }

    private void startFallbackTunnel(FallbackWait pending) {
        if (fallbackExpired(pending)) return;
        showProgress("正在启动异地连接，可随时取消…");
        network.connect((state, error) -> {
            if (fallbackExpired(pending)) return;
            renderNetwork(state, error);
            if (error != null) { completeFallback(pending, Collections.emptyList(), new java.io.IOException(error)); return; }
            Thread waiter = new Thread(() -> {
                try {
                    long until = android.os.SystemClock.elapsedRealtime() + pending.budget.remaining(20000);
                    while (!EmbeddedVpnService.isTunnelActive() && android.os.SystemClock.elapsedRealtime() < until) {
                        if (fallbackExpired(pending)) return;
                        Thread.sleep(100);
                    }
                    if (fallbackExpired(pending)) return;
                    if (!EmbeddedVpnService.isTunnelActive()) throw new java.io.IOException("异地连接启动超时，请重试。");
                    showProgress("异地通道已建立，正在查找已绑定电脑…");
                    long peerUntil = android.os.SystemClock.elapsedRealtime() + pending.budget.remaining(6000);
                    List<String> hosts = Collections.emptyList();
                    do {
                        if (fallbackExpired(pending)) return;
                        EmbeddedNetwork.Status status = awaitNetworkStatus(pending.budget, 3000);
                        hosts = windowsPeerHosts(status);
                        if (!hosts.isEmpty()) break;
                        Thread.sleep(250);
                    } while (android.os.SystemClock.elapsedRealtime() < peerUntil);
                    completeFallback(pending, hosts, null);
                } catch (Exception failure) { completeFallback(pending, Collections.emptyList(), failure); }
            }, "Hub-Tailnet-Fallback");
            waiter.setDaemon(true); waiter.start();
        });
    }

    private EmbeddedNetwork.Status awaitNetworkStatus(HubConnectBudget budget, long maximumMs) throws Exception {
        CountDownLatch done = new CountDownLatch(1);
        AtomicReference<EmbeddedNetwork.Status> result = new AtomicReference<>();
        AtomicReference<String> error = new AtomicReference<>();
        network.status((state, failure) -> { result.set(state); error.set(failure); done.countDown(); });
        long until = android.os.SystemClock.elapsedRealtime() + budget.remaining(maximumMs);
        while (!done.await(100, TimeUnit.MILLISECONDS)) {
            budget.check();
            if (android.os.SystemClock.elapsedRealtime() >= until) throw new java.io.IOException("读取连接账号状态超时，请重试。");
        }
        budget.check();
        if (error.get() != null) throw new java.io.IOException(error.get());
        return result.get();
    }

    private List<String> windowsPeerHosts(EmbeddedNetwork.Status state) {
        if (state == null) return Collections.emptyList();
        java.util.LinkedHashSet<String> addresses = new java.util.LinkedHashSet<>();
        for (EmbeddedNetwork.Peer peer : state.peers) if ("windows".equalsIgnoreCase(peer.operatingSystem))
            for (String host : peer.addresses) if (HubRoutes.isUsableIpv4(host)) addresses.add(host);
        return new ArrayList<>(addresses);
    }

    private void completeFallback(FallbackWait pending, List<String> hosts, Exception error) {
        if (pending.completed) return;
        synchronized (pending) {
            if (pending.completed) return;
            pending.completed = true; pending.hosts = hosts; pending.error = error;
        }
        if (pendingFallback == pending) pendingFallback = null;
        pending.done.countDown();
    }
    private void renderNetwork(EmbeddedNetwork.Status state, String error) {
        if (isDestroyed()) return;
        if (error != null) { authorizationUrl = ""; lastNetworkState = null; peerList.removeAllViews(); networkStatus.setText(error); if (networkDetails != null) { networkDetails.setVisibility(View.GONE); } updateAccountActions(false, false, false); return; }
        if (state == null || !state.revision.equals(store.get("transportRevision", ""))) return;
        HubTransports.Settings settings;
        try { settings = store.transport(); } catch (IllegalArgumentException invalid) { authorizationUrl = ""; networkStatus.setText(invalid.getMessage()); return; }
        lastNetworkState = state; revealNetworkDetails = false;
        remoteAccount.setVisibility(settings.transport().usesAccount() ? View.VISIBLE : View.GONE);
        authorizationUrl = HubTransports.authorizationUrl(settings, state.authorizationUrl);
        networkStatus.setText(state.message + (settings.transport().usesAccount() ? state.loggedIn() ? " · 已登录" : " · 需要登录" : "" ) + (settings.transport().usesAccount() ? state.vpnActive ? " · 隧道已连接" : " · 隧道未启用" : ""));
        renderNetworkDetails(state);
        updateAccountActions(settings.transport().usesAccount(), state.loggedIn(), state.vpnActive);
        peerList.removeAllViews();
        List<EmbeddedNetwork.Peer> visiblePeers = new ArrayList<>();
        for (EmbeddedNetwork.Peer peer : state.peers) if ("windows".equalsIgnoreCase(peer.operatingSystem) && !peer.addresses.isEmpty()) visiblePeers.add(peer);
        visiblePeers.sort((left, right) -> Boolean.compare(right.online, left.online));
        LinearLayout offlineList = column();
        long offlineCount = visiblePeers.stream().filter(peer -> !peer.online).count();
        for (EmbeddedNetwork.Peer peer : visiblePeers) {
            Button entry = new Button(this); entry.setStateListAnimator(null); entry.setElevation(0);
            String route = peer.routeText == null || peer.routeText.isEmpty() ? "路径未知" : peer.routeText;
            entry.setText(peer.name + "\n" + (peer.online ? "在线" : "离线") + " · " + route + " · " + peerAddressSummary(peer.addresses));
            entry.setTextColor(TEXT); entry.setTextSize(14); entry.setGravity(Gravity.START | Gravity.CENTER_VERTICAL);
            entry.setPadding(dp(14), dp(8), dp(14), dp(8)); entry.setAllCaps(false); entry.setMinHeight(dp(68));
            GradientDrawable peerBackground = new GradientDrawable(); peerBackground.setColor(FIELD); peerBackground.setCornerRadius(dp(8)); peerBackground.setStroke(dp(1), BORDER);
            entry.setBackground(new RippleDrawable(android.content.res.ColorStateList.valueOf(RIPPLE), peerBackground, null));
            LinearLayout.LayoutParams peerSize = new LinearLayout.LayoutParams(-1, dp(72)); peerSize.topMargin = dp(6); (peer.online ? peerList : offlineList).addView(entry, peerSize);
            entry.setOnClickListener(v -> {
                String saved = store.get("hosts", "");
                StringBuilder merged = new StringBuilder(saved);
                for (String address : peer.addresses) if (!saved.contains(address)) {
                    if (merged.length() > 0) merged.append(",");
                    merged.append(address);
                }
                String addresses = merged.toString();
                hosts.setText(addresses); store.put("hosts", addresses); updateComputerAddress();
                showProgress("已选择账号内电脑 · " + peer.routeText + "；首次使用仍需绑定码确认远控权限。");
            });
        }
        if (offlineCount > 0) {
            textButton(peerList, (showOfflinePeers ? "收起" : "查看") + "离线电脑（" + offlineCount + "）", MUTED, v -> { showOfflinePeers = !showOfflinePeers; renderNetwork(lastNetworkState, null); });
            offlineList.setVisibility(showOfflinePeers ? View.VISIBLE : View.GONE); peerList.addView(offlineList);
        }
        if (peerList.getChildCount() == 0) peerList.addView(label(settings.transport().usesAccount() ? "登录后显示同账号的 Windows 电脑。" : "当前通道使用已绑定的电脑地址，无需云账号。", 13, MUTED));
    }
    private String peerAddressSummary(List<String> addresses) {
        if (addresses == null || addresses.isEmpty()) return "地址未知";
        String value = addresses.get(0);
        String[] parts = value.split("\\.");
        return parts.length == 4 ? parts[0] + "." + parts[1] + ".••.••" : "地址已隐藏";
    }
    private void updateAccountActions(boolean usesAccount, boolean loggedIn, boolean vpnActive) {
        if (loginAccountButton == null) return;
        loginAccountButton.setVisibility(usesAccount && !loggedIn ? View.VISIBLE : View.GONE);
        authorizationButton.setVisibility(usesAccount && !loggedIn && !authorizationUrl.isEmpty() ? View.VISIBLE : View.GONE);
        copyAuthorizationButton.setVisibility(usesAccount && !loggedIn && !authorizationUrl.isEmpty() ? View.VISIBLE : View.GONE);
        connectNetworkButton.setVisibility(usesAccount && loggedIn && !vpnActive ? View.VISIBLE : View.GONE);
        disconnectNetworkButton.setVisibility(usesAccount && loggedIn && vpnActive ? View.VISIBLE : View.GONE);
        logoutAccountButton.setVisibility(usesAccount && loggedIn ? View.VISIBLE : View.GONE);
    }
    private void renderNetworkDetails(EmbeddedNetwork.Status state) {
        if (networkDetails == null) return;
        if (state == null) { networkDetails.setVisibility(View.GONE); return; }
        String account = state.account.isEmpty() ? "" : (revealNetworkDetails ? state.account : maskAccount(state.account));
        String addresses = state.addresses.isEmpty() ? "" : (revealNetworkDetails ? android.text.TextUtils.join(" / ", state.addresses) : maskAddresses(state.addresses));
        String details = account.isEmpty() && addresses.isEmpty() ? "当前通道不显示账号地址。" : (account.isEmpty() ? "地址：" + addresses : "账号：" + account + (addresses.isEmpty() ? "" : "\n地址：" + addresses));
        networkDetails.setText(details + (revealNetworkDetails ? "\n点按隐藏详情" : "\n点按查看完整信息"));
        networkDetails.setContentDescription(revealNetworkDetails ? "连接账号和地址，点按隐藏" : "连接账号和地址已遮盖，点按查看完整信息");
        networkDetails.setVisibility(View.VISIBLE);
    }
    private String maskAccount(String value) {
        int at = value.indexOf('@');
        if (at <= 1) return "•••";
        return value.substring(0, 1) + "•••" + value.substring(at);
    }
    private String maskAddresses(List<String> values) {
        List<String> masked = new ArrayList<>();
        for (String value : values) {
            String[] parts = value.split("\\.");
            if (parts.length == 4) masked.add(parts[0] + "." + parts[1] + ".••.••");
            else masked.add("•••");
        }
        return android.text.TextUtils.join(" / ", masked);
    }
    private void openAuthorization() {
        if (authorizationUrl.isEmpty()) { showProgress("授权地址尚未就绪，请先点“登录连接账号”。"); return; }
        String safe;
        try { safe = HubTransports.authorizationUrl(store.transport(), authorizationUrl); }
        catch (IllegalArgumentException error) { safe = ""; }
        if (safe.isEmpty()) { authorizationUrl = ""; showProgress("授权地址未通过当前通道校验。"); return; }
        Uri uri = Uri.parse(safe);
        try { startActivity(new Intent(Intent.ACTION_VIEW, uri)); } catch (Exception e) { showProgress("请复制授权链接，在浏览器中打开。"); }
    }
    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (request == REQUEST_AUTO_VPN) {
            FallbackWait pending = pendingFallback; pendingFallback = null;
            if (pending == null) return;
            if (pending.completed) return;
            if (result == RESULT_OK) startFallbackTunnel(pending);
            else completeFallback(pending, Collections.emptyList(), new java.io.IOException("未允许安全连接；本地热点无法直连时，请到“连接”页面完成异地网络授权。"));
            return;
        }
        if (request == 81) { if (result == RESULT_OK) network.connect(this::renderNetwork); else showProgress("未授权建立异地连接；局域网和 USB 仍可使用。"); }
    }
}


