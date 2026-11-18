package com.limelight.streamhub;

import java.io.IOException;
import org.json.JSONObject;

/** Durable, lease-scoped remote cleanup; never derives ownership from BUSY. */
final class HubSessionEndQueue {
    interface Storage {
        String read();
        boolean write(String value);
    }
    interface Sender { void send(JSONObject body) throws Exception; }
    interface Cancellation { void check(); }
    static final class Owner {
        final String device, client, revision;
        Owner(String device, String client, String revision) {
            this.device = device; this.client = client; this.revision = revision;
        }
    }
    private final Storage storage;
    HubSessionEndQueue(Storage storage) { this.storage = storage; }

    void remember(Owner owner, String lease, boolean userExit) throws Exception {
        JSONObject existing = pending();
        if (existing != null && matches(existing, owner) && !lease.equals(existing.getString("leaseId")))
            throw new IOException("上次会话尚未结束，请重试连接。");
        JSONObject value = new JSONObject().put("deviceId", owner.device).put("clientId", owner.client)
                .put("bindingRevision", owner.revision).put("leaseId", lease).put("exitReason", userExit ? "user" : "internal");
        validate(value);
        if (!storage.write(value.toString())) throw new IOException("无法保存会话收尾状态，请重试。");
    }

    boolean retry(Owner owner, boolean supported, Sender sender, Cancellation cancellation) throws Exception {
        JSONObject value = pending();
        if (value == null) return false;
        cancellation.check();
        // A record from an old binding cannot authorize a request to this PC.
        if (!matches(value, owner)) {
            clear();
            return false;
        }
        if (!supported) throw new IOException("电脑端不支持会话收尾，请更新电脑端后重试。");
        sender.send(new JSONObject().put("mode", "remote").put("clientId", owner.client)
                .put("leaseId", value.getString("leaseId")).put("exitReason", value.getString("exitReason"))
                .put("endBackend", true));
        // A lost reply is retained. Only an exact completed/obsolete receipt
        // retires this record. Obsolete says nothing about current backend idle;
        // the coordinator reads current info again before preparing a stream.
        clear();
        cancellation.check();
        return true;
    }

    private JSONObject pending() throws Exception {
        String raw = storage.read();
        if (raw.isEmpty()) return null;
        if (raw.length() > 4096) throw new IOException("会话收尾记录无效，请在电脑端断开连接后重试。");
        try {
            JSONObject result = new JSONObject(raw);
            validate(result);
            return result;
        } catch (Exception error) {
            throw new IOException("会话收尾记录无效，请在电脑端断开连接后重试。", error);
        }
    }

    private static void validate(JSONObject value) throws Exception {
        if (value.length() != 5 || value.getString("deviceId").isEmpty() || value.getString("clientId").isEmpty()
                || !value.getString("leaseId").matches("[A-Za-z0-9_-]{8,128}")
                || !("user".equals(value.getString("exitReason")) || "internal".equals(value.getString("exitReason"))))
            throw new IOException("会话收尾标识无效。");
        value.getString("bindingRevision");
    }
    private static boolean matches(JSONObject value, Owner owner) throws Exception {
        return owner.device.equals(value.getString("deviceId")) && owner.client.equals(value.getString("clientId"))
                && owner.revision.equals(value.getString("bindingRevision"));
    }
    static void confirmReceipt(JSONObject request, JSONObject answer) throws Exception {
        JSONObject receipt = answer.optJSONObject("backendRelease");
        boolean completed = receipt != null && Boolean.TRUE.equals(receipt.opt("completed"));
        boolean obsolete = receipt != null && Boolean.TRUE.equals(receipt.opt("obsolete"));
        boolean resolved = receipt != null && (completed && (!receipt.has("obsolete") || Boolean.FALSE.equals(receipt.opt("obsolete")))
                || Boolean.FALSE.equals(receipt.opt("completed")) && obsolete);
        if (!resolved
                || !request.getString("leaseId").equals(receipt.optString("leaseId"))
                || !request.getString("clientId").equals(receipt.optString("clientId")))
            throw new IOException("电脑未确认本次会话已结束，请重试。");
    }
    private void clear() throws IOException {
        if (!storage.write("")) throw new IOException("无法清除已完成的会话收尾记录，请重试。");
    }
}
