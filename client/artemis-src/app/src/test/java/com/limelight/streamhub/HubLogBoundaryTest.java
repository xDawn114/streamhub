package com.limelight.streamhub;

import com.limelight.LimeLog;
import java.util.ArrayList;
import java.util.List;
import java.util.logging.Handler;
import java.util.logging.LogRecord;
import java.util.logging.Logger;
import org.junit.Test;
import static org.junit.Assert.*;

public class HubLogBoundaryTest {
    @Test public void remoteTextCannotForgeAnotherLogLine() {
        List<String> entries = new ArrayList<>();
        Logger logger = Logger.getLogger(LimeLog.class.getName());
        Handler capture = new Handler() {
            public void publish(LogRecord record) { entries.add(record.getMessage()); }
            public void flush() { }
            public void close() { }
        };
        logger.addHandler(capture);
        try {
            LimeLog.info("name\r\nINFO: forged");
            LimeLog.warning("value\u0085\u2028\u2029forged");
            LimeLog.severe(null);
        } finally { logger.removeHandler(capture); }
        assertEquals(3, entries.size());
        assertEquals("name  INFO: forged", entries.get(0));
        assertEquals("value   forged", entries.get(1));
        assertEquals("null", entries.get(2));
    }
}
