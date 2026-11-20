package com.limelight.streamhub;

import java.net.SocketTimeoutException;
import javax.net.ssl.SSLHandshakeException;
import org.json.JSONArray;
import org.json.JSONObject;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.annotation.Config;
import static org.junit.Assert.*;
import static org.mockito.Mockito.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk = 34, application = android.app.Application.class)
public class HubFileBrowserTest {
    private HubFileClient client(String root) throws Exception {
        HubFileClient client = mock(HubFileClient.class);
        when(client.isCurrent()).thenReturn(true);
        when(client.register()).thenReturn(new JSONObject().put("allowed", true).put("enabled", true));
        when(client.json("roots", null)).thenReturn(new JSONObject().put("roots", new JSONArray().put(root)));
        return client;
    }

    @Test public void trustedFilePageReopensAndReadsWithoutRepeatedRegistrationPerOperation() throws Exception {
        for (int reopen = 0; reopen < 2; reopen++) {
            HubFileClient client = client("saved-root");
            when(client.register()).thenReturn(new JSONObject().put("allowed", true).put("enabled", true)
                    .put("usesDevicePermissions", true));
            HubFileBrowser browser = new HubFileBrowser(() -> client);
            for (int read = 0; read < 3; read++) {
                assertEquals("saved-root", browser.read(false, (active, access) -> {
                    assertTrue(access.getBoolean("allowed"));
                    assertTrue(access.getBoolean("usesDevicePermissions"));
                    return browser.roots(active).getString(0);
                }));
            }
            verify(client).connect();
            verify(client).register();
            verify(client).json("roots", null);
            browser.close();
            verify(client).cancel();
        }
    }

    @Test public void taskPollReconnectsOnceAndNextBrowseReadsRootsFromTheNewConnection() throws Exception {
        HubFileClient first = client("first"), second = client("second");
        when(first.json("tasks", null)).thenThrow(new SocketTimeoutException("stale connection"));
        when(second.json("tasks", null)).thenReturn(new JSONObject());
        var clients = java.util.List.of(first, second).iterator();
        HubFileBrowser browser = new HubFileBrowser(clients::next);
        assertEquals("first", browser.read(true, (client, access) -> browser.roots(client).getString(0)));
        browser.read(false, (client, access) -> client.json("tasks", null));
        verify(second, never()).json("roots", null);
        assertEquals("second", browser.read(false, (client, access) -> browser.roots(client).getString(0)));
        verify(first).cancel();verify(second).connect();verify(second).register();verify(second).json("roots", null);
    }

    @Test public void transportFailureGetsAtMostOneReconnect() throws Exception {
        HubFileClient first = client("first"), second = client("second");
        when(first.json("tasks", null)).thenThrow(new SocketTimeoutException("stale connection"));
        SocketTimeoutException failed = new SocketTimeoutException("still unavailable");
        when(second.json("tasks", null)).thenThrow(failed);
        var clients = java.util.List.of(first, second).iterator();
        HubFileBrowser browser = new HubFileBrowser(clients::next);
        browser.read(true, (client, access) -> null);
        assertSame(failed, assertThrows(SocketTimeoutException.class,
                () -> browser.read(false, (client, access) -> client.json("tasks", null))));
        verify(second).connect();verify(second).cancel();
    }

    @Test public void permissionAndCertificateFailuresNeverReconnectAutomatically() throws Exception {
        for (Exception denied : new Exception[] { new HubFileRetryPolicy.HttpFailure(403, "denied"), new SSLHandshakeException("pin mismatch") }) {
            HubFileClient client = client("shared");
            when(client.json("tasks", null)).thenThrow(denied);
            var clients = java.util.List.of(client).iterator();
            HubFileBrowser browser = new HubFileBrowser(clients::next);
            browser.read(true, (active, access) -> null);
            assertSame(denied, assertThrows(denied.getClass(),
                    () -> browser.read(false, (active, access) -> active.json("tasks", null))));
            verify(client).cancel();
        }
    }
}
