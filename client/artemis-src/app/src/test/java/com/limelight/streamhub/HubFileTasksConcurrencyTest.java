package com.limelight.streamhub;

import android.app.Application;
import android.content.Context;
import android.content.ContextWrapper;
import android.util.AtomicFile;
import android.util.Log;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.StandardCopyOption;
import java.util.List;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicReference;
import org.json.JSONObject;
import org.json.JSONArray;
import org.junit.After;
import org.junit.Before;
import org.junit.Rule;
import org.junit.Test;
import org.junit.rules.TemporaryFolder;
import org.junit.runner.RunWith;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.annotation.Config;
import org.robolectric.annotation.Implementation;
import org.robolectric.annotation.Implements;
import org.robolectric.annotation.RealObject;
import org.robolectric.shadow.api.Shadow;
import org.robolectric.shadows.ShadowLog;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk = {28, 33}, manifest = Config.NONE, application = Application.class,
        shadows = HubFileTasksConcurrencyTest.PausingAtomicFile.class)
public class HubFileTasksConcurrencyTest {
    @Rule public TemporaryFolder temporary = new TemporaryFolder();
    private final ExecutorService workers = Executors.newFixedThreadPool(2);
    private HubFileTasks activity;
    private HubFileTasks service;
    private Context context;

    @Before public void setUp() throws Exception {
        File files = temporary.newFolder("files");
        context = new ContextWrapper(RuntimeEnvironment.getApplication()) {
            @Override public File getFilesDir() { return files; }
        };
        activity = new HubFileTasks(context);
        service = new HubFileTasks(context);
    }

    @After public void tearDown() throws Exception {
        WriteGate gate = PausingAtomicFile.gate.getAndSet(null);
        if (gate != null) gate.release.countDown();
        workers.shutdownNow();
        assertTrue("workers must exit before temporary files are removed",
                workers.awaitTermination(10, TimeUnit.SECONDS));
    }

    private JSONObject create(long size) throws Exception {
        return activity.create("computer-one", "download", "content://selected", "test.bin",
                size, "remote-task", "");
    }

    // Pause with a real journal in flight; on newer Android, stop after close but before rename.
    private WriteGate pauseNextWrite() {
        WriteGate gate = new WriteGate();
        assertTrue(PausingAtomicFile.gate.compareAndSet(null, gate));
        return gate;
    }

    private <T> Future<T> overlap(WriteGate gate, Callable<T> action) throws Exception {
        assertTrue("writer did not open its journal", gate.opened.await(10, TimeUnit.SECONDS));
        CountDownLatch started = new CountDownLatch(1);
        AtomicReference<Thread> thread = new AtomicReference<>();
        Future<T> other = workers.submit(() -> {
            thread.set(Thread.currentThread());
            started.countDown();
            return action.call();
        });
        assertTrue(started.await(10, TimeUnit.SECONDS));
        // The original code finishes the other operation; a correct store blocks on its shared lock.
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(10);
        while (!other.isDone() && thread.get().getState() != Thread.State.BLOCKED
                && System.nanoTime() < deadline) Thread.sleep(1);
        assertTrue("other operation neither finished nor waited for the store lock",
                other.isDone() || thread.get().getState() == Thread.State.BLOCKED);
        gate.release.countDown();
        return other;
    }

    @Test public void singleWriterCanReplaceAnExistingRecord() throws Exception {
        JSONObject task = create(37);
        task.put("offset", 12);
        activity.save(task);
        assertEquals(12, service.read(task.getString("id")).getLong("offset"));
    }

    @Test @Config(sdk = 33) public void activityListCannotDiscardServiceNewJournal() throws Exception {
        JSONObject task = create(37);
        String id = task.getString("id");
        JSONObject progress = service.read(id).put("offset", 12);
        WriteGate gate = pauseNextWrite();
        Future<?> writer = workers.submit(() -> { service.save(progress); return null; });
        Future<List<JSONObject>> listing = overlap(gate, activity::list);
        writer.get(10, TimeUnit.SECONDS);
        for (ShadowLog.LogItem log : ShadowLog.getLogsForTag("AtomicFile"))
            System.out.println("AtomicFile: " + log.msg);
        assertEquals(12, service.read(id).getLong("offset"));
        assertEquals(12, listing.get(10, TimeUnit.SECONDS).get(0).getLong("offset"));
        assertFalse("no abandoned new journal", new File(service.data(id).getParent(), id + ".json.new").exists());
        assertTrue("AtomicFile must not silently log a failed commit", ShadowLog.getLogsForTag("AtomicFile").isEmpty());
    }

    @Test public void twoWritersKeepBothEdits() throws Exception {
        String id = create(37).getString("id");
        JSONObject progress = service.read(id).put("offset", 12);
        JSONObject publication = activity.read(id).put("savedUri", "content://published");
        WriteGate gate = pauseNextWrite();
        Future<?> writer = workers.submit(() -> { service.save(progress); return null; });
        Future<?> other = overlap(gate, () -> { activity.save(publication); return null; });
        writer.get(10, TimeUnit.SECONDS);
        other.get(10, TimeUnit.SECONDS);
        JSONObject loaded = new HubFileTasks(context).read(id);
        assertEquals(12, loaded.getLong("offset"));
        assertEquals("content://published", loaded.getString("savedUri"));
    }

    @Test @Config(sdk = 33) public void canonicalDirectoryAliasesShareTheReadLock() throws Exception {
        String id = create(37).getString("id");
        Context alias = new ContextWrapper(context) {
            @Override public File getFilesDir() { return new File(context.getFilesDir(), "."); }
        };
        HubFileTasks recreated = new HubFileTasks(alias);
        JSONObject progress = service.read(id).put("offset", 12);
        WriteGate gate = pauseNextWrite();
        Future<?> writer = workers.submit(() -> { service.save(progress); return null; });
        Future<JSONObject> read = overlap(gate, () -> recreated.read(id));
        writer.get(10, TimeUnit.SECONDS);
        assertEquals(12, read.get(10, TimeUnit.SECONDS).getLong("offset"));
    }

    @Test @Config(sdk = 33) public void atomicReadRecoversByDiscardingAnUncommittedNewJournal() throws Exception {
        String id = create(37).getString("id");
        File journal = new File(service.data(id).getParent(), id + ".json.new");
        try (FileOutputStream output = new FileOutputStream(journal)) { output.write('{'); }
        assertEquals(0, activity.read(id).getLong("offset"));
        assertFalse("the real platform read path must discard the pending journal", journal.exists());
    }

    @Test public void nestedChangesAndExplicitNullStaySeparateFromTheBaseline() throws Exception {
        JSONObject task = create(37).put("details", new JSONObject().put("chunks", new JSONArray().put(1)));
        activity.save(task);
        String id = task.getString("id");
        JSONObject stale = service.read(id);
        task.getJSONObject("details").getJSONArray("chunks").put(2);
        task.put("nullable", JSONObject.NULL);
        activity.save(task);
        stale.put("offset", 12);
        service.save(stale);
        assertEquals(2, stale.getJSONObject("details").getJSONArray("chunks").length());
        assertTrue(stale.has("nullable"));
        assertTrue(stale.isNull("nullable"));
        stale.remove("nullable");
        stale.put("offset", 0); // An intentional rewind is allowed after our own successful save.
        service.save(stale);
        assertFalse(activity.read(id).has("nullable"));
        assertEquals(0, activity.read(id).getLong("offset"));
    }

    @Test public void detachedJsonCannotBlindlyReplaceAnExistingRecord() throws Exception {
        JSONObject task = create(37);
        JSONObject detached = new JSONObject(task.toString()).put("offset", 12);
        try { service.save(detached); fail("detached JSON replaced a task without a read baseline"); }
        catch (IOException expected) { }
        assertEquals(0, activity.read(task.getString("id")).getLong("offset"));
    }

    @Test public void oversizedSaveLeavesRecordAndBaselineAvailableForRetry() throws Exception {
        JSONObject task = create(37);
        task.put("offset", 12).put("message", new String(new char[16384]).replace('\0', 'x'));
        try { activity.save(task); fail("unreadably large task committed"); }
        catch (IOException expected) { }
        assertEquals(0, service.read(task.getString("id")).getLong("offset"));
        task.put("message", "retry");
        activity.save(task);
        assertEquals(12, service.read(task.getString("id")).getLong("offset"));
    }

    @Test public void longLivedSnapshotsMergeOnlyTheirEdits() throws Exception {
        String id = create(37).getString("id");
        JSONObject progress = service.read(id);
        JSONObject publication = activity.read(id);
        progress.put("offset", 12).put("message", "receiving");
        service.save(progress);
        publication.put("publicationPhase", "writing").put("savedUri", "content://published");
        activity.save(publication);
        progress.put("offset", 24);
        service.save(progress);
        JSONObject loaded = activity.read(id);
        assertEquals(24, loaded.getLong("offset"));
        assertEquals("receiving", loaded.getString("message"));
        assertEquals("writing", loaded.getString("publicationPhase"));
        assertEquals("content://published", loaded.getString("savedUri"));
    }

    @Test public void conflictingStaleEditFailsWithoutPartialCommit() throws Exception {
        String id = create(37).getString("id");
        JSONObject older = activity.read(id);
        JSONObject newer = service.read(id).put("offset", 24);
        service.save(newer);
        older.put("offset", 12).put("savedUri", "content://must-not-commit");
        try { activity.save(older); fail("conflicting stale progress overwrote the current record"); }
        catch (IOException expected) { }
        JSONObject loaded = service.read(id);
        assertEquals(24, loaded.getLong("offset"));
        assertFalse(loaded.has("savedUri"));
    }

    @Test public void fieldRemovalDoesNotRestoreAnotherWritersOldValue() throws Exception {
        JSONObject initial = create(37).put("obsolete", "remove-me");
        activity.save(initial);
        String id = initial.getString("id");
        JSONObject older = activity.read(id);
        JSONObject newer = service.read(id);
        newer.remove("obsolete");
        service.save(newer);
        older.put("offset", 12);
        activity.save(older);
        assertFalse(service.read(id).has("obsolete"));
        assertEquals(12, service.read(id).getLong("offset"));
    }

    @Test public void cleanupCannotDeleteATaskBeingReactivated() throws Exception {
        JSONObject task = create(37).put("state", "completed");
        activity.save(task);
        String id = task.getString("id");
        JSONObject resumed = service.read(id).put("state", "running");
        WriteGate gate = pauseNextWrite();
        Future<?> writer = workers.submit(() -> { service.save(resumed); return null; });
        Future<?> cleanup = overlap(gate, () -> { activity.clearFinished(); return null; });
        writer.get(10, TimeUnit.SECONDS);
        cleanup.get(10, TimeUnit.SECONDS);
        assertEquals("running", activity.read(id).getString("state"));
    }

    @Test public void staleSnapshotCannotResurrectClearedHistory() throws Exception {
        JSONObject task = create(37).put("state", "completed");
        activity.save(task);
        String id = task.getString("id");
        JSONObject stale = service.read(id);
        activity.clearFinished();
        stale.put("message", "old callback");
        try { service.save(stale); fail("cleared task was resurrected"); }
        catch (IOException expected) { }
        assertTrue(activity.list().isEmpty());
    }

    @Test public void concurrentCreatesCannotExceedQueueLimit() throws Exception {
        for (int i = 0; i < 63; i++) create(0);
        assertOnlyOneCreateFits(0);
        assertEquals(64, activity.list().size());
    }

    @Test public void concurrentCreatesCannotOverReserveSpace() throws Exception {
        create(HubFileTasks.MAX_BYTES);
        assertOnlyOneCreateFits(HubFileTasks.MAX_BYTES);
        assertEquals(2, activity.list().size());
    }

    private void assertOnlyOneCreateFits(long size) throws Exception {
        WriteGate gate = pauseNextWrite();
        Future<?> writer = workers.submit(() -> { create(size); return null; });
        Future<Boolean> other = overlap(gate, () -> {
            try {
                service.create("computer-one", "download", "content://selected", "second.bin", size, "", "");
                return true;
            } catch (IOException full) { return false; }
        });
        writer.get(10, TimeUnit.SECONDS);
        assertFalse("capacity check and creation must form one transaction", other.get(10, TimeUnit.SECONDS));
    }

    static final class WriteGate {
        final CountDownLatch opened = new CountDownLatch(1);
        final CountDownLatch release = new CountDownLatch(1);
        boolean claimed;
    }

    @Implements(AtomicFile.class) public static class PausingAtomicFile {
        static final AtomicReference<WriteGate> gate = new AtomicReference<>();
        @RealObject private AtomicFile real;

        @Implementation protected FileOutputStream startWrite() throws IOException {
            FileOutputStream output = Shadow.directlyOn(real, AtomicFile.class, "startWrite");
            if (RuntimeEnvironment.getApiLevel() < 30) pause();
            return output;
        }

        @Implementation(minSdk = 30) protected static void rename(File source, File target) throws IOException {
            if (source.getName().endsWith(".new")) pause();
            // Android's POSIX rename replaces a destination; File.renameTo on Windows does not.
            // Keep the platform AtomicFile read/recovery/write code, adapting only that host syscall.
            try { Files.move(source.toPath(), target.toPath(), StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING); }
            catch (IOException failure) { Log.e("AtomicFile", "Failed to rename " + source + " to " + target, failure); }
        }

        private static void pause() throws IOException {
            WriteGate current = gate.get();
            boolean pause = false;
            if (current != null) synchronized (current) {
                if (!current.claimed) { current.claimed = true; pause = true; }
            }
            if (pause) {
                current.opened.countDown();
                try {
                    if (!current.release.await(15, TimeUnit.SECONDS)) throw new IOException("test journal gate timed out");
                } catch (InterruptedException interrupted) {
                    Thread.currentThread().interrupt();
                    throw new IOException(interrupted);
                }
            }
        }
    }
}
