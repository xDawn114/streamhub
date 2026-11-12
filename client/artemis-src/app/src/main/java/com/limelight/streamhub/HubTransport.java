package com.limelight.streamhub;

import java.util.List;

/** A connection provider supplies eligible destinations; binding/TLS still authorize the PC. */
public interface HubTransport {
    String id();
    String label();
    boolean usesAccount();
    List<String> candidates(List<String> known, HubTransports.Settings settings, String route);
}
