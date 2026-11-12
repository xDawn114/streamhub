package com.limelight.streamhub;

import android.content.Context;
import android.util.AtomicFile;
import org.json.JSONArray;
import org.json.JSONObject;
import java.io.*;
import java.util.*;

/** Private task records contain document URIs and progress, never binding secrets. */
public final class HubFileTasks {
    public static final long MAX_BYTES = 2L * 1024 * 1024 * 1024;
    // Activity and Service have separate stores in the same process (no android:process).
    // AtomicFile readers also recover/delete journals, so they must share the writers' lock.
    private static final Map<File, Object> LOCKS = new HashMap<>();
    private final File root;
    private final Object lock;

    public HubFileTasks(Context context) {
        try { root = new File(context.getFilesDir(), "file-transfer").getCanonicalFile(); }
        catch (IOException error) { throw new IllegalStateException("无法定位文件暂存目录。", error); }
        synchronized (LOCKS) { lock = LOCKS.computeIfAbsent(root, ignored -> new Object()); }
        synchronized (lock) {
            if (!root.isDirectory() && !root.mkdirs() && !root.isDirectory())
                throw new IllegalStateException("无法创建文件暂存目录。");
        }
    }
    public static boolean isSizeAllowed(long size) { return size >= 0 && size <= MAX_BYTES; }
    private File record(String id) { if (!id.matches("[a-f0-9]{32}")) throw new IllegalArgumentException("文件任务标识无效。"); return new File(root, id + ".json"); }
    public File data(String id) { record(id); return new File(root, id + ".part"); }
    public static boolean safeName(String name) {
        if (name == null || name.isEmpty() || name.length() > 180 || name.endsWith(".") || name.endsWith(" ") || name.matches(".*[\\x00-\\x1f<>:\"/\\\\|?*].*")) return false;
        String stem = name.split("\\.", 2)[0].toUpperCase(Locale.ROOT);
        return !stem.matches("CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³]");
    }
    public JSONObject read(String id) throws Exception {
        synchronized (lock) { return readLocked(id); }
    }

    private TaskSnapshot readLocked(String id) throws Exception {
        try (InputStream input = new AtomicFile(record(id)).openRead(); ByteArrayOutputStream bytes = new ByteArrayOutputStream()) {
            byte[] buffer = new byte[2048]; int count;
            while ((count = input.read(buffer)) != -1) { if (bytes.size() + count > 16384) throw new IOException("文件任务记录过大。"); bytes.write(buffer, 0, count); }
            return new TaskSnapshot(root, bytes.toString("UTF-8"));
        }
    }

    /** Save a snapshot returned by create/read/list; conflicting edits require a fresh read. */
    public void save(JSONObject task) throws Exception {
        synchronized (lock) {
            // A detached JSON copy has no read baseline and cannot safely replace an existing task.
            if (!(task instanceof TaskSnapshot) || !root.equals(((TaskSnapshot) task).root))
                throw new IOException("请重新读取文件任务后再保存。");
            TaskSnapshot snapshot = (TaskSnapshot) task;
            JSONObject before = new JSONObject(snapshot.baseline);
            String id = before.getString("id");
            if (!id.equals(task.getString("id"))) throw new IOException("文件任务标识不能更改。");
            JSONObject proposed = new JSONObject(task.toString());
            // This read must succeed: an old callback must never recreate cleared history.
            JSONObject merged = readLocked(id);
            Set<String> keys = keys(before);
            keys.addAll(keys(proposed));
            for (String key : keys) {
                Object previous = before.opt(key), next = proposed.opt(key), current = merged.opt(key);
                if (same(previous, next)) continue;
                if (!same(previous, current) && !same(next, current))
                    throw new IOException("文件任务已被更新，请重新读取后重试。");
                if (proposed.has(key)) merged.put(key, next); else merged.remove(key);
            }
            writeLocked(merged);
            // Advance both the caller's view and its baseline, including other instances' edits.
            for (String key : keys(task)) task.remove(key);
            for (String key : keys(merged)) task.put(key, merged.get(key));
            snapshot.baseline = merged.toString();
        }
    }

    private void writeLocked(JSONObject task) throws Exception {
        byte[] bytes = task.toString().getBytes(java.nio.charset.StandardCharsets.UTF_8);
        if (bytes.length > 16384) throw new IOException("文件任务记录过大。");
        AtomicFile file = new AtomicFile(record(task.getString("id"))); FileOutputStream output = file.startWrite();
        try { output.write(bytes); file.finishWrite(output); }
        catch (Exception error) { file.failWrite(output); throw error; }
    }

    private static Set<String> keys(JSONObject object) {
        Set<String> keys = new HashSet<>();
        Iterator<String> iterator = object.keys();
        while (iterator.hasNext()) keys.add(iterator.next());
        return keys;
    }

    private static boolean same(Object left, Object right) throws Exception {
        if (left == right) return true;
        if (left == null || right == null) return false; // Missing and explicit JSON null differ.
        if (left instanceof JSONObject && right instanceof JSONObject) {
            JSONObject a = (JSONObject) left, b = (JSONObject) right;
            if (!keys(a).equals(keys(b))) return false;
            for (String key : keys(a)) if (!same(a.opt(key), b.opt(key))) return false;
            return true;
        }
        if (left instanceof JSONArray && right instanceof JSONArray) {
            JSONArray a = (JSONArray) left, b = (JSONArray) right;
            if (a.length() != b.length()) return false;
            for (int i = 0; i < a.length(); i++) if (!same(a.opt(i), b.opt(i))) return false;
            return true;
        }
        if (left instanceof Number && right instanceof Number)
            return JSONObject.numberToString((Number) left).equals(JSONObject.numberToString((Number) right));
        return left.equals(right);
    }

    // The baseline stays private and is never persisted or exposed as a task field.
    private static final class TaskSnapshot extends JSONObject {
        final File root;
        String baseline;
        TaskSnapshot(File root, String json) throws org.json.JSONException {
            super(json);
            this.root = root;
            baseline = json;
        }
    }

    public List<JSONObject> list() {
        synchronized (lock) { return listLocked(); }
    }
    public JSONObject nextQueued(String computer) {
        synchronized (lock) {
            return listLocked().stream().filter(task -> computer.equals(task.optString("computer")) &&
                    task.optBoolean("autoQueued") && !task.optBoolean("attempted") && "paused".equals(task.optString("state")))
                    .min(java.util.Comparator.comparingLong(task -> task.optLong("created"))).orElse(null);
        }
    }

    private List<JSONObject> listLocked() {
        List<JSONObject> tasks = new ArrayList<>(); File[] files = root.listFiles((directory, name) -> name.endsWith(".json"));
        if (files != null) for (File file : files) try { tasks.add(readLocked(file.getName().replace(".json", ""))); } catch (Exception ignored) { }
        tasks.sort((a,b) -> Long.compare(b.optLong("created"), a.optLong("created"))); return tasks;
    }
    public JSONObject create(String computer, String direction, String uri, String name, long size, String remoteId, String sha256) throws Exception {
        synchronized (lock) {
            List<JSONObject> existing = listLocked();
            if (!safeName(name) || !isSizeAllowed(size) || existing.size() >= 64) throw new IOException("请选择有效文件；单文件最多2GiB，队列最多64项。");
            long reserved = size;
            for (JSONObject task : existing) if (!"completed".equals(task.optString("state")) && !"cancelled".equals(task.optString("state"))) reserved += task.optLong("size");
            if (reserved > 4L * 1024 * 1024 * 1024 || root.getUsableSpace() < size + 64L * 1024 * 1024) throw new IOException("平板暂存空间不足或队列已满。");
            long created=Math.max(System.currentTimeMillis(),existing.stream().mapToLong(task->task.optLong("created")).max().orElse(0)+1);
            JSONObject task = new JSONObject().put("id", UUID.randomUUID().toString().replace("-", "")).put("computer", computer)
                .put("direction", direction).put("uri", uri).put("name", name).put("size", size).put("remoteId", remoteId)
                .put("sha256", sha256).put("offset", 0).put("created", created).put("state", "paused").put("message", "等待传输");
            writeLocked(task); return new TaskSnapshot(root, task.toString());
        }
    }

    public void clearFinished() {
        synchronized (lock) {
            for (JSONObject task : listLocked()) if ("completed".equals(task.optString("state")) || ("cancelled".equals(task.optString("state")) && !task.optBoolean("remoteCancelPending"))) {
                String id = task.optString("id"); if (data(id).exists()) data(id).delete(); new AtomicFile(record(id)).delete();
            }
        }
    }
}
