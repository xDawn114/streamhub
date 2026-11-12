package com.limelight.streamhub;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CancellationException;
import org.json.JSONObject;
import org.junit.Rule;
import org.junit.Test;
import org.junit.rules.TemporaryFolder;
import org.junit.runner.RunWith;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.annotation.Config;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=28, manifest=Config.NONE, application=android.app.Application.class)
public class HubFilePublicationTest {
    @Rule public TemporaryFolder files = new TemporaryFolder();
    private static final byte[] CONTENT = "transfer data".getBytes(java.nio.charset.StandardCharsets.UTF_8);

    private static final class Item {
        String name; byte[] data = new byte[0];
        Item(String name) { this.name = name; }
    }
    private static final class FakeDocuments implements HubFilePublication.Documents {
        final Map<String, Item> items = new LinkedHashMap<>();
        int sequence, creates, writes, renames;
        public List<String> find(String name) {
            List<String> result = new ArrayList<>();
            for (Map.Entry<String, Item> item : items.entrySet()) if (name.equals(item.getValue().name)) result.add(item.getKey());
            return result;
        }
        public String name(String uri) { Item item = items.get(uri); return item == null ? null : item.name; }
        public String create(String name) { String uri = "doc:" + ++sequence; items.put(uri, new Item(name)); creates++; return uri; }
        public InputStream read(String uri) { return new ByteArrayInputStream(items.get(uri).data); }
        public OutputStream write(String uri) {
            writes++; Item item = items.get(uri);
            return new ByteArrayOutputStream() { @Override public void close() { item.data = toByteArray(); } };
        }
        public String rename(String uri, String name) {
            Item item = items.remove(uri); item.name = name; String result = "doc:" + ++sequence;
            items.put(result, item); renames++; return result;
        }
    }
    private static final class FakeJournal implements HubFilePublication.Journal {
        JSONObject persisted; boolean failSaved;
        public void save(JSONObject task) throws Exception {
            if (failSaved && task.optBoolean("saved")) { failSaved = false; throw new IOException("simulated process interruption"); }
            persisted = new JSONObject(task.toString());
        }
    }
    private JSONObject task() throws Exception {
        return new JSONObject().put("id", "0123456789abcdef0123456789abcdef").put("name", "file.txt")
                .put("size", CONTENT.length).put("sha256", HubControlClient.hex(MessageDigest.getInstance("SHA-256").digest(CONTENT)));
    }
    private File spool() throws Exception {
        File file = files.newFile();
        try (FileOutputStream output = new FileOutputStream(file)) { output.write(CONTENT); }
        return file;
    }
    @Test public void interruptedRenameIsRecoveredWithoutCreatingAnotherFile() throws Exception {
        FakeDocuments docs = new FakeDocuments(); FakeJournal journal = new FakeJournal(); journal.failSaved = true;
        File spool = spool();
        try { HubFilePublication.publish(task(), spool, journal, docs, () -> { }); fail("save should fail"); }
        catch (IOException expected) { assertEquals("renaming", journal.persisted.getString("publicationPhase")); }
        JSONObject resumed = journal.persisted;
        HubFilePublication.publish(resumed, spool, journal, docs, () -> { });
        assertTrue(resumed.getBoolean("saved"));
        assertEquals(1, docs.creates); assertEquals(1, docs.writes); assertEquals(1, docs.renames);
        assertArrayEquals(CONTENT, docs.items.get(resumed.getString("savedUri")).data);
    }
    @Test public void interruptedCopyReusesOnlyItsRecordedTemporaryDocument() throws Exception {
        FakeDocuments docs = new FakeDocuments(); FakeJournal journal = new FakeJournal(); File spool = spool();
        try { HubFilePublication.publish(task(), spool, journal, docs, () -> {
            if ("writing".equals(journal.persisted.optString("publicationPhase"))) throw new CancellationException();
        }); fail("copy should stop"); } catch (CancellationException expected) { }
        assertEquals("writing", journal.persisted.getString("publicationPhase"));
        JSONObject resumed = journal.persisted;
        HubFilePublication.publish(resumed, spool, journal, docs, () -> { });
        assertEquals(1, docs.creates); assertEquals(2, docs.writes);
        assertArrayEquals(CONTENT, docs.items.get(resumed.getString("savedUri")).data);
    }
    @Test public void unconfirmedOrModifiedResultIsNeverPublishedTwice() throws Exception {
        FakeDocuments docs = new FakeDocuments(); FakeJournal journal = new FakeJournal(); journal.failSaved = true; File spool = spool();
        try { HubFilePublication.publish(task(), spool, journal, docs, () -> { }); fail(); } catch (IOException expected) { }
        docs.items.values().iterator().next().data = new byte[]{1, 2};
        try { HubFilePublication.publish(journal.persisted, spool, journal, docs, () -> { }); fail("modified result accepted"); }
        catch (IOException expected) { assertTrue(expected.getMessage().contains("不符")); }
        assertEquals(1, docs.creates); assertEquals(1, docs.items.size());
    }
    @Test public void existingTemporaryOrFinalNameNeverGetsOverwritten() throws Exception {
        for (boolean temporary : Arrays.asList(false, true)) {
            FakeDocuments docs = new FakeDocuments(); FakeJournal journal = new FakeJournal(); JSONObject task = task();
            String id = task.getString("id");
            String existing = temporary ? ".我控制-" + id + ".part" : "file (我控制 " + id.substring(0, 8) + ").txt";
            String uri = docs.create(existing); docs.items.get(uri).data = new byte[]{42};
            try { HubFilePublication.publish(task, spool(), journal, docs, () -> { }); fail("collision accepted"); }
            catch (IOException expected) { assertTrue(expected.getMessage().contains("同名")); }
            assertArrayEquals(new byte[]{42}, docs.items.get(uri).data); assertEquals(0, docs.writes);
        }
    }
}
