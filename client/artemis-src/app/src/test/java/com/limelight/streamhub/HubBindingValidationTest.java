package com.limelight.streamhub;

import android.app.Activity;
import android.content.Context;
import android.content.SharedPreferences;
import org.json.JSONObject;
import org.junit.Before;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.Robolectric;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.annotation.Config;
import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.CancellationException;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=28, manifest=Config.NONE, application=android.app.Application.class)
public class HubBindingValidationTest {
    private static final HubStore.InvitationCodec TEST_CODEC = new HubStore.InvitationCodec() {
        public String seal(String value) { return "sealed:" + value; }
        public String open(String value) {
            if (!value.startsWith("sealed:")) throw new IllegalArgumentException("Invalid sealed test trust");
            return value.substring(7);
        }
    };
    private Activity activity;
    private SharedPreferences preferences;
    private Map<String, ?> original;
    @Before public void prepare() {
        activity = Robolectric.buildActivity(Activity.class).setup().get();
        preferences = activity.getSharedPreferences("streamhub-device", Context.MODE_PRIVATE);
        preferences.edit().clear().putString("invitation", "old-sealed-trust").putString("deviceId", "old-computer")
            .putString("hosts", "192.168.9.20").putString("cert-old-computer-remote", "old-certificate").commit();
        original = new HashMap<>(preferences.getAll());
    }
    @Test public void malformedNewTrustPreservesAllOldBindingData() throws Exception {
        HubSessionCoordinator coordinator = new HubSessionCoordinator(activity, message -> { });
        try { coordinator.bind("192.168.9.21", "malformed-code"); fail("invalid code accepted"); }
        catch (Exception expected) { assertEquals(original, preferences.getAll()); }
    }
    @Test public void missingComputerIdentityNeverStartsTrustReplacement() throws Exception {
        try { new HubStore(activity).acceptBinding("unused", "192.168.9.21", new JSONObject()); fail("missing identity accepted"); }
        catch (IllegalStateException expected) { assertEquals(original, preferences.getAll()); }
    }
    @Test public void cancelledBindingNeverCommitsNewTrust() throws Exception {
        HubSessionCoordinator coordinator = new HubSessionCoordinator(activity, message -> { }); coordinator.cancel();
        try { coordinator.bind("192.168.9.21", "malformed-code"); fail("cancel ignored"); }
        catch (CancellationException expected) { assertEquals(original, preferences.getAll()); }
    }
    @Test public void cancelledSaveNeverOpensKeystoreOrWritesPreferences() throws Exception {
        try { new HubStore(activity).acceptBinding("unused", "127.0.0.1", new JSONObject().put("deviceId", "new-computer"), () -> true); fail("cancel ignored"); }
        catch (CancellationException expected) { assertEquals(original, preferences.getAll()); }
    }
    @Test public void unreachableNewComputerPreservesPreviousBinding() throws Exception {
        String code = new JSONObject().put("v", 1).put("port", 1)
            .put("token", new String(new char[64]).replace('\0', 'A'))
            .put("pin", new String(new char[64]).replace('\0', 'B')).toString();
        String trust = android.util.Base64.encodeToString(code.getBytes(java.nio.charset.StandardCharsets.UTF_8),
            android.util.Base64.URL_SAFE | android.util.Base64.NO_WRAP);
        try { new HubSessionCoordinator(activity, message -> { }).bind("127.0.0.1", trust); fail("unreachable computer accepted"); }
        catch (java.io.IOException expected) { assertEquals(original, preferences.getAll()); }
    }
    @Test public void authenticatedAddressRefreshPreservesBothMediaPinsAndClientIdentity() throws Exception {
        preferences.edit().putString("invitation", "sealed:existing-trust").putString("lastMode", "remote")
            .putString("cert-old-computer-tablet", "tablet-certificate").putString("clientId", "original-client")
            .putString("fileDeviceKey", "sealed:file-key").putString("bindingRevision", "old-revision").commit();
        HubStore store = new HubStore(activity, TEST_CODEC);
        store.acceptBinding("existing-trust", "192.168.9.1", new JSONObject().put("deviceId", "old-computer"));
        assertEquals("old-certificate", store.get("cert-old-computer-remote", ""));
        assertEquals("tablet-certificate", store.get("cert-old-computer-tablet", ""));
        assertEquals("remote", store.get("lastMode", ""));
        assertEquals("192.168.9.1", store.get("lastHost", ""));
        assertEquals("original-client", store.clientId());
        assertEquals("file-key", store.fileKey());
        assertNotEquals("old-revision", store.get("bindingRevision", ""));
    }
    @Test public void sameDeviceWithNewControlTrustDoesNotReuseOldMediaPins() throws Exception {
        preferences.edit().putString("invitation", "sealed:old-trust").putString("lastMode", "remote").commit();
        HubStore store = new HubStore(activity, TEST_CODEC);
        store.acceptBinding("replacement-trust", "192.168.9.1", new JSONObject().put("deviceId", "old-computer"));
        assertEquals("", store.get("cert-old-computer-remote", ""));
        assertEquals("", store.get("lastMode", ""));
        assertEquals("replacement-trust", store.invitation());
    }
    @Test public void differentComputerNeverInheritsOldMediaPins() throws Exception {
        preferences.edit().putString("invitation", "sealed:existing-trust").commit();
        HubStore store = new HubStore(activity, TEST_CODEC);
        store.acceptBinding("existing-trust", "192.168.9.1", new JSONObject().put("deviceId", "another-computer"));
        assertEquals("", store.get("cert-old-computer-remote", ""));
        assertEquals("another-computer", store.get("deviceId", ""));
    }
    @Test public void encryptionFailureDuringAddressRefreshPreservesOldState() throws Exception {
        HubStore store = new HubStore(activity, new HubStore.InvitationCodec() {
            public String seal(String value) throws Exception { throw new java.io.IOException("encryption unavailable"); }
            public String open(String value) { return value; }
        });
        try {
            store.acceptBinding("existing-trust", "192.168.9.1", new JSONObject().put("deviceId", "old-computer"));
            fail("encryption failure ignored");
        } catch (java.io.IOException expected) { assertEquals(original, preferences.getAll()); }
    }
}
