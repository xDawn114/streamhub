package com.limelight;


import static com.limelight.StartExternalDisplayControlReceiver.requestFocusToExternalDisplayControl;
import static com.limelight.binding.input.KeyboardTranslator.getModifier;
import static com.limelight.utils.ExternalDisplayControlActivity.SECONDARY_SCREEN_NOTIFICATION_ID;
import static com.limelight.utils.ExternalDisplayControlActivity.closeExternalDisplayControl;
import static com.limelight.utils.ServerHelper.getActiveDisplay;
import static com.limelight.utils.ServerHelper.getSecondaryDisplay;

import com.limelight.binding.PlatformBinding;
import com.limelight.binding.audio.AndroidAudioRenderer;
import com.limelight.binding.input.ControllerHandler;
import com.limelight.binding.input.GameInputDevice;
import com.limelight.binding.input.KeyboardTranslator;
import com.limelight.binding.input.capture.InputCaptureManager;
import com.limelight.binding.input.capture.InputCaptureProvider;
import com.limelight.binding.input.touch.AbsoluteTouchContext;
import com.limelight.binding.input.touch.RelativeTouchContext;
import com.limelight.binding.input.driver.UsbDriverService;
import com.limelight.binding.input.evdev.EvdevListener;
import com.limelight.binding.input.touch.TouchContext;
import com.limelight.binding.input.touch.TrackpadContext;
import com.limelight.binding.input.virtual_controller.VirtualController;
import com.limelight.binding.input.virtual_controller.keyboard.KeyBoardController;
import com.limelight.binding.input.virtual_controller.keyboard.KeyBoardLayoutController;
import com.limelight.binding.video.CrashListener;
import com.limelight.binding.video.MediaCodecDecoderRenderer;
import com.limelight.binding.video.MediaCodecHelper;
import com.limelight.binding.video.PerfOverlayListener;
import com.limelight.nvstream.NvConnection;
import com.limelight.nvstream.NvConnectionListener;
import com.limelight.nvstream.StreamConfiguration;
import com.limelight.nvstream.http.ComputerDetails;
import com.limelight.nvstream.http.NvApp;
import com.limelight.nvstream.http.NvHTTP;
import com.limelight.nvstream.input.KeyboardPacket;
import com.limelight.nvstream.input.MouseButtonPacket;
import com.limelight.nvstream.jni.MoonBridge;
import com.limelight.preferences.GlPreferences;
import com.limelight.preferences.PreferenceConfiguration;
import com.limelight.preferences.StreamSettings;
import com.limelight.profiles.ProfilesManager;
import com.limelight.streamhub.RtInputToolbar;
import com.limelight.streamhub.HubStreamUi;
import com.limelight.streamhub.RtStreamSession;
import com.limelight.streamhub.HubRotationMonitor;
import com.limelight.streamhub.HubTransportPolicy;
import com.limelight.streamhub.HubStore;
import com.limelight.streamhub.HubRoutes;
import com.limelight.streamhub.HubControlClient;
import com.limelight.streamhub.HubClipboard;
import com.limelight.streamhub.HubClipboardPreview;
import com.limelight.streamhub.HubStreamWarnings;
import com.limelight.streamhub.HubNetworkLadder;
import com.limelight.streamhub.HubStreamLoadPolicy;
import com.limelight.streamhub.HubSessionLease;
import com.limelight.streamhub.HubLiveControls;
import com.limelight.streamhub.HubDesktopInput;
import com.limelight.streamhub.HubDisplayCapabilities;
import com.limelight.streamhub.HubDisplayModePolicy;
import com.limelight.ui.ExternalControllerView;
import com.limelight.ui.GameGestures;
import com.limelight.ui.StreamView;
import com.limelight.utils.Dialog;
import com.limelight.utils.ExternalDisplayControlActivity;
import com.limelight.utils.MouseModeOption;
import com.limelight.utils.PanZoomHandler;
import com.limelight.utils.PerformanceDataTracker;
import com.limelight.utils.ServerHelper;
import com.limelight.utils.ShortcutHelper;
import com.limelight.utils.SpinnerDialog;
import com.limelight.utils.UiHelper;

import android.annotation.SuppressLint;
import android.annotation.TargetApi;
import android.app.AlertDialog;
import android.app.PictureInPictureParams;
import android.app.Service;
import android.content.ClipData;
import android.content.ClipDescription;
import android.content.ClipboardManager;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.ServiceConnection;
import android.content.SharedPreferences;
import android.content.pm.ActivityInfo;
import android.content.pm.PackageManager;
import android.content.res.Configuration;
import android.graphics.Outline;
import android.graphics.Point;
import android.graphics.PointF;
import android.graphics.Rect;
import android.hardware.display.DisplayManager;
import android.hardware.input.InputManager;
import android.media.AudioManager;
import android.net.ConnectivityManager;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.IBinder;
import android.os.PersistableBundle;
import android.os.VibrationEffect;
import android.os.Vibrator;
import android.util.Rational;
import android.view.Display;
import android.view.Gravity;
import android.view.InputDevice;
import android.view.KeyCharacterMap;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.Surface;
import android.view.SurfaceHolder;
import android.view.View;
import android.view.View.OnGenericMotionListener;
import android.view.View.OnSystemUiVisibilityChangeListener;
import android.view.View.OnTouchListener;
import android.view.ViewOutlineProvider;
import android.view.ViewParent;
import android.view.Window;
import android.view.WindowManager;
import android.widget.FrameLayout;
import android.view.inputmethod.InputMethodManager;
import android.widget.TextView;
import android.widget.Toast;
import android.widget.ImageButton;
import androidx.annotation.NonNull;
import androidx.appcompat.app.AppCompatActivity;
import androidx.core.app.NotificationManagerCompat;
import androidx.preference.PreferenceManager;

import android.os.Looper;
import java.nio.charset.StandardCharsets;
import java.util.HashSet;
import java.util.List;
import java.util.Objects;
import java.util.Queue;
import java.util.ArrayDeque;

import java.io.ByteArrayInputStream;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.security.cert.CertificateFactory;
import java.security.cert.X509Certificate;
import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Date;
import java.util.HashMap;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;


public class Game extends AppCompatActivity implements SurfaceHolder.Callback,
        OnGenericMotionListener, OnTouchListener, NvConnectionListener, EvdevListener,
        OnSystemUiVisibilityChangeListener, GameGestures, StreamView.InputCallbacks, ExternalControllerView.InputCallbacks,
        PerfOverlayListener, UsbDriverService.UsbDriverStateListener, View.OnKeyListener {
    public static Game instance;

    private int lastButtonState = 0;

    // Only 2 touches are supported
    private final TouchContext[] touchContextMap = new TouchContext[2];
    private final TouchContext[] trackpadContextMap = new TouchContext[2];
    private PanZoomHandler panZoomHandler;
    private long threeFingerDownTime = 0;
    private long fourFingerDownTime = 0;
    private long fiveFingerDownTime = 0;

    private static final int REFERENCE_HORIZ_RES = 1280;
    private static final int REFERENCE_VERT_RES = 720;

    private static final int STYLUS_DOWN_DEAD_ZONE_DELAY = 100;
    private static final int STYLUS_DOWN_DEAD_ZONE_RADIUS = 20;

    private static final int STYLUS_UP_DEAD_ZONE_DELAY = 150;
    private static final int STYLUS_UP_DEAD_ZONE_RADIUS = 50;

    private static final int THREE_FINGER_TAP_THRESHOLD = 300;
    private static final int FOUR_FINGER_TAP_THRESHOLD = 300;
    private static final int FIVE_FINGER_TAP_THRESHOLD = 300;

    private ControllerHandler controllerHandler;
    private KeyboardTranslator keyboardTranslator;
    private VirtualController virtualController;

    private KeyBoardController keyBoardController;

    private KeyBoardLayoutController keyBoardLayoutController;

    private PreferenceConfiguration prefConfig;
    private SharedPreferences tombstonePrefs;

    private int displayWidth;
    private int displayHeight;
    private int currentOrientation;

    public NvConnection conn;
    private SpinnerDialog spinner;
    private boolean displayedFailureDialog = false;
    private boolean connecting = false;
    public boolean connected = false;
    private boolean autoEnterPip = false;
    private boolean surfaceCreated = false;
    private com.limelight.streamhub.HubStartupTrace hubStartupTrace;
    private boolean attemptedConnection = false;
    private int suppressPipRefCount = 0;
    private String pcName;
    private String appName;
    private NvApp app;
    private float desiredRefreshRate;
    private float clientRefreshRate;
    private int hubRequestedDisplayModeId;
    private float hubRequestedDisplayRefreshRate;

    private InputCaptureProvider inputCaptureProvider;
    private int modifierFlags = 0;
    private boolean grabbedInput = true;
    private boolean cursorVisible = false;
    private boolean isPanZoomMode = false;
    private boolean synthClickPending = false;
    private boolean pointerSwiping = false;
    private boolean waitingForAllModifiersUp = false;
    private int specialKeyCode = KeyEvent.KEYCODE_UNKNOWN;
    private StreamView streamView;
    private long synthTouchDownTime = 0;

    private boolean pendingDrag = false;
    private boolean isDragging = false;
    private float lastTouchDownX, lastTouchDownY;

    private long lastAbsTouchUpTime = 0;
    private long lastAbsTouchDownTime = 0;
    private float lastAbsTouchUpX, lastAbsTouchUpY;
    private float lastAbsTouchDownX, lastAbsTouchDownY;

    private boolean quitOnStop = false;
    private boolean connectionStopRequested = false;
    private static volatile CountDownLatch hubConnectionStop = new CountDownLatch(0);
    private boolean isHidingOverlays;
    private boolean floatingButtonShown;
    private boolean overlayToggleZoomButtonShown;
    private TextView notificationOverlayView;
    private int requestedNotificationOverlayVisibility = View.GONE;
    private View performanceOverlayView;

    private TextView performanceOverlayLite;

    private TextView performanceOverlayBig;

    private MediaCodecDecoderRenderer decoderRenderer;
    private boolean reportedCrash;

    private WifiManager.WifiLock highPerfWifiLock;
    private WifiManager.WifiLock lowLatencyWifiLock;

    private boolean connectedToUsbDriverService = false;
    private ServiceConnection usbDriverServiceConnection = new ServiceConnection() {
        @Override
        public void onServiceConnected(ComponentName componentName, IBinder iBinder) {
            UsbDriverService.UsbDriverBinder binder = (UsbDriverService.UsbDriverBinder) iBinder;
            binder.setListener(controllerHandler);
            binder.setStateListener(Game.this);
            binder.start();
            connectedToUsbDriverService = true;
        }

        @Override
        public void onServiceDisconnected(ComponentName componentName) {
            connectedToUsbDriverService = false;
        }
    };

    public static final String EXTRA_HOST = "Host";
    public static final String EXTRA_PORT = "Port";
    public static final String EXTRA_HTTPS_PORT = "HttpsPort";
    public static final String EXTRA_APP_NAME = "AppName";
    public static final String EXTRA_APP_UUID = "AppUUID";
    public static final String EXTRA_APP_ID = "AppId";
    public static final String EXTRA_UNIQUEID = "UniqueId";
    public static final String EXTRA_PC_UUID = "UUID";
    public static final String EXTRA_PC_NAME = "PcName";
    public static final String EXTRA_APP_HDR = "HDR";
    public static final String EXTRA_SERVER_CERT = "ServerCert";
    public static final String EXTRA_VDISPLAY = "VirtualDisplay";
    public static final String EXTRA_SERVER_COMMANDS = "ServerCommands";
    public static final String EXTRA_DISPLAY_ID = "DisplayID";
    public static final String EXTRA_STREAMHUB_MODE = RtStreamSession.EXTRA_MODE;
    private String streamHubMode;
    private RtInputToolbar streamHubInputToolbar;
    private String hubConnectionState = "正在连接电脑…";
    /** Latest renderer statistics, reused by the in-stream diagnostics panel. */
    private Display hubStreamingDisplay;
    private volatile String hubLastPerfText = "";
    private volatile long hubLastPerfAt;
    private HubRotationMonitor hubRotationMonitor;
    private boolean hubRotationReconnect;
    private HubSessionLease hubSessionLease;
    private HubLiveControls hubLiveControls;
    private HubDesktopInput hubDesktopInput;
    private HubStreamWarnings hubWarnings;
    private boolean hubGestureNative, hubGestureActive;

    public static final String CLIPBOARD_IDENTIFIER = "ArtemisStreaming";

    private String appUUID;
    private String host;
    private int port;
    private int httpsPort;
    private int appId;
    private String uniqueId;
    private X509Certificate serverCert;
    private boolean vDisplay;
    private ArrayList<String> serverCommands;

    private ViewParent rootView;
    private ClipboardManager clipboardManager;
    private boolean clipboardSyncRunning = false;

    private NvHTTP httpConn;

    public interface GameMenuCallbacks {
        void showMenu(GameInputDevice devic);
        void hideMenu();
        boolean isMenuOpen();
    }

    public GameMenuCallbacks gameMenuCallbacks;

    public boolean isInputOnly = true;
    public boolean allowChangeMouseMode = true;
    private boolean onExternelDisplay = false;
    private ImageButton floatingMenuButton;
    private ImageButton overlayToggleButton;
    private float floatingButtonDX, floatingButtonDY;
    private boolean isButtonMoving = false;
    private static final float CLICK_ACTION_THRESHOLD = 5;
    private float floatingButtonStartX, floatingButtonStartY;

    // Zoom button drag state
    private float zoomButtonDX, zoomButtonDY;
    private boolean isZoomButtonMoving = false;
    private float zoomButtonStartX, zoomButtonStartY;

    // Queue for batching commitText payloads
    private static final int UTF8_CHUNK_SIZE = 512;
    private final Queue<String> commitTextQueue = new ArrayDeque<>();
    private final Handler commitTextHandler = new Handler(Looper.getMainLooper());

    private final Runnable flushCommitTextQueue = new Runnable() {
        @Override
        public void run() {
            if (commitTextQueue.isEmpty()) {
                return;
            }
            String chunk = commitTextQueue.poll();
            if (conn != null) {
                conn.sendUtf8Text(chunk);
            }
            if (!commitTextQueue.isEmpty()) {
                commitTextHandler.postDelayed(this, 15);
            }
        }
    };

    @SuppressLint({"MissingInflatedId", "ClickableViewAccessibility"})
    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        if (isHubManagedSession()) hubSessionLease = HubSessionLease.start(this, getIntent());
        if (hubSessionLease != null) {
            hubStartupTrace = hubSessionLease.startupTrace();
            hubStartupTrace.event("activity-created");
        }

        instance = this;

        UiHelper.setLocale(this);

        // We don't want a title bar
        requestWindowFeature(Window.FEATURE_NO_TITLE);

        // Full-screen
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN);

        // If we're going to use immersive mode, we want to have
        // the entire screen
        getWindow().getDecorView().setSystemUiVisibility(
                View.SYSTEM_UI_FLAG_LAYOUT_STABLE |
                        View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION |
                        View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN);

        getWindow().addFlags(WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN);

        // Listen for UI visibility events
        getWindow().getDecorView().setOnSystemUiVisibilityChangeListener(this);

        // Change volume button behavior
        setVolumeControlStream(AudioManager.STREAM_MUSIC);

        if (isHubManagedSession()) {
            int softInputMode = getWindow().getAttributes().softInputMode;
            getWindow().setSoftInputMode((softInputMode & ~WindowManager.LayoutParams.SOFT_INPUT_MASK_STATE)
                    | WindowManager.LayoutParams.SOFT_INPUT_STATE_ALWAYS_HIDDEN);
        }

        // Inflate the content
        setContentView(R.layout.activity_game);

        clipboardManager = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);

        // Start the spinner
        spinner = SpinnerDialog.displayDialog(this, getResources().getString(R.string.conn_establishing_title),
                getResources().getString(R.string.conn_establishing_msg), true);

        // Read the stream preferences
        prefConfig = PreferenceConfiguration.readPreferences(this);
        streamHubMode = RtStreamSession.mode(getIntent());
        quitOnStop = getIntent().getBooleanExtra("HubManaged", false);
        tombstonePrefs = Game.this.getSharedPreferences("DecoderTombstone", 0);

        Display currentDisplay = null;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            int displayId = getIntent().getIntExtra(EXTRA_DISPLAY_ID, Display.DEFAULT_DISPLAY);
            currentDisplay = getSystemService(DisplayManager.class).getDisplay(displayId);
        }

        if (currentDisplay == null) {
            currentDisplay = getWindowManager().getDefaultDisplay();
        }

        hubStreamingDisplay = currentDisplay;
        onExternelDisplay = currentDisplay.getDisplayId() != Display.DEFAULT_DISPLAY;
        if (streamHubMode != null) PreferenceConfiguration.applyStreamHubDefaults(this, prefConfig, currentDisplay,
                RtStreamSession.REMOTE.equals(streamHubMode), getIntent().getStringExtra("HubRouteType"));

        if (isHubManagedSession()) {
            applyHubQualityPreset();
            // The product settings page edits base preferences. Legacy per-game
            // overlays must not silently replace its visible audio selection.
            prefConfig.audioOutputMode = com.limelight.streamhub.HubAudioPolicy.outputMode(
                    androidx.preference.PreferenceManager.getDefaultSharedPreferences(this)
                            .getString("streamhub_audio_output", "computer"));
            prefConfig.playHostAudio = com.limelight.streamhub.HubAudioPolicy.playOnHost(streamHubMode, prefConfig.audioOutputMode);
        }

        boolean shouldInvertDecoderResolution = false;
        if (isHubManagedSession()) {
            int preparedFps = getIntent().getIntExtra("HubPreparedFps", 0);
            if (preparedFps >= 24 && preparedFps <= 240) prefConfig.fps = preparedFps;
        }

        if (isHubManagedTablet()) {
            // The PC has already prepared this exact mode. Request the same
            // dimensions regardless of old client rotation preferences or the
            // brief local rotation that can occur while reconnecting.
            int preparedOrientation = hubPreparedOrientation();
            boolean portrait = preparedOrientation == 90 || preparedOrientation == 270;
            int preparedWidth = getIntent().getIntExtra("HubPreparedWidth", 0);
            int preparedHeight = getIntent().getIntExtra("HubPreparedHeight", 0);
            if (preparedWidth <= 0 || preparedHeight <= 0) {
                try {
                    org.json.JSONObject panel = HubDisplayCapabilities.describe(this, preparedOrientation);
                    preparedWidth = panel.optInt("width", 1920); preparedHeight = panel.optInt("height", 1080);
                } catch (Exception ignored) { preparedWidth = portrait ? 1080 : 1920; preparedHeight = portrait ? 1920 : 1080; }
            }
            prefConfig.width = preparedWidth;
            prefConfig.height = preparedHeight;
            // The host result already includes orientation. A second inversion
            // would feed the decoder landscape dimensions for a portrait stream.
            displayWidth = preparedWidth;
            displayHeight = preparedHeight;
            currentOrientation = portrait ? Configuration.ORIENTATION_PORTRAIT : Configuration.ORIENTATION_LANDSCAPE;
            setPreferredOrientationForActivity();
        } else if (isHubManagedSession()) {
            // Remote mode receives the host's physical mode from /v1/prepare.
            // This avoids requesting an oversized stream just because the tablet
            // panel is larger than the laptop's native 2560x1600 mode.
            int preparedWidth = getIntent().getIntExtra("HubPreparedWidth", 0);
            int preparedHeight = getIntent().getIntExtra("HubPreparedHeight", 0);
            if (preparedWidth > 0 && preparedHeight > 0) {
                prefConfig.width = preparedWidth;
                prefConfig.height = preparedHeight;
                displayWidth = preparedWidth;
                displayHeight = preparedHeight;
                prefConfig.videoScaleMode = PreferenceConfiguration.ScaleMode.FIT;
            }
            applyHubQualityPreset();
            displayWidth = prefConfig.width; displayHeight = prefConfig.height;
        } else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M && onExternelDisplay) {
            Display.Mode currentMode = currentDisplay.getMode();
            displayWidth = currentMode.getPhysicalWidth();
            displayHeight = currentMode.getPhysicalHeight();
            prefConfig.width = displayWidth;
            prefConfig.height = displayHeight;
            prefConfig.fps = currentMode.getRefreshRate();
            prefConfig.videoScaleMode = PreferenceConfiguration.ScaleMode.STRETCH;
            prefConfig.enableFloatingButton = false;
            prefConfig.showOverlayZoomToggleButton = false;
            prefConfig.enablePip = false;
            currentOrientation = Configuration.ORIENTATION_LANDSCAPE;
            setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_USER_LANDSCAPE);
        } else {
            if (prefConfig.autoOrientation) {
                currentOrientation = getResources().getConfiguration().orientation;
            } else {
                currentOrientation = Configuration.ORIENTATION_LANDSCAPE;
            }

            boolean portraitMode = currentOrientation == Configuration.ORIENTATION_PORTRAIT;
            shouldInvertDecoderResolution = portraitMode && prefConfig.autoInvertVideoResolution;

            displayWidth = shouldInvertDecoderResolution ? prefConfig.height : prefConfig.width;
            displayHeight = shouldInvertDecoderResolution ? prefConfig.width : prefConfig.height;

            // Enter landscape unless we're on a square screen
            setPreferredOrientationForActivity();
        }


        if (
                prefConfig.videoScaleMode == PreferenceConfiguration.ScaleMode.STRETCH ||
                        shouldIgnoreInsetsForResolution(displayWidth, displayHeight)
        ) {
            // Allow the activity to layout under notches if the fill-screen option
            // was turned on by the user or it's a full-screen native resolution
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                getWindow().getAttributes().layoutInDisplayCutoutMode =
                        WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_ALWAYS;
            }
            else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                getWindow().getAttributes().layoutInDisplayCutoutMode =
                        WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES;
            }
        }

        //光标是否显示
        cursorVisible = prefConfig.enableMouseLocalCursor;

        // Listen for non-touch events on the game surface
        streamView = findViewById(R.id.surfaceView);
        streamView.setOnGenericMotionListener(this);
        streamView.setOnKeyListener(this);
        streamView.setInputCallbacks(this);
        // Product streams become text editors only after the user opens the keyboard.
        streamView.setCommitTextEnabled(!isHubManagedSession() && prefConfig.enableCommitText);

        rootView = streamView.getParent();

        //串流画面 顶部居中显示
        if(prefConfig.alignDisplayTopCenter){
            FrameLayout.LayoutParams params = (FrameLayout.LayoutParams) streamView.getLayoutParams();
            params.gravity = Gravity.CENTER_HORIZONTAL|Gravity.TOP;
        }
        // Listen for touch events on the background touch view to enable trackpad mode
        // to work on areas outside of the StreamView itself. We use a separate View
        // for this rather than just handling it at the Activity level, because that
        // allows proper touch splitting, which the OSC relies upon.
        View backgroundTouchView = findViewById(R.id.backgroundTouchView);
        backgroundTouchView.setOnTouchListener(this);

        panZoomHandler = new PanZoomHandler(
                getApplicationContext(),
                this,
                streamView,
                prefConfig
        );

        // Restore previous zoom & pan if enabled and saved
        if (prefConfig.rememberZoomPan) {
            streamView.post(() -> panZoomHandler.setInitialZoomAndPan(
                    prefConfig.zoomScale,
                    prefConfig.panOffsetX,
                    prefConfig.panOffsetY));
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            // Request unbuffered input event dispatching for all input classes we handle here.
            // Without this, input events are buffered to be delivered in lock-step with VBlank,
            // artificially increasing input latency while streaming.
            streamView.requestUnbufferedDispatch(
                    InputDevice.SOURCE_CLASS_BUTTON | // Keyboards
                            InputDevice.SOURCE_CLASS_JOYSTICK | // Gamepads
                            InputDevice.SOURCE_CLASS_POINTER | // Touchscreens and mice (w/o pointer capture)
                            InputDevice.SOURCE_CLASS_POSITION | // Touchpads
                            InputDevice.SOURCE_CLASS_TRACKBALL // Mice (pointer capture)
            );
            backgroundTouchView.requestUnbufferedDispatch(
                    InputDevice.SOURCE_CLASS_BUTTON | // Keyboards
                            InputDevice.SOURCE_CLASS_JOYSTICK | // Gamepads
                            InputDevice.SOURCE_CLASS_POINTER | // Touchscreens and mice (w/o pointer capture)
                            InputDevice.SOURCE_CLASS_POSITION | // Touchpads
                            InputDevice.SOURCE_CLASS_TRACKBALL // Mice (pointer capture)
            );
        }

        notificationOverlayView = findViewById(R.id.notificationOverlay);

        performanceOverlayView = findViewById(R.id.performanceOverlay);

        performanceOverlayLite = findViewById(R.id.performanceOverlayLite);

        performanceOverlayBig = findViewById(R.id.performanceOverlayBig);

        inputCaptureProvider = InputCaptureManager.getInputCaptureProvider(this, this);

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            streamView.setOnCapturedPointerListener(new View.OnCapturedPointerListener() {
                @Override
                public boolean onCapturedPointer(View view, MotionEvent motionEvent) {
//                    LimeLog.info("onCapturedPointer="+motionEvent.toString());
//                    LimeLog.info("onCapturedPointer-Device="+motionEvent.getDevice().toString());
                    return handleMotionEvent(view, motionEvent);
                }
            });
        }

        // Warn the user if they're on a metered connection
        ConnectivityManager connMgr = (ConnectivityManager) getSystemService(Context.CONNECTIVITY_SERVICE);
        boolean isMetered = connMgr.isActiveNetworkMetered();
        if (isMetered) {
            displayTransientMessage(getResources().getString(R.string.conn_metered));
        }
        // Keep a high-quality USB second screen from silently becoming an
        // unusable Wi-Fi stream.  The cap is per session and never changes the
        // saved settings or the remote desktop profile. Wireless sessions
        // stay capped at 60 fps even if an older profile saved a 90-fps target;
        // high-resolution 90-fps video can overwhelm Wi-Fi queues.
        if (isHubManagedTablet()) applyHubTabletTransportCaps(isMetered);

        // Make sure Wi-Fi is fully powered up
        WifiManager wifiMgr = (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
        try {
            highPerfWifiLock = wifiMgr.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "Moonlight High Perf Lock");
            highPerfWifiLock.setReferenceCounted(false);
            highPerfWifiLock.acquire();

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                lowLatencyWifiLock = wifiMgr.createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "Moonlight Low Latency Lock");
                lowLatencyWifiLock.setReferenceCounted(false);
                lowLatencyWifiLock.acquire();
            }
        } catch (SecurityException e) {
            // Some Samsung Galaxy S10+/S10e devices throw a SecurityException from
            // WifiLock.acquire() even though we have android.permission.WAKE_LOCK in our manifest.
            e.printStackTrace();
        }

        appName = Game.this.getIntent().getStringExtra(EXTRA_APP_NAME);
        pcName = Game.this.getIntent().getStringExtra(EXTRA_PC_NAME);

        host = Game.this.getIntent().getStringExtra(EXTRA_HOST);
        port = Game.this.getIntent().getIntExtra(EXTRA_PORT, streamHubMode == null ? NvHTTP.DEFAULT_HTTP_PORT :
                RtStreamSession.TABLET.equals(streamHubMode) ? RtStreamSession.TABLET_PORT : RtStreamSession.REMOTE_PORT);
        httpsPort = Game.this.getIntent().getIntExtra(EXTRA_HTTPS_PORT, 0); // 0 is treated as unknown
        appUUID = Game.this.getIntent().getStringExtra(EXTRA_APP_UUID);
        appId = Game.this.getIntent().getIntExtra(EXTRA_APP_ID, StreamConfiguration.INVALID_APP_ID);
        uniqueId = Game.this.getIntent().getStringExtra(EXTRA_UNIQUEID);
        vDisplay = Game.this.getIntent().getBooleanExtra(EXTRA_VDISPLAY, false);
        serverCommands = Game.this.getIntent().getStringArrayListExtra(EXTRA_SERVER_COMMANDS);
        if (serverCommands == null) serverCommands = new ArrayList<>();
        boolean appSupportsHdr = Game.this.getIntent().getBooleanExtra(EXTRA_APP_HDR, false);
        byte[] derCertData = Game.this.getIntent().getByteArrayExtra(EXTRA_SERVER_CERT);

        app = new NvApp(appName != null ? appName : "app", appUUID, appId, appSupportsHdr);

        try {
            if (derCertData != null) {
                serverCert = (X509Certificate) CertificateFactory.getInstance("X.509")
                        .generateCertificate(new ByteArrayInputStream(derCertData));

                httpConn = new NvHTTP(new ComputerDetails.AddressTuple(host, port), httpsPort, uniqueId, serverCert, PlatformBinding.getCryptoProvider(this));
                if (isHubManagedSession()) httpConn.boundProductOperations();
            }
        } catch (Exception e) {
            e.printStackTrace();
        }

        if (appId == StreamConfiguration.INVALID_APP_ID) {
            finish();
            return;
        }

        // Initialize the MediaCodec helper before creating the decoder
        GlPreferences glPrefs = GlPreferences.readPreferences(this);
        MediaCodecHelper.initialize(this, glPrefs.glRenderer);

        // Check if the user has enabled HDR
        boolean willStreamHdr = false;
        if (prefConfig.enableHdr) {
            if (onExternelDisplay) {
                // Enforce HDR on unsupported hardware can still enable 10bit streaming for better quality
                willStreamHdr = true;
            } else {
                // Start our HDR checklist
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
                    Display.HdrCapabilities hdrCaps = currentDisplay.getHdrCapabilities();

                    // We must now ensure our display is compatible with HDR10
                    if (hdrCaps != null) {
                        // getHdrCapabilities() returns null on Lenovo Lenovo Mirage Solo (vega), Android 8.0
                        for (int hdrType : hdrCaps.getSupportedHdrTypes()) {
                            if (hdrType == Display.HdrCapabilities.HDR_TYPE_HDR10) {
                                willStreamHdr = true;
                                break;
                            }
                        }
                    }

                    if (!willStreamHdr) {
                        // Nope, no HDR for us :(
                        Toast.makeText(this, "此显示屏不支持 HDR10", Toast.LENGTH_LONG).show();
                    }
                }
                else {
                    Toast.makeText(this, "HDR 需要安卓 7.0 或更高版本", Toast.LENGTH_LONG).show();
                }
            }
        }

        // Check if the user has enabled performance stats overlay
        if (prefConfig.enablePerfOverlay) {
            performanceOverlayView.setVisibility(View.VISIBLE);
            if (prefConfig.enablePerfOverlayLite) {
                performanceOverlayLite.setVisibility(View.VISIBLE);
                if(prefConfig.enablePerfOverlayLiteDialog){
                    performanceOverlayLite.setOnClickListener(v -> showGameMenu(null));
                }
            } else {
                performanceOverlayBig.setVisibility(View.VISIBLE);
            }
            if (prefConfig.enablePerfOverlayBottom) {
                FrameLayout.LayoutParams params = (FrameLayout.LayoutParams) performanceOverlayView.getLayoutParams();
                params.gravity = Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL;
                performanceOverlayView.setLayoutParams(params);
            }
        }

        decoderRenderer = new MediaCodecDecoderRenderer(
                this,
                prefConfig,
                new CrashListener() {
                    @Override
                    public void notifyCrash(Exception e) {
                        // The MediaCodec instance is going down due to a crash
                        // let's tell the user something when they open the app again

                        // We must use commit because the app will crash when we return from this function
                        tombstonePrefs.edit().putInt("CrashCount", tombstonePrefs.getInt("CrashCount", 0) + 1).commit();
                        reportedCrash = true;
                    }
                },
                tombstonePrefs.getInt("CrashCount", 0),
                connMgr.isActiveNetworkMetered(),
                willStreamHdr,
                shouldInvertDecoderResolution,
                glPrefs.glRenderer,
                this);
        if (hubSessionLease != null) decoderRenderer.setFirstFrameRenderedListener(() -> {
            hubSessionLease.firstFrame(hubStartupTrace);
            android.util.Log.i("HubConnection", "stream-first-frame elapsedMs=" +
                    hubSessionLease.connectionElapsedMs());
            if (getIntent().getBooleanExtra("HubCustomResolution", false) && getIntent().getBooleanExtra("HubPreparedDegraded", false))
                runOnUiThread(() -> Toast.makeText(this, "电脑未采用全部请求参数，实际画面：" + displayWidth + " × " + displayHeight + " / " + Math.round(prefConfig.fps) + " FPS", Toast.LENGTH_LONG).show());
        });
        if (hubSessionLease != null) decoderRenderer.setHubTimelineListeners(
                () -> hubStartupTrace.event("first-video-unit"),
                () -> hubStartupTrace.stageStarted("decoder-config"),
                () -> hubStartupTrace.stageCompleted("decoder-config"),
                () -> hubStartupTrace.event("decoder-config-failed"));
        if (hubSessionLease != null) decoderRenderer.setDecoderTimelineListener(
                new MediaCodecDecoderRenderer.DecoderTimelineListener() {
                    @Override
                    public void stageStarted(String key) {
                        hubStartupTrace.stageStarted(key);
                    }

                    @Override
                    public void stageCompleted(String key) {
                        hubStartupTrace.stageCompleted(key);
                    }
                    @Override public void event(String key) { hubStartupTrace.event(key); }
                });

        // Don't stream HDR if the decoder can't support it
        if (willStreamHdr && !decoderRenderer.isHevcMain10Hdr10Supported() && !decoderRenderer.isAv1Main10Supported()) {
            willStreamHdr = false;
            Toast.makeText(this, "此解码器不支持 HDR10", Toast.LENGTH_LONG).show();
        }

        // Display a message to the user if HEVC was forced on but we still didn't find a decoder
        if (prefConfig.videoFormat == PreferenceConfiguration.FormatOption.FORCE_HEVC && !decoderRenderer.isHevcSupported()) {
            Toast.makeText(this, streamHubMode != null ? "HEVC 硬解不可用，自动改用 H.264" : "未找到 HEVC 解码器", Toast.LENGTH_LONG).show();
        }

        // Display a message to the user if AV1 was forced on but we still didn't find a decoder
        if (prefConfig.videoFormat == PreferenceConfiguration.FormatOption.FORCE_AV1 && !decoderRenderer.isAv1Supported()) {
            Toast.makeText(this, "未找到 AV1 解码器", Toast.LENGTH_LONG).show();
        }

        // H.264 is always supported
        int supportedVideoFormats = MoonBridge.VIDEO_FORMAT_H264;
        if (decoderRenderer.isHevcSupported()) {
            supportedVideoFormats |= MoonBridge.VIDEO_FORMAT_H265;
            if (willStreamHdr && decoderRenderer.isHevcMain10Hdr10Supported()) {
                supportedVideoFormats |= MoonBridge.VIDEO_FORMAT_H265_MAIN10;
            }
        }
        if (decoderRenderer.isAv1Supported()) {
            supportedVideoFormats |= MoonBridge.VIDEO_FORMAT_AV1_MAIN8;
            if (willStreamHdr && decoderRenderer.isAv1Main10Supported()) {
                supportedVideoFormats |= MoonBridge.VIDEO_FORMAT_AV1_MAIN10;
            }
        }

        int gamepadMask = ControllerHandler.getAttachedControllerMask(this);
        if (!prefConfig.multiController) {
            // Always set gamepad 1 present for when multi-controller is
            // disabled for games that don't properly support detection
            // of gamepads removed and replugged at runtime.
            gamepadMask = 1;
        }
        if (prefConfig.onscreenController) {
            // If we're using OSC, always set at least gamepad 1.
            gamepadMask |= 1;
        }

        // Set to the optimal mode for streaming
        float displayRefreshRate = prepareDisplayForRendering(currentDisplay);
        LimeLog.info("Display refresh rate: "+displayRefreshRate);

        // If the user requested frame pacing using a capped FPS, we will need to change our
        // desired FPS setting here in accordance with the active display refresh rate.
        int roundedRefreshRate = Math.round(displayRefreshRate);
        float chosenFrameRate = prefConfig.fps;
        if (prefConfig.framePacing == PreferenceConfiguration.FRAME_PACING_CAP_FPS) {
            if (prefConfig.fps >= roundedRefreshRate) {
                if (prefConfig.fps > roundedRefreshRate + 3) {
                    // Use frame drops when rendering above the screen frame rate
                    prefConfig.framePacing = PreferenceConfiguration.FRAME_PACING_BALANCED;
                    LimeLog.info("Using drop mode for FPS > Hz");
                } else if (roundedRefreshRate <= 49) {
                    // Let's avoid clearly bogus refresh rates and fall back to legacy rendering
                    prefConfig.framePacing = PreferenceConfiguration.FRAME_PACING_BALANCED;
                    LimeLog.info("Bogus refresh rate: " + roundedRefreshRate);
                }
                else {
                    chosenFrameRate = roundedRefreshRate - 1;
                    LimeLog.info("Adjusting FPS target for screen to " + chosenFrameRate);
                }
            }
        }

        if (prefConfig.framePacingWarpFactor > 0) {
            chosenFrameRate *= prefConfig.framePacingWarpFactor;
        }

        String hubRoute = hubRouteType();
        int routeMode = "remote".equals(hubRoute) ? StreamConfiguration.STREAM_CFG_REMOTE
                : isHubManagedSession() ? StreamConfiguration.STREAM_CFG_LOCAL : StreamConfiguration.STREAM_CFG_AUTO;
        // The control API can use USB while Android's active network remains
        // Wi-Fi, so AUTO detection can select the wrong media profile. Keep
        // product-managed media on the route that was actually selected.
        // Local Wi-Fi and USB use the standard path MTU and Moonlight's normal
        // payload size. Only the encrypted remote tunnel needs smaller packets
        // to stay below its reduced MTU.
        int packetSize = isHubManagedSession() && "remote".equals(hubRoute) ? 1200 : 1392;

        int hubSessionBitrate = isMetered ? prefConfig.meteredBitrate : prefConfig.bitrate;
        if (isHubManagedSession()) {
            hubStreamLoadPolicy = new HubStreamLoadPolicy();
            hubNetworkLadder = hubSessionBitrate > 0 ? new HubNetworkLadder(hubSessionBitrate) : null;
        }
        StreamConfiguration config = new StreamConfiguration.Builder()
                .setResolution(
                        displayWidth,
                        displayHeight
                )
                .setLaunchRefreshRate(prefConfig.fps)
                .setRefreshRate(chosenFrameRate)
                .setVirtualDisplay(vDisplay)
                .setResolutionScaleFactor(prefConfig.resolutionScaleFactor)
                .setApp(app)
                .setEnableUltraLowLatency(prefConfig.enableUltraLowLatency)
                .setBitrate(hubSessionBitrate)
                .setEnableSops(prefConfig.enableSops)
                // This protocol flag means Windows host playback, not Android
                // playback. Client audio is selected separately at conn.start.
                .enableLocalAudioPlayback(prefConfig.playHostAudio)
                .setMaxPacketSize(packetSize)
                .setRemoteConfiguration(routeMode)
                .setSupportedVideoFormats(supportedVideoFormats)
                .setAttachedGamepadMask(gamepadMask)
                .setClientRefreshRateX100((int)(clientRefreshRate * 100))
                .setAudioConfiguration(prefConfig.audioConfiguration)
                .setColorSpace(decoderRenderer.getPreferredColorSpace())
                .setColorRange(decoderRenderer.getPreferredColorRange())
                .setPersistGamepadsAfterDisconnect(!prefConfig.multiController)
                .build();

        // Initialize the connection
        conn = new NvConnection(getApplicationContext(),
                new ComputerDetails.AddressTuple(host, port),
                httpsPort, uniqueId, config,
                PlatformBinding.getCryptoProvider(this), serverCert);
        controllerHandler = new ControllerHandler(this, conn, this, prefConfig);
        keyboardTranslator = new KeyboardTranslator(prefConfig);

        InputManager inputManager = (InputManager) getSystemService(Context.INPUT_SERVICE);
        inputManager.registerInputDeviceListener(keyboardTranslator, null);

        // Initialize trackpad contexts
        for (int i = 0; i < trackpadContextMap.length; i++) {
            trackpadContextMap[i] = new TrackpadContext(conn, i, prefConfig.trackpadSwapAxis, prefConfig.trackpadSensitivityX, prefConfig.trackpadSensitivityY);
        }

        if (Objects.equals(appUUID, NvApp.REMOTE_INPUT_UUID)) {
            // Force trackpad mode since we won't see anything on the screen
            isInputOnly = true;
            allowChangeMouseMode = false;
            applyMouseMode(2);
        } else {
            if (prefConfig.enableFullExDisplay && onExternelDisplay) {
                requestFocusToExternalDisplayControl(this);
                listenForExternalDisplayRemoval();
            }

            // Initialize touch contexts based on preferences
            // The mouse mode preference is also read in PreferenceConfiguration to set the boolean flags
            initMouseMode();
        }

        if (prefConfig.onscreenController) {
            // create virtual onscreen controller
            if (prefConfig.hideOSCWhenHasGamepad) {
                if (!controllerHandler.hasController()) {
                    initVirtualController();
                }
            } else {
                initVirtualController();
            }
        }

        //特殊按键屏幕布局
        if(prefConfig.enableKeyboard){
            initKeyboardController();
        }

        if (!decoderRenderer.isAvcSupported()) {
            if (spinner != null) {
                spinner.dismiss();
                spinner = null;
            }

            // If we can't find an AVC decoder, we can't proceed
            Dialog.displayDialog(this, getResources().getString(R.string.conn_error_title),
                    "此设备或系统不支持 H.264 硬件解码。", true);
            return;
        }

        // The connection will be started when the surface gets created
        streamView.getHolder().addCallback(this);

        gameMenuCallbacks = new GameMenu(this);

        floatingMenuButton = findViewById(R.id.floatingMenuButton);
        updateFloatingButtonVisibility(prefConfig.enableBackMenu && prefConfig.enableFloatingButton);
        initFloatingButton();

        overlayToggleButton = findViewById(R.id.overlayToggleZoomButton);
        setupOverlayToggleButton();
        if (streamHubMode != null && !onExternelDisplay) {
            hubLiveControls = new HubLiveControls(this);
            if ("remote".equals(streamHubMode) && isHubManagedSession())
                hubDesktopInput = new HubDesktopInput(this, getIntent().getStringExtra(EXTRA_HOST), getIntent().getStringExtra("HubLeaseId"));
            hubWarnings = new HubStreamWarnings(this);
            streamHubInputToolbar = new RtInputToolbar(this, (FrameLayout) rootView, new RtInputToolbar.Callbacks() {
                @Override public void showKeyboard() { toggleKeyboard(); }
                @Override public void showFullKeyboard() { toggleFullKeyboard(); }
                @Override public void hideKeyboard() { hideAllStreamKeyboards(); }
                @Override public void sendKeys(short[] keys) { Game.this.sendKeys(keys); }
                @Override public void sendMouseButton(byte button) {
                    if (connected && conn != null) {
                        conn.sendMouseButtonDown(button);
                        conn.sendMouseButtonUp(button);
                    }
                }
                @Override public void sendMouseScroll(byte amount) {
                    if (connected && conn != null) conn.sendMouseScroll(amount);
                }
                @Override public void sendText(String text) {
                    if (connected && conn != null) enqueueCommitText(text);
                    else Toast.makeText(Game.this, "请先连接电脑，再发送文字", Toast.LENGTH_SHORT).show();
                }
                @Override public void toggleStats() { Game.this.toggleHUD(); }
                @Override public void showDiagnostics() { Game.this.showStreamDiagnostics(); }
                @Override public void showAudioSettings() { Game.this.showAudioOutputSettings(); }
                @Override public void showMouseMode() { showHubTouchModes(); }
                @Override public void showWarningSettings() { hubWarnings.show(Game.this,Game.this::updateHubManagedNetworkWarning); }
                @Override public void showLockSettings() { if(hubLiveControls!=null)hubLiveControls.showLockSettings(); }
                @Override public void showDisplaySettings() {
                    com.limelight.streamhub.HubQualitySettings.show(Game.this, true, null);
                }
                @Override public void showResolutionSettings() { Game.this.showHubResolution(); }
                @Override public boolean privacyAvailable() { return "remote".equals(streamHubMode) && isHubManagedSession(); }
                @Override public void showPrivacySettings() { if (hubLiveControls != null) hubLiveControls.showPrivacy(); }
                @Override public void rotateScreen() { Game.this.rotateScreen(); }
                @Override public void toggleZoom() { Game.this.toggleZoomMode(); }
                @Override public boolean zoomEnabled() { return Game.this.isZoomModeEnabled(); }
                @Override public void sendClipboard() { Game.this.sendClipboard(true); }
                @Override public void fetchClipboard() { Game.this.getClipboard(0); }
                @Override public void disconnect() { if(hubSessionLease!=null)hubSessionLease.userExit();finish(); }
            });
            String modeTitle = RtStreamSession.TABLET.equals(streamHubMode) ? "平板副屏" : "远程桌面";
            String targetName = pcName == null || pcName.trim().isEmpty() ? "我的电脑" : pcName;
            String frameRate = Math.abs(chosenFrameRate - Math.round(chosenFrameRate)) < 0.01f
                    ? Integer.toString(Math.round(chosenFrameRate)) : String.format(Locale.CHINA, "%.1f", chosenFrameRate);
            streamHubInputToolbar.setSessionInfo(targetName + " · " + modeTitle,
                    "目标 " + displayWidth + " × " + displayHeight + " · " + frameRate + " 帧/秒 · " + HubRoutes.label(host));
            streamHubInputToolbar.setStatsVisible(prefConfig.enablePerfOverlay);
            if (isHubManagedSession()) positionHubOverlays();
        }
        if (!onExternelDisplay && HubRotationMonitor.isAutomaticTablet(getIntent(), streamHubMode)) {
            hubRotationMonitor = new HubRotationMonitor(this, degrees -> {
                if (isFinishing() || isDestroyed()) return;
                boolean custom = getIntent().getBooleanExtra("HubCustomResolution", false);
                boolean portrait = degrees == 90 || degrees == 270;
                int longEdge = Math.max(displayWidth, displayHeight), shortEdge = Math.min(displayWidth, displayHeight);
                reconnectHubStream(degrees, custom ? (portrait ? shortEdge : longEdge) : 0,
                        custom ? (portrait ? longEdge : shortEdge) : 0, custom ? Math.round(prefConfig.fps) : 0);

            });
            hubRotationMonitor.start();
        }
    }

    private void showHubResolution() {
        if (!isHubManagedSession() || !connected || hubLiveControls == null) return;
        if (!getIntent().getBooleanExtra("HubSupportsResolution", false)) {
            Toast.makeText(this, "请更新电脑端后使用当前分辨率调整", Toast.LENGTH_LONG).show(); return;
        }
        int nativeWidth = getIntent().getIntExtra("HubNativeWidth", displayWidth);
        int nativeHeight = getIntent().getIntExtra("HubNativeHeight", displayHeight);
        if (isHubManagedTablet()) try {
            org.json.JSONObject nativeMode = HubDisplayCapabilities.describeNative(this, hubPreparedOrientation());
            nativeWidth = nativeMode.getInt("width"); nativeHeight = nativeMode.getInt("height");
        } catch (Exception ignored) { }
        hubLiveControls.showResolution(displayWidth, displayHeight, Math.round(prefConfig.fps), nativeWidth, nativeHeight,
                isHubManagedTablet() ? getIntent().getStringExtra("HubSupportedDisplayModes") == null ? "[]" : getIntent().getStringExtra("HubSupportedDisplayModes") : null,
                (width, height, fps) -> reconnectHubStream(hubPreparedOrientation(), width, height, fps));
    }

    private void reconnectHubStream(int degrees, int width, int height, int fps) {
        if (isFinishing() || hubRotationReconnect || !isHubManagedSession()) return;
        displayedFailureDialog = true; hubRotationReconnect = true; quitOnStop = false;
        stopConnection();
        Intent reconnect = new Intent().setClassName(getPackageName(), "com.limelight.streamhub.HubActivity")
                .addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP | Intent.FLAG_ACTIVITY_SINGLE_TOP)
                .putExtra("HubReconnect", true).putExtra("HubReconnectMode", streamHubMode)
                .putExtra("HubLeaseId", getIntent().getStringExtra("HubLeaseId"))
                .putExtra("HubRequestId", getIntent().getStringExtra("HubRequestId"))
                .putExtra("HubOrientation", degrees).putExtra("HubLink", getIntent().getStringExtra("HubLink"))
                .putExtra("HubAutoRotate", getIntent().getBooleanExtra("HubAutoRotate", false))
                .putExtra("HubTargetWidth", width).putExtra("HubTargetHeight", height).putExtra("HubTargetFps", fps);
        finish(); startActivity(reconnect);
    }

    @SuppressLint("ClickableViewAccessibility")
    private void setupOverlayToggleButton() {
        if (overlayToggleButton != null) {
            if (prefConfig.showOverlayZoomToggleButton && !(isHubManagedSession() && !onExternelDisplay)) {
                overlayToggleButton.setVisibility(View.VISIBLE);

                // Set initial appearance based on current state
                updateZoomButtonAppearance();

                // Touch listener for drag and click
                overlayToggleButton.setOnTouchListener((view, event) -> {
                    switch (event.getAction()) {
                        case MotionEvent.ACTION_DOWN:
                            zoomButtonStartX = event.getRawX();
                            zoomButtonStartY = event.getRawY();
                            zoomButtonDX = view.getX() - event.getRawX();
                            zoomButtonDY = view.getY() - event.getRawY();
                            isZoomButtonMoving = false;
                            return true;
                        case MotionEvent.ACTION_MOVE:
                            float newX = event.getRawX() + zoomButtonDX;
                            float newY = event.getRawY() + zoomButtonDY;

                            // Check if it's a move or just a tap
                            if (Math.abs(event.getRawX() - zoomButtonStartX) > CLICK_ACTION_THRESHOLD ||
                                    Math.abs(event.getRawY() - zoomButtonStartY) > CLICK_ACTION_THRESHOLD) {
                                isZoomButtonMoving = true;
                            }

                            // Ensure the button stays within screen bounds
                            if (newX < 0) newX = 0;
                            if (newY < 0) newY = 0;

                            int maxOffsetX = getWindow().getDecorView().getWidth() - view.getWidth();
                            if (newX > maxOffsetX) {
                                newX = maxOffsetX;
                            }

                            int maxOffsetY = getWindow().getDecorView().getHeight() - view.getHeight();
                            if (newY > maxOffsetY) {
                                newY = maxOffsetY;
                            }

                            view.setX(newX);
                            view.setY(newY);
                            return true;
                        case MotionEvent.ACTION_UP:
                            if (!isZoomButtonMoving) {
                                // It's a click event, toggle zoom mode
                                toggleZoomMode();
                                updateZoomButtonAppearance();
                            }
                            isZoomButtonMoving = false;
                            return true;
                        default:
                            return false;
                    }
                });
            } else {
                overlayToggleButton.setVisibility(View.GONE);
            }
        }
    }

    private void updateZoomButtonAppearance() {
        if (overlayToggleButton != null) {
            // Change background based on pan/zoom mode state
            overlayToggleButton.setBackgroundResource(isPanZoomMode ?
                R.drawable.floating_menu_button_active : R.drawable.floating_menu_button);
            // No need for alpha changes since the color indicates the state
            overlayToggleButton.setAlpha(1.0f);
        }
    }

    private void listenForExternalDisplayRemoval() {
        DisplayManager displayManager = (DisplayManager) getSystemService(Context.DISPLAY_SERVICE);
        displayManager.registerDisplayListener(new DisplayManager.DisplayListener() {
            @Override
            public void onDisplayAdded(int displayId) {
            }

            @Override
            public void onDisplayRemoved(int displayId) {
                if (getSecondaryDisplay(getBaseContext()) == null) {
                    handleDisplayRemoved();
                    finish();
                }
            }

            @Override
            public void onDisplayChanged(int displayId) {
            }
        }, null);
    }

    private void handleDisplayRemoved() {
        NotificationManagerCompat.from(getBaseContext()).cancel(SECONDARY_SCREEN_NOTIFICATION_ID);
        closeExternalDisplayControl();
    }

    @SuppressLint("ClickableViewAccessibility")
    private void initFloatingButton() {
        // Touch listener for drag and click
        if (floatingMenuButton != null) {
            floatingMenuButton.setOnTouchListener((view, event) -> {
                switch (event.getAction()) {
                    case MotionEvent.ACTION_DOWN:
                        floatingButtonStartX = event.getRawX();
                        floatingButtonStartY = event.getRawY();
                        floatingButtonDX = view.getX() - event.getRawX();
                        floatingButtonDY = view.getY() - event.getRawY();
                        isButtonMoving = false;
                        return true;
                    case MotionEvent.ACTION_MOVE:
                        float newX = event.getRawX() + floatingButtonDX;
                        float newY = event.getRawY() + floatingButtonDY;

                        // Check if it's a move or just a tap
                        if (Math.abs(event.getRawX() - floatingButtonStartX) > CLICK_ACTION_THRESHOLD ||
                                Math.abs(event.getRawY() - floatingButtonStartY) > CLICK_ACTION_THRESHOLD) {
                            isButtonMoving = true;
                        }

                        // Ensure the button stays within screen bounds
                        if (newX < 0) newX = 0;
                        if (newY < 0) newY = 0;

                        int maxOffsetX = getWindow().getDecorView().getWidth() - view.getWidth();
                        if (newX > maxOffsetX) {
                            newX = maxOffsetX;
                        }

                        int maxOffsetY = getWindow().getDecorView().getHeight() - view.getHeight();
                        if (newY > maxOffsetY) {
                            newY = maxOffsetY;
                        }

                        view.setX(newX);
                        view.setY(newY);
                        return true;
                    case MotionEvent.ACTION_UP:
                        if (!isButtonMoving) {
                            // It's a click event, show menu
                            showGameMenu(null);
                        }
                        isButtonMoving = false;
                        return true;
                    default:
                        return false;
                }
            });
        }
    }

    private void initKeyboardController(){
        keyBoardController = new KeyBoardController(conn,(FrameLayout)rootView, this);
        keyBoardController.refreshLayout();
        keyBoardController.show();
    }

    public Boolean isKeyboardLayoutVisible() {
        return keyBoardLayoutController != null && keyBoardLayoutController.shown;
    }

    private void initVirtualController(){
        virtualController = new VirtualController(controllerHandler, (FrameLayout)rootView, this);
        virtualController.refreshLayout();
        virtualController.show();
    }

    private void initkeyBoardLayoutController(){
        keyBoardLayoutController = new KeyBoardLayoutController((FrameLayout)rootView, this, prefConfig);
        keyBoardLayoutController.refreshLayout();
        keyBoardLayoutController.show();
    }

    //显示隐藏虚拟特殊按键
    public void toggleKeyboardController(){
        if (keyBoardController==null) {
            initKeyboardController();
            return;
        }
        keyBoardController.toggleVisibility();
    }

    public void toggleFullKeyboard() {
        if (isOnExternalDisplay()) {
            ExternalDisplayControlActivity.toggleFullKeyboard();
            return;
        }
        if (keyBoardLayoutController == null) {
            initkeyBoardLayoutController();
            return;
        }
        keyBoardLayoutController.toggleVisibility();
    }

    //显示隐藏虚拟手柄控制器
    public void toggleVirtualController(){
        if (virtualController==null) {
            initVirtualController();
            prefConfig.onscreenController=true;
            return;
        }
        prefConfig.onscreenController= virtualController.switchShowHide() != 0;
    }

    private void setPreferredOrientationForActivity() {
        if (isHubManagedSession() && !onExternelDisplay && getIntent().getBooleanExtra("HubAutoRotate", false)) {
            // Automatic sessions follow the device while respecting the system
            // rotation lock, in both remote-control and second-screen modes.
            // The stream keeps the host-negotiated dimensions and FIT mode;
            // this only changes the Android activity orientation policy.
            HubRotationMonitor.allowAllDirections(this);
            return;
        }
        if (isHubManagedTablet()) {
            int orientation;
                switch (hubPreparedOrientation()) {
                    case 90: orientation = ActivityInfo.SCREEN_ORIENTATION_PORTRAIT; break;
                    case 180: orientation = ActivityInfo.SCREEN_ORIENTATION_REVERSE_LANDSCAPE; break;
                    case 270: orientation = ActivityInfo.SCREEN_ORIENTATION_REVERSE_PORTRAIT; break;
                    default: orientation = ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE; break;
                }
            setRequestedOrientation(orientation);
            return;
        }
        Display display = getActiveDisplay(Game.this, prefConfig);

        // For semi-square displays, we use more complex logic to determine which orientation to use (if any)
        if (PreferenceConfiguration.isSquarishScreen(display)) {
            int desiredOrientation = Configuration.ORIENTATION_UNDEFINED;

            // OSC doesn't properly support portrait displays, so don't use it in portrait mode by default
            if (prefConfig.onscreenController) {
                desiredOrientation = Configuration.ORIENTATION_LANDSCAPE;
            }

            // For native resolution, we will lock the orientation to the one that matches the specified resolution
            if (PreferenceConfiguration.isNativeResolution(prefConfig.width, prefConfig.height)) {
                if (displayWidth > displayHeight) {
                    desiredOrientation = Configuration.ORIENTATION_LANDSCAPE;
                }
                else {
                    desiredOrientation = Configuration.ORIENTATION_PORTRAIT;
                }
            }

            if (desiredOrientation == Configuration.ORIENTATION_LANDSCAPE) {
                setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_USER_LANDSCAPE);
            }
            else if (desiredOrientation == Configuration.ORIENTATION_PORTRAIT) {
                setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_USER_PORTRAIT);
            }
            else {
                // If we don't have a reason to lock to portrait or landscape, allow any orientation
                setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_FULL_USER);
            }
        }
        else {
            // Lock to current orientation
            if (currentOrientation == Configuration.ORIENTATION_LANDSCAPE) {
                setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_USER_LANDSCAPE);
            } else {
                setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_USER_PORTRAIT);
            }
        }
    }

    public boolean isHubManagedSession() {
        return getIntent().getBooleanExtra("HubManaged", false);
    }

    private boolean isHubManagedTablet() {
        return isHubManagedSession() && RtStreamSession.TABLET.equals(streamHubMode);
    }

    private String hubRouteType() {
        String route = getIntent().getStringExtra("HubRouteType");
        if (route == null || route.isEmpty()) {
            String address = getIntent().getStringExtra(EXTRA_HOST);
            route = address == null || address.isEmpty() ? "lan" : HubRoutes.type(address);
        }
        return route;
    }

    private void applyHubQualityPreset() {
        String preset = new HubStore(this).get("qualityPreset", "advanced");
        if ("advanced".equals(preset)) return;
        if (!getIntent().getBooleanExtra("HubCustomResolution", false)) {
            int maxEdge = "smooth".equals(preset) ? 1280 : "quality".equals(preset) ? 2560 : 1920;
            float scale = Math.min(1f, maxEdge / (float)Math.max(prefConfig.width, prefConfig.height));
            prefConfig.width = Math.max(2, Math.round(prefConfig.width * scale) / 2 * 2);
            prefConfig.height = Math.max(2, Math.round(prefConfig.height * scale) / 2 * 2);
            prefConfig.fps = Math.min(prefConfig.fps, "smooth".equals(preset) ? 30 : "quality".equals(preset) && "usb".equals(hubRouteType()) ? 90 : 60);
        }
        prefConfig.bitrate = "smooth".equals(preset) ? 8000 : "quality".equals(preset) ? 32000 : 18000;
        prefConfig.meteredBitrate = Math.min(prefConfig.meteredBitrate, prefConfig.bitrate);
    }

    private void applyHubTabletTransportCaps(boolean metered) {
        String route = hubRouteType();
        int maxKbps = HubTransportPolicy.bitrateCap(new HubStore(this).get("tabletQuality", "stable"), route, metered);
        android.content.SharedPreferences prefs = ProfilesManager.getInstance().getOverlayingSharedPreferences(this);
        if ("advanced".equals(new HubStore(this).get("qualityPreset", "advanced")) && !prefs.contains("seekbar_bitrate_kbps") && !prefs.contains("seekbar_bitrate") && !prefs.contains("edit_diy_bitrate"))
            prefConfig.bitrate = maxKbps;
        // Kbps throughout; explicit lower quality settings remain in force.
        prefConfig.bitrate = Math.min(prefConfig.bitrate, maxKbps);
        prefConfig.meteredBitrate = Math.min(prefConfig.meteredBitrate, maxKbps);
        prefConfig.fps = HubTransportPolicy.limitFps(prefConfig.fps, route);
    }

    private void setHubConnectionState(String text) {
        hubConnectionState = text;
    }

    private void positionHubOverlays() {
        if (!isHubManagedSession() || onExternelDisplay) return;
        // Keep the connection warning clear of the controls at the top.
        FrameLayout.LayoutParams warningParams = (FrameLayout.LayoutParams) notificationOverlayView.getLayoutParams();
        warningParams.gravity = Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL;
        warningParams.setMargins(Math.round(UiHelper.dpToPx(this, 16)), 0,
                Math.round(UiHelper.dpToPx(this, 16)), Math.round(UiHelper.dpToPx(this, 24)));
        notificationOverlayView.setLayoutParams(warningParams);
        notificationOverlayView.setGravity(Gravity.CENTER);
        notificationOverlayView.setTextSize(12);
        notificationOverlayView.setMaxWidth(Math.max(1, getResources().getDisplayMetrics().widthPixels - Math.round(UiHelper.dpToPx(this, 32))));
        notificationOverlayView.setElevation(UiHelper.dpToPx(this, 26));
        if (!prefConfig.enablePerfOverlayBottom) {
            FrameLayout.LayoutParams statsParams = (FrameLayout.LayoutParams) performanceOverlayView.getLayoutParams();
            statsParams.topMargin = Math.round(UiHelper.dpToPx(this, 64));
            performanceOverlayView.setLayoutParams(statsParams);
        }
    }

    private int hubPreparedOrientation() {
        int orientation = getIntent().getIntExtra("HubOrientation", 0);
        return orientation == 90 || orientation == 180 || orientation == 270 ? orientation : 0;
    }

    @Override
    public void onConfigurationChanged(Configuration newConfig) {
        super.onConfigurationChanged(newConfig);
        if (streamHubInputToolbar != null) {
            streamHubInputToolbar.refreshLayout();
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) streamHubInputToolbar.setPictureInPicture(isInPictureInPictureMode());
            positionHubOverlays();
        }

        // Set requested orientation for possible new screen size
        setPreferredOrientationForActivity();
        if (isHubManagedSession()) reapplyHubDisplayMode("configuration-changed");

        if (virtualController != null) {
            // Refresh layout of OSC for possible new screen size
            virtualController.refreshLayout();
        }

        if(keyBoardController != null){
            keyBoardController.refreshLayout();
        }

        if(keyBoardLayoutController != null){
            keyBoardLayoutController.refreshLayout();
        }

        // Hide on-screen overlays in PiP mode
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            if (isInPictureInPictureMode()) {
                isHidingOverlays = true;

                floatingButtonShown = floatingMenuButton.isShown();

                if (floatingButtonShown) {
                    floatingMenuButton.setVisibility(View.GONE);
                }

                overlayToggleZoomButtonShown = overlayToggleButton != null && overlayToggleButton.isShown();

                if (overlayToggleZoomButtonShown) {
                    overlayToggleButton.setVisibility(View.GONE);
                }

                if (virtualController != null) {
                    virtualController.hide();
                }

                if (keyBoardController != null && keyBoardController.shown) {
                    keyBoardController.hide(true);
                }

                if (keyBoardLayoutController!=null && keyBoardLayoutController.shown) {
                    keyBoardLayoutController.hide(true);
                }

                hideGameMenu();

                performanceOverlayView.setVisibility(View.GONE);
                notificationOverlayView.setVisibility(View.GONE);

                // Disable sensors while in PiP mode
                controllerHandler.disableSensors();

                // Update GameManager state to indicate we're in PiP (still gaming, but interruptible)
                UiHelper.notifyStreamEnteringPiP(this);
            }
            else {
                isHidingOverlays = false;

                if (floatingButtonShown) {
                    floatingMenuButton.setVisibility(View.VISIBLE);
                }

                if (overlayToggleZoomButtonShown) {
                    overlayToggleButton.setVisibility(View.VISIBLE);
                }

                // Restore overlays to previous state when leaving PiP

                if (virtualController != null) {
                    virtualController.show();
                }

                if (keyBoardController != null && keyBoardController.shown) {
                    keyBoardController.show();
                }

                if(keyBoardLayoutController!=null && keyBoardLayoutController.shown){
                    keyBoardLayoutController.show();
                }

                if (prefConfig.enablePerfOverlay) {
                    performanceOverlayView.setVisibility(View.VISIBLE);
                }

                notificationOverlayView.setVisibility(requestedNotificationOverlayVisibility);

                // Enable sensors again after exiting PiP
                controllerHandler.enableSensors();

                // Update GameManager state to indicate we're out of PiP (gaming, non-interruptible)
                UiHelper.notifyStreamExitingPiP(this);
            }
        }
    }

    @TargetApi(Build.VERSION_CODES.O)
    private PictureInPictureParams getPictureInPictureParams(boolean autoEnter) {
        View view;
        Rect hint;
        if (prefConfig.videoScaleMode == PreferenceConfiguration.ScaleMode.FIT && streamView.getScaleX() == 1) {
            view = streamView;
        } else {
            view = (View)rootView;
        }

        int[] viewLocation = new int[2];

        view.getLocationOnScreen(viewLocation);

        int left = viewLocation[0];
        int top = viewLocation[1];
        int width = view.getWidth();
        int height = view.getHeight();
        Rational aspectRatio = new Rational(width, height);
        hint = new Rect(left, top, left + width, top + height);

        PictureInPictureParams.Builder builder =
                new PictureInPictureParams.Builder()
                        .setAspectRatio(aspectRatio)
                        .setSourceRectHint(hint);

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            builder.setAutoEnterEnabled(autoEnter);
            builder.setSeamlessResizeEnabled(true);
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (appName != null) {
                builder.setTitle(AppView.getDisplayAppName(appName));
                if (pcName != null) {
                    builder.setSubtitle(pcName);
                }
            }
            else if (pcName != null) {
                builder.setTitle(pcName);
            }
        }

        return builder.build();
    }

    public void updatePipAutoEnter() {
        if (!prefConfig.enablePip) {
            return;
        }

        boolean autoEnter = connected && suppressPipRefCount == 0;

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            setPictureInPictureParams(getPictureInPictureParams(autoEnter));
        }
        else {
            autoEnterPip = autoEnter;
        }
    }

    public void setMetaKeyCaptureState(boolean enabled) {
        // This uses custom APIs present on some Samsung devices to allow capture of
        // meta key events while streaming.
        try {
            Class<?> semWindowManager = Class.forName("com.samsung.android.view.SemWindowManager");
            Method getInstanceMethod = semWindowManager.getMethod("getInstance");
            Object manager = getInstanceMethod.invoke(null);

            if (manager != null) {
                Class<?>[] parameterTypes = new Class<?>[2];
                parameterTypes[0] = ComponentName.class;
                parameterTypes[1] = boolean.class;
                Method requestMetaKeyEventMethod = semWindowManager.getDeclaredMethod("requestMetaKeyEvent", parameterTypes);
                requestMetaKeyEventMethod.invoke(manager, this.getComponentName(), enabled);
            }
            else {
                LimeLog.warning("SemWindowManager.getInstance() returned null");
            }
        } catch (ClassNotFoundException | NoSuchMethodException | InvocationTargetException |
                 IllegalAccessException e) {
            e.printStackTrace();
        }
    }

    @Override
    public void onUserLeaveHint() {
        super.onUserLeaveHint();

        // PiP is only supported on Oreo and later, and we don't need to manually enter PiP on
        // Android S and later. On Android R, we will use onPictureInPictureRequested() instead.
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && Build.VERSION.SDK_INT < Build.VERSION_CODES.R) {
            if (autoEnterPip) {
                try {
                    // This has thrown all sorts of weird exceptions on Samsung devices
                    // running Oreo. Just eat them and close gracefully on leave, rather
                    // than crashing.
                    enterPictureInPictureMode(getPictureInPictureParams(false));
                } catch (Exception e) {
                    e.printStackTrace();
                }
            }
        }
    }

    @Override
    @TargetApi(Build.VERSION_CODES.R)
    public boolean onPictureInPictureRequested() {
        // Enter PiP when requested unless we're on Android 12 which supports auto-enter.
        if (autoEnterPip && Build.VERSION.SDK_INT < Build.VERSION_CODES.S) {
            enterPictureInPictureMode(getPictureInPictureParams(false));
        }
        return true;
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (!hasFocus && isHubManagedSession()) releaseHubTouches();

        // We can't guarantee the state of modifiers keys which may have
        // lifted while focus was not on us. Clear the modifier state.
        this.modifierFlags = 0;

        // With Android native pointer capture, capture is lost when focus is lost,
        // so it must be requested again when focus is regained.
        inputCaptureProvider.onWindowFocusChanged(hasFocus);

        if (hasFocus) reapplyHubDisplayMode("window-focus");
    }

    private boolean isRefreshRateEqualMatch(float refreshRate) {
        return refreshRate >= prefConfig.fps &&
                refreshRate <= prefConfig.fps + 3;
    }

    private boolean isRefreshRateGoodMatch(float refreshRate) {
        return refreshRate >= prefConfig.fps &&
                Math.round(refreshRate) % prefConfig.fps <= 3;
    }

    private boolean shouldIgnoreInsetsForResolution(int width, int height) {
        // Never ignore insets for non-native resolutions
        if (!PreferenceConfiguration.isNativeResolution(width, height)) {
            return false;
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            Display display = getActiveDisplay(Game.this, prefConfig);
            for (Display.Mode candidate : display.getSupportedModes()) {
                // Ignore insets if this is an exact match for the display resolution
                if ((width == candidate.getPhysicalWidth() && height == candidate.getPhysicalHeight()) ||
                        (height == candidate.getPhysicalWidth() && width == candidate.getPhysicalHeight())) {
                    return true;
                }
            }
        }

        return false;
    }

    private boolean mayReduceRefreshRate() {
        return prefConfig.framePacing == PreferenceConfiguration.FRAME_PACING_CAP_FPS ||
                prefConfig.framePacing == PreferenceConfiguration.FRAME_PACING_MAX_SMOOTHNESS ||
                (prefConfig.framePacing == PreferenceConfiguration.FRAME_PACING_BALANCED && prefConfig.reduceRefreshRate);
    }

    private void requestHubPanelMode(String reason) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.M || prefConfig == null ||
                !HubDisplayModePolicy.applies(isHubManagedSession(), onExternelDisplay)) return;

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.VANILLA_ICE_CREAM && prefConfig.fps > 60) {
            getWindow().setFrameRatePowerSavingsBalanced(false);
        }

        Display activeDisplay = currentStreamingDisplay();
        if (activeDisplay != null) {
            Display.Mode currentMode = activeDisplay.getMode();
            Display.Mode[] supportedModes = activeDisplay.getSupportedModes();
            int[] widths = new int[supportedModes.length];
            int[] heights = new int[supportedModes.length];
            float[] refreshRates = new float[supportedModes.length];
            for (int i = 0; i < supportedModes.length; i++) {
                widths[i] = supportedModes[i].getPhysicalWidth();
                heights[i] = supportedModes[i].getPhysicalHeight();
                refreshRates[i] = supportedModes[i].getRefreshRate();
            }
            int matchingIndex = HubDisplayModePolicy.bestMatchingModeIndex(
                    currentMode.getPhysicalWidth(), currentMode.getPhysicalHeight(), prefConfig.fps,
                    widths, heights, refreshRates);
            int previousModeId = hubRequestedDisplayModeId;
            if (matchingIndex >= 0) {
                Display.Mode matchingMode = supportedModes[matchingIndex];
                hubRequestedDisplayModeId = matchingMode.getModeId();
                hubRequestedDisplayRefreshRate = matchingMode.getRefreshRate();
            }
            else {
                hubRequestedDisplayModeId = 0;
                hubRequestedDisplayRefreshRate = 0;
                if (previousModeId > 0) {
                    WindowManager.LayoutParams previousParams = getWindow().getAttributes();
                    if (previousParams.preferredDisplayModeId == previousModeId) {
                        previousParams.preferredDisplayModeId = 0;
                        previousParams.preferredRefreshRate = 0;
                        getWindow().setAttributes(previousParams);
                    }
                }
                LimeLog.warning("No exact Hub display mode for " + prefConfig.fps +
                        " FPS at " + currentMode.getPhysicalWidth() + "x" + currentMode.getPhysicalHeight());
                return;
            }
        }

        if (hubRequestedDisplayModeId <= 0) return;

        WindowManager.LayoutParams params = getWindow().getAttributes();
        params.preferredDisplayModeId = hubRequestedDisplayModeId;
        params.preferredRefreshRate = hubRequestedDisplayRefreshRate;
        getWindow().setAttributes(params);

        if (activeDisplay != null) {
            Display.Mode activeMode = activeDisplay.getMode();
            LimeLog.info("Hub display mode request (" + reason + "): mode " + hubRequestedDisplayModeId +
                    " at " + hubRequestedDisplayRefreshRate + " Hz; active mode " + activeMode.getModeId() +
                    " at " + activeMode.getRefreshRate() + " Hz");
        }
        else {
            LimeLog.info("Hub display mode request (" + reason + "): mode " + hubRequestedDisplayModeId +
                    " at " + hubRequestedDisplayRefreshRate + " Hz");
        }
    }

    private float surfaceFrameRate() {
        if (mayReduceRefreshRate() || desiredRefreshRate < prefConfig.fps) return prefConfig.fps;
        return desiredRefreshRate;
    }

    private void applySurfaceFrameRate(Surface surface, String reason) {
        if (surface == null || !surface.isValid()) return;
        float frameRate = surfaceFrameRate();
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            int compatibility = Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.BAKLAVA &&
                    HubDisplayModePolicy.shouldUseAtLeastSurfaceRate(
                    isHubManagedSession(), onExternelDisplay, frameRate, Build.VERSION.SDK_INT)) {
                compatibility = Surface.FRAME_RATE_COMPATIBILITY_AT_LEAST;
            }
            surface.setFrameRate(frameRate, compatibility,
                    Surface.CHANGE_FRAME_RATE_ALWAYS);
        }
        else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            surface.setFrameRate(frameRate, Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE);
        }
        LimeLog.info("Surface frame rate request (" + reason + "): " + frameRate + " FPS");
    }

    /** Re-submit both window and Surface hints after Android has attached or refocused the stream. */
    private void reapplyHubDisplayMode(String reason) {
        requestHubPanelMode(reason);
        if (surfaceCreated && streamView != null) {
            applySurfaceFrameRate(streamView.getHolder().getSurface(), reason);
        }

        View decorView = getWindow().getDecorView();
        if (decorView != null) {
            decorView.post(() -> {
                if (connectionStopRequested || isFinishing() || isDestroyed()) return;
                requestHubPanelMode(reason + "-post");
                if (surfaceCreated && streamView != null) {
                    applySurfaceFrameRate(streamView.getHolder().getSurface(), reason + "-post");
                }
            });
        }
    }

    public boolean isOnExternalDisplay() {
        return onExternelDisplay;
    }

    private float prepareDisplayForRendering(Display currentDisplay) {
        WindowManager.LayoutParams windowLayoutParams = getWindow().getAttributes();
        float displayRefreshRate;
        boolean hubPanelModeMatched = false;
        hubRequestedDisplayModeId = 0;
        hubRequestedDisplayRefreshRate = 0;

        // On M, we can explicitly set the optimal display mode
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            Display.Mode currentMode = currentDisplay.getMode();
            Display.Mode bestMode = currentMode;
            Display.Mode hubMatchingMode = null;
            boolean managedSession = isHubManagedSession();
            boolean preferHubPanelMode = HubDisplayModePolicy.applies(managedSession, onExternelDisplay);
            boolean isNativeResolutionStream = PreferenceConfiguration.isNativeResolution(prefConfig.width, prefConfig.height);
            boolean refreshRateIsGood = isRefreshRateGoodMatch(bestMode.getRefreshRate());
            boolean refreshRateIsEqual = isRefreshRateEqualMatch(bestMode.getRefreshRate());

            LimeLog.info("Current display mode: "+bestMode.getPhysicalWidth()+"x"+
                    bestMode.getPhysicalHeight()+"x"+bestMode.getRefreshRate());

            for (Display.Mode candidate : currentDisplay.getSupportedModes()) {
                if (preferHubPanelMode && HubDisplayModePolicy.isBetterMatch(
                        currentMode.getPhysicalWidth(), currentMode.getPhysicalHeight(), prefConfig.fps,
                        candidate.getPhysicalWidth(), candidate.getPhysicalHeight(), candidate.getRefreshRate(),
                        hubMatchingMode == null ? Float.NaN : hubMatchingMode.getRefreshRate())) {
                    hubMatchingMode = candidate;
                }

                boolean refreshRateReduced = candidate.getRefreshRate() < bestMode.getRefreshRate();
                boolean resolutionReduced = candidate.getPhysicalWidth() < bestMode.getPhysicalWidth() ||
                        candidate.getPhysicalHeight() < bestMode.getPhysicalHeight();
                boolean resolutionFitsStream = candidate.getPhysicalWidth() >= prefConfig.width &&
                        candidate.getPhysicalHeight() >= prefConfig.height;

                LimeLog.info("Examining display mode: "+candidate.getPhysicalWidth()+"x"+
                        candidate.getPhysicalHeight()+"x"+candidate.getRefreshRate());

                if (candidate.getPhysicalWidth() > 4096 && prefConfig.width <= 4096) {
                    // Avoid resolutions options above 4K to be safe
                    continue;
                }

                // On non-4K streams, we force the resolution to never change unless it's above
                // 60 FPS, which may require a resolution reduction due to HDMI bandwidth limitations,
                // or it's a native resolution stream.
                if (prefConfig.width < 3840 && prefConfig.fps <= 60 && !isNativeResolutionStream) {
                    if (currentDisplay.getMode().getPhysicalWidth() != candidate.getPhysicalWidth() ||
                            currentDisplay.getMode().getPhysicalHeight() != candidate.getPhysicalHeight()) {
                        continue;
                    }
                }

                // Make sure the resolution doesn't regress unless if it's over 60 FPS
                // where we may need to reduce resolution to achieve the desired refresh rate.
                if (resolutionReduced && !(prefConfig.fps > 60 && resolutionFitsStream)) {
                    continue;
                }

                if (mayReduceRefreshRate() && refreshRateIsEqual && !isRefreshRateEqualMatch(candidate.getRefreshRate())) {
                    // If we had an equal refresh rate and this one is not, skip it. In min latency
                    // mode, we want to always prefer the highest frame rate even though it may cause
                    // microstuttering.
                    continue;
                }
                else if (refreshRateIsGood) {
                    // We've already got a good match, so if this one isn't also good, it's not
                    // worth considering at all.
                    if (!isRefreshRateGoodMatch(candidate.getRefreshRate())) {
                        continue;
                    }

                    if (mayReduceRefreshRate()) {
                        // User asked for the lowest possible refresh rate, so don't raise it if we
                        // have a good match already
                        if (candidate.getRefreshRate() > bestMode.getRefreshRate()) {
                            continue;
                        }
                    }
                    else {
                        // User asked for the highest possible refresh rate, so don't reduce it if we
                        // have a good match already
                        if (refreshRateReduced) {
                            continue;
                        }
                    }
                }
                else if (!isRefreshRateGoodMatch(candidate.getRefreshRate())) {
                    // We didn't have a good match and this match isn't good either, so just don't
                    // reduce the refresh rate.
                    if (refreshRateReduced) {
                        continue;
                    }
                } else {
                    // We didn't have a good match and this match is good. Prefer this refresh rate
                    // even if it reduces the refresh rate. Lowering the refresh rate can be beneficial
                    // when streaming a 60 FPS stream on a 90 Hz device. We want to select 60 Hz to
                    // match the frame rate even if the active display mode is 90 Hz.
                }

                bestMode = candidate;
                refreshRateIsGood = isRefreshRateGoodMatch(candidate.getRefreshRate());
                refreshRateIsEqual = isRefreshRateEqualMatch(candidate.getRefreshRate());
            }

            if (hubMatchingMode != null) bestMode = hubMatchingMode;
            hubPanelModeMatched = hubMatchingMode != null;

            LimeLog.info("Best display mode: "+bestMode.getPhysicalWidth()+"x"+
                    bestMode.getPhysicalHeight()+"x"+bestMode.getRefreshRate());

            if (hubMatchingMode != null) {
                // Keep a preference even if the panel already happens to be in this mode.
                hubRequestedDisplayModeId = bestMode.getModeId();
                hubRequestedDisplayRefreshRate = bestMode.getRefreshRate();
                windowLayoutParams.preferredDisplayModeId = bestMode.getModeId();
                windowLayoutParams.preferredRefreshRate = bestMode.getRefreshRate();
                getWindow().setAttributes(windowLayoutParams);
                LimeLog.info("Hub tablet requested display mode " + bestMode.getModeId() +
                        " at " + bestMode.getRefreshRate() + " Hz for " + prefConfig.fps +
                        " FPS; current display reports " + currentDisplay.getRefreshRate() + " Hz");
            }
            else if (currentDisplay.getMode().getModeId() != bestMode.getModeId()) {
                // If we only changed refresh rate and we're on an OS that supports Surface.setFrameRate()
                // use that instead of using preferredDisplayModeId to avoid the possibility of triggering
                // bugs that can cause the system to switch from 4K60 to 4K24 on Chromecast 4K.
                if (prefConfig.enforceDisplayMode ||
                        Build.VERSION.SDK_INT < Build.VERSION_CODES.S ||
                        currentDisplay.getMode().getPhysicalWidth() != bestMode.getPhysicalWidth() ||
                        currentDisplay.getMode().getPhysicalHeight() != bestMode.getPhysicalHeight()) {
                    // Apply the display mode change
                    windowLayoutParams.preferredDisplayModeId = bestMode.getModeId();
                    if (managedSession && !onExternelDisplay) {
                        windowLayoutParams.preferredRefreshRate = bestMode.getRefreshRate();
                    }
                    getWindow().setAttributes(windowLayoutParams);
                }
                else {
                    LimeLog.info("Using setFrameRate() instead of preferredDisplayModeId due to matching resolution");
                }
            }
            else {
                LimeLog.info("Current display mode is already the best display mode");
            }

            displayRefreshRate = bestMode.getRefreshRate();
        }
        // On L, we can at least tell the OS that we want a refresh rate
        else {
            float bestRefreshRate = currentDisplay.getRefreshRate();
            for (float candidate : currentDisplay.getSupportedRefreshRates()) {
                LimeLog.info("Examining refresh rate: "+candidate);

                if (candidate > bestRefreshRate) {
                    // Ensure the frame rate stays around 60 Hz for <= 60 FPS streams
                    if (prefConfig.fps <= 60) {
                        if (candidate >= 63) {
                            continue;
                        }
                    }

                    bestRefreshRate = candidate;
                }
            }

            LimeLog.info("Selected refresh rate: "+bestRefreshRate);
            windowLayoutParams.preferredRefreshRate = bestRefreshRate;
            displayRefreshRate = bestRefreshRate;

            // Apply the refresh rate change
            getWindow().setAttributes(windowLayoutParams);
        }

        // Until Marshmallow, we can't ask for a 4K display mode, so we'll
        // need to hint the OS to provide one.
        boolean aspectRatioMatch = false;
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.M) {
            // We'll calculate whether we need to scale by aspect ratio. If not, we'll use
            // setFixedSize so we can handle 4K properly. The only known devices that have
            // >= 4K screens have exactly 4K screens, so we'll be able to hit this good path
            // on these devices. On Marshmallow, we can start changing to 4K manually but no
            // 4K devices run 6.0 at the moment.
            Point screenSize = new Point(0, 0);
            currentDisplay.getSize(screenSize);

            double screenAspectRatio = ((double)screenSize.y) / screenSize.x;
            double streamAspectRatio = ((double)displayHeight) / displayWidth;
            if (Math.abs(screenAspectRatio - streamAspectRatio) < 0.001|| isOnExternalDisplay()) {
                LimeLog.info("Stream has compatible aspect ratio with output display");
                aspectRatioMatch = true;
            }
        }

        // Don't do setFixedSize since it might not update the view dimensions correctly when entering PiP mode
        if (!(prefConfig.videoScaleMode == PreferenceConfiguration.ScaleMode.STRETCH || aspectRatioMatch)) {
            // Set the surface to scale based on the aspect ratio of the stream
            streamView.setDesiredAspectRatio((double)displayWidth / (double)displayHeight);
            streamView.setFillDisplay(prefConfig.videoScaleMode == PreferenceConfiguration.ScaleMode.FILL);
            LimeLog.info("surfaceChanged-->"+(double)displayWidth / (double)displayHeight);
            LimeLog.info("scaleMode-->"+prefConfig.videoScaleMode);
        }

        // Set the desired refresh rate that will get passed into setFrameRate() later
        desiredRefreshRate = displayRefreshRate;

        if (getPackageManager().hasSystemFeature(PackageManager.FEATURE_TELEVISION) ||
                getPackageManager().hasSystemFeature(PackageManager.FEATURE_LEANBACK)
            || isOnExternalDisplay()) {// TVs may take a few moments to switch refresh rates, and we can probably assume
            // it will be eventually activated.
            // external displays cant be compared with displaymanager currents display refreshrate
            // TODO: Improve this
            clientRefreshRate = displayRefreshRate;
            return displayRefreshRate;
        }
        else {
            // Use the lower of the current refresh rate and the selected refresh rate.
            // The preferred refresh rate may not actually be applied (ex: Battery Saver mode).
            float effectiveRefreshRate = Math.min(currentDisplay.getRefreshRate(), displayRefreshRate);
            // Android may continue reporting the old active mode for a short
            // interval after preferredDisplayModeId is accepted. A managed
            // tablet has an exact physical-mode match here, so negotiate
            // against the requested mode while keeping the current rate in
            // diagnostics until the system reports the switch.
            if (isHubManagedSession() && HubDisplayModePolicy.applies(true, onExternelDisplay) &&
                    hubPanelModeMatched) {
                clientRefreshRate = effectiveRefreshRate;
                LimeLog.info("Using requested Hub panel mode rate " + displayRefreshRate +
                        " Hz for stream negotiation; client reports current " + clientRefreshRate +
                        " Hz until the system reports the switch");
                return displayRefreshRate;
            }
            clientRefreshRate = effectiveRefreshRate;
            return effectiveRefreshRate;
        }
    }

    @SuppressLint("InlinedApi")
    private final Runnable hideSystemUi = new Runnable() {
        @Override
        public void run() {
            // TODO: Do we want to use WindowInsetsController here on R+ instead of
            // SYSTEM_UI_FLAG_IMMERSIVE_STICKY? They seem to do the same thing as of S...

            // In multi-window mode on N+, we need to drop our layout flags or we'll
            // be drawing underneath the system UI.
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N && isInMultiWindowMode()) {
                Game.this.getWindow().getDecorView().setSystemUiVisibility(
                        View.SYSTEM_UI_FLAG_LAYOUT_STABLE);
            }
            else {
                // Use immersive mode
                Game.this.getWindow().getDecorView().setSystemUiVisibility(
                        View.SYSTEM_UI_FLAG_LAYOUT_STABLE |
                                View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION |
                                View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN |
                                View.SYSTEM_UI_FLAG_HIDE_NAVIGATION |
                                View.SYSTEM_UI_FLAG_FULLSCREEN |
                                View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY);
            }
            if (isHubManagedSession()) reapplyHubDisplayMode("immersive-ui");
        }
    };

    private void hideSystemUi(int delay) {
        Handler h = getWindow().getDecorView().getHandler();
        if (h != null) {
            h.removeCallbacks(hideSystemUi);
            h.postDelayed(hideSystemUi, delay);
        }
    }

    @Override
    @TargetApi(Build.VERSION_CODES.N)
    public void onMultiWindowModeChanged(boolean isInMultiWindowMode) {
        super.onMultiWindowModeChanged(isInMultiWindowMode);

        // In multi-window, we don't want to use the full-screen layout
        // flag. It will cause us to collide with the system UI.
        // This function will also be called for PiP so we can cover
        // that case here too.
        if (isInMultiWindowMode) {
            getWindow().clearFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN);
            decoderRenderer.notifyVideoBackground();
        }
        else {
            getWindow().addFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN);
            decoderRenderer.notifyVideoForeground();
        }

        // Correct the system UI visibility flags
        hideSystemUi(50);
    }

    @Override
    protected void onDestroy() {
        super.onDestroy();
        if (hubSessionLease != null && !connectionStopRequested) {
            if (conn != null && attemptedConnection) stopConnection();
            else new Thread(hubSessionLease::end, "Hub-EndUnusedLease").start();
        }
        if (hubRotationMonitor != null) hubRotationMonitor.close();
        if (hubLiveControls != null) hubLiveControls.close();
        if (hubDesktopInput != null) hubDesktopInput.close();
        if (streamHubInputToolbar != null) streamHubInputToolbar.dispose();
        commitTextHandler.removeCallbacks(flushCommitTextQueue);
        commitTextQueue.clear();

        instance = null;

        if (prefConfig != null && prefConfig.enableFullExDisplay) handleDisplayRemoved();

        if (controllerHandler != null) {
            controllerHandler.destroy();
        }
        if (keyboardTranslator != null) {
            InputManager inputManager = (InputManager) getSystemService(Context.INPUT_SERVICE);
            inputManager.unregisterInputDeviceListener(keyboardTranslator);
        }

        if (lowLatencyWifiLock != null) {
            lowLatencyWifiLock.release();
        }
        if (highPerfWifiLock != null) {
            highPerfWifiLock.release();
        }

        // Save zoom/pan before other cleanup
        if (prefConfig != null && prefConfig.rememberZoomPan && panZoomHandler != null) {
            SharedPreferences basePrefs = PreferenceManager.getDefaultSharedPreferences(this);
            basePrefs.edit()
                    .putFloat("number_zoom_scale", panZoomHandler.getScaleFactor())
                    .putFloat("number_pan_offset_x", panZoomHandler.getChildX())
                    .putFloat("number_pan_offset_y", panZoomHandler.getChildY())
                    .apply();
        }

        if (connectedToUsbDriverService) {
            // Unbind from the discovery service
            unbindService(usbDriverServiceConnection);
        }

        // Destroy the capture provider
        if (inputCaptureProvider != null) inputCaptureProvider.destroy();
    }

    @Override
    protected void onPause() {
        if (isFinishing()) {
            // Stop any further input device notifications before we lose focus (and pointer capture)
            if (controllerHandler != null) {
                controllerHandler.stop();
            }

            // Ungrab input to prevent further input device notifications
            setInputGrabState(false);
        }

        super.onPause();
    }

    @Override
    protected void onStop() {
        super.onStop();
        if (hubRotationMonitor != null) hubRotationMonitor.close();
        if (getIntent().getBooleanExtra("HubManaged", false) && !hubRotationReconnect) quitOnStop = true;

        SpinnerDialog.closeDialogs(this);
        Dialog.closeDialogs();

        if (virtualController != null) {
            virtualController.hide();
        }
        if (keyBoardController != null) {
            keyBoardController.hide();
        }

        if(keyBoardLayoutController!=null){
            keyBoardLayoutController.hide();
        }

        if (conn != null) {
            displayedFailureDialog = true;
            stopConnection();
        }
        if (hubSessionLease != null && !connectionStopRequested) new Thread(hubSessionLease::end, "Hub-EndLease").start();
        if (conn != null && decoderRenderer != null && prefConfig != null) {
            int videoFormat = decoderRenderer.getActiveVideoFormat();
            String message = null;
            String selectedVideoFormat = "";

            int averageEndToEndLat = decoderRenderer.getAverageEndToEndLatency();
            int averageDecoderLat = decoderRenderer.getAverageDecoderLatency();

            if (averageEndToEndLat > 0) {
                message = getResources().getString(R.string.conn_client_latency) + " " + averageEndToEndLat + " ms";
                if (averageDecoderLat > 0) {
                    message += " (" + getResources().getString(R.string.conn_client_latency_hw) + " " + averageDecoderLat + " ms)";
                }
            } else if (averageDecoderLat > 0) {
                message = getResources().getString(R.string.conn_hardware_latency) + " " + averageDecoderLat + " ms";
            }

            // Add the video codec to the post-stream toast
            selectedVideoFormat += " [";

            if ((videoFormat & MoonBridge.VIDEO_FORMAT_MASK_H264) != 0) {
                selectedVideoFormat += "H.264";
            } else if ((videoFormat & MoonBridge.VIDEO_FORMAT_MASK_H265) != 0) {
                selectedVideoFormat += "HEVC";
            } else if ((videoFormat & MoonBridge.VIDEO_FORMAT_MASK_AV1) != 0) {
                selectedVideoFormat += "AV1";
            }
            else {
                selectedVideoFormat += "UNKNOWN";
            }

            if ((videoFormat & MoonBridge.VIDEO_FORMAT_MASK_10BIT) != 0) {
                selectedVideoFormat += " HDR";
            }

            selectedVideoFormat += "]";

            if (message != null) {
                message += selectedVideoFormat;
            }

            if (message != null) {
                if (prefConfig.enableLatencyToast) {
                    Toast.makeText(this, message, Toast.LENGTH_LONG).show();
                }
            }

            // Clear the tombstone count if we terminated normally
            if (!reportedCrash && tombstonePrefs != null && tombstonePrefs.getInt("CrashCount", 0) != 0) {
                tombstonePrefs.edit()
                        .putInt("CrashCount", 0)
                        .putInt("LastNotifiedCrashCount", 0)
                        .apply();
            }
            if(prefConfig.enablePerfLogging && decoderRenderer.performanceWasTracked()) {
                new PerformanceDataTracker().savePerformanceStatistics(
                        getBaseContext(),
                        Build.MODEL,
                        Build.VERSION.SDK_INT + "",
                        BuildConfig.VERSION_NAME,
                        selectedVideoFormat,
                        decoderRenderer.getMinDecoderLatency(),
                        decoderRenderer.getMinDecoderLatencyFullLog(),
                        String.valueOf((prefConfig.bitrate / 1000)),
                        displayWidth + "x" + displayHeight,
                        prefConfig.fps + " hz",
                        decoderRenderer.getAverageDecoderLatency() + " ms",
                        PreferenceConfiguration.getSelectedFramePacingName(getBaseContext()),
                        formatCurrentTime(System.currentTimeMillis())
                );
            }

        }

        finish();
    }

    public static String formatCurrentTime(long currentTimeMillis) {
        SimpleDateFormat dateFormat = new SimpleDateFormat("yyyy-MM-dd HH:mm");
        Date date = new Date(currentTimeMillis);
        return dateFormat.format(date);
    }

    private void setInputGrabState(boolean grab) {
        // Grab/ungrab the mouse cursor
        if (inputCaptureProvider != null && grab) {
            inputCaptureProvider.enableCapture();

            // Enabling capture may hide the cursor again, so
            // we will need to show it again.
            if (cursorVisible) {
                inputCaptureProvider.showCursor();
            }
        }
        else if (inputCaptureProvider != null) {
            inputCaptureProvider.disableCapture();
        }

        // Grab/ungrab system keyboard shortcuts
        setMetaKeyCaptureState(grab);

        grabbedInput = grab;
    }

    private final Runnable toggleGrab = new Runnable() {
        @Override
        public void run() {
            setInputGrabState(!grabbedInput);
        }
    };

    // Returns true if the key stroke was consumed
    private boolean handleSpecialKeys(int androidKeyCode, boolean down) {
        int modifierMask = 0;
        int nonModifierKeyCode = KeyEvent.KEYCODE_UNKNOWN;

        if (androidKeyCode == KeyEvent.KEYCODE_CTRL_LEFT ||
                androidKeyCode == KeyEvent.KEYCODE_CTRL_RIGHT) {
            modifierMask = KeyboardPacket.MODIFIER_CTRL;
        }
        else if (androidKeyCode == KeyEvent.KEYCODE_SHIFT_LEFT ||
                androidKeyCode == KeyEvent.KEYCODE_SHIFT_RIGHT) {
            modifierMask = KeyboardPacket.MODIFIER_SHIFT;
        }
        else if (androidKeyCode == KeyEvent.KEYCODE_ALT_LEFT ||
                androidKeyCode == KeyEvent.KEYCODE_ALT_RIGHT) {
            modifierMask = KeyboardPacket.MODIFIER_ALT;
        }
        else if (androidKeyCode == KeyEvent.KEYCODE_META_LEFT ||
                androidKeyCode == KeyEvent.KEYCODE_META_RIGHT) {
            modifierMask = KeyboardPacket.MODIFIER_META;
        }
        else {
            nonModifierKeyCode = androidKeyCode;
        }

        if (down) {
            this.modifierFlags |= modifierMask;
        }
        else {
            this.modifierFlags &= ~modifierMask;
        }

        // Handle the special combos on the key up
        if (waitingForAllModifiersUp || specialKeyCode != KeyEvent.KEYCODE_UNKNOWN) {
            if (specialKeyCode == androidKeyCode) {
                // If this is a key up for the special key itself, eat that because the host never saw the original key down
                return true;
            }
            else if (modifierFlags != 0) {
                // While we're waiting for modifiers to come up, eat all key downs and allow all key ups to pass
                return down;
            }
            else {
                // When all modifiers are up, perform the special action
                switch (specialKeyCode) {
                    // Toggle input grab
                    case KeyEvent.KEYCODE_Z:
                        Handler h = getWindow().getDecorView().getHandler();
                        if (h != null) {
                            h.postDelayed(toggleGrab, 250);
                        }
                        break;

                    // Quit
                    case KeyEvent.KEYCODE_Q:
                        finish();
                        break;

                    // Toggle cursor visibility
                    case KeyEvent.KEYCODE_C:
                        if (!grabbedInput) {
                            inputCaptureProvider.enableCapture();
                            grabbedInput = true;
                        }
                        cursorVisible = !cursorVisible;
                        if (cursorVisible) {
                            inputCaptureProvider.showCursor();
                        } else {
                            inputCaptureProvider.hideCursor();
                        }
                        break;

                    default:
                        break;
                }

                // Reset special key state
                specialKeyCode = KeyEvent.KEYCODE_UNKNOWN;
                waitingForAllModifiersUp = false;
            }
        }
        // Check if Ctrl+Alt+Shift is down when a non-modifier key is pressed
        else if ((modifierFlags & (KeyboardPacket.MODIFIER_CTRL | KeyboardPacket.MODIFIER_ALT | KeyboardPacket.MODIFIER_SHIFT)) ==
                (KeyboardPacket.MODIFIER_CTRL | KeyboardPacket.MODIFIER_ALT | KeyboardPacket.MODIFIER_SHIFT) &&
                (down && nonModifierKeyCode != KeyEvent.KEYCODE_UNKNOWN)) {
            switch (androidKeyCode) {
                case KeyEvent.KEYCODE_Z:
                case KeyEvent.KEYCODE_Q:
                case KeyEvent.KEYCODE_C:
                    // Remember that a special key combo was activated, so we can consume all key
                    // events until the modifiers come up
                    specialKeyCode = androidKeyCode;
                    waitingForAllModifiersUp = true;
                    return true;

                default:
                    // This isn't a special combo that we consume on the client side
                    return false;
            }
        }

        // Not a special combo
        return false;
    }

    // We cannot simply use modifierFlags for all key event processing, because
    // some IMEs will not generate real key events for pressing Shift. Instead
    // they will simply send key events with isShiftPressed() returning true,
    // and we will need to send the modifier flag ourselves.
    private byte getModifierState(KeyEvent event) {
        // Start with the global modifier state to ensure we cover the case
        // detailed in https://github.com/moonlight-stream/moonlight-android/issues/840
        byte modifier = getModifierState();
        if (event.isShiftPressed()) {
            modifier |= KeyboardPacket.MODIFIER_SHIFT;
        }
        if (event.isCtrlPressed()) {
            modifier |= KeyboardPacket.MODIFIER_CTRL;
        }
        if (event.isAltPressed()) {
            modifier |= KeyboardPacket.MODIFIER_ALT;
        }
        if (event.isMetaPressed()) {
            modifier |= KeyboardPacket.MODIFIER_META;
        }
        return modifier;
    }

    private byte getModifierState() {
        return (byte) modifierFlags;
    }

    @Override
    public boolean onKeyDown(int keyCode, KeyEvent event) {
        return handleKeyDown(event) || super.onKeyDown(keyCode, event);
    }

    @Override
    public boolean handleKeyDown(KeyEvent event) {
        // Pass-through virtual navigation keys
        if ((event.getFlags() & KeyEvent.FLAG_VIRTUAL_HARD_KEY) != 0) {
            return false;
        }

        int deviceId = event.getDeviceId();
        if (prefConfig.ignoreSynthEvents && deviceId <= 0) {
            return false;
        }

        // Handle a synthetic back button event that some Android OS versions
        // create as a result of a right-click. This event WILL repeat if
        // the right mouse button is held down, so we ignore those.
        int eventSource = event.getSource();
        if ((eventSource == InputDevice.SOURCE_MOUSE ||
                eventSource == InputDevice.SOURCE_MOUSE_RELATIVE) &&
                event.getKeyCode() == KeyEvent.KEYCODE_BACK) {

            // Send the right mouse button event if mouse back and forward
            // are disabled. If they are enabled, handleMotionEvent() will take
            // care of this.
            if (!prefConfig.mouseNavButtons) {
                conn.sendMouseButtonDown(MouseButtonPacket.BUTTON_RIGHT);
            }

            // Always return true, otherwise the back press will be propagated
            // up to the parent and finish the activity.
            return true;
        }

        boolean handled = false;

        if (ControllerHandler.isGameControllerDevice(event.getDevice())) {
            // Always try the controller handler first, unless it's an alphanumeric keyboard device.
            // Otherwise, controller handler will eat keyboard d-pad events.
            handled = controllerHandler.handleButtonDown(event);
        }

        // Try the keyboard handler if it wasn't handled as a game controller
        if (!handled) {
            // Let this method take duplicate key down events
            if (handleSpecialKeys(event.getKeyCode(), true)) {
                return true;
            }

            // Pass through keyboard input if we're not grabbing
            if (!grabbedInput) {
                return false;
            }

            // We'll send it as a raw key event if we have a key mapping, otherwise we'll send it
            // as UTF-8 text (if it's a printable character).
            short translated = keyboardTranslator.translate(event.getKeyCode(), event.getScanCode(), deviceId);
            if (translated == 0) {
                if (prefConfig.backAsMeta && event.getKeyCode() == KeyEvent.KEYCODE_BACK) {
                    translated = 0x5b; // Meta key
                } else {
                    // Make sure it has a valid Unicode representation and it's not a dead character
                    // (which we don't support). If those are true, we can send it as UTF-8 text.
                    //
                    // NB: We need to be sure this happens before the getRepeatCount() check because
                    // UTF-8 events don't auto-repeat on the host side.
                    int unicodeChar = event.getUnicodeChar();
                    if ((unicodeChar & KeyCharacterMap.COMBINING_ACCENT) == 0 && (unicodeChar & KeyCharacterMap.COMBINING_ACCENT_MASK) != 0) {
                        conn.sendUtf8Text(""+(char)unicodeChar);
                        return true;
                    }

                    return false;
                }
            }

            // Eat repeat down events
            if (event.getRepeatCount() > 0) {
                return true;
            }

            conn.sendKeyboardInput(translated, KeyboardPacket.KEY_DOWN, getModifierState(event),
                    keyboardTranslator.hasNormalizedMapping(event.getKeyCode(), deviceId) ? 0 : MoonBridge.SS_KBE_FLAG_NON_NORMALIZED);
        }

        return true;
    }

    @Override
    public boolean onKeyUp(int keyCode, KeyEvent event) {
        return handleKeyUp(event) || super.onKeyUp(keyCode, event);
    }

    @Override
    public boolean handleKeyUp(KeyEvent event) {
        // Pass-through virtual navigation keys
        if ((event.getFlags() & KeyEvent.FLAG_VIRTUAL_HARD_KEY) != 0) {
            return false;
        }

        int deviceId = event.getDeviceId();
        if (prefConfig.ignoreSynthEvents && deviceId <= 0) {
            return false;
        }

        // Handle a synthetic back button event that some Android OS versions
        // create as a result of a right-click.
        int eventSource = event.getSource();
        if ((eventSource == InputDevice.SOURCE_MOUSE ||
                eventSource == InputDevice.SOURCE_MOUSE_RELATIVE) &&
                event.getKeyCode() == KeyEvent.KEYCODE_BACK) {

            // Send the right mouse button event if mouse back and forward
            // are disabled. If they are enabled, handleMotionEvent() will take
            // care of this.
            if (!prefConfig.mouseNavButtons) {
                conn.sendMouseButtonUp(MouseButtonPacket.BUTTON_RIGHT);
            }

            // Always return true, otherwise the back press will be propagated
            // up to the parent and finish the activity.
            return true;
        }

        boolean handled = false;
        if (ControllerHandler.isGameControllerDevice(event.getDevice())) {
            // Always try the controller handler first, unless it's an alphanumeric keyboard device.
            // Otherwise, controller handler will eat keyboard d-pad events.
            handled = controllerHandler.handleButtonUp(event);
        }

        // Try the keyboard handler if it wasn't handled as a game controller
        if (!handled) {
            if (handleSpecialKeys(event.getKeyCode(), false)) {
                return true;
            }

            // Pass through keyboard input if we're not grabbing
            if (!grabbedInput) {
                return false;
            }

            short translated = keyboardTranslator.translate(event.getKeyCode(), event.getScanCode(), deviceId);
            if (translated == 0) {
                if (prefConfig.backAsMeta && event.getKeyCode() == KeyEvent.KEYCODE_BACK) {
                    translated = 0x5b; // Meta key
                } else {
                    // If we sent this event as UTF-8 on key down, also report that it was handled
                    // when we get the key up event for it.
                    int unicodeChar = event.getUnicodeChar();
                    return (unicodeChar & KeyCharacterMap.COMBINING_ACCENT) == 0 && (unicodeChar & KeyCharacterMap.COMBINING_ACCENT_MASK) != 0;
                }
            }

            conn.sendKeyboardInput(translated, KeyboardPacket.KEY_UP, getModifierState(event),
                    keyboardTranslator.hasNormalizedMapping(event.getKeyCode(), deviceId) ? 0 : MoonBridge.SS_KBE_FLAG_NON_NORMALIZED);
        }

        return true;
    }

    @Override
    public boolean onKeyMultiple(int keyCode, int repeatCount, KeyEvent event) {
        return handleKeyMultiple(event) || super.onKeyMultiple(keyCode, repeatCount, event);
    }

    public boolean handleKeyMultiple(KeyEvent event) {
        // We can receive keys from a software keyboard that don't correspond to any existing
        // KEYCODE value. Android will give those to us as an ACTION_MULTIPLE KeyEvent.
        //
        // Despite the fact that the Android docs say this is unused since API level 29, these
        // events are still sent as of Android 13 for the above case.
        //
        // For other cases of ACTION_MULTIPLE, we will not report those as handled so hopefully
        // they will be passed to us again as regular singular key events.
        if (event.getKeyCode() != KeyEvent.KEYCODE_UNKNOWN || event.getCharacters() == null) {
            return false;
        }

        conn.sendUtf8Text(event.getCharacters());
        return true;
    }

    public void sendKeys(short[] keys) {
        if (conn == null || !connected) return;
        final NvConnection keyConnection = conn;
        final byte[] modifier = {(byte) 0};

        for (short key : keys) {
            keyConnection.sendKeyboardInput(key, KeyboardPacket.KEY_DOWN, modifier[0], (byte) 0);

            // Apply the modifier of the pressed key, e.g. CTRL first issues a CTRL event (without
            // modifier) and then sends the following keys with the CTRL modifier applied
            modifier[0] |= getModifier(key);
        }

        new Handler().postDelayed((() -> {
            for (int pos = keys.length - 1; pos >= 0; pos--) {
                short key = keys[pos];

                // Remove the keys modifier before releasing the key
                modifier[0] &= (byte) ~getModifier(key);

                keyConnection.sendKeyboardInput(key, KeyboardPacket.KEY_UP, modifier[0], (byte) 0);
            }
        }), GameMenu.KEY_UP_DELAY);
    }

    public boolean handleFocusChange(boolean hasFocus) {
        // The product control API requires an explicit button action. Keep the
        // upstream automatic clipboard behavior for ordinary Moonlight sessions,
        // while avoiding background clipboard reads in a managed session.
        if (connected && prefConfig.smartClipboardSync && !isHubManagedSession()) {
            if (hasFocus) {
                return sendClipboard(false);
            } else {
                return getClipboard(0);
            }
        }

        return false;
    }

    // Method to get clipboard content
    private String getClipboardContent(boolean force) {
        // Check if there is any clipboard data
        if (clipboardManager.hasPrimaryClip()) {
            ClipDescription clipDescription = clipboardManager.getPrimaryClipDescription();
            if (!force && clipDescription != null) {
                if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.N) {
                    PersistableBundle extras = clipDescription.getExtras();
                    if (extras != null && extras.getBoolean(CLIPBOARD_IDENTIFIER)) {
                        // We're getting the clipboard data we just set/read a while ago
                        return null;
                    }
                } else {
                    CharSequence clipLabel = clipDescription.getLabel();
                    if (clipLabel != null && clipLabel.equals(CLIPBOARD_IDENTIFIER)) {
                        // We're getting the clipboard data we set a while ago
                        return null;
                    }
                }
            }

            ClipData clipData = clipboardManager.getPrimaryClip();

            if (clipData != null && clipData.getItemCount() > 0) {
                // Get the first item from the clipboard data
                ClipData.Item item = clipData.getItemAt(0);

                // Mark the clip as visited
                if (clipDescription != null) {
                    ClipData clonedClip = cloneClipData(clipDescription, item);
                    clipboardManager.setPrimaryClip(clonedClip);
                }

                // Get the text data from the clipboard item
                CharSequence clipText = item.getText();
                if (clipText == null) {
                    return  null;
                }
                return clipText.toString();
            }
        }

        return null;
    }

    private static @NonNull ClipData cloneClipData(ClipDescription clipDescription, ClipData.Item item) {
        ClipDescription clonedDescription = new ClipDescription(clipDescription);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
            PersistableBundle extras = clipDescription.getExtras();
            if (extras == null) {
                extras = new PersistableBundle();
            }
            extras.putBoolean(CLIPBOARD_IDENTIFIER, true);
            clonedDescription.setExtras(extras);
        }

        return new ClipData(clonedDescription, item);
    }

    public boolean sendClipboard(boolean force) {
        if (isHubManagedSession()) return sendHubClipboard(force);
        if (httpConn == null) {
            LimeLog.warning("httpConn not ready, cannot send clipboard!");
            return false;
        }

        String clipboardText = getClipboardContent(force);
        if (clipboardText != null) {
            new Thread() {
                public void run() {
                    try {
                        if (!httpConn.sendClipboard(clipboardText)) {
                            if (prefConfig.smartClipboardSyncToast) {
                                Game.this.runOnUiThread(() -> Toast.makeText(Game.this, getString(R.string.clipboard_sync_unsupported), Toast.LENGTH_SHORT).show());
                            }
                        } else {
                            if (prefConfig.smartClipboardSyncToast) {
                                Game.this.runOnUiThread(() -> Toast.makeText(Game.this, getString(R.string.send_clipboard_success), Toast.LENGTH_SHORT).show());
                            }
                        }
                    } catch (Exception e) {
                        e.printStackTrace();
                        if (prefConfig.smartClipboardSyncToast) {
                            Game.this.runOnUiThread(() -> Toast.makeText(Game.this, getString(R.string.send_clipboard_failed) + e.getMessage(), Toast.LENGTH_SHORT).show());
                        }
                    }
                }
            }.start();

            return true;
        }

        return false;
    }

    public boolean getClipboard(int delay) {
        if (isHubManagedSession()) return getHubClipboard(delay);
        if (httpConn == null) {
            LimeLog.warning("httpConn not ready, cannot get clipboard!");
            return false;
        }

        if (delay == 0 && gameMenuCallbacks != null && gameMenuCallbacks.isMenuOpen()) {
            return false;
        }

        new Thread() {
            public void run() {
                if (clipboardSyncRunning) {
                    return;
                }

                clipboardSyncRunning = true;
                try {
                    if (delay > 0) {
                        sleep(delay);
                    }
                    String clipboardContent = httpConn.getClipboard();
                    ClipData clipData = ClipData.newPlainText(CLIPBOARD_IDENTIFIER, clipboardContent);

                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
                        ClipDescription clipDescription = clipData.getDescription();
                        PersistableBundle newExtras = new PersistableBundle();
                        newExtras.putBoolean(CLIPBOARD_IDENTIFIER, true);
                        if (prefConfig.hideClipboardContent) {
                            // We don't know if the message is sensitive or not, to be safe mark them all as sensitive.
                            newExtras.putBoolean("android.content.extra.IS_SENSITIVE", true);
                        }
                        clipDescription.setExtras(newExtras);
                    }

                    clipboardManager.setPrimaryClip(clipData);
                    if (prefConfig.smartClipboardSyncToast) {
                        Game.this.runOnUiThread(() -> Toast.makeText(Game.this, getString(R.string.get_clipboard_success), Toast.LENGTH_SHORT).show());
                    }
                } catch (Exception e) {
                    e.printStackTrace();
                    if (prefConfig.smartClipboardSyncToast) {
                        Game.this.runOnUiThread(() -> Toast.makeText(Game.this, getString(R.string.get_clipboard_failed) + e.getMessage(), Toast.LENGTH_SHORT).show());
                    }
                }
                clipboardSyncRunning = false;
            }
        }.start();

        return true;
    }

    private boolean beginHubClipboard() {
        synchronized (this) {
            if (clipboardSyncRunning) return false;
            clipboardSyncRunning = true;
            return true;
        }
    }

    private void finishHubClipboard() {
        synchronized (this) { clipboardSyncRunning = false; }
    }

    private String hubLeaseId() {
        String value = getIntent().getStringExtra("HubLeaseId");
        return value == null ? "" : value;
    }

    /** Send or fetch text through the product API, bound to this live lease. */
    private boolean sendHubClipboard(boolean force) {
        if (!connected) { Toast.makeText(this, "请先连接电脑", Toast.LENGTH_SHORT).show(); return false; }
        if (!beginHubClipboard()) return false;
        ClipData clip = clipboardManager.getPrimaryClip();
        if (clip == null || clip.getItemCount() == 0) {
            finishHubClipboard();
            Toast.makeText(this, "当前剪贴板没有可发送的内容", Toast.LENGTH_SHORT).show();
            return false;
        }
        new Thread(() -> requestHubClipboard("set", clip), "streamhub-clipboard").start();
        return true;
    }

    private boolean getHubClipboard(int delay) {
        if (!connected) { Toast.makeText(this, "请先连接电脑", Toast.LENGTH_SHORT).show(); return false; }
        if (!beginHubClipboard()) return false;
        Toast.makeText(this, "正在读取电脑剪贴板…", Toast.LENGTH_SHORT).show();
        new Thread(() -> {
            try {
                if (delay > 0) Thread.sleep(delay);
                requestHubClipboard("get", null);
            } catch (InterruptedException ignored) {
                Thread.currentThread().interrupt();
                finishHubClipboard();
            }
        }, "streamhub-clipboard").start();
        return true;
    }

    private void requestHubClipboard(String action, ClipData source) {
        HubControlClient control = null;
        try {
            String lease = hubLeaseId();
            if (host == null || host.isEmpty() || lease.isEmpty() || streamHubMode == null)
                throw new java.io.IOException("当前连接没有有效会话，无法访问电脑剪贴板。");
            HubStore store = new HubStore(this);
            control = new HubControlClient(host, store.invitation());
            org.json.JSONObject payload = source == null ? null : HubClipboard.capture(this, source);
            org.json.JSONObject result = control.clipboard(streamHubMode, lease, store.clientId(), action, payload);
            if ("get".equals(action)) {
                final ClipData clip = HubClipboard.restore(this, result.optJSONObject("payload"));
                runOnUiThread(() -> {
                    if (isFinishing() || isDestroyed() || !connected) return;
                    HubClipboardPreview.show(this,clip,prefConfig.hideClipboardContent);
                });
            } else {
                runOnUiThread(() -> Toast.makeText(this, "剪贴板已发送到电脑", Toast.LENGTH_SHORT).show());
            }
        } catch (Exception error) {
            String message = error.getMessage();
            runOnUiThread(() -> {
                if(isFinishing() || isDestroyed() || !connected)return;
                String detail=message==null?"电脑暂未返回可用内容。":message;
                if("get".equals(action))HubClipboardPreview.showFailure(this,detail,()->getHubClipboard(0));
                else Toast.makeText(this,"剪贴板操作失败："+detail,Toast.LENGTH_LONG).show();
            });
        } finally {
            if (control != null) control.cancel();
            finishHubClipboard();
        }
    }

    private void releaseHubTouches() {
        hubGestureActive=false;
        if(connected && conn!=null) {
            conn.sendTouchEvent(MoonBridge.LI_TOUCH_EVENT_CANCEL_ALL,0,0,0,0,0,0,MoonBridge.LI_ROT_UNKNOWN);
            for(TouchContext touch:touchContextMap)if(touch!=null)touch.cancelTouch();
        }
    }
    private void showHubTouchModes() {
        releaseHubTouches();
        String[] choices={"桌面兼容触控（锁屏自动适配）","原生多点触控（标题栏自动适配）","触控板鼠标"};
        boolean compat=getSharedPreferences("streamhub_controls",MODE_PRIVATE).getBoolean("touch_compatibility",true);
        int selected=prefConfig.touchscreenTrackpad?2:compat?0:1;
        HubStreamUi.dialog(this).setTitle("触控与鼠标模式").setSingleChoiceItems(choices,selected,(d,index)->{
            releaseHubTouches();
            getSharedPreferences("streamhub_controls",MODE_PRIVATE).edit().putBoolean("touch_compatibility",index==0).putInt("input_mode",index).apply();
            applyMouseMode(index==1?0:index==2?2:1);
            d.dismiss();
        }).setNegativeButton("取消",null).show();
    }
    private TouchContext getTouchContext(int actionIndex, TouchContext[] inputContextMap)
    {
        if (actionIndex < inputContextMap.length) {
            return inputContextMap[actionIndex];
        }
        else {
            return null;
        }
    }

    @Override
    public void toggleKeyboard() {
        if (isOnExternalDisplay()) {
            ExternalDisplayControlActivity.toggleKeyboard();
        } else {
            LimeLog.info("Toggling keyboard overlay");
            InputMethodManager inputManager = (InputMethodManager) getSystemService(Context.INPUT_METHOD_SERVICE);
            if (streamHubMode != null) {
                streamView.setCommitTextEnabled(true);
                streamView.requestFocus();
                Rect visibleFrame = new Rect();
                streamView.getWindowVisibleDisplayFrame(visibleFrame);
                boolean keyboardVisible = streamView.getRootView().getHeight() - visibleFrame.bottom > UiHelper.dpToPx(this, 120);
                if (keyboardVisible) hideStreamKeyboard();
                else streamView.post(() -> {
                    if (isHubManagedSession() && (isFinishing() || isDestroyed() || !streamView.onCheckIsTextEditor())) return;
                    inputManager.restartInput(streamView);
                    inputManager.showSoftInput(streamView, InputMethodManager.SHOW_IMPLICIT);
                });
            } else inputManager.toggleSoftInput(0, 0);
        }
    }

    private void hideStreamKeyboard() {
        if (streamView == null) return;
        InputMethodManager inputManager = (InputMethodManager) getSystemService(Context.INPUT_METHOD_SERVICE);
        if (isHubManagedSession()) {
            streamView.setCommitTextEnabled(false);
            inputManager.restartInput(streamView);
        }
        inputManager.hideSoftInputFromWindow(streamView.getWindowToken(), 0);
    }

    /** Hide both the Android IME and the product's full on-screen keyboard. */
    private void hideAllStreamKeyboards() {
        hideStreamKeyboard();
        if (keyBoardController != null) keyBoardController.hide();
        if (keyBoardLayoutController != null) keyBoardLayoutController.hide();
        // Some tablet IMEs dispatch the toolbar tap before the full keyboard
        // finishes its own pointer-up event. Repeat after the event queue has
        // settled so the hide action is deterministic on the first tap.
        if (streamView != null) streamView.post(() -> {
            if (keyBoardController != null) keyBoardController.hide();
            if (keyBoardLayoutController != null) keyBoardLayoutController.hide();
        });
    }

    private byte getLiTouchTypeFromEvent(MotionEvent event) {
        switch (event.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
            case MotionEvent.ACTION_POINTER_DOWN:
                return MoonBridge.LI_TOUCH_EVENT_DOWN;

            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_POINTER_UP:
                if ((event.getFlags() & MotionEvent.FLAG_CANCELED) != 0) {
                    return MoonBridge.LI_TOUCH_EVENT_CANCEL;
                }
                else {
                    return MoonBridge.LI_TOUCH_EVENT_UP;
                }

            case MotionEvent.ACTION_MOVE:
                return MoonBridge.LI_TOUCH_EVENT_MOVE;

            case MotionEvent.ACTION_CANCEL:
                // ACTION_CANCEL applies to *all* pointers in the gesture, so it maps to CANCEL_ALL
                // rather than CANCEL. For a single pointer cancellation, that's indicated via
                // FLAG_CANCELED on a ACTION_POINTER_UP.
                // https://developer.android.com/develop/ui/views/touch-and-input/gestures/multi
                return MoonBridge.LI_TOUCH_EVENT_CANCEL_ALL;

            case MotionEvent.ACTION_HOVER_ENTER:
            case MotionEvent.ACTION_HOVER_MOVE:
                return MoonBridge.LI_TOUCH_EVENT_HOVER;

            case MotionEvent.ACTION_HOVER_EXIT:
                return MoonBridge.LI_TOUCH_EVENT_HOVER_LEAVE;

            case MotionEvent.ACTION_BUTTON_PRESS:
            case MotionEvent.ACTION_BUTTON_RELEASE:
                return MoonBridge.LI_TOUCH_EVENT_BUTTON_ONLY;

            default:
                return -1;
        }
    }

    //灵敏度保存到集合 适配多个手指
    private Map<String,SensitivityBean> sensitivityMap=new HashMap<>();

    //修改移动的触控灵敏度（通过修改移动的距离实现） 默认使用右半边屏幕的时候开启
    private float[] getStreamViewRelativeSensitivityXY(MotionEvent event,float normalizedX,float normalizedY,int pointerIndex){
        float[] normalized=new float[2];
        normalized[0]=normalizedX;
        normalized[1]=normalizedY;

        //如果不是全局模式 并且 坐标 不在右边 则返回
        if(!prefConfig.touchSensitivityGlobal&&normalizedX<getResources().getDisplayMetrics().widthPixels/2){
            return normalized;
        }
        if (event.getActionMasked() == MotionEvent.ACTION_MOVE) {
            SensitivityBean bean=sensitivityMap.get(String.valueOf(event.getPointerId(pointerIndex)));
            if(bean==null){
                bean=new SensitivityBean();
            }
            if(bean.getLastAbsoluteX() !=-1){
                float dx=normalizedX- bean.getLastAbsoluteX();
                float dy=normalizedY- bean.getLastAbsoluteY();
                dx*=0.01f*prefConfig.touchSensitivityX;//灵敏度
                dy*=0.01f*prefConfig.touchSensitivityY;
                normalizedX= bean.getLastRelativelyX() +dx;
                normalizedY= bean.getLastRelativelyY() +dy;
            }
            if(prefConfig.touchSensitivityRotationAuto){
                if(normalizedX>=streamView.getWidth()){
                    normalizedX=streamView.getWidth()/2.0f;
                }
                if(normalizedY>=streamView.getHeight()){
                    normalizedY=streamView.getHeight()/2.0f;
                }
            }
            bean.setLastAbsoluteX(event.getX(pointerIndex));
            bean.setLastAbsoluteY(event.getY(pointerIndex));
            bean.setLastRelativelyX(normalizedX);
            bean.setLastRelativelyY(normalizedY);
            sensitivityMap.put(String.valueOf(event.getPointerId(pointerIndex)),bean);
        }
        //抬起的时候，恢复初始化状态
        if (event.getActionMasked() == MotionEvent.ACTION_UP||event.getActionMasked() == MotionEvent.ACTION_POINTER_UP) {
            sensitivityMap.remove(String.valueOf(event.getPointerId(pointerIndex)));
        }
        normalized[0]=normalizedX;
        normalized[1]=normalizedY;
        return normalized;
    }


    private float[] getStreamViewRelativeNormalizedXY(View view, MotionEvent event, int pointerIndex) {
        float normalizedX = event.getX(pointerIndex);
        float normalizedY = event.getY(pointerIndex);
        //开启自定义修改触控灵敏度 并且 数值不为100
        if(prefConfig.enableTouchSensitivity&&(prefConfig.touchSensitivityX !=100||prefConfig.touchSensitivityY!=100)){
            float[] normalized=getStreamViewRelativeSensitivityXY(event,normalizedX,normalizedY,pointerIndex);
            normalizedX=normalized[0];
            normalizedY=normalized[1];
        }
        // For the containing background view, we must subtract the origin
        // of the StreamView to get video-relative coordinates.
        if (view != streamView) {
            float[] normalized = getNormalizedCoordinates(streamView, normalizedX, normalizedY);
            normalizedX = normalized[0];
            normalizedY = normalized[1];
        }

        normalizedX = Math.max(normalizedX, 0.0f);
        normalizedY = Math.max(normalizedY, 0.0f);

        normalizedX = Math.min(normalizedX, streamView.getWidth());
        normalizedY = Math.min(normalizedY, streamView.getHeight());

        normalizedX /= streamView.getWidth();
        normalizedY /= streamView.getHeight();

        return new float[] { normalizedX, normalizedY };
    }

    private float[] getNormalizedCoordinates(View streamView, float rawX, float rawY) {
        float scaleX = streamView.getScaleX();
        float scaleY = streamView.getScaleY();

        float normalizedX = (rawX - streamView.getX()) / scaleX;
        float normalizedY = (rawY - streamView.getY()) / scaleY;

        return new float[] { normalizedX, normalizedY };
    }

    private static float normalizeValueInRange(float value, InputDevice.MotionRange range) {
        return (value - range.getMin()) / range.getRange();
    }

    private static float getPressureOrDistance(MotionEvent event, int pointerIndex) {
        InputDevice dev = event.getDevice();
        switch (event.getActionMasked()) {
            case MotionEvent.ACTION_HOVER_ENTER:
            case MotionEvent.ACTION_HOVER_MOVE:
            case MotionEvent.ACTION_HOVER_EXIT:
                // Hover events report distance
                if (dev != null) {
                    InputDevice.MotionRange distanceRange = dev.getMotionRange(MotionEvent.AXIS_DISTANCE, event.getSource());
                    if (distanceRange != null) {
                        return normalizeValueInRange(event.getAxisValue(MotionEvent.AXIS_DISTANCE, pointerIndex), distanceRange);
                    }
                }
                return 0.0f;

            default:
                // Other events report pressure
                return event.getPressure(pointerIndex);
        }
    }

    private static short getRotationDegrees(MotionEvent event, int pointerIndex) {
        InputDevice dev = event.getDevice();
        if (dev != null) {
            if (dev.getMotionRange(MotionEvent.AXIS_ORIENTATION, event.getSource()) != null) {
                short rotationDegrees = (short) Math.toDegrees(event.getOrientation(pointerIndex));
                if (rotationDegrees < 0) {
                    rotationDegrees += 360;
                }
                return rotationDegrees;
            }
        }
        return MoonBridge.LI_ROT_UNKNOWN;
    }

    private static float[] polarToCartesian(float r, float theta) {
        return new float[] { (float)(r * Math.cos(theta)), (float)(r * Math.sin(theta)) };
    }

    private static float cartesianToR(float[] point) {
        return (float)Math.sqrt(Math.pow(point[0], 2) + Math.pow(point[1], 2));
    }

    private float[] getStreamViewNormalizedContactArea(MotionEvent event, int pointerIndex) {
        float orientation;

        // If the orientation is unknown, we'll just assume it's at a 45 degree angle and scale it by
        // X and Y scaling factors evenly.
        if (event.getDevice() == null || event.getDevice().getMotionRange(MotionEvent.AXIS_ORIENTATION, event.getSource()) == null) {
            orientation = (float)(Math.PI / 4);
        }
        else {
            orientation = event.getOrientation(pointerIndex);
        }

        float contactAreaMajor, contactAreaMinor;
        switch (event.getActionMasked()) {
            // Hover events report the tool size
            case MotionEvent.ACTION_HOVER_ENTER:
            case MotionEvent.ACTION_HOVER_MOVE:
            case MotionEvent.ACTION_HOVER_EXIT:
                contactAreaMajor = event.getToolMajor(pointerIndex);
                contactAreaMinor = event.getToolMinor(pointerIndex);
                break;

            // Other events report contact area
            default:
                contactAreaMajor = event.getTouchMajor(pointerIndex);
                contactAreaMinor = event.getTouchMinor(pointerIndex);
                break;
        }

        // The contact area major axis is parallel to the orientation, so we simply convert
        // polar to cartesian coordinates using the orientation as theta.
        float[] contactAreaMajorCartesian = polarToCartesian(contactAreaMajor, orientation);

        // The contact area minor axis is perpendicular to the contact area major axis (and thus
        // the orientation), so rotate the orientation angle by 90 degrees.
        float[] contactAreaMinorCartesian = polarToCartesian(contactAreaMinor, (float)(orientation + (Math.PI / 2)));

        // Normalize the contact area to the stream view size
        contactAreaMajorCartesian[0] = Math.min(Math.abs(contactAreaMajorCartesian[0]), streamView.getWidth()) / streamView.getWidth();
        contactAreaMinorCartesian[0] = Math.min(Math.abs(contactAreaMinorCartesian[0]), streamView.getWidth()) / streamView.getWidth();
        contactAreaMajorCartesian[1] = Math.min(Math.abs(contactAreaMajorCartesian[1]), streamView.getHeight()) / streamView.getHeight();
        contactAreaMinorCartesian[1] = Math.min(Math.abs(contactAreaMinorCartesian[1]), streamView.getHeight()) / streamView.getHeight();

        // Convert the normalized values back into polar coordinates
        return new float[] { cartesianToR(contactAreaMajorCartesian), cartesianToR(contactAreaMinorCartesian) };
    }

    private boolean sendPenEventForPointer(View view, MotionEvent event, byte eventType, byte toolType, int pointerIndex) {
        byte penButtons = 0;
        if ((event.getButtonState() & MotionEvent.BUTTON_STYLUS_PRIMARY) != 0) {
            penButtons |= MoonBridge.LI_PEN_BUTTON_PRIMARY;
        }
        if ((event.getButtonState() & MotionEvent.BUTTON_STYLUS_SECONDARY) != 0) {
            penButtons |= MoonBridge.LI_PEN_BUTTON_SECONDARY;
        }

        byte tiltDegrees = MoonBridge.LI_TILT_UNKNOWN;
        InputDevice dev = event.getDevice();
        if (dev != null) {
            if (dev.getMotionRange(MotionEvent.AXIS_TILT, event.getSource()) != null) {
                tiltDegrees = (byte)Math.toDegrees(event.getAxisValue(MotionEvent.AXIS_TILT, pointerIndex));
            }
        }

        float[] normalizedCoords = getStreamViewRelativeNormalizedXY(view, event, pointerIndex);
        float[] normalizedContactArea = getStreamViewNormalizedContactArea(event, pointerIndex);
        return conn.sendPenEvent(eventType, toolType, penButtons,
                normalizedCoords[0], normalizedCoords[1],
                getPressureOrDistance(event, pointerIndex),
                normalizedContactArea[0], normalizedContactArea[1],
                getRotationDegrees(event, pointerIndex), tiltDegrees) != MoonBridge.LI_ERR_UNSUPPORTED;
    }

    private static byte convertToolTypeToStylusToolType(MotionEvent event, int pointerIndex) {
        switch (event.getToolType(pointerIndex)) {
            case MotionEvent.TOOL_TYPE_ERASER:
                return MoonBridge.LI_TOOL_TYPE_ERASER;
            case MotionEvent.TOOL_TYPE_STYLUS:
                return MoonBridge.LI_TOOL_TYPE_PEN;
            default:
                return MoonBridge.LI_TOOL_TYPE_UNKNOWN;
        }
    }

    private boolean trySendPenEvent(View view, MotionEvent event) {
        byte eventType = getLiTouchTypeFromEvent(event);
        if (eventType < 0) {
            return false;
        }

        if (event.getActionMasked() == MotionEvent.ACTION_MOVE) {
            // Move events may impact all active pointers
            boolean handledStylusEvent = false;
            for (int i = 0; i < event.getPointerCount(); i++) {
                byte toolType = convertToolTypeToStylusToolType(event, i);
                if (toolType == MoonBridge.LI_TOOL_TYPE_UNKNOWN) {
                    // Not a stylus pointer, so skip it
                    continue;
                }
                else {
                    // This pointer is a stylus, so we'll report that we handled this event
                    handledStylusEvent = true;
                }

                if (!sendPenEventForPointer(view, event, eventType, toolType, i)) {
                    // Pen events aren't supported by the host
                    return false;
                }
            }
            return handledStylusEvent;
        }
        else if (event.getActionMasked() == MotionEvent.ACTION_CANCEL) {
            // Cancel impacts all active pointers
            return conn.sendPenEvent(MoonBridge.LI_TOUCH_EVENT_CANCEL_ALL, MoonBridge.LI_TOOL_TYPE_UNKNOWN, (byte)0,
                    0, 0, 0, 0, 0,
                    MoonBridge.LI_ROT_UNKNOWN, MoonBridge.LI_TILT_UNKNOWN) != MoonBridge.LI_ERR_UNSUPPORTED;
        }
        else {
            // Up, Down, and Hover events are specific to the action index
            byte toolType = convertToolTypeToStylusToolType(event, event.getActionIndex());
            if (toolType == MoonBridge.LI_TOOL_TYPE_UNKNOWN) {
                // Not a stylus event
                return false;
            }
            return sendPenEventForPointer(view, event, eventType, toolType, event.getActionIndex());
        }
    }

    private boolean sendTouchEventForPointer(View view, MotionEvent event, byte eventType, int pointerIndex) {
        float[] normalizedCoords = getStreamViewRelativeNormalizedXY(view, event, pointerIndex);
        float[] normalizedContactArea = getStreamViewNormalizedContactArea(event, pointerIndex);
        return conn.sendTouchEvent(eventType, event.getPointerId(pointerIndex),
                normalizedCoords[0], normalizedCoords[1],
                getPressureOrDistance(event, pointerIndex),
                normalizedContactArea[0], normalizedContactArea[1],
                getRotationDegrees(event, pointerIndex)) != MoonBridge.LI_ERR_UNSUPPORTED;
    }

    private boolean trySendTouchEvent(View view, MotionEvent event) {
        byte eventType = getLiTouchTypeFromEvent(event);
        if (eventType < 0) {
            return false;
        }

        if (event.getActionMasked() == MotionEvent.ACTION_MOVE) {
            // Move events may impact all active pointers
            for (int i = 0; i < event.getPointerCount(); i++) {
                if (!sendTouchEventForPointer(view, event, eventType, i)) {
                    return false;
                }
            }
            return true;
        }
        else if (event.getActionMasked() == MotionEvent.ACTION_CANCEL) {
            // Cancel impacts all active pointers
            return conn.sendTouchEvent(MoonBridge.LI_TOUCH_EVENT_CANCEL_ALL, 0,
                    0, 0, 0, 0, 0,
                    MoonBridge.LI_ROT_UNKNOWN) != MoonBridge.LI_ERR_UNSUPPORTED;
        }
        else {
            // Up, Down, and Hover events are specific to the action index
            return sendTouchEventForPointer(view, event, eventType, event.getActionIndex());
        }
    }

    // Returns true if the event was consumed
    // NB: View is only present if called from a view callback
    public boolean handleMotionEvent(View view, MotionEvent event) {
        // Pass through mouse/touch/joystick input if we're not grabbing
        if (!grabbedInput) {
            return false;
        }

        int deviceId = event.getDeviceId();
        if (prefConfig.ignoreSynthEvents && deviceId <= 0) {
            return false;
        }

        int eventSource = event.getSource();
        int deviceSources = event.getDevice() != null ? event.getDevice().getSources() : 0;
        if ((eventSource & InputDevice.SOURCE_CLASS_JOYSTICK) != 0) {
            if (controllerHandler.handleMotionEvent(event)) {
                return true;
            }
        }
        else if ((deviceSources & InputDevice.SOURCE_CLASS_JOYSTICK) != 0 && controllerHandler.tryHandleTouchpadEvent(event)) {
            return true;
        }
        else if ((eventSource & InputDevice.SOURCE_CLASS_POINTER) != 0 ||
                (eventSource & InputDevice.SOURCE_CLASS_POSITION) != 0 ||
                eventSource == InputDevice.SOURCE_MOUSE_RELATIVE)
        {
            boolean hasActionButton = Build.VERSION.SDK_INT < Build.VERSION_CODES.M || (event.getActionButton() != 0);
            // This case is for mice and non-finger touch devices
            if (
                    eventSource == InputDevice.SOURCE_MOUSE ||
                            ((eventSource & InputDevice.SOURCE_CLASS_POSITION) != 0 && hasActionButton) || // SOURCE_TOUCHPAD
                            (eventSource == InputDevice.SOURCE_MOUSE_RELATIVE ||
                                    (event.getPointerCount() >= 1 &&
                                            (event.getToolType(0) == MotionEvent.TOOL_TYPE_MOUSE ||
                                                    event.getToolType(0) == MotionEvent.TOOL_TYPE_STYLUS ||
                                                    event.getToolType(0) == MotionEvent.TOOL_TYPE_ERASER)) ||
                                    eventSource == 12290) // 12290 = Samsung DeX mode desktop mouse
            ) {
                int buttonState = event.getButtonState();
                int changedButtons = buttonState ^ lastButtonState;

                // Two finger click
                if ((eventSource & InputDevice.SOURCE_CLASS_POSITION) != 0 &&
                        event.getPointerCount() == 2 &&
                        (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M && event.getActionButton() == MotionEvent.BUTTON_PRIMARY)) {
                    if (event.getActionMasked() == MotionEvent.ACTION_BUTTON_PRESS) {
                        buttonState |= MotionEvent.BUTTON_SECONDARY;
                    }
                    else if (event.getActionMasked() == MotionEvent.ACTION_BUTTON_RELEASE) {
                        buttonState &= ~MotionEvent.BUTTON_SECONDARY;
                    }
                    // We may not pressing the primary button down from a previous event,
                    // so be sure to clear that bit out the button state.
                    buttonState &= ~MotionEvent.BUTTON_PRIMARY;
                    buttonState |= (lastButtonState & MotionEvent.BUTTON_PRIMARY);

                    changedButtons = buttonState ^ lastButtonState;
                }

                // Ignore mouse input if we're not capturing from our input source
                if (!inputCaptureProvider.isCapturingActive()) {
                    // We return true here because otherwise the events may end up causing
                    // Android to synthesize d-pad events.
                    return true;
                }

                // Always update the position before sending any button events. If we're
                // dealing with a stylus without hover support, our position might be
                // significantly different than before.
                if (inputCaptureProvider.eventHasRelativeMouseAxes(event)) {
                    // Send the deltas straight from the motion event
                    short deltaX = (short)inputCaptureProvider.getRelativeAxisX(event);
                    short deltaY = (short)inputCaptureProvider.getRelativeAxisY(event);

                    if (deltaX != 0 || deltaY != 0) {
                        if (prefConfig.absoluteMouseMode) {
                            // NB: view may be null, but we can unconditionally use streamView because we don't need to adjust
                            // relative axis deltas for the position of the streamView within the parent's coordinate system.
                            conn.sendMouseMoveAsMousePosition(deltaX, deltaY, (short)streamView.getWidth(), (short)streamView.getHeight());
                        }
                        else {
                            conn.sendMouseMove(deltaX, deltaY);
                        }
                    }
                }
                else if ((eventSource & InputDevice.SOURCE_CLASS_POSITION) != 0) {
                    // If this input device is not associated with the view itself (like a trackpad),
                    // we'll convert the device-specific coordinates to use to send the cursor position.
                    // This really isn't ideal but it's probably better than nothing.
                    //
                    // Trackpad on newer versions of Android (Oreo and later) should be caught by the
                    // relative axes case above. If we get here, we're on an older version that doesn't
                    // support pointer capture.
                    InputDevice device = event.getDevice();
                    if (device != null) {
                        InputDevice.MotionRange xRange = device.getMotionRange(MotionEvent.AXIS_X, eventSource);
                        InputDevice.MotionRange yRange = device.getMotionRange(MotionEvent.AXIS_Y, eventSource);

                        // All touchpads coordinate planes should start at (0, 0)
                        if (xRange != null && yRange != null && xRange.getMin() == 0 && yRange.getMin() == 0) {
                            int xMax = (int)xRange.getMax();
                            int yMax = (int)yRange.getMax();

                            // Touchpads must be smaller than (65535, 65535)
                            if (xMax <= Short.MAX_VALUE && yMax <= Short.MAX_VALUE) {
                                conn.sendMousePosition((short)event.getX(), (short)event.getY(),
                                        (short)xMax, (short)yMax);
                            }
                        }
                    }
                }
                else if (view != null && trySendPenEvent(view, event)) {
                    // If our host supports pen events, send it directly
                    return true;
                }
                else if (view != null) {
                    if (event.getToolType(0) == MotionEvent.TOOL_TYPE_FINGER) {
                        // Handle trackpad when pointer is not captured by synthesizing a trackpad movement
                        int eventAction = event.getActionMasked();
                        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q && event.getClassification() == MotionEvent.CLASSIFICATION_TWO_FINGER_SWIPE) {
                            if (!pointerSwiping) {
                                pointerSwiping = true;
                                handleTouchInput(event, trackpadContextMap, false, MotionEvent.ACTION_POINTER_DOWN, 1, 2);
                            }
                            return handleTouchInput(event, trackpadContextMap, false, MotionEvent.ACTION_MOVE, 1, 2);
                        } else if (pointerSwiping && eventAction == MotionEvent.ACTION_UP) {
                            pointerSwiping = false;
                            synthClickPending = false;
                            handleTouchInput(event, trackpadContextMap, false, MotionEvent.ACTION_POINTER_UP, 1, 2);
                            return true;
                        }

                        // Press & Hold / Double-Tap & Hold for Selection or Drag & Drop
                        double positionDelta = Math.sqrt(
                                Math.pow(event.getX() - lastTouchDownX, 2) +
                                Math.pow(event.getY() - lastTouchDownY, 2)
                        );

                        if (synthClickPending &&
                            event.getEventTime() - synthTouchDownTime >= prefConfig.trackpadDragDropThreshold) {
                            if (positionDelta > 50) {
                                pendingDrag = false;
                            } else if (pendingDrag) {
                                pendingDrag = false;
                                isDragging = true;
                                if (prefConfig.trackpadDragDropVibration) {
                                    Vibrator vibrator = ((Vibrator) getSystemService(Context.VIBRATOR_SERVICE));
                                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                                        vibrator.vibrate(VibrationEffect.createOneShot(20, 127));
                                    } else {
                                        vibrator.vibrate(20);
                                    }
                                }
                                conn.sendMouseButtonDown(MouseButtonPacket.BUTTON_LEFT);
                                return true;
                            }
                        }

                        switch (eventAction) {
                            case MotionEvent.ACTION_HOVER_MOVE:
                            case MotionEvent.ACTION_MOVE:
                                updateMousePosition(view, event);
                                return true;
                            case MotionEvent.ACTION_HOVER_EXIT:
                            case MotionEvent.ACTION_DOWN:
                                pendingDrag = true;
                                synthClickPending = true;
                                lastTouchDownX = event.getX();
                                lastTouchDownY = event.getY();
                                synthTouchDownTime = event.getEventTime();
                                return true;
                            case MotionEvent.ACTION_HOVER_ENTER:
                            case MotionEvent.ACTION_UP:
                                if (synthClickPending) {
                                    long timeDiff = event.getEventTime() - synthTouchDownTime;

                                    if (eventSource == 12290) {
                                        // Special handle for DeX
                                        // DeX reports button secondary when tapping with two fingers
                                        // So there's no need to distinguish left/right click by time difference
                                        if (timeDiff < 120) {
                                            conn.sendMouseButtonDown(MouseButtonPacket.BUTTON_LEFT);
                                            conn.sendMouseButtonUp(MouseButtonPacket.BUTTON_LEFT);
                                        }
                                    } else {
                                        if (timeDiff < 20) {
                                            conn.sendMouseButtonDown(MouseButtonPacket.BUTTON_LEFT);
                                            conn.sendMouseButtonUp(MouseButtonPacket.BUTTON_LEFT);
                                        } else if (timeDiff < 120) {
                                            conn.sendMouseButtonDown(MouseButtonPacket.BUTTON_RIGHT);
                                            conn.sendMouseButtonUp(MouseButtonPacket.BUTTON_RIGHT);
                                        }
                                    }
                                    if (isDragging) {
                                        isDragging = false;
                                        conn.sendMouseButtonUp(MouseButtonPacket.BUTTON_LEFT);
                                    }
                                    pendingDrag = false;
                                    synthClickPending = false;
                                }
                                return true;
                            case MotionEvent.ACTION_BUTTON_PRESS:
                            case MotionEvent.ACTION_BUTTON_RELEASE:
                                synthClickPending = false;
                            default:
                                break;
                        }
                    } else {
                        updateMousePosition(view, event);
                    }
                }

                if (event.getActionMasked() == MotionEvent.ACTION_SCROLL) {
                    // Send the vertical scroll packet
                    conn.sendMouseHighResScroll((short)(event.getAxisValue(MotionEvent.AXIS_VSCROLL) * 120));
                    conn.sendMouseHighResHScroll((short)(event.getAxisValue(MotionEvent.AXIS_HSCROLL) * 120));
                }

                if ((changedButtons & MotionEvent.BUTTON_PRIMARY) != 0) {
                    if ((buttonState & MotionEvent.BUTTON_PRIMARY) != 0) {
                        conn.sendMouseButtonDown(MouseButtonPacket.BUTTON_LEFT);
                    }
                    else {
                        conn.sendMouseButtonUp(MouseButtonPacket.BUTTON_LEFT);
                    }
                }

                // Mouse secondary or stylus primary is right click (stylus down is left click)
                if ((changedButtons & (MotionEvent.BUTTON_SECONDARY | MotionEvent.BUTTON_STYLUS_PRIMARY)) != 0) {
                    if ((buttonState & (MotionEvent.BUTTON_SECONDARY | MotionEvent.BUTTON_STYLUS_PRIMARY)) != 0) {
                        conn.sendMouseButtonDown(MouseButtonPacket.BUTTON_RIGHT);
                    }
                    else {
                        conn.sendMouseButtonUp(MouseButtonPacket.BUTTON_RIGHT);
                    }
                }

                // Mouse tertiary or stylus secondary is middle click
                if ((changedButtons & (MotionEvent.BUTTON_TERTIARY | MotionEvent.BUTTON_STYLUS_SECONDARY)) != 0) {
                    if ((buttonState & (MotionEvent.BUTTON_TERTIARY | MotionEvent.BUTTON_STYLUS_SECONDARY)) != 0) {
                        conn.sendMouseButtonDown(MouseButtonPacket.BUTTON_MIDDLE);
                    }
                    else {
                        conn.sendMouseButtonUp(MouseButtonPacket.BUTTON_MIDDLE);
                    }
                }

                if (prefConfig.mouseNavButtons) {
                    if ((changedButtons & MotionEvent.BUTTON_BACK) != 0) {
                        if ((buttonState & MotionEvent.BUTTON_BACK) != 0) {
                            conn.sendMouseButtonDown(MouseButtonPacket.BUTTON_X1);
                        }
                        else {
                            conn.sendMouseButtonUp(MouseButtonPacket.BUTTON_X1);
                        }
                    }

                    if ((changedButtons & MotionEvent.BUTTON_FORWARD) != 0) {
                        if ((buttonState & MotionEvent.BUTTON_FORWARD) != 0) {
                            conn.sendMouseButtonDown(MouseButtonPacket.BUTTON_X2);
                        }
                        else {
                            conn.sendMouseButtonUp(MouseButtonPacket.BUTTON_X2);
                        }
                    }
                }

                // Handle stylus presses
                if (event.getPointerCount() == 1 && event.getActionIndex() == 0) {
                    if (event.getActionMasked() == MotionEvent.ACTION_DOWN) {
                        if (event.getToolType(0) == MotionEvent.TOOL_TYPE_STYLUS) {
                            lastAbsTouchDownTime = event.getEventTime();
                            lastAbsTouchDownX = event.getX(0);
                            lastAbsTouchDownY = event.getY(0);

                            // Stylus is left click
                            conn.sendMouseButtonDown(MouseButtonPacket.BUTTON_LEFT);
                        } else if (event.getToolType(0) == MotionEvent.TOOL_TYPE_ERASER) {
                            lastAbsTouchDownTime = event.getEventTime();
                            lastAbsTouchDownX = event.getX(0);
                            lastAbsTouchDownY = event.getY(0);

                            // Eraser is right click
                            conn.sendMouseButtonDown(MouseButtonPacket.BUTTON_RIGHT);
                        }
                    }
                    else if (event.getActionMasked() == MotionEvent.ACTION_UP || event.getActionMasked() == MotionEvent.ACTION_CANCEL) {
                        if (event.getToolType(0) == MotionEvent.TOOL_TYPE_STYLUS) {
                            lastAbsTouchUpTime = event.getEventTime();
                            lastAbsTouchUpX = event.getX(0);
                            lastAbsTouchUpY = event.getY(0);

                            // Stylus is left click
                            conn.sendMouseButtonUp(MouseButtonPacket.BUTTON_LEFT);
                        } else if (event.getToolType(0) == MotionEvent.TOOL_TYPE_ERASER) {
                            lastAbsTouchUpTime = event.getEventTime();
                            lastAbsTouchUpX = event.getX(0);
                            lastAbsTouchUpY = event.getY(0);

                            // Eraser is right click
                            conn.sendMouseButtonUp(MouseButtonPacket.BUTTON_RIGHT);
                        }
                    }
                }

                lastButtonState = buttonState;
            }
            // This case is for fingers
            else {
                if (eventSource == InputDevice.SOURCE_TOUCHPAD) {
                    return handleTouchInput(event, trackpadContextMap, false);
                } else {
                    if (virtualController != null &&
                            (virtualController.getControllerMode() == VirtualController.ControllerMode.MoveButtons ||
                                    virtualController.getControllerMode() == VirtualController.ControllerMode.ResizeButtons)) {
                        // Ignore presses when the virtual controller is being configured
                        return true;
                    }

                    if (isPanZoomMode) {
                        // panning the streamView
                        panZoomHandler.handleTouchEvent(event);
                        return true;
                    }

                    // If touch is disabled or not initialized, we'll try panning the streamView
                    if (touchContextMap[0] == null) {
                        return true;
                    }

                    if (prefConfig.enableMultiTouchGestures || !prefConfig.enableMultiTouchScreen) {
                        int pointerCount = event.getPointerCount();
                        if (pointerCount > 2) {
                            int eventAction = event.getActionMasked();
                            if (
                                    (
                                            eventAction == MotionEvent.ACTION_POINTER_DOWN
                                                    || eventAction == MotionEvent.ACTION_POINTER_UP
                                                    || eventAction == MotionEvent.ACTION_UP
                                    )
                                            && handleMultiTouchGesture(event, eventAction, pointerCount, view)
                            ) {
                                return true;
                            }
                        }
                    }

                    if (isHubManagedSession()) {
                        if(event.getActionMasked()==MotionEvent.ACTION_DOWN) {
                            releaseHubTouches();
                            hubGestureActive=true;
                            boolean login=hubDesktopInput!=null && hubDesktopInput.locked();
                            float[] point=getStreamViewRelativeNormalizedXY(view,event,event.getActionIndex());
                            boolean caption=hubDesktopInput!=null && hubDesktopInput.captionButton(point[0],point[1]);
                            hubGestureNative=!prefConfig.touchscreenTrackpad && (login ||
                                    prefConfig.enableMultiTouchScreen && !caption &&
                                    !getSharedPreferences("streamhub_controls",MODE_PRIVATE).getBoolean("touch_compatibility",true));
                        }
                        boolean nativeGesture=hubGestureActive && hubGestureNative;
                        if(event.getActionMasked()==MotionEvent.ACTION_UP || event.getActionMasked()==MotionEvent.ACTION_CANCEL)hubGestureActive=false;
                        // A gesture never falls through from partially delivered touch to mouse.
                        if(nativeGesture) {
                            trySendTouchEvent(view,event);
                            if(event.getActionMasked()==MotionEvent.ACTION_UP && hubDesktopInput!=null)hubDesktopInput.touchedLoginField();
                            return true;
                        }
                        return handleTouchInput(event,touchContextMap,true);
                    }
                    if (prefConfig.enableMultiTouchScreen && !prefConfig.touchscreenTrackpad && trySendTouchEvent(view, event)) {
                        // If this host supports touch events and absolute touch is enabled,
                        // send it directly as a touch event.
                        return true;
                    }

                    return handleTouchInput(event, touchContextMap, true);
                }
            }

            // Handled a known source
            return true;
        }

        // Unknown class
        return false;
    }

    private boolean handleTouchInput(MotionEvent event, TouchContext[] inputContextMap, boolean isTouchScreen) {
        return handleTouchInput(event, inputContextMap, isTouchScreen, event.getActionMasked(), event.getActionIndex(), event.getPointerCount());
    }

    private boolean handleTouchInput(MotionEvent event, TouchContext[] inputContextMap, boolean isTouchScreen, int eventAction, int actionIndex, int pointerCount) {
        int actualActionIndex = event.getActionIndex();
        int actualPointerCount = event.getPointerCount();

        boolean shouldDuplicateMovement = actualPointerCount < pointerCount;

        int eventX = (int)event.getX(actualActionIndex);
        int eventY = (int)event.getY(actualActionIndex);

        // Handle view scaling
        if (isTouchScreen) {
            float[] normalizedCoords = getNormalizedCoordinates(streamView, eventX, eventY);
            eventX = (int)normalizedCoords[0];
            eventY = (int)normalizedCoords[1];
        }

        TouchContext context = getTouchContext(actionIndex, inputContextMap);
        if (context == null) {
            return false;
        }

        switch (eventAction)
        {
            case MotionEvent.ACTION_POINTER_DOWN:
            case MotionEvent.ACTION_DOWN:
                for (TouchContext touchContext : inputContextMap) {
                    touchContext.setPointerCount(pointerCount);
                }
                context.touchDownEvent(eventX, eventY, event.getEventTime(), true);
                break;
            case MotionEvent.ACTION_POINTER_UP:
            case MotionEvent.ACTION_UP:
                //是触控板模式 三点呼出软键盘
                if(prefConfig.touchscreenTrackpad){
                    if (pointerCount == 1 &&
                            (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU || (event.getFlags() & MotionEvent.FLAG_CANCELED) == 0)) {
                        // All fingers up
                        long currentEventTime = event.getEventTime();
                        if (currentEventTime - threeFingerDownTime < THREE_FINGER_TAP_THRESHOLD) {
                            // This is a 3 finger tap to bring up the keyboard
                            toggleKeyboard();
                            return true;
                        } else if (currentEventTime - fourFingerDownTime < FOUR_FINGER_TAP_THRESHOLD) {
                            toggleFullKeyboard();
                            return true;
                        } else if (currentEventTime - fiveFingerDownTime < FIVE_FINGER_TAP_THRESHOLD) {
                            if(prefConfig.enableBackMenu) {
                                showGameMenu(null);
                            }
                            return true;
                        }
                    }
                }
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU && (event.getFlags() & MotionEvent.FLAG_CANCELED) != 0) {
                    context.cancelTouch();
                }
                else {
                    context.touchUpEvent(eventX, eventY, event.getEventTime());
                }

                for (TouchContext touchContext : inputContextMap) {
                    touchContext.setPointerCount(pointerCount - 1);
                }
                if (actionIndex == 0 && pointerCount > 1 && !context.isCancelled()) {
                    // The original secondary touch now becomes primary
                    int pointer1X = (int)event.getX(1);
                    int pointer1Y = (int)event.getY(1);
                    if (isTouchScreen) {
                        float[] normalizedCoords = getNormalizedCoordinates(streamView, pointer1X, pointer1Y);
                        pointer1X = (int)normalizedCoords[0];
                        pointer1Y = (int)normalizedCoords[1];
                    }
                    context.touchDownEvent(
                            pointer1X,
                            pointer1Y,
                            event.getEventTime(), false);
                }
                break;
            case MotionEvent.ACTION_MOVE:
                // ACTION_MOVE is special because it always has actionIndex == 0
                // We'll call the move handlers for all indexes manually

                // First process the historical events
                for (int i = 0; i < event.getHistorySize(); i++) {
                    for (TouchContext aTouchContextMap : inputContextMap) {
                        if (aTouchContextMap.getActionIndex() < pointerCount)
                        {
                            int aActionIndex = shouldDuplicateMovement ? 0 : aTouchContextMap.getActionIndex();
                            int historicalX = (int)event.getHistoricalX(aActionIndex, i);
                            int historicalY = (int)event.getHistoricalY(aActionIndex, i);
                            if (isTouchScreen) {
                                float[] normalizedCoords = getNormalizedCoordinates(streamView, historicalX, historicalY);
                                historicalX = (int)normalizedCoords[0];
                                historicalY = (int)normalizedCoords[1];
                            }
                            aTouchContextMap.touchMoveEvent(
                                    historicalX,
                                    historicalY,
                                    event.getHistoricalEventTime(i));
                        }
                    }
                }

                // Now process the current values
                for (TouchContext aTouchContextMap : inputContextMap) {
                    if (aTouchContextMap.getActionIndex() < pointerCount)
                    {
                        int aActionIndex = shouldDuplicateMovement ? 0 : aTouchContextMap.getActionIndex();
                        int currentX = (int)event.getX(aActionIndex);
                        int currentY = (int)event.getY(aActionIndex);
                        if (isTouchScreen) {
                            float[] normalizedCoords = getNormalizedCoordinates(streamView, currentX, currentY);
                            currentX = (int)normalizedCoords[0];
                            currentY = (int)normalizedCoords[1];
                        }
                        aTouchContextMap.touchMoveEvent(
                                currentX,
                                currentY,
                                event.getEventTime());
                    }
                }
                break;
            case MotionEvent.ACTION_CANCEL:
                for (TouchContext aTouchContext : inputContextMap) {
                    aTouchContext.cancelTouch();
                    aTouchContext.setPointerCount(0);
                }
                break;
            default:
                return false;
        }

        return true;
    }

    private boolean handleMultiTouchGesture(MotionEvent event, int eventAction, int pointerCount, View view) {

        if (eventAction == MotionEvent.ACTION_POINTER_DOWN) {
            if (pointerCount == 3) {
                threeFingerDownTime = event.getEventTime();
            } else if (pointerCount == 4) {
                threeFingerDownTime = 0;
                fourFingerDownTime = event.getEventTime();
            } else if (pointerCount == 5) {
                threeFingerDownTime = 0;
                fourFingerDownTime = 0;
                fiveFingerDownTime = event.getEventTime();
            }
        }

        switch (eventAction) {
            case MotionEvent.ACTION_POINTER_UP:
            case MotionEvent.ACTION_UP:
                long currentEventTime = event.getEventTime();
                if (pointerCount >= 5 && fiveFingerDownTime > 0 && currentEventTime - fiveFingerDownTime < FIVE_FINGER_TAP_THRESHOLD) {
                    if(prefConfig.enableBackMenu) {
                        showGameMenu(null);
                    }
                    fiveFingerDownTime = 0;
                    break;
                } else if (pointerCount == 4 && fourFingerDownTime > 0 && currentEventTime - fourFingerDownTime < FOUR_FINGER_TAP_THRESHOLD) {
                    toggleFullKeyboard();
                    fourFingerDownTime = 0;
                    break;
                } else if (pointerCount == 3 && threeFingerDownTime > 0 && currentEventTime - threeFingerDownTime < THREE_FINGER_TAP_THRESHOLD) {
                    toggleKeyboard();
                    threeFingerDownTime = 0;
                    break;
                }
                threeFingerDownTime = 0;
                fourFingerDownTime = 0;
                fiveFingerDownTime = 0;

                cancelStaleTouchState(event, view);
                return false;
            default:
                return false;
        }

        cancelStaleTouchState(event, view);
        return true;
    }

    private void cancelStaleTouchState(MotionEvent event, View view) {
        MotionEvent cancelEvent = MotionEvent.obtain(event);
        cancelEvent.setAction(MotionEvent.ACTION_CANCEL);
        view.dispatchTouchEvent(cancelEvent);
        cancelEvent.recycle();
        for (TouchContext aTouchContext : touchContextMap) {
            aTouchContext.cancelTouch();
            aTouchContext.setPointerCount(0);
        }
    }

    @Override
    public boolean onGenericMotionEvent(MotionEvent event) {
        return handleMotionEvent(null, event) || super.onGenericMotionEvent(event);
    }

    /**
     * Maps MotionEvent coordinates from the internal display to the given streamView
     * on the external display by scaling from the primary display's resolution to
     * the streamView's size.
     *
     * @param event      The MotionEvent to map (typically from ACTION_HOVER)
     * @param streamView The View shown on the external display
     * @return A PointF with the mapped (x, y) inside the streamView
     */
    public PointF mapMouseCoordinatesToStreamView(MotionEvent event, View streamView) {
        if (event == null || streamView == null) {
            return new PointF(0, 0);
        }

        float sourceWidth = 0f;
        float sourceHeight = 0f;

        int[] loc = new int[2];
        if (rootView instanceof View) {
            View mouseHoverArea = (View) rootView;
            mouseHoverArea.getLocationOnScreen(loc);
            sourceWidth = mouseHoverArea.getWidth();
            sourceHeight = mouseHoverArea.getHeight();
        }

        float rawX = event.getX();
        float rawY = event.getY();

        float targetWidth = streamView.getWidth();
        float targetHeight = streamView.getHeight();

        // Calculate relative position in source display
        float relativeX = rawX / sourceWidth;
        float relativeY = rawY / sourceHeight;

        // Scale to target streamView
        float scaledX = relativeX * targetWidth;
        float scaledY = relativeY * targetHeight;

        // Clamp within bounds
        scaledX = Math.max(0, Math.min(scaledX, targetWidth - 1));
        scaledY = Math.max(0, Math.min(scaledY, targetHeight - 1));

        return new PointF(scaledX, scaledY);
    }


    private void updateMousePosition(View touchedView, MotionEvent event) {
        // X and Y are already relative to the provided view object
        float eventX, eventY;
        // For our StreamView itself, we can use the coordinates unmodified.

        if (touchedView == streamView) {
            eventX = event.getX(0);
            eventY = event.getY(0);
        } else {
            // For the containing background view, we must subtract the origin
            // of the StreamView to get video-relative coordinates.
            eventX = event.getX(0) - streamView.getX();
            eventY = event.getY(0) - streamView.getY();
        }

        if (event.getPointerCount() == 1 && event.getActionIndex() == 0 &&
                (event.getToolType(0) == MotionEvent.TOOL_TYPE_ERASER ||
                        event.getToolType(0) == MotionEvent.TOOL_TYPE_STYLUS))
        {
            switch (event.getActionMasked()) {
                case MotionEvent.ACTION_DOWN:
                case MotionEvent.ACTION_HOVER_ENTER:
                case MotionEvent.ACTION_HOVER_EXIT:
                case MotionEvent.ACTION_HOVER_MOVE:
                    if (event.getEventTime() - lastAbsTouchUpTime <= STYLUS_UP_DEAD_ZONE_DELAY &&
                            Math.sqrt(Math.pow(eventX - lastAbsTouchUpX, 2) + Math.pow(eventY - lastAbsTouchUpY, 2)) <= STYLUS_UP_DEAD_ZONE_RADIUS) {
                        // Enforce a small deadzone between touch up and hover or touch down to allow more precise double-clicking
                        return;
                    }
                    break;

                case MotionEvent.ACTION_MOVE:
                case MotionEvent.ACTION_UP:
                    if (event.getEventTime() - lastAbsTouchDownTime <= STYLUS_DOWN_DEAD_ZONE_DELAY &&
                            Math.sqrt(Math.pow(eventX - lastAbsTouchDownX, 2) + Math.pow(eventY - lastAbsTouchDownY, 2)) <= STYLUS_DOWN_DEAD_ZONE_RADIUS) {
                        // Enforce a small deadzone between touch down and move or touch up to allow more precise double-clicking
                        return;
                    }
                    break;
            }
        }

        // We may get values slightly outside our view region on ACTION_HOVER_ENTER and ACTION_HOVER_EXIT.
        // Normalize these to the view size. We can't just drop them because we won't always get an event
        // right at the boundary of the view, so dropping them would result in our cursor never really
        // reaching the sides of the screen.
        eventX = Math.min(Math.max(eventX, 0), streamView.getWidth());
        eventY = Math.min(Math.max(eventY, 0), streamView.getHeight());

        conn.sendMousePosition((short)eventX, (short)eventY, (short)streamView.getWidth(), (short)streamView.getHeight());
    }

    @Override
    public boolean onGenericMotion(View view, MotionEvent event) {
        return handleMotionEvent(view, event);
    }

    @SuppressLint("ClickableViewAccessibility")
    @Override
    public boolean onTouch(View view, MotionEvent event) {
        if (streamHubInputToolbar != null && isHubManagedSession()
                && event.getActionMasked() == MotionEvent.ACTION_DOWN) {
            // The stream surface receives touches outside the floating control
            // panel; close the panel before forwarding the touch to the PC.
            streamHubInputToolbar.collapseFromOutsideTouch();
        }
        if (event.getAction() == MotionEvent.ACTION_DOWN) {
            // Tell the OS not to buffer input events for us
            //
            // NB: This is still needed even when we call the newer requestUnbufferedDispatch()!
            view.requestUnbufferedDispatch(event);
        }

        return handleMotionEvent(view, event);
    }

    private static String getStageDisplayName(String stage) {
        if (stage == null) return "连接";
        switch (stage) {
            case "none": return "连接";
            case "platform initialization": return "初始化串流平台";
            case "name resolution": return "解析主机地址";
            case "audio stream initialization": return "初始化音频串流";
            case "RTSP handshake": return "协商串流连接";
            case "control stream initialization": return "初始化控制通道";
            case "video stream initialization": return "初始化视频串流";
            case "input stream initialization": return "初始化输入通道";
            case "control stream establishment": return "建立控制通道";
            case "video stream establishment": return "建立视频串流";
            case "audio stream establishment": return "建立音频串流";
            case "input stream establishment": return "建立输入通道";
            case "Desktop": return "远程主屏";
            case "Tablet Second Screen": return "平板副屏";
            default: return stage;
        }
    }

    @Override
    public void stageStarting(final String stage) {
        String mediaKey = hubMediaStageKey(stage);
        if (mediaKey != null && hubSessionLease != null) {
            hubStartupTrace.stageStarted(mediaKey);
        }
        runOnUiThread(new Runnable() {
            @Override
            public void run() {
                if (spinner != null) {
                    spinner.setMessage(getResources().getString(R.string.conn_starting) + "：" + getStageDisplayName(stage));
                }
                if (!connectionStopRequested) setHubConnectionState("正在" + getStageDisplayName(stage) + "…");
            }
        });
    }

    @Override
    public void stageComplete(String stage) {
        String mediaKey = hubMediaStageKey(stage);
        if (mediaKey != null && hubSessionLease != null) hubStartupTrace.stageCompleted(mediaKey);
    }

    private static String hubMediaStageKey(String stage) {
        if ("platform initialization".equals(stage)) return "platform-initialization";
        if ("name resolution".equals(stage)) return "name-resolution";
        if ("RTSP handshake".equals(stage)) return "rtsp-handshake";
        if ("control stream initialization".equals(stage)) return "control-initialization";
        if ("video stream initialization".equals(stage)) return "video-initialization";
        if ("audio stream initialization".equals(stage)) return "audio-initialization";
        if ("input stream initialization".equals(stage)) return "input-initialization";
        if ("control stream establishment".equals(stage)) return "control-establishment";
        if ("video stream establishment".equals(stage)) return "video-establishment";
        if ("audio stream establishment".equals(stage)) return "audio-establishment";
        if ("input stream establishment".equals(stage)) return "input-establishment";
        return null;
    }

    /** Called from the product preparation worker before it launches a replacement stream. */
    public static boolean awaitHubConnectionStop(long timeoutMillis) throws InterruptedException {
        return hubConnectionStop.await(timeoutMillis, TimeUnit.MILLISECONDS);
    }

    private void stopConnection() {
        final boolean hubManaged = getIntent().getBooleanExtra("HubManaged", false);
        if (!connectionStopRequested && conn != null && (connecting || connected || (hubManaged && attemptedConnection))) {
            connectionStopRequested = true;
            final NvConnection stoppingConnection = conn;
            final NvHTTP stoppingHttp = httpConn;
            // An ordinary tablet exit ends capture before releasing its lease.
            // The host may then keep the idle engine briefly with its display
            // detached. Rotation keeps its app and lease exactly as before.
            final boolean shouldQuit = HubSessionLease.shouldQuitBackend(hubManaged, streamHubMode, quitOnStop, hubRotationReconnect);
            final CountDownLatch stopFinished = hubManaged ? new CountDownLatch(1) : null;
            if (stopFinished != null) hubConnectionStop = stopFinished;
            connecting = connected = false;
            if (hubDesktopInput != null) { hubDesktopInput.close(); hubDesktopInput = null; }
            setHubConnectionState(hubRotationReconnect ? "正在调整画面并重连…" : "正在结束连接…");
            updatePipAutoEnter();

            if (controllerHandler != null) controllerHandler.stop();

            // Update GameManager state to indicate we're no longer in game
            UiHelper.notifyStreamEnded(this);

            // Stop may take a few hundred ms to do some network I/O to tell
            // the server we're going away and clean up. Let it run in a separate
            // thread to keep things smooth for the UI. Inside moonlight-common,
            // we prevent another thread from starting a connection before and
            // during the process of stopping this one.
            new Thread() {
                public void run() {
                    try {
                        try {
                            stoppingConnection.stop();
                        } catch (RuntimeException e) {
                            LimeLog.warning("Stream connection cleanup did not complete normally");
                        }
                        // Rotation is a session-preserving reconfigure. Calling
                        // quitApp here changes the host to IDLE and forces a
                        // full virtual-display teardown before the new activity.
                        if (stoppingHttp != null && shouldQuit && !hubRotationReconnect) {
                            try {
                                if (!hubManaged) sleep(1000);
                                stoppingHttp.quitApp();
                                if (!hubManaged) Game.this.runOnUiThread(() -> Toast.makeText(Game.this, Game.this.getResources().getString(R.string.applist_quit_success) + " " + AppView.getDisplayAppName(appName), Toast.LENGTH_LONG).show());
                            } catch (Exception e) {
                                if (hubManaged) LimeLog.warning("Product session cleanup will be retried before the next connection");
                                else Game.this.runOnUiThread(() -> Toast.makeText(Game.this, e.getMessage(), Toast.LENGTH_LONG).show());
                            }
                        }
                    } finally {
                        // Rotation is a stream-preserving reconfiguration. Keep the
                        // lease heartbeat alive so the host can reuse the healthy
                        //副屏引擎 and only change its display mode. Ordinary exits
                        // still end the lease and clean the virtual display.
                        if (hubSessionLease != null && !hubRotationReconnect) hubSessionLease.end();
                        if (stopFinished != null) stopFinished.countDown();
                    }
                }
            }.start();
        }
    }

    @Override
    public boolean stageFailed(final String stage, final int portFlags, final int errorCode) {
        // Perform a connection test if the failure could be due to a blocked port
        // This does network I/O, so don't do it on the main thread.
        final int portTestResult = MoonBridge.testClientConnectivity(ServerHelper.CONNECTION_TEST_SERVER, 443, portFlags);

        if (errorCode == 0 && portFlags != 0 && (portTestResult == MoonBridge.ML_TEST_RESULT_INCONCLUSIVE || portTestResult == 0)) {
            spinner.setMessage(getResources().getString(R.string.unlocking_or_starting));
            return true;
        }

        runOnUiThread(new Runnable() {
            @Override
            public void run() {
                if (spinner != null) {
                    spinner.dismiss();
                    spinner = null;
                }

                if (!displayedFailureDialog) {
                    displayedFailureDialog = true;
                    setHubConnectionState("连接失败，请返回后重试");
                    if (isHubManagedSession()) { quitOnStop = true; stopConnection(); }
                    LimeLog.severe(stage + " failed: " + errorCode);

                    // If video initialization failed and the surface is still valid, display extra information for the user
                    if (stage.contains("video") && streamView.getHolder().getSurface().isValid()) {
                        Toast.makeText(Game.this, getResources().getText(R.string.video_decoder_init_failed), Toast.LENGTH_LONG).show();
                    }

                    String dialogText = getResources().getString(R.string.conn_error_msg) + "：" + getStageDisplayName(stage) + "（错误代码：" + errorCode + "）";

                    switch (errorCode) {
                        case 403: {
                            dialogText += "\n\n" + getResources().getString(R.string.error_msg_permission_denied) + " (" + getResources().getString(R.string.permission_launch_app) + ")";
                            break;
                        }
                        case -408: {
                            dialogText += "\n\n" + getResources().getString(R.string.error_msg_timeout);
                            break;
                        }
                        default: {
                            // do nothing
                        }
                    }

                    if (portFlags != 0) {
                        dialogText += "\n\n" + getResources().getString(R.string.check_ports_msg) + "\n" +
                                MoonBridge.stringifyPortFlags(portFlags, "\n");
                    }

                    if (portTestResult != MoonBridge.ML_TEST_RESULT_INCONCLUSIVE && portTestResult != 0)  {
                        dialogText += "\n\n" + getResources().getString(R.string.nettest_text_blocked);
                    }

                    Dialog.displayDialog(Game.this, getResources().getString(R.string.conn_error_title), dialogText, true);
                    finishSecondScreen();
                }
            }
        });

        return false;
    }

    private void finishSecondScreen() {
        // Otherwise screen stays connected but not working with no way of quitting it
        if (prefConfig.enableFullExDisplay) {
            Handler h = new Handler();
            h.postDelayed(new Runnable() {
                @Override
                public void run() {
                    finish();
                }
            }, 2000);
        }
    }

    @Override
    public void connectionTerminated(final int errorCode) {
        // Start native cleanup before the optional network diagnostic, which can block.
        if (isHubManagedSession() && !hubRotationReconnect) runOnUiThread(() -> {
            if (!hubRotationReconnect && !connectionStopRequested) stopConnection();
        });
        // Perform a connection test if the failure could be due to a blocked port
        // This does network I/O, so don't do it on the main thread.
        final int portFlags = MoonBridge.getPortFlagsFromTerminationErrorCode(errorCode);
        final int portTestResult = MoonBridge.testClientConnectivity(ServerHelper.CONNECTION_TEST_SERVER,443, portFlags);

        runOnUiThread(new Runnable() {
            @Override
            public void run() {
                // Let the display go to sleep now
                getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);

                // Stop processing controller input
                controllerHandler.stop();

                // Ungrab input
                setInputGrabState(false);
                setHubConnectionState("连接已断开");

                if (!displayedFailureDialog) {
                    displayedFailureDialog = true;
                    LimeLog.severe("Connection terminated: " + errorCode);
                    stopConnection();

                    // Display the error dialog if it was an unexpected termination.
                    // Otherwise, just finish the activity immediately.
                    if (errorCode != MoonBridge.ML_ERROR_GRACEFUL_TERMINATION) {
                        String message;

                        if (portTestResult != MoonBridge.ML_TEST_RESULT_INCONCLUSIVE && portTestResult != 0) {
                            // If we got a blocked result, that supersedes any other error message
                            message = getResources().getString(R.string.nettest_text_blocked);
                        }
                        else {
                            switch (errorCode) {
                                case MoonBridge.ML_ERROR_NO_VIDEO_TRAFFIC:
                                    message = getResources().getString(R.string.no_video_received_error);
                                    break;

                                case MoonBridge.ML_ERROR_NO_VIDEO_FRAME:
                                    message = getResources().getString(R.string.no_frame_received_error);
                                    break;

                                case MoonBridge.ML_ERROR_UNEXPECTED_EARLY_TERMINATION:
                                case MoonBridge.ML_ERROR_PROTECTED_CONTENT:
                                    message = getResources().getString(R.string.early_termination_error);
                                    break;

                                case MoonBridge.ML_ERROR_FRAME_CONVERSION:
                                    message = getResources().getString(R.string.frame_conversion_error);
                                    break;

                                default:
                                    String errorCodeString;
                                    // We'll assume large errors are hex values
                                    if (Math.abs(errorCode) > 1000) {
                                        errorCodeString = Integer.toHexString(errorCode);
                                    }
                                    else {
                                        errorCodeString = Integer.toString(errorCode);
                                    }
                                    message = getResources().getString(R.string.conn_terminated_msg) + "\n\n" +
                                            getResources().getString(R.string.error_code_prefix) + " " + errorCodeString;
                                    break;
                            }
                        }

                        if (portFlags != 0) {
                            message += "\n\n" + getResources().getString(R.string.check_ports_msg) + "\n" +
                                    MoonBridge.stringifyPortFlags(portFlags, "\n");
                        }

                        Dialog.displayDialog(Game.this, getResources().getString(R.string.conn_terminated_title),
                                message, true);
                    }
                    else {
                        finish();
                    }
                }
            }
        });
    }

    @Override
    public void connectionStatusUpdate(final int connectionStatus) {
        runOnUiThread(new Runnable() {
            @Override
            public void run() {
                if (connectionStopRequested) return;
                if (isHubManagedSession()) {
                    updateHubManagedNetworkWarning();
                    return;
                }
                if (connectionStatus == MoonBridge.CONN_STATUS_POOR) setHubConnectionState("已连接 · 当前网络不稳定");
                else if (connectionStatus == MoonBridge.CONN_STATUS_OKAY) setHubConnectionState("已连接");
                if (prefConfig.disableWarnings) {
                    return;
                }

                if (connectionStatus == MoonBridge.CONN_STATUS_POOR) {
                    if (prefConfig.bitrate > 5000) {
                        notificationOverlayView.setText(getResources().getString(R.string.slow_connection_msg));
                    }
                    else {
                        notificationOverlayView.setText(getResources().getString(R.string.poor_connection_msg));
                    }

                    requestedNotificationOverlayVisibility = View.VISIBLE;
                }
                else if (connectionStatus == MoonBridge.CONN_STATUS_OKAY) {
                    requestedNotificationOverlayVisibility = View.GONE;
                }

                if (!isHidingOverlays) {
                    notificationOverlayView.setVisibility(requestedNotificationOverlayVisibility);
                }
            }
        });
    }

    @Override
    public void connectionStarted() {
        if (hubStartupTrace != null) hubStartupTrace.event("transport-ready");
        runOnUiThread(new Runnable() {
            @Override
            public void run() {
                if (connectionStopRequested || isFinishing() || isDestroyed()) return;
                if (spinner != null) {
                    spinner.dismiss();
                    spinner = null;
                }

                connected = true;
                if (hubDesktopInput != null) hubDesktopInput.connected();
                connecting = false;
                if (hubSessionLease != null) hubSessionLease.connected();
                setHubConnectionState("已连接");
                reapplyHubDisplayMode("connection-started");
                updatePipAutoEnter();

                // Hide the mouse cursor now after a short delay.
                // Doing it before dismissing the spinner seems to be undone
                // when the spinner gets displayed. On Android Q, even now
                // is too early to capture. We will delay a second to allow
                // the spinner to dismiss before capturing.
                Handler h = new Handler();
                h.postDelayed(new Runnable() {
                    @Override
                    public void run() {
                        if (!connectionStopRequested && !isFinishing() && !isDestroyed()) setInputGrabState(true);
                    }
                }, 500);

                // Keep the display on
                getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);

                // Update GameManager state to indicate we're in game
                UiHelper.notifyStreamConnected(Game.this);

                // Sync local clipboard to host
                handleFocusChange(true);

                // Ensure overlay toggle button visibility is properly set
                setupOverlayToggleButton();

                hideSystemUi(1000);
            }
        });

        if (prefConfig.usbDriver) {
            // Start the USB driver
            bindService(new Intent(this, UsbDriverService.class),
                    usbDriverServiceConnection, Service.BIND_AUTO_CREATE);
        }

        // Report this shortcut being used (off the main thread to prevent ANRs)
        ComputerDetails computer = new ComputerDetails();
        computer.name = pcName;
        computer.uuid = Game.this.getIntent().getStringExtra(EXTRA_PC_UUID);
        ShortcutHelper shortcutHelper = new ShortcutHelper(this);
        shortcutHelper.reportComputerShortcutUsed(computer);
        if (appName != null) {
            // This may be null if launched from the "Resume Session" PC context menu item
            shortcutHelper.reportGameLaunched(computer, app);
        }
    }

    @Override
    public void displayMessage(final String message) {
        runOnUiThread(new Runnable() {
            @Override
            public void run() {
                Toast.makeText(Game.this, message, Toast.LENGTH_LONG).show();
            }
        });
    }

    @Override
    public void displayTransientMessage(final String message) {
        if (!prefConfig.disableWarnings) {
            runOnUiThread(new Runnable() {
                @Override
                public void run() {
                    Toast.makeText(Game.this, message, Toast.LENGTH_LONG).show();
                }
            });
        }
    }

    @Override
    public void rumble(short controllerNumber, short lowFreqMotor, short highFreqMotor) {
        LimeLog.info(String.format((Locale)null, "Rumble on gamepad %d: %04x %04x", controllerNumber, lowFreqMotor, highFreqMotor));

        controllerHandler.handleRumble(controllerNumber, lowFreqMotor, highFreqMotor);
    }

    @Override
    public void rumbleTriggers(short controllerNumber, short leftTrigger, short rightTrigger) {
        LimeLog.info(String.format((Locale)null, "Rumble on gamepad triggers %d: %04x %04x", controllerNumber, leftTrigger, rightTrigger));

        controllerHandler.handleRumbleTriggers(controllerNumber, leftTrigger, rightTrigger);
    }

    @Override
    public void setHdrMode(boolean enabled, byte[] hdrMetadata) {
        LimeLog.info("Display HDR mode: " + (enabled ? "enabled" : "disabled"));
        decoderRenderer.setHdrMode(enabled, hdrMetadata);
    }

    @Override
    public void setMotionEventState(short controllerNumber, byte motionType, short reportRateHz) {
        controllerHandler.handleSetMotionEventState(controllerNumber, motionType, reportRateHz);
    }

    @Override
    public void setControllerLED(short controllerNumber, byte r, byte g, byte b) {
        controllerHandler.handleSetControllerLED(controllerNumber, r, g, b);
    }

    @Override
    public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) {
        if (!surfaceCreated) {
            throw new IllegalStateException("Surface changed before creation!");
        }

        LimeLog.info("surfaceChanged-->"+width+" x "+height + "----"+displayWidth+" x "+displayHeight);

        if (!attemptedConnection) {
            attemptedConnection = true;
            connecting = true;

            // Update GameManager state to indicate we're "loading" while connecting
            UiHelper.notifyStreamConnecting(Game.this);

            decoderRenderer.setRenderTarget(holder.getSurface());
            boolean muteClientAudio = RtStreamSession.TABLET.equals(streamHubMode)
                    || (isHubManagedSession() && !com.limelight.streamhub.HubAudioPolicy.playOnAndroid(streamHubMode, prefConfig.audioOutputMode));
            if (hubStartupTrace != null) {
                conn.setStartupTimeline(new NvConnection.StartupTimeline() {
                    @Override public void stageStarted(String key) { hubStartupTrace.stageStarted(key); }
                    @Override public void stageCompleted(String key) { hubStartupTrace.stageCompleted(key); }
                });
                hubStartupTrace.event("connection-start-requested");
            }
            conn.start(muteClientAudio ? new RtStreamSession.MutedAudioRenderer() : new AndroidAudioRenderer(Game.this, prefConfig.enableAudioFx),
                    decoderRenderer, Game.this);
        }

        panZoomHandler.handleSurfaceChange();

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            if (!isInPictureInPictureMode()) {
                updatePipAutoEnter();
            }
        }
    }

    @Override
    public void surfaceCreated(SurfaceHolder holder) {
        surfaceCreated = true;

        // Re-submit the mode after the stream Surface is attached. Android may ignore a window
        // mode request made before the content Surface exists, especially when smart adaptation
        // has left the user preference unset.
        requestHubPanelMode("surface-created");
        applySurfaceFrameRate(holder.getSurface(), "surface-created");
    }

    @Override
    public void surfaceDestroyed(SurfaceHolder holder) {
        if (!surfaceCreated) {
            throw new IllegalStateException("Surface destroyed before creation!");
        }

        if (attemptedConnection) {
            // Let the decoder know immediately that the surface is gone
            decoderRenderer.prepareForStop();

            if (connected) {
                stopConnection();
            }
        }
    }

    @Override
    public void mouseMove(int deltaX, int deltaY) {
        conn.sendMouseMove((short) deltaX, (short) deltaY);
    }

    @Override
    public void mouseButtonEvent(int buttonId, boolean down) {
        byte buttonIndex;

        switch (buttonId)
        {
            case EvdevListener.BUTTON_LEFT:
                buttonIndex = MouseButtonPacket.BUTTON_LEFT;
                break;
            case EvdevListener.BUTTON_MIDDLE:
                buttonIndex = MouseButtonPacket.BUTTON_MIDDLE;
                break;
            case EvdevListener.BUTTON_RIGHT:
                buttonIndex = MouseButtonPacket.BUTTON_RIGHT;
                break;
            case EvdevListener.BUTTON_X1:
                buttonIndex = MouseButtonPacket.BUTTON_X1;
                break;
            case EvdevListener.BUTTON_X2:
                buttonIndex = MouseButtonPacket.BUTTON_X2;
                break;
            default:
                LimeLog.warning("Unhandled button: "+buttonId);
                return;
        }

        if (down) {
            conn.sendMouseButtonDown(buttonIndex);
        }
        else {
            conn.sendMouseButtonUp(buttonIndex);
        }
    }

    @Override
    public void mouseVScroll(byte amount) {
        conn.sendMouseScroll(amount);
    }

    @Override
    public void mouseHScroll(byte amount) {
        conn.sendMouseHScroll(amount);
    }

    @Override
    public void keyboardEvent(boolean buttonDown, short keyCode) {
        short keyMap = keyboardTranslator.translate(keyCode, 0, -1);
        if (keyMap != 0) {
            // handleSpecialKeys() takes the Android keycode
            if (handleSpecialKeys(keyCode, buttonDown)) {
                return;
            }

            if (buttonDown) {
                conn.sendKeyboardInput(keyMap, KeyboardPacket.KEY_DOWN, getModifierState(), (byte)0);
            }
            else {
                conn.sendKeyboardInput(keyMap, KeyboardPacket.KEY_UP, getModifierState(), (byte)0);
            }
        }
    }

    @Override
    public void onSystemUiVisibilityChange(int visibility) {
        // Don't do anything if we're not connected
        if (!connected) {
            return;
        }

        // This flag is set for all devices
        if ((visibility & View.SYSTEM_UI_FLAG_FULLSCREEN) == 0) {
            hideSystemUi(2000);
        }
        else if ((visibility & View.SYSTEM_UI_FLAG_HIDE_NAVIGATION) == 0) {
            hideSystemUi(2000);
        }
    }

    @Override
    public boolean needsPerfUpdates() { return isHubManagedSession(); }

    private volatile HubNetworkLadder hubNetworkLadder;
    private volatile HubStreamLoadPolicy hubStreamLoadPolicy;
    @Override
    public boolean needsNetworkSamples() { return isHubManagedSession() && hubNetworkLadder != null; }

    @Override
    public void onNetworkSample(long nowMs, int lostFrames, int totalFrames, int rttMs) {
        HubNetworkLadder ladder = hubNetworkLadder;
        if (ladder != null) {
            ladder.sample(nowMs, lostFrames, totalFrames, rttMs);
            updateHubManagedNetworkWarning();
        }
    }

    private void updateHubManagedNetworkWarning() {
        HubNetworkLadder ladder = hubNetworkLadder;
        boolean degraded = ladder != null && ladder.isNetworkDegraded();
        HubStreamLoadPolicy loadPolicy = hubStreamLoadPolicy;
        boolean streamLoad = loadPolicy != null && loadPolicy.isHighLoad();
        runOnUiThread(() -> {
            if (connectionStopRequested) return;
            setHubConnectionState(degraded ? "已连接 · 网络持续拥塞"
                    : streamLoad ? "已连接 · 串流负载较高" : "已连接");
            boolean networkVisible = hubWarnings == null ? degraded : hubWarnings.networkVisible(degraded);
            boolean loadVisible = hubWarnings == null ? streamLoad : hubWarnings.loadVisible(streamLoad);
            requestedNotificationOverlayVisibility = (networkVisible || loadVisible) && !prefConfig.disableWarnings
                    ? View.VISIBLE : View.GONE;
            if (!isHidingOverlays && notificationOverlayView != null) {
                if (networkVisible) {
                    int recommendedKbps = ladder == null ? prefConfig.bitrate : ladder.recommendationKbps();
                    notificationOverlayView.setText(recommendedKbps < prefConfig.bitrate
                            ? getString(R.string.streamhub_network_load_msg,
                                    Math.max(1, Math.round(recommendedKbps / 1000f)))
                            : getString(R.string.streamhub_network_congested_msg));
                }
                else if (loadVisible) {
                    notificationOverlayView.setText(getResources().getString(R.string.streamhub_stream_load_msg));
                }
                notificationOverlayView.setVisibility(requestedNotificationOverlayVisibility);
            }
        });
    }

    @Override
    public void onVideoSample(float receivedFps, float renderedFps, float decodeMs, int rttMs, int lostFrames, int totalFrames) {
        if (!isHubManagedSession()) return;
        Display currentDisplay = currentStreamingDisplay();
        if (currentDisplay == null) return;
        float panelHz = currentDisplay.getRefreshRate();
        HubStreamLoadPolicy loadPolicy = hubStreamLoadPolicy;
        HubNetworkLadder ladder = hubNetworkLadder;
        if (loadPolicy != null) {
            loadPolicy.sample(ladder != null && ladder.isNetworkDegraded(), prefConfig.fps,
                    panelHz, receivedFps, renderedFps, decodeMs, rttMs, lostFrames, totalFrames);
            updateHubManagedNetworkWarning();
        }
        int panelModeId = currentDisplay.getMode().getModeId();
        int requestedPanelModeId = getWindow().getAttributes().preferredDisplayModeId;
        runOnUiThread(() -> com.limelight.streamhub.HubSessionDiagnostics.performance(this, prefConfig.fps,
                panelHz, desiredRefreshRate, panelModeId, requestedPanelModeId,
                getIntent().getIntExtra("HubPreparedFps", 0),
                receivedFps, renderedFps, decodeMs, rttMs, lostFrames, totalFrames));
    }

    private Display currentStreamingDisplay() {
        Display fallback = hubStreamingDisplay;
        if (fallback == null) return null;
        DisplayManager manager = (DisplayManager) getSystemService(DISPLAY_SERVICE);
        Display current = manager == null ? null : manager.getDisplay(fallback.getDisplayId());
        return current == null ? fallback : current;
    }

    @Override
    public void onPerfUpdate(final String text) {
        runOnUiThread(new Runnable() {
            @Override
            public void run() {
                String overlayText = isHubManagedSession()
                        ? text.replace(prefConfig.width + "x" + prefConfig.height, displayWidth + "x" + displayHeight)
                        : text;
                if (isHubManagedSession()) { hubLastPerfText = overlayText; hubLastPerfAt = android.os.SystemClock.elapsedRealtime(); }
                if (!prefConfig.enablePerfOverlay) return;
                if(prefConfig.enablePerfOverlayLite){
                    performanceOverlayLite.setText(overlayText);
                }else{
                    performanceOverlayBig.setText(overlayText);
                }
            }
        });
    }

    @Override
    public void onUsbPermissionPromptStarting() {
        // Disable PiP auto-enter while the USB permission prompt is on-screen. This prevents
        // us from entering PiP while the user is interacting with the OS permission dialog.
        suppressPipRefCount++;
        updatePipAutoEnter();
    }

    @Override
    public void onUsbPermissionPromptCompleted() {
        suppressPipRefCount--;
        updatePipAutoEnter();
    }

    @Override
    public boolean onKey(View view, int keyCode, KeyEvent keyEvent) {
        switch (keyEvent.getAction()) {
            case KeyEvent.ACTION_DOWN:
                return handleKeyDown(keyEvent);
            case KeyEvent.ACTION_UP:
                return handleKeyUp(keyEvent);
            case KeyEvent.ACTION_MULTIPLE:
                return handleKeyMultiple(keyEvent);
            default:
                return false;
        }
    }

    @Override
    public void onBackPressed() {
        if (isHubManagedSession() && streamHubInputToolbar != null) {
            // Back is a session action for the unified client. Close an open
            // local panel first; when the desktop view is unobstructed, leave
            // the session directly instead of opening the legacy game menu.
            if (!streamHubInputToolbar.collapseOrConsumeBack()) {if(hubSessionLease!=null)hubSessionLease.userExit();disconnect();}
            return;
        }
        if(prefConfig.enableBackMenu){
            showGameMenu(null);
            return;
        }
        super.onBackPressed();
    }

    public void sendExecServerCmd(int cmdId) {
        conn.sendExecServerCmd(cmdId);
    }

    public ArrayList<String> getServerCmds() {
        return serverCommands;
    }

    public boolean isZoomModeEnabled() {
        return isPanZoomMode;
    }
    public void toggleZoomMode() {
        this.isPanZoomMode = !this.isPanZoomMode;
        if (this.isPanZoomMode) {
            Toast.makeText(this, getString(R.string.pan_zoom_mode_enabled), Toast.LENGTH_SHORT).show();
        } else {
            Toast.makeText(this, getString(R.string.pan_zoom_mode_disabled), Toast.LENGTH_SHORT).show();
        }
        updateZoomButtonAppearance();

        if (ExternalDisplayControlActivity.instance != null) {
            ExternalDisplayControlActivity.instance.toggleZoomMode(false);
        }
    }

    public void rotateScreen() {
        if (currentOrientation == Configuration.ORIENTATION_LANDSCAPE) {
            currentOrientation = Configuration.ORIENTATION_PORTRAIT;
            setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_USER_PORTRAIT);
        } else {
            currentOrientation = Configuration.ORIENTATION_LANDSCAPE;
            setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_USER_LANDSCAPE);
        }
    }

    /**
     * Initializes and applies the appropriate mouse mode based on user preferences
     * and whether the app is running in secondary display mode (such as Samsung DeX).
     *
     * Behavior:
     * - If the app is in secondary display mode:
     *   - Applies the user's saved mouse mode if it's one of the supported modes:
     *     "Trackpad Natural", "Trackpad Gaming", or "Disabled".
     *   - Otherwise, defaults to applying the "Trackpad Natural" mode.
     *
     * - If the app is not in secondary display mode:
     *   - Applies the user's saved mouse mode as is.
     *
     * This ensures the correct input mode is applied depending on the environment,
     * improving compatibility with desktop-like multi-display modes.
     */
    private void initMouseMode() {
        if (isHubManagedSession()) {
            int mode = getSharedPreferences("streamhub_controls", MODE_PRIVATE).getInt("input_mode", 0);
            applyMouseMode(mode == 1 ? 0 : mode == 2 ? 2 : 1);
            return;
        }
        String[] mouseModes = getResources().getStringArray(R.array.mouse_mode_names);

        String savedMouseModeIndexStr = ProfilesManager.getInstance()
                .getOverlayingSharedPreferences(this)
                .getString("mouse_mode_list", "0");

        int savedMouseModeIndex;
        try {
            savedMouseModeIndex = Integer.parseInt(savedMouseModeIndexStr);
        } catch (NumberFormatException e) {
            savedMouseModeIndex = 0;
        }

        String savedMouseModeString = (savedMouseModeIndex >= 0 && savedMouseModeIndex < mouseModes.length)
                ? mouseModes[savedMouseModeIndex]
                : null;

        String natural = getString(R.string.mouse_mode_track_pad_natural);
        String gaming = getString(R.string.mouse_mode_track_pad_gaming);
        String disabled = getString(R.string.mouse_mode_disabled);

        int naturalIndex = 2; //fallback natural mode for secondary screen
        for (int i = 0; i < mouseModes.length; i++) {
            if (mouseModes[i].equals(natural)) {
                naturalIndex = i;
                break;
            }
        }
        // We only want to temporary override the mouse mode to work with external, but not store it
        if (isOnExternalDisplay()) {
            if (savedMouseModeString != null &&
                    (savedMouseModeString.equals(natural) ||
                            savedMouseModeString.equals(gaming) ||
                            savedMouseModeString.equals(disabled))) {
                applyMouseMode(savedMouseModeIndex);
            } else {
                applyMouseMode(naturalIndex);
            }
        } else {
            applyMouseMode(savedMouseModeIndex);
        }
    }

    /**
     * Displays a dialog allowing the user to select a mouse input mode.
     *
     * Behavior:
     * - On regular displays, all available mouse modes are shown.
     * - On secondary displays (e.g. Samsung DeX), only a limited set of modes are allowed:
     *   "Trackpad Natural", "Trackpad Gaming", and "Disabled".
     * - An additional option to toggle the local mouse cursor is always included.
     *
     * When the user selects a mode:
     * - If it's the toggle option, the local mouse cursor mode is toggled.
     * - Otherwise, the selected mode is applied and saved to shared preferences.
     *
     * @param context The context to use to decide where to show the dialog.
     */
    public void selectMouseMode(Context context){
        String[] allModes = getResources().getStringArray(R.array.mouse_mode_names);

        Set<String> allowedLabels = new HashSet<>(Arrays.asList(
                getString(R.string.mouse_mode_track_pad_natural),
                getString(R.string.mouse_mode_track_pad_gaming),
                getString(R.string.mouse_mode_disabled)
        ));

        List<MouseModeOption> options = new ArrayList<>();

        for (int i = 0; i < allModes.length; i++) {
            String label = allModes[i];
            boolean isAllowed = !isOnExternalDisplay() || allowedLabels.contains(label);
            if (isAllowed) {
                options.add(new MouseModeOption(i, label));
            }
        }

        options.add(new MouseModeOption(-1, getString(R.string.toggle_local_mouse_cursor)));

        String[] labels = new String[options.size()];
        for (int i = 0; i < options.size(); i++) {
            labels[i] = options.get(i).label;
        }
        final MouseModeOption[] optionArray = options.toArray(new MouseModeOption[0]);

        (isHubManagedSession() ? HubStreamUi.dialog(context) : new AlertDialog.Builder(context))
                .setTitle(getString(R.string.game_menu_select_mouse_mode))
                .setItems(labels, (dialog, which) -> {
                    dialog.dismiss();
                    MouseModeOption selected = optionArray[which];
                    if (selected.index == -1) {
                        toggleMouseLocalCursor();
                    } else {
                        applyMouseMode(selected.index);
                        ProfilesManager.getInstance().getOverlayingSharedPreferences(this)
                                .edit()
                                .putString("mouse_mode_list", String.valueOf(selected.index))
                                .apply();
                    }
                })
                .create()
                .show();
    }

    //本地鼠标光标切换
    private void toggleMouseLocalCursor(){
        if (!grabbedInput) {
            inputCaptureProvider.enableCapture();
            grabbedInput = true;
        }
        cursorVisible = !cursorVisible;
        if (cursorVisible) {
            inputCaptureProvider.showCursor();
        } else {
            inputCaptureProvider.hideCursor();
        }
    }

    private void applyMouseMode(int mode) {
        switch (mode) {
            case 0: // Multi-touch
            prefConfig.enableMultiTouchScreen = true;
            prefConfig.touchscreenTrackpad = false;
            break;
            case 1: // Normal mouse
            case 5: // Normal mouse with swapped buttons
                prefConfig.enableMultiTouchScreen = false;
                prefConfig.touchscreenTrackpad = false;
                break;
            case 2: // Trackpad (natural)
            case 3: // Trackpad (gaming)
                prefConfig.enableMultiTouchScreen = false;
                prefConfig.touchscreenTrackpad = true;
                break;
            case 4: // Touch mouse disabled
                break;
            default:
                break;
        }

        //Initialize touch contexts
        for (int i = 0; i < touchContextMap.length; i++) {
            if (touchContextMap[i] != null) touchContextMap[i].cancelTouch();
            if (mode == 4) {
                // Touch mouse disabled
                touchContextMap[i] = null;
            } else if (!prefConfig.touchscreenTrackpad) {
                touchContextMap[i] = new AbsoluteTouchContext(conn, i, streamView, mode == 5);
            } else if (mode == 3) {
                touchContextMap[i] = new RelativeTouchContext(conn, i, REFERENCE_HORIZ_RES, REFERENCE_VERT_RES, streamView, prefConfig);
            } else {
                touchContextMap[i] = new TrackpadContext(conn, i);
            }
        }

        // Always exit zoom mode if mouse mode has changed
        isPanZoomMode = false;
        updateZoomButtonAppearance();
    }

    public void toggleHUD() {
        prefConfig.enablePerfOverlay = !prefConfig.enablePerfOverlay;
        if (prefConfig.enablePerfOverlay) {
            performanceOverlayView.setVisibility(View.VISIBLE);
            if(prefConfig.enablePerfOverlayLite){
                performanceOverlayLite.setVisibility(View.VISIBLE);
            }else{
                performanceOverlayBig.setVisibility(View.VISIBLE);
            }
        } else {
            performanceOverlayView.setVisibility(View.GONE);
        }
        if (streamHubInputToolbar != null) streamHubInputToolbar.setStatsVisible(prefConfig.enablePerfOverlay);
    }

    /** Show current stream metrics without requiring a persistent overlay. */
    public void showStreamDiagnostics() {
        String stats = hubLastPerfText == null || hubLastPerfText.trim().isEmpty()
                ? "实时统计尚未收到，请保持连接几秒后重试。" : hubLastPerfText;
        String mode = RtStreamSession.TABLET.equals(streamHubMode) ? "副屏" : "远程控制";
        String route = host == null || host.trim().isEmpty() ? "自动" : host;
        String message = "模式：" + mode
                + "\n连接：" + (hubConnectionState == null ? "未知" : hubConnectionState)
                + "\n目标：" + route
                + "\n画面：" + displayWidth + " × " + displayHeight
                + "\n请求帧率：" + prefConfig.fps + " FPS（实际以接收端统计为准）"
                + "\n当前面板：" + hubStreamingDisplay.getMode().getPhysicalWidth() + " × " + hubStreamingDisplay.getMode().getPhysicalHeight()
                + " / " + String.format(java.util.Locale.ROOT, "%.2f", hubStreamingDisplay.getRefreshRate()) + " Hz"
                + "\n面板模式：" + hubStreamingDisplay.getMode().getModeId()
                + "\n刷新率状态：" + (HubDisplayModePolicy.isApplied(hubStreamingDisplay.getRefreshRate(), desiredRefreshRate,
                        hubStreamingDisplay.getMode().getModeId(), getWindow().getAttributes().preferredDisplayModeId)
                        ? "已应用" : "系统调度")
                + "\n目标码率：" + (prefConfig.bitrate / 1000) + " Mbps"
                + (hubNetworkLadder == null ? "" : "\n网络建议码率：" + (hubNetworkLadder.recommendationKbps() / 1000f) + " Mbps（需下次连接时调整）")
                + (hubLastPerfAt == 0 ? "" : "\n统计时间：" + Math.max(0, (android.os.SystemClock.elapsedRealtime() - hubLastPerfAt) / 1000) + " 秒前")
                + "\n\n实时统计\n" + stats;
        if (isHubManagedSession()) {
            HubStreamUi.dialog(this).setTitle("网络与串流诊断")
                    .setView(HubStreamUi.diagnosticsView(this, message))
                    .setPositiveButton("关闭", null).show();
        } else {
            new AlertDialog.Builder(this).setTitle("网络与串流诊断").setMessage(message)
                    .setPositiveButton("关闭", null).show();
        }
    }

    /** Configure where remote audio should play on the next connection. */
    public void showAudioOutputSettings() {
        if (RtStreamSession.TABLET.equals(streamHubMode)) {
            (isHubManagedSession() ? HubStreamUi.dialog(this) : new AlertDialog.Builder(this))
                    .setTitle("音频输出")
                    .setMessage("副屏模式默认不输出音频，电脑扬声器保持本机播放。")
                    .setPositiveButton("知道了", null)
                    .show();
            return;
        }
        final String[] choices = {"仅平板扬声器（接管电脑输出设备）", "仅电脑扬声器", "电脑和平板同时输出"};
        int selected = Math.max(0, Math.min(2, prefConfig.audioOutputMode));
        (isHubManagedSession() ? HubStreamUi.dialog(this) : new AlertDialog.Builder(this))
                .setTitle("音频输出")
                .setSingleChoiceItems(choices, selected, (dialog, which) -> {
                    Runnable applyOutput = () -> {
                    prefConfig.audioOutputMode = which;
                    boolean playOnComputer = which != 0;
                    prefConfig.playHostAudio = playOnComputer;
                    (isHubManagedSession() ? androidx.preference.PreferenceManager.getDefaultSharedPreferences(this)
                            : ProfilesManager.getInstance().getOverlayingSharedPreferences(this))
                            .edit().putBoolean("checkbox_host_audio", playOnComputer)
                            .putString("streamhub_audio_output", which == 0 ? "tablet" : which == 1 ? "computer" : "both")
                            .apply();
                    Toast.makeText(this, "音频输出将在下次连接时生效", Toast.LENGTH_SHORT).show();
                    dialog.dismiss();
                    };
                    if (which == 0 && isHubManagedSession()) {
                        HubStreamUi.dialog(this).setTitle("仅在平板播放声音？")
                                .setMessage("串流期间会接管电脑的默认音频设备，电脑手动切换可能被引擎切回。需要保留电脑自由切换时，请选电脑输出或同时输出。断开后恢复原设备。")
                                .setNegativeButton("取消", null).setPositiveButton("仅平板输出", (d, w) -> applyOutput.run()).show();
                    } else applyOutput.run();
                })
                .setNegativeButton("取消", null)
                .show();
    }

    //切换触控灵敏度开关
    public void switchTouchSensitivity(){
        prefConfig.enableTouchSensitivity = !prefConfig.enableTouchSensitivity;
    }

    public void disconnect() {
        if (prefConfig.smartClipboardSync && !isHubManagedSession()) {
            getClipboard(-1);
        }
        finish();
    }

    public void quit() {
        Context context;
        if (isOnExternalDisplay() && ExternalDisplayControlActivity.instance != null) {
            context = ExternalDisplayControlActivity.instance;
        } else {
            context = this;
        }
        AlertDialog.Builder builder = new AlertDialog.Builder(context);
        builder.setTitle(R.string.game_dialog_title_quit_confirm);
        builder.setMessage(R.string.game_dialog_message_quit_confirm);

        builder.setPositiveButton(getString(R.string.yes), (dialog, which) -> {
            quitOnStop = true;
            dialog.dismiss();
            finish();
        });

        builder.setNegativeButton(getString(R.string.no), (dialog, which) -> dialog.dismiss());

        AlertDialog dialog = builder.create();
        dialog.show();
    }

    @Override
    public void showGameMenu(GameInputDevice device) {
        if (isHubManagedSession() && streamHubInputToolbar != null) {
            if (!streamHubInputToolbar.collapseOrConsumeBack()) streamHubInputToolbar.show();
            return;
        }
        if(isOnExternalDisplay()) {
            ExternalDisplayControlActivity.toggleGameMenu();
        } else {
            if (gameMenuCallbacks != null) {
                gameMenuCallbacks.showMenu(device);
            }
        }
    }

    public void hideGameMenu() {
        if (isHubManagedSession() && streamHubInputToolbar != null) streamHubInputToolbar.collapseOrConsumeBack();
        if (gameMenuCallbacks != null) {
            gameMenuCallbacks.hideMenu();
        }
    }

    private void updateFloatingButtonVisibility(boolean show) {
        floatingMenuButton.setVisibility(show && !(isHubManagedSession() && !onExternelDisplay) ? View.VISIBLE : View.GONE);
    }

    public void toggleFloatingButtonVisibility() {
        if (floatingMenuButton != null) {
            updateFloatingButtonVisibility(floatingMenuButton.getVisibility() == View.GONE);
        }
    }


    // 设置surfaceView的圆角 setSurfaceviewCorner(UiHelper.dpToPx(this,24));
    private void setSurfaceviewCorner(final float radius) {

        streamView.setOutlineProvider(new ViewOutlineProvider() {
            @Override
            public void getOutline(View view, Outline outline) {
                Rect rect = new Rect();
                view.getGlobalVisibleRect(rect);
                int leftMargin = 0;
                int topMargin = 0;
                Rect selfRect = new Rect(leftMargin, topMargin, rect.right - rect.left - leftMargin, rect.bottom - rect.top - topMargin);
                outline.setRoundRect(selfRect, radius);
            }
        });
        streamView.setClipToOutline(true);
    }

    @Override
    public boolean handleCommitText(CharSequence text) {
        if (!prefConfig.enableCommitText || conn == null || !connected) {
            return false;
        }
        enqueueCommitText(text.toString());
        return true;
    }

    @Override
    public boolean handleDeleteSurroundingText(int beforeLength, int afterLength) {
        if (!prefConfig.enableCommitText || conn == null || !connected) {
            return false;
        }
        // Send backspace events for deleted preceding characters
        if (beforeLength > 0) {
            short backspaceCode = keyboardTranslator.translate(KeyEvent.KEYCODE_DEL, 0, -1);
            for (int i = 0; i < Math.min(beforeLength, 512); i++) {
                conn.sendKeyboardInput(backspaceCode, com.limelight.nvstream.input.KeyboardPacket.KEY_DOWN, (byte)0, (byte)0);
                conn.sendKeyboardInput(backspaceCode, com.limelight.nvstream.input.KeyboardPacket.KEY_UP, (byte)0, (byte)0);
            }
        }
        if (afterLength > 0) {
            short deleteCode = keyboardTranslator.translate(KeyEvent.KEYCODE_FORWARD_DEL, 0, -1);
            for (int i = 0; i < Math.min(afterLength, 512); i++) {
                conn.sendKeyboardInput(deleteCode, KeyboardPacket.KEY_DOWN, (byte)0, (byte)0);
                conn.sendKeyboardInput(deleteCode, KeyboardPacket.KEY_UP, (byte)0, (byte)0);
            }
        }
        return true;
    }

    private void enqueueCommitText(String text) {
        if (text == null || text.length() == 0 || !connected) {
            return;
        }
        byte[] utf8 = text.getBytes(StandardCharsets.UTF_8);
        int chunkSize = isHubManagedSession() ? 24 : UTF8_CHUNK_SIZE;
        int offset = 0;
        while (offset < utf8.length) {
            int end = Math.min(offset + chunkSize, utf8.length);
            // Ensure we don't cut inside a multi-byte sequence
            while (end < utf8.length && (utf8[end] & 0xC0) == 0x80) {
                end--; // step back until we are at start of code point
            }
            String chunk = new String(utf8, offset, end - offset, StandardCharsets.UTF_8);
            commitTextQueue.add(chunk);
            offset = end;
        }
        // Kick off flushing if not already scheduled
        commitTextHandler.removeCallbacks(flushCommitTextQueue);
        commitTextHandler.post(flushCommitTextQueue);
    }
}
