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
import com.limelight.Game;
import com.limelight.R;
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
    private TextView status, computerName, networkStatus;
    private TextView lastConnectionSummary, homeChannel, networkDetails;
    private TextView shortcutsState;
    private TextView deviceState, detailsState,previewCaption;
    private ImageView homePreview, detailsPreview;
    private View deviceStateDot;
    private boolean computerOnline, lastOperationFailed;
    private boolean presenceChecking;
    private long presenceValidUntil, nextPresenceAt, operationGeneration;
    private String presenceNetwork = "", mediaState = "", presenceHost = "";
    private volatile HubSessionCoordinator presenceCoordinator;
    private EditText hosts, invitation;
    private FrameLayout pageRoot;
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
    private TextView bindingFeedback, accountFeedback;
    private Button checkComputerButton;
    private EditText bindingAddressInput, bindingCodeInput;
    private boolean renderingProvider;
    private boolean providerChoicePending;
    private int accountParentPage;
    private boolean fullBindingOpen;
    private Object pageBack;
    private android.widget.PopupMenu homeMenu;
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
    private boolean pageShown;
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
    private final Runnable poll = new Runnable() { @Override public void run() { if (foreground) { refreshNetwork(); refreshComputerPresence(); main.postDelayed(this, Game.isHubConnectionStopping() ? 500 : 3000); } } };

    @Override public void onCreate(Bundle state) {
        super.onCreate(state); HubNavigation.prepareWindow(this); store = new HubStore(this); updates = HubUpdates.get(this); network = EmbeddedNetwork.get(this); refreshPalette();
        if (state != null) { currentPage = state.getInt("hubPage", 0); accountParentPage = state.getInt("hubAccountParent", 0); }
        build();
        if (HubReceiverService.isEnabled(this) && store.bound()) HubReceiverService.enable(this, true);
        handleReceiverIntent(getIntent());
        if (state == null && getIntent().hasExtra("HubPage")) showPage(getIntent().getIntExtra("HubPage", 0));
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
        super.onResume(); mediaState="";nextPresenceAt=0;renderDeviceState();refreshPreview();refreshShortcutsState(); main.postDelayed(() -> { if(foreground)refreshShortcutsState(); },600); foreground = true; main.removeCallbacks(poll); main.post(poll);
        HubReceiverService.listen(this::showPcRequest);
        if (!busy && activeCoordinator == null) {
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
    @Override protected void onPause() { foreground = false; cancelLocalBinding.set(true); if (localBinding != null) localBinding.cancel(); if (presenceCoordinator != null) presenceCoordinator.cancel(); HubReceiverService.listen(null); dismissPcRequest(); main.removeCallbacks(poll); super.onPause(); }
    @Override protected void onDestroy() {
        if (Build.VERSION.SDK_INT >= 33 && pageBack != null) Api33Back.unregister(this, pageBack);
        main.removeCallbacks(checkAppUpdates); HubUpdates.setPreparing(false);
        if (activeCoordinator != null) activeCoordinator.cancel();
        if (presenceCoordinator != null) presenceCoordinator.cancel();
        presenceWorker.shutdownNow();
        FallbackWait pending = pendingFallback;
        if (pending != null) completeFallback(pending, Collections.emptyList(), new java.io.IOException("连接界面已关闭。"));
        worker.shutdown(); main.removeCallbacks(poll); super.onDestroy();
    }
    @Override protected void onSaveInstanceState(Bundle state) {
        state.putInt("hubPage", currentPage); state.putInt("hubAccountParent", accountParentPage);
        super.onSaveInstanceState(state);
    }
    @Override public void onBackPressed() {
        if (homeMenu != null) { homeMenu.dismiss(); homeMenu = null; return; }
        if (currentPage == 6) { showPage(0); return; }
        if (currentPage == 5) { showPage(2); return; }
        if (currentPage == 4) { showPage(accountParentPage); return; }
        if (currentPage != 0) { showPage(0); return; }
        // Keep the root page visible instead of returning to the app that launched this task.
    }
    private static final class Api33Back {
        static Object register(Activity activity, Runnable action) {
            android.window.OnBackInvokedCallback callback = action::run;
            activity.getOnBackInvokedDispatcher().registerOnBackInvokedCallback(android.window.OnBackInvokedDispatcher.PRIORITY_DEFAULT, callback);
            return callback;
        }
        static void unregister(Activity activity, Object callback) {
            activity.getOnBackInvokedDispatcher().unregisterOnBackInvokedCallback((android.window.OnBackInvokedCallback) callback);
        }
    }
    private void openAccount() { accountParentPage = currentPage == 2 ? 2 : 0; showPage(4); }
    private void pageHeader(LinearLayout parent, String title, String backLabel) {
        LinearLayout header = new LinearLayout(this); header.setGravity(Gravity.CENTER_VERTICAL);
        android.widget.ImageButton back = new android.widget.ImageButton(this);
        back.setImageResource(R.drawable.ic_hub_back); back.setImageTintList(android.content.res.ColorStateList.valueOf(TEXT));
        back.setContentDescription(backLabel); back.setPadding(dp(12), dp(12), dp(12), dp(12));
        GradientDrawable feedback = new GradientDrawable(); feedback.setColor(Color.WHITE);
        back.setBackground(new RippleDrawable(android.content.res.ColorStateList.valueOf(RIPPLE), null, feedback));
        back.setOnClickListener(v -> onBackPressed()); header.addView(back, new LinearLayout.LayoutParams(dp(48), dp(48)));
        TextView heading = label(title, 24, TEXT); heading.setTypeface(null, Typeface.BOLD);
        heading.setGravity(Gravity.CENTER_VERTICAL); heading.setIncludeFontPadding(false);
        header.addView(heading, new LinearLayout.LayoutParams(0, -2, 1));
        parent.addView(header, new LinearLayout.LayoutParams(-1, -2)); gap(parent, 8);
    }
    private TextView pageFeedback(LinearLayout parent) {
        TextView feedback = label("", 14, TEXT); feedback.setVisibility(View.GONE);
        feedback.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
        parent.addView(feedback, new LinearLayout.LayoutParams(-1, -2)); return feedback;
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
        LinearLayout section = column();
        LinearLayout.LayoutParams size = new LinearLayout.LayoutParams(-1, -2); size.bottomMargin = dp(16); content.addView(section, size);
        if (!title.isEmpty()) {
            TextView heading = label(title, 13, ACCENT_DARK); heading.setTypeface(null, Typeface.BOLD);
            LinearLayout.LayoutParams headingSize = new LinearLayout.LayoutParams(-1, -2); headingSize.topMargin = dp(12); headingSize.bottomMargin = dp(8); section.addView(heading, headingSize);
        }
        LinearLayout card = column(); card.setPadding(dp(16), dp(8), dp(16), dp(12));
        GradientDrawable background = new GradientDrawable(); background.setColor(SURFACE); background.setCornerRadius(dp(16));
        card.setBackground(background); card.setElevation(0); section.addView(card, new LinearLayout.LayoutParams(-1,-2)); return card;
    }
    private LinearLayout nestedCard(String title) {
        LinearLayout card = column(); card.setPadding(0, dp(12), 0, dp(12));
        GradientDrawable bg = new GradientDrawable(); bg.setColor(Color.TRANSPARENT); card.setBackground(bg);
        TextView heading = label(title, 14, TEXT); heading.setTypeface(null, Typeface.BOLD); card.addView(heading); gap(card, 6); return card;
    }
    private LinearLayout insetGroup(String title) {
        LinearLayout group = column(); group.setPadding(0, dp(10), 0, dp(10));
        GradientDrawable bg = new GradientDrawable(); bg.setColor(Color.TRANSPARENT); group.setBackground(bg);
        TextView heading = label(title, 16, TEXT); heading.setTypeface(null, Typeface.BOLD); group.addView(heading); return group;
    }
    private View settingLink(LinearLayout parent, String title, String detail, View.OnClickListener action) {
        LinearLayout row = new LinearLayout(this); row.setGravity(Gravity.CENTER_VERTICAL); row.setPadding(0, dp(8), 0, dp(8));
        GradientDrawable bg = new GradientDrawable(); bg.setColor(Color.TRANSPARENT); bg.setCornerRadius(dp(8)); row.setBackground(new RippleDrawable(android.content.res.ColorStateList.valueOf(RIPPLE), bg, null));
        LinearLayout text = column(); text.setGravity(Gravity.CENTER_VERTICAL);
        TextView heading = label(title, 15, TEXT); heading.setTypeface(null, Typeface.BOLD); text.addView(heading);
        TextView secondary = label(detail == null ? "" : detail, 12, MUTED); secondary.setVisibility(View.GONE); text.addView(secondary);
        row.addView(text, new LinearLayout.LayoutParams(0, -2, 1));
        TextView arrow = label("›", 28, ACCENT_DARK); arrow.setGravity(Gravity.CENTER); arrow.setIncludeFontPadding(false); arrow.setContentDescription(title + "，打开");
        row.addView(arrow, new LinearLayout.LayoutParams(dp(28), dp(44)));
        LinearLayout.LayoutParams size = new LinearLayout.LayoutParams(-1, -2); size.topMargin = dp(8); parent.addView(row, size);
        row.setMinimumHeight(dp(52)); row.setClickable(true); row.setFocusable(true); row.setOnClickListener(action); modeActions.add(row); return row;
    }
    private View choiceSetting(LinearLayout parent,String title,String[] values,int selected,java.util.function.IntConsumer changed) {
        return choiceSetting(parent,title,values,selected,changed,null);
    }
    private View choiceSetting(LinearLayout parent,String title,String[] values,int selected,java.util.function.IntConsumer changed,String note) {
        final int[] choice={Math.max(0,Math.min(values.length-1,selected))};
        final View[] holder=new View[1];
        holder[0]=settingLink(parent,title,"",v->{
            AlertDialog.Builder dialog=HubStreamUi.hubDialog(this).setTitle(title);
            if(note!=null)dialog.setNeutralButton("说明",(d,w)->HubStreamUi.hubDialog(this).setTitle(title).setMessage(note).setPositiveButton("取消",null).show());
            dialog.setSingleChoiceItems(values,choice[0],(d,which)->{
                choice[0]=which;settingValue(holder[0],values[which]);d.dismiss();changed.accept(which);
            }).setNegativeButton("取消",null).show();
        });
        settingValue(holder[0],values[choice[0]]);return holder[0];
    }
    private void settingValue(View row,String value) {
        LinearLayout text=(LinearLayout)((LinearLayout)row).getChildAt(0);
        TextView current=(TextView)text.getChildAt(1);current.setText(value);current.setVisibility(View.VISIBLE);
        row.setContentDescription(((TextView)text.getChildAt(0)).getText()+"，"+value);
    }
    /** Compact entry cards keep the common actions visible without a long settings scroll. */
    private View moduleCell(String title, String detail, boolean primary, View.OnClickListener action) {
        LinearLayout cell = column();
        cell.setGravity(Gravity.CENTER);
        cell.setPadding(dp(16), dp(14), dp(16), dp(14));
        cell.setBackground(moduleBackground(primary,false));
        TextView name = label(title, 16, primary ? HubTheme.onAccent(this) : ACCENT_DARK); name.setGravity(Gravity.CENTER);
        name.setTypeface(null, Typeface.BOLD);
        TextView caption = label(detail, 12, MUTED);
        caption.setMaxLines(2);
        cell.addView(name);
        caption.setVisibility(View.GONE); cell.addView(caption, new LinearLayout.LayoutParams(-1, -2));
        cell.setMinimumHeight(dp(56));
        cell.setClickable(true);
        cell.setFocusable(true);
        cell.setOnClickListener(action);
        cell.setTag(Boolean.valueOf(primary));modeActions.add(cell);
        return cell;
    }
    private android.graphics.drawable.Drawable moduleBackground(boolean primary,boolean disabled) {
        GradientDrawable face=new GradientDrawable();
        face.setColor(disabled?FIELD:primary?HubTheme.primary(this):SELECTED);face.setCornerRadius(dp(12));
        if(disabled)face.setStroke(dp(1),BORDER);
        return new RippleDrawable(android.content.res.ColorStateList.valueOf(RIPPLE),face,null);
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
        Button v = new Button(this); v.setText(title); HubButtons.apply(v, primary); v.setMinHeight(dp(52));
        LinearLayout.LayoutParams size = new LinearLayout.LayoutParams(-1, -2); size.topMargin = dp(8); parent.addView(v, size);
        v.setOnClickListener(action); actions.add(v); return v;
    }
    private Button compactButton(LinearLayout parent, String title, View.OnClickListener action) {
        Button v = new Button(this); HubButtons.apply(v, false); v.setText(title); v.setTextSize(14);
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
        GradientDrawable shape = new GradientDrawable(); shape.setColor(FIELD); shape.setCornerRadius(dp(8)); shape.setStroke(dp(1), HubTheme.controlBorder(this)); field.setBackground(shape);
        field.setMinHeight(dp(multiLine ? 96 : 52));
        LinearLayout.LayoutParams fs = new LinearLayout.LayoutParams(-1, -2); fs.topMargin = dp(6); parent.addView(field, fs); return field;
    }
    private ScrollView pageScroll(LinearLayout page) { return pageScroll(page, 720); }
    private ScrollView pageScroll(LinearLayout page, int maxDp) {
        return pageScroll(page, maxDp, false);
    }
    private ScrollView pageScroll(LinearLayout page, int maxDp, boolean balanceHome) {
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setFocusableInTouchMode(true);
        scroll.setDescendantFocusability(android.view.ViewGroup.FOCUS_BEFORE_DESCENDANTS);
        FrameLayout centered = new FrameLayout(this);
        scroll.addView(centered, new ScrollView.LayoutParams(-1, -2));
        HubWindowLayout layout = HubWindowLayout.of(this);
        page.setPadding(dp(layout.pageInsetDp()), dp(layout.compact() ? 16 : 24), dp(layout.pageInsetDp()), dp(30));
        int width = dp(Math.max(1, Math.min(layout.widthDp - layout.railWidthDp(), maxDp)));
        int gravity = Gravity.TOP | Gravity.CENTER_HORIZONTAL;
        centered.addView(page, new FrameLayout.LayoutParams(width, -2, gravity));
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
        if (pageSlot == null || pageIndex < 0 || pageIndex > 6 || pageIndex == 3) return;
        boolean animate = pageShown;
        currentPage = pageIndex;
        int slot = pageIndex == 6 ? 5 : pageIndex == 4 ? 3 : pageIndex == 5 ? 4 : pageIndex;
        for (int i = 0; i < pageSlot.getChildCount(); i++) pageSlot.getChildAt(i).setVisibility(i == slot ? View.VISIBLE : View.GONE);
        View selected = pageSlot.getChildAt(slot);
        if (animate) {
            boolean subpage = pageIndex == 1 || pageIndex >= 4;
            HubMotion.pageIn(this, selected, subpage, pageIndex == 6);
        } else {
            selected.setAlpha(1f); selected.setTranslationX(0f); selected.setTranslationY(0f);
            selected.setScaleX(1f); selected.setScaleY(1f);
        }
        pageShown = true;
        selected.requestFocus();
        if (navigation != null) navigation.select(pageIndex == 6 ? 0 : pageIndex == 4 ? accountParentPage : pageIndex == 5 ? 2 : pageIndex);
        if (Build.VERSION.SDK_INT >= 33) {
            if (pageBack == null) pageBack = Api33Back.register(this, this::onBackPressed);
        }
        if(pageIndex==2)refreshShortcutsState();
    }
    private void build() {
        pageShown = false;
        actions.clear();
        modeActions.clear();
        FrameLayout page = new FrameLayout(this); pageRoot = page;
        page.setFocusableInTouchMode(true); page.setBackgroundColor(BACKGROUND); setContentView(page); page.requestFocus();
        pageSlot = new FrameLayout(this);
        LinearLayout home = column(), connection = column(), settings = column(), accountPage = column(), advancedPage = column(), devicePage=column();
        HubWindowLayout homeLayout = HubWindowLayout.of(this);
        pageSlot.addView(pageScroll(home), new FrameLayout.LayoutParams(-1, -1));
        pageSlot.addView(pageScroll(connection), new FrameLayout.LayoutParams(-1, -1));
        pageSlot.addView(pageScroll(settings), new FrameLayout.LayoutParams(-1, -1));
        pageSlot.addView(pageScroll(accountPage), new FrameLayout.LayoutParams(-1, -1));
        pageSlot.addView(pageScroll(advancedPage), new FrameLayout.LayoutParams(-1, -1));
        pageSlot.addView(pageScroll(devicePage),new FrameLayout.LayoutParams(-1,-1));
        navigation = new HubNavigation(this, pageSlot, currentPage, index -> {
            if (index == 3) HubFilesActivity.open(this); else showPage(index);
        });
        page.addView(navigation.view, new FrameLayout.LayoutParams(-1, -1));

        content = home;
        LinearLayout homeHeader = new LinearLayout(this); homeHeader.setGravity(Gravity.CENTER_VERTICAL);
        TextView title = label("全部设备", 24, TEXT); title.setTypeface(null, Typeface.BOLD); homeHeader.addView(title, new LinearLayout.LayoutParams(0, -2, 1));
        Button add = textButton(homeHeader, HubWindowLayout.of(this).compact() ? "+" : "+ 添加电脑", ACCENT_DARK, v -> showPage(1)); add.setContentDescription("添加电脑"); add.setGravity(Gravity.CENTER); add.setLayoutParams(new LinearLayout.LayoutParams(HubWindowLayout.of(this).compact() ? dp(48) : -2, dp(48)));
        Button more = textButton(homeHeader, "⋯", TEXT, v -> {
            android.widget.PopupMenu menu = new android.widget.PopupMenu(HubStreamUi.hubContext(this), v);
            menu.getMenu().add("连接诊断").setOnMenuItemClickListener(item -> { showConnectionDiagnostics(); return true; });
            homeMenu = menu;
            menu.setOnDismissListener(closed -> { if (homeMenu == closed) homeMenu = null; });
            menu.show();
        });
        more.setContentDescription("更多"); more.setGravity(Gravity.CENTER); more.setLayoutParams(new LinearLayout.LayoutParams(dp(48), dp(48)));
        home.addView(homeHeader, new LinearLayout.LayoutParams(-1, -2)); gap(home, 8);
        LinearLayout computer = column(); computer.setPadding(dp(20),dp(20),dp(20),dp(20));
        GradientDrawable computerBg = new GradientDrawable(); computerBg.setColor(SURFACE);computerBg.setCornerRadius(dp(20));computer.setBackground(computerBg);computer.setElevation(0);
        LinearLayout.LayoutParams computerSize = new LinearLayout.LayoutParams(-1,-2);computerSize.topMargin=dp(16);home.addView(computer,computerSize);
        LinearLayout identity = new LinearLayout(this); identity.setGravity(Gravity.CENTER_VERTICAL);
        ImageView icon = new ImageView(this);homePreview=icon;icon.setImageResource(R.drawable.ic_hub_computer);
        icon.setImageTintList(android.content.res.ColorStateList.valueOf(ACCENT_DARK)); icon.setPadding(dp(12),dp(12),dp(12),dp(12));
        GradientDrawable iconBg = new GradientDrawable(); iconBg.setColor(SELECTED); iconBg.setCornerRadius(dp(8)); icon.setBackground(iconBg);
        icon.setContentDescription("电脑");
        identity.addView(icon, new LinearLayout.LayoutParams(dp(homeLayout.compact()?72:112),dp(70)));
        identity.setClickable(true);identity.setFocusable(true);identity.setContentDescription("打开已绑定电脑详情");identity.setOnClickListener(v->showPage(6));
        LinearLayout identityText = column(); LinearLayout.LayoutParams nameSize = new LinearLayout.LayoutParams(0, -2, 1); nameSize.leftMargin = dp(14); identity.addView(identityText, nameSize);
        computerName = label(store.get("deviceName", "未绑定电脑"), 20, TEXT); computerName.setTypeface(null, Typeface.BOLD);
        computerName.setMaxLines(1); computerName.setEllipsize(android.text.TextUtils.TruncateAt.END); identityText.addView(computerName);
        LinearLayout stateRow = new LinearLayout(this); stateRow.setGravity(Gravity.CENTER_VERTICAL);
        deviceStateDot = new View(this); deviceStateDot.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO); stateRow.addView(deviceStateDot, new LinearLayout.LayoutParams(dp(8), dp(8)));
        deviceState = label("已绑定 · 待检查", 14, MUTED); LinearLayout.LayoutParams stateSize = new LinearLayout.LayoutParams(-2, -2); stateSize.leftMargin = dp(7); stateRow.addView(deviceState, stateSize); identityText.addView(stateRow);
        computer.addView(identity); gap(computer, 18);
        computer.setOnClickListener(v->showPage(6)); computer.setFocusable(true);
        moduleRow(computer,
                moduleCell("远程控制", "查看并操作电脑主屏", true, v -> startSession("remote", "auto", false, 0)),
                moduleCell("用作副屏", "作为电脑第二块屏幕", false, v -> HubStreamUi.hubDialog(this)
                        .setTitle("副屏连接方式").setItems(new String[]{"无线网络", "USB 网络共享"},
                            (dialog, which) -> startSession("tablet", which == 0 ? "lan" : "usb", false, selectedOrientation())).show()));

        gap(computer,20);
        View connectionDivider=new View(this);connectionDivider.setBackgroundColor(BORDER);computer.addView(connectionDivider,new LinearLayout.LayoutParams(-1,dp(1)));gap(computer,16);
        homeChannel = label("",13,MUTED);computer.addView(homeChannel);gap(computer,8);
        lastConnectionSummary = label("",13,MUTED);computer.addView(lastConnectionSummary);refreshDiagnosticsCard();updateComputerAddress();
        textButton(computer,"查看设备详情 ›",ACCENT_DARK,v->showPage(6));
        status = label(store.bound() ? "" : "点“+ 添加电脑”绑定。", 14, MUTED);
        status.setVisibility(store.bound() ? View.GONE : View.VISIBLE);
        status.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE); computer.addView(status); renderDeviceState();
        cancelConnection = button(computer, "取消连接", false, v -> {
            HubSessionCoordinator coordinator = activeCoordinator;
            if (coordinator != null) { coordinator.cancel(); showProgress("正在取消并还原电脑显示…"); cancelConnection.setEnabled(false); }
        });
        cancelConnection.setVisibility(View.GONE);

        content = connection;
        pageHeader(connection, "添加电脑", "返回全部设备");
        bindingFeedback = pageFeedback(connection);
        buildAddressBinding();
        LinearLayout binding = card("其他绑定方式");
        bindingSummary = label("", 15, TEXT); binding.addView(bindingSummary);
        bindingForm = column(); binding.addView(bindingForm);
        textButton(binding, "使用完整绑定码", ACCENT_DARK, v -> { fullBindingOpen = !fullBindingOpen; bindingForm.setVisibility(fullBindingOpen ? View.VISIBLE : View.GONE); });
        hosts = field(bindingForm, "电脑地址", "输入电脑地址", false); hosts.setId(View.generateViewId()); hosts.setContentDescription("电脑地址"); hosts.setText(bindingAddresses());
        invitation = field(bindingForm, "电脑端设备绑定码", "粘贴电脑端显示的绑定码", true); invitation.setContentDescription("电脑端设备绑定码"); invitation.setImportantForAutofill(View.IMPORTANT_FOR_AUTOFILL_NO);
        button(bindingForm, "验证并绑定", true, v -> bindComputer());
        checkComputerButton = null;
        refreshBindingSummary();

        content = accountPage;
        pageHeader(accountPage, "账号与连接", "返回上一级");
        accountFeedback = pageFeedback(accountPage);
        buildProviderCard();
        LinearLayout account = card("账号与异地连接"); networkStatus = label("正在读取异地网络状态…", 14, MUTED); account.addView(networkStatus);
        networkDetails = label("", 14, TEXT); networkDetails.setPadding(dp(12), dp(9), dp(12), dp(9));
        networkDetails.setMinHeight(dp(48)); networkDetails.setGravity(Gravity.CENTER_VERTICAL); networkDetails.setClickable(true); networkDetails.setFocusable(true);
        GradientDrawable networkDetailBg = new GradientDrawable(); networkDetailBg.setColor(FIELD); networkDetailBg.setCornerRadius(dp(8)); networkDetails.setBackground(networkDetailBg);
        networkDetails.setVisibility(View.GONE); networkDetails.setOnClickListener(v -> { revealNetworkDetails = !revealNetworkDetails; renderNetworkDetails(lastNetworkState); }); account.addView(networkDetails);
        remoteAccount = column(); account.addView(remoteAccount);
        loginAccountButton = button(remoteAccount, "登录连接账号", false, v -> loginAccount());
        authorizationButton = button(remoteAccount, "打开账号授权页", false, v -> openAuthorization());
        copyAuthorizationButton = button(remoteAccount, "复制授权链接", false, v -> { if (authorizationUrl.isEmpty()) { showProgress("请先获取授权地址。"); return; } ((ClipboardManager)getSystemService(CLIPBOARD_SERVICE)).setPrimaryClip(ClipData.newPlainText("我控制账号授权", authorizationUrl)); showProgress("授权链接已复制。"); });
        connectNetworkButton = button(remoteAccount, "连接异地网络", true, v -> { Intent consent = network.prepareVpn(); if (consent != null) startActivityForResult(consent, 81); else network.connect(this::renderNetwork); });
        disconnectNetworkButton = button(remoteAccount, "断开异地网络", false, v -> network.disconnect(this::renderNetwork));
        logoutAccountButton = button(account, "退出当前账号", false, v -> HubStreamUi.hubDialog(this).setTitle("退出当前连接账号？")
                .setMessage("退出后可登录其他账号；电脑绑定和本地通道设置会保留。")
                .setNegativeButton("取消", null).setPositiveButton("退出并切换", (dialog, which) -> { authorizationUrl = ""; network.logout(this::renderNetwork); }).show());
        boolean accountRequired = store.transport().transport().usesAccount();
        remoteAccount.setVisibility(accountRequired ? View.VISIBLE : View.GONE); updateAccountActions(accountRequired, false, false);
        gap(account, 12); account.addView(label("账号内的电脑", 15, TEXT)); account.addView(label("离线电脑可从本机列表移除；彻底删除账号内设备需到相应账号后台。",13,MUTED)); peerList = column(); account.addView(peerList);

        content = settings;
        TextView settingsTitle = label("设置", 24, TEXT); settingsTitle.setTypeface(null, Typeface.BOLD); settings.addView(settingsTitle); gap(settings, 18);
        LinearLayout appearance = card("外观");
        choiceSetting(appearance,"应用主题",new String[]{getString(R.string.hub_theme_system),getString(R.string.hub_theme_light),getString(R.string.hub_theme_dark)},HubTheme.mode(this),position->{
            if(position!=HubTheme.mode(this)){HubTheme.setMode(this,position);refreshPalette();build();}
        });
        LinearLayout connectionSettings = card("连接");
        settingLink(connectionSettings, "账号与连接", "", v -> openAccount());

        LinearLayout receiver = card("副屏");
        receiverSwitch = new HubSwitch(this); receiverSwitch.setText("接收电脑副屏请求"); receiverSwitch.setTextColor(TEXT); receiverSwitch.setTextSize(16); receiverSwitch.setPadding(0, dp(8), 0, dp(8));
        receiverSwitch.setChecked(HubReceiverService.isEnabled(this)); receiver.addView(receiverSwitch, new LinearLayout.LayoutParams(-1, dp(56)));
        receiverSwitch.setOnCheckedChangeListener((view, enabled) -> {
            if (enabled && !store.bound()) { receiverSwitch.setChecked(false); showProgress("请先绑定电脑。"); return; }
            if (enabled && Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS}, 82);
            }
            try { HubReceiverService.enable(this, enabled); showProgress(enabled ? "已开启接收；电脑可在副屏页面发起请求。" : "已关闭接收电脑副屏请求。"); }
            catch (RuntimeException e) { receiverSwitch.setChecked(false); showProgress("暂时无法启用接收，请重新打开应用后重试。"); }
        });
        try { selectedOrientationIndex = Math.max(0, Math.min(4, Integer.parseInt(store.get("tabletOrientation", "0")))); } catch (NumberFormatException ignored) { selectedOrientationIndex = 0; }
        choiceSetting(receiver,"平板发起时的方向",new String[]{"自动","横屏","竖屏","反向横屏","反向竖屏"},selectedOrientationIndex,position->{selectedOrientationIndex=position;store.put("tabletOrientation", Integer.toString(position));},"仅用于平板点击“用作副屏”。电脑发起时采用电脑端的方向设置。");

        final View[] tabletQualityRow = new View[1];
        tabletQualityRow[0] = settingLink(receiver, "副屏画质", tabletQualityLabel(), v -> HubStreamUi.hubDialog(this)
                .setTitle("副屏画质 · 下次连接生效")
                .setSingleChoiceItems(new String[]{"稳定优先", "清晰优先（需要更好网络）"}, "clear".equals(store.get("tabletQuality", "stable")) ? 1 : 0,
                        (dialog, which) -> { store.put("tabletQuality", which == 1 ? "clear" : "stable");
                            ((TextView)((LinearLayout)((LinearLayout)tabletQualityRow[0]).getChildAt(0)).getChildAt(1)).setText(tabletQualityLabel()); dialog.dismiss(); }).setNegativeButton("取消", null).show());
        settingLink(receiver, "USB 网络共享设置", "", v -> { try { startActivity(new Intent("android.settings.TETHER_SETTINGS")); } catch (Exception e) { startActivity(new Intent(Settings.ACTION_WIRELESS_SETTINGS)); } });
        LinearLayout display = card("远程控制");
        settingLink(display, "远程控制画质", "", v -> showQualityPresets());
        final View[] panelMode={null};
        panelMode[0]=settingLink(display,"面板展开方式",HubPanelLayout.label(this),v->HubPanelLayout.show(this,()->settingValue(panelMode[0],HubPanelLayout.label(this))));
        LinearLayout sound = card("声音输出");

        android.content.SharedPreferences audioPrefs=androidx.preference.PreferenceManager.getDefaultSharedPreferences(this);
        String savedAudio=audioPrefs.getString("streamhub_audio_output","computer");
        String[] audioModes={"仅平板扬声器","仅电脑扬声器","电脑和平板同时输出"};
        int audioSelection="tablet".equals(savedAudio)?0:"both".equals(savedAudio)?2:1;
        choiceSetting(sound,"输出设备",audioModes,audioSelection,position->{
            String mode=position==0?"tablet":position==1?"computer":"both";
            audioPrefs.edit().putString("streamhub_audio_output",mode).putBoolean("checkbox_host_audio",position!=0).apply();
        },"仅平板输出会临时接管电脑音频；其余选项保留电脑当前设备。下次连接生效。");
        LinearLayout input = card("输入");
        View shortcutsRow = settingLink(input, "系统快捷键", "", v -> showShortcuts());
        shortcutsState = (TextView)((LinearLayout)((LinearLayout)shortcutsRow).getChildAt(0)).getChildAt(1);
        shortcutsState.setVisibility(View.VISIBLE); refreshShortcutsState();
        LinearLayout updateCard = card("版本与更新");
        // Match the visible inset of the vertically centered check row.
        updateCard.setPadding(dp(16), dp(28), dp(16), dp(12));
        updateCard.addView(new HubUpdatePanel(this, updates, () -> !busy && activeCoordinator == null), new LinearLayout.LayoutParams(-1, -2));
        LinearLayout advanced = card("高级");
        settingLink(advanced, "音视频与输入高级设置", "", v -> showPage(5));
        settingLink(advanced,"屏幕键盘布局","",v->startActivity(new Intent(this,HubKeyboardLayoutsActivity.class)));
        settingLink(advanced,"兼容主机","独立管理兼容串流主机与配对",v->startActivity(new Intent(this,HubCompatibleActivity.class)));
        if (new HubLoginSecrets(this).saved() || "legacy".equals(store.hostPolicyMode()))
            settingLink(advanced, "旧版登录设置", "", v -> HubStreamUi.hubDialog(this).setTitle("旧版登录设置").setView(HubLoginSettings.view(this,false,null)).setPositiveButton("取消",null).show());
        LinearLayout help = card("帮助");
        settingLink(help, "使用手册", "", v -> showManual());
        LinearLayout authorization = card("设备授权");
        Button clearBinding = textButton(authorization, "清除当前电脑绑定", DANGER, v -> HubStreamUi.hubDialog(this)
                .setTitle("清除电脑绑定？").setMessage("清除后需要重新输入电脑端绑定码，已保存的串流证书也会失效。")
                .setNegativeButton("取消", null).setPositiveButton("清除", (dialog, which) -> {
                    store.clearBinding(); refreshPreview();computerOnline = false; presenceValidUntil = 0; lastOperationFailed = false; mediaState = "";
                    computerName.setText("未绑定电脑"); showProgress("电脑绑定已清除。");
                }).show().getButton(AlertDialog.BUTTON_POSITIVE).setTextColor(DANGER));
        settings.addView(label("我控制 · iControl", 13, MUTED));
        content = advancedPage;
        pageHeader(advancedPage, "音视频与输入", "返回设置");
        buildAdvancedSettings();
        content=devicePage;pageHeader(devicePage,store.get("deviceName","设备详情"),"返回全部设备");
        detailsState=label("",14,MUTED);detailsState.setPadding(dp(48),0,0,dp(8));devicePage.addView(detailsState);
        LinearLayout preview=card("");preview.setPadding(dp(16),dp(16),dp(16),dp(16));
        FrameLayout previewArea=new FrameLayout(this);previewArea.setBackgroundColor(FIELD);
        preview.addView(previewArea,new LinearLayout.LayoutParams(-1,dp(220)));
        detailsPreview=new ImageView(this);detailsPreview.setScaleType(ImageView.ScaleType.CENTER_CROP);
        detailsPreview.setContentDescription("上次连接预览，仅保存在此平板");
        previewArea.addView(detailsPreview,new FrameLayout.LayoutParams(-1,-1));
        previewCaption=label("连接成功后显示预览\n仅保存在此平板",13,MUTED);previewCaption.setGravity(Gravity.CENTER);
        previewCaption.setPadding(dp(16),dp(16),dp(16),dp(16));
        previewArea.addView(previewCaption,new FrameLayout.LayoutParams(-1,-1));
        LinearLayout operations=card("连接与操作");
        moduleRow(operations,moduleCell("远程控制","进入电脑桌面",true,v->startSession("remote","auto",false,0)),
            moduleCell("用作副屏","作为电脑第二块屏幕",false,v->startSession("tablet","auto",false,selectedOrientation())));
        settingLink(operations,"文件传输","浏览电脑文件和传输任务",v->HubFilesActivity.open(this));
        settingLink(operations,"连接诊断","查看最近连接记录并检查连接",v->showConnectionDiagnostics());
        settingLink(operations,"连接地址","已绑定电脑的地址信息",v->showConnectionAddresses());
        settingLink(operations,"设备设置","方向、画质、输入与声音",v->showPage(2));
        LinearLayout previews=card("本机预览");
        textButton(previews,"清除上次连接预览",DANGER,v->HubStreamUi.hubDialog(this).setTitle("清除电脑预览？")
            .setMessage("只清除此平板缓存的预览图片；下次连接成功后会重新生成。")
            .setNegativeButton("取消",null).setPositiveButton("清除",(d,w)->{HubDevicePreview.forget(this,store.get("deviceId",""));refreshPreview();}).show());
        refreshPreview();renderDeviceState();
        showPage(currentPage);
        setBusy(busy);
    }
    private void refreshPreview(){
        if(homePreview==null)return;
        android.graphics.Bitmap image=HubDevicePreview.read(this,store.get("deviceId",""));
        if(previewCaption!=null)previewCaption.setVisibility(image==null?View.VISIBLE:View.GONE);
        if(detailsPreview!=null)detailsPreview.setVisibility(image==null?View.INVISIBLE:View.VISIBLE);
        for(ImageView target:new ImageView[]{homePreview,detailsPreview})if(target!=null){
            if(image!=null){target.setPadding(0,0,0,0);target.setImageTintList(null);target.setImageBitmap(image);target.setScaleType(ImageView.ScaleType.CENTER_CROP);}
            else{target.setImageResource(R.drawable.ic_hub_computer);target.setImageTintList(android.content.res.ColorStateList.valueOf(ACCENT_DARK));target.setScaleType(ImageView.ScaleType.CENTER_INSIDE);target.setPadding(dp(12),dp(12),dp(12),dp(12));}
        }
    }
    private String actualRoute(String host) {
        List<HubAddressLabels.Link> links = HubAddressLabels.currentLinks();
        try {
            HubTransports.Settings settings = store.transport();
            return HubAddressLabels.actualRoute(host, links, settings.provider,
                    lastNetworkState != null && lastNetworkState.vpnActive,
                    HubRoutes.usableAddresses(settings.manualAddresses));
        } catch (IllegalArgumentException ignored) { return HubAddressLabels.describe(host, links); }
    }
    private void buildAdvancedSettings() {
        android.content.SharedPreferences prefs = HubAdvancedPreferences.active(this);
        LinearLayout video = card("视频 · 下次连接生效");
        final View[] fps = new View[1];
        fps[0] = settingLink(video,"帧率","",v -> {
            String current=HubAdvancedPreferences.active(this).getString("list_fps","60");
            String[] values={"30","60","90","120"};int selected=4;
            for(int i=0;i<values.length;i++)if(values[i].equals(current))selected=i;
            HubStreamUi.hubDialog(this).setTitle("帧率")
                .setSingleChoiceItems(new String[]{"30 FPS","60 FPS","90 FPS","120 FPS","自定义…"},selected,(d,which)->{
                    d.dismiss();if(which==4){showCustomFrameRate(fps[0]);return;}
                    String value=values[which];
                    if(!HubAdvancedPreferences.save(this,java.util.Map.of("list_fps",value,"custom_refresh_rate",value)))return;
                    store.put("qualityPreset","advanced");settingValue(fps[0],value+" FPS");
                }).setNegativeButton("取消",null).show();
        });
        settingValue(fps[0],prefs.getString("list_fps","60")+" FPS");
        final View[] bitrate = new View[1];
        bitrate[0] = settingLink(video, "码率", "", v -> {
            LinearLayout input = column(); input.setPadding(dp(24), dp(8), dp(24), dp(8));
            EditText value = field(input, "码率（Mbps）", "1–150", false); value.setInputType(InputType.TYPE_CLASS_NUMBER | InputType.TYPE_NUMBER_FLAG_DECIMAL);
            value.setText(scaledNumber(prefs.getInt("seekbar_bitrate_kbps", prefs.getInt("seekbar_bitrate",18)*1000),1000));
            AlertDialog dialog = HubStreamUi.hubDialog(this).setTitle("码率").setView(input).setNegativeButton("取消", null).setPositiveButton("保存", null).create();
            dialog.setOnShowListener(d -> dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(button -> {
                int selected; try { selected = scaledInput(value.getText().toString(),1,150,1000); }
                catch (IllegalArgumentException error) { value.setError("请输入1–150之间的数值，最多三位小数"); return; }
                if (!HubAdvancedPreferences.save(this, java.util.Map.of("seekbar_bitrate_kbps", selected))) return; store.put("qualityPreset", "advanced");
                settingValue(bitrate[0], scaledNumber(selected,1000) + " Mbps"); dialog.dismiss();
            })); dialog.show();
        });
        settingValue(bitrate[0], prefs.contains("seekbar_bitrate_kbps") || prefs.contains("seekbar_bitrate")
            ? scaledNumber(prefs.getInt("seekbar_bitrate_kbps",prefs.getInt("seekbar_bitrate",18)*1000),1000)+" Mbps" : "自动");
        preferenceChoice(video,"视频编码","video_format",new String[]{"自动","H.264","HEVC","AV1"},new String[]{"auto","neverh265","forceh265","forceav1"},"auto");
        preferenceChoice(video,"画面缩放","list_video_scale_mode",new String[]{"保持比例","填满画面","拉伸"},new String[]{"fit","fill","stretch"},"fit");
        preferenceChoice(video,"帧调度","frame_pacing",new String[]{"低延迟","均衡","限制帧率","流畅优先"},new String[]{"latency","balanced","cap-fps","smoothness"},"latency");
        preferenceNumber(video,"移动网络码率","seekbar_metered_bitrate_kbps",0,0,150,"Mbps",1000,"0为自动；仅在系统标记为按流量计费的网络上使用。");
        preferenceToggle(video,"完整色彩范围","checkbox_full_range",false);
        Switch hdr=preferenceToggle(video,"HDR画面","checkbox_enable_hdr",false);
        android.view.Display display=getWindowManager().getDefaultDisplay();
        boolean hdrAvailable=Build.VERSION.SDK_INT>=24 && display!=null && display.getHdrCapabilities()!=null && display.getHdrCapabilities().getSupportedHdrTypes().length>0;
        hdr.setEnabled(hdrAvailable);
        if (!hdrAvailable) hdr.setTooltipText("当前屏幕不支持 HDR。");
        LinearLayout controls = card("输入 · 下次连接生效");
        preferenceToggle(controls,"绝对鼠标坐标","checkbox_absolute_mouse_mode",false);
        preferenceToggle(controls,"显示本地鼠标指针","checkbox_mouse_local_cursor",false);
        preferenceToggle(controls,"多指手势","checkbox_multi_touch_gestures",false);
        preferenceToggle(controls,"将鼠标侧键发送到电脑","checkbox_mouse_nav_buttons",false);
        preferenceNumber(controls,"触控板水平灵敏度","seekbar_trackpad_sensitivity_x",100,-200,200,"%",1,"100为原速，负值反转方向，0停用此方向移动。");
        preferenceNumber(controls,"触控板垂直灵敏度","seekbar_trackpad_sensitivity_y",100,-200,200,"%",1,null);
        preferenceNumber(controls,"触控板拖动等待","seekbar_trackpad_drag_drop_threshold",250,0,2000,"ms",1,null);
        preferenceToggle(controls,"触控板拖动振动反馈","checkbox_trackpad_drag_drop_vibration",false);
        preferenceToggle(controls,"交换触控板横纵方向","checkbox_trackpad_swap_axis",false);
        preferenceToggle(controls,"实体键盘使用标准QWERTY映射","checkbox_force_qwerty",true);
        LinearLayout gamepadCard = card("手柄 · 下次连接生效");
        LinearLayout gamepad=column();gamepad.setVisibility(View.GONE);
        Button expandGamepad=textButton(gamepadCard,"展开手柄设置 ▾",ACCENT_DARK,v->{});
        expandGamepad.setOnClickListener(v->{boolean expanded=gamepad.getVisibility()!=View.VISIBLE;
            gamepad.setVisibility(expanded?View.VISIBLE:View.GONE);expandGamepad.setText(expanded?"收起手柄设置 ▴":"展开手柄设置 ▾");});
        gamepadCard.addView(gamepad);
        preferenceToggle(gamepad,"屏幕手柄","checkbox_show_onscreen_controls",false);
        preferenceToggle(gamepad,"连接手柄时隐藏屏幕手柄","checkbox_hide_osc_when_has_gamepad",true);
        preferenceToggle(gamepad,"多手柄分别控制","checkbox_multi_controller",true);
        preferenceToggle(gamepad,"互换 A/B 与 X/Y 按键","checkbox_flip_face_buttons",false);
        preferenceToggle(gamepad,"手柄触控板控制鼠标","checkbox_gamepad_touchpad_as_mouse",false);
        preferenceNumber(gamepad,"摇杆死区","seekbar_deadzone",5,-20,20,"%",1,"正值过滤中心抖动，负值用于补偿手柄已有死区。");
        preferenceToggle(gamepad,"USB手柄驱动","checkbox_usb_driver",true);
        preferenceToggle(gamepad,"手柄运动传感器","checkbox_gamepad_motion_sensors",true);
        preferenceToggle(gamepad,"屏幕手柄振动","checkbox_vibrate_osc",true);
        preferenceToggle(gamepad,"使用平板振动反馈","checkbox_vibrate_fallback",false);
        preferenceNumber(gamepad,"平板振动强度","seekbar_vibrate_fallback_strength",100,0,100,"%",1,null);
        LinearLayout audio = card("声音 · 下次连接生效");
        preferenceChoice(audio,"声道","list_audio_config",new String[]{"立体声","5.1声道","7.1声道"},new String[]{"2","51","71"},"2");
        preferenceToggle(audio,"系统音效处理","checkbox_enable_audiofx",false);
        LinearLayout window=card("窗口 · 下次连接生效");
        Switch pip=preferenceToggle(window,"离开串流时使用小窗","checkbox_enable_pip",false);
        boolean pipAvailable=Build.VERSION.SDK_INT>=26 && getPackageManager().hasSystemFeature(android.content.pm.PackageManager.FEATURE_PICTURE_IN_PICTURE);
        pip.setEnabled(pipAvailable);
        if (!pipAvailable) pip.setTooltipText("当前系统不支持画中画。");
    }
    private void preferenceNumber(LinearLayout parent,String title,String key,int fallback,int min,int max,String unit,int multiplier,String note) {
        final View[] row={null};
        java.util.function.IntFunction<String> format=value->value==0 && multiplier==1000?"自动":scaledNumber(value,multiplier)+" "+unit;
        row[0]=settingLink(parent,title,"",v->{
            LinearLayout input=column();input.setPadding(dp(24),dp(8),dp(24),dp(8));
            EditText value=field(input,title+"（"+unit+"）",min+"–"+max,false);
            value.setInputType(InputType.TYPE_CLASS_NUMBER|(min<0?InputType.TYPE_NUMBER_FLAG_SIGNED:0)|(multiplier>1?InputType.TYPE_NUMBER_FLAG_DECIMAL:0));
            value.setText(scaledNumber(HubAdvancedPreferences.active(this).getInt(key,fallback),multiplier));
            if(note!=null)input.addView(label(note,13,MUTED));
            AlertDialog dialog=HubStreamUi.hubDialog(this).setTitle(title).setView(input).setNegativeButton("取消",null).setPositiveButton("保存",null).create();
            dialog.setOnShowListener(d->dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(button->{
                int selected;
                try{selected=scaledInput(value.getText().toString(),min,max,multiplier);}
                catch(IllegalArgumentException error){value.setError("请输入"+min+"–"+max+"之间的"+(multiplier>1?"数值，最多三位小数":"整数"));return;}
                if(!HubAdvancedPreferences.save(this,java.util.Map.of(key,selected)))return;
                settingValue(row[0],format.apply(selected));dialog.dismiss();
            }));dialog.show();
        });
        settingValue(row[0],format.apply(HubAdvancedPreferences.active(this).getInt(key,fallback)));
    }
    private static String scaledNumber(int value,int multiplier) {
        return java.math.BigDecimal.valueOf(value).divide(java.math.BigDecimal.valueOf(multiplier)).stripTrailingZeros().toPlainString();
    }
    private static int scaledInput(String text,int min,int max,int multiplier) {
        try {
            java.math.BigDecimal value=new java.math.BigDecimal(text.trim());
            if(value.compareTo(java.math.BigDecimal.valueOf(min))<0||value.compareTo(java.math.BigDecimal.valueOf(max))>0)throw new IllegalArgumentException();
            return value.multiply(java.math.BigDecimal.valueOf(multiplier)).intValueExact();
        } catch(ArithmeticException error) { throw new IllegalArgumentException(error); }
    }
    private void showCustomFrameRate(View row) {
        android.content.SharedPreferences prefs=HubAdvancedPreferences.active(this);
        LinearLayout input=column();input.setPadding(dp(24),dp(8),dp(24),dp(8));
        EditText value=field(input,"帧率（FPS）","30–120",false);
        value.setSingleLine(true);value.setInputType(InputType.TYPE_CLASS_NUMBER);value.setText(prefs.getString("list_fps","60"));
        AlertDialog dialog=HubStreamUi.hubDialog(this).setTitle("自定义帧率").setView(input).setNegativeButton("取消",null).setPositiveButton("保存",null).create();
        dialog.setOnShowListener(d->dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(v->{
            String selected=value.getText().toString().trim();
            try{int fps=Integer.parseInt(selected);if(fps<30||fps>120)throw new IllegalArgumentException();}
            catch(IllegalArgumentException error){value.setError("请输入30–120之间的整数");return;}
            if(!HubAdvancedPreferences.save(this,java.util.Map.of("list_fps",selected,"custom_refresh_rate",selected)))return;
            store.put("qualityPreset","advanced");settingValue(row,selected+" FPS");dialog.dismiss();
        }));dialog.show();
    }
    private void preferenceChoice(LinearLayout parent, String title, String key, String[] labels, String[] values, String fallback) {
        android.content.SharedPreferences prefs = HubAdvancedPreferences.active(this);
        String saved = prefs.getString(key, fallback); int selected = 0;
        for (int i = 0; i < values.length; i++) if (values[i].equals(saved)) selected = i;
        choiceSetting(parent,title,labels,selected,position->{
            java.util.Map<String,Object> changed = new java.util.LinkedHashMap<>(); changed.put(key, values[position]);
            if ("list_fps".equals(key)) changed.put("custom_refresh_rate", values[position]);
            if (!HubAdvancedPreferences.save(this, changed)) { build(); return; }
            store.put("qualityPreset", "advanced");
        });
    }
    private Switch preferenceToggle(LinearLayout parent, String title, String key, boolean fallback) {
        android.content.SharedPreferences prefs = HubAdvancedPreferences.active(this);
        Switch control = new HubSwitch(this); control.setText(title); control.setTextColor(TEXT); control.setTextSize(15);
        control.setMinimumHeight(dp(56)); control.setChecked(prefs.getBoolean(key, fallback));
        parent.addView(control, new LinearLayout.LayoutParams(-1,-2));
        final boolean[] restoring = {false};
        control.setOnCheckedChangeListener((button, enabled)->{
            if (restoring[0]) return;
            if (!HubAdvancedPreferences.save(this,java.util.Map.of(key,enabled))) {
                restoring[0] = true; button.setChecked(!enabled); restoring[0] = false;
            }
        });
        return control;
    }
    private void refreshShortcutsState() {
        if (shortcutsState != null) shortcutsState.setText(!HubSystemShortcuts.requested(this) ? "已关闭" : HubSystemShortcuts.authorized(this) ? "已开启" : "待系统授权");
    }
    private void showShortcuts() {
        boolean requested = HubSystemShortcuts.requested(this), authorized = HubSystemShortcuts.authorized(this);
        AlertDialog.Builder dialog = HubStreamUi.hubDialog(this).setTitle("系统快捷键")
            .setMessage(!requested ? "已关闭" : authorized ? "已开启并取得系统授权" : "已开启，等待系统授权")
            .setNegativeButton("取消", null);
        if (requested) dialog.setNeutralButton("停用快捷键", (d,w)->{HubSystemShortcuts.request(this,false);refreshShortcutsState();});
        if (!requested || !authorized) dialog.setPositiveButton(authorized ? "开启" : "前往系统授权",(d,w)->{
            HubSystemShortcuts.request(this,true);refreshShortcutsState();
            if (!authorized) startActivity(new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS));
        });
        dialog.show();
    }
    private void showManual() {
        ScrollView scroll = new ScrollView(this);
        LinearLayout sections = column(); sections.setPadding(dp(24), dp(8), dp(24), dp(16)); scroll.addView(sections);
        manualSection(sections, "添加电脑", "电脑点“+ 绑定设备”，平板填写显示的 IPv4 和六位短码；核对两端校验码后，在电脑点“允许绑定”。");
        manualSection(sections, "连接", "同一网络直接连接。外出前，在“账号与连接”中选择两端相同的异地通道并登录。");
        manualSection(sections, "副屏", "平板点“用作副屏”；电脑发起时，先在平板开启接收请求，再在电脑“全部设备”的对应卡片点“连接副屏”。");
        manualSection(sections, "文件", "电脑在设备权限中允许访问并选择目录。平板文件页可浏览、下载或发送；删除权限单独开启。");
        manualSection(sections, "键盘", "Ctrl+Alt+Tab 切换电脑窗口。可选“系统快捷键”需手动授权：Alt+Tab 按住选择、松开确认，Win+Tab 打开任务视图。");
        manualSection(sections, "停止连接", "电脑顶栏可断开连接或暂停接受连接。暂停会断开当前连接，重启后仍保持暂停。Ctrl+Alt+F12 恢复隐私屏。");
        HubStreamUi.hubDialog(this).setTitle("使用手册")
            .setView(scroll)
            .setPositiveButton("取消", null).show();
        scroll.getLayoutParams().height = dp(Math.min(520, Math.max(160, (int)(HubWindowLayout.of(this).heightDp * .6f))));
        scroll.requestLayout();
    }
    private void manualSection(LinearLayout parent, String title, String body) {
        TextView heading = label(title, 18, TEXT); heading.setTypeface(null, Typeface.BOLD);
        if (Build.VERSION.SDK_INT >= 28) heading.setAccessibilityHeading(true);
        LinearLayout.LayoutParams size = new LinearLayout.LayoutParams(-1, -2); size.topMargin = dp(16); size.bottomMargin = dp(6);
        parent.addView(heading, size); parent.addView(label(body, 14, TEXT));
    }
    private int selectedOrientation() { int i = selectedOrientationIndex; return i == 0 ? deviceOrientation() : (i - 1) * 90; }
    private int deviceOrientation() {
        android.view.Display display = getWindowManager().getDefaultDisplay(); Point point = new Point(); display.getRealSize(point);
        int rotation = display.getRotation(); boolean naturalPortrait = (rotation == Surface.ROTATION_0 || rotation == Surface.ROTATION_180) ? point.y > point.x : point.x > point.y;
        return (naturalPortrait ? new int[]{90, 0, 270, 180} : new int[]{0, 90, 180, 270})[rotation];
    }
    private interface Work { String execute() throws Exception; }
    private void checkComputer() {
        if (!store.bound()) { showProgress("请先添加电脑。"); return; }
        run("正在检查电脑状态…", () -> {
            HubSessionCoordinator checker = new HubSessionCoordinator(this, this::showProgress); activeCoordinator = checker;
            try { return checker.checkStatus(); } finally { activeCoordinator = null; }
        });
    }
    private void loginAccount() {
        if (busy) return;
        try {
            if (!store.transport().transport().usesAccount()) { showProgress("当前通道无需登录账号。"); return; }
            if (applyProvider.isEnabled()) { showProgress("请先保存并应用通道。"); return; }
        } catch (IllegalArgumentException error) { showProgress(error.getMessage()); return; }
        setBusy(true); loginAccountButton.setText("正在获取授权…"); showProgress("正在获取账号授权地址…");
        network.login((state, error) -> {
            if (isDestroyed()) return;
            setBusy(false); loginAccountButton.setText("登录连接账号"); renderNetwork(state, error);
            if (error != null) { showProgress(error); return; }
            if (state == null) { showProgress("未取得账号状态，请重试。"); return; }
            if (!authorizationUrl.isEmpty()) { showProgress("请在浏览器完成账号授权。"); openAuthorization(); }
            else showProgress(state.loggedIn() ? "账号已登录。" : "未取得授权链接，请重试。");
        });
    }
    private void setBusy(boolean value) {
        HubUpdates.setPreparing(value);
        if (provider != null) { provider.setEnabled(!value); for (int i=0;i<provider.getChildCount();i++) provider.getChildAt(i).setEnabled(!value); providerServer.setEnabled(!value); providerManual.setEnabled(!value); } busy = value; if (value && presenceCoordinator != null) presenceCoordinator.cancel(); for (Button button : actions) if (button != cancelConnection) { button.setEnabled(!value); button.setAlpha(1f); } for (View view : modeActions) {
            view.setEnabled(!value);view.setAlpha(1f);
            if(!(view.getTag() instanceof Boolean))continue;
            boolean primary=Boolean.TRUE.equals(view.getTag());
            view.setBackground(moduleBackground(primary,value));
            ((TextView)((LinearLayout)view).getChildAt(0)).setTextColor(value?MUTED:primary?HubTheme.onAccent(this):ACCENT_DARK);
        } renderDeviceState(); updateProviderApply(); }
    private void run(Work work) { run("正在处理…", work, null); }
    private void run(Work work, Runnable onSuccess) { run("正在处理…", work, onSuccess); }
    private void run(String progress, Work work) { run(progress, work, null); }
    private void run(String progress, Work work, Runnable onSuccess) {
        if (busy) return;
        final long operation = ++operationGeneration;
        setBusy(true); showProgress(progress);
        worker.execute(() -> {
            String message; boolean success = false;
            try { message = work.execute(); success = true; }
            catch (java.util.concurrent.CancellationException e) { message = e.getMessage(); }
            catch (Exception e) { message = "未完成：" + (e.getMessage() == null ? e.getClass().getSimpleName() : e.getMessage()); }
            final String result = message; final boolean succeeded = success;
            main.post(() -> {
                if (isDestroyed() || operation != operationGeneration) return;
                computerOnline = succeeded && store.bound();
                presenceValidUntil = computerOnline ? android.os.SystemClock.elapsedRealtime() + 15000 : 0;
                presenceNetwork = HubNetworkStamp.current(); lastOperationFailed = !succeeded;
                nextPresenceAt = android.os.SystemClock.elapsedRealtime() + 12000;
                setBusy(false);
                computerName.setText(store.get("deviceName", "我的电脑")); updateComputerAddress();
                refreshBindingSummary(); refreshDiagnosticsCard();
                if (!hosts.hasFocus()) hosts.setText(bindingAddresses());
                if (succeeded && onSuccess != null) onSuccess.run();
                showProgress(result);
            });
        });
    }
    private void refreshComputerPresence() {
        if (Game.isHubConnectionStopping()) {
            // Do not cache the host's pre-disconnect snapshot for another 12 seconds.
            nextPresenceAt = 0;
            renderDeviceState();
            return;
        }
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
    private void showProgress(String message) { main.post(() -> {
        if (isDestroyed()) return;
        TextView target = currentPage == 1 ? bindingFeedback : currentPage == 4 ? accountFeedback : status;
        if (target == null) return;
        target.setText(message == null ? "操作未完成，请重试。" : message); target.setVisibility(View.VISIBLE);
        if (currentPage == 1 || currentPage == 4) target.post(() -> target.requestRectangleOnScreen(new Rect(0, 0, target.getWidth(), target.getHeight()), true));
    }); }
    private void refreshDiagnosticsCard() {
        if (lastConnectionSummary == null) return;
        String summary="尚无连接记录";
        try {
            JSONObject record=new JSONObject(HubSessionDiagnostics.read(this));long started=record.optLong("startedAt",0);
            if(started>0) {
                JSONObject phases=record.optJSONObject("phases");boolean shown=phases!=null&&phases.has("first-frame");
                summary=(shown?"最近连接 ":"最近尝试 ")+android.text.format.DateFormat.format("MM-dd HH:mm",started);
            }
        } catch(Exception ignored) { }
        lastConnectionSummary.setText(summary);
    }
    private void showConnectionDiagnostics() {
        LinearLayout panel = column();
        TextView result = label("",14,TEXT); result.setPadding(dp(24),dp(8),dp(24),dp(8)); result.setVisibility(View.GONE);
        result.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
        View divider = new View(this); divider.setBackgroundColor(BORDER); panel.addView(divider,new LinearLayout.LayoutParams(-1,dp(1)));
        panel.addView(HubStreamUi.diagnosticsView(this,null)); panel.addView(result);
        AlertDialog dialog = HubStreamUi.hubDialog(this).setTitle("最近一次连接").setView(panel)
            .setPositiveButton("取消",null).setNeutralButton("检查连接",null)
            .setNegativeButton("复制诊断",(d,w)->((ClipboardManager)getSystemService(CLIPBOARD_SERVICE)).setPrimaryClip(ClipData.newPlainText("我控制连接诊断",HubSessionDiagnostics.read(this)))).create();
        dialog.setOnShowListener(d -> dialog.getButton(AlertDialog.BUTTON_NEUTRAL).setOnClickListener(v -> {
            Button check = dialog.getButton(AlertDialog.BUTTON_NEUTRAL); check.setEnabled(false);result.setText("正在检查…");result.setVisibility(View.VISIBLE);
            worker.execute(()->{
                String message;try { message=new HubSessionCoordinator(this,ignored->{}).checkStatus(); }
                catch(Exception error) { message="检查未完成："+error.getMessage(); }
                final String feedback=message;main.post(()->{if(dialog.isShowing()){result.setText(feedback);check.setEnabled(true);}});
            });
        }));dialog.show();
    }
    private String bindingAddresses() {
        try { HubTransports.Settings settings = store.transport(); return "manual".equals(settings.provider) ? settings.manualAddresses : store.get("hosts", ""); }
        catch (IllegalArgumentException error) { return ""; }
    }
    private void showConnectionAddresses() {
            List<String> annotated = new ArrayList<>();
            List<HubAddressLabels.Link> links = HubAddressLabels.currentLinks();
            String current = computerOnline ? presenceHost : "";
            for (String address : HubRoutes.usableAddresses(bindingAddresses())) annotated.add(HubAddressLabels.line(address, current, links));
            String addresses = android.text.TextUtils.join("\n\n", annotated);
            String detail = addresses.isEmpty() ? "尚未发现可用的 IPv4 地址" : addresses;
            if (store.bound()) detail += "\n\n已绑定，可检查电脑状态或开始连接。";
            HubStreamUi.hubDialog(this).setTitle("连接地址").setMessage(detail)
                .setPositiveButton("取消", null).show();
    }
    private void updateComputerAddress() {
        if(homeChannel!=null) {
            String channel="通道待设置";
            try { channel=computerOnline&&!presenceHost.isEmpty()?actualRoute(presenceHost)+" · "+presenceHost:"实际连接待检测"; }
            catch(IllegalArgumentException ignored) { }
            homeChannel.setText(store.bound()?channel:"添加电脑后显示连接信息");
        }
    }
    private void renderDeviceState() {
        if (deviceState == null || deviceStateDot == null) return;
        int color = busy ? WARNING : lastOperationFailed ? DANGER : computerOnline ? SUCCESS : MUTED;
        if(detailsState!=null)detailsState.setText((computerOnline?"在线":store.bound()?"正在检查连接":"尚未绑定电脑")+(computerOnline&&!presenceHost.isEmpty()?" · "+actualRoute(presenceHost)+" · "+presenceHost:""));
        deviceState.setText(busy ? "正在连接" : Game.isHubConnectionStopping() ? "正在结束连接" : lastOperationFailed ? "连接未完成" : computerOnline ? "在线" + mediaState : store.bound() ? "暂不可达" : "未绑定");
        deviceState.setTextColor(busy || lastOperationFailed ? color : MUTED); GradientDrawable dot = new GradientDrawable(); dot.setShape(GradientDrawable.OVAL); dot.setColor(color); deviceStateDot.setBackground(dot);
    }
    private void finishBindingInput() {
        hosts.clearFocus(); invitation.clearFocus(); pageRoot.requestFocus();
        ((android.view.inputmethod.InputMethodManager)getSystemService(INPUT_METHOD_SERVICE)).hideSoftInputFromWindow(pageRoot.getWindowToken(), 0);
    }
    private void buildProviderCard() {
        LinearLayout card = card("连接通道");
        providerSaved = label("正在读取通道设置…", 14, MUTED); card.addView(providerSaved); gap(card, 12);

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
        providerMessage = label("选择后自动应用；地址输入需保存。更换服务区前请先退出连接账号。", 14, MUTED);
        card.addView(providerMessage);
        try {
            HubTransports.Settings settings = store.transport();
            hosts.setEnabled(true);
            for (int i = 0; i < HubTransports.IDS.length; i++) if (HubTransports.IDS[i].equals(settings.provider)) provider.check(provider.getChildAt(i).getId());
            providerServer.setText(settings.serverUrl); providerManual.setText(settings.manualAddresses);
            providerSaved.setText("已应用：" + settings.transport().label());
        } catch (IllegalArgumentException error) { providerSaved.setText(error.getMessage()); }
        provider.setOnCheckedChangeListener((group, checkedId) -> {
            if (renderingProvider || providerChoicePending) return;
            // Coalesce RadioGroup broadcasts before restoring or applying the selection.
            providerChoicePending = true;
            main.post(() -> { providerChoicePending=false; if(!isDestroyed())providerChoiceChanged(); });
        });
        showProviderFields();
        applyProvider = button(card, "保存地址", false, v -> saveProvider());
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
    private void restoreProviderSelection() {
        try {
            HubTransports.Settings saved = store.transport();
            renderingProvider = true;
            for (int i=0;i<HubTransports.IDS.length;i++) if (saved.provider.equals(HubTransports.IDS[i])) provider.check(provider.getChildAt(i).getId());
        } finally { renderingProvider = false; }
        showProviderFields();
    }
    private void providerChoiceChanged() {
        if (renderingProvider) return;
        String requested = HubTransports.IDS[selectedProviderIndex()];
        restoreProviderSelection();
        if (busy || requested.equals(store.transport().provider)) return;
        if ("self-hosted".equals(requested) || "manual".equals(requested)) {
            LinearLayout input = column(); input.setPadding(dp(24),dp(8),dp(24),dp(8));
            boolean server = "self-hosted".equals(requested);
            EditText address = field(input, server ? "服务区 HTTPS 地址" : "电脑 IPv4 地址", server ? "https://vpn.example.com" : "用逗号分隔多个地址", false);
            address.setInputType(InputType.TYPE_CLASS_TEXT | (server ? InputType.TYPE_TEXT_VARIATION_URI : 0));
            address.setText(server ? providerServer.getText() : providerManual.getText());
            AlertDialog dialog = HubStreamUi.hubDialog(this).setTitle(server ? "配置自建通道" : "配置手动通道").setView(input)
                    .setNegativeButton("取消",null).setPositiveButton("保存",null).create();
            dialog.setOnShowListener(d -> dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(v -> {
                try {
                    HubTransports.Settings next = new HubTransports.Settings(requested, server ? address.getText().toString().trim() : "", server ? "" : address.getText().toString().trim());
                    dialog.dismiss(); confirmProvider(next);
                } catch (IllegalArgumentException error) { address.setError(error.getMessage()); }
            })); dialog.show();
        } else confirmProvider(new HubTransports.Settings(requested,"",""));
    }
    private void confirmProvider(HubTransports.Settings settings) {
        if (lastNetworkState != null && lastNetworkState.vpnActive) {
            HubStreamUi.hubDialog(this).setTitle("切换连接通道？").setMessage("当前异地连接将断开，电脑绑定和账号保留。")
                    .setNegativeButton("取消",null).setPositiveButton("切换",(d,w)->applyProviderSettings(settings)).show();
        } else applyProviderSettings(settings);
    }
    private void showProviderFields() {
        String id = HubTransports.IDS[selectedProviderIndex()];
        providerServerPanel.setVisibility("self-hosted".equals(id) ? View.VISIBLE : View.GONE);
        providerManualPanel.setVisibility("manual".equals(id) ? View.VISIBLE : View.GONE);
        updateProviderApply();
    }
    private void refreshBindingSummary() {
        if (bindingSummary == null) return;
        bindingSummary.setText(store.bound() ? "已绑定 · " + store.get("deviceName", "我的电脑") : "尚未绑定");
        bindingForm.setVisibility(fullBindingOpen ? View.VISIBLE : View.GONE);
        if (checkComputerButton != null) checkComputerButton.setVisibility(store.bound() ? View.VISIBLE : View.GONE);
    }
    private void updateProviderApply() {
        if (applyProvider == null) return;
        String id = HubTransports.IDS[selectedProviderIndex()];
        applyProvider.setVisibility("manual".equals(id) || "self-hosted".equals(id) ? View.VISIBLE : View.GONE);
        applyProvider.setText("self-hosted".equals(id) ? "保存服务区地址" : "保存电脑地址");
        boolean changed = true;
        try {
            HubTransports.Settings current = store.transport();
            HubTransports.Settings selected = new HubTransports.Settings(HubTransports.IDS[selectedProviderIndex()],
                    providerServer.getText().toString().trim(), providerManual.getText().toString().trim());
            changed = !current.provider.equals(selected.provider) || !current.serverUrl.equals(selected.serverUrl) || !current.manualAddresses.equals(selected.manualAddresses);
        } catch (IllegalArgumentException ignored) { }
        applyProvider.setEnabled(!busy && changed); applyProvider.setAlpha(1f);
    }
    private String tabletQualityLabel() { return "clear".equals(store.get("tabletQuality", "stable")) ? "清晰优先 · 下次连接生效" : "稳定优先 · 下次连接生效"; }
    private void showQualityPresets() {
        HubQualitySettings.show(this, false, () -> showPage(5));
    }
    private void saveProvider() {
        final HubTransports.Settings settings;
        try { settings = new HubTransports.Settings(HubTransports.IDS[selectedProviderIndex()],
                providerServer.getText().toString().trim(), providerManual.getText().toString().trim()); }
        catch (IllegalArgumentException error) { providerMessage.setText(error.getMessage()); return; }
        confirmProvider(settings);
    }
    private void applyProviderSettings(HubTransports.Settings settings) {
        if (busy || HubFileService.busy() || HubSessionLease.active()) { providerMessage.setText("请先结束串流和文件传输，再切换通道。"); restoreProviderSelection(); return; }
        if (presenceCoordinator != null) presenceCoordinator.cancel();
        authorizationUrl = ""; setBusy(true); providerMessage.setText("正在应用通道设置…");
        network.configure(settings, (state, error) -> {
            if (isDestroyed()) return;
            setBusy(false); authorizationUrl = "";
            restoreProviderSelection();
            if (error != null) { providerMessage.setText(error); renderNetwork(null, error); return; }
            providerServer.setText(settings.serverUrl); providerManual.setText(settings.manualAddresses);
            providerSaved.setText("已应用：" + settings.transport().label());
            providerMessage.setText("已保存。异地连接已暂停，账号与电脑绑定保留。");
            updateProviderApply();
            hosts.setText(bindingAddresses()); hosts.setEnabled(true);
            presenceValidUntil = nextPresenceAt = 0; computerOnline = false; renderDeviceState();
            renderNetwork(state, null); refreshComputerPresence();
        });
    }
    private void buildAddressBinding() {
        LinearLayout binding = card("绑定电脑");
        binding.addView(label("填写电脑“绑定设备”中显示的地址和短码。", 14, MUTED));
        bindingAddressInput = field(binding, "电脑 IPv4", "例如 192.168.1.10", false);
        bindingAddressInput.setInputType(InputType.TYPE_CLASS_PHONE);
        bindingAddressInput.setSingleLine(true);
        bindingAddressInput.setContentDescription("电脑 IPv4");
        bindingCodeInput = shortCodeField(binding);
        button(binding, "绑定", true, v -> submitAddressBinding());
    }
    private void submitAddressBinding() {
        if (busy) return;
        String address = bindingAddressInput.getText().toString().trim();
        String code = bindingCodeInput.getText().toString().trim();
        if (!HubLanDiscovery.localIpv4(address)) {
            bindingAddressInput.setError("请输入有效的局域网 IPv4 地址");
            bindingAddressInput.requestFocus();
            return;
        }
        if (!code.matches("[0-9]{6}")) {
            bindingCodeInput.setError("请输入六位数字");
            bindingCodeInput.requestFocus();
            return;
        }
        finishBindingInput();
        beginLocalBinding(address, code);
    }
    private EditText shortCodeField(LinearLayout input) {
        EditText code = field(input, "六位短码", "例如 012345", false);
        code.setInputType(InputType.TYPE_CLASS_NUMBER); code.setFilters(new android.text.InputFilter[]{new android.text.InputFilter.LengthFilter(6)});
        code.setHint("输入六位数字");
        code.setSingleLine(true); code.setGravity(Gravity.START | Gravity.CENTER_VERTICAL); code.setTextSize(22); code.setTypeface(Typeface.MONOSPACE);
        code.setLetterSpacing(0f); code.setMinimumHeight(dp(64));
        return code;
    }
    private void sizeBindingDialog(AlertDialog dialog) {
        android.view.Window window = dialog.getWindow();
        if (window == null) return;
        window.setLayout(Math.max(1, Math.min(dp(560), windowBounds().width() - dp(40))), ViewGroup.LayoutParams.WRAP_CONTENT);
        window.setSoftInputMode(android.view.WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
    }
    private void beginLocalBinding(String address, String code) {
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
            final HubLanDiscovery.Device device = HubLocalBinding.lookup(address);
            client = new HubLocalBinding(device); localBinding = client;
                if (cancelLocalBinding.get()) throw new java.util.concurrent.CancellationException("绑定已取消。");
                String name = Build.MODEL == null ? "Android 平板" : Build.MODEL;
                JSONObject request = client.begin(code, store.clientId(), name.length() > 64 ? name.substring(0, 64) : name);
                String requestId = request.getString("requestId"), comparison = request.getString("comparison");
                main.post(() -> { if (waiting.isShowing()) {
                    comparisonView.setText(comparison);
                    comparisonView.setContentDescription("设备核对码：" + comparison);
                    confirmationHint.setText("核对两端校验码后，在电脑点“允许绑定”。");
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
                        return "已绑定 " + device.name + "。";
                    }
                    if (!"pending".equals(state)) throw new java.io.IOException("电脑已拒绝或确认已过期，请重新生成短码。");
                }
                throw new java.util.concurrent.CancellationException("绑定已取消或确认超时，原绑定已保留。");
            } finally {
                if(client!=null)client.cancel(); if (localBinding == client) localBinding = null;
                main.post(() -> { if (waiting.isShowing()) waiting.dismiss(); });
            }
        }, this::returnHomeAfterBinding);
    }
    private void returnHomeAfterBinding() {
        invitation.setText(""); bindingCodeInput.setText(""); fullBindingOpen = false;
        refreshBindingSummary(); showPage(0);
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
            return "绑定已保存。";
        }, this::returnHomeAfterBinding);
    }
    private void startSession(String mode, String link, boolean reconnect, int degrees) {
        startSession(mode, link, reconnect, degrees, "", "");
    }
    private void startSession(String mode, String link, boolean reconnect, int degrees, String pcRequestId) {
        startSession(mode, link, reconnect, degrees, pcRequestId, "");
    }
    private void startSession(String mode, String link, boolean reconnect, int degrees, String pcRequestId, String reuseLeaseId) {
        startSession(mode, link, reconnect, degrees, pcRequestId, reuseLeaseId, 0, 0, 0, selectedOrientationIndex == 0);
    }
    private void startSession(String mode, String link, boolean reconnect, int degrees, String pcRequestId, String reuseLeaseId,
                              int targetWidth, int targetHeight, int targetFps, boolean requestedAutomatic) {
        pcRequestId = pcRequestId == null ? "" : pcRequestId;
        reuseLeaseId = reuseLeaseId == null ? "" : reuseLeaseId;
        final String requestId = pcRequestId, leaseId = reuseLeaseId;
        if (isDestroyed() || isFinishing()) return;
        if (busy) { showProgress("正在处理当前连接，请稍候。"); return; }
        if (!store.bound()) { showPage(1); showProgress("点“+ 添加电脑”绑定。"); return; }
        showPage(0);
        finishBindingInput();
        final String route = link == null ? "auto" : link;
        final boolean automatic = requestedAutomatic;
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
        boolean automatic = request.optBoolean("autoOrientation", false);
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
                            int initialOrientation = automatic ? HubDisplayCapabilities.currentOrientation(this) : degrees;
                            startSession("tablet", link, false, initialOrientation, id, "", 0, 0, 0, automatic);
                        });
                    });
                }).setNegativeButton("拒绝", (dialog, which) -> HubReceiverService.respond(this, id, "reject", error -> { if (error != null) showProgress(error.getMessage()); }))
                .setOnCancelListener(dialog -> { receiverDialogId = ""; }).create();
        receiverDialog.show(); receiverDialog.getButton(AlertDialog.BUTTON_POSITIVE).setTextColor(ACCENT_DARK); receiverDialog.getButton(AlertDialog.BUTTON_NEGATIVE).setTextColor(TEXT);
    }
    private void refreshNetwork() { if (networkChecking || busy) return; networkChecking = true; network.status((state, error) -> { networkChecking = false; renderNetwork(state, error); }); }

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
        if (error != null) {
            authorizationUrl = ""; lastNetworkState = null; peerList.removeAllViews(); networkStatus.setText(error);
            if (networkDetails != null) networkDetails.setVisibility(View.GONE);
            boolean usesAccount = store.transport().transport().usesAccount();
            remoteAccount.setVisibility(usesAccount ? View.VISIBLE : View.GONE);
            updateAccountActions(usesAccount, false, false); return;
        }
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
        java.util.Set<String> hiddenPeers=getSharedPreferences("streamhub_controls",MODE_PRIVATE).getStringSet("hidden_account_peers",java.util.Collections.emptySet());
        int removedPeers=0;
        for (EmbeddedNetwork.Peer peer : state.peers) if ("windows".equalsIgnoreCase(peer.operatingSystem) && !peer.addresses.isEmpty()) {
            if(!peer.online&&hiddenPeers.contains(accountPeerKey(peer)))removedPeers++;else visiblePeers.add(peer);
        }
        visiblePeers.sort((left, right) -> Boolean.compare(right.online, left.online));
        LinearLayout offlineList = column();
        long offlineCount = visiblePeers.stream().filter(peer -> !peer.online).count();
        for (EmbeddedNetwork.Peer peer : visiblePeers) {
            Button entry = new Button(this); entry.setStateListAnimator(null); entry.setElevation(0);
            String route = peer.routeText == null || peer.routeText.isEmpty() ? "路径未知" : peer.routeText;
            HubButtons.apply(entry,false);
            entry.setText(peer.name + "\n" + (peer.online ? "在线 · " + route : "离线") + " · " + android.text.TextUtils.join("、", peer.addresses));
            entry.setTextColor(peer.online?TEXT:MUTED); entry.setTextSize(14); entry.setGravity(Gravity.START | Gravity.CENTER_VERTICAL);
            entry.setPadding(dp(14), dp(8), dp(14), dp(8)); entry.setAllCaps(false); entry.setMinHeight(dp(68));
            GradientDrawable peerBackground = new GradientDrawable(); peerBackground.setColor(FIELD); peerBackground.setCornerRadius(dp(8)); peerBackground.setStroke(dp(1), BORDER);
            entry.setBackground(new RippleDrawable(android.content.res.ColorStateList.valueOf(RIPPLE), peerBackground, null));
            LinearLayout peerRow=new LinearLayout(this);peerRow.setGravity(Gravity.CENTER_VERTICAL);
            peerRow.addView(entry,new LinearLayout.LayoutParams(0,-2,1));
            LinearLayout.LayoutParams peerSize = new LinearLayout.LayoutParams(-1, -2); peerSize.topMargin = dp(6); (peer.online ? peerList : offlineList).addView(peerRow, peerSize);
            entry.setEnabled(peer.online && !busy);
            entry.setContentDescription(peer.name + (peer.online ? "，在线，打开绑定表单" : "，离线，无法选择") + "，" + android.text.TextUtils.join("、", peer.addresses));
            if(!peer.online){
                Button remove=textButton(peerRow,"移除",ACCENT_DARK,v->HubStreamUi.hubDialog(this).setTitle("从本机列表移除？")
                    .setMessage(peer.name+"\n"+android.text.TextUtils.join("、",peer.addresses)+"\n仅从此平板隐藏离线记录，不删除账号内设备，也不会更换当前绑定。")
                    .setNegativeButton("取消",null).setPositiveButton("移除",(d,w)->{
                        android.content.SharedPreferences prefs=getSharedPreferences("streamhub_controls",MODE_PRIVATE);
                        java.util.Set<String> next=new java.util.HashSet<>(prefs.getStringSet("hidden_account_peers",java.util.Collections.emptySet()));next.add(accountPeerKey(peer));
                        if(prefs.edit().putStringSet("hidden_account_peers",next).commit())renderNetwork(lastNetworkState,null);
                        else android.widget.Toast.makeText(this,"记录未移除，请重试",android.widget.Toast.LENGTH_LONG).show();
                    }).show());
                remove.setContentDescription("从本机列表移除 "+peer.name+"，"+android.text.TextUtils.join("、",peer.addresses));
                LinearLayout.LayoutParams removeSize=new LinearLayout.LayoutParams(dp(64),-2);removeSize.leftMargin=dp(8);remove.setLayoutParams(removeSize);
                remove.setGravity(Gravity.CENTER);remove.setMinWidth(0);remove.setEnabled(!busy);
            }
            entry.setOnClickListener(v -> {
                if (!peer.online || busy) return;
                fullBindingOpen = true; refreshBindingSummary();
                hosts.setText(android.text.TextUtils.join(",", peer.addresses));
                showPage(1);
                showProgress("待绑定：" + peer.name + "。填写绑定码后验证，成功前不会更换当前电脑。");
                android.widget.Toast.makeText(this, "已带入绑定表单，尚未保存", android.widget.Toast.LENGTH_SHORT).show();
            });
        }
        if (offlineCount > 0) {
            textButton(peerList, (showOfflinePeers ? "收起" : "查看") + "离线电脑（" + offlineCount + "） "+(showOfflinePeers?"▴":"▾"), ACCENT_DARK, v -> { showOfflinePeers = !showOfflinePeers; renderNetwork(lastNetworkState, null); });
            offlineList.setVisibility(showOfflinePeers ? View.VISIBLE : View.GONE); peerList.addView(offlineList);
        }
        if(removedPeers>0)textButton(peerList,"恢复本机已移除的离线记录（"+removedPeers+"）",ACCENT_DARK,v->{
            if(getSharedPreferences("streamhub_controls",MODE_PRIVATE).edit().remove("hidden_account_peers").commit())renderNetwork(lastNetworkState,null);
            else android.widget.Toast.makeText(this,"记录未恢复，请重试",android.widget.Toast.LENGTH_LONG).show();
        });
        if (peerList.getChildCount() == 0) peerList.addView(label(settings.transport().usesAccount() ? "登录后显示同账号的 Windows 电脑。" : "当前通道使用已绑定的电脑地址，无需云账号。", 13, MUTED));
    }
    private String accountPeerKey(EmbeddedNetwork.Peer peer){
        java.util.List<String> addresses=new java.util.ArrayList<>(peer.addresses);java.util.Collections.sort(addresses);
        return selectedProviderIndex()+"|"+providerServer.getText().toString().trim()+"|"+android.text.TextUtils.join(",",addresses);
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


