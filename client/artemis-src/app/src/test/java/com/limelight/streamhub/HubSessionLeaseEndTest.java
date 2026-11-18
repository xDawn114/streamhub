package com.limelight.streamhub;

import android.content.Context;
import android.content.SharedPreferences;
import org.json.JSONObject;
import org.junit.Before;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.annotation.Config;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=28, manifest=Config.NONE, application=android.app.Application.class)
public class HubSessionLeaseEndTest {
    private Context context;
    private SharedPreferences pending;
    @Before public void prepare() {
        context = RuntimeEnvironment.getApplication();
        // Invalid invitation prevents transport construction before any socket.
        context.getSharedPreferences("streamhub-device", Context.MODE_PRIVATE).edit().clear()
                .putString("deviceId", "computer-test").putString("clientId", "client-test")
                .putString("bindingRevision", "binding-test").putString("invitation", "invalid-test-trust").commit();
        pending = context.getSharedPreferences("streamhub-session-cleanup", Context.MODE_PRIVATE);
        pending.edit().clear().commit();
    }
    private HubSessionLease start(String mode, boolean supported) throws Exception {
        HubSessionLease lease = HubSessionLease.start(context, "127.0.0.1", "exact-test-lease", mode, "", 100);
        lease.rememberCapabilities(new JSONObject().put("capabilities", new JSONObject().put("remoteBackendRelease", supported)));
        return lease;
    }
    @Test public void attemptedRemoteStreamKeepsIdentityEvenWhenTransportCannotBeConstructed() throws Exception {
        HubSessionLease lease = start("remote", true);
        lease.userExit();
        lease.endAfterStream();
        JSONObject record = new JSONObject(pending.getString("pendingRemoteEnd", ""));
        assertEquals("exact-test-lease", record.getString("leaseId"));
        assertEquals("user", record.getString("exitReason"));
        assertEquals("client-test", record.getString("clientId"));
        assertFalse(HubSessionLease.active());
        String saved = pending.getString("pendingRemoteEnd", "");
        lease.endAfterStream();
        assertEquals(saved, pending.getString("pendingRemoteEnd", ""));
    }
    @Test public void unusedRemotePreparationDoesNotAcquireBackendCloseAuthority() throws Exception {
        start("remote", true).end();
        assertEquals("", pending.getString("pendingRemoteEnd", ""));
        assertFalse(HubSessionLease.active());
    }
    @Test public void olderHostsDoNotReceiveNewCleanupFields() throws Exception {
        start("remote", false).endAfterStream();
        assertEquals("", pending.getString("pendingRemoteEnd", ""));
        assertFalse(HubSessionLease.active());
    }
    @Test public void tabletStillUsesItsExistingMediaAndDisplayCleanupPath() throws Exception {
        start("tablet", true).endAfterStream();
        assertEquals("", pending.getString("pendingRemoteEnd", ""));
        assertFalse(HubSessionLease.active());
    }
    @Test public void changedBindingCannotReceiveAnOldLeaseEnd() throws Exception {
        HubSessionLease lease = start("remote", true);
        context.getSharedPreferences("streamhub-device", Context.MODE_PRIVATE).edit()
                .putString("bindingRevision", "replacement-binding").commit();
        lease.endAfterStream();
        assertEquals("", pending.getString("pendingRemoteEnd", ""));
        assertFalse(HubSessionLease.active());
    }
}
