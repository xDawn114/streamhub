package com.limelight;

import java.io.IOException;
import java.util.logging.FileHandler;
import java.util.logging.Logger;

public class LimeLog {
    private static final Logger LOGGER = Logger.getLogger(LimeLog.class.getName());

    public static void info(String msg) {
        LOGGER.info(singleLine(msg));
    }
    
    public static void warning(String msg) {
        LOGGER.warning(singleLine(msg));
    }
    
    public static void severe(String msg) {
        LOGGER.severe(singleLine(msg));
    }

    private static String singleLine(String message) {
        return message == null ? "null" : message.replaceAll("[\\r\\n\\u0085\\u2028\\u2029]", " ");
    }
    
    public static void setFileHandler(String fileName) throws IOException {
        LOGGER.addHandler(new FileHandler(fileName));
    }
}
