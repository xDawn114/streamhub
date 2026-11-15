package com.limelight.utils;

import java.net.URI;

/** Help pages may navigate the web, never app files, content providers or intent handlers. */
public final class HelpUrlPolicy {
    private HelpUrlPolicy() { }
    public static boolean allowed(String value) {
        if (value == null || value.length() > 8192) return false;
        try {
            URI uri = new URI(value);
            return "https".equalsIgnoreCase(uri.getScheme()) && uri.getHost() != null &&
                    !uri.getHost().isEmpty() && uri.getRawUserInfo() == null &&
                    (uri.getPort() == -1 || uri.getPort() == 443);
        } catch (Exception invalid) { return false; }
    }
}
