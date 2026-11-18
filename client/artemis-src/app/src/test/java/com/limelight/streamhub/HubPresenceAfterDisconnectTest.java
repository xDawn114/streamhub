package com.limelight.streamhub;

import android.content.Context;
import android.content.Intent;
import android.widget.TextView;
import com.limelight.Game;
import com.limelight.nvstream.NvConnection;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.util.concurrent.CountDownLatch;
import org.junit.After;
import org.junit.Before;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.Robolectric;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.annotation.Config;
import org.robolectric.util.ReflectionHelpers;
import static org.junit.Assert.*;
import static org.mockito.Mockito.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=28, application=android.app.Application.class)
public class HubPresenceAfterDisconnectTest {
    private Field barrierField;
    private CountDownLatch previousBarrier;
    private CountDownLatch cleanup;

    @Before public void prepare() throws Exception {
        RuntimeEnvironment.getApplication().getSharedPreferences("streamhub-device", Context.MODE_PRIVATE)
                .edit().clear().commit();
        barrierField = Game.class.getDeclaredField("hubConnectionStop");
        barrierField.setAccessible(true);
        previousBarrier = (CountDownLatch) barrierField.get(null);
        cleanup = new CountDownLatch(1);
        barrierField.set(null, cleanup);
    }

    @After public void restoreBarrier() throws Exception { barrierField.set(null, previousBarrier); }

    private Field field(String name) throws Exception {
        Field field = HubActivity.class.getDeclaredField(name); field.setAccessible(true); return field;
    }
    private void invoke(HubActivity activity, String name) throws Exception {
        Method method = HubActivity.class.getDeclaredMethod(name); method.setAccessible(true); method.invoke(activity);
    }

    @Test public void cleanupDoesNotRepublishOrProbeThePreDisconnectStreamingState() throws Exception {
        try (var controller = Robolectric.buildActivity(HubActivity.class).create()) {
            HubActivity activity = controller.get();
            // Invalid trust is deliberate: no test may contact a real computer.
            new HubStore(activity).put("invitation", "invalid-test-trust");
            field("computerOnline").setBoolean(activity, true);
            field("mediaState").set(activity, " · 正在串流");
            field("nextPresenceAt").setLong(activity, Long.MAX_VALUE);
            invoke(activity, "refreshComputerPresence");
            assertEquals("正在结束连接", ((TextView) field("deviceState").get(activity)).getText().toString());
            assertFalse(field("presenceChecking").getBoolean(activity));
            assertNull(field("presenceCoordinator").get(activity));
            assertEquals(0, field("nextPresenceAt").getLong(activity));
        }
    }

    @Test public void completedCleanupDoesNotHideAnotherRealStreamingSession() throws Exception {
        try (var controller = Robolectric.buildActivity(HubActivity.class).create()) {
            HubActivity activity = controller.get();
            field("computerOnline").setBoolean(activity, true);
            field("mediaState").set(activity, " · 正在串流");
            cleanup.countDown();
            assertFalse(Game.isHubConnectionStopping());
            invoke(activity, "renderDeviceState");
            assertEquals("在线 · 正在串流", ((TextView) field("deviceState").get(activity)).getText().toString());
            field("mediaState").set(activity, "");
            invoke(activity, "renderDeviceState");
            assertEquals("在线", ((TextView) field("deviceState").get(activity)).getText().toString());
        }
    }

    @Test public void statusWaitsForTheExistingCleanupBarrierWithoutBlockingUi() throws Exception {
        assertTrue(Game.isHubConnectionStopping());
        assertFalse(Game.awaitHubConnectionStop(0));
        cleanup.countDown();
        assertTrue(Game.awaitHubConnectionStop(0));
        assertFalse(Game.isHubConnectionStopping());
    }

    private Game game(NvConnection connection) {
        Game game = spy(Robolectric.buildActivity(Game.class, new Intent().putExtra("HubManaged", true)).get());
        // Attach the framework host without running Game.onCreate or starting native media.
        androidx.savedstate.SavedStateRegistryController savedState = ReflectionHelpers.getField(game, "mSavedStateRegistryController");
        savedState.performRestore(null);
        androidx.fragment.app.FragmentController fragments = ReflectionHelpers.getField(game, "mFragments");
        fragments.attachHost(null);
        doNothing().when(game).updatePipAutoEnter();
        doNothing().when(game).setMetaKeyCaptureState(anyBoolean());
        ReflectionHelpers.setField(game, "conn", connection);
        ReflectionHelpers.setField(game, "connected", true);
        ReflectionHelpers.setField(game, "attemptedConnection", true);
        return game;
    }

    private void pause(Game game) throws Exception {
        Method method = Game.class.getDeclaredMethod("onPause");
        method.setAccessible(true); method.invoke(game);
    }

    @Test public void finishingPublishesCleanupBeforeHomeCanResume() throws Exception {
        cleanup.countDown();
        CountDownLatch releaseStop = new CountDownLatch(1);
        NvConnection connection = mock(NvConnection.class);
        doAnswer(call -> { releaseStop.await(); return null; }).when(connection).stop();
        Game game = game(connection);
        try {
            game.finish(); pause(game);
            assertTrue("onPause publishes the barrier without waiting for onStop", Game.isHubConnectionStopping());
            assertTrue(ReflectionHelpers.getField(game, "connectionStopRequested"));
            assertFalse(ReflectionHelpers.getField(game, "connected"));
            pause(game);
        } finally {
            releaseStop.countDown();
            assertTrue(Game.awaitHubConnectionStop(2000));
        }
        verify(connection, times(1)).stop();
    }

    @Test public void ordinaryPauseDoesNotStartFinalCleanup() throws Exception {
        cleanup.countDown();
        NvConnection connection = mock(NvConnection.class);
        Game game = game(connection);
        pause(game);
        assertFalse(Game.isHubConnectionStopping());
        assertFalse(ReflectionHelpers.getField(game, "connectionStopRequested"));
        verify(connection, never()).stop();
    }

    @Test public void rotationFinishKeepsItsExistingReconnectPath() throws Exception {
        cleanup.countDown();
        NvConnection connection = mock(NvConnection.class);
        Game game = game(connection);
        ReflectionHelpers.setField(game, "hubRotationReconnect", true);
        game.finish(); pause(game);
        assertFalse(Game.isHubConnectionStopping());
        assertFalse(ReflectionHelpers.getField(game, "connectionStopRequested"));
        verify(connection, never()).stop();
    }
}
