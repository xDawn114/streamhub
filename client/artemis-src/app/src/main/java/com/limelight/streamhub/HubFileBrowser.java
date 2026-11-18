package com.limelight.streamhub;

import java.util.concurrent.CancellationException;
import java.util.function.Supplier;
import org.json.JSONArray;
import org.json.JSONObject;

/** The Files page uses one validated client; the transfer worker keeps its own lifecycle. */
final class HubFileBrowser {
    interface Read<T> {
        T run(HubFileClient client, JSONObject registration) throws Exception;
    }

    private final Supplier<HubFileClient> factory;
    private volatile HubFileClient client;
    private volatile boolean closed;
    private JSONObject registration;
    private JSONArray roots;

    HubFileBrowser(Supplier<HubFileClient> factory) { this.factory = factory; }

    // Reads run on the Activity's serial worker. Never pass a mutation to this retry path.
    <T> T read(boolean refreshRegistration, Read<T> read) throws Exception {
        if (closed) throw new CancellationException();
        if (client != null && !client.isCurrent()) reset();
        HubFileClient reused = client;
        try {
            return readOnce(refreshRegistration, read);
        } catch (Exception error) {
            boolean stillCurrent = reused != null && reused.isCurrent();
            reset();
            if (!stillCurrent || closed || error instanceof HubFileRetryPolicy.HttpFailure ||
                    !HubFileRetryPolicy.shouldRetry(error, 0, true)) throw error;
            try { return readOnce(false, read); }
            catch (Exception retryError) { reset(); throw retryError; }
        }
    }

    private <T> T readOnce(boolean refreshRegistration, Read<T> read) throws Exception {
        HubFileClient active = client;
        if (active == null) {
            client = active = factory.get();
            if (closed) throw new CancellationException();
            active.connect();
        }
        active.check();
        if (refreshRegistration || registration == null) {
            registration = active.register();
            roots = null;
        }
        JSONObject access = registration;
        if (!access.optBoolean("allowed") || !access.optBoolean("enabled")) registration = null;
        T result = read.run(active, access);
        active.check();
        return result;
    }

    JSONArray roots(HubFileClient active) throws Exception {
        if (roots == null) roots = active.json("roots", null).getJSONArray("roots");
        return roots;
    }

    private void reset() {
        HubFileClient active = client;
        client = null;
        registration = null;
        roots = null;
        if (active != null) active.cancel();
    }

    void close() {
        closed = true;
        HubFileClient active = client;
        if (active != null) active.cancel();
    }
}
