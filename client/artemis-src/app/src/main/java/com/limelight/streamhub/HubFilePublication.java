package com.limelight.streamhub;

import org.json.JSONObject;
import java.io.File;
import java.io.FileInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.security.MessageDigest;
import java.util.List;

/** Journal the SAF copy and rename so a resumed task can recognize its own result. */
final class HubFilePublication {
    interface Journal { void save(JSONObject task) throws Exception; }
    interface Check { void check() throws Exception; }
    interface Documents {
        List<String> find(String name) throws Exception;
        String name(String uri) throws Exception;
        String create(String name) throws Exception;
        InputStream read(String uri) throws Exception;
        OutputStream write(String uri) throws Exception;
        String rename(String uri, String name) throws Exception;
    }
    private HubFilePublication() { }

    static void publish(JSONObject task, File spool, Journal journal, Documents documents, Check check) throws Exception {
        String phase = task.optString("publicationPhase", "");
        if (phase.isEmpty()) {
            String id = task.getString("id");
            String name = task.getString("name");
            int dot = name.lastIndexOf('.');
            String suffix = " (我控制 " + id.substring(0, 8) + ")";
            String target = dot > 0 ? name.substring(0, dot) + suffix + name.substring(dot) : name + suffix;
            String temporary = ".我控制-" + id + ".part";
            if (!documents.find(target).isEmpty() || !documents.find(temporary).isEmpty())
                throw new IOException("接收文件夹已有同名文件，请先检查后继续。");
            task.put("publicationName", target).put("publicationTemporaryName", temporary)
                    .put("publicationPhase", "creating");
            journal.save(task);
            phase = "creating";
        }
        String target = task.getString("publicationName");
        String temporary = task.getString("publicationTemporaryName");
        if ("creating".equals(phase)) {
            // A prior create may have completed before the URI could be journaled.
            if (!documents.find(temporary).isEmpty())
                throw new IOException("上次保存可能留下临时文件，请检查接收文件夹后继续。");
            check.check();
            String uri = documents.create(temporary);
            if (uri == null || uri.isEmpty()) throw new IOException("无法在所选文件夹创建临时文件。");
            task.put("publicationUri", uri).put("publicationPhase", "writing");
            journal.save(task);
            phase = "writing";
        }
        String uri = task.getString("publicationUri");
        if ("writing".equals(phase)) {
            if (!temporary.equals(documents.name(uri)))
                throw new IOException("临时文件状态已改变，请检查接收文件夹。");
            try (InputStream input = new FileInputStream(spool); OutputStream output = documents.write(uri)) {
                if (output == null) throw new IOException("接收文件夹不可写。");
                byte[] bytes = new byte[256 * 1024]; int count;
                while ((count = input.read(bytes)) != -1) { check.check(); output.write(bytes, 0, count); }
                check.check(); output.flush();
            }
            task.put("publicationPhase", "renaming");
            journal.save(task);
            phase = "renaming";
        }
        if (!"renaming".equals(phase)) throw new IOException("文件保存记录无效，请检查接收文件夹。");
        check.check();
        String currentName;
        try { currentName = documents.name(uri); } catch (Exception unavailable) { currentName = null; }
        String saved;
        if (temporary.equals(currentName)) {
            if (!documents.find(target).isEmpty())
                throw new IOException("接收文件夹出现同名文件，请检查后继续。");
            saved = documents.rename(uri, target);
            if (saved == null || saved.isEmpty()) throw new IOException("保存状态尚未确认，请检查接收文件夹后继续。");
        } else {
            List<String> matches = documents.find(target);
            if (matches.size() != 1) throw new IOException("保存状态尚未确认，请检查接收文件夹后继续。");
            saved = matches.get(0);
            verify(saved, task, documents, check);
        }
        task.put("saved", true).put("savedUri", saved).put("publicationPhase", "saved");
        journal.save(task);
    }

    private static void verify(String uri, JSONObject task, Documents documents, Check check) throws Exception {
        MessageDigest digest = MessageDigest.getInstance("SHA-256"); long size = 0;
        try (InputStream input = documents.read(uri)) {
            if (input == null) throw new IOException("无法核实已保存文件。");
            byte[] bytes = new byte[256 * 1024]; int count;
            while ((count = input.read(bytes)) != -1) {
                check.check(); size += count;
                if (size > task.getLong("size")) throw new IOException("已保存文件大小不符，请检查接收文件夹。");
                digest.update(bytes, 0, count);
            }
        }
        if (size != task.getLong("size") || !HubControlClient.hex(digest.digest()).equalsIgnoreCase(task.getString("sha256")))
            throw new IOException("已保存文件校验不符，请检查接收文件夹。");
    }
}
