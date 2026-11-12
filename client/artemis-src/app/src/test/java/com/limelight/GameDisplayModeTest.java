package com.limelight;

import android.content.Intent;
import android.os.Build;
import android.view.Display;

import com.limelight.preferences.PreferenceConfiguration;
import com.limelight.streamhub.RtStreamSession;

import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.Robolectric;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.annotation.Config;
import org.robolectric.shadow.api.Shadow;
import org.robolectric.shadows.ShadowDisplay;
import org.robolectric.shadows.ShadowDisplayManager;
import org.robolectric.util.ReflectionHelpers;

import static org.junit.Assert.assertEquals;
import static org.robolectric.Shadows.shadowOf;
import static org.robolectric.util.ReflectionHelpers.ClassParameter.from;

@RunWith(RobolectricTestRunner.class)
@Config(sdk = 28, manifest = Config.NONE, application = android.app.Application.class)
public class GameDisplayModeTest {
    @Test public void managedTabletRequestsExactRefreshModeOnLanAndRemoteRoutes() {
        for (String route : new String[]{"lan", "remote"}) {
            Game game = game(true, RtStreamSession.TABLET, route, 90);
            Display display = display(60, mode(1, 60), mode(2, 90));

            assertEquals(90f, prepareOnAndroidS(game, display), 0.01f);
            assertEquals(2, game.getWindow().getAttributes().preferredDisplayModeId);
            assertEquals(90f, game.getWindow().getAttributes().preferredRefreshRate, 0.01f);
        }
    }

    @Test public void managedTabletRequestsNinetyHertzForStreamNegotiationWhileSystemSwitches() {
        Game game = game(true, RtStreamSession.TABLET, "usb", 90);
        Display display = display(60, mode(1, 60), mode(2, 90));

        assertEquals(90f, prepareOnAndroidS(game, display), 0.01f);
        assertEquals(2, game.getWindow().getAttributes().preferredDisplayModeId);
        assertEquals(90f, (Float) ReflectionHelpers.getField(game, "desiredRefreshRate"), 0.01f);
        assertEquals(60f, (Float) ReflectionHelpers.getField(game, "clientRefreshRate"), 0.01f);
    }

    @Test public void managedTabletRequestsNegotiatedSixtyRatherThanNinety() {
        Game game = game(true, RtStreamSession.TABLET, "usb", 60);
        Display display = display(90, mode(1, 90), mode(2, 60));

        assertEquals(60f, prepareOnAndroidS(game, display), 0.01f);
        assertEquals(2, game.getWindow().getAttributes().preferredDisplayModeId);
    }

    @Test public void managedTabletRetainsPreferenceWhenMatchingModeIsAlreadyActive() {
        Game game = game(true, RtStreamSession.TABLET, "usb", 90);
        Display display = display(90, mode(1, 90), mode(2, 60));

        prepareOnAndroidS(game, display);
        assertEquals(1, game.getWindow().getAttributes().preferredDisplayModeId);
        assertEquals(90f, (Float) ReflectionHelpers.getField(game, "clientRefreshRate"), 0.01f);
    }

    @Test public void managedRemoteControlRequestsExactPanelMode() {
        Game game = game(true, RtStreamSession.REMOTE, "lan", 90);
        assertEquals(90f, prepareOnAndroidS(game, display(60, mode(1, 60), mode(2, 90))), 0.01f);
        assertEquals(2, game.getWindow().getAttributes().preferredDisplayModeId);
        assertEquals(90f, game.getWindow().getAttributes().preferredRefreshRate, 0.01f);
    }

    @Test public void managedSessionReappliesRememberedModeAfterWindowAttachment() {
        Game game = game(true, RtStreamSession.TABLET, "lan", 90);
        Display display = display(60, mode(1, 60), mode(2, 90));
        prepareOnAndroidS(game, display);
        ReflectionHelpers.setField(game, "hubStreamingDisplay", display);

        display(60, mode(4, 60), mode(8, 90));

        android.view.WindowManager.LayoutParams params = game.getWindow().getAttributes();
        params.preferredDisplayModeId = 0;
        params.preferredRefreshRate = 0;
        game.getWindow().setAttributes(params);

        ReflectionHelpers.callInstanceMethod(game, "requestHubPanelMode",
                from(String.class, "test-reattach"));
        assertEquals(8, game.getWindow().getAttributes().preferredDisplayModeId);
        assertEquals(90f, game.getWindow().getAttributes().preferredRefreshRate, 0.01f);
    }

    @Test public void unmanagedSessionsKeepSetFrameRatePath() {
        Game game = game(false, RtStreamSession.TABLET, "usb", 90);
        prepareOnAndroidS(game, display(60, mode(1, 60), mode(2, 90)));
        assertEquals(0, game.getWindow().getAttributes().preferredDisplayModeId);
    }

    @Test public void unmatchedManagedRequestUsesSameFallbackAsWireless() {
        Game usb = game(true, RtStreamSession.TABLET, "usb", 75);
        Game wireless = game(true, RtStreamSession.TABLET, "lan", 75);
        Display display = display(60, mode(1, 60), mode(2, 90));

        assertEquals(prepareOnAndroidS(wireless, display), prepareOnAndroidS(usb, display), 0.01f);
        assertEquals(wireless.getWindow().getAttributes().preferredDisplayModeId,
                usb.getWindow().getAttributes().preferredDisplayModeId);
        assertEquals((Float) ReflectionHelpers.getField(wireless, "desiredRefreshRate"),
                (Float) ReflectionHelpers.getField(usb, "desiredRefreshRate"), 0.01f);
    }

    private static Game game(boolean managed, String mode, String route, float fps) {
        Intent intent = new Intent().putExtra("HubManaged", managed).putExtra("HubRouteType", route);
        Game game = Robolectric.buildActivity(Game.class, intent).get();
        PreferenceConfiguration preferences = new PreferenceConfiguration();
        preferences.width = 2064;
        preferences.height = 3096;
        preferences.fps = fps;
        preferences.videoScaleMode = PreferenceConfiguration.ScaleMode.STRETCH;
        ReflectionHelpers.setField(game, "prefConfig", preferences);
        ReflectionHelpers.setField(game, "streamHubMode", mode);
        return game;
    }

    private static Display.Mode mode(int id, float rate) {
        return ReflectionHelpers.callConstructor(Display.Mode.class, from(int.class, id),
                from(int.class, 2064), from(int.class, 3096), from(float.class, rate));
    }

    @SuppressWarnings("unchecked")
    private static Display display(float currentRate, Display.Mode... modes) {
        ShadowDisplayManager.setSupportedModes(Display.DEFAULT_DISPLAY, modes);
        Display display = ShadowDisplay.getDefaultDisplay();
        Object global = ReflectionHelpers.getField(display, "mGlobal");
        Object info = ReflectionHelpers.callInstanceMethod(global, "getDisplayInfo", from(int.class, Display.DEFAULT_DISPLAY));
        ReflectionHelpers.setField(info, "modeId", modes[0].getModeId());
        ReflectionHelpers.callInstanceMethod(Shadow.extract(global), "changeDisplay",
                from(int.class, Display.DEFAULT_DISPLAY), from((Class<Object>) info.getClass(), info));
        shadowOf(display).setRefreshRate(currentRate);
        return display;
    }

    private static float prepareOnAndroidS(Game game, Display display) {
        int originalSdk = Build.VERSION.SDK_INT;
        try {
            ReflectionHelpers.setStaticField(Build.VERSION.class, "SDK_INT", Build.VERSION_CODES.S);
            return ReflectionHelpers.callInstanceMethod(game, "prepareDisplayForRendering", from(Display.class, display));
        } finally {
            ReflectionHelpers.setStaticField(Build.VERSION.class, "SDK_INT", originalSdk);
        }
    }
}
