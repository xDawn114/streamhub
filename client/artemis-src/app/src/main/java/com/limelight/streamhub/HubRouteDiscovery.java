package com.limelight.streamhub;

import java.io.IOException;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.concurrent.CancellationException;

/** Alternate links may teach a new USB address; they never become the selected stream link. */
public final class HubRouteDiscovery {
    public interface Candidates { List<String> list(String route); }
    public interface Probe<T> { T select(String host) throws Exception; }
    public interface Learn { void addresses(String host) throws Exception; }
    private HubRouteDiscovery() { }

    public static <T> T resolve(String route, Candidates candidates, Probe<T> probe, Learn learn) throws Exception {
        Set<String> attempted = new HashSet<>(); Exception failure = null;
        for (int pass = 0; pass < 2; pass++) {
            for (String host : candidates.list(route)) {
                if (!attempted.add(host)) continue;
                try { return probe.select(host); }
                catch (CancellationException cancelled) { throw cancelled; }
                catch (Exception error) { failure = error; }
            }
            if (pass != 0 || !"usb".equals(route)) break;
            Set<String> usbHosts = new HashSet<>(candidates.list("usb"));
            for (String host : candidates.list("auto")) {
                if (usbHosts.contains(host)) continue;
                try { learn.addresses(host); break; }
                catch (CancellationException cancelled) { throw cancelled; }
                catch (Exception error) { failure = error; }
            }
            // Re-evaluate the explicit USB list after authenticated discovery.
            // Each selected candidate, including this retry, must satisfy that filter.
        }
        if (failure != null) throw failure;
        throw new IOException("没有可用的连接地址。");
    }
}
