package com.limelight.streamhub;

import android.content.Context;
import android.content.SharedPreferences;
import com.limelight.streamhub.networking.HubNetworkProviderSession;
import org.json.JSONObject;
import org.junit.Before;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.annotation.Config;
import java.io.IOException;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.concurrent.CancellationException;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=28, manifest=Config.NONE, application=android.app.Application.class)
public class HubTransportsTest {
    private HubStore store;
    private SharedPreferences preferences;
    private static HubTransports.Settings official() { return new HubTransports.Settings("tailscale", "", ""); }
    private static HubTransports.Settings self() { return new HubTransports.Settings("self-hosted", "https://vpn.example.com:8443", ""); }
    @Before public void prepare() {
        Context context = RuntimeEnvironment.getApplication();
        preferences = context.getSharedPreferences("streamhub-device", Context.MODE_PRIVATE);
        preferences.edit().clear().putString("hosts", "100.70.1.2,192.168.10.20").putString("invitation", "sealed-test")
            .putString("bindingRevision", "original").putString("cert-pc-remote", "certificate").commit();
        store = new HubStore(context);
    }
    @Test public void upgradeDefaultsToOfficialWithoutWritingPreferences() {
        int count = preferences.getAll().size(); assertEquals("tailscale", store.transport().provider); assertEquals(count, preferences.getAll().size());
    }
    @Test public void allFourProvidersHaveStableIds() {
        assertEquals(4, HubTransports.IDS.length);
        for (String id : HubTransports.IDS) assertEquals(id, HubTransports.get(id).id());
    }
    @Test public void malformedSettingsFailClosed() {
        store.put("transport", "unknown"); assertThrows(IllegalArgumentException.class, () -> store.candidates("auto"));
    }
    @Test public void serverRequiresHttpsRootWithoutCredentialsOrDecorations() {
        for (String url : Arrays.asList("http://vpn.example.com", "https://u:p@vpn.example.com", "https://@vpn.example.com", "https://vpn.example.com/a",
                "https://vpn.example.com/?key=x", "https://vpn.example.com/#fragment", "https://vpn.example.com\\@evil.test", "https://vpn.example.com\n", "https://vpn.example.com:0"))
            assertThrows(url, IllegalArgumentException.class, () -> HubTransports.serverUrl(url));
        assertEquals("https://vpn.example.com", HubTransports.serverUrl("https://VPN.EXAMPLE.COM:443/"));
    }
    @Test public void selfAuthMustMatchExactHttpsOrigin() {
        assertFalse(HubTransports.authorizationUrl(self(), "https://vpn.example.com:8443/register/node").isEmpty());
        for (String url : Arrays.asList("https://vpn.example.com/register/node", "https://login.vpn.example.com:8443/a", "https://vpn.example.com.evil.test:8443/a",
                "https://u@vpn.example.com:8443/a", "http://vpn.example.com:8443/a", "https://login.tailscale.com/a"))
            assertEquals(url, "", HubTransports.authorizationUrl(self(), url));
    }
    @Test public void officialAuthRejectsOtherProvidersAndUnusualPorts() {
        assertFalse(HubTransports.authorizationUrl(official(), "https://login.tailscale.com/a").isEmpty());
        for (String url : Arrays.asList("https://evil.tailscale.com.evil.test/a", "https://tailscale.com:444/a", "https://u@tailscale.com/a", "https://vpn.example.com/a"))
            assertEquals("", HubTransports.authorizationUrl(official(), url));
    }
    @Test public void lanAndManualNeverOfferAnAccount() {
        for (String id : Arrays.asList("lan", "manual")) {
            HubTransports.Settings settings = new HubTransports.Settings(id, "", "192.168.10.30");
            assertFalse(settings.transport().usesAccount()); assertEquals("", HubTransports.authorizationUrl(settings, "https://login.tailscale.com/a"));
        }
    }
    @Test public void lanExcludesSavedRemoteAddressForEveryConsumer() {
        store.saveTransport(new HubTransports.Settings("lan", "", ""));
        assertEquals(Arrays.asList("192.168.10.20"), store.candidates("auto")); assertTrue(store.candidates("remote").isEmpty());
    }
    @Test public void localSeedSurvivesTheCandidateLimitWithoutLastHost() {
        store.put("hosts", "100.70.1.1,100.70.1.2,100.70.1.3,100.70.1.4,100.70.1.5,100.70.1.6,100.70.1.7,100.70.1.8,192.168.10.20");
        List<String> candidates = store.candidates("auto");
        assertEquals(8, candidates.size()); assertEquals("192.168.10.20", candidates.get(0));
    }
    @Test public void manualUsesOnlyExplicitAddressesEvenAfterDiscovery() throws Exception {
        store.saveTransport(new HubTransports.Settings("manual", "", "192.168.10.30,100.71.1.2,192.168.10.30"));
        store.mergeAddresses(new org.json.JSONArray().put("192.168.10.99").put("100.75.1.2"), "192.168.10.99");
        assertEquals(Arrays.asList("192.168.10.30", "100.71.1.2"), store.candidates("auto"));
        assertEquals(Arrays.asList("100.71.1.2"), store.candidates("remote"));
    }
    @Test public void manualCannotSilentlyDiscardInvalidInput() {
        for (String value : Arrays.asList("", ",,,", "192.168.1.2,evil.test", "192.168.1.2:49199", "127.0.0.1", "192.168.01.2", "224.1.1.1"))
            assertThrows(IllegalArgumentException.class, () -> new HubTransports.Settings("manual", "", value));
    }
    @Test public void savingProviderKeepsBindingCertificatesAndChangesOnlyTransportRevision() {
        store.saveTransport(self()); String first = store.get("transportRevision", "");
        store.saveTransport(new HubTransports.Settings("lan", "", ""));
        assertNotEquals(first, store.get("transportRevision", "")); assertEquals("original", store.get("bindingRevision", ""));
        assertEquals("sealed-test", store.get("invitation", "")); assertEquals("certificate", store.get("cert-pc-remote", ""));
    }
    @Test public void providerSwitchInvalidatesOldFileClientBeforeDiscovery() {
        HubFileClient old = new HubFileClient(RuntimeEnvironment.getApplication());
        store.saveTransport(new HubTransports.Settings("lan", "", ""));
        assertThrows(CancellationException.class, old::check);
    }
    @Test public void localProbeFinishesBeforeRemoteIsAttempted() throws Exception {
        List<String> calls = java.util.Collections.synchronizedList(new ArrayList<>());
        HubProbeRace.Match<String> match = HubTransportProbe.first(Arrays.asList("100.70.1.2", "192.168.10.20"), host -> {
            calls.add(host); return "ok";
        }, () -> false, 1000);
        assertEquals("192.168.10.20", match.host); assertEquals(Arrays.asList("192.168.10.20"), calls);
    }
    @Test public void routeFailuresFallBackWithoutLosingTrustErrors() {
        IOException error = assertThrows(IOException.class, () -> HubTransportProbe.first(Arrays.asList("192.168.10.20", "100.70.1.2"), host -> {
            if (host.startsWith("192")) throw new javax.net.ssl.SSLException("bad trust");
            throw new java.net.ConnectException("offline");
        }, () -> false, 1000));
        assertEquals(2, error.getSuppressed().length);
        assertFalse(HubFileRetryPolicy.shouldRetry(new HubFileRetryPolicy.DiscoveryFailure(error), 0, true));
    }
    @Test public void cancellationDoesNotTryFallback() {
        assertThrows(CancellationException.class, () -> HubTransportProbe.first(Arrays.asList("192.168.10.20", "100.70.1.2"), host -> "unused", () -> true, 1000));
    }
    @Test public void usbThenLanThenRemoteOrderIsStrict() throws Exception {
        List<String> calls = java.util.Collections.synchronizedList(new ArrayList<>());
        HubProbeRace.Match<String> match = HubTransportProbe.first(Arrays.asList("100.70.1.2", "192.168.10.20", "192.168.42.20"), host -> {
            calls.add(host);
            if (!host.startsWith("100.")) throw new java.net.ConnectException("unreachable");
            return "ok";
        }, () -> false, 1500, host -> host.startsWith("100.") ? "remote" : host.startsWith("192.168.42.") ? "usb" : "lan");
        assertEquals("100.70.1.2", match.host); assertEquals(Arrays.asList("192.168.42.20", "192.168.10.20", "100.70.1.2"), calls);
    }
    @Test public void corruptPreferenceTypeDoesNotDefaultToOfficial() {
        preferences.edit().putInt("transport", 7).commit();
        assertThrows(IllegalArgumentException.class, () -> store.transport());
    }

    private static final class Fake implements HubNetworkProviderSession.Api {
        String control = ""; boolean loggedIn; final List<String> mutations = new ArrayList<>();
        public JSONObject call(String method, String endpoint, String body) throws Exception {
            if (method.equals("GET") && endpoint.equals("prefs")) return new JSONObject().put("ControlURL", control);
            if (method.equals("GET") && endpoint.equals("status")) return new JSONObject().put("TailscaleIPs", new org.json.JSONArray(loggedIn ? "[\"100.70.1.2\"]" : "[]"))
                .put("Self", new JSONObject().put("UserID", loggedIn ? "123" : "0"));
            mutations.add(method + " " + endpoint);
            if (method.equals("POST") && endpoint.equals("start")) { assertEquals("{}", body); return new JSONObject(); }
            JSONObject patch = new JSONObject(body);
            assertTrue(patch.getBoolean("WantRunningSet")); assertFalse(patch.getBoolean("WantRunning"));
            if (patch.optBoolean("ControlURLSet")) control = patch.getString("ControlURL");
            assertFalse(patch.has("AuthKey")); assertFalse(patch.has("LoggedOutSet")); return new JSONObject();
        }
    }
    @Test public void changingServerWhileLoggedInDoesNotMutateOrLogout() {
        Fake api = new Fake(); api.loggedIn = true;
        assertThrows(IllegalStateException.class, () -> new HubNetworkProviderSession(api).configure(self())); assertTrue(api.mutations.isEmpty());
    }
    @Test public void loggedOutApplyUsesOnlyMaskedControlUrlAndPause() throws Exception {
        Fake api = new Fake(); HubNetworkProviderSession session = new HubNetworkProviderSession(api); session.configure(self());
        assertEquals(self().controlUrl(), api.control); assertEquals(Arrays.asList("PATCH prefs", "POST start"), api.mutations); session.requireCurrent(self());
    }
    @Test public void officialServerAliasRetainsExistingIdentity() throws Exception {
        Fake api = new Fake(); api.control = "https://login.tailscale.com"; api.loggedIn = true;
        new HubNetworkProviderSession(api).configure(official());
        assertEquals(Arrays.asList("PATCH prefs", "POST start"), api.mutations); assertEquals("https://login.tailscale.com", api.control);
    }
    @Test public void mismatchedPersistedServerCannotExposeStatusOrConnect() {
        Fake api = new Fake(); api.control = "https://other.example.com";
        assertThrows(IllegalStateException.class, () -> new HubNetworkProviderSession(api).status(self())); assertTrue(api.mutations.isEmpty());
    }
    @Test public void pausingLocallyRetainsTheLoggedInServer() throws Exception {
        Fake api = new Fake(); api.loggedIn = true; api.control = self().controlUrl();
        HubNetworkProviderSession session = new HubNetworkProviderSession(api); session.configure(new HubTransports.Settings("lan", "", ""));
        assertEquals(self().controlUrl(), api.control); session.configure(self()); assertTrue(api.loggedIn);
    }
}
