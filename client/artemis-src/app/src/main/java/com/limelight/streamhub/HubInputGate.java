package com.limelight.streamhub;

import java.util.LinkedHashMap;
import java.util.Map;
import java.util.function.IntSupplier;

/** Serializes the view-only transition with every input packet, including releases. */
public final class HubInputGate {
    private boolean enabled = true;
    private final Map<String, Runnable> held = new LinkedHashMap<>();

    public synchronized boolean enabled() { return enabled; }
    public synchronized void run(Runnable packet) { if (enabled) packet.run(); }
    public synchronized int call(IntSupplier packet) { return enabled ? packet.getAsInt() : 0; }
    public synchronized void press(String id, Runnable packet, Runnable release) {
        if (!enabled) return;
        packet.run(); held.put(id, release);
    }
    public synchronized void release(String id, Runnable packet) {
        if (!enabled) return;
        packet.run(); held.remove(id);
    }
    public synchronized int contact(String id, boolean ending, IntSupplier packet, Runnable cancel) {
        if (!enabled) return 0;
        int result = packet.getAsInt();
        if (ending) held.remove(id); else if (result == 0) held.put(id, cancel);
        return result;
    }
    public synchronized void setEnabled(boolean value) {
        if (enabled == value) return;
        if (!value) {
            // Release keys, mouse buttons, controllers and contacts before suppressing input.
            enabled = false;
            RuntimeException failure=null;
            for (Runnable release : held.values()) {
                try {release.run();}catch(RuntimeException error){if(failure==null)failure=error;else failure.addSuppressed(error);}
            }
            held.clear();
            if(failure!=null)throw failure;
        }
        enabled = value;
    }
}
